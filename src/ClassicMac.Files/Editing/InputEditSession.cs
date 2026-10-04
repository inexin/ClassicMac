using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;
using ClassicMac.Resources.Editing;

namespace ClassicMac.Files.Editing;

/// <summary>What an opened input lets an <see cref="InputEditSession"/> change.</summary>
public enum InputEditKind
{
    /// <summary>Nothing: a file inside an archive or a disk image that is not a plain HFS volume, a PC Exchange folder …</summary>
    ReadOnly,

    /// <summary>A plain HFS volume image: its files and folders, their Finder info, and its files' resources.</summary>
    HfsVolume,

    /// <summary>One Mac file (a resource fork, MacBinary, BinHex, AppleSingle, AppleDouble or Basilisk II file): its resources and Finder info.</summary>
    SingleFile,

    /// <summary>An HFS Plus volume image (bare or in an HFS wrapper, plain, in a partition or a Disk Copy image): repaired by First Aid only.</summary>
    HfsPlusVolume,
}

/// <summary>One change an <see cref="InputEditSession"/> will make when saved.</summary>
/// <param name="Action"><c>add</c>, <c>mkdir</c>, <c>delete</c>, <c>rename</c>, <c>set</c>, <c>res-set</c> or <c>res-delete</c>.</param>
/// <param name="Path">The item's path in the input (folders and name joined with ':'; empty for a single-file input).</param>
/// <param name="Detail">What changes, in words.</param>
public sealed record PlannedChange(string Action, string Path, string Detail)
{
    /// <summary>What the change will break that the change itself does not show (aliases that lose their original), in sentences.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// An edit session on an opened input: files and folders added, deleted and renamed in a plain HFS volume, Finder
/// info set, resources set and deleted, all in memory; then saved to a new file (verified, the input untouched), or in
/// place only when asked (the original kept as <c>.orig</c>). The app's Volume menu and Save commands and the CLI's
/// write commands share it. Paths are inside the volume, without its name (<c>Docs:Letter</c>); on a partitioned disk
/// with several Mac volume partitions they start with the partition's name (<c>Two:Docs:Letter</c>).
/// </summary>
public sealed class InputEditSession
{
    private readonly List<PlannedChange> changes = [];
    private readonly Dictionary<string, EditSession> forks = new(StringComparer.Ordinal);
    private readonly ContainerReadOptions options;
    private readonly SaveLocation? location;
    private readonly SaveAsFormat singleFormat;
    private readonly bool forkInDataFork;
    private readonly List<EditedVolume> volumes = [];
    private bool resized;
    private readonly MacFile? ndif;
    private readonly HostFile host;
    private MacFile? single;

