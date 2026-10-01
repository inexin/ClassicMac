using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ClassicMac.Core;
using ClassicMac.Graphics;

namespace ClassicMac.Graphics.QuickTime
{
    /// <summary>
    /// QuickTime image files (<c>QTIF</c>; <c>.qtif</c>, <c>.qti</c>, <c>.qif</c>): the standalone form of the images
    /// QuickTime embeds in pictures. The file is a sequence of atoms (<c>u32</c> size including the 8-byte header, a
    /// four-character type), among them <c>idsc</c> (the image description), <c>idat</c> (the compressed image) and
    /// optionally <c>iicc</c> (an ICC profile).
    /// </summary>
    /// <remarks>
    /// The image is decompressed by the codecs built into the core (<c>raw </c>, <c>rle </c>, <c>rpza</c>,
    /// <c>smc </c>, <c>cvid</c>, <c>8BPS</c>, <c>yuv2</c>, <c>YVU9</c>, <c>tga </c>, <c>PNTG</c>), or by the
    /// <see cref="IPictImageCodec"/> given (JPEG, PNG and other embedded file formats).
    /// </remarks>
    public static class QuickTimeImageFile
    {
        /// <summary>Bytes needed to recognize a QuickTime image file.</summary>
        public const int SignatureLength = 8;

        /// <summary>True if <paramref name="data"/> starts with a QuickTime image file's first atom.</summary>
        public static bool IsQuickTimeImageFile(ReadOnlySpan<byte> data)
        {
            if (data.Length < SignatureLength) return false;
            uint size = new ClassicMac.Core.BigEndianReader(data).ReadUInt32At(0);
            if (size != 1 && size < 8) return false;
            string type = Encoding.Latin1.GetString(data.Slice(4, 4));
            return type is "idsc" or "idat" or "iicc";
        }

        /// <summary>Reads the file's image description.</summary>
        /// <exception cref="NotSupportedException">The data has no readable image description.</exception>
        public static PictImageDescription ReadDescription(ReadOnlySpan<byte> data)
        {
            return TryFindAtom(data, "idsc", out int offset, out int length)
                ? Description(data.Slice(offset, length))
                : throw NoDescription();
        }

        /// <summary>The file's embedded ICC profile (<c>iicc</c> atom), or null.</summary>
        public static byte[]? ReadIccProfile(ReadOnlySpan<byte> data)
        {
            return TryFindAtom(data, "iicc", out int offset, out int length) ? data.Slice(offset, length).ToArray() : null;
        }

        /// <summary>Decodes the image.</summary>
        /// <param name="data">The file's bytes.</param>
        /// <param name="codec">Decoder for codecs the core lacks (e.g. JPEG); null for built-in codecs only.</param>
        /// <exception cref="NotSupportedException">The file has no image, or its codec is not supported.</exception>
        public static RgbaBitmap Decode(ReadOnlySpan<byte> data, IPictImageCodec? codec = null)
        {
            var description = ReadDescription(data);
            if (!TryFindAtom(data, "idat", out int offset, out int length)) throw NoImage();
            return DecodeImage(description, data.Slice(offset, length).ToArray(), codec);
        }

        /// <summary>Decodes the image read from the current position of <paramref name="stream"/>.</summary>
        /// <inheritdoc cref="Read(Stream, IPictImageCodec?, CancellationToken)"/>
        public static RgbaBitmap Decode(Stream stream, IPictImageCodec? codec = null, CancellationToken cancellationToken = default) =>
            Read(stream, codec, cancellationToken).Bitmap;

        /// <summary>
        /// Decodes the image read from the current position of <paramref name="stream"/>, with its description and ICC
        /// profile. The atoms are read in order and the stream to its end; only the first image description, image
        /// data and ICC profile are kept, other atoms are skipped. The stream is left open and need not be seekable.
        /// </summary>
        /// <param name="stream">The file's bytes.</param>
        /// <param name="codec">Decoder for codecs the core lacks (e.g. JPEG); null for built-in codecs only.</param>
        /// <param name="cancellationToken">Cancels reading between atoms.</param>
        /// <exception cref="NotSupportedException">The file has no image, or its codec is not supported.</exception>
        public static QuickTimeImageResult Read(Stream stream, IPictImageCodec? codec = null, CancellationToken cancellationToken = default)
        {
            var (description, image, profile) = Scan(stream, keepImage: true, cancellationToken);
            if (image == null) throw NoImage();
            cancellationToken.ThrowIfCancellationRequested();
            return new QuickTimeImageResult(DecodeImage(description, image, codec), description, profile);
        }

        // The description and ICC profile read from the current position of stream, without decoding the image.
        internal static (PictImageDescription Description, byte[]? IccProfile) ReadMetadata(Stream stream, CancellationToken cancellationToken = default)
        {
            var (description, _, profile) = Scan(stream, keepImage: false, cancellationToken);
            return (description, profile);
        }

