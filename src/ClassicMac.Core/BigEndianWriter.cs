using System;
using System.Buffers.Binary;

namespace ClassicMac.Core
{
    /// <summary>Writes big-endian values sequentially into a caller-owned byte span.</summary>
    /// <remarks>
    /// This stack-only writer does not copy or own the destination. Failed <c>TryWrite</c> operations do not advance
    /// <see cref="Position"/> or modify the destination. Throwing writes report insufficient capacity with
    /// <see cref="ArgumentException"/>.
    /// </remarks>
    public ref struct BigEndianWriter
    {
        private readonly Span<byte> destination;
        private int position;

        /// <summary>Creates a writer positioned at the beginning of <paramref name="destination"/>.</summary>
        public BigEndianWriter(Span<byte> destination)
        {
            this.destination = destination;
            position = 0;
        }

        /// <summary>The total capacity of the destination.</summary>
        public readonly int Length => destination.Length;

        /// <summary>The current write position; setting it outside the destination throws.</summary>
        public int Position
        {
            readonly get => position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)value, (uint)destination.Length, nameof(value));
                position = value;
            }
        }

        /// <summary>The number of bytes available to write.</summary>
        public readonly int Remaining => destination.Length - position;

        /// <summary>Writes one byte.</summary>
        public void WriteByte(byte value)
        {
            if (!TryWriteByte(value)) throw new ArgumentException("The destination does not have enough remaining space.", nameof(destination));
        }

        /// <summary>Attempts to write one byte.</summary>
        public bool TryWriteByte(byte value)
        {
            if (Remaining < 1) return false;
            destination[position++] = value;
            return true;
        }

        /// <summary>Writes a byte at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteByteAt(int offset, byte value)
        {
            EnsureRange(offset, 1);
            destination[offset] = value;
        }

        /// <summary>Attempts to write a byte at an absolute offset without changing <see cref="Position"/>.</summary>
        public bool TryWriteByteAt(int offset, byte value)
        {
            if (!IsRangeValid(offset, 1)) return false;
            destination[offset] = value;
            return true;
        }

        /// <summary>Writes a signed 16-bit integer.</summary>
        public void WriteInt16(short value)
        {
            EnsureCapacity(sizeof(short));
            BinaryPrimitives.WriteInt16BigEndian(destination[position..], value);
            position += sizeof(short);
        }

        /// <summary>Attempts to write a signed 16-bit integer.</summary>
        public bool TryWriteInt16(short value)
        {
            if (Remaining < sizeof(short)) return false;
            BinaryPrimitives.WriteInt16BigEndian(destination[position..], value);
            position += sizeof(short);
            return true;
        }

        /// <summary>Writes a signed 16-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteInt16At(int offset, short value) => BinaryPrimitives.WriteInt16BigEndian(GetSpanAt(offset, sizeof(short)), value);

        /// <summary>Attempts to write a signed 16-bit integer at an absolute offset.</summary>
        public bool TryWriteInt16At(int offset, short value)
        {
            if (!TryGetSpanAt(offset, sizeof(short), out var bytes)) return false;
            BinaryPrimitives.WriteInt16BigEndian(bytes, value);
            return true;
        }

        /// <summary>Writes an unsigned 16-bit integer.</summary>
        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(sizeof(ushort));
            BinaryPrimitives.WriteUInt16BigEndian(destination[position..], value);
            position += sizeof(ushort);
        }

        /// <summary>Attempts to write an unsigned 16-bit integer.</summary>
        public bool TryWriteUInt16(ushort value)
        {
            if (Remaining < sizeof(ushort)) return false;
            BinaryPrimitives.WriteUInt16BigEndian(destination[position..], value);
            position += sizeof(ushort);
            return true;
        }

        /// <summary>Writes an unsigned 16-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteUInt16At(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(GetSpanAt(offset, sizeof(ushort)), value);

        /// <summary>Attempts to write an unsigned 16-bit integer at an absolute offset.</summary>
        public bool TryWriteUInt16At(int offset, ushort value)
        {
            if (!TryGetSpanAt(offset, sizeof(ushort), out var bytes)) return false;
            BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            return true;
        }

        /// <summary>Writes a signed 32-bit integer.</summary>
        public void WriteInt32(int value)
        {
            EnsureCapacity(sizeof(int));
            BinaryPrimitives.WriteInt32BigEndian(destination[position..], value);
            position += sizeof(int);
        }

        /// <summary>Attempts to write a signed 32-bit integer.</summary>
        public bool TryWriteInt32(int value)
        {
            if (Remaining < sizeof(int)) return false;
            BinaryPrimitives.WriteInt32BigEndian(destination[position..], value);
            position += sizeof(int);
            return true;
        }

        /// <summary>Writes a signed 32-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteInt32At(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(GetSpanAt(offset, sizeof(int)), value);

        /// <summary>Attempts to write a signed 32-bit integer at an absolute offset.</summary>
        public bool TryWriteInt32At(int offset, int value)
        {
            if (!TryGetSpanAt(offset, sizeof(int), out var bytes)) return false;
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return true;
        }

        /// <summary>Writes an unsigned 32-bit integer.</summary>
        public void WriteUInt32(uint value)
        {
            EnsureCapacity(sizeof(uint));
            BinaryPrimitives.WriteUInt32BigEndian(destination[position..], value);
            position += sizeof(uint);
        }

        /// <summary>Attempts to write an unsigned 32-bit integer.</summary>
        public bool TryWriteUInt32(uint value)
        {
            if (Remaining < sizeof(uint)) return false;
            BinaryPrimitives.WriteUInt32BigEndian(destination[position..], value);
            position += sizeof(uint);
            return true;
        }

        /// <summary>Writes an unsigned 32-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteUInt32At(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(GetSpanAt(offset, sizeof(uint)), value);

        /// <summary>Attempts to write an unsigned 32-bit integer at an absolute offset.</summary>
        public bool TryWriteUInt32At(int offset, uint value)
        {
            if (!TryGetSpanAt(offset, sizeof(uint), out var bytes)) return false;
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return true;
        }

        /// <summary>Writes a signed 64-bit integer.</summary>
        public void WriteInt64(long value)
        {
            EnsureCapacity(sizeof(long));
            BinaryPrimitives.WriteInt64BigEndian(destination[position..], value);
            position += sizeof(long);
        }

        /// <summary>Attempts to write a signed 64-bit integer.</summary>
        public bool TryWriteInt64(long value)
        {
            if (Remaining < sizeof(long)) return false;
            BinaryPrimitives.WriteInt64BigEndian(destination[position..], value);
            position += sizeof(long);
            return true;
        }

        /// <summary>Writes a signed 64-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteInt64At(int offset, long value) => BinaryPrimitives.WriteInt64BigEndian(GetSpanAt(offset, sizeof(long)), value);

        /// <summary>Attempts to write a signed 64-bit integer at an absolute offset.</summary>
        public bool TryWriteInt64At(int offset, long value)
        {
            if (!TryGetSpanAt(offset, sizeof(long), out var bytes)) return false;
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            return true;
        }

        /// <summary>Writes an unsigned 64-bit integer.</summary>
        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(sizeof(ulong));
            BinaryPrimitives.WriteUInt64BigEndian(destination[position..], value);
            position += sizeof(ulong);
        }

        /// <summary>Attempts to write an unsigned 64-bit integer.</summary>
        public bool TryWriteUInt64(ulong value)
        {
            if (Remaining < sizeof(ulong)) return false;
            BinaryPrimitives.WriteUInt64BigEndian(destination[position..], value);
            position += sizeof(ulong);
            return true;
        }

        /// <summary>Writes an unsigned 64-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteUInt64At(int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(GetSpanAt(offset, sizeof(ulong)), value);

        /// <summary>Attempts to write an unsigned 64-bit integer at an absolute offset.</summary>
        public bool TryWriteUInt64At(int offset, ulong value)
        {
            if (!TryGetSpanAt(offset, sizeof(ulong), out var bytes)) return false;
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            return true;
        }

        /// <summary>Writes a four-character code.</summary>
        public void WriteFourCC(FourCC value) => WriteUInt32(value.Value);

        /// <summary>Attempts to write a four-character code.</summary>
        public bool TryWriteFourCC(FourCC value) => TryWriteUInt32(value.Value);

        /// <summary>Writes a four-character code at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteFourCCAt(int offset, FourCC value) => WriteUInt32At(offset, value.Value);

        /// <summary>Attempts to write a four-character code at an absolute offset.</summary>
        public bool TryWriteFourCCAt(int offset, FourCC value) => TryWriteUInt32At(offset, value.Value);

        /// <summary>Writes a QuickDraw point.</summary>
        public void WriteMacPoint(MacPoint value)
        {
            EnsureCapacity(MacPoint.Length);
            WriteInt16(value.V);
            WriteInt16(value.H);
        }

        /// <summary>Attempts to write a QuickDraw point.</summary>
        public bool TryWriteMacPoint(MacPoint value)
        {
            if (Remaining < MacPoint.Length) return false;
            WriteInt16(value.V);
            WriteInt16(value.H);
            return true;
        }

        /// <summary>Writes a QuickDraw point at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteMacPointAt(int offset, MacPoint value)
        {
            var bytes = GetSpanAt(offset, MacPoint.Length);
            value.Write(bytes);
        }

        /// <summary>Attempts to write a QuickDraw point at an absolute offset.</summary>
        public bool TryWriteMacPointAt(int offset, MacPoint value)
        {
            if (!TryGetSpanAt(offset, MacPoint.Length, out var bytes)) return false;
            value.Write(bytes);
            return true;
        }

        /// <summary>Writes a QuickDraw rectangle.</summary>
        public void WriteMacRect(MacRect value)
        {
            EnsureCapacity(MacRect.Length);
            WriteInt16(value.Top);
            WriteInt16(value.Left);
            WriteInt16(value.Bottom);
            WriteInt16(value.Right);
        }

        /// <summary>Attempts to write a QuickDraw rectangle.</summary>
        public bool TryWriteMacRect(MacRect value)
        {
            if (Remaining < MacRect.Length) return false;
            WriteInt16(value.Top);
            WriteInt16(value.Left);
            WriteInt16(value.Bottom);
            WriteInt16(value.Right);
            return true;
        }

        /// <summary>Writes a QuickDraw rectangle at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteMacRectAt(int offset, MacRect value)
        {
            var bytes = GetSpanAt(offset, MacRect.Length);
            value.Write(bytes);
        }

        /// <summary>Attempts to write a QuickDraw rectangle at an absolute offset.</summary>
        public bool TryWriteMacRectAt(int offset, MacRect value)
        {
            if (!TryGetSpanAt(offset, MacRect.Length, out var bytes)) return false;
            value.Write(bytes);
            return true;
        }

        /// <summary>Writes a signed 16.16 fixed-point value.</summary>
        public void WriteFixed(Fixed value) => WriteInt32(value.Raw);

        /// <summary>Attempts to write a signed 16.16 fixed-point value.</summary>
        public bool TryWriteFixed(Fixed value) => TryWriteInt32(value.Raw);

        /// <summary>Writes a signed 16.16 fixed-point value at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteFixedAt(int offset, Fixed value) => WriteInt32At(offset, value.Raw);

        /// <summary>Attempts to write a signed 16.16 fixed-point value at an absolute offset.</summary>
        public bool TryWriteFixedAt(int offset, Fixed value) => TryWriteInt32At(offset, value.Raw);

        /// <summary>Writes an unsigned 16.16 fixed-point value.</summary>
        public void WriteUnsignedFixed(UnsignedFixed value) => WriteUInt32(value.Raw);

        /// <summary>Attempts to write an unsigned 16.16 fixed-point value.</summary>
        public bool TryWriteUnsignedFixed(UnsignedFixed value) => TryWriteUInt32(value.Raw);

        /// <summary>Writes an unsigned 16.16 fixed-point value at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteUnsignedFixedAt(int offset, UnsignedFixed value) => WriteUInt32At(offset, value.Raw);

        /// <summary>Attempts to write an unsigned 16.16 fixed-point value at an absolute offset.</summary>
        public bool TryWriteUnsignedFixedAt(int offset, UnsignedFixed value) => TryWriteUInt32At(offset, value.Raw);

        /// <summary>Copies <paramref name="value"/> into the destination.</summary>
        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            EnsureCapacity(value.Length);
            value.CopyTo(destination[position..]);
            position += value.Length;
        }

        /// <summary>Attempts to copy <paramref name="value"/> into the destination.</summary>
        public bool TryWriteBytes(ReadOnlySpan<byte> value)
        {
            if (value.Length > Remaining) return false;
            value.CopyTo(destination[position..]);
            position += value.Length;
            return true;
        }

        /// <summary>Copies bytes at an absolute offset without changing <see cref="Position"/>.</summary>
        public void WriteBytesAt(int offset, ReadOnlySpan<byte> value)
        {
            var target = GetSpanAt(offset, value.Length);
            value.CopyTo(target);
        }

        /// <summary>Attempts to copy bytes at an absolute offset without changing <see cref="Position"/>.</summary>
        public bool TryWriteBytesAt(int offset, ReadOnlySpan<byte> value)
        {
            if (!TryGetSpanAt(offset, value.Length, out var target)) return false;
            value.CopyTo(target);
            return true;
        }

        /// <summary>Advances past <paramref name="length"/> bytes, leaving them unchanged.</summary>
        public void Skip(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            EnsureCapacity(length);
            position += length;
        }

        /// <summary>Attempts to advance past <paramref name="length"/> bytes, leaving them unchanged.</summary>
        public bool TrySkip(int length)
        {
            if (length < 0 || length > Remaining) return false;
            position += length;
            return true;
        }

        private void EnsureCapacity(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (length > Remaining) throw new ArgumentException("The destination does not have enough remaining space.", nameof(destination));
        }

        private readonly bool IsRangeValid(int offset, int length) =>
            offset >= 0 && length >= 0 && offset <= destination.Length && length <= destination.Length - offset;

        private readonly Span<byte> GetSpanAt(int offset, int length)
        {
            EnsureRange(offset, length);
            return destination.Slice(offset, length);
        }

        private readonly bool TryGetSpanAt(int offset, int length, out Span<byte> value)
        {
            if (!IsRangeValid(offset, length)) { value = default; return false; }
            value = destination.Slice(offset, length);
            return true;
        }

        private readonly void EnsureRange(int offset, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (!IsRangeValid(offset, length)) throw new ArgumentException("The destination range is outside its capacity.", nameof(destination));
        }

    }
}
