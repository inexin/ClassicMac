using System;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;

namespace ClassicMac.Cli;

// A refused write: a usage error with its message.
internal class WriteRefused(string message) : Exception(message);

// A path that names nothing: exit NotFound (the CLI) or a not-found error (MCP), as the read commands do.
internal sealed class PathNotFound(string message) : WriteRefused(message);

/// <summary>
/// The write commands' changes (docs/cli.md §3), shared by the CLI and the MCP server: each resolves its Mac path in
/// a tree of the input, checks it, and returns the change to make on an <see cref="InputEditSession"/> of that input
/// (paths inside the volume, so the change can be made again on another session of the same input).
/// </summary>
internal static class MacEdits
{
    /// <summary>The entry a path inside the input names; refused (not found) when it names nothing.</summary>
    public static MacPathEntry Existing(MacPathTree tree, string rest) =>
        tree.Resolve(rest) ?? throw new PathNotFound("names nothing.");

    /// <summary>Adds a host file read by <see cref="HostImport"/>: into a folder (keeping its name), or as a new item's path.</summary>
    public static Action<InputEditSession> Put(MacPathTree tree, string rest, MacFile file, string? name, string? type, string? creator)
    {
        var entry = tree.Resolve(rest);
        var (folder, newName) = entry is { Kind: MacPathKind.Folder or MacPathKind.Container } && IsVolumeFolder(tree, entry)
            ? (VolumePath(tree, entry), name ?? file.Name.ToMacRoman())
            : NewItem(tree, rest);
        newName = name ?? newName;
        var info = file.FinderInfo with
        {
            Type = Code(type, "type") ?? file.FinderInfo.Type,
            Creator = Code(creator, "creator") ?? file.FinderInfo.Creator,
        };
        var path = Join(folder, newName);
        var added = file with { Name = MacString.FromMacRoman(newName), FinderInfo = info };
        return session => session.AddFile(path, added);
    }

    /// <summary>A host file to add: as a text document (<see cref="HostImport.ReadText"/>) or as it is (<see cref="HostImport.Read"/>).</summary>
    public static MacFile Import(string path, bool text, ContainerReadOptions options)
    {
        try
        {
            return text ? HostImport.ReadText(path) : HostImport.Read(path, options);
        }
        catch (System.IO.InvalidDataException e)
        {
            throw new WriteRefused(e.Message);
        }
    }

    /// <summary>Makes a folder.</summary>
    public static Action<InputEditSession> Mkdir(MacPathTree tree, string rest)
    {
        var (folder, name) = NewItem(tree, rest);
        var path = Join(folder, name);
        return session => session.AddFolder(path);
    }

    /// <summary>Deletes a file or folder (a folder with contents only when <paramref name="recursive"/>).</summary>
    public static Action<InputEditSession> Rm(MacPathTree tree, string rest, bool recursive)
    {
        var entry = Existing(tree, rest);
        var path = VolumePath(tree, entry);
        var warnings = tree.AliasesTo(entry).OrderBy(a => a.Alias, StringComparer.Ordinal)
            .Select(a => $"{a.Alias} will no longer find its original, {a.Original}").ToList();
        return session => session.Delete(path, recursive, warnings);
    }

    /// <summary>Renames a file or folder.</summary>
    public static Action<InputEditSession> Rename(InputEditKind kind, MacPathTree tree, string rest, string newName)
    {
        var path = ItemPath(kind, tree, Existing(tree, rest));
        return session => session.Rename(path, newName);
    }

    /// <summary>Moves a file or folder into a folder of the same volume (<paramref name="folder"/>, a path inside the input).</summary>
    public static Action<InputEditSession> Mv(MacPathTree tree, string rest, string folder)
    {
        var path = VolumePath(tree, Existing(tree, rest));
        var target = tree.Resolve(folder) ?? throw new PathNotFound($"{folder}: names nothing.");
        if (!IsVolumeFolder(tree, target))
        {
            throw new WriteRefused($"{target.Path} is not a folder of the volume.");
        }

        var into = VolumePath(tree, target);
        return session => session.Move(path, into);
    }

