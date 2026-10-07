using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Resources;

namespace ClassicMac.Files.Commands;

/// <summary>Which bytes of a file <see cref="MacCommands.ReadBytes"/> reads.</summary>
public enum MacFork
{
    /// <summary>The data fork (for a resource, its data).</summary>
    Data,

    /// <summary>The resource fork, as stored.</summary>
    Resource,
}

/// <summary>How <see cref="MacCommands.Get"/> writes a file to the host.</summary>
public enum MacGetFormat
{
    /// <summary>The data fork as the file, the resource fork, Finder info and dates in an AppleDouble <c>._</c> file.</summary>
    AppleDouble,

    /// <summary>Basilisk II / SheepShaver's <c>.rsrc</c> and <c>.finf</c> folders beside the file.</summary>
    Basilisk,

    /// <summary>One MacBinary III file (<c>.bin</c>).</summary>
    MacBinary,

    /// <summary>The data fork as the file and the resource fork as <c>.rsrc</c>, without Finder info.</summary>
    Raw,

    /// <summary>One BinHex 4.0 file (<c>.hqx</c>): both forks and the Finder info, in 7-bit text.</summary>
    BinHex,

    /// <summary>
    /// The data fork alone as UTF-8 text with LF line ends, read as Mac OS Roman with CR line ends (<c>.txt</c> added to
    /// a name without an extension).
    /// </summary>
    Text,
}

/// <summary>One step of how an entry was read: a file and the format it was read as.</summary>
public sealed record MacReadStep(string Name, string Format);

/// <summary>
/// What ls, stat and find say about an entry (docs/cli.md §2). Facts that do not apply are null.
/// </summary>
public sealed record MacEntryInfo
{
    /// <summary>Its name.</summary>
    public required string Name { get; init; }

    /// <summary>Its full Mac path.</summary>
    public required string Path { get; init; }

    /// <summary>folder, file, container, resource-fork, resource-type or resource.</summary>
    public required string Kind { get; init; }

    /// <summary>The file's type, or a resource's.</summary>
    public string? Type { get; init; }

    /// <summary>The file's creator.</summary>
    public string? Creator { get; init; }

    /// <summary>The data fork's size, or a resource's (decompressed).</summary>
    public long? DataSize { get; init; }

    /// <summary>The resource fork's size.</summary>
    public long? ResourceSize { get; init; }

    /// <summary>When it was created, as the Mac recorded it (local time, no zone).</summary>
    public DateTime? Created { get; init; }

    /// <summary>When it was modified.</summary>
    public DateTime? Modified { get; init; }

    /// <summary>The Finder flags.</summary>
    public ushort? Flags { get; init; }

    /// <summary>The Finder flags set, by name (hasBundle, invisible…).</summary>
    public IReadOnlyList<string> FlagNames { get; init; } = [];

    /// <summary>Whether the file is locked.</summary>
    public bool? Locked { get; init; }

    /// <summary>For a container, what it holds ("HFS volume", "MacBinary II"…).</summary>
    public string? Format { get; init; }

    /// <summary>How many entries it holds (a folder's, a fork's types, a type's resources), when it is known cheaply.</summary>
    public int? Count { get; init; }

    /// <summary>A resource's type (four characters).</summary>
    public string? ResourceType { get; init; }

    /// <summary>A resource's ID.</summary>
    public short? ResourceId { get; init; }

    /// <summary>A resource's name.</summary>
    public string? ResourceName { get; init; }

    /// <summary>A resource's attributes (stat).</summary>
    public string? ResourceAttributes { get; init; }

    /// <summary>Where a resource fork was read from (stat): ResourceFork, AppleDouble, DataFork….</summary>
    public string? ResourceForkSource { get; init; }

    /// <summary>The formats the entry was read through, from the host file down (stat).</summary>
    public IReadOnlyList<MacReadStep> Chain { get; init; } = [];

    /// <summary>For an alias file, where it points and whether that resolves (stat).</summary>
    public MacAliasInfo? Alias { get; init; }

    /// <summary>For a symbolic link, its target path and where it leads (stat).</summary>
    public MacSymbolicLinkInfo? SymbolicLink { get; init; }

    /// <summary>For a volume (a container that is an HFS, HFS Plus or MFS volume), its name, space, counts and locks (stat).</summary>
    public VolumeInfo? Volume { get; init; }

    /// <summary>For a volume, its blessed System Folder's path inside it (stat); null when none is blessed or found.</summary>
    public string? BlessedFolder { get; init; }

