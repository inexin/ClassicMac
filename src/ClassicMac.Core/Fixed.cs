using System;
using System.Buffers.Binary;
using System.IO;
namespace ClassicMac.Core
{
    /// <summary>
    /// A Mac <c>Fixed</c>: a signed 32-bit number with 16 integer and 16 fraction bits (<i>Inside Macintosh: Operating
    /// System Utilities</i>, Mathematical and Logical Utilities). Used for resolutions, font metrics and QuickTime values.
    /// </summary>
    /// <param name="Raw">The 32-bit value as stored.</param>
    public readonly record struct Fixed(int Raw) : IComparable<Fixed>
    {
        /// <summary>1.0.</summary>
        public static Fixed One { get; } = new(0x10000);

        /// <summary>The value as a <see cref="double"/> (exact).</summary>
        public double ToDouble() => Raw / 65536.0;

        /// <summary>
        /// The nearest <c>Fixed</c> to <paramref name="value"/>, halves rounded away from zero. Not taken from Apple's
        /// <c>X2Fix</c>; values outside −32768 … 32767.99998 throw rather than saturate.
        /// </summary>
        public static Fixed FromDouble(double value)
        {
            var raw = Math.Round(value * 65536.0, MidpointRounding.AwayFromZero);
            if (raw is < int.MinValue or > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A Fixed holds −32768 up to just under 32768.");
            }

            return new Fixed((int)raw);
        }

        /// <summary>Reads a big-endian <c>Fixed</c>.</summary>
        public static Fixed Read(ReadOnlySpan<byte> source)
        {
            if (source.Length < 4)
            {
                throw new EndOfStreamException();
            }

            return new(BinaryPrimitives.ReadInt32BigEndian(source));
        }

        /// <summary>Reads a fixed-point value at the reader's current position and advances it.</summary>
        public static Fixed Read(BigEndianReader reader) => reader.ReadFixed();

        /// <summary>Writes the value as four big-endian bytes.</summary>
        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, Raw);
        }

        /// <summary>Writes a fixed-point value at the end of the writer's output.</summary>
        public void Write(BigEndianWriter writer) => writer.WriteFixed(this);

        /// <inheritdoc/>
        public int CompareTo(Fixed other) => Raw.CompareTo(other.Raw);

        /// <inheritdoc/>
        public override string ToString() => ToDouble().ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A Mac <c>UnsignedFixed</c>: an unsigned 16.16 number. Sound sample rates use it (<i>Inside Macintosh: Sound</i>),
    /// since 44100 Hz does not fit a signed <see cref="Fixed"/>.
    /// </summary>
    /// <param name="Raw">The 32-bit value as stored.</param>
    public readonly record struct UnsignedFixed(uint Raw) : IComparable<UnsignedFixed>
    {
        /// <summary>1.0.</summary>
        public static UnsignedFixed One { get; } = new(0x10000);

        /// <summary>The value as a <see cref="double"/> (exact).</summary>
        public double ToDouble() => Raw / 65536.0;

        /// <summary>
        /// The nearest <c>UnsignedFixed</c> to <paramref name="value"/>, halves rounded up; values outside 0 … 65535.99998
        /// throw.
        /// </summary>
        public static UnsignedFixed FromDouble(double value)
        {
            var raw = Math.Round(value * 65536.0, MidpointRounding.AwayFromZero);
            if (raw is < 0 or > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "An UnsignedFixed holds 0 up to just under 65536.");
            }

            return new UnsignedFixed((uint)raw);
        }

        /// <summary>Reads a big-endian <c>UnsignedFixed</c>.</summary>
        public static UnsignedFixed Read(ReadOnlySpan<byte> source)
        {
            if (source.Length < 4)
            {
                throw new EndOfStreamException();
            }

            return new(BinaryPrimitives.ReadUInt32BigEndian(source));
        }

        /// <summary>Reads an unsigned fixed-point value at the reader's current position and advances it.</summary>
        public static UnsignedFixed Read(BigEndianReader reader) => reader.ReadUnsignedFixed();

        /// <summary>Writes the value as four big-endian bytes.</summary>
        public void Write(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, Raw);
        }

        /// <summary>Writes an unsigned fixed-point value at the end of the writer's output.</summary>
        public void Write(BigEndianWriter writer) => writer.WriteUnsignedFixed(this);

        /// <inheritdoc/>
        public int CompareTo(UnsignedFixed other) => Raw.CompareTo(other.Raw);

        /// <inheritdoc/>
        public override string ToString() => ToDouble().ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
    }
}
