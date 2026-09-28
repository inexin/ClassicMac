using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// Builds a UDIF image of a device: one block table (blkx 0) whose runs store the given sector ranges in the given ways,
// then the tables (in an embedded resource fork, as Disk Copy 6.5 writes them, or in an XML property list, as Mac OS X
// does), then the 'koly' trailer, with CRC-32 or MD5 checksums computed as Disk Copy computes them.
internal static class UdifBuilder
{
    public enum Run : uint { Zero = 0, Raw = 1, Free = 2, Adc = 0x80000004, Zlib = 0x80000005, Bzip2 = 0x80000006, Lzfse = 0x80000007 }

    public static byte[] Build(byte[] device, bool propertyList, bool md5, params (int Sectors, Run Type, byte[]? Stored)[] runs)
    {
        var data = new List<byte>();
        var entries = new List<(uint Type, long First, long Count, long Offset, long Length)>();
        var hash = md5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
        uint crc = 0xFFFFFFFF;
        long sector = 0;
        foreach (var (count, type, given) in runs)
        {
            var bytes = device.AsSpan((int)(sector * 512), count * 512).ToArray();
            byte[] stored = type switch
            {
                Run.Raw => bytes,
                Run.Adc => NdifBuilder.Adc(bytes),
                Run.Zlib => Zlib(bytes),
                Run.Bzip2 or Run.Lzfse => given!,
                _ => [],
            };
            entries.Add(((uint)type, sector, count, data.Count, stored.Length));
            data.AddRange(stored);
            if (type is not (Run.Zero or Run.Free))
            {
                if (hash is not null) hash.AppendData(bytes);
                else crc = Crc(crc, bytes);
            }
            sector += count;
        }
        entries.Add((0xFFFFFFFF, sector, 0, data.Count, 0));
        byte[] tableChecksum = hash is not null ? hash.GetHashAndReset() : BigEndian(~crc);

        var mish = new byte[0xCC + entries.Count * 0x28];
        "mish"u8.CopyTo(mish);
        BinaryPrimitives.WriteUInt32BigEndian(mish.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(mish.AsSpan(0x10), (ulong)sector);
        WriteChecksum(mish.AsSpan(0x40), md5, tableChecksum);
        BinaryPrimitives.WriteUInt32BigEndian(mish.AsSpan(0xC8), (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var r = mish.AsSpan(0xCC + i * 0x28);
            BinaryPrimitives.WriteUInt32BigEndian(r, entries[i].Type);
            BinaryPrimitives.WriteUInt64BigEndian(r[8..], (ulong)entries[i].First);
            BinaryPrimitives.WriteUInt64BigEndian(r[0x10..], (ulong)entries[i].Count);
            BinaryPrimitives.WriteUInt64BigEndian(r[0x18..], (ulong)entries[i].Offset);
            BinaryPrimitives.WriteUInt64BigEndian(r[0x20..], (ulong)entries[i].Length);
        }

        var dataLength = data.Count;
        var dataChecksum = md5 ? MD5.HashData(data.ToArray()) : BigEndian(~Crc(0xFFFFFFFF, data.ToArray()));
        var masterChecksum = md5 ? MD5.HashData(tableChecksum) : BigEndian(~Crc(0xFFFFFFFF, tableChecksum));
        byte[] tables;
        if (propertyList)
        {
            tables = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\">\n<dict>\n\t<key>resource-fork</key>\n\t<dict>\n"
                + "\t\t<key>blkx</key>\n\t\t<array>\n\t\t\t<dict>\n\t\t\t\t<key>Attributes</key>\n\t\t\t\t<string>0x0050</string>\n"
                + $"\t\t\t\t<key>Data</key>\n\t\t\t\t<data>\n\t\t\t\t{Convert.ToBase64String(mish, Base64FormattingOptions.InsertLineBreaks)}\n\t\t\t\t</data>\n"
                + "\t\t\t\t<key>ID</key>\n\t\t\t\t<string>0</string>\n\t\t\t\t<key>Name</key>\n\t\t\t\t<string>whole disk</string>\n"
                + "\t\t\t</dict>\n\t\t</array>\n\t</dict>\n</dict>\n</plist>\n");
        }
        else
        {
            var fork = new ResourceFork();
            fork.Add(new Resource(FourCC.FromString("blkx"), 0, mish) { Name = MacString.FromMacRoman("whole disk") });
            tables = fork.ToArray();
        }
        var tablesOffset = data.Count;
        data.AddRange(tables);

        var koly = new byte[512];
        "koly"u8.CopyTo(koly);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(8), 512);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(0xC), propertyList ? 0u : 1u);
        BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(0x20), (ulong)dataLength);
        if (!propertyList)
        {
            BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(0x28), (ulong)tablesOffset);
            BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(0x30), (ulong)tables.Length);
        }
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(0x38), 1);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(0x3C), 1);
        WriteChecksum(koly.AsSpan(0x50), md5, dataChecksum);
        if (propertyList)
        {
            BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(0xD8), (ulong)tablesOffset);
            BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(0xE0), (ulong)tables.Length);
        }
        WriteChecksum(koly.AsSpan(0x160), md5, masterChecksum);
        BinaryPrimitives.WriteUInt32BigEndian(koly.AsSpan(0x1E8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(koly.AsSpan(0x1EC), (ulong)(device.Length / 512));
        return [.. data, .. koly];
    }

    private static void WriteChecksum(Span<byte> at, bool md5, byte[] value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(at, md5 ? 4u : 2u);
        BinaryPrimitives.WriteUInt32BigEndian(at[4..], md5 ? 128u : 32u);
        value.CopyTo(at[8..]);
    }

    private static byte[] Zlib(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal)) zlib.Write(bytes);
        return output.ToArray();
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc;
    }
}
