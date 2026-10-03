using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Core.Tests;

// Compound files built byte by byte as [MS-CFB] §2 lays them out (docs/formats/containers/compound-file.md): the header,
// FAT and DIFAT sectors, the directory, the mini FAT and mini stream, and the streams. Small streams go in the mini
// stream; storages list their children as a chain of right siblings.
internal sealed class CompoundFileBuilder
{
    private const uint Free = 0xFFFFFFFF, End = 0xFFFFFFFE, FatSector = 0xFFFFFFFD, DifatSector = 0xFFFFFFFC, NoStream = 0xFFFFFFFF;

    private readonly List<(string Path, byte[] Data)> streams = [];
    private readonly List<string> storages = [];

    /// <summary>3 (512-byte sectors) or 4 (4096-byte sectors).</summary>
    public int Version { get; set; } = 3;

    /// <summary>FAT sectors beyond those needed, to make the header's 109 DIFAT slots overflow into DIFAT sectors.</summary>
    public int ExtraFatSectors { get; set; }

    /// <summary>Changes the built bytes before they are returned (to damage them).</summary>
    public Action<byte[], Layout>? Damage { get; set; }

    public CompoundFileBuilder Stream(string path, byte[] data)
    {
        streams.Add((path, data));
        return this;
    }

    public CompoundFileBuilder Storage(string path)
    {
        storages.Add(path);
        return this;
    }

    // Where things went, for tests that damage them.
    public sealed record Layout(int SectorSize, int FirstDirectorySector, Dictionary<string, int> FirstSectors, List<int> FatSectors);

    public byte[] Build()
    {
        var size = Version == 4 ? 4096 : 512;
        var perSector = size / 4;

        // The directory: the root, then storages and streams in order.
        var entries = new List<(string Path, byte Type, byte[]? Data)> { ("", 5, null) };
        entries.AddRange(storages.Select(s => (s, (byte)1, (byte[]?)null)));
        entries.AddRange(streams.Select(s => (s.Path, (byte)2, (byte[]?)s.Data)));

        // The mini stream: the small streams, each in 64-byte mini sectors.
        var mini = new List<byte>();
        var miniFat = new List<uint>();
        var miniStart = new Dictionary<string, int>();
        foreach (var (path, data) in streams.Where(s => s.Data.Length < 4096))
        {
            var count = (data.Length + 63) / 64;
            if (count == 0)
            {
                continue;
            }

            miniStart[path] = miniFat.Count;
            for (var i = 0; i < count; i++)
            {
                miniFat.Add(i + 1 < count ? (uint)(miniFat.Count + 1) : End);
            }

            mini.AddRange(data);
            mini.AddRange(new byte[count * 64 - data.Length]);
        }

        int Sectors(int bytes) => (bytes + size - 1) / size;
        var directorySectors = Sectors(entries.Count * 128);
        var miniFatSectors = Sectors(miniFat.Count * 4);
        var miniStreamSectors = Sectors(mini.Count);
        var big = streams.Where(s => s.Data.Length >= 4096).ToList();
        var dataSectors = directorySectors + miniFatSectors + miniStreamSectors + big.Sum(b => Sectors(b.Data.Length));

        // FAT and DIFAT sectors: enough FAT entries for every sector, the FAT and DIFAT sectors included.
        int fat = 1, difat = 0;
        while (true)
        {
            var total = fat + difat + dataSectors;
            var neededFat = Math.Max((total + perSector - 1) / perSector, 1) + ExtraFatSectors;
            var neededDifat = neededFat > 109 ? (neededFat - 109 + perSector - 2) / (perSector - 1) : 0;
            if (neededFat == fat && neededDifat == difat)
            {
                break;
            }

            (fat, difat) = (neededFat, neededDifat);
        }

        var totalSectors = fat + difat + dataSectors;
        var file = new byte[size + totalSectors * size];
        var next = 0;
        var fatTable = new uint[fat * perSector];
        Array.Fill(fatTable, Free);
        var fatSectors = Enumerable.Range(next, fat).ToList();
        next += fat;
        foreach (var s in fatSectors)
        {
            fatTable[s] = FatSector;
        }

        var difatSectors = Enumerable.Range(next, difat).ToList();
        next += difat;
        foreach (var s in difatSectors)
        {
            fatTable[s] = DifatSector;
        }

        int Chain(int count)
        {
            var first = next;
            for (var i = 0; i < count; i++)
            {
                fatTable[next + i] = i + 1 < count ? (uint)(next + i + 1) : End;
            }

            next += count;
            return count == 0 ? unchecked((int)End) : first;
        }

        Span<byte> SectorSpan(int sector) => file.AsSpan(size + sector * size, size);

        var directoryStart = Chain(directorySectors);
        var miniFatStart = Chain(miniFatSectors);
        var miniStreamStart = Chain(miniStreamSectors);
        var bigStart = big.ToDictionary(b => b.Path, b => Chain(Sectors(b.Data.Length)));

        // Contents.
        void Write(int start, ReadOnlySpan<byte> data)
        {
            for (var i = 0; i * size < data.Length; i++)
            {
                data.Slice(i * size, Math.Min(size, data.Length - i * size)).CopyTo(SectorSpan(start + i));
            }
        }

        var miniFatBytes = new byte[miniFatSectors * size];
        miniFatBytes.AsSpan().Fill(0xFF);
        for (var i = 0; i < miniFat.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(4 * i), miniFat[i]);
        }

