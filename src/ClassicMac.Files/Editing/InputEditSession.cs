using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;
using ClassicMac.Resources.Editing;

namespace ClassicMac.Files.Editing
{
    /// <summary>What an opened input lets an <see cref="InputEditSession"/> change.</summary>
    public enum InputEditKind
    {
        /// <summary>Nothing: a file inside an archive or a disk image that is not a plain HFS volume, a PC Exchange folder …</summary>
        ReadOnly,

        /// <summary>A plain HFS volume image: its files and folders, their Finder info, and its files' resources.</summary>
        HfsVolume,

        /// <summary>One Mac file (a resource fork, MacBinary, BinHex, AppleSingle, AppleDouble or Basilisk II file): its resources and Finder info.</summary>
        SingleFile,
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
    /// write commands share it. Paths are inside the volume, without its name (<c>Docs:Letter</c>).
    /// </summary>
    public sealed class InputEditSession
    {
        private readonly List<PlannedChange> changes = [];
        private readonly Dictionary<string, EditSession> forks = new(StringComparer.Ordinal);
        private readonly ContainerReadOptions options;
        private readonly SaveLocation? location;
        private readonly SaveAsFormat singleFormat;
        private readonly bool forkInDataFork;
        private HfsVolume? overlay;
        private bool resized;
        private readonly MacPartition? partition;
        private readonly HfsImageRegion? region;
        private readonly MacFile? ndif;
        private readonly HostFile host;
        private MacFile? single;

