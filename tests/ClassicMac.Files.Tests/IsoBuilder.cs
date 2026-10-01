using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Files.Tests;

// Builds a small ISO 9660 (or High Sierra) image: descriptors from sector 16, a big-endian path table, one sector per
// directory, file data after them. Records are added in order with raw names, flags, dates and system-use bytes, so
// tests can lay out exactly what the Mac's plug-ins meet.
internal sealed class IsoBuilder(bool highSierra = false)
{
    private const int Sector = 2048;

    public sealed record Rec(string Name, byte[] Data, byte Flags = 0, byte[]? SystemUse = null, byte[]? Date = null,
        byte AttributeBlocks = 0, byte Unit = 0, byte Gap = 0, string? Folder = null, int? At = null);

    // The absolute sector of the image's first sector (a later session's start): every block number is counted from
    // the disc's start, as on a multisession disc. A record with At points at data already there.
    public int Origin { get; init; }

    private readonly List<(string Path, List<Rec> Records)> directories = [("", [])];

    public static readonly byte[] DefaultDate = [99, 7, 20, 14, 12, 52, 0];

    public void Folder(string path) =>
        directories.Add((path, [])); // its record goes into the parent when built

    public void Add(string folder, Rec record) => directories.Single(d => d.Path == folder).Records.Add(record);

    public byte[] Build()
    {
        // Layout: 16 system sectors, descriptor, terminator, path table, directories, then data.
        var sectors = new List<byte[]>();
        for (var i = 0; i < 16; i++) sectors.Add(new byte[Sector]);
        var descriptor = new byte[Sector];
        var terminator = new byte[Sector];
        var pathTable = new byte[Sector];
        sectors.AddRange([descriptor, terminator, pathTable]);
        var directorySectors = new Dictionary<string, int>();
        foreach (var (path, _) in directories)
        {
            directorySectors[path] = sectors.Count;
            sectors.Add(new byte[Sector]);
        }

        // File data: each record's bytes (with extended attribute blocks before, and interleave gaps).
        var dataSectors = new Dictionary<Rec, int>(ReferenceEqualityComparer.Instance);
        foreach (var record in directories.SelectMany(d => d.Records))
        {
            if (record.At is { } absolute)
            {
                dataSectors[record] = absolute - Origin; // data already on the disc (an earlier session)
                continue;
            }
            dataSectors[record] = sectors.Count;
            for (var i = 0; i < record.AttributeBlocks; i++) sectors.Add(new byte[Sector]);
            var at = 0;
            do
            {
                var units = record.Unit == 0 ? Math.Max(1, (record.Data.Length + Sector - 1) / Sector) : record.Unit;
                for (var u = 0; u < units; u++)
                {
                    var sector = new byte[Sector];
                    if (at < record.Data.Length) record.Data.AsSpan(at, Math.Min(Sector, record.Data.Length - at)).CopyTo(sector);
                    at += Sector;
                    sectors.Add(sector);
                }
                for (var g = 0; g < record.Gap && at < record.Data.Length; g++) sectors.Add(new byte[Sector]);
            }
            while (record.Unit != 0 && at < record.Data.Length);
        }

        // Directories: ".", "..", subfolders' records in the parent, then the records.
        foreach (var (path, records) in directories)
        {
            var bytes = new List<byte>();
            var self = directorySectors[path];
            var parent = path.Contains('/') ? directorySectors[path[..path.LastIndexOf('/')]] : directorySectors[""];
            bytes.AddRange(Record("\0", self, Sector, 2, null, DefaultDate, 0, 0, 0));
            bytes.AddRange(Record("\u0001", parent, Sector, 2, null, DefaultDate, 0, 0, 0));
            foreach (var record in records)
            {
                bytes.AddRange(Record(record.Name, dataSectors[record], record.Data.Length, record.Flags, record.SystemUse,
                    record.Date ?? DefaultDate, record.AttributeBlocks, record.Unit, record.Gap));
            }
            foreach (var (sub, _) in directories.Where(d => d.Path.Length > 0 && (d.Path.Contains('/') ? d.Path[..d.Path.LastIndexOf('/')] : "") == path))
            {
                var name = sub.Contains('/') ? sub[(sub.LastIndexOf('/') + 1)..] : sub;
                var hidden = name.StartsWith('.');
                bytes.AddRange(Record(hidden ? name[1..] : name, directorySectors[sub], Sector, (byte)(2 | (hidden ? 1 : 0)), null, DefaultDate, 0, 0, 0));
            }
            bytes.ToArray().CopyTo(sectors[directorySectors[path]], 0);
        }

        // Descriptor and path table (root entry only; the plug-ins read nothing else from it).
        BinaryPrimitives.WriteUInt32BigEndian(pathTable.AsSpan(highSierra ? 0 : 2), (uint)(directorySectors[""] + Origin));
        Record("\0",directorySectors[""], Sector, 2, null, DefaultDate, 0, 0, 0).CopyTo(descriptor, highSierra ? 180 : 156);
        if (highSierra)
        {
            descriptor[8] = 1;
            "CDROM"u8.CopyTo(descriptor.AsSpan(9));
            descriptor[14] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(138), Sector);
            BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(164), (uint)(18 + Origin));
        }
        else
        {
            descriptor[0] = 1;
            "CD001"u8.CopyTo(descriptor.AsSpan(1));
            descriptor[6] = 1;
            Encoding.ASCII.GetBytes("TEST DISC".PadRight(32)).CopyTo(descriptor, 40);
            BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(130), Sector);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(0x84), 10);
            BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x88), 10);
            BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(148), (uint)(18 + Origin));
            descriptor[0x371] = 1;
        }
        terminator[0] = 0xFF;
        "CD001"u8.CopyTo(terminator.AsSpan(1));
        return sectors.SelectMany(s => s).ToArray();
    }

    private byte[] Record(string name, int extent, int size, byte flags, byte[]? systemUse, byte[] date, byte xar, byte unit, byte gap)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        var pad = (nameBytes.Length & 1) == 0 ? 1 : 0;
        var length = 33 + nameBytes.Length + pad + (systemUse?.Length ?? 0);
        length += length & 1;
        var r = new byte[length];
        r[0] = (byte)length;
        r[1] = xar;
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(2), extent + Origin);
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(6), extent + Origin);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(10), size);
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(14), size);
        date.AsSpan(0, highSierra ? 6 : 7).CopyTo(r.AsSpan(18));
        r[highSierra ? 24 : 25] = flags;
        r[26] = unit;
        r[27] = gap;
        r[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(r, 33);
        systemUse?.CopyTo(r, 33 + nameBytes.Length + pad);
        return r;
    }

    // Apple's 'AA' system-use entry (version 2): type, creator, Finder flags.
    public static byte[] AA(string type, string creator, ushort flags) =>
        [(byte)'A', (byte)'A', 14, 2, .. Encoding.ASCII.GetBytes(type), .. Encoding.ASCII.GetBytes(creator), (byte)(flags >> 8), (byte)flags];

    // The old 'BA' form at the start of the field: kind, type, creator, Finder flags.
    public static byte[] BA(byte kind, string type, string creator, ushort flags) =>
        [(byte)'B', (byte)'A', kind, .. Encoding.ASCII.GetBytes(type), .. Encoding.ASCII.GetBytes(creator), (byte)(flags >> 8), (byte)flags, 0];
}