        Write(miniFatStart, miniFatBytes);
        Write(miniStreamStart, mini.ToArray());
        foreach (var (path, data) in big)
        {
            Write(bigStart[path], data);
        }

        // The directory entries.
        var directory = new byte[directorySectors * size];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = directory.AsSpan(128 * i, 128);
            var (path, type, data) = entries[i];
            var name = i == 0 ? "Root Entry" : path.Split('/')[^1];
            Encoding.Unicode.GetBytes(name).CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)((name.Length + 1) * 2));
            entry[0x42] = type;
            entry[0x43] = 1;                                            // black
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x44..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x48..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], NoStream);
            if (i == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry[0x74..], mini.Count == 0 ? End : (uint)miniStreamStart);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[0x78..], (ulong)mini.Count);
            }
            else if (type == 2)
            {
                var start = data!.Length >= 4096 ? (uint)bigStart[path] : miniStart.TryGetValue(path, out var m) ? (uint)m : End;
                BinaryPrimitives.WriteUInt32LittleEndian(entry[0x74..], start);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[0x78..], (ulong)data.Length);
            }
        }

        // Children of each storage: the first is the child, each next one the previous one's right sibling.
        string ParentOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        foreach (var group in Enumerable.Range(1, entries.Count - 1).GroupBy(i => ParentOf(entries[i].Path)))
        {
            var children = group.ToList();
            var parentIndex = group.Key == "" ? 0 : entries.FindIndex(e => e.Path == group.Key);
            BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(128 * parentIndex + 0x4C), (uint)children[0]);
            for (var k = 0; k + 1 < children.Count; k++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(128 * children[k] + 0x48), (uint)children[k + 1]);
            }
        }

        Write(directoryStart, directory);

        // The FAT.
        for (var f = 0; f < fat; f++)
        {
            var sector = SectorSpan(fatSectors[f]);
            for (var j = 0; j < perSector; j++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(sector[(4 * j)..], fatTable[f * perSector + j]);
            }
        }

        // The DIFAT: the header's 109 slots, then DIFAT sectors, each ending with the next one's number.
        var header = file.AsSpan(0, 512);
        header.Slice(0x4C, 436).Fill(0xFF);
        for (var f = 0; f < Math.Min(fat, 109); f++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header[(0x4C + 4 * f)..], (uint)fatSectors[f]);
        }

        for (var d = 0; d < difat; d++)
        {
            var sector = SectorSpan(difatSectors[d]);
            sector.Fill(0xFF);
            for (var j = 0; j < perSector - 1; j++)
            {
                var f = 109 + d * (perSector - 1) + j;
                if (f < fat)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(sector[(4 * j)..], (uint)fatSectors[f]);
                }
            }

            BinaryPrimitives.WriteUInt32LittleEndian(sector[(4 * (perSector - 1))..], d + 1 < difat ? (uint)difatSectors[d + 1] : End);
        }

        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x18..], 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1A..], (ushort)Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1C..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x1E..], (ushort)(Version == 4 ? 12 : 9));
        BinaryPrimitives.WriteUInt16LittleEndian(header[0x20..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x28..], Version == 4 ? (uint)directorySectors : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x2C..], (uint)fat);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x30..], (uint)directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x38..], 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x3C..], miniFatSectors == 0 ? End : (uint)miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x40..], (uint)miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x44..], difat == 0 ? End : (uint)difatSectors[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0x48..], (uint)difat);

        var firstSectors = bigStart.ToDictionary(p => p.Key, p => p.Value);
        Damage?.Invoke(file, new Layout(size, directoryStart, firstSectors, fatSectors));
        return file;
    }
}