        private InputEditSession(string path, HostFile host, ContainerNode root, ContainerReadOptions options, ReadOptions readOptions,
            ICollection<Diagnostic> diagnostics)
        {
            Path = System.IO.Path.GetFullPath(path);
            this.options = options;
            this.host = host;
            // A plain HFS volume image, known by its MDB (an empty volume has no files to show it).
            if (DataFileIsDisk(host) && root.Volume?.Format == "HFS")
            {
                Kind = InputEditKind.HfsVolume;
                return;
            }

            // A partitioned disk whose map holds one Mac volume, a plain HFS one: that partition is edited and put back in
            // place (partition-map.md §5). A disk with more is not written.
            if (DataFileIsDisk(host) && PartitionMapReader.Partitions(host.File.DataFork) is { Count: > 0 } partitions)
            {
                if (partitions is [var only] && IsPlainHfs(host.File.DataFork.Slice(only.Offset, only.Length)))
                {
                    partition = only;
                    region = new HfsImageRegion(only.Offset, only.Length);
                    Kind = InputEditKind.HfsVolume;
                    return;
                }

                Kind = InputEditKind.ReadOnly;
                return;
            }

            // A Disk Copy 4.2 image of an HFS disk: the disk is edited in place and the data checksum made again
            // (diskcopy42.md §3).
            if (DataFileIsDisk(host) && DiskCopy42Reader.Instance.CanRead(host.File.DataFork))
            {
                long dataSize = new BigEndianReader(host.File.DataFork.ReadPrefix(84)).ReadUInt32At(0x40);
                if (84 + dataSize <= host.File.DataFork.Length && IsPlainHfs(host.File.DataFork.Slice(84, dataSize)))
                {
                    region = new HfsImageRegion(84, dataSize, DiskCopy42: true);
                    Kind = InputEditKind.HfsVolume;
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
                if (format is { } chosen && NdifWriter.CanRewrite(image.File) && image.Children is [var disk] && IsPlainHfs(disk.File.DataFork))
                {
                    ndif = image.File;
                    singleFormat = chosen;
                    Kind = InputEditKind.HfsVolume;
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

        /// <summary>What the input lets the session change.</summary>
        public InputEditKind Kind { get; }

        /// <summary>The changes made so far, in order.</summary>
        public IReadOnlyList<PlannedChange> Changes => changes;

        /// <summary>Whether anything has changed.</summary>
        public bool HasChanges => changes.Count > 0;

        /// <summary>
        /// A volume's image with the item changes made so far (the input's own bytes before any; for a partitioned disk, its
        /// HFS partition's), made whole in memory: the session itself holds only the sectors it changed.
        /// </summary>
        public byte[] Volume => Overlay.ToArray();

        /// <summary>How many bytes the session's changes hold (the sectors written over the input's volume).</summary>
        public long ChangedBytes => (overlay?.Sectors.Count ?? 0) * 512L;

        // The volume as edited: the input's, read where it lies (the file, a partition, a Disk Copy disk, the decoded NDIF
        // disk), and the sectors written over it.
        private HfsVolume Overlay => overlay ??= Kind == InputEditKind.HfsVolume ? new HfsVolume(VolumeData()) : throw NotVolume("have a volume image");

        /// <summary>For a partitioned disk, the HFS partition edited; null for a plain volume image.</summary>
        public MacPartition? Partition => partition;

        /// <summary>Where the volume lies in the input when it is not the whole file (a partition, a Disk Copy image's disk).</summary>
        public HfsImageRegion? Region => region;

        private ForkData VolumeData()
        {
            if (ndif is not null)
            {
                return NdifReader.Instance.Read(ndif, new ContainerContext(options)).Single().DataFork;
            }

            // The host file as read (on disk, or in memory for a session over changes not saved yet).
            return region is null ? host.File.DataFork : host.File.DataFork.Slice(region.Offset, region.Length);
        }

        // An HFS volume (signature 'BD') that does not wrap HFS Plus.
        private static bool IsPlainHfs(ForkData data)
        {
            var mdb = data.ReadPrefix(1024 + 0x7E);
            return mdb.Length == 1024 + 0x7E && new BigEndianReader(mdb) is var reader &&
                reader.ReadUInt16At(1024) == 0x4244 && reader.ReadUInt16At(1024 + 0x7C) != 0x482B;
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

        private void RequireVolume(string what)
        {
            if (Kind != InputEditKind.HfsVolume)
            {
                throw NotVolume(what);
            }

            Prepare();
        }

        private bool prepared;

        // Before the first change to a volume: thread records shorter than Mac OS's 46 bytes (as hfsutils writes them)
        // are written at full length, since Disk First Aid rejects them (hfs.md §1.9); listed as a change of its own.
        private void Prepare()
        {
            if (prepared || Kind != InputEditKind.HfsVolume)
            {
                return;
            }

            prepared = true;
            var (repaired, count) = HfsWriter.RepairThreads(Overlay);
            if (count > 0)
            {
                overlay = repaired;
                changes.Add(new PlannedChange("repair", "",
                    $"{count} thread record{(count == 1 ? "" : "s")} written at Mac OS's full length (Disk First Aid rejects shorter ones)"));
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
            RequireVolume("hold files");
            var data = file.DataFork.ToArray();
            var resource = file.ResourceFork.ToArray();
            overlay = HfsWriter.CreateFile(Overlay, macPath, data, resource, file.FinderInfo, file.Created, file.Modified);
            changes.Add(new PlannedChange("add", macPath,
                $"data {data.Length} bytes, resources {resource.Length} bytes, {file.FinderInfo.Type}/{file.FinderInfo.Creator}"));
        }

        /// <summary>Adds an empty folder at <paramref name="macPath"/> in a volume.</summary>
        public void AddFolder(string macPath)
        {
            RequireVolume("hold folders");
            overlay = HfsWriter.CreateFolder(Overlay, macPath);
            changes.Add(new PlannedChange("mkdir", macPath, "a new folder"));
        }

        /// <summary>Deletes a file, or a folder: an empty one, or with <paramref name="recursive"/> everything in it.</summary>
        /// <param name="macPath">The item.</param>
        /// <param name="recursive">Whether a folder with contents is deleted, with everything in it.</param>
        /// <param name="warnings">What the deletion breaks, recorded on the change (for example the aliases that lose their original).</param>
        public void Delete(string macPath, bool recursive = false, IReadOnlyList<string>? warnings = null)
        {
            ArgumentNullException.ThrowIfNull(macPath);
            RequireVolume("hold files");
            overlay = HfsWriter.Delete(Overlay, macPath, recursive);
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

            Prepare();
            overlay = HfsWriter.Rename(Overlay, macPath, newName);
            var parent = macPath.Contains(':') ? macPath[..(macPath.LastIndexOf(':') + 1)] : "";
            var renamed = parent + newName;
            foreach (var key in forks.Keys.Where(k => Within(k, macPath)).ToList())
            {
                forks[renamed + key[macPath.Length..]] = forks[key];
                forks.Remove(key);
            }

            changes.Add(new PlannedChange("rename", macPath, $"to {newName}"));
        }

        /// <summary>Grows a plain volume image to <paramref name="size"/> bytes (hfs.md §3.2); not a partitioned disk's partition.</summary>
        public void Resize(long size)
        {
            RequireVolume("have a size to change");
            if (ndif is not null)
            {
                throw new InvalidOperationException("An NDIF image's disk cannot be resized yet.");
            }

            if (region is not null)
            {
                throw new InvalidOperationException(partition is not null
                    ? "A partition of a partitioned disk cannot be resized: its map would change."
                    : "A Disk Copy image's disk cannot be resized yet.");
            }

            // Resizing rewrites the volume whole: it is held in memory from here, and saved whole.
            overlay = new HfsVolume(ForkData.FromBytes(HfsWriter.Resize(Overlay.AsForkData(), size)));
            resized = true;
            changes.Add(new PlannedChange("resize", "", $"to {size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes"));
        }

        /// <summary>Locks or unlocks a volume's file (an HFS folder has no lock).</summary>
        public void SetLocked(string macPath, bool locked)
        {
            ArgumentNullException.ThrowIfNull(macPath);
            RequireVolume("hold files");
            overlay = HfsWriter.SetLocked(Overlay, macPath, locked);
            changes.Add(new PlannedChange(locked ? "lock" : "unlock", macPath, ""));
        }

        /// <summary>Blesses a folder holding a System file as the volume's System Folder.</summary>
        public void Bless(string folderPath)
        {
            ArgumentNullException.ThrowIfNull(folderPath);
            RequireVolume("hold folders");
            overlay = HfsWriter.Bless(Overlay, folderPath);
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
            RequireVolume("hold folders");
            overlay = HfsWriter.Move(Overlay, macPath, folderPath);
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

            if (FileAt(macPath) is { } file)
            {
                Prepare();
                overlay = HfsWriter.SetFinderInfo(Overlay, macPath, With(file.FinderInfo, type, creator, flags));
            }
            else
            {
                if (type is not null || creator is not null)
                {
                    throw new InvalidOperationException($"{macPath} is a folder: it has no type or creator.");
                }

                Prepare();
                overlay = HfsWriter.SetFolderFlags(Overlay, macPath, flags ?? FinderFlags.None);
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

            Prepare();
            var file = FileAt(macPath) ?? throw new InvalidOperationException($"There is no file {macPath}.");
            var bytes = file.ResourceFork.ToArray();
            session = new EditSession(bytes.Length == 0 ? new ResourceFork() : ResourceFork.Read(bytes));
            forks[macPath] = session;
            return session;
        }

        // The file at a path in the volume as edited so far, or null (a folder, or nothing).
        private MacFile? FileAt(string macPath) =>
            HfsReader.Instance.Read(Overlay.AsForkData(), new ContainerContext(options))
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
        private IReadOnlyList<string> WriteCompanion(string destination)
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

        // Writes the input with the volume's changes to destination through a temporary file beside it: a streamed copy of
        // the input with the changed sectors written into it (a resized volume whole), a Disk Copy 4.2 image's checksum
        // made again; the copy is read back (the writer's checks on its volume, every changed sector compared) and then
        // moved into place, so destination is never half written.
        private void WriteVolume(string destination)
        {
            var edited = Replacements() is { Count: > 0 } replaced ? ForkSaver.ApplyHfsForks(Overlay, replaced) : Overlay;
            var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, $".classicmac-{Guid.NewGuid():N}.tmp");
            long at = region?.Offset ?? 0;
            try
            {
                if (resized)
                {
                    File.WriteAllBytes(temporary, edited.ToArray());
                }
                else
                {
                    File.Copy(Path, temporary);
                    using var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    var sector = new byte[512];
                    foreach (var number in edited.Sectors.Order())
                    {
                        var length = (int)Math.Min(512, edited.Length - number * 512);
                        edited.Read(number * 512, sector.AsSpan(0, length));
                        stream.Position = at + number * 512;
                        stream.Write(sector, 0, length);
                    }

                    if (region is { DiskCopy42: true })
                    {
                        var sum = new BigEndianWriter(4);
                        sum.WriteUInt32(DiskCopy42Reader.Sum(edited.AsForkData()));
                        stream.Position = 0x48;
                        stream.Write(sum.WrittenSpan);
                    }
                }

                var written = ForkData.FromFile(temporary);
                var disk = region is null ? written : written.Slice(region.Offset, region.Length);
                var fault = HfsWriter.Check(disk);
                var original = new byte[512];
                var copy = new byte[512];
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
            var edited = ForkSaver.ApplyHfsForks(Overlay, Replacements());
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
            if (Kind != InputEditKind.HfsVolume)
            {
                return null;
            }

            if (ndif is not null)
            {
                return host.File == ndif ? host with { File = Rewritten() } : null;
            }

            var edited = (Replacements() is { Count: > 0 } replaced ? ForkSaver.ApplyHfsForks(Overlay, replaced) : Overlay).AsForkData();
            if (region is not null)
            {
                var whole = ForkData.Splice(host.File.DataFork, region.Offset, edited);
                if (region.DiskCopy42)
                {
                    var sum = new BigEndianWriter(4);
                    sum.WriteUInt32(DiskCopy42Reader.Sum(edited));
                    whole = ForkData.Splice(whole, 0x48, ForkData.FromBytes(sum.ToArray()));
                }

                edited = whole;
            }

            return host with { File = host.File with { DataFork = edited } };
        }

        private List<HfsForkReplacement> Replacements() =>
            [.. forks.Where(f => f.Value.IsDirty).Select(f => new HfsForkReplacement(f.Key, f.Value.Fork)), .. extra];
    }
}