    private InputEditSession(string path, HostFile host, ContainerNode root, ContainerReadOptions options, ReadOptions readOptions,
        ICollection<Diagnostic> diagnostics)
    {
        Path = System.IO.Path.GetFullPath(path);
        this.options = options;
        this.host = host;
        // A plain HFS volume image, known by its MDB (an empty volume has no files to show it), or an HFS Plus one; also
        // one the reader refused, which First Aid may still repair.
        if (DataFileIsDisk(host) && root.Volume?.Format is "HFS" or "HFS Plus" or null && VolumeKind(host.File.DataFork) is { } plain)
        {
            volumes.Add(new EditedVolume(plain, null, null, ""));
            Kind = plain;
            return;
        }

        // A partitioned disk: each HFS or HFS Plus partition is edited and put back in place (partition-map.md §5). On a
        // disk with several Mac volume partitions, each is a folder named after it, which paths start with.
        if (DataFileIsDisk(host) && PartitionMapReader.Partitions(host.File.DataFork) is { Count: > 0 } partitions)
        {
            foreach (var partition in partitions)
            {
                if (VolumeKind(host.File.DataFork.Slice(partition.Offset, partition.Length)) is { } kind)
                {
                    volumes.Add(new EditedVolume(kind, partition, new HfsImageRegion(partition.Offset, partition.Length),
                        partitions.Count == 1 ? "" : partition.Name));
                }
            }

            Kind = volumes.Any(v => v.Kind == InputEditKind.HfsVolume) ? InputEditKind.HfsVolume
                : volumes.Count > 0 ? InputEditKind.HfsPlusVolume
                : InputEditKind.ReadOnly;
            return;
        }

        // A Disk Copy 4.2 image of an HFS disk: the disk is edited in place and the data checksum made again
        // (diskcopy42.md §3).
        if (DataFileIsDisk(host) && DiskCopy42Reader.Instance.CanRead(host.File.DataFork))
        {
            long dataSize = new BigEndianReader(host.File.DataFork.ReadPrefix(84)).ReadUInt32At(0x40);
            if (84 + dataSize <= host.File.DataFork.Length && VolumeKind(host.File.DataFork.Slice(84, dataSize)) is { } kind)
            {
                volumes.Add(new EditedVolume(kind, null, new HfsImageRegion(84, dataSize, DiskCopy42: true), ""));
                Kind = kind;
                return;
            }

            Kind = InputEditKind.ReadOnly;
            return;
        }

        // An NDIF (Disk Copy 6) image of an HFS disk, as an AppleDouble pair, a Basilisk II entry, or in MacBinary,
        // AppleSingle or BinHex: the disk is edited as a volume and the image made again around it, saved in the
        // input's own layout (ndif.md §3, §5).
        var image = NdifReader.Instance.CanRead(root.File) ? root : root.Children is [var wrapped] && NdifReader.Instance.CanRead(wrapped.File) ? wrapped : null;
        if (image is not null)
        {
            SaveAsFormat? format = image == root
                ? host.Layout switch { HostLayout.AppleDouble => SaveAsFormat.AppleDoublePair, HostLayout.BasiliskII => SaveAsFormat.BasiliskEntry, _ => null }
                : host.Layout != HostLayout.Plain ? null
                : image.Format.StartsWith("MacBinary", StringComparison.Ordinal) ? SaveAsFormat.MacBinary
                : image.Format.StartsWith("AppleSingle", StringComparison.Ordinal) ? SaveAsFormat.AppleSingle
                : image.Format.StartsWith("BinHex", StringComparison.Ordinal) ? SaveAsFormat.BinHex
                : null;
            if (format is { } chosen && NdifWriter.CanRewrite(image.File) && image.Children is [var disk] && VolumeKind(disk.File.DataFork) is { } kind)
            {
                ndif = image.File;
                singleFormat = chosen;
                volumes.Add(new EditedVolume(kind, null, null, ""));
                Kind = kind;
                return;
            }

            Kind = InputEditKind.ReadOnly;
            return;
        }

        // One Mac file: the input itself, or the one file a MacBinary, BinHex or AppleSingle input holds.
        var node = root.Children.Count == 1 ? root.Children[0] : root;
        var found = MacFileResources.Read(node.File, readOptions, diagnostics);
        forkInDataFork = found.Source == ResourceForkSource.DataFork;
        location = ForkSaver.Locate(Path, host, root, node, forkInDataFork);
        if (location is null)
        {
            Kind = InputEditKind.ReadOnly;
            return;
        }

        Kind = InputEditKind.SingleFile;
        single = node.File;
        forks[""] = new EditSession(found.Fork ?? new ResourceFork());
        singleFormat = location.Target switch
        {
            SaveTarget.MacBinary => SaveAsFormat.MacBinary,
            SaveTarget.BinHex => SaveAsFormat.BinHex,
            SaveTarget.AppleSingle => SaveAsFormat.AppleSingle,
            SaveTarget.AppleDoubleHeader => SaveAsFormat.AppleDoublePair,
            SaveTarget.BasiliskResourceFork => SaveAsFormat.BasiliskEntry,
            _ => SaveAsFormat.RawFork,
        };
    }

    /// <summary>The input, as a full path.</summary>
    public string Path { get; }

    /// <summary>
    /// What the input lets the session change. A disk with several Mac volume partitions is <see
    /// cref="InputEditKind.HfsVolume"/> when one of them is HFS; <see cref="KindOf"/> tells each partition's.
    /// </summary>
    public InputEditKind Kind { get; }

    /// <summary>
    /// On a disk with several Mac volume partitions, the HFS and HFS Plus ones the session edits, by name: the first
    /// name of every path. Empty for any other input.
    /// </summary>
    public IReadOnlyList<string> PartitionNames => [.. volumes.Where(v => v.Name.Length > 0).Select(v => v.Name)];

    /// <summary>What the session can change at a path: its partition's kind on a disk with several (read only when it names none), else <see cref="Kind"/>.</summary>
    public InputEditKind KindOf(string macPath)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        return PartitionNames.Count == 0 ? Kind : Find(macPath) is { } found ? found.Volume.Kind : InputEditKind.ReadOnly;
    }

    /// <summary>The changes made so far, in order.</summary>
    public IReadOnlyList<PlannedChange> Changes => changes;

    /// <summary>Whether anything has changed.</summary>
    public bool HasChanges => changes.Count > 0;

    /// <summary>
    /// A volume's image with the item changes made so far (the input's own bytes before any; for a partitioned disk, its
    /// HFS partition's), made whole in memory: the session itself holds only the sectors it changed.
    /// </summary>
    public byte[] Volume => VolumeOf("");

