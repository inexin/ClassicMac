using System;
using System.Buffers.Binary;
using System.IO;

namespace ClassicMac.Core
{
    /// <summary>Writes big-endian values into a buffer that grows as needed, like a <see cref="System.Text.StringBuilder"/> for bytes.</summary>
    /// <remarks>
    /// <c>Write…</c> appends. <c>Write…At</c> overwrites bytes already written, for a length or offset that is only known
    /// later: write a placeholder, then patch it.
    /// </remarks>
    public sealed class BigEndianWriter
    {
        private byte[] buffer;
        private int length;

        /// <summary>Creates an empty writer.</summary>
        /// <param name="capacity">The bytes to reserve; the buffer grows past it as needed.</param>
        public BigEndianWriter(int capacity = 256)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(capacity);
            buffer = capacity == 0 ? [] : new byte[capacity];
        }

        /// <summary>The number of bytes written.</summary>
        public int Length => length;

        /// <summary>The bytes written, without copying; valid until the next write.</summary>
        public ReadOnlyMemory<byte> WrittenMemory => buffer.AsMemory(0, length);

        /// <summary>The bytes written, without copying; valid until the next write.</summary>
        public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, length);

        /// <summary>A copy of the bytes written.</summary>
        public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();

        /// <summary>Writes the bytes written so far to <paramref name="stream"/>, which is left open.</summary>
        public void WriteTo(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            stream.Write(buffer, 0, length);
        }

        /// <summary>Discards the bytes written, keeping the buffer for reuse.</summary>
        public void Clear() => length = 0;

        /// <summary>Appends one byte.</summary>
        public void WriteByte(byte value) => Append(1)[0] = value;

        /// <summary>Appends a signed 16-bit integer.</summary>
        public void WriteInt16(short value) => BinaryPrimitives.WriteInt16BigEndian(Append(sizeof(short)), value);

        /// <summary>Appends an unsigned 16-bit integer.</summary>
        public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Append(sizeof(ushort)), value);

        /// <summary>Appends a signed 32-bit integer.</summary>
        public void WriteInt32(int value) => BinaryPrimitives.WriteInt32BigEndian(Append(sizeof(int)), value);

        /// <summary>Appends an unsigned 32-bit integer.</summary>
        public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Append(sizeof(uint)), value);

        /// <summary>Appends a signed 64-bit integer.</summary>
        public void WriteInt64(long value) => BinaryPrimitives.WriteInt64BigEndian(Append(sizeof(long)), value);

        /// <summary>Appends an unsigned 64-bit integer.</summary>
        public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Append(sizeof(ulong)), value);

        /// <summary>Appends a four-character code.</summary>
        public void WriteFourCC(FourCC value) => WriteUInt32(value.Value);

        /// <summary>Appends a QuickDraw point (v, h).</summary>
        public void WriteMacPoint(MacPoint value)
        {
            WriteInt16(value.V);
            WriteInt16(value.H);
        }

        /// <summary>Appends a QuickDraw rectangle (top, left, bottom, right).</summary>
        public void WriteMacRect(MacRect value)
        {
            WriteInt16(value.Top);
            WriteInt16(value.Left);
            WriteInt16(value.Bottom);
            WriteInt16(value.Right);
        }

        /// <summary>Appends a signed 16.16 fixed-point value.</summary>
        public void WriteFixed(Fixed value) => WriteInt32(value.Raw);

        /// <summary>Appends an unsigned 16.16 fixed-point value.</summary>
        public void WriteUnsignedFixed(UnsignedFixed value) => WriteUInt32(value.Raw);

        /// <summary>Appends bytes verbatim.</summary>
        public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Append(value.Length));

        /// <summary>Appends <paramref name="count"/> zero bytes (padding, reserved fields, placeholders).</summary>
        public void WriteZeros(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            Append(count).Clear();
        }

        /// <summary>Overwrites one byte already written.</summary>
        public void WriteByteAt(int offset, byte value) => At(offset, 1)[0] = value;

        /// <summary>Overwrites a signed 16-bit integer already written.</summary>
        public void WriteInt16At(int offset, short value) => BinaryPrimitives.WriteInt16BigEndian(At(offset, sizeof(short)), value);

        /// <summary>Overwrites an unsigned 16-bit integer already written.</summary>
        public void WriteUInt16At(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(At(offset, sizeof(ushort)), value);

        /// <summary>Overwrites a signed 32-bit integer already written.</summary>
        public void WriteInt32At(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(At(offset, sizeof(int)), value);

        /// <summary>Overwrites an unsigned 32-bit integer already written.</summary>
        public void WriteUInt32At(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(At(offset, sizeof(uint)), value);

        /// <summary>Overwrites bytes already written.</summary>
        public void WriteBytesAt(int offset, ReadOnlySpan<byte> value) => value.CopyTo(At(offset, value.Length));

        // The next count bytes, growing the buffer, and counts them as written.
        private Span<byte> Append(int count)
        {
            if (buffer.Length - length < count)
                Array.Resize(ref buffer, (int)Math.Min(Array.MaxLength, Math.Max((long)length + count, Math.Max(256, 2L * buffer.Length))));
            var span = buffer.AsSpan(length, count);
            length += count;
            return span;
        }

        private Span<byte> At(int offset, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset > length - count) throw new ArgumentOutOfRangeException(nameof(offset), "Only bytes already written can be overwritten.");
            return buffer.AsSpan(offset, count);
        }
    }
}
