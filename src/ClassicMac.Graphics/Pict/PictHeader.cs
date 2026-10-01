using System;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickTime;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Graphics.Pict
{
    /// <summary>
    /// Recognizes QuickDraw pictures. PICT has no magic number: a picture starts with <c>u16 picSize</c> and
    /// <c>Rect picFrame</c>, then the version opcode, which is <c>0x1101</c> (v1) or <c>0x0011 0x02FF</c> (v2,
    /// always followed by the <c>0x0C00</c> header opcode). A <c>.pict</c> file puts a 512-byte application header
    /// before the picture; a <c>PICT</c> resource does not.
    /// </summary>
    public static class PictHeader
    {
        /// <summary>Size of the application header that precedes the picture in a <c>.pict</c> file.</summary>
        public const int FileHeaderSize = 512;

        /// <summary>Bytes of a bare picture needed to recognize it: picSize, picFrame and the v2 version + header opcodes.</summary>
        public const int SignatureLength = 16;

        /// <summary>True if <paramref name="data"/> starts with a bare picture (as stored in a <c>PICT</c> resource).</summary>
        public static bool IsPicture(ReadOnlySpan<byte> data)
        {
            var reader = new ClassicMac.Core.BigEndianReader(data);
            return IsPicture(ref reader);
        }

        /// <summary>Tests for a bare picture at the reader's current position without advancing it.</summary>
        public static bool IsPicture(ref ClassicMac.Core.BigEndianReader reader)
        {
            int start = reader.Position;
            if (reader.Remaining < 12) return false;
            short top = reader.ReadInt16At(start + 2), left = reader.ReadInt16At(start + 4),
                bottom = reader.ReadInt16At(start + 6), right = reader.ReadInt16At(start + 8);
            if (bottom <= top || right <= left) return false;
            if (IsVersion1(reader, start)) return true;
            return reader.Remaining >= SignatureLength
                && reader.ReadUInt16At(start + 10) == 0x0011 && reader.ReadUInt16At(start + 12) == 0x02FF
                && reader.ReadUInt16At(start + 14) == 0x0C00;
        }

        /// <summary>True if <paramref name="data"/> starts with a <c>.pict</c> file: a 512-byte header, then a picture.</summary>
        public static bool IsPictFile(ReadOnlySpan<byte> data)
        {
            if (data.Length < FileHeaderSize + 12) return false;
            var picture = data.Slice(FileHeaderSize);
            if (!IsPicture(picture)) return false;
            // v1's two-byte version opcode is a weak signature at an arbitrary offset, so for v1 also require the
            // conventional all-zero application header.
            var reader = new BigEndianReader(data);
            return !IsVersion1(reader, FileHeaderSize) || data.Slice(0, FileHeaderSize).IndexOfAnyExcept((byte)0) < 0;
        }

        /// <summary>
        /// Reads the header of the picture at the current position of <paramref name="stream"/> (bare picture or
        /// <c>.pict</c> file) without decoding it, reading no further than the header: a seekable stream is left just
        /// after it; a non-seekable one may have been read up to 526 bytes ahead while looking for the header. Comments
        /// and the ICC profile are only collected by <see cref="PictReader"/>. The stream is left open.
        /// </summary>
        /// <exception cref="EndOfStreamException">The stream ends inside the picture header.</exception>
        /// <exception cref="NotSupportedException">The data does not start with a PICT version opcode.</exception>
        public static PictInfo ReadInfo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var reader = new BigEndianStreamReader(stream);
            var info = Parse(reader, out _);
            reader.ReturnLookahead();
            return info;
        }

        // Parses picSize, picFrame (skipping a 512-byte file header), the version opcode(s) and, for version 2, the
        // HeaderOp (0x0C00) if present, leaving the reader at the first drawing opcode.
        // - Extended version 2 (header version -2): hRes/vRes are the picture's resolution and a non-empty srcRect is
        //   the coordinate space its opcodes draw in (Listing A-5; Executor DrawPicture). Version -1 carries a Fixed
        //   bounding box instead and draws in picFrame at 72 dpi (Listing A-6).
        internal static PictInfo Parse(BigEndianStreamReader b, out bool version1)
        {
            // A .pict file's 512-byte application header is usually zero, but some creators (MacDraw: "DRWG...",
            // MacDraft: "pictDF...") fill it; skip it whenever the picture's version opcode is found after it
            // rather than at the start.
            Span<byte> head = stackalloc byte[FileHeaderSize + 14];
            if (!HasVersionOpcode(head[..b.Peek(head[..14])], 0) && HasVersionOpcode(head[..b.Peek(head)], FileHeaderSize))
                b.Skip(FileHeaderSize);
            b.ReadUInt16();                                            // picSize: unreliable in v2
            var frame = PictRect.Read(b);

            ushort versionOp = b.ReadUInt16();
            if (versionOp == 0x1101)                                  // 0x11 VersionOp, 0x01: 1-byte opcodes follow
            {
                version1 = true;
                return new PictInfo(1, false, frame, frame, 72, 72);
            }
            if (versionOp != 0x0011)
                throw new NotSupportedException($"Unexpected PICT version opcode 0x{versionOp:X4}");

            version1 = false;
            b.ReadUInt16();                                            // Version (0x02FF)
            Span<byte> headerOp = stackalloc byte[2 + 24];
            if (b.Peek(headerOp) < headerOp.Length || new BigEndianReader(headerOp).ReadUInt16() != 0x0C00)
                return new PictInfo(2, false, frame, frame, 72, 72);   // no HeaderOp: that word is the first opcode

            b.ReadUInt16();                                            // HeaderOp
            short headerVersion = b.ReadInt16();
            b.ReadUInt16();                                            // reserved
            int hRes = b.ReadInt32(), vRes = b.ReadInt32();           // Fixed 16.16
            var srcRect = PictRect.Read(b);
            b.ReadUInt32();                                            // reserved
            if (headerVersion != -2)
                return new PictInfo(2, false, frame, frame, 72, 72);
            return new PictInfo(2, true, frame, srcRect.IsEmpty ? frame : srcRect,
                hRes > 0 ? hRes / 65536.0 : 72, vRes > 0 ? vRes / 65536.0 : 72);
        }

        // True if the picture starting at offset has a v1 (0x1101) or v2 (0x0011 0x02FF) version opcode after picSize
        // and picFrame.
        private static bool HasVersionOpcode(ReadOnlySpan<byte> data, int offset)
        {
            if (data.Length - offset < 14) return false;
            var reader = new BigEndianReader(data);
            ushort op = reader.ReadUInt16At(offset + 10);
            return op == 0x1101 || (op == 0x0011 && reader.ReadUInt16At(offset + 12) == 0x02FF);
        }

        private static bool IsVersion1(BigEndianReader reader, int offset) =>
            reader.ReadByteAt(offset + 10) == 0x11 && reader.ReadByteAt(offset + 11) == 0x01;
    }
}
