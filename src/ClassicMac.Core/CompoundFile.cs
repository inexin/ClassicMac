using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClassicMac.Core;

/// <summary>What a compound file's directory entry is ([MS-CFB] §2.6.1).</summary>
public enum CompoundFileEntryType
{
    /// <summary>A storage: a folder of entries.</summary>
    Storage = 1,

    /// <summary>A stream: a file of bytes.</summary>
    Stream = 2,

    /// <summary>The root storage, which also holds the mini stream.</summary>
    Root = 5,
}

/// <summary>One entry of a compound file's directory.</summary>
/// <param name="Id">Its index in the directory.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its storages' names and its own, '/'-separated, from the root (empty for the root).</param>
/// <param name="Type">Storage, stream or root.</param>
/// <param name="StartSector">The first sector (or mini sector, for a small stream) of its data.</param>
/// <param name="Size">Its data's length in bytes.</param>
public sealed record CompoundFileEntry(int Id, string Name, string Path, CompoundFileEntryType Type, uint StartSector, long Size);

/// <summary>
/// A Microsoft compound file (OLE2 structured storage, [MS-CFB]): a little-endian file system in a file, holding Word
/// 6 to 2003 documents among others. Reads the directory and the streams, as
/// <c>docs/formats/containers/compound-file.md</c> says. Damaged chains and directory entries are reported and cut;
/// a header that is not a compound file's throws <see cref="InvalidDataException"/>.
/// </summary>
public sealed class CompoundFile
{
    private const uint MaxRegularSector = 0xFFFFFFFA, EndOfChain = 0xFFFFFFFE, FatSectorMark = 0xFFFFFFFD, NoStream = 0xFFFFFFFF;
    private const int HeaderSize = 512, MiniSectorSize = 64, EntrySize = 128;

    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly ReadOnlyMemory<byte> data;
    private readonly ICollection<Diagnostic> diagnostics;
    private readonly uint[] fat;
    private readonly uint[] miniFat;
    private readonly uint miniStreamCutoff;
    private readonly List<CompoundFileEntry> entries = [];
    private ReadOnlyMemory<byte> miniStream;

    private CompoundFile(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
    {
        this.data = data;
        this.diagnostics = diagnostics;
        var header = data.Span;
        MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[0x1A..]);
        var shift = BinaryPrimitives.ReadUInt16LittleEndian(header[0x1E..]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[0x1C..]) != 0xFFFE || !(MajorVersion == 3 && shift == 9 || MajorVersion == 4 && shift == 12)
            || BinaryPrimitives.ReadUInt16LittleEndian(header[0x20..]) != 6)
        {
            throw new InvalidDataException("Not a compound file: its byte order, version or sector size is not [MS-CFB]'s.");
        }

