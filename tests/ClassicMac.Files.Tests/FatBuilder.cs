using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Files.Tests;

// Builds a FAT volume byte by byte (after the user's qdharness\fatimg.py): boot sector, two FATs, root directory (FAT12
// and FAT16: fixed; FAT32: a cluster chain), 512-byte clusters. Folders and files by path with explicit 8.3 names,
// optional VFAT long names, and optionally fragmented data (a free cluster between each of a file's clusters).
internal sealed class FatBuilder
{
    private const int Sector = 512;

    private readonly int bits;
    private readonly int reserved, rootEntries, totalSectors, fatSectors;
    private readonly Dictionary<string, List<byte[]>> directories = new() { [""] = [] };
    private readonly Dictionary<string, uint> directoryClusters = [];
    private readonly List<(uint Cluster, byte[] Data)> clusterData = [];
    private readonly Dictionary<uint, uint> fat = [];
    private uint next;

    public FatBuilder(int bits)
    {
        this.bits = bits;
        (reserved, rootEntries, totalSectors) = bits switch
        {
            12 => (1, 224, 2880),
            16 => (1, 512, 8192),
            _ => (32, 0, 70000),
        };
        var clusters = totalSectors; // an upper bound, enough to size the FAT
        fatSectors = (int)Math.Ceiling((clusters + 2) * (bits == 12 ? 1.5 : bits / 8.0) / Sector);
        next = 2;
        if (bits == 32)
        {
            directoryClusters[""] = Allocate(new byte[Sector * 4], fragmented: false); // the root folder
        }
    }

    public static readonly DateTime DefaultTime = new(2020, 1, 2, 3, 4, 6);

    public void Folder(string path, string shortName, string? longName = null, byte attributes = 0x10)
    {
        directories[path] = [];
        var parent = Parent(path);
        // Directory clusters are allocated in Build, once their size is known; remember the entry to fill in.
        directories[parent].AddRange(Entries(shortName, longName, attributes, 0xFFFFFFFF, 0, DefaultTime, DefaultTime, pendingFolder: path));
    }

    public void File(string path, string shortName, byte[] data, string? longName = null, byte attributes = 0x20,
        DateTime? created = null, DateTime? modified = null, bool fragmented = false)
    {
        var cluster = data.Length == 0 ? 0 : Allocate(data, fragmented);
        directories[Parent(path)].AddRange(Entries(shortName, longName, attributes, cluster, (uint)data.Length,
            created ?? DefaultTime, modified ?? created ?? DefaultTime));
    }

