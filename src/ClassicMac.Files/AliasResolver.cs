using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files;

/// <summary>How an alias found its target (docs/formats/resources/aliases.md §2).</summary>
public enum AliasResolvedBy
{
    /// <summary>The file ID (a folder's directory ID) the alias recorded.</summary>
    TargetId,

    /// <summary>The full path the alias recorded.</summary>
    FullPath,

    /// <summary>The target's name in the folder whose ID the alias recorded.</summary>
    ParentAndName,
}

/// <summary>
/// An open volume's files and folders, for resolving aliases: its name (the root folder's), and its files and folders
/// by catalog ID and by path.
/// </summary>
public sealed class AliasVolume
{
    private readonly Dictionary<uint, MacFile> filesById = [];
    private readonly Dictionary<uint, MacFolder> foldersById = [];
    private readonly IReadOnlyList<MacFile> files;
    private readonly IReadOnlyList<MacFolder> folders;

    /// <summary>A volume from what its reader returned: the files and the folders, the root folder included.</summary>
    /// <exception cref="ArgumentException">There is no root folder.</exception>
    public AliasVolume(IReadOnlyList<MacFile> files, IReadOnlyList<MacFolder> folders, object? tag = null, MacDate? created = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(folders);
        this.files = files;
        this.folders = folders;
        Tag = tag;
        Created = created;
        Root = folders.FirstOrDefault(f => f.IsRoot) ?? throw new ArgumentException("The volume has no root folder.", nameof(folders));
        foreach (var file in files.Where(f => f.CatalogId is not null))
        {
            filesById.TryAdd(file.CatalogId!.Value, file);
        }

        foreach (var folder in folders.Where(f => f.CatalogId is not null))
        {
            foldersById.TryAdd(folder.CatalogId!.Value, folder);
        }
    }

    /// <summary>The volume's name.</summary>
    public MacString Name => Root.Name;

    /// <summary>The root folder.</summary>
    public MacFolder Root { get; }

    /// <summary>What the caller keeps with the volume (the app's tree node, the CLI's input).</summary>
    public object? Tag { get; }

    /// <summary>The volume's creation date, which aliases record to tell volumes of one name apart; null when not known.</summary>
    public MacDate? Created { get; }

    /// <summary>
    /// The volume a container node holds: an HFS or HFS Plus volume (plain or wrapped, read again for its folders);
    /// null for other containers or a volume that cannot be read.
    /// </summary>
    public static AliasVolume? Read(ContainerNode holder, object? tag = null)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (!holder.Children.Any(c => c.Format == HfsReader.Instance.FormatName))
        {
            return null;
        }

        try
        {
            var folders = HfsReader.Instance.ReadFolders(holder.File.DataFork, new ContainerContext());
            return folders.Any(f => f.IsRoot)
                ? new AliasVolume([.. holder.Children.Select(c => c.File)], folders, tag, HfsReader.Instance.ReadVolumeInfo(holder.File.DataFork)?.Created)
                : null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }
    }

    internal MacFile? FileById(uint id) => filesById.GetValueOrDefault(id);

    internal MacFolder? FolderById(uint id) => foldersById.GetValueOrDefault(id);

    internal MacFile? FileAt(IReadOnlyList<MacString> folderPath, MacString name) =>
        files.FirstOrDefault(f => SamePath(f.FolderPath, folderPath) && Same(f.Name, name));

    internal MacFolder? FolderAt(IReadOnlyList<MacString> folderPath, MacString name) =>
        folders.FirstOrDefault(f => !f.IsRoot && SamePath(f.FolderPath, folderPath) && Same(f.Name, name));

    /// <summary>Where a file or folder is on this volume, as Get Info shows a path: "Volume: Folder: …: Name".</summary>
    public string PathOf(MacFile file) => string.Join(": ", new[] { Name }.Concat(file.FolderPath).Append(file.Name).Select(n => n.ToMacRoman()));

    /// <inheritdoc cref="PathOf(MacFile)"/>
    public string PathOf(MacFolder folder) => string.Join(": ", new[] { Name }.Concat(folder.Path).Select(n => n.ToMacRoman()));

    internal static bool Same(MacString left, MacString right) => MacPaths.NamesEqual(left.ToMacRoman(), right.ToMacRoman());

    private static bool SamePath(IReadOnlyList<MacString> left, IReadOnlyList<MacString> right) =>
        left.Count == right.Count && left.Zip(right).All(p => Same(p.First, p.Second));
}

/// <summary>Whether an alias's original was found, and if not, why (aliases.md §5).</summary>
public enum AliasState
{
    /// <summary>Found on an open volume.</summary>
    Found,

    /// <summary>Its volume is open (by name and date, date or name), but the original is not on it.</summary>
    Missing,