    /// <summary>For an HFS volume, where its free space and files lie (stat); null for other entries.</summary>
    public VolumeLayout? Layout { get; init; }
}

/// <summary>Where an alias file points (docs/formats/resources/aliases.md §2) and what resolving it found.</summary>
/// <param name="StoredPath">The path the alias recorded, as Get Info shows it ("Mac OS 9: System Folder: Note Pad").</param>
/// <param name="Found">Whether the original was found.</param>
/// <param name="How">How it was found ("by its file ID", "by name in its folder", "by its path", "not found").</param>
/// <param name="ResolvedPath">Where the original is now, as Get Info shows a path; the stored path when not found.</param>
/// <param name="Target">The original's full path in the tree, when found.</param>
public sealed record MacAliasInfo(string StoredPath, bool Found, string How, string ResolvedPath, string? Target)
{
    /// <summary>found, missing, volumeNotOpen or network (aliases.md §5).</summary>
    public string State { get; init; } = Found ? "found" : "volumeNotOpen";

    /// <summary>The state in a sentence (<see cref="AliasResolution.Explanation"/>).</summary>
    public string Explanation { get; init; } = "";
}

/// <summary>A symbolic link's target (docs/formats/file-systems/hfs-plus.md §2.8) and where it leads.</summary>
/// <param name="Target">The POSIX path it holds.</param>
/// <param name="Found">Whether the path leads to an entry on the link's volume.</param>
/// <param name="ResolvedPath">That entry's full path in the tree, when found.</param>
public sealed record MacSymbolicLinkInfo(string Target, bool Found, string? ResolvedPath);

/// <summary>What <see cref="MacCommands.Find"/> looks for; every given criterion must hold.</summary>
public sealed record MacFindQuery
{
    /// <summary>A name pattern: '*' any characters, '?' one, ignoring case as HFS does.</summary>
    public string? Name { get; init; }

    /// <summary>The file type.</summary>
    public FourCC? Type { get; init; }

    /// <summary>The file creator.</summary>
    public FourCC? Creator { get; init; }

    /// <summary>Folders, files or containers only.</summary>
    public MacPathKind? Kind { get; init; }

    /// <summary>Files whose resource fork holds resources of this type.</summary>
    public FourCC? ResourceType { get; init; }

    /// <summary>Files either of whose forks holds these bytes.</summary>
    public byte[]? Contains { get; init; }

    /// <summary>How many levels of containers below the start are entered (0: none).</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>The largest fork searched for <see cref="Contains"/>.</summary>
    public long MaxSearchBytes { get; init; } = 64L << 20;
}

/// <summary>
/// The read side of the file commands on Mac paths (docs/cli.md §2): ls, stat, the bytes cat shows, find and get.
/// The CLI and the MCP server present them; decoding a resource (cat's JSON) and naming kinds are theirs.
/// </summary>
public static class MacCommands
{
    private static readonly (FinderFlags Flag, string Name)[] FlagNamesTable =
    [
        (FinderFlags.IsOnDesk, "onDesk"), (FinderFlags.IsShared, "shared"), (FinderFlags.HasNoInits, "noInits"),
        (FinderFlags.HasBeenInited, "inited"), (FinderFlags.HasCustomIcon, "customIcon"), (FinderFlags.IsStationery, "stationery"),
        (FinderFlags.NameLocked, "nameLocked"), (FinderFlags.HasBundle, "hasBundle"), (FinderFlags.IsInvisible, "invisible"),
        (FinderFlags.IsAlias, "alias"),
    ];

    /// <summary>ls: what an entry holds (a file: itself).</summary>
    public static IReadOnlyList<MacEntryInfo> List(MacPathTree tree, MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind is MacPathKind.File or MacPathKind.Resource
            ? [Info(tree, entry)]
            : [.. tree.Children(entry).Select(e => Info(tree, e))];
    }

