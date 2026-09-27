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