    /// <summary>Locks or unlocks a file of the volume.</summary>
    public static Action<InputEditSession> Lock(MacPathTree tree, string rest, bool locked)
    {
        var path = VolumePath(tree, Existing(tree, rest));
        return session => session.SetLocked(path, locked);
    }

    /// <summary>Blesses a folder of the volume as its System Folder.</summary>
    public static Action<InputEditSession> Bless(MacPathTree tree, string rest)
    {
        var entry = Existing(tree, rest);
        if (entry == tree.Root || !IsVolumeFolder(tree, entry))
        {
            throw new WriteRefused($"{entry.Path} is not a folder of the volume.");
        }

        var path = VolumePath(tree, entry);
        return session => session.Bless(path);
    }

    /// <summary>Sets a file's type and creator, or Finder flags (text as <see cref="Flags"/> reads it).</summary>
    public static Action<InputEditSession> Set(InputEditKind kind, MacPathTree tree, string rest, string? type, string? creator, string? flags)
    {
        var entry = Existing(tree, rest);
        var setFlags = flags is not null ? Flags(flags) : (FinderFlags?)null;
        var setType = Code(type, "type");
        var setCreator = Code(creator, "creator");
        if (setFlags is null && setType is null && setCreator is null)
        {
            throw new WriteRefused("Give a type, a creator or Finder flags.");
        }

        var path = ItemPath(kind, tree, entry);
        return session => session.SetInfo(path, setType, setCreator, setFlags);
    }

    /// <summary>Adds a resource, or replaces one when <paramref name="replace"/>.</summary>
    public static Action<InputEditSession> ResAdd(InputEditKind kind, MacPathTree tree, string rest, byte[] data, string? name, bool replace)
    {
        var (file, type, id) = ResourcePath(kind, tree, rest);
        var resourceName = name is not null ? MacString.FromMacRoman(name) : (MacString?)null;
        return session =>
        {
            if (!replace && session.Resources(file).Find(type, id) is not null)
            {
                throw new WriteRefused($"'{type}' {id} exists; give replace to replace it.");
            }

            session.SetResource(file, type, id, data, resourceName);
        };
    }

    /// <summary>Deletes a resource.</summary>
    public static Action<InputEditSession> ResRm(InputEditKind kind, MacPathTree tree, string rest)
    {
        Existing(tree, rest);
        var (file, type, id) = ResourcePath(kind, tree, rest);
        return session => session.DeleteResource(file, type, id);
    }

    // Whether an entry is the volume's root or a folder in it, not in a container inside it.
    private static bool IsVolumeFolder(MacPathTree tree, MacPathEntry entry) =>
        (entry == tree.Root || entry.Kind == MacPathKind.Folder) && InVolume(tree, entry);

    // Whether every entry between the root and this one is a folder (the path does not go into a nested container).
    private static bool InVolume(MacPathTree tree, MacPathEntry entry)
    {
        for (var at = entry.Parent; at is not null && at != tree.Root; at = at.Parent)
        {
            if (at.Kind != MacPathKind.Folder)
            {
                return false;
            }
        }

        return true;
    }

    // An item's path in the volume: its folders and its name, as Mac OS Roman text (empty for the root).
    private static string VolumePath(MacPathTree tree, MacPathEntry entry)
    {
        if (entry == tree.Root)
        {
            return "";
        }

        if (!InVolume(tree, entry) || entry.Kind is not (MacPathKind.Folder or MacPathKind.File or MacPathKind.Container))
        {
            throw new WriteRefused($"{entry.Path} is not a file or folder of the volume ClassicMac can change.");
        }

        var names = entry.Kind == MacPathKind.Folder
            ? entry.FolderPath!.Select(n => n.ToMacRoman())
            : entry.File!.FolderPath.Select(n => n.ToMacRoman()).Append(entry.File.Name.ToMacRoman());
        return string.Join(":", names);
    }

