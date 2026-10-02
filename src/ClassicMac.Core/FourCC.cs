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
            if (bytes.Length != 4)
            {
                throw new ArgumentException("A four-character code is four bytes.", nameof(bytes));
            }

            Value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        /// <summary>
        /// Creates a code from its text: four Mac OS Roman characters (<c>"snd "</c>, <c>"PICT"</c>, <c>"©abc"</c>), any
        /// of them may be written <c>\xHH</c>, as <see cref="ToString"/> writes control characters and backslash.
        /// </summary>
        public static FourCC FromString(string code) =>
            TryParse(code, out var result)
                ? result
                : throw new ArgumentException(
                    "A four-character code is four Mac OS Roman characters or \\xHH escapes.", nameof(code));

        /// <summary>Parses the text form described at <see cref="FromString"/>.</summary>
        public static bool TryParse(string? code, out FourCC result)
        {
            result = default;
            if (code is null)
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[4];
            var count = 0;
            for (var i = 0; i < code.Length; i++)
            {
                if (count == 4)
                {
                    return false;
                }

                if (code[i] == '\\' && i + 3 < code.Length && code[i + 1] == 'x'
                    && byte.TryParse(code.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var escaped))
                {
                    bytes[count++] = escaped;
                    i += 3;
                }
                else if (MacRoman.TryGetByte(code[i], out var b))
                {
                    bytes[count++] = b;
                }
                else
                {
                    return false;
                }
            }
            if (count != 4)
            {
                return false;
            }

            result = new FourCC(bytes);
            return true;
        }

        /// <summary>Writes the four bytes to <paramref name="destination"/>.</summary>
        public void CopyTo(Span<byte> destination)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, Value);
        }

        /// <summary>
        /// The code as Mac OS Roman text; control characters and backslash as <c>\xHH</c>. <see cref="FromString"/>
        /// reads it back.
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

    // Display text for codes and names: Mac OS Roman, with control characters, DEL and backslash as \xHH so every
    // byte stays visible and the text reads back unambiguously.
    internal static class MacText
    {
        public static string Escape(ReadOnlySpan<byte> bytes)
        {
            var text = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                if (b < 0x20 || b == 0x7F || b == '\\')
                {
                    text.Append("\\x").Append(b.ToString("X2"));
                }
                else
                {
                    text.Append(MacRoman.ToChar(b));
                }
            }
            return text.ToString();
        }
    }
}