    /// <summary>The volume holding <paramref name="macPath"/>, as <see cref="Volume"/> gives it: on a disk with several partitions, the partition the path names.</summary>
    public byte[] VolumeOf(string macPath)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        return Overlay(Route(macPath, "have a volume image").Volume).ToArray();
    }

    /// <summary>How many bytes the session's changes hold (the sectors written over the input's volumes).</summary>
    public long ChangedBytes => volumes.Sum(v => (v.Overlay?.Sectors.Count ?? 0) * 512L);

    /// <summary>For a partitioned disk with one Mac volume partition, that partition; null otherwise.</summary>
    public MacPartition? Partition => volumes is [var only] ? only.Partition : null;

    /// <summary>
    /// Where the volume lies in the input when it is not the whole file (a partition, a Disk Copy image's disk); null for
    /// a whole file, and on a disk with several partitions.
    /// </summary>
    public HfsImageRegion? Region => volumes is [var only] ? only.Region : null;

    // A volume the session edits: the input's (a plain image, a Disk Copy or NDIF image's disk, a partitioned disk's
    // only Mac volume), or one partition of a disk with several, named by the first name of its paths.
    private sealed class EditedVolume(InputEditKind kind, MacPartition? partition, HfsImageRegion? region, string name)
    {
        public InputEditKind Kind { get; } = kind;

        public MacPartition? Partition { get; } = partition;

        public HfsImageRegion? Region { get; } = region;

        // The partition's name on a disk with several; "" otherwise.
        public string Name { get; } = name;

        // The volume as edited (the input's, and the sectors written over it), once anything reads or changes it.
        public HfsVolume? Overlay { get; set; }

        public bool Prepared { get; set; }
    }

    // The volume as edited: the input's, read where it lies (the file, a partition, a Disk Copy disk, the decoded NDIF
    // disk), and the sectors written over it.
    private HfsVolume Overlay(EditedVolume volume) => volume.Overlay ??= new HfsVolume(VolumeData(volume));

    private ForkData VolumeData(EditedVolume volume)
    {
        if (ndif is not null)
        {
            return NdifReader.Instance.Read(ndif, new ContainerContext(options)).Single().DataFork;
        }

        // The host file as read (on disk, or in memory for a session over changes not saved yet).
        return volume.Region is not { } region ? host.File.DataFork : host.File.DataFork.Slice(region.Offset, region.Length);
    }

    // The volume a session path is in, and the path inside it: on a disk with several partitions, the partition its
    // first name names (compared as the catalog compares names).
    private (EditedVolume Volume, string Path)? Find(string macPath)
    {
        if (volumes is [{ Name: "" } only])
        {
            return (only, macPath);
        }

        var colon = macPath.IndexOf(':', StringComparison.Ordinal);
        var name = colon < 0 ? macPath : macPath[..colon];
        return volumes.FirstOrDefault(v => MacPaths.NamesEqual(v.Name, name)) is { } volume
            ? (volume, colon < 0 ? "" : macPath[(colon + 1)..])
            : null;
    }

    private (EditedVolume Volume, string Path) Route(string macPath, string what)
    {
        if (volumes.Count == 0)
        {
            throw NotVolume(what);
        }

        return Find(macPath) ?? throw new InvalidOperationException(
            $"{System.IO.Path.GetFileName(Path)} has several partitions: a path starts with the name of one ClassicMac writes ({string.Join(", ", PartitionNames)}).");
    }

    // An HFS volume (signature 'BD') that does not wrap HFS Plus, an HFS Plus or HFSX volume ('H+', 'HX', or 'BD'
    // wrapping HFS Plus), or neither.
    private static InputEditKind? VolumeKind(ForkData data)
    {
        var mdb = data.ReadPrefix(1024 + 0x7E);
        if (mdb.Length != 1024 + 0x7E)
        {
            return null;
        }

        var reader = new BigEndianReader(mdb);
        return (reader.ReadUInt16At(1024), reader.ReadUInt16At(1024 + 0x7C)) switch
        {
            (0x482B or 0x4858, _) or (0x4244, 0x482B) => InputEditKind.HfsPlusVolume,
            (0x4244, _) => InputEditKind.HfsVolume,
            _ => null,
        };
    }

    /// <summary>Opens <paramref name="path"/> as the CLI and the app read it.</summary>
    public static InputEditSession Open(string path, ContainerReadOptions? options = null, ReadOptions? readOptions = null,
        ICollection<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= ContainerReadOptions.Default;
        diagnostics ??= new List<Diagnostic>();
        var host = HostFiles.Read(path, options, diagnostics);
        // One level is enough to tell what the input lets the session change (wrappers of one file do not count):
        // the archives and disk images stored in a volume are not opened.
        var root = ContainerUnwrapper.Default.Unwrap(host.File, HostFiles.FormatName(host.Layout),
            new ContainerContext(options, diagnostics, siblings: HostFiles.Siblings(path, options, diagnostics)), levels: 1);
        return new InputEditSession(path, host, root, options, readOptions ?? ReadOptions.Default, diagnostics);
    }

    /// <summary>A type or creator code typed by a person: up to four Mac OS Roman characters, padded with spaces.</summary>
    public static bool TryParseCode(string text, out FourCC code)
    {
        ArgumentNullException.ThrowIfNull(text);
        code = default;
        return text.Length <= 4 && FourCC.TryParse(text.PadRight(4), out code);
    }

    private InvalidOperationException NotVolume(string what) =>
        new($"{System.IO.Path.GetFileName(Path)} is not a plain HFS volume image, so it does not {what}.");

    // The HFS volume a path is in, prepared, and the path inside it.
    private (EditedVolume Volume, string Path) RequireVolume(string macPath, string what)
    {
        var (volume, inner) = Route(macPath, what);
        if (volume.Kind != InputEditKind.HfsVolume)
        {
            throw NotVolume(what);
        }

        Prepare(volume);
        return (volume, inner);
    }

    // Before the first change to a volume: what hfsutils writes and Disk First Aid rejects is made as Mac OS writes it
    // (short thread records at full length, file records' reserved fields cleared; hfs.md §1.9), each listed as a
    // change of its own.
    private void Prepare(EditedVolume volume)
    {
        if (volume.Prepared || volume.Kind != InputEditKind.HfsVolume)
        {
            return;
        }

        volume.Prepared = true;
        var (repaired, threads, files) = HfsWriter.RepairCatalog(Overlay(volume));
        volume.Overlay = repaired;
        if (threads > 0)
        {
            changes.Add(new PlannedChange("repair", volume.Name,
                $"{threads} thread record{(threads == 1 ? "" : "s")} written at Mac OS's full length (Disk First Aid rejects shorter ones)"));
        }

        if (files > 0)
        {
            changes.Add(new PlannedChange("repair", volume.Name,
                $"{files} file record{(files == 1 ? "'s" : "s'")} reserved fields cleared (Disk First Aid reports them)"));
        }
    }

    private void RequireEditable()
    {
        if (Kind == InputEditKind.ReadOnly)
        {
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(Path)} cannot be edited: it is inside a container ClassicMac does not write.");
        }
    }

    /// <summary>Adds <paramref name="file"/> (its forks, Finder info and dates) at <paramref name="macPath"/> in a volume.</summary>
    public void AddFile(string macPath, MacFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var (volume, inner) = RequireVolume(macPath, "hold files");
        var data = file.DataFork.ToArray();
        var resource = file.ResourceFork.ToArray();
        volume.Overlay = HfsWriter.CreateFile(Overlay(volume), inner, data, resource, file.FinderInfo, file.Created, file.Modified);
        changes.Add(new PlannedChange("add", macPath,
            $"data {data.Length} bytes, resources {resource.Length} bytes, {file.FinderInfo.Type}/{file.FinderInfo.Creator}"));
    }

    /// <summary>Adds an empty folder at <paramref name="macPath"/> in a volume.</summary>
    public void AddFolder(string macPath)
    {
        var (volume, inner) = RequireVolume(macPath, "hold folders");
        volume.Overlay = HfsWriter.CreateFolder(Overlay(volume), inner);
        changes.Add(new PlannedChange("mkdir", macPath, "a new folder"));
    }

    /// <summary>Deletes a file, or a folder: an empty one, or with <paramref name="recursive"/> everything in it.</summary>
    /// <param name="macPath">The item.</param>
    /// <param name="recursive">Whether a folder with contents is deleted, with everything in it.</param>
    /// <param name="warnings">What the deletion breaks, recorded on the change (for example the aliases that lose their original).</param>
    public void Delete(string macPath, bool recursive = false, IReadOnlyList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        var (volume, inner) = RequireVolume(macPath, "hold files");
        volume.Overlay = HfsWriter.Delete(Overlay(volume), inner, recursive);
        foreach (var key in forks.Keys.Where(k => Within(k, macPath)).ToList())
        {
            forks.Remove(key);
        }

        changes.Add(new PlannedChange("delete", macPath, recursive ? "with everything in it" : "") { Warnings = warnings ?? [] });
    }

    /// <summary>Renames the item at <paramref name="macPath"/> in its folder; resource edits made to it (or in it) follow.</summary>
    public void Rename(string macPath, string newName)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        ArgumentNullException.ThrowIfNull(newName);
        RequireEditable();
        if (Kind == InputEditKind.SingleFile)
        {
            if (singleFormat is SaveAsFormat.RawFork)
            {
                throw new InvalidOperationException("A resource fork file has no Mac name to change.");
            }

            single = single! with { Name = MacString.FromMacRoman(newName) };
            changes.Add(new PlannedChange("rename", macPath, $"to {newName}"));
            return;
        }

        var (volume, inner) = Route(macPath, "hold files");
        Prepare(volume);
        volume.Overlay = HfsWriter.Rename(Overlay(volume), inner, newName);
        var parent = macPath.Contains(':') ? macPath[..(macPath.LastIndexOf(':') + 1)] : "";
        var renamed = parent + newName;
        foreach (var key in forks.Keys.Where(k => Within(k, macPath)).ToList())
        {
            forks[renamed + key[macPath.Length..]] = forks[key];
            forks.Remove(key);
        }

        changes.Add(new PlannedChange("rename", macPath, $"to {newName}"));
    }

    /// <summary>Whether <see cref="Resize"/> takes the input: a plain HFS volume image (not a partition, nor a Disk Copy or NDIF image's disk).</summary>
    public bool CanResize => Kind == InputEditKind.HfsVolume && ndif is null && volumes is [{ Region: null }];

    /// <summary>The volume's size in bytes as edited so far; for an input <see cref="CanResize"/> takes.</summary>
    public long VolumeSize => CanResize ? Overlay(volumes[0]).Length : throw NotVolume("have a size to change");

    /// <summary>The smallest size the volume, as edited so far, shrinks to (hfs.md §3.3); for an input <see cref="CanResize"/> takes.</summary>
    public long SmallestSize => CanResize ? HfsWriter.SmallestSize(Overlay(volumes[0]).AsForkData()) : throw NotVolume("have a size to change");

    /// <summary>
    /// Grows or shrinks a plain volume image to <paramref name="size"/> bytes (hfs.md §3.2, §3.3); not a partitioned
    /// disk's partition, nor a Disk Copy or NDIF image's disk.
    /// </summary>
    public void Resize(long size)
    {
        if (volumes.Count > 1)
        {
            throw PartitionNotResized();
        }

        var (volume, _) = RequireVolume("", "have a size to change");
        if (ndif is not null)
        {
            throw new InvalidOperationException("An NDIF image's disk cannot be resized yet.");
        }

        if (volume.Region is not null)
        {
            throw volume.Partition is not null ? PartitionNotResized() : new InvalidOperationException("A Disk Copy image's disk cannot be resized yet.");
        }

        // Resizing rewrites the volume whole: it is held in memory from here, and saved whole.
        volume.Overlay = new HfsVolume(ForkData.FromBytes(HfsWriter.Resize(Overlay(volume).AsForkData(), size)));
        resized = true;
        changes.Add(new PlannedChange("resize", "", $"to {size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes"));
    }

    /// <summary>
    /// Defragments the volume (hfs.md §3.4): every fork in one extent and the free space in one run at the end, written
    /// over the volume where its sectors differ. On a disk with several partitions, <paramref name="volume"/> names the
    /// partition.
    /// </summary>
    public void Defragment(string volume = "")
    {
        ArgumentNullException.ThrowIfNull(volume);
        var (edited, _) = RequireVolume(volume, "have a volume to defragment");
        var overlay = Overlay(edited);
        var bytes = HfsWriter.Defragment(overlay.AsForkData());
        var sector = new byte[512];
        for (long at = 0; at < bytes.Length; at += sector.Length)
        {
            var laidOut = bytes.AsSpan((int)at, sector.Length);
            overlay.Read(at, sector);
            if (!laidOut.SequenceEqual(sector))
            {
                overlay.Write(at, laidOut);
            }
        }

        changes.Add(new PlannedChange("defragment", edited.Name, "every fork in one extent, the free space in one run at the end"));
    }

    private static InvalidOperationException PartitionNotResized() => new("A partition of a partitioned disk cannot be resized: its map would change.");

    /// <summary>
    /// Repairs the volume as Disk First Aid would, with ClassicMac's safe extras (hfs.md §5.6): the repairs become this
    /// session's changes; a volume that appears to be OK or cannot be repaired is left as it is. On a disk with several
    /// partitions, <paramref name="volume"/> names the partition.
    /// </summary>
    public FirstAidRepairResult Repair(string volume = "")
    {
        ArgumentNullException.ThrowIfNull(volume);
        // Not prepared first: the writer's own checks refuse much of what First Aid repairs.
        var (edited, _) = Route(volume, "have a volume to repair");
        var result = FirstAidRepairer.Repair(Overlay(edited));
        if (result.Repaired is { } repaired)
        {
            edited.Overlay = repaired;
            changes.AddRange(result.Changes.Select(c => edited.Name.Length == 0 ? c : c with { Path = c.Path.Length == 0 ? edited.Name : edited.Name + ":" + c.Path }));
        }

        return result;
    }

    /// <summary>Locks or unlocks a volume's file (an HFS folder has no lock).</summary>
    public void SetLocked(string macPath, bool locked)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        var (volume, inner) = RequireVolume(macPath, "hold files");
        volume.Overlay = HfsWriter.SetLocked(Overlay(volume), inner, locked);
        changes.Add(new PlannedChange(locked ? "lock" : "unlock", macPath, ""));
    }

    /// <summary>Blesses a folder holding a System file as the volume's System Folder.</summary>
    public void Bless(string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        var (volume, inner) = RequireVolume(folderPath, "hold folders");
        volume.Overlay = HfsWriter.Bless(Overlay(volume), inner);
        changes.Add(new PlannedChange("bless", folderPath, "as the System Folder"));
    }

    /// <summary>
    /// Moves the item at <paramref name="macPath"/> into the folder at <paramref name="folderPath"/> (empty: the volume's
    /// top level); resource edits made to it (or in it) follow.
    /// </summary>
    public void Move(string macPath, string folderPath)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        ArgumentNullException.ThrowIfNull(folderPath);
        var (volume, inner) = RequireVolume(macPath, "hold folders");
        var (into, folder) = Route(folderPath, "hold folders");
        if (into != volume)
        {
            throw new InvalidOperationException($"{macPath} cannot be moved to another partition.");
        }

        volume.Overlay = HfsWriter.Move(Overlay(volume), inner, folder);
        var name = macPath.Contains(':') ? macPath[(macPath.LastIndexOf(':') + 1)..] : macPath;
        var moved = folderPath.Length == 0 ? name : folderPath + ":" + name;
        foreach (var key in forks.Keys.Where(k => Within(k, macPath)).ToList())
        {
            forks[moved + key[macPath.Length..]] = forks[key];
            forks.Remove(key);
        }

        changes.Add(new PlannedChange("move", macPath, folderPath.Length == 0 ? "to the volume's top level" : $"to {folderPath}"));
    }

    /// <summary>
    /// Sets a file's type, creator and Finder flags (those given), or a folder's Finder flags. A resource fork file has
    /// no Finder info.
    /// </summary>
    public void SetInfo(string macPath, FourCC? type = null, FourCC? creator = null, FinderFlags? flags = null)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        RequireEditable();
        var detail = string.Join(", ", new[]
        {
            type is { } t ? $"type {t}" : null,
            creator is { } c ? $"creator {c}" : null,
            flags is { } f ? $"flags {f}" : null,
        }.Where(s => s is not null));
        if (Kind == InputEditKind.SingleFile)
        {
            if (singleFormat is SaveAsFormat.RawFork)
            {
                throw new InvalidOperationException("A resource fork file has no Finder info.");
            }

            single = single! with { FinderInfo = With(single.FinderInfo, type, creator, flags) };
            changes.Add(new PlannedChange("set", macPath, detail));
            return;
        }

        var (volume, inner) = Route(macPath, "hold files");
        if (FileAt(volume, inner) is { } file)
        {
            Prepare(volume);
            volume.Overlay = HfsWriter.SetFinderInfo(Overlay(volume), inner, With(file.FinderInfo, type, creator, flags));
        }
        else
        {
            if (type is not null || creator is not null)
            {
                throw new InvalidOperationException($"{macPath} is a folder: it has no type or creator.");
            }

            Prepare(volume);
            volume.Overlay = HfsWriter.SetFolderFlags(Overlay(volume), inner, flags ?? FinderFlags.None);
        }

        changes.Add(new PlannedChange("set", macPath, detail));
    }

    private static FinderInfo With(FinderInfo info, FourCC? type, FourCC? creator, FinderFlags? flags) =>
        info with { Type = type ?? info.Type, Creator = creator ?? info.Creator, Flags = flags ?? info.Flags };

    /// <summary>The resources of the file at <paramref name="macPath"/> (empty for a single-file input), with the edits made so far.</summary>
    public ResourceFork Resources(string macPath) => Session(macPath).Fork;

    /// <summary>Sets a resource: replaces its data (and name, when given) when it exists, else adds it.</summary>
    public void SetResource(string macPath, FourCC type, short id, ReadOnlyMemory<byte> data, MacString? name = null)
    {
        var session = Session(macPath);
        if (session.Fork.Find(type, id) is { } existing)
        {
            session.Execute(new SetResourceData(existing, data));
            if (name is not null)
            {
                session.Execute(new SetResourceInfo(existing, id, name, existing.Attributes));
            }
        }
        else
        {
            session.Execute(new AddResource(type, id, name, data));
        }

        changes.Add(new PlannedChange("res-set", macPath, $"'{type}' {id}, {data.Length} bytes"));
    }

    /// <summary>Deletes a resource.</summary>
    public void DeleteResource(string macPath, FourCC type, short id)
    {
        var session = Session(macPath);
        if (session.Fork.Find(type, id) is not { } existing)
        {
            throw new InvalidOperationException($"{(macPath.Length == 0 ? "The file" : macPath)} has no '{type}' {id}.");
        }

        session.Execute(new DeleteResource(existing));
        changes.Add(new PlannedChange("res-delete", macPath, $"'{type}' {id}"));
    }

    private EditSession Session(string macPath)
    {
        ArgumentNullException.ThrowIfNull(macPath);
        RequireEditable();
        if (forks.TryGetValue(macPath, out var session))
        {
            return session;
        }

        if (Kind == InputEditKind.SingleFile)
        {
            throw new InvalidOperationException("A single-file input's resources are at the path \"\".");
        }

        var (volume, inner) = Route(macPath, "hold files");
        Prepare(volume);
        var file = FileAt(volume, inner) ?? throw new InvalidOperationException($"There is no file {macPath}.");
        var bytes = file.ResourceFork.ToArray();
        session = new EditSession(bytes.Length == 0 ? new ResourceFork() : ResourceFork.Read(bytes));
        forks[macPath] = session;
        return session;
    }

    // The file at a path in the volume as edited so far, or null (a folder, or nothing).
    private MacFile? FileAt(EditedVolume volume, string macPath) =>
        HfsReader.Instance.Read(Overlay(volume).AsForkData(), new ContainerContext(options))
            .FirstOrDefault(f => string.Join(":", f.FolderPath.Select(n => n.ToMacRoman()).Append(f.Name.ToMacRoman())) == macPath);

    private static bool Within(string key, string macPath) => key == macPath || key.StartsWith(macPath + ":", StringComparison.Ordinal);

    /// <summary>
    /// Writes the input with the changes to <paramref name="destination"/>, a new file (never the input), verified;
    /// returns the paths written (an AppleDouble pair or a Basilisk II entry is more than one).
    /// </summary>
    public IReadOnlyList<string> SaveAs(string destination) => SaveAs(destination, []);

    /// <summary>
    /// Save As with forks edited outside the session (the app's resource editors) replaced too: a volume's files'
    /// forks, by their paths in the volume.
    /// </summary>
    public IReadOnlyList<string> SaveAs(string destination, IReadOnlyList<HfsForkReplacement> otherForks)
    {
        ArgumentNullException.ThrowIfNull(otherForks);
        extra = otherForks;
        try
        {
            return SaveAsCore(destination);
        }
        finally
        {
            extra = [];
        }
    }

    private IReadOnlyList<string> SaveAsCore(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        RequireEditable();
        var full = System.IO.Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full, Path, comparison))
        {
            throw new InvalidOperationException("Save As writes a new file; saving over the input needs Save In Place.");
        }

        if (Kind == InputEditKind.SingleFile)
        {
            return ForkSaver.SaveAs(full, singleFormat, single!, forks[""].Fork, forkInDataFork);
        }

        if (ndif is not null)
        {
            var image = Rewritten();
            return ForkSaver.SaveAs(full, singleFormat, image, ResourceForkOf(image));
        }

        WriteVolume(full);
        return host.Layout == HostLayout.Plain ? [full] : [full, .. WriteCompanion(full)];
    }

    // The disk is the data file itself, alone or with an AppleDouble header or Basilisk II companions beside it (its
    // type, creator and resource fork), which an edit leaves alone.
    private static bool DataFileIsDisk(HostFile host) => host.Layout is HostLayout.Plain or HostLayout.AppleDouble or HostLayout.BasiliskII;

    // The input's companions written beside destination: the pair written with no data in a temporary folder, its
    // files other than the data file moved into place.
    private List<string> WriteCompanion(string destination)
    {
        var directory = System.IO.Path.GetDirectoryName(destination)!;
        var staging = System.IO.Path.Combine(directory, $".classicmac-{Guid.NewGuid():N}");
        try
        {
            var name = System.IO.Path.GetFileName(destination);
            var paths = HostFiles.Write(host.File with { DataFork = ForkData.Empty }, staging,
                new HostWriteOptions { Layout = host.Layout, Overwrite = true }, name);
            var moved = new List<string>();
            foreach (var path in paths.Where(p => System.IO.Path.GetFullPath(p) != System.IO.Path.Combine(staging, name)))
            {
                var target = System.IO.Path.Combine(directory, System.IO.Path.GetRelativePath(staging, path));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Move(path, target, overwrite: true);
                moved.Add(target);
            }

            return moved;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    // Writes the input with the volumes' changes to destination through a temporary file beside it: a streamed copy of
    // the input with each volume's changed sectors written into it where it lies (a resized volume whole), a Disk Copy
    // 4.2 image's checksum made again; the copy is read back (the writer's checks on each volume changed, every changed
    // sector compared) and then moved into place, so destination is never half written.
    private void WriteVolume(string destination)
    {
        var edits = EditedVolumes();
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, $".classicmac-{Guid.NewGuid():N}.tmp");
        try
        {
            if (resized)
            {
                File.WriteAllBytes(temporary, edits.Single().Edited.ToArray());
            }
            else
            {
                File.Copy(Path, temporary);
                using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var sector = new byte[512];
                foreach (var (volume, edited) in edits)
                {
                    long at = volume.Region?.Offset ?? 0;
                    foreach (var number in edited.Sectors.Order())
                    {
                        var length = (int)Math.Min(512, edited.Length - number * 512);
                        edited.Read(number * 512, sector.AsSpan(0, length));
                        stream.Position = at + number * 512;
                        stream.Write(sector, 0, length);
                    }

                    if (volume.Region is { DiskCopy42: true })
                    {
                        var sum = new BigEndianWriter(4);
                        sum.WriteUInt32(DiskCopy42Reader.Sum(edited.AsForkData()));
                        stream.Position = 0x48;
                        stream.Write(sum.WrittenSpan);
                    }
                }
            }

            var written = ForkData.FromFile(temporary);
            string? fault = null;
            var original = new byte[512];
            var copy = new byte[512];
            foreach (var (volume, edited) in edits)
            {
                var disk = volume.Region is not { } region ? written : written.Slice(region.Offset, region.Length);
                // The writer's checks for HFS; an HFS Plus volume, which only First Aid writes, is compared sector by sector.
                fault ??= volume.Kind == InputEditKind.HfsPlusVolume ? null : HfsWriter.Check(disk);
                foreach (var number in resized ? [] : edited.Sectors)
                {
                    var length = (int)Math.Min(512, edited.Length - number * 512);
                    edited.Read(number * 512, original.AsSpan(0, length));
                    disk.ReadAt(number * 512, copy.AsSpan(0, length));
                    if (!original.AsSpan(0, length).SequenceEqual(copy.AsSpan(0, length)))
                    {
                        fault ??= $"sector {number} reads back differently";
                    }
                }
            }

            ForkData.CloseHostFile(temporary);
            if (fault is not null)
            {
                throw new SaveVerificationException([$"The saved volume did not read back as edited: {fault}"]);
            }

            ForkData.CloseHostFile(destination);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                ForkData.CloseHostFile(temporary);
                File.Delete(temporary);
            }
        }
    }

    // Each volume read or changed, with its fork edits made (a plain volume always, so a save checks it).
    private List<(EditedVolume Volume, HfsVolume Edited)> EditedVolumes() =>
        [.. volumes.Select(v => (Volume: v, Forks: Replacements(v)))
            .Where(e => e.Volume.Overlay is not null || e.Forks.Count > 0 || volumes.Count == 1)
            .Select(e => (e.Volume, e.Forks.Count > 0 ? ForkSaver.ApplyHfsForks(Overlay(e.Volume), e.Forks) : Overlay(e.Volume)))];

    private IReadOnlyList<HfsForkReplacement> extra = [];

    /// <summary>Opens an input already read (its host file and what it unwraps to), as the app has it.</summary>
    public static InputEditSession Open(string path, HostFile host, ContainerNode root, ContainerReadOptions? options = null,
        ReadOptions? readOptions = null, ICollection<Diagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(root);
        return new InputEditSession(path, host, root, options ?? ContainerReadOptions.Default, readOptions ?? ReadOptions.Default,
            diagnostics ?? new List<Diagnostic>());
    }

    /// <summary>Writes the changes over the input, verified, keeping the original as <c>.orig</c> on the first save.</summary>
    public void SaveInPlace()
    {
        RequireEditable();
        if (Kind == InputEditKind.SingleFile)
        {
            ForkSaver.Save(location! with { File = single! }, forks[""].Fork);
            return;
        }

        if (ndif is not null)
        {
            SaveNdifInPlace();
            return;
        }

        // An input another program has open (an image mounted in an emulator) is not replaced.
        ForkData.CloseHostFile(Path);
        try
        {
            using var exclusive = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            throw new IOException($"{System.IO.Path.GetFileName(Path)} is open in another program (a mounted disk image?); close it there and save again.", e);
        }

        var backup = Path + ".orig";
        if (!File.Exists(backup))
        {
            File.Copy(Path, backup);
        }

        WriteVolume(Path);
    }

    // The NDIF image made again around the edited disk (its forks' edits made too).
    // The changed sectors name the chunks to store again, so the old disk is not decoded to compare them.
    private MacFile Rewritten()
    {
        var edited = ForkSaver.ApplyHfsForks(Overlay(volumes[0]), Replacements(volumes[0]));
        return NdifWriter.Rewrite(ndif!, edited.ToArray(), edited.Sectors.ToHashSet());
    }

    private static ResourceFork ResourceForkOf(MacFile file) => ResourceFork.Read(file.ResourceFork.ToArray());

    // Writes the image in a temporary folder beside the input, then moves each file written over its original (the
    // data file, and the AppleDouble header or Basilisk II companions), keeping each original as .orig the first time.
    private void SaveNdifInPlace()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var temporary = System.IO.Path.Combine(folder, $".classicmac-{Guid.NewGuid():N}");
        try
        {
            var image = Rewritten();
            var written = ForkSaver.SaveAs(System.IO.Path.Combine(temporary, System.IO.Path.GetFileName(Path)), singleFormat, image, ResourceForkOf(image));
            foreach (var file in written)
            {
                var target = System.IO.Path.Combine(folder, System.IO.Path.GetRelativePath(temporary, file));
                if (File.Exists(target) && !File.Exists(target + ".orig"))
                {
                    File.Copy(target, target + ".orig");
                }

                ForkData.CloseHostFile(target);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
            }
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    /// <summary>
    /// The input as it stands with the changes made so far, in memory, as Save As would write it: a volume with its
    /// fork edits, put back in its partition or Disk Copy image, or an NDIF image made again (as an AppleDouble pair or
    /// a Basilisk II entry). Null for other inputs, which are read from a saved copy.
    /// </summary>
    public HostFile? Current()
    {
        if (Kind is not (InputEditKind.HfsVolume or InputEditKind.HfsPlusVolume))
        {
            return null;
        }

        if (ndif is not null)
        {
            return host.File == ndif ? host with { File = Rewritten() } : null;
        }

        var whole = host.File.DataFork;
        foreach (var (volume, changed) in EditedVolumes())
        {
            var edited = changed.AsForkData();
            if (volume.Region is not { } region)
            {
                whole = edited;
                continue;
            }

            whole = ForkData.Splice(whole, region.Offset, edited);
            if (region.DiskCopy42)
            {
                var sum = new BigEndianWriter(4);
                sum.WriteUInt32(DiskCopy42Reader.Sum(edited));
                whole = ForkData.Splice(whole, 0x48, ForkData.FromBytes(sum.ToArray()));
            }
        }

        return host with { File = host.File with { DataFork = whole } };
    }

    // The forks edited in a volume, by their paths in it.
    private List<HfsForkReplacement> Replacements(EditedVolume volume) =>
        [.. forks.Where(f => f.Value.IsDirty).Select(f => new HfsForkReplacement(f.Key, f.Value.Fork)).Concat(extra)
            .Select(r => (Replacement: r, At: Route(r.MacPath, "hold files")))
            .Where(r => r.At.Volume == volume)
            .Select(r => r.Replacement with { MacPath = r.At.Path })];
}