    // A file's or folder's path for a session: in a volume, its path; for a single-file input, "".
    private static string ItemPath(InputEditKind kind, MacPathTree tree, MacPathEntry entry)
    {
        if (kind == InputEditKind.SingleFile)
        {
            if (entry != tree.Root && entry.Parent != tree.Root)
            {
                throw new WriteRefused($"{entry.Path} is not the file the input holds.");
            }

            return "";
        }

        return VolumePath(tree, entry);
    }

    // A new item: its folder's path in the volume (which must exist) and its name.
    private static (string Folder, string Name) NewItem(MacPathTree tree, string rest)
    {
        var names = MacPaths.Split(rest);
        if (names.Count == 0)
        {
            throw new WriteRefused("Give the new item's name after the volume.");
        }

        var parent = tree.Resolve(string.Join(":", names.Take(names.Count - 1).Select(MacPaths.Escape)));
        if (parent is null)
        {
            throw new PathNotFound($"There is no folder to put {names[^1]} in.");
        }

        if (!IsVolumeFolder(tree, parent))
        {
            throw new WriteRefused($"There is no folder to put {names[^1]} in.");
        }

        return (VolumePath(tree, parent), names[^1]);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + ":" + name;

    // A resource path: the file before #rsrc, the type and the ID.
    private static (string File, FourCC Type, short Id) ResourcePath(InputEditKind kind, MacPathTree tree, string rest)
    {
        var names = MacPaths.Split(rest);
        var fork = names.Count >= 3 && names[^3] == MacPaths.ResourceFork ? names.Count - 3 : -1;
        if (fork < 0)
        {
            throw new WriteRefused("Give the resource as <file>:#rsrc:<type>:<ID>.");
        }

        var typeText = names[^2].Length >= 2 && names[^2][0] == '\'' && names[^2][^1] == '\'' ? names[^2][1..^1] : names[^2];
        if (!FourCC.TryParse(typeText, out var type))
        {
            throw new WriteRefused($"{names[^2]} is not a resource type (four Mac OS Roman characters).");
        }

        if (!short.TryParse(names[^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            throw new WriteRefused($"{names[^1]} is not a resource ID (-32768 to 32767).");
        }

        var file = tree.Resolve(string.Join(":", names.Take(fork).Select(MacPaths.Escape)));
        if (file is null)
        {
            throw new PathNotFound("The path before #rsrc names nothing.");
        }

        if (file.Kind is not (MacPathKind.File or MacPathKind.Container))
        {
            throw new WriteRefused("The path before #rsrc names no file.");
        }

        return (ItemPath(kind, tree, file), type, id);
    }

    private static FourCC? Code(string? text, string what)
    {
        if (text is null)
        {
            return null;
        }

        return InputEditSession.TryParseCode(text, out var code) ? code : throw new WriteRefused($"'{text}' is not a {what}: up to four Mac OS Roman characters.");
    }

    /// <summary>Finder flags: a number (0x…, $…, decimal), or names joined with commas (with or without their Is/Has).</summary>
    public static FinderFlags Flags(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('$'))
        {
            var hex = trimmed.StartsWith('$') ? trimmed[1..] : trimmed[2..];
            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return (FinderFlags)value;
            }
        }
        else if (ushort.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return (FinderFlags)number;
        }
        else
        {
            var flags = FinderFlags.None;
            foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var found = new[] { part, "Is" + part, "Has" + part }
                    .Select(name => Enum.TryParse<FinderFlags>(name, ignoreCase: true, out var flag) && !int.TryParse(name, out _) ? flag : (FinderFlags?)null)
                    .FirstOrDefault(f => f is not null);
                flags |= found ?? throw new WriteRefused($"'{part}' is not a Finder flag ({string.Join(", ", Enum.GetNames<FinderFlags>().Where(n => n is not "None" and not "ColorMask"))}).");
            }

            return flags;
        }

        throw new WriteRefused($"'{text}' is not a set of Finder flags.");
    }
}