    /// <summary>No open volume is its volume.</summary>
    VolumeNotOpen,

    /// <summary>Its volume is a network volume (AppleShare), not open.</summary>
    Network,
}

/// <summary>What resolving an alias found: the target on one of the volumes, or nothing, with the path the alias stored.</summary>
/// <param name="Alias">The alias record.</param>
/// <param name="Volume">The volume the target is on; null when not found.</param>
/// <param name="File">The target file; null for a folder or when not found.</param>
/// <param name="Folder">The target folder; null for a file or when not found.</param>
/// <param name="By">How it was found; null when not found.</param>
public sealed record AliasResolution(AliasRecord Alias, AliasVolume? Volume, MacFile? File, MacFolder? Folder, AliasResolvedBy? By)
{
    /// <summary>Whether the target was found.</summary>
    public bool Found => By is not null;

    /// <summary>Found; else missing from its open volume, on a volume that is not open, or on a network volume.</summary>
    public AliasState State { get; init; } = By is not null ? AliasState.Found : AliasState.VolumeNotOpen;

    /// <summary>
    /// The state in a sentence: "Found by its file ID."; "The original is not on Disk any more."; "The original is on the
    /// hard disk “Bag of Holding”, which is not open."; "The original is on the network volume “Shared” on the server
    /// “Studio” (zone “Office”, as “lars”)."
    /// </summary>
    public string Explanation => State switch
    {
        AliasState.Found => $"Found {How}.",
        AliasState.Missing => $"The original is not on {Alias.VolumeName.ToMacRoman()} any more.",
        AliasState.Network => NetworkSentence(Alias.Network!),
        _ => $"The original is on {(Alias.VolumeKindName is { } kind ? "the " + kind : "the disk")} “{Alias.VolumeName.ToMacRoman()}”, which is not open.",
    };

    private static string NetworkSentence(AliasNetwork network)
    {
        var text = new System.Text.StringBuilder("The original is on the network volume");
        if (network.Volume is { } volume)
        {
            text.Append($" “{volume}”");
        }

        if (network.Server is { } server)
        {
            text.Append($" on the server “{server}”");
        }

        var details = new List<string>();
        if (network.Zone is { } zone)
        {
            details.Add($"zone “{zone}”");
        }

        if (network.User is { } user)
        {
            details.Add($"as “{user}”");
        }

        return text.Append(details.Count > 0 ? $" ({string.Join(", ", details)})." : ".").ToString();
    }

    /// <summary>The path the alias recorded (<see cref="AliasRecord.TargetPath"/>).</summary>
    public string StoredPath => Alias.TargetPath;

    /// <summary>Where the target is now, "Volume:Folder:…:Name"; the stored path when not found.</summary>
    public string ResolvedPath => (Volume, File, Folder) switch
    {
        ({ } volume, { } file, _) => volume.PathOf(file),
        ({ } volume, _, { } folder) => volume.PathOf(folder),
        _ => StoredPath,
    };

    /// <summary>How it was found, in words ("by its file ID", "by its path", "by name in its folder", "not found").</summary>
    public string How => By switch
    {
        AliasResolvedBy.TargetId => Alias.Kind == AliasKind.Folder ? "by its folder ID" : "by its file ID",
        AliasResolvedBy.FullPath => "by its path",
        AliasResolvedBy.ParentAndName => "by name in its folder",
        _ => "not found",
    };
}

/// <summary>
/// Alias files and their targets (docs/formats/resources/aliases.md §2): a file's alias record, and the target found on
/// the open volumes with the alias's volume name.
/// </summary>
public static class AliasResolver
{
    private static readonly FourCC Alis = FourCC.FromString("alis");

    /// <summary>Whether the Finder takes <paramref name="file"/> for an alias (its isAlias flag).</summary>
    public static bool IsAlias(MacFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return (file.FinderInfo.Flags & FinderFlags.IsAlias) != 0;
    }

    /// <summary>
    /// An alias file's record: its <c>'alis'</c> 0, else its first <c>'alis'</c>; null when it has none or the record
    /// cannot be read.
    /// </summary>
    public static AliasRecord? ReadAlias(MacFile file, ReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.ResourceFork.Length == 0)
        {
            return null;
        }