        // Reads the atoms in order to the end of the stream, keeping the first description, image data (if asked) and
        // ICC profile, and skipping the rest.
        private static (PictImageDescription Description, byte[]? Image, byte[]? IccProfile) Scan(Stream stream, bool keepImage,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var reader = new BigEndianStreamReader(stream);
            byte[]? descriptionAtom = null, image = null, profile = null;
            Span<byte> header = stackalloc byte[16];
            while (reader.Peek(header[..8]) == 8)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size = reader.ReadUInt32();
                string type = Encoding.Latin1.GetString(reader.ReadBytes(4));
                int headerLength = 8;
                if (size == 1)
                {
                    if (reader.Peek(header[..8]) < 8) break;
                    size = (long)reader.ReadUInt64();
                    headerLength = 16;
                }
                else if (size == 0) size = long.MaxValue;
                if (size < headerLength) break;
                long length = size == long.MaxValue ? long.MaxValue : size - headerLength;
                switch (type)
                {
                    case "idsc" when descriptionAtom == null: descriptionAtom = ReadAtMost(reader, length); break;
                    case "idat" when keepImage && image == null: image = ReadAtMost(reader, length); break;
                    case "iicc" when profile == null: profile = ReadAtMost(reader, length); break;
                    default: reader.SkipAtMost(length); break;
                }
            }
            reader.SkipAtMost(long.MaxValue);
            return (descriptionAtom != null ? Description(descriptionAtom) : throw NoDescription(), image, profile);
        }

        // The content of an atom: length bytes, or fewer when the file ends first (an atom running past the end is cut there).
        private static byte[] ReadAtMost(BigEndianStreamReader reader, long length)
        {
            var result = new byte[(int)Math.Min(length, 81920)];
            int filled = 0;
            while (filled < length)
            {
                if (filled == result.Length)
                {
                    if (result.Length == Array.MaxLength)
                        throw new NotSupportedException("The QuickTime image file's atom is too large.");
                    Array.Resize(ref result, (int)Math.Min(Math.Min(length, Array.MaxLength), 2L * result.Length));
                }
                int read = reader.ReadAtMost(result.AsSpan(filled));
                if (read == 0) break;
                filled += read;
            }
            return filled == result.Length ? result : result[..filled];
        }

        // An image description atom's content; the description must lie within its atom.
        private static PictImageDescription Description(ReadOnlySpan<byte> atom) =>
            ImageDescriptionReader.Read(atom, 0, out _) ?? throw NoDescription();

        private static RgbaBitmap DecodeImage(PictImageDescription description, byte[] image, IPictImageCodec? codec) =>
            QuickTimeCodecs.Decode(description, image) ?? codec?.Decode(description, image)
                ?? throw new NotSupportedException($"QuickTime codec '{description.CodecType}' is not supported.");

        private static NotSupportedException NoDescription() =>
            new("Not a QuickTime image file: it has no image description ('idsc').");

        private static NotSupportedException NoImage() => new("The QuickTime image file has no image data ('idat').");

        // Finds the first top-level atom of the requested type. Size 0 runs to the end of the file, size 1 has a
        // 64-bit size after the type; an atom running past the end is cut there.
        private static bool TryFindAtom(ReadOnlySpan<byte> data, string requestedType, out int contentOffset, out int contentLength)
        {
            long p = 0;
            while (p + 8 <= data.Length)
            {
                var atom = new BigEndianReader(data.Slice((int)p, 8));
                long size = atom.ReadUInt32();
                string type = Encoding.Latin1.GetString(atom.ReadBytes(4));
                int header = 8;
                if (size == 1)
                {
                    if (p + 16 > data.Length) break;
                    size = (long)new BigEndianReader(data.Slice((int)p + 8, 8)).ReadUInt64();
                    header = 16;
                }
                else if (size == 0) size = data.Length - p;
                if (size < header) break;
                long end = Math.Min(data.Length, p + size);
                if (type == requestedType)
                {
                    contentOffset = (int)(p + header);
                    contentLength = (int)(end - p - header);
                    return true;
                }
                p += size;
            }

            contentOffset = 0;
            contentLength = 0;
            return false;
        }
    }

    /// <summary>A decoded QuickTime image file: the image, its description and its ICC profile.</summary>
    /// <param name="Bitmap">The decoded image.</param>
    /// <param name="Description">The file's image description (<c>idsc</c>).</param>
    /// <param name="IccProfile">The embedded ICC profile (<c>iicc</c>), or null.</param>
    public sealed record QuickTimeImageResult(RgbaBitmap Bitmap, PictImageDescription Description, byte[]? IccProfile);
}
