using System;
using System.Buffers.Binary;
using System.Text;

namespace ClassicMac.Core
{
    /// <summary>
    /// A four-character code (OSType, ResType): four bytes, compared exactly and case-sensitively.
    /// </summary>
    public readonly struct FourCC : IEquatable<FourCC>, IComparable<FourCC>
    {
        /// <summary>The code as a big-endian 32-bit value, as stored in Mac files.</summary>
        public uint Value { get; }

        /// <summary>Creates a code from its big-endian 32-bit value.</summary>
        public FourCC(uint value) => Value = value;

        /// <summary>Creates a code from its four bytes.</summary>
        public FourCC(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != 4) throw new ArgumentException("A four-character code is four bytes.", nameof(bytes));
            Value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        /// <summary>
        /// Creates a code from four characters in the range U+0000–U+00FF, one byte each (<c>"snd "</c>, <c>"PICT"</c>).
        /// </summary>
        public static FourCC FromString(string code)
        {
            ArgumentNullException.ThrowIfNull(code);
            if (code.Length != 4) throw new ArgumentException("A four-character code is four characters.", nameof(code));
            Span<byte> bytes = stackalloc byte[4];
            for (var i = 0; i < 4; i++)
            {
                if (code[i] > 0xFF) throw new ArgumentException("Characters must be in U+0000–U+00FF.", nameof(code));
                bytes[i] = (byte)code[i];
            }
            return new FourCC(bytes);
        }

        /// <summary>Writes the four bytes to <paramref name="destination"/>.</summary>
        public void CopyTo(Span<byte> destination) => BinaryPrimitives.WriteUInt32BigEndian(destination, Value);

        /// <summary>
        /// The code as text: printable ASCII as is, other bytes as <c>\xHH</c>. Mac encodings replace this once the
        /// text encodings exist.
        /// </summary>
        public override string ToString()
        {
            Span<byte> bytes = stackalloc byte[4];
            CopyTo(bytes);
            return MacText.Escape(bytes);
        }

        /// <inheritdoc/>
        public bool Equals(FourCC other) => Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is FourCC other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (int)Value;

        /// <summary>Orders codes by their byte values, for stable output.</summary>
        public int CompareTo(FourCC other) => Value.CompareTo(other.Value);

        /// <summary>Equality.</summary>
        public static bool operator ==(FourCC left, FourCC right) => left.Equals(right);

        /// <summary>Inequality.</summary>
        public static bool operator !=(FourCC left, FourCC right) => !left.Equals(right);
    }

    // Placeholder text conversion until the Mac text encodings exist.
    internal static class MacText
    {
        public static string Escape(ReadOnlySpan<byte> bytes)
        {
            var text = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                if (b is >= 0x20 and < 0x7F && b != '\\') text.Append((char)b);
                else text.Append("\\x").Append(b.ToString("X2"));
            }
            return text.ToString();
        }
    }
}
