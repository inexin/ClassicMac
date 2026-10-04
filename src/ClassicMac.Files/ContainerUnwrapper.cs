using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files.Archives;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Fat;
using ClassicMac.Files.Hfs;
using ClassicMac.Files.Iso;
using ClassicMac.Files.Rom;

namespace ClassicMac.Files;

/// <summary>One file in an unwrapped input: the container format it was read with and what it held.</summary>
/// <param name="Format">The format the file was found in (the reader's name, or the host layout at the root).</param>
/// <param name="File">The Mac file.</param>
/// <param name="Children">The files its data fork contained, if the data fork was itself a container.</param>
public sealed record ContainerNode(string Format, MacFile File, IReadOnlyList<ContainerNode> Children)
{
    /// <summary>
    /// The container format of the file's data fork when it was recognised but not read, because the unwrap stopped
    /// at its level limit (<see cref="ContainerUnwrapper.Expand(ContainerNode, ContainerContext, int)"/> reads it); null otherwise.
    /// </summary>
    public string? UnreadFormat
    {
        get => probe is null ? unreadFormat : probe.Value?.FormatName;
        init => unreadFormat = value;
    }

    private readonly string? unreadFormat;

    // The probe that tells UnreadFormat when it is first asked for (a file at the level limit, among thousands on a
    // volume, is not probed until something looks at it).
    private readonly Lazy<IContainerReader?>? probe;

    internal Lazy<IContainerReader?>? Probe
    {
        get => probe;
        init => probe = value;
    }

    /// <summary>The volume's own dates when the file's data fork is a volume that was read (HFS, HFS Plus, MFS); null otherwise.</summary>
    public VolumeInfo? Volume { get; init; }

    /// <summary>The files at the bottom of the tree: those whose data fork is not a container.</summary>
    public IEnumerable<ContainerNode> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(c => c.Leaves());
}

/// <summary>
/// Unwraps nested containers: tries each reader on a file's data fork and, when one matches, reads the files it
/// holds and unwraps those in turn, up to <see cref="ContainerReadOptions.MaxNestingDepth"/>. A MacBinary file inside
/// a BinHex file inside an AppleSingle file unwraps to one tree.
/// </summary>
public sealed class ContainerUnwrapper
{
    private readonly IReadOnlyList<IContainerReader> readers;

    /// <summary>
    /// The built-in readers, in the order they are tried: AppleSingle, AppleDouble, MacBinary III, II and I, BinHex
    /// 4.0, uuencode, DiskDoubler and its split files, PackIt, StuffIt split files and archives, Compact Pro, LHA, zip, gzip, tar, NewWorld "Mac OS ROM" files and Mac ROM images, UDIF, Apple partition maps, Disk Copy 4.2, NDIF, DART, HFS and MFS volumes, DOS partition tables, FAT volumes
    /// with PC Exchange data, raw CD images, cue sheets, and ISO 9660 / High Sierra volumes.
    /// </summary>
    public static ContainerUnwrapper Default { get; } = new([]);

    /// <summary>The built-in readers, with <paramref name="extra"/> tried first.</summary>
    public ContainerUnwrapper(IEnumerable<IContainerReader> extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        readers =
        [
            .. extra,
            AppleSingleReader.AppleSingle,
            AppleSingleReader.AppleDouble,
            MacBinaryReader.III,
            MacBinaryReader.II,
            MacBinaryReader.I,
            BinHexReader.Instance,
            UuencodeReader.Instance,
            DiskDoublerReader.Instance,
            DiskDoublerSplitReader.Instance,
            PackItReader.Instance,
            StuffItSplitReader.Instance,
            StuffItReader.Instance,
            CompactProReader.Instance,
            LhaReader.Instance,
            ZipReader.Instance,
            GzipReader.Instance,
            TarArchiveReader.Instance,
            NewWorldRomReader.Instance,
            MacRomReader.Instance,
            UdifReader.Instance,
            PartitionMapReader.Instance,
            DiskCopy42Reader.Instance,
            NdifReader.Instance,
            DartReader.Instance,
            HfsReader.Instance,
            MfsReader.Instance,
            MbrReader.Instance,
            FatReader.Instance,
            RawCdReader.Instance,
            CueSheetReader.Instance,
            IsoReader.Instance,
        ];
    }

    /// <summary>The readers, in the order they are tried.</summary>
    public IReadOnlyList<IContainerReader> Readers => readers;