    /// <summary>stat: an entry's facts, with how it was read and, for a resource, its attributes.</summary>
    public static MacEntryInfo Stat(MacPathTree tree, MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        var info = Info(tree, entry);
        var count = entry.Kind is MacPathKind.Folder or MacPathKind.Container ? tree.Children(entry).Count : info.Count;
        return info with
        {
            Count = count,
            Chain = Chain(tree, entry),
            ResourceForkSource = entry.Kind == MacPathKind.ResourceFork ? entry.ResourcesSource?.ToString() : null,
            ResourceAttributes = entry.Resource is { } resource ? resource.Attributes.ToString() : null,
            Alias = AliasOf(tree, entry),
            SymbolicLink = entry.File?.SymbolicLinkTarget is { } target && entry.Kind is MacPathKind.File or MacPathKind.Container
                ? tree.SymbolicLinkTargetOf(entry) is { } found
                    ? new MacSymbolicLinkInfo(target, true, found.Path)
                    : new MacSymbolicLinkInfo(target, false, null)
                : null,
            Volume = tree.VolumeInfoOf(entry),
            BlessedFolder = tree.VolumeInfoOf(entry)?.BlessedFolderId is { } blessed ? tree.FolderPathOf(entry, blessed) : null,
            Layout = tree.LayoutOf(entry),
        };
    }

    /// <summary>An alias file's target and how it resolves on the volume holding it; null for other entries.</summary>
    public static MacAliasInfo? AliasOf(MacPathTree tree, MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        return tree.ResolveAlias(entry) is { } resolution
            ? new MacAliasInfo(resolution.StoredPath, resolution.Found, resolution.How, resolution.ResolvedPath, tree.TargetOf(resolution)?.Path)
            {
                State = resolution.State switch
                {
                    AliasState.Found => "found",
                    AliasState.Missing => "missing",
                    AliasState.Network => "network",
                    _ => "volumeNotOpen",
                },
                Explanation = resolution.Explanation,
            }
            : null;
    }

    /// <summary>An entry's facts, as ls lists them.</summary>
    public static MacEntryInfo Info(MacPathTree tree, MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        var info = new MacEntryInfo { Name = entry.Name, Path = entry.Path, Kind = KindName(entry.Kind) };
        switch (entry.Kind)
        {
            case MacPathKind.File or MacPathKind.Container when entry.File is { } file:
                var flags = file.FinderInfo.Flags;
                return info with
                {
                    Type = file.FinderInfo.Type.ToString(),
                    Creator = file.FinderInfo.Creator.ToString(),
                    DataSize = file.DataFork.Length,
                    ResourceSize = file.ResourceFork.Length,
                    Created = file.Created?.ToDateTime(),
                    Modified = file.Modified?.ToDateTime(),
                    Flags = (ushort)flags,
                    FlagNames = [.. FlagNamesTable.Where(f => (flags & f.Flag) != 0).Select(f => f.Name)],
                    Locked = file.IsLocked,
                    Format = entry.Format,
                };
            case MacPathKind.Folder:
                return info with { Created = entry.Folder?.Created?.ToDateTime(), Modified = entry.Folder?.Modified?.ToDateTime() };
            case MacPathKind.ResourceFork:
                return info with { Count = entry.Resources!.Types.Count, DataSize = entry.File?.ResourceFork.Length };
            case MacPathKind.ResourceType:
                return info with { ResourceType = entry.ResourceType.ToString(), Count = entry.Resources!.OfType(entry.ResourceType!.Value).Count() };
            case MacPathKind.Resource when entry.Resource is { } resource:
                return info with
                {
                    Type = resource.Type.ToString(),
                    ResourceType = resource.Type.ToString(),
                    ResourceId = resource.Id,
                    ResourceName = resource.Name?.ToMacRoman(),
                    DataSize = ResourceDecompression.Default.GetData(resource, entry.Resources).Length,
                };
            default:
                return info;
        }
    }

    /// <summary>The formats an entry was read through: the host file's layout, then each container down to it.</summary>
    public static IReadOnlyList<MacReadStep> Chain(MacPathTree tree, MacPathEntry entry)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        var steps = new List<MacReadStep>();
        for (var at = entry.Parent; at is not null; at = at.Parent)
        {
            if (at.Kind == MacPathKind.Container && at.Format is { } format)
            {
                steps.Insert(0, new MacReadStep(at.Name, format));
            }
        }

        if (entry.Kind == MacPathKind.Container && entry.Format is { } own)
        {
            steps.Add(new MacReadStep(entry.Name, own));
        }

