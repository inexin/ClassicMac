using System;
using System.IO;
using System.Threading;
using ClassicMac.Core;

namespace ClassicMac.Graphics
{
    /// <summary>
    /// MacPaint documents (file type <c>PNTG</c>; <c>.pntg</c>, <c>.pnt</c>, <c>.mac</c>): a 576 × 720 1-bit image.
    /// The file is a 512-byte header (<c>u32</c> version 0, 2 or 3; for versions 2 and 3, 38 fill patterns of 8 bytes;
    /// then padding) followed by 720 rows of 72 bytes, each PackBits-compressed; a 1 bit is black. A MacBinary wrapper
    /// (a 128-byte header naming the file type <c>PNTG</c>) is skipped.
    /// </summary>
    public static class MacPaintFile
    {
        /// <summary>Width of every MacPaint image.</summary>
        public const int Width = 576;

        /// <summary>Height of every MacPaint image.</summary>
        public const int Height = 720;

        /// <summary>Size of the MacPaint header before the image rows.</summary>
        public const int HeaderSize = 512;

        private const int MacBinaryHeaderSize = 128;

        /// <summary>
        /// True if <paramref name="data"/> looks like a MacPaint document: MacBinary-wrapped with file type
        /// <c>PNTG</c>, or a header with version 0, 2 or 3 and zero padding whose first row unpacks to exactly 72 bytes.
        /// MacPaint has no magic number, so a bare header is only a strong hint; prefer the file extension or type.
        /// </summary>
        public static bool IsMacPaintFile(ReadOnlySpan<byte> data)
        {
            var reader = new BigEndianReader(data);
            if (IsMacBinary(reader)) return true;
            if (data.Length < HeaderSize + 2) return false;
            uint version = reader.ReadUInt32At(0);
            if (version != 0 && version != 2 && version != 3) return false;
            if (data.Slice(4 + 38 * 8, HeaderSize - 4 - 38 * 8).IndexOfAnyExcept((byte)0) >= 0) return false;
            reader.Position = HeaderSize;
            var rows = reader.ReadSubReader(reader.Remaining);
            return FirstRowIsWhole(ref rows);
        }

        /// <summary>Decodes the image: black on white, opaque. Rows missing from a truncated file stay white.</summary>
        /// <exception cref="NotSupportedException">The data is too short to hold a MacPaint image.</exception>
        public static RgbaBitmap Decode(ReadOnlySpan<byte> data)
        {
            return Decode(BytesReader.Over(data), CancellationToken.None);
        }

        /// <summary>
        /// Decodes the document read from the current position of <paramref name="stream"/>, decompressing its rows as
        /// they are read; the stream is read to its end and left open.
        /// </summary>
        /// <param name="stream">The document, MacBinary-wrapped or not.</param>
        /// <param name="cancellationToken">Cancels decoding before the rows are read and before they are converted.</param>
        /// <inheritdoc cref="Decode(ReadOnlySpan{byte})"/>
        public static RgbaBitmap Decode(Stream stream, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var reader = new BigEndianStreamReader(stream);
            var image = Decode(reader, cancellationToken);
            reader.SkipAtMost(long.MaxValue);
            return image;
        }

        private static RgbaBitmap Decode(BigEndianStreamReader reader, CancellationToken cancellationToken)
        {
            // A MacBinary wrapper is skipped, and the image read no further than its data fork's length.
            long length = long.MaxValue;
            Span<byte> head = stackalloc byte[MacBinaryHeaderSize];
            int peeked = reader.Peek(head);
            var header = new BigEndianReader(head[..peeked]);
            if (IsMacBinary(header))
            {
                length = header.ReadUInt32At(83);
                reader.Skip(MacBinaryHeaderSize);
            }
            if (length <= HeaderSize || reader.SkipAtMost(HeaderSize) < HeaderSize || reader.IsAtEnd)
                throw new NotSupportedException("The data is too short to be a MacPaint document.");
            cancellationToken.ThrowIfCancellationRequested();
            var image = DecodeRows(reader, length - HeaderSize, cancellationToken);
            return image ?? throw new NotSupportedException("The MacPaint document has no image data.");
        }

        // The image rows (after any header), PackBits-compressed back to back and read from at most maxInput bytes;
        // 1 = black; rows missing from the data stay white. Also QuickTime's 'PNTG' codec.
        internal static RgbaBitmap? DecodeRows(BigEndianStreamReader data, long maxInput = long.MaxValue,
            CancellationToken cancellationToken = default)
        {
            const int rowBytes = Width / 8;
            var bits = new byte[rowBytes * Height];
            long consumed = PackBits.Unpack(data, bits, maxInput);
            if (consumed == 0) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var img = new RgbaBitmap(Width, Height);
            var px = img.Pixels;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    byte v = ((bits[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1) != 0 ? (byte)0 : (byte)255;
                    int i = (y * Width + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = v;
                    px[i + 3] = 255;
                }
            return img;
        }

        // MacBinary (I/II/III): byte 0 zero, a 1-63 character name at 1, file type PNTG at 65, zero at 74 and 82.
        private static bool IsMacBinary(BigEndianReader data) =>
            data.Length >= MacBinaryHeaderSize && data.ReadByteAt(0) == 0 && data.ReadByteAt(1) is >= 1 and <= 63 &&
            data.ReadByteAt(74) == 0 && data.ReadByteAt(82) == 0 && data.ReadFourCCAt(65) == FourCC.FromString("PNTG");

        // The first PackBits row fills exactly 72 bytes (a run or literal never straddles MacPaint's rows).
        private static bool FirstRowIsWhole(ref BigEndianReader rows)
        {
            int produced = 0;
            while (produced < Width / 8)
            {
                if (!rows.TryReadByte(out byte rawFlag)) return false;
                sbyte flag = (sbyte)rawFlag;
                if (flag == -128) continue;
                if (flag < 0)
                {
                    produced += 1 - flag;
                    if (!rows.TrySkip(1)) return false;
                }
                else
                {
                    produced += flag + 1;
                    if (!rows.TrySkip(flag + 1)) return false;
                }
            }
            return produced == Width / 8;
        }
    }
}