        SectorSize = 1 << shift;
        miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[0x38..]);
        fat = ReadFat(header);
        miniFat = ReadMiniFat(header);
        ReadDirectory(BinaryPrimitives.ReadUInt32LittleEndian(header[0x30..]));
    }

    /// <summary>3 (512-byte sectors) or 4 (4096-byte sectors).</summary>
    public int MajorVersion { get; }

    /// <summary>The sector size in bytes.</summary>
    public int SectorSize { get; }

    /// <summary>How many FAT sectors the header and DIFAT sectors list.</summary>
    public int FatSectorCount { get; private set; }

    /// <summary>The directory's entries in tree order: the root first, each storage before its contents.</summary>
    public IReadOnlyList<CompoundFileEntry> Entries => entries;

    /// <summary>Whether <paramref name="data"/> starts with the compound file signature ([MS-CFB] §2.2).</summary>
    public static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    /// <summary>Reads a compound file's header, FAT and directory; problems go to <paramref name="diagnostics"/>.</summary>
    /// <exception cref="InvalidDataException">The data is no compound file.</exception>
    public static CompoundFile Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic>? diagnostics = null)
    {
        if (data.Length < HeaderSize || !IsCompoundFile(data.Span))
        {
            throw new InvalidDataException("Not a compound file: no [MS-CFB] signature.");
        }

        return new CompoundFile(data, diagnostics ?? new List<Diagnostic>());
    }

    /// <summary>The entry at <paramref name="path"/> ('/'-separated, names compared ignoring case), or null.</summary>
    public CompoundFileEntry? Find(string path) =>
        entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) && e.Type != CompoundFileEntryType.Root);

    /// <summary>
    /// A stream's bytes: from the mini stream when it is smaller than the cutoff, else from sectors. A chain that ends
    /// early, loops or leaves the file is cut (reported) and the rest is zeros.
    /// </summary>
    public ReadOnlyMemory<byte> ReadStream(CompoundFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Size == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        return entry.Type == CompoundFileEntryType.Stream && entry.Size < miniStreamCutoff
            ? Chain(miniFat, entry.StartSector, entry.Size, MiniSectorSize, MiniSector, entry.Path)
            : Chain(fat, entry.StartSector, entry.Size, SectorSize, Sector, entry.Path);
    }

    private long SectorOffset(uint sector) => ((long)sector + 1) * SectorSize;

    private ReadOnlyMemory<byte> Sector(uint sector) =>
        sector > MaxRegularSector || SectorOffset(sector) + SectorSize > data.Length ? ReadOnlyMemory<byte>.Empty : data.Slice((int)SectorOffset(sector), SectorSize);

    private ReadOnlyMemory<byte> MiniSector(uint sector) =>
        (long)(sector + 1) * MiniSectorSize > miniStream.Length ? ReadOnlyMemory<byte>.Empty : miniStream.Slice((int)sector * MiniSectorSize, MiniSectorSize);

    private void Report(string code, string message) => diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, code, message));

    // Follows a chain of (mini) sectors for size bytes; cuts it where a sector is missing, out of the table or seen before.
    private ReadOnlyMemory<byte> Chain(uint[] table, uint start, long size, int sectorSize, Func<uint, ReadOnlyMemory<byte>> read, string what)
    {
        if (size > data.Length * 64L)
        {
            Report("cfb.bad-chain", $"Stream \"{what}\" says it is {size} bytes, far more than the file; left out.");
            return ReadOnlyMemory<byte>.Empty;
        }

        var result = new byte[size];
        var seen = new HashSet<uint>();
        var sector = start;
        for (long at = 0; at < size; at += sectorSize)
        {
            var bytes = sector < table.Length && seen.Add(sector) ? read(sector) : ReadOnlyMemory<byte>.Empty;
            if (bytes.IsEmpty)
            {
                Report("cfb.bad-chain", $"Stream \"{what}\" breaks off after {at} of its {size} bytes (sector {sector}); the rest is zeros.");
                break;
            }

            bytes.Span[..(int)Math.Min(sectorSize, size - at)].CopyTo(result.AsSpan((int)at));
            sector = table[sector];
        }

        return result;
    }

    // The FAT: the sectors the header's 109 DIFAT entries list, then those the chain of DIFAT sectors lists ([MS-CFB] §2.5).
    private uint[] ReadFat(ReadOnlySpan<byte> header)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[0x2C..]);
        var sectors = new List<uint>();
        for (var i = 0; i < 109 && sectors.Count < count; i++)
        {
            sectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(header[(0x4C + 4 * i)..]));
        }

        var difat = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        var perSector = SectorSize / 4;
        var visited = new HashSet<uint>();
        while (sectors.Count < count && difat <= MaxRegularSector && visited.Add(difat))
        {
            var sector = Sector(difat).Span;
            if (sector.IsEmpty)
            {
                break;
            }

            for (var i = 0; i < perSector - 1 && sectors.Count < count; i++)
            {
                sectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(4 * i)..]));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector[(4 * (perSector - 1))..]);
        }

        if (sectors.Count < count)
        {
            Report("cfb.bad-fat", $"The header says {count} FAT sectors; {sectors.Count} are listed.");
        }

        FatSectorCount = sectors.Count;
        var table = new uint[sectors.Count * perSector];
        for (var f = 0; f < sectors.Count; f++)
        {
            var sector = Sector(sectors[f]).Span;
            for (var i = 0; i < perSector && !sector.IsEmpty; i++)
            {
                table[f * perSector + i] = BinaryPrimitives.ReadUInt32LittleEndian(sector[(4 * i)..]);
            }

            if (sector.IsEmpty)
            {
                table.AsSpan(f * perSector, perSector).Fill(EndOfChain);
            }
        }

        // Each FAT sector is marked as one in the FAT ([MS-CFB] §2.3).
        if (sectors.Any(s => s >= table.Length || table[s] != FatSectorMark))
        {
            Report("cfb.bad-fat", "A sector the DIFAT lists as a FAT sector is not marked as one in the FAT.");
        }

        return table;
    }

    private uint[] ReadMiniFat(ReadOnlySpan<byte> header)
    {
        var start = BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[0x40..]);
        if (count == 0 || start > MaxRegularSector)
        {
            return [];
        }

        var bytes = Chain(fat, start, (long)count * SectorSize, SectorSize, Sector, "the mini FAT").Span;
        var table = new uint[bytes.Length / 4];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * i)..]);
        }

        return table;
    }

    // The directory: 128-byte entries in a chain of sectors; each storage's children are a red-black tree of siblings
    // under its child pointer ([MS-CFB] §2.6). The tree is walked in order of the sibling links, not checked for balance.
    private void ReadDirectory(uint start)
    {
        var directory = new List<byte[]>();
        var seen = new HashSet<uint>();
        for (var sector = start; sector <= MaxRegularSector && sector < fat.Length && seen.Add(sector); sector = fat[sector])
        {
            var bytes = Sector(sector);
            if (bytes.IsEmpty)
            {
                Report("cfb.bad-directory", $"The directory's sector {sector} is past the end of the file.");
                break;
            }

            for (var i = 0; i < SectorSize / EntrySize; i++)
            {
                directory.Add(bytes.Slice(i * EntrySize, EntrySize).ToArray());
            }
        }

        if (directory.Count == 0)
        {
            throw new InvalidDataException("The compound file has no directory.");
        }

        var visited = new HashSet<uint>();
        CompoundFileEntry Entry(uint id, string parent)
        {
            var raw = directory[(int)id];
            var nameLength = Math.Clamp(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x40)), (ushort)2, (ushort)64);
            var name = Encoding.Unicode.GetString(raw, 0, nameLength - 2);
            var type = (CompoundFileEntryType)raw[0x42];
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(0x78));
            if (MajorVersion == 3)
            {
                size &= 0xFFFFFFFF;                                     // the high half is not used in version 3 ([MS-CFB] §2.6.1)
            }

            var path = parent.Length == 0 ? (type == CompoundFileEntryType.Root ? "" : name) : parent + "/" + name;
            return new CompoundFileEntry((int)id, name, path, type, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x74)), size);
        }

        bool Valid(uint id)
        {
            if (id == NoStream)
            {
                return false;
            }

            if (id >= directory.Count || !visited.Add(id))
            {
                Report("cfb.bad-directory", $"A directory entry points to entry {id}, which is past the directory or already seen; left out.");
                return false;
            }

            return true;
        }

        void Walk(uint id, string parent)
        {
            // In order: left subtree, the entry and its contents, right subtree.
            var raw = directory[(int)id];
            var left = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x44));
            var right = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x48));
            var child = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0x4C));
            if (Valid(left))
            {
                Walk(left, parent);
            }

            var entry = Entry(id, parent);
            if (entry.Type is CompoundFileEntryType.Storage or CompoundFileEntryType.Stream)
            {
                entries.Add(entry);
                if (entry.Type == CompoundFileEntryType.Storage && Valid(child))
                {
                    Walk(child, entry.Path);
                }
            }

            if (Valid(right))
            {
                Walk(right, parent);
            }
        }

        visited.Add(0);
        var root = Entry(0, "");
        if (root.Type != CompoundFileEntryType.Root)
        {
            throw new InvalidDataException("The compound file's first directory entry is not its root.");
        }

        entries.Add(root);
        miniStream = root.StartSector <= MaxRegularSector && root.Size > 0
            ? Chain(fat, root.StartSector, root.Size, SectorSize, Sector, "the mini stream")
            : ReadOnlyMemory<byte>.Empty;
        var first = BinaryPrimitives.ReadUInt32LittleEndian(directory[0].AsSpan(0x4C));
        if (Valid(first))
        {
            Walk(first, "");
        }
    }
}