    public byte[] Build()
    {
        // Folders: allocate each one's clusters, then point its entry in the parent at them.
        foreach (var path in directories.Keys.Where(p => p.Length > 0).OrderBy(p => p.Count(c => c == '/')))
        {
            var size = (directories[path].Count + 2) * 32;
            directoryClusters[path] = Allocate(new byte[Math.Max(size, Sector)], fragmented: false);
        }
        foreach (var (path, entries) in directories)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (pending.TryGetValue(entries[i], out var folder))
                {
                    SetCluster(entries[i], directoryClusters[folder]);
                }
            }
            if (path.Length > 0)
            {
                entries.Insert(0, Entry(".          "u8.ToArray(), 0x10, directoryClusters[path], 0, DefaultTime, DefaultTime));
                entries.Insert(1, Entry("..         "u8.ToArray(), 0x10, directoryClusters.GetValueOrDefault(Parent(path)), 0, DefaultTime, DefaultTime));
            }
        }

        var image = new byte[totalSectors * Sector];
        var boot = image.AsSpan();
        boot[0] = 0xEB;
        boot[1] = 0x3C;
        boot[2] = 0x90;
        "MSDOS5.0"u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[11..], Sector);
        boot[13] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[14..], (ushort)reserved);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(boot[17..], (ushort)rootEntries);
        if (totalSectors < 0x10000)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(boot[19..], (ushort)totalSectors);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(boot[32..], (uint)totalSectors);
        }

        boot[21] = 0xF0;
        if (bits == 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(boot[36..], (uint)fatSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(boot[44..], directoryClusters[""]);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(boot[22..], (ushort)fatSectors);
        }

        boot[510] = 0x55;
        boot[511] = 0xAA;

        // The allocation tables.
        var table = new byte[fatSectors * Sector];
        void Set(uint cluster, uint value)
        {
            switch (bits)
            {
                case 12:
                    var at = (int)(cluster * 3 / 2);
                    if ((cluster & 1) == 0)
                    {
                        table[at] = (byte)value;
                        table[at + 1] = (byte)((table[at + 1] & 0xF0u) | (value >> 8 & 0x0Fu));
                    }
                    else
                    {
                        table[at] = (byte)((table[at] & 0x0Fu) | (value << 4 & 0xF0u));
                        table[at + 1] = (byte)(value >> 4);
                    }
                    break;
                case 16:
                    BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan((int)cluster * 2), (ushort)value);
                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan((int)cluster * 4), value);
                    break;
            }
        }
        Set(0, bits == 12 ? 0xFF0u : bits == 16 ? 0xFFF0u : 0x0FFFFFF0u);
        Set(1, bits == 12 ? 0xFFFu : bits == 16 ? 0xFFFFu : 0x0FFFFFFFu);
        foreach (var (cluster, value) in fat)
        {
            Set(cluster, value == uint.MaxValue ? (bits == 12 ? 0xFFFu : bits == 16 ? 0xFFFFu : 0x0FFFFFFFu) : value);
        }

        for (var copy = 0; copy < 2; copy++)
        {
            table.CopyTo(image, (reserved + copy * fatSectors) * Sector);
        }

        var rootOffset = (reserved + 2 * fatSectors) * Sector;
        var dataOffset = rootOffset + rootEntries * 32;
        foreach (var (cluster, data) in clusterData)
        {
            data.CopyTo(image, dataOffset + (cluster - 2) * Sector);
        }

        // Directory contents.
        foreach (var (path, entries) in directories)
        {
            var bytes = entries.SelectMany(e => e).ToArray();
            if (path.Length == 0 && bits != 32)
            {
                bytes.CopyTo(image, rootOffset);
            }
            else
            {
                bytes.CopyTo(image, dataOffset + (directoryClusters[path] - 2) * Sector);
            }
        }
        return image;
    }

    // Offset of the first data cluster, for tests that patch the table or clusters.
    public int DataOffset => (reserved + 2 * fatSectors) * Sector + rootEntries * 32;

    public int FatOffset => reserved * Sector;

    private readonly Dictionary<byte[], string> pending = new(ReferenceEqualityComparer.Instance);

    private uint Allocate(byte[] data, bool fragmented)
    {
        var count = Math.Max(1, (data.Length + Sector - 1) / Sector);
        var first = next;
        for (var i = 0; i < count; i++)
        {
            var cluster = next;
            next += fragmented ? 2u : 1u;
            var chunk = new byte[Sector];
            data.AsSpan(i * Sector, Math.Min(Sector, data.Length - i * Sector)).CopyTo(chunk);
            clusterData.Add((cluster, chunk));
            fat[cluster] = i + 1 < count ? next : uint.MaxValue;
        }
        return first;
    }

    private IEnumerable<byte[]> Entries(string shortName, string? longName, byte attributes, uint cluster, uint size,
        DateTime created, DateTime modified, string? pendingFolder = null)
    {
        var name = ShortName(shortName);
        var entries = new List<byte[]>();
        if (longName is not null)
        {
            byte sum = 0;
            foreach (var b in name)
            {
                sum = (byte)(((sum & 1) << 7) + (sum >> 1) + b);
            }

            var chars = longName.ToCharArray().Append('\0').ToList();
            while (chars.Count % 13 != 0)
            {
                chars.Add('￿');
            }

            var parts = chars.Count / 13;
            for (var p = parts; p >= 1; p--)
            {
                var e = new byte[32];
                e[0] = (byte)(p | (p == parts ? 0x40 : 0));
                e[11] = 0x0F;
                e[13] = sum;
                var slots = new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
                for (var i = 0; i < 13; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(slots[i]), chars[(p - 1) * 13 + i]);
                }

                entries.Add(e);
            }
        }
        var entry = Entry(name, attributes, cluster, size, created, modified);
        if (pendingFolder is not null)
        {
            pending[entry] = pendingFolder;
        }

        entries.Add(entry);
        return entries;
    }

    private byte[] Entry(byte[] name, byte attributes, uint cluster, uint size, DateTime created, DateTime modified)
    {
        var e = new byte[32];
        name.CopyTo(e, 0);
        e[11] = attributes;
        var (ct, cd) = Dos(created);
        var (mt, md) = Dos(modified);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(14), ct);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(16), cd);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(22), mt);
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(24), md);
        SetCluster(e, cluster);
        BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(28), size);
        return e;
    }

    private void SetCluster(byte[] e, uint cluster)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(26), (ushort)cluster);
        if (bits == 32)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(20), (ushort)(cluster >> 16));
        }
    }

    private static (ushort Time, ushort Date) Dos(DateTime t) =>
        ((ushort)(t.Hour << 11 | t.Minute << 5 | t.Second / 2), (ushort)((t.Year - 1980) << 9 | t.Month << 5 | t.Day));

    private static byte[] ShortName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        var extension = dot < 0 ? "" : name[(dot + 1)..];
        return Encoding.Latin1.GetBytes(stem.PadRight(8) + extension.PadRight(3)); // one byte per character, as stored
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
}
