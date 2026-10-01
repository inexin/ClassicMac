using System;
using System.IO;

namespace ClassicMac.Core
{
    /// <summary>Writes big endian values and byte spans to a stream.</summary>
    /// <remarks>
    /// This writer does not own or close the stream. <see cref="BytesWritten"/> counts bytes written through this
    /// instance, starting at zero, and does not depend on the stream being seekable.
    /// </remarks>
    public sealed class BigEndianStreamWriter
    {
        private readonly Stream stream;

        /// <summary>Creates a writer for a writable stream.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="ArgumentException">The stream does not support writing.</exception>
        public BigEndianStreamWriter(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanWrite) throw new ArgumentException("The stream must support writing.", nameof(stream));
            this.stream = stream;
        }

        /// <summary>The number of bytes successfully written through this writer.</summary>
        public long BytesWritten { get; private set; }

        /// <summary>Writes one byte.</summary>
        public void WriteByte(byte value)
        {
            Span<byte> bytes = stackalloc byte[1];
            var writer = new BigEndianWriter(bytes);
            writer.WriteByte(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes a signed 16-bit integer in big endian order.</summary>
        public void WriteInt16(short value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            var writer = new BigEndianWriter(bytes);
            writer.WriteInt16(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes an unsigned 16-bit integer in big endian order.</summary>
        public void WriteUInt16(ushort value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ushort)];
            var writer = new BigEndianWriter(bytes);
            writer.WriteUInt16(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes a signed 32-bit integer in big endian order.</summary>
        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            var writer = new BigEndianWriter(bytes);
            writer.WriteInt32(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes an unsigned 32-bit integer in big endian order.</summary>
        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            var writer = new BigEndianWriter(bytes);
            writer.WriteUInt32(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes a signed 64-bit integer in big endian order.</summary>
        public void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            var writer = new BigEndianWriter(bytes);
            writer.WriteInt64(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes an unsigned 64-bit integer in big endian order.</summary>
        public void WriteUInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            var writer = new BigEndianWriter(bytes);
            writer.WriteUInt64(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes a four-character code.</summary>
        public void WriteFourCC(FourCC value) => WriteUInt32(value.Value);

        /// <summary>Writes a QuickDraw point.</summary>
        public void WriteMacPoint(MacPoint value)
        {
            Span<byte> bytes = stackalloc byte[MacPoint.Length];
            var writer = new BigEndianWriter(bytes);
            writer.WriteMacPoint(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes a QuickDraw rectangle.</summary>
        public void WriteMacRect(MacRect value)
        {
            Span<byte> bytes = stackalloc byte[MacRect.Length];
            var writer = new BigEndianWriter(bytes);
            writer.WriteMacRect(value);
            WriteBytes(bytes);
        }

        /// <summary>Writes a signed 16.16 fixed-point value.</summary>
        public void WriteFixed(Fixed value) => WriteInt32(value.Raw);

        /// <summary>Writes an unsigned 16.16 fixed-point value.</summary>
        public void WriteUnsignedFixed(UnsignedFixed value) => WriteUInt32(value.Raw);

        /// <summary>Writes bytes verbatim.</summary>
        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            stream.Write(value);
            BytesWritten += value.Length;
        }

        /// <summary>Flushes the underlying stream.</summary>
        public void Flush() => stream.Flush();
    }
}
