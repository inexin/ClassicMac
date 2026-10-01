using System;
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
        public static bool IsQuickTimeImageFile(ReadOnlyMemory<byte> data)
        {
            if (data.Length < SignatureLength) return false;
            uint size = new BigEndianReader(data).ReadUInt32At(0);
            if (size != 1 && size < 8) return false;
            string type = Encoding.Latin1.GetString(data.Span.Slice(4, 4));
            return type is "idsc" or "idat" or "iicc";
        }

        /// <summary>Reads the file's image description.</summary>
        /// <exception cref="NotSupportedException">The data has no readable image description.</exception>
        public static PictImageDescription ReadDescription(ReadOnlyMemory<byte> data) =>
            TryFindAtom(data, "idsc", out var atom) ? Description(atom) : throw NoDescription();

        /// <summary>The file's embedded ICC profile (<c>iicc</c> atom), or null.</summary>
        public static byte[]? ReadIccProfile(ReadOnlyMemory<byte> data) =>
            TryFindAtom(data, "iicc", out var atom) ? atom.ToArray() : null;

        /// <summary>Decodes the image.</summary>
        /// <param name="data">The file's bytes.</param>
        /// <param name="codec">Decoder for codecs the core lacks (e.g. JPEG); null for built-in codecs only.</param>
        /// <exception cref="NotSupportedException">The file has no image, or its codec is not supported.</exception>
        public static RgbaBitmap Decode(ReadOnlyMemory<byte> data, IPictImageCodec? codec = null) => Read(data, codec).Bitmap;

        /// <summary>Decodes the image read from the current position of <paramref name="stream"/>.</summary>
        /// <inheritdoc cref="Read(Stream, IPictImageCodec?, CancellationToken)"/>
        public static RgbaBitmap Decode(Stream stream, IPictImageCodec? codec = null, CancellationToken cancellationToken = default) =>
            Read(stream, codec, cancellationToken).Bitmap;

        /// <summary>
        /// Decodes the image read from the current position of <paramref name="stream"/> to its end, with its
        /// description and ICC profile. The stream is left open.
        /// </summary>
        /// <param name="stream">The file's bytes.</param>
        /// <param name="codec">Decoder for codecs the core lacks (e.g. JPEG); null for built-in codecs only.</param>
        /// <param name="cancellationToken">Cancels decoding once the file is read.</param>
        /// <exception cref="NotSupportedException">The file has no image, or its codec is not supported.</exception>
        public static QuickTimeImageResult Read(Stream stream, IPictImageCodec? codec = null, CancellationToken cancellationToken = default)
        {
            var data = new BigEndianReader(stream).Source;
            cancellationToken.ThrowIfCancellationRequested();
            return Read(data, codec);
        }

        // The description and ICC profile read from the current position of stream to its end, without decoding the image.
        internal static (PictImageDescription Description, byte[]? IccProfile) ReadMetadata(Stream stream)
        {
            var data = new BigEndianReader(stream).Source;
            return (ReadDescription(data), ReadIccProfile(data));
        }

        private static QuickTimeImageResult Read(ReadOnlyMemory<byte> data, IPictImageCodec? codec)
        {
            var description = ReadDescription(data);
            if (!TryFindAtom(data, "idat", out var atom)) throw NoImage();
            var image = atom.ToArray();
            var bitmap = QuickTimeCodecs.Decode(description, image) ?? codec?.Decode(description, image)
                ?? throw new NotSupportedException($"QuickTime codec '{description.CodecType}' is not supported.");
            return new QuickTimeImageResult(bitmap, description, ReadIccProfile(data));
        }

        // An image description atom's content; the description must lie within its atom.
        private static PictImageDescription Description(ReadOnlyMemory<byte> atom) =>
            ImageDescriptionReader.Read(atom, 0, out _) ?? throw NoDescription();

        private static NotSupportedException NoDescription() =>
            new("Not a QuickTime image file: it has no image description ('idsc').");

        private static NotSupportedException NoImage() => new("The QuickTime image file has no image data ('idat').");

        // Finds the content of the first top-level atom of the requested type. Size 0 runs to the end of the file, size 1
        // has a 64-bit size after the type; an atom running past the end is cut there, and one whose size is less than
        // its header ends the search.
        private static bool TryFindAtom(ReadOnlyMemory<byte> data, string requestedType, out ReadOnlyMemory<byte> content)
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
                    content = data[(int)(p + header)..(int)end];
                    return true;
                }
                p += size;
            }
            content = default;
            return false;
        }
    }

    /// <summary>A decoded QuickTime image file: the image, its description and its ICC profile.</summary>
    /// <param name="Bitmap">The decoded image.</param>
    /// <param name="Description">The file's image description (<c>idsc</c>).</param>
    /// <param name="IccProfile">The embedded ICC profile (<c>iicc</c>), or null.</param>
    public sealed record QuickTimeImageResult(RgbaBitmap Bitmap, PictImageDescription Description, byte[]? IccProfile);
}
