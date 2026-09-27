using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Files.Tests;

// Builds container files byte by byte, following each format's description, so tests don't depend on a writer.
internal static class Fixtures
{
    public const uint AppleSingleMagic = 0x00051600;
    public const uint AppleDoubleMagic = 0x00051607;

    // 32 bytes: FInfo (type, creator, flags, location v/h, folder) then FXInfo.
    public static byte[] FinderInfo(string type, string creator, ushort flags = 0, short v = 0, short h = 0)
    {
        var bytes = new byte[32];
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 0);
        Encoding.ASCII.GetBytes(creator).CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), flags);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(10), v);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(12), h);
        bytes[16] = 0xFE; // a recognisable FXInfo byte
        return bytes;
    }

    public static byte[] AppleSingle(
        uint magic, uint version, string homeFileSystem, params (uint Id, byte[] Data)[] entries)
    {
        var header = 26 + entries.Length * 12;
        var output = new MemoryStream();
        var buffer = new byte[header];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, magic);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), version);
        Encoding.ASCII.GetBytes(homeFileSystem.PadRight(16, version == 0x00010000 ? ' ' : '\0')).CopyTo(buffer, 8);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(24), (ushort)entries.Length);
        var offset = header;
        for (var i = 0; i < entries.Length; i++)
        {
            var at = 26 + i * 12;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(at), entries[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(at + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(at + 8), (uint)entries[i].Data.Length);
            offset += entries[i].Data.Length;
        }
        output.Write(buffer);
        foreach (var entry in entries) output.Write(entry.Data);
        return output.ToArray();
    }

    // A MacBinary file: 128-byte header, secondary header, data and resource forks each padded to 128.
    public static byte[] MacBinary(
        int version, string name, byte[] data, byte[] resource, string type = "TEXT", string creator = "ttxt",
        ushort flags = 0, uint created = 0, uint modified = 0, int secondaryLength = 0)
    {
        var header = new byte[128];
        header[1] = (byte)name.Length;
        Encoding.ASCII.GetBytes(name).CopyTo(header, 2);
        Encoding.ASCII.GetBytes(type).CopyTo(header, 65);
        Encoding.ASCII.GetBytes(creator).CopyTo(header, 69);
        header[73] = (byte)(flags >> 8);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(75), 30); // v
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(77), 40); // h
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(83), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(87), (uint)resource.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(91), created);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(95), modified);
        if (version >= 2)
        {
            header[101] = (byte)flags;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(120), (ushort)secondaryLength);
            header[122] = (byte)(version == 3 ? 130 : 129);
            header[123] = 129;
            if (version == 3)
            {
                Encoding.ASCII.GetBytes("mBIN").CopyTo(header, 102);
                header[106] = 7; // fdScript
                header[107] = 0x80; // fdXFlags
            }
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(124), Crc16.Compute(header.AsSpan(0, 124)));
        }
        var output = new MemoryStream();
        output.Write(header);
        output.Write(new byte[Pad(secondaryLength)]);
        output.Write(data);
        output.Write(new byte[Pad(data.Length) - data.Length]);
        output.Write(resource);
        output.Write(new byte[Pad(resource.Length) - resource.Length]);
        return output.ToArray();
    }

    private static int Pad(int length) => (length + 127) / 128 * 128;

    public static byte[] Int32s(params int[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    public static byte[] UInt32s(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }
}