        try
        {
            var fork = MacFileResources.Read(file, options).Fork;
            var resource = fork?.Find(Alis, 0) ?? fork?.Resources.FirstOrDefault(r => r.Type == Alis);
            return resource is null ? null : AliasRecord.Read(resource.GetData(), out _);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the target as MatchAlias's fast search does (aliases.md §2.1) [Fitted]: the volumes in the order the alias
    /// matches them (name and creation date, then date only, then name only; the alias's own volume first among equals),
    /// on each the recorded number (file number or directory ID), then the name in the recorded parent folder, then the
    /// full path. On a volume whose date differs, a file found by name must have the alias's creation date, type and
    /// creator, a folder its creation date. <paramref name="aliasFile"/>, when given, is never its own target.
    /// </summary>
    public static AliasResolution Resolve(AliasRecord alias, IEnumerable<AliasVolume> volumes, MacFile? aliasFile = null)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(volumes);
        var all = volumes.ToList();
        bool Named(AliasVolume v) => AliasVolume.Same(v.Name, alias.VolumeName);
        bool Dated(AliasVolume v) => alias.VolumeCreated is { } date && v.Created == date;
        var candidates = all.Where(v => Named(v) && Dated(v)).Select(v => (Volume: v, Dated: true))
            .Concat(all.Where(v => !Named(v) && Dated(v)).Select(v => (Volume: v, Dated: true)))
            .Concat(all.Where(v => Named(v) && !Dated(v)).Select(v => (Volume: v, Dated: false)));
        var searched = false;
        foreach (var (volume, dated) in candidates)
        {
            searched = true;
            foreach (var found in new[] { ById(alias, volume), ByParent(alias, volume), ByPath(alias, volume) })
            {
                if (found is not null && (aliasFile is null || !ReferenceEquals(found.File, aliasFile)) && (dated || found.By == AliasResolvedBy.TargetId || Matches(alias, found)))
                {
                    return found;
                }
            }
        }

        var state = searched ? AliasState.Missing : alias.Network is not null ? AliasState.Network : AliasState.VolumeNotOpen;
        return new AliasResolution(alias, null, null, null, null) { State = state };
    }

    /// <summary>
    /// Follows an alias file to a file that is not an alias, through at most <paramref name="maxHops"/> aliases (as
    /// ResolveAliasFile does, aliases.md §2.1); null when a step has no record or is not found.
    /// </summary>
    public static AliasResolution? Follow(MacFile aliasFile, IEnumerable<AliasVolume> volumes, int maxHops = 10, ReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(aliasFile);
        var list = volumes.ToList();
        AliasResolution? result = null;
        var file = aliasFile;
        for (var hop = 0; hop < maxHops && IsAlias(file); hop++)
        {
            if (ReadAlias(file, options) is not { } alias)
            {
                return null;
            }

            result = Resolve(alias, list, file);
            if (result.File is not { } next)
            {
                return result.Found ? result : null;
            }

            file = next;
        }

        return result is not null && !IsAlias(file) ? result : null;
    }

    // The match test on a volume whose date differs from the alias's [Fitted].
    private static bool Matches(AliasRecord alias, AliasResolution found) => found switch
    {
        { File: { } file } => file.Created == alias.TargetCreated && file.FinderInfo.Type == alias.Type && file.FinderInfo.Creator == alias.Creator,
        { Folder: { } folder } => folder.Created == alias.TargetCreated,
        _ => false,
    };

    private static AliasResolution? ById(AliasRecord alias, AliasVolume volume)
    {
        if (alias.Kind == AliasKind.Folder)
        {
            return volume.FolderById(alias.TargetId) is { } folder ? new(alias, volume, null, folder, AliasResolvedBy.TargetId) : null;
        }

        return volume.FileById(alias.TargetId) is { } file ? new(alias, volume, file, null, AliasResolvedBy.TargetId) : null;
    }

    // The full path's names after the volume's: the folders, then the target's name.
    private static AliasResolution? ByPath(AliasRecord alias, AliasVolume volume)
    {
        if (alias.FullPath is not { } path)
        {
            return null;
        }

        var names = path.ToMacRoman().Split(':', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(MacString.FromMacRoman).ToList();
        if (names.Count == 0)
        {
            return alias.Kind == AliasKind.Folder ? new(alias, volume, null, volume.Root, AliasResolvedBy.FullPath) : null;
        }

        return Find(alias, volume, names[..^1], names[^1], AliasResolvedBy.FullPath);
    }

    private static AliasResolution? ByParent(AliasRecord alias, AliasVolume volume)
    {
        if (alias.ParentId == 1 && alias.Kind == AliasKind.Folder)
        {
            return new(alias, volume, null, volume.Root, AliasResolvedBy.ParentAndName);
        }

        return volume.FolderById(alias.ParentId) is { } parent ? Find(alias, volume, parent.Path, alias.Name, AliasResolvedBy.ParentAndName) : null;
    }

    private static AliasResolution? Find(AliasRecord alias, AliasVolume volume, IReadOnlyList<MacString> folderPath, MacString name, AliasResolvedBy by)
    {
        if (alias.Kind == AliasKind.Folder)
        {
            return volume.FolderAt(folderPath, name) is { } folder ? new(alias, volume, null, folder, by) : null;
        }

        return volume.FileAt(folderPath, name) is { } file ? new(alias, volume, file, null, by) : null;
    }
}