        steps.Insert(0, new MacReadStep(tree.Root.Name, tree.Root.FoundIn ?? HostFiles.FormatName(tree.HostLayout)));
        return steps;
    }

    /// <summary>
    /// cat's bytes: a file's data or resource fork, or a resource's data (decompressed). A folder, container
    /// contents, a fork or a type have none (<see cref="InvalidOperationException"/>).
    /// </summary>
    public static byte[] ReadBytes(MacPathTree tree, MacPathEntry entry, MacFork fork, long maxBytes = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Kind switch
        {
            MacPathKind.Resource => ResourceDecompression.Default.GetData(entry.Resource!, entry.Resources, null, tree.Diagnostics).ToArray(),
            MacPathKind.File or MacPathKind.Container when entry.File is { } file =>
                (fork == MacFork.Data ? file.DataFork : file.ResourceFork).ToArray(Math.Min(maxBytes, int.MaxValue)),
            MacPathKind.ResourceFork => entry.File!.ResourceFork.ToArray(Math.Min(maxBytes, int.MaxValue)),
            _ => throw new InvalidOperationException($"{entry.Path} is a {KindName(entry.Kind)}, which has no bytes of its own."),
        };
    }

    /// <summary>Mac OS Roman text as Unicode, its CRs (and CR LFs) as line feeds.</summary>
    public static string Text(ReadOnlySpan<byte> bytes) => Text(bytes, MacTextEncoding.Roman);

    /// <summary>Text in a Mac encoding as Unicode, its CRs (and CR LFs) as line feeds.</summary>
    public static string Text(ReadOnlySpan<byte> bytes, MacTextEncoding encoding) =>
        MacEncodings.Decode(bytes, encoding).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>A hex dump: offset, 16 bytes, and the bytes as Mac OS Roman (control characters as '.').</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();
        for (var at = 0; at < bytes.Length; at += 16)
        {
            var line = bytes.Slice(at, Math.Min(16, bytes.Length - at));
            text.Append(at.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                text.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
                if (i == 7)
                {
                    text.Append(' ');
                }
            }

            text.Append(' ');
            foreach (var b in line)
            {
                text.Append(b < 0x20 || b == 0x7F ? '.' : MacRoman.Decode([b])[0]);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// find: the folders, files and containers below an entry that match every criterion, depth first, entering
    /// containers up to <see cref="MacFindQuery.MaxDepth"/> levels. Lazy: take as many as needed.
    /// </summary>
    public static IEnumerable<MacEntryInfo> Find(MacPathTree tree, MacPathEntry start, MacFindQuery query)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(query);
        return Walk(tree, start, query, 0);
    }

    private static IEnumerable<MacEntryInfo> Walk(MacPathTree tree, MacPathEntry at, MacFindQuery query, int depth)
    {
        foreach (var child in tree.Children(at))
        {
            if (child.Kind is not (MacPathKind.Folder or MacPathKind.File or MacPathKind.Container))
            {
                continue;
            }

            if (IsMatch(tree, child, query))
            {
                yield return Info(tree, child);
            }

            if (child.Kind == MacPathKind.Folder || child.Kind == MacPathKind.Container && depth < query.MaxDepth)
            {
                foreach (var below in Walk(tree, child, query, child.Kind == MacPathKind.Container ? depth + 1 : depth))
                {
                    yield return below;
                }
            }
        }
    }

    private static bool IsMatch(MacPathTree tree, MacPathEntry entry, MacFindQuery query)
    {
        if (query.Name is { } name && !Matches(name, entry.Name)
            || query.Kind is { } kind && entry.Kind != kind)
        {
            return false;
        }

        var file = entry.File;
        if ((query.Type is not null || query.Creator is not null || query.ResourceType is not null || query.Contains is not null)
            && (file is null || entry.Kind == MacPathKind.Folder))
        {
            return false;
        }

        if (query.Type is { } type && file!.FinderInfo.Type != type || query.Creator is { } creator && file!.FinderInfo.Creator != creator)
        {
            return false;
        }

        if (query.ResourceType is { } resourceType
            && !(tree.Resolve(Relative(tree, entry) + ":" + MacPaths.ResourceFork) is { Resources: { } fork } && fork.Types.Contains(resourceType)))
        {
            return false;
        }

        return query.Contains is not { Length: > 0 } needle
            || Holds(file!.DataFork, needle, query.MaxSearchBytes) || Holds(file.ResourceFork, needle, query.MaxSearchBytes);
    }

    // An entry's path after the host file.
    private static string Relative(MacPathTree tree, MacPathEntry entry) => entry.Path[tree.Root.Path.Length..];

    private static bool Holds(ForkData fork, byte[] needle, long max) =>
        fork.Length > 0 && fork.Length <= max && fork.ToArray(max).AsSpan().IndexOf(needle) >= 0;

    /// <summary>Whether a name matches a pattern: '*' any characters, '?' one, the rest compared as HFS compares names.</summary>
    public static bool Matches(string pattern, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);
        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return MacPaths.NamesEqual(pattern, name);
        }

        var regex = "^" + string.Concat(pattern.Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(c.ToString()),
        })) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    /// <summary>
    /// get: writes an entry to a host folder and returns the paths written. A file or container as one file in the
    /// format chosen (a container entered as a folder with <paramref name="enter"/>); a folder as a folder of its files
    /// and folders; a resource as its data (decompressed), <c>TYPE_ID.bin</c>. Throws <see cref="IOException"/> when a
    /// file exists and <paramref name="overwrite"/> is off.
    /// </summary>
    public static IReadOnlyList<string> Get(MacPathTree tree, MacPathEntry entry, string directory, MacGetFormat format, bool overwrite, bool enter = false)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(directory);
        var written = new List<string>();
        Write(tree, entry, directory, format, overwrite, enter, written);
        return written;
    }

    private static void Write(MacPathTree tree, MacPathEntry entry, string directory, MacGetFormat format, bool overwrite, bool enter, List<string> written)
    {
        switch (entry.Kind)
        {
            case MacPathKind.Resource:
                var name = HostName($"{entry.ResourceType.ToString()!.Trim()}_{entry.Resource!.Id.ToString(CultureInfo.InvariantCulture)}.bin");
                written.Add(WriteFile(Path.Combine(directory, name), ReadBytes(tree, entry, MacFork.Data), overwrite));
                return;
            case MacPathKind.Folder:
            case MacPathKind.Container when enter:
                var folder = Path.Combine(directory, HostName(entry.Name));
                Directory.CreateDirectory(folder);
                foreach (var child in tree.Children(entry).Where(c => c.Kind is MacPathKind.Folder or MacPathKind.File or MacPathKind.Container))
                {
                    Write(tree, child, folder, format, overwrite, enter, written);
                }

                return;
            case MacPathKind.File or MacPathKind.Container when entry.File is { } file:
                var host = HostName(entry.Name);
                switch (format)
                {
                    case MacGetFormat.MacBinary:
                        written.Add(WriteFile(Path.Combine(directory, host + ".bin"), MacBinaryWriter.ToArray(file), overwrite));
                        break;
                    case MacGetFormat.BinHex:
                        written.Add(WriteFile(Path.Combine(directory, host + ".hqx"), Encoding.ASCII.GetBytes(BinHexWriter.ToText(file)), overwrite));
                        break;
                    case MacGetFormat.Text:
                        written.Add(WriteFile(Path.Combine(directory, Path.HasExtension(host) ? host : host + ".txt"),
                            Encoding.UTF8.GetBytes(Text(file.DataFork.ToArray())), overwrite));
                        break;
                    case MacGetFormat.Raw:
                        written.Add(WriteFile(Path.Combine(directory, host), file.DataFork.ToArray(), overwrite));
                        if (file.ResourceFork.Length > 0)
                        {
                            written.Add(WriteFile(Path.Combine(directory, host + ".rsrc"), file.ResourceFork.ToArray(), overwrite));
                        }

                        break;
                    default:
                        written.AddRange(HostFiles.Write(file, directory, HostWriteOptions.Default with
                        {
                            Layout = format == MacGetFormat.Basilisk ? HostLayout.BasiliskII : HostLayout.AppleDouble,
                            Overwrite = overwrite,
                        }, host));
                        break;
                }

                return;
            default:
                throw new InvalidOperationException($"{entry.Path} is a {KindName(entry.Kind)}; get takes a file, folder, container or resource.");
        }
    }

    private static string WriteFile(string path, byte[] bytes, bool overwrite)
    {
        if (!overwrite && File.Exists(path))
        {
            throw new IOException($"{path} exists (overwrite to replace it).");
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    // A Mac name as a host file name: characters the host does not allow replaced.
    private static string HostName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var host = new string([.. name.Select(c => invalid.Contains(c) || c < 0x20 ? '_' : c)]).TrimEnd('.', ' ');
        return host.Length == 0 ? "_" : host;
    }

    /// <summary>The kind's name in the commands' output.</summary>
    public static string KindName(MacPathKind kind) => kind switch
    {
        MacPathKind.Folder => "folder",
        MacPathKind.File => "file",
        MacPathKind.Container => "container",
        MacPathKind.ResourceFork => "resource-fork",
        MacPathKind.ResourceType => "resource-type",
        _ => "resource",
    };
}