    /// <summary>Unwraps <paramref name="file"/>, which was found as <paramref name="format"/>.</summary>
    public ContainerNode Unwrap(MacFile file, string format, ContainerContext context) =>
        Unwrap(file, format, context, int.MaxValue);

    /// <summary>
    /// Unwraps <paramref name="file"/>, reading at most <paramref name="levels"/> levels of containers: a file below
    /// them whose data fork is a container is only recognised, and left with its
    /// <see cref="ContainerNode.UnreadFormat"/> set. A container holding one file (a MacBinary or BinHex wrapper, a
    /// disk image's disk) does not use up a level: its file is read too, since showing the container means showing
    /// that file's contents. Browsing reads one level at a time, so opening a disk does not decompress every archive
    /// on it.
    /// </summary>
    public ContainerNode Unwrap(MacFile file, string format, ContainerContext context, int levels)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        long expanded = 0;
        return Unwrap(file, format, context, 0, levels, null, Probe(file), ref expanded);
    }

    /// <summary>
    /// Reads the unread containers in <paramref name="node"/>'s tree (see <see cref="ContainerNode.UnreadFormat"/>),
    /// at most <paramref name="levels"/> levels below each. A tree with nothing unread comes back as it is. The
    /// siblings of <paramref name="node"/> itself come from <paramref name="context"/>; those of the files below it
    /// from the tree.
    /// </summary>
    public ContainerNode Expand(ContainerNode node, ContainerContext context, int levels = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
        long expanded = 0;
        return Expand(node, context, 0, levels, null, ref expanded);
    }

    private ContainerNode Expand(ContainerNode node, ContainerContext context, int depth, int levels, string? location, ref long expanded)
    {
        if (node.UnreadFormat is not null)
        {
            return Unwrap(node.File, node.Format, context, depth, levels, location, node.Probe?.Value ?? Probe(node.File), ref expanded);
        }

        if (node.Children.Count == 0)
        {
            return node;
        }

        // The files not yet probed, side by side (in order, the first that fails is thrown when reached).
        Parallel.ForEach(node.Children.Where(c => c.Probe is { IsValueCreated: false }), child =>
        {
            try
            {
                _ = child.Probe!.Value;
            }
            catch (Exception)
            {
                // Lazy keeps the exception; the loop below meets it in order.
            }
        });

        var files = node.Children.Select(c => c.File).ToList();
        var children = new List<ContainerNode>(node.Children.Count);
        var changed = false;
        foreach (var child in node.Children)
        {
            var file = child.File;
            var read = Expand(child, context.For(null, () => SiblingsOf(files, file)), depth + 1, levels, Within(location, file), ref expanded);
            changed |= !ReferenceEquals(read, child);
            children.Add(read);
        }
        return changed ? node with { Children = children } : node;
    }

    // A nested file's location: its Mac path, after the files holding it.
    private static string Within(string? location, MacFile file) =>
        location is null ? file.MacPath : $"{location} > {file.MacPath}";

    /// <summary>Reads a host file with its companions and unwraps it.</summary>
    public ContainerNode Unwrap(string path, ContainerReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
    {
        var context = new ContainerContext(options, diagnostics);
        var host = HostFiles.Read(path, context.Options, context.Diagnostics);
        return Unwrap(host.File, HostFiles.FormatName(host.Layout),
            context.For(null, HostFiles.Siblings(path, context.Options, context.Diagnostics)));
    }

    // The first reader whose format the file's data fork is in, or null. The probes share one read of each fork's
    // head and tail.
    private IContainerReader? Probe(MacFile file)
    {
        if (file.DataFork.Length == 0)
        {
            return null;
        }

        var probe = file with { DataFork = ForkData.ForProbing(file.DataFork), ResourceFork = ForkData.ForProbing(file.ResourceFork) };
        return readers.FirstOrDefault(r => r.CanRead(probe));
    }

    // Probes the files side by side (each probe only reads its own file; a disk holds thousands): the readers in
    // the files' order, and the first probe that failed, in order, rethrown when its file is reached.
    private (IContainerReader? Reader, ExceptionDispatchInfo? Error)[] ProbeAll(IReadOnlyList<MacFile> files)
    {
        var found = new (IContainerReader?, ExceptionDispatchInfo?)[files.Count];
        Parallel.For(0, files.Count, index =>
        {
            try
            {
                found[index] = (Probe(files[index]), null);
            }
            catch (Exception e)
            {
                found[index] = (null, ExceptionDispatchInfo.Capture(e));
            }
        });
        return found;
    }

    private ContainerNode Unwrap(MacFile file, string format, ContainerContext context, int depth, int levels, string? location,
        IContainerReader? reader, ref long expanded)
    {
        if (reader is null)
        {
            return new ContainerNode(format, file, []);
        }

        if (levels == 0)
        {
            return new ContainerNode(format, file, []) { UnreadFormat = reader.FormatName };
        }
        // What reading this file's container reports is about this file; the files inside it get their own locations.
        var outer = context;
        if (location is not null)
        {
            context = context.WithDiagnostics(new LocatedDiagnostics(context.Diagnostics, location));
        }

        if (depth >= context.Options.MaxNestingDepth)
        {
            context.Report(DiagnosticSeverity.Warning, "container.too-deep",
                $"\"{file.Name}\" is a {reader.FormatName} file nested {depth + 1} deep, past the " +
                $"{context.Options.MaxNestingDepth}-level limit; not unwrapped.");
            return new ContainerNode(format, file, []);
        }

        IReadOnlyList<MacFile> contents;
        try
        {
            contents = reader.Read(file, context.For(file.Name, context.Siblings));
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            // Damaged data: structures that say what is not there, or data shorter than they say.
            context.Report(DiagnosticSeverity.Error, "container.unreadable",
                $"\"{file.Name}\" looks like {reader.FormatName} but cannot be read: {e.Message}");
            return new ContainerNode(format, file, []);
        }
        catch (Exception e) when (e is OverflowException or IndexOutOfRangeException or ArgumentOutOfRangeException or DivideByZeroException)
        {
            // A reader that fails on damaged data this way has a bug; the file is kept as it is, and the unwrap goes on.
            context.Report(DiagnosticSeverity.Error, "container.reader-fault",
                $"\"{file.Name}\" looks like {reader.FormatName}, and ClassicMac's reader failed on its data ({e.GetType().Name}); it is kept unread.");
            return new ContainerNode(format, file, []);
        }

        // A container holding one file (a wrapper, a disk image's disk) does not use up a level; a volume always does,
        // even with one file on it. Files at the level limit are probed when their format is asked for.
        var below = levels == int.MaxValue || contents.Count == 1 && reader is not IVolumeReader ? levels : levels - 1;
        var probed = below == 0 ? null : ProbeAll(contents);
        var children = new List<ContainerNode>(contents.Count);
        for (var index = 0; index < contents.Count; index++)
        {
            var inner = contents[index];
            probed?[index].Error?.Throw();
            expanded += inner.DataFork.Length + inner.ResourceFork.Length;
            if (expanded > context.Options.MaxExpandedBytesPerInput)
            {
                context.Report(DiagnosticSeverity.Error, "container.too-large",
                    $"Unwrapping produced more than {context.Options.MaxExpandedBytesPerInput} bytes; stopped.");
                break;
            }
            if (probed is null)
            {
                children.Add(new ContainerNode(reader.FormatName, inner, []) { Probe = new Lazy<IContainerReader?>(() => Probe(inner)) });
                continue;
            }

            children.Add(Unwrap(inner, reader.FormatName, outer.For(null, () => SiblingsOf(contents, inner)), depth + 1,
                below, Within(location, inner), probed[index].Reader, ref expanded));
        }
        return new ContainerNode(format, file, children) { Volume = (reader as IVolumeReader)?.ReadVolumeInfo(file.DataFork) };
    }

    // Adds a location to the diagnostics that have none.
    private sealed class LocatedDiagnostics(ICollection<Diagnostic> inner, string location) : ICollection<Diagnostic>
    {
        public int Count => inner.Count;

        public bool IsReadOnly => inner.IsReadOnly;

        public void Add(Diagnostic item) => inner.Add(item.Location is null ? item with { Location = location } : item);

        public void Clear() => inner.Clear();

        public bool Contains(Diagnostic item) => inner.Contains(item);

        public void CopyTo(Diagnostic[] array, int arrayIndex) => inner.CopyTo(array, arrayIndex);

        public bool Remove(Diagnostic item) => inner.Remove(item);

        public IEnumerator<Diagnostic> GetEnumerator() => inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // The other files the same container read, in the same folder.
    private static IEnumerable<MacFile> SiblingsOf(IReadOnlyList<MacFile> contents, MacFile file) =>
        contents.Where(f => !ReferenceEquals(f, file) && f.FolderPath.SequenceEqual(file.FolderPath));
}
