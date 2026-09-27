using System;

namespace ClassicMac.Core
{
    /// <summary>
    /// Text as the Mac stored it: the raw bytes of a Pascal string, in whatever encoding the file used. Kept as bytes so
    /// a read → write round trip is exact; decoding to Unicode is a separate step that needs the file's encoding.
    /// </summary>
    public readonly struct MacString : IEquatable<MacString>
    {
        private readonly byte[]? bytes;

        /// <summary>Creates a string from its raw bytes (at most 255, the Pascal string limit).</summary>
        public MacString(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > 255) throw new ArgumentException("A Pascal string holds at most 255 bytes.", nameof(bytes));
            this.bytes = bytes.ToArray();
        }

        /// <summary>The raw bytes.</summary>
        public ReadOnlySpan<byte> Bytes => bytes;

        /// <summary>The length in bytes.</summary>
        public int Length => bytes?.Length ?? 0;

        /// <summary>
        /// The text: printable ASCII as is, other bytes as <c>\xHH</c>. Mac encodings replace this once the text
        /// encodings exist.
        /// </summary>
        public override string ToString() => MacText.Escape(Bytes);

        /// <inheritdoc/>
        public bool Equals(MacString other) => Bytes.SequenceEqual(other.Bytes);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is MacString other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(Bytes);
            return hash.ToHashCode();
        }

        /// <summary>Equality.</summary>
        public static bool operator ==(MacString left, MacString right) => left.Equals(right);

        /// <summary>Inequality.</summary>
        public static bool operator !=(MacString left, MacString right) => !left.Equals(right);
    }
}
