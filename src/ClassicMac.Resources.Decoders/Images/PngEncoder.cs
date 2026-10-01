using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Images
{
    /// <summary>Encodes decoded pictures as image files; <see cref="DecodeOptions.ImageEncoder"/> picks one.</summary>
    public interface IImageEncoder
    {
        /// <summary>The encoder's name, recorded in the manifest (<c>png</c>).</summary>
        string Name { get; }

        /// <summary>The files' extension, with the dot (<c>.png</c>).</summary>
        string Extension { get; }

        /// <summary>An image of <paramref name="width"/> × <paramref name="height"/> RGBA pixels (8 bits each, rows top down).</summary>
        byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba);
    }

    /// <summary>
    /// PNG (ISO/IEC 15948): 8-bit RGBA, one IDAT of unfiltered rows compressed with zlib. Plain rather than small; the
    /// pixels are exact.
    /// </summary>
    public sealed class PngEncoder : IImageEncoder
    {
        private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>The encoder.</summary>
        public static PngEncoder Instance { get; } = new();

        private PngEncoder()
        {
        }

        /// <inheritdoc/>
        public string Name => "png";

        /// <inheritdoc/>
        public string Extension => ".png";

        /// <inheritdoc/>
        public byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
            var stride = checked(width * 4);
            if (rgba.Length < (long)stride * height) throw new ArgumentException("Fewer pixels than the size says.", nameof(rgba));

            var output = new BigEndianWriter();
            output.WriteBytes(Signature);
            var header = new BigEndianWriter(13);
            header.WriteInt32(width);
            header.WriteInt32(height);
            header.WriteByte(8); // bit depth
            header.WriteByte(6); // colour type: RGBA
            header.WriteZeros(3);
            Chunk(output, "IHDR", header.WrittenSpan);

            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    for (var y = 0; y < height; y++)
                    {
                        zlib.WriteByte(0); // filter: none
                        zlib.Write(rgba.Slice(y * stride, stride));
                    }
                }
                Chunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            }
            Chunk(output, "IEND", []);
            return output.ToArray();
        }

        private static void Chunk(BigEndianWriter output, string type, ReadOnlySpan<byte> data)
        {
            output.WriteInt32(data.Length);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            output.WriteBytes(typeBytes);
            output.WriteBytes(data);
            output.WriteUInt32(Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF);
        }

        // CRC-32 (ISO 3309, reflected, polynomial $EDB88320) as the PNG specification defines it.
        private static uint Crc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
