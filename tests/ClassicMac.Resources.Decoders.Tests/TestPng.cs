using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ClassicMac.Resources.Decoders.Tests;

// Reads back the PNGs the decoders write (8-bit RGBA, filter 0), checking signature, chunk CRCs and layout.
internal static class TestPng
{
    public static (int Width, int Height, byte[] Rgba) Read(ReadOnlySpan<byte> png)
    {
        Assert.True(png[..8].SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]), "PNG signature");
        var at = 8;
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        while (true)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png[at..]);
            var type = Encoding.ASCII.GetString(png.Slice(at + 4, 4));
            var data = png.Slice(at + 8, length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png[(at + 8 + length)..]);
            Assert.Equal(Crc(png.Slice(at + 4, 4 + length)), crc);
            at += 12 + length;
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    Assert.Equal((8, 6), (data[8], data[9]));
                    break;
                case "IDAT":
                    idat.Write(data);
                    break;
                case "IEND":
                    idat.Position = 0;
                    using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
                    using (var raw = new MemoryStream())
                    {
                        zlib.CopyTo(raw);
                        var rows = raw.ToArray();
                        var rgba = new byte[width * height * 4];
                        for (var y = 0; y < height; y++)
                        {
                            Assert.Equal(0, rows[y * (width * 4 + 1)]);
                            rows.AsSpan(y * (width * 4 + 1) + 1, width * 4).CopyTo(rgba.AsSpan(y * width * 4));
                        }
                        return (width, height, rgba);
                    }
            }
        }
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
