using System;
using System.Buffers.Binary;
using System.IO;

namespace ClassicMac.Core
{
    /// <summary>Reads big-endian values sequentially from a stream, forward only.</summary>
    /// <remarks>
    /// This reader does not own or close the stream, and reads from its current position. <see cref="Position"/>
    /// counts the bytes consumed through this instance, starting at zero, and does not depend on the stream being
    /// seekable. <see cref="Peek"/> looks ahead without consuming: the bytes it reads from the stream are kept and
    /// returned by the next reads, so the stream itself may be ahead of <see cref="Position"/>. There are no reads at
    /// absolute offsets and no <c>Remaining</c>: a format bounds its own sections. Truncated input throws
    /// <see cref="EndOfStreamException"/>.
    /// </remarks>
    public sealed class BigEndianStreamReader
    {
        private const int ChunkSize = 81920;

        private readonly Stream stream;
        private byte[] lookahead = [];
        private int lookaheadStart;
        private int lookaheadCount;

        /// <summary>Creates a reader for a readable stream.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="ArgumentException">The stream does not support reading.</exception>
        public BigEndianStreamReader(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanRead) throw new ArgumentException("The stream must support reading.", nameof(stream));
            this.stream = stream;
        }

        /// <summary>The number of bytes consumed through this reader.</summary>
        public long Position { get; private set; }

        /// <summary>True when no byte is left to read.</summary>
        public bool IsAtEnd
        {
            get
            {
                Span<byte> one = stackalloc byte[1];
                return Peek(one) == 0;
            }
        }

        /// <summary>Reads one byte.</summary>
        public byte ReadByte()
        {
            Span<byte> bytes = stackalloc byte[1];
            ReadExactly(bytes);
            return bytes[0];
        }

        /// <summary>Reads a signed 16-bit integer.</summary>
        public short ReadInt16()
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt16BigEndian(bytes);
        }

        /// <summary>Reads an unsigned 16-bit integer.</summary>
        public ushort ReadUInt16()
        {
            Span<byte> bytes = stackalloc byte[sizeof(ushort)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }

        /// <summary>Reads a signed 32-bit integer.</summary>
        public int ReadInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }

        /// <summary>Reads an unsigned 32-bit integer.</summary>
        public uint ReadUInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        /// <summary>Reads a signed 64-bit integer.</summary>
        public long ReadInt64()
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt64BigEndian(bytes);
        }

        /// <summary>Reads an unsigned 64-bit integer.</summary>
        public ulong ReadUInt64()
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt64BigEndian(bytes);
        }

        /// <summary>Reads a four-character code.</summary>
        public FourCC ReadFourCC() => new(ReadUInt32());

        /// <summary>Reads a QuickDraw point.</summary>
        public MacPoint ReadMacPoint()
        {
            Span<byte> bytes = stackalloc byte[MacPoint.Length];
            ReadExactly(bytes);
            return new BigEndianReader(bytes).ReadMacPoint();
        }

        /// <summary>Reads a QuickDraw rectangle.</summary>
        public MacRect ReadMacRect()
        {
            Span<byte> bytes = stackalloc byte[MacRect.Length];
            ReadExactly(bytes);
            return new BigEndianReader(bytes).ReadMacRect();
        }

        /// <summary>Reads a signed 16.16 fixed-point value.</summary>
        public Fixed ReadFixed() => new(ReadInt32());

        /// <summary>Reads an unsigned 16.16 fixed-point value.</summary>
        public UnsignedFixed ReadUnsignedFixed() => new(ReadUInt32());

        /// <summary>Fills <paramref name="destination"/> with the next bytes.</summary>
        /// <exception cref="EndOfStreamException">The input ends first; the bytes that were there are consumed.</exception>
        public void ReadExactly(Span<byte> destination)
        {
            if (ReadAtMost(destination) < destination.Length) throw new EndOfStreamException();
        }

        /// <summary>Reads the next <paramref name="length"/> bytes into a new array.</summary>
        /// <remarks>The array grows as the data arrives, so a length larger than the input never allocates it whole.</remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        /// <exception cref="EndOfStreamException">The input ends first; the bytes that were there are consumed.</exception>
        public byte[] ReadBytes(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            var result = new byte[Math.Min(length, ChunkSize)];
            int filled = 0;
            while (filled < length)
            {
                if (filled == result.Length) Array.Resize(ref result, (int)Math.Min(length, 2L * result.Length));
                int read = ReadAtMost(result.AsSpan(filled));
                if (read == 0) throw new EndOfStreamException();
                filled += read;
            }
            return result;
        }

        /// <summary>Reads into <paramref name="destination"/> until it is full or the input ends.</summary>
        /// <returns>The number of bytes read: less than the destination's length only at the end of the input.</returns>
        public int ReadAtMost(Span<byte> destination)
        {
            int filled = 0;
            if (lookaheadCount > 0)
            {
                filled = Math.Min(lookaheadCount, destination.Length);
                lookahead.AsSpan(lookaheadStart, filled).CopyTo(destination);
                lookaheadStart += filled;
                lookaheadCount -= filled;
            }
            while (filled < destination.Length)
            {
                int read = stream.Read(destination[filled..]);
                if (read == 0) break;
                filled += read;
            }
            Position += filled;
            return filled;
        }

        /// <summary>Advances past <paramref name="length"/> bytes.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        /// <exception cref="EndOfStreamException">The input ends first; it is consumed to its end.</exception>
        public void Skip(long length)
        {
            if (SkipAtMost(length) < length) throw new EndOfStreamException();
        }

        /// <summary>Advances past up to <paramref name="length"/> bytes, stopping at the end of the input.</summary>
        /// <returns>The number of bytes skipped.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        public long SkipAtMost(long length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            Span<byte> scratch = stackalloc byte[(int)Math.Min(length, 4096)];
            long skipped = 0;
            while (skipped < length)
            {
                int read = ReadAtMost(scratch[..(int)Math.Min(length - skipped, scratch.Length)]);
                if (read == 0) break;
                skipped += read;
            }
            return skipped;
        }

        /// <summary>Copies the next bytes into <paramref name="destination"/> without consuming them.</summary>
        /// <returns>The number of bytes copied: less than the destination's length only at the end of the input.</returns>
        public int Peek(scoped Span<byte> destination)
        {
            if (lookaheadCount < destination.Length)
            {
                if (lookahead.Length - lookaheadStart < destination.Length)
                {
                    var grown = lookahead.Length >= destination.Length ? lookahead : new byte[destination.Length];
                    lookahead.AsSpan(lookaheadStart, lookaheadCount).CopyTo(grown);
                    lookahead = grown;
                    lookaheadStart = 0;
                }
                while (lookaheadCount < destination.Length)
                {
                    int read = stream.Read(lookahead.AsSpan(lookaheadStart + lookaheadCount, destination.Length - lookaheadCount));
                    if (read == 0) break;
                    lookaheadCount += read;
                }
            }
            int count = Math.Min(lookaheadCount, destination.Length);
            lookahead.AsSpan(lookaheadStart, count).CopyTo(destination);
            return count;
        }

        // Moves a seekable stream back over the bytes peeked but not consumed, so it stands at Position; a
        // non-seekable stream keeps them read.
        internal void ReturnLookahead()
        {
            if (lookaheadCount == 0 || !stream.CanSeek) return;
            stream.Seek(-lookaheadCount, SeekOrigin.Current);
            lookaheadCount = 0;
            lookaheadStart = 0;
        }
    }
}
