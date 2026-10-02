using System;
using System.Buffers.Binary;
using System.IO;

namespace ClassicMac.Core
{
    /// <summary>Reads big-endian values from bytes, sequentially or at absolute offsets.</summary>
    /// <remarks>
    /// The reader does not copy the bytes it is given. Failed <c>TryRead</c> operations do not advance
    /// <see cref="Position"/>. Throwing reads report truncated input with <see cref="EndOfStreamException"/>.
    /// </remarks>
    public sealed class BigEndianReader
    {
        private readonly ReadOnlyMemory<byte> source;
        private int position;

        /// <summary>Creates a reader positioned at the beginning of <paramref name="source"/>.</summary>
        public BigEndianReader(ReadOnlyMemory<byte> source)
        {
            this.source = source;
        }

        /// <summary>
        /// Creates a reader over the bytes from the current position of <paramref name="stream"/> to its end, which are
        /// read now. The stream is left open.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public BigEndianReader(Stream stream) : this(ReadToEnd(stream))
        {
        }

        /// <summary>All the bytes the reader reads from.</summary>
        public ReadOnlyMemory<byte> Source => source;

        /// <summary>The total number of bytes in the source.</summary>
        public int Length => source.Length;

        /// <summary>The current read position; setting it outside the source throws.</summary>
        public int Position
        {
            get => position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)value, (uint)source.Length, nameof(value));
                position = value;
            }
        }

        /// <summary>The number of unread bytes.</summary>
        public int Remaining => source.Length - position;

        /// <summary>Reads one byte.</summary>
        public byte ReadByte() => ReadBytes(1)[0];

        /// <summary>Attempts to read one byte.</summary>
        public bool TryReadByte(out byte value)
        {
            if (Remaining < 1)
            {
                value = default;
                return false;
            }
            value = source.Span[position++];
            return true;
        }

        /// <summary>Reads a byte at an absolute offset without changing <see cref="Position"/>.</summary>
        public byte ReadByteAt(int offset) => ReadBytesAt(offset, 1)[0];

        /// <summary>Attempts to read a byte at an absolute offset without changing <see cref="Position"/>.</summary>
        public bool TryReadByteAt(int offset, out byte value)
        {
            if (!TryReadBytesAt(offset, 1, out var bytes))
            {
                value = default;
                return false;
            }
            value = bytes[0];
            return true;
        }

        /// <summary>Reads a signed 16-bit integer.</summary>
        public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(ReadBytes(sizeof(short)));

        /// <summary>Attempts to read a signed 16-bit integer.</summary>
        public bool TryReadInt16(out short value)
        {
            if (Remaining < sizeof(short))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadInt16BigEndian(source.Span[position..]);
            position += sizeof(short);
            return true;
        }

        /// <summary>Reads a signed 16-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public short ReadInt16At(int offset) => BinaryPrimitives.ReadInt16BigEndian(ReadBytesAt(offset, sizeof(short)));

        /// <summary>Attempts to read a signed 16-bit integer at an absolute offset.</summary>
        public bool TryReadInt16At(int offset, out short value)
        {
            if (!TryReadBytesAt(offset, sizeof(short), out var bytes))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadInt16BigEndian(bytes);
            return true;
        }

        /// <summary>Reads an unsigned 16-bit integer.</summary>
        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(sizeof(ushort)));

        /// <summary>Attempts to read an unsigned 16-bit integer.</summary>
        public bool TryReadUInt16(out ushort value)
        {
            if (Remaining < sizeof(ushort))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadUInt16BigEndian(source.Span[position..]);
            position += sizeof(ushort);
            return true;
        }

        /// <summary>Reads an unsigned 16-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public ushort ReadUInt16At(int offset) => BinaryPrimitives.ReadUInt16BigEndian(ReadBytesAt(offset, sizeof(ushort)));

        /// <summary>Attempts to read an unsigned 16-bit integer at an absolute offset.</summary>
        public bool TryReadUInt16At(int offset, out ushort value)
        {
            if (!TryReadBytesAt(offset, sizeof(ushort), out var bytes))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadUInt16BigEndian(bytes);
            return true;
        }

        /// <summary>Reads a signed 32-bit integer.</summary>
        public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(ReadBytes(sizeof(int)));

        /// <summary>Attempts to read a signed 32-bit integer.</summary>
        public bool TryReadInt32(out int value)
        {
            if (Remaining < sizeof(int))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadInt32BigEndian(source.Span[position..]);
            position += sizeof(int);
            return true;
        }

        /// <summary>Reads a signed 32-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public int ReadInt32At(int offset) => BinaryPrimitives.ReadInt32BigEndian(ReadBytesAt(offset, sizeof(int)));

        /// <summary>Attempts to read a signed 32-bit integer at an absolute offset.</summary>
        public bool TryReadInt32At(int offset, out int value)
        {
            if (!TryReadBytesAt(offset, sizeof(int), out var bytes))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadInt32BigEndian(bytes);
            return true;
        }

        /// <summary>Reads an unsigned 32-bit integer.</summary>
        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(sizeof(uint)));

        /// <summary>Attempts to read an unsigned 32-bit integer.</summary>
        public bool TryReadUInt32(out uint value)
        {
            if (Remaining < sizeof(uint))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadUInt32BigEndian(source.Span[position..]);
            position += sizeof(uint);
            return true;
        }

        /// <summary>Reads an unsigned 32-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public uint ReadUInt32At(int offset) => BinaryPrimitives.ReadUInt32BigEndian(ReadBytesAt(offset, sizeof(uint)));

        /// <summary>Attempts to read an unsigned 32-bit integer at an absolute offset.</summary>
        public bool TryReadUInt32At(int offset, out uint value)
        {
            if (!TryReadBytesAt(offset, sizeof(uint), out var bytes))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }

        /// <summary>Reads a signed 64-bit integer.</summary>
        public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(sizeof(long)));

        /// <summary>Attempts to read a signed 64-bit integer.</summary>
        public bool TryReadInt64(out long value)
        {
            if (Remaining < sizeof(long))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadInt64BigEndian(source.Span[position..]);
            position += sizeof(long);
            return true;
        }

        /// <summary>Reads a signed 64-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public long ReadInt64At(int offset) => BinaryPrimitives.ReadInt64BigEndian(ReadBytesAt(offset, sizeof(long)));

        /// <summary>Attempts to read a signed 64-bit integer at an absolute offset.</summary>
        public bool TryReadInt64At(int offset, out long value)
        {
            if (!TryReadBytesAt(offset, sizeof(long), out var bytes))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadInt64BigEndian(bytes);
            return true;
        }

        /// <summary>Reads an unsigned 64-bit integer.</summary>
        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(sizeof(ulong)));

        /// <summary>Attempts to read an unsigned 64-bit integer.</summary>
        public bool TryReadUInt64(out ulong value)
        {
            if (Remaining < sizeof(ulong))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadUInt64BigEndian(source.Span[position..]);
            position += sizeof(ulong);
            return true;
        }

        /// <summary>Reads an unsigned 64-bit integer at an absolute offset without changing <see cref="Position"/>.</summary>
        public ulong ReadUInt64At(int offset) => BinaryPrimitives.ReadUInt64BigEndian(ReadBytesAt(offset, sizeof(ulong)));

        /// <summary>Attempts to read an unsigned 64-bit integer at an absolute offset.</summary>
        public bool TryReadUInt64At(int offset, out ulong value)
        {
            if (!TryReadBytesAt(offset, sizeof(ulong), out var bytes))
            {
                value = default;
                return false;
            }
            value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            return true;
        }

        /// <summary>Reads a four-character code.</summary>
        public FourCC ReadFourCC() => new(ReadUInt32());

        /// <summary>Attempts to read a four-character code.</summary>
        public bool TryReadFourCC(out FourCC value)
        {
            if (!TryReadUInt32(out uint raw))
            {
                value = default;
                return false;
            }
            value = new FourCC(raw);
            return true;
        }

        /// <summary>Reads a four-character code at an absolute offset without changing <see cref="Position"/>.</summary>
        public FourCC ReadFourCCAt(int offset) => new(ReadUInt32At(offset));

        /// <summary>Attempts to read a four-character code at an absolute offset.</summary>
        public bool TryReadFourCCAt(int offset, out FourCC value)
        {
            if (!TryReadUInt32At(offset, out uint raw))
            {
                value = default;
                return false;
            }
            value = new FourCC(raw);
            return true;
        }

        /// <summary>Reads a QuickDraw point.</summary>
        public MacPoint ReadMacPoint()
        {
            if (Remaining < MacPoint.Length)
            {
                throw new EndOfStreamException();
            }

            return new(ReadInt16(), ReadInt16());
        }

        /// <summary>Attempts to read a QuickDraw point.</summary>
        public bool TryReadMacPoint(out MacPoint value)
        {
            if (Remaining < MacPoint.Length)
            {
                value = default;
                return false;
            }
            value = new MacPoint(ReadInt16(), ReadInt16());
            return true;
        }

        /// <summary>Reads a QuickDraw point at an absolute offset without changing <see cref="Position"/>.</summary>
        public MacPoint ReadMacPointAt(int offset) => new(ReadInt16At(offset), ReadInt16At(offset + sizeof(short)));

        /// <summary>Attempts to read a QuickDraw point at an absolute offset.</summary>
        public bool TryReadMacPointAt(int offset, out MacPoint value)
        {
            if (!TryReadInt16At(offset, out var v) || !TryReadInt16At(offset + sizeof(short), out var h))
            {
                value = default;
                return false;
            }
            value = new MacPoint(v, h);
            return true;
        }

        /// <summary>Reads a QuickDraw rectangle.</summary>
        public MacRect ReadMacRect()
        {
            if (Remaining < MacRect.Length)
            {
                throw new EndOfStreamException();
            }

            return new(ReadInt16(), ReadInt16(), ReadInt16(), ReadInt16());
        }

        /// <summary>Attempts to read a QuickDraw rectangle.</summary>
        public bool TryReadMacRect(out MacRect value)
        {
            if (Remaining < MacRect.Length)
            {
                value = default;
                return false;
            }
            value = new MacRect(ReadInt16(), ReadInt16(), ReadInt16(), ReadInt16());
            return true;
        }

        /// <summary>Reads a QuickDraw rectangle at an absolute offset without changing <see cref="Position"/>.</summary>
        public MacRect ReadMacRectAt(int offset) => new(ReadInt16At(offset), ReadInt16At(offset + 2),
            ReadInt16At(offset + 4), ReadInt16At(offset + 6));

        /// <summary>Attempts to read a QuickDraw rectangle at an absolute offset.</summary>
        public bool TryReadMacRectAt(int offset, out MacRect value)
        {
            if (!TryReadInt16At(offset, out var top) || !TryReadInt16At(offset + 2, out var left)
                || !TryReadInt16At(offset + 4, out var bottom) || !TryReadInt16At(offset + 6, out var right))
            {
                value = default;
                return false;
            }
            value = new MacRect(top, left, bottom, right);
            return true;
        }

        /// <summary>Reads a signed 16.16 fixed-point value.</summary>
        public Fixed ReadFixed() => new(ReadInt32());

        /// <summary>Attempts to read a signed 16.16 fixed-point value.</summary>
        public bool TryReadFixed(out Fixed value)
        {
            if (!TryReadInt32(out int raw))
            {
                value = default;
                return false;
            }
            value = new Fixed(raw);
            return true;
        }

        /// <summary>Reads a signed 16.16 fixed-point value at an absolute offset without changing <see cref="Position"/>.</summary>
        public Fixed ReadFixedAt(int offset) => new(ReadInt32At(offset));

        /// <summary>Attempts to read a signed 16.16 fixed-point value at an absolute offset.</summary>
        public bool TryReadFixedAt(int offset, out Fixed value)
        {
            if (!TryReadInt32At(offset, out int raw))
            {
                value = default;
                return false;
            }
            value = new Fixed(raw);
            return true;
        }

        /// <summary>Reads an unsigned 16.16 fixed-point value.</summary>
        public UnsignedFixed ReadUnsignedFixed() => new(ReadUInt32());

        /// <summary>Attempts to read an unsigned 16.16 fixed-point value.</summary>
        public bool TryReadUnsignedFixed(out UnsignedFixed value)
        {
            if (!TryReadUInt32(out uint raw))
            {
                value = default;
                return false;
            }
            value = new UnsignedFixed(raw);
            return true;
        }

        /// <summary>Reads an unsigned 16.16 fixed-point value at an absolute offset without changing <see cref="Position"/>.</summary>
        public UnsignedFixed ReadUnsignedFixedAt(int offset) => new(ReadUInt32At(offset));

        /// <summary>Attempts to read an unsigned 16.16 fixed-point value at an absolute offset.</summary>
        public bool TryReadUnsignedFixedAt(int offset, out UnsignedFixed value)
        {
            if (!TryReadUInt32At(offset, out uint raw))
            {
                value = default;
                return false;
            }
            value = new UnsignedFixed(raw);
            return true;
        }

        /// <summary>Returns the next <paramref name="length"/> bytes as a borrowed slice and advances the reader.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        /// <exception cref="EndOfStreamException">Fewer than <paramref name="length"/> bytes remain.</exception>
        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (length > Remaining)
            {
                throw new EndOfStreamException();
            }

            var result = source.Span.Slice(position, length);
            position += length;
            return result;
        }

        /// <summary>Creates a bounded reader over the next <paramref name="length"/> bytes and advances this reader past them.</summary>
        /// <remarks>The returned reader borrows the same source; it cannot read beyond the selected section.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        /// <exception cref="EndOfStreamException">Fewer than <paramref name="length"/> bytes remain.</exception>
        public BigEndianReader ReadSubReader(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (length > Remaining)
            {
                throw new EndOfStreamException();
            }

            var sub = new BigEndianReader(source.Slice(position, length));
            position += length;
            return sub;
        }

        /// <summary>Attempts to return the next <paramref name="length"/> bytes as a borrowed slice.</summary>
        /// <remarks>A failed attempt returns an empty slice and leaves <see cref="Position"/> unchanged.</remarks>
        public bool TryReadBytes(int length, out ReadOnlySpan<byte> value)
        {
            if (length < 0 || length > Remaining)
            {
                value = default;
                return false;
            }
            value = source.Span.Slice(position, length);
            position += length;
            return true;
        }

        /// <summary>Returns a borrowed byte slice at an absolute offset without changing <see cref="Position"/>.</summary>
        public ReadOnlySpan<byte> ReadBytesAt(int offset, int length)
        {
            ValidateRange(offset, length);
            return source.Span.Slice(offset, length);
        }

        /// <summary>Attempts to return a borrowed byte slice at an absolute offset.</summary>
        public bool TryReadBytesAt(int offset, int length, out ReadOnlySpan<byte> value)
        {
            if (!IsRangeValid(offset, length))
            {
                value = default;
                return false;
            }
            value = source.Span.Slice(offset, length);
            return true;
        }

        /// <summary>Advances past <paramref name="length"/> bytes.</summary>
        public void Skip(int length) => ReadBytes(length);

        /// <summary>Attempts to advance past <paramref name="length"/> bytes.</summary>
        public bool TrySkip(int length)
        {
            if (length < 0 || length > Remaining)
            {
                return false;
            }

            position += length;
            return true;
        }

        private bool IsRangeValid(int offset, int length) =>
            offset >= 0 && length >= 0 && offset <= source.Length && length <= source.Length - offset;

        private void ValidateRange(int offset, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (!IsRangeValid(offset, length))
            {
                throw new EndOfStreamException();
            }
        }

        private static byte[] ReadToEnd(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }
}
