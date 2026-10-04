using System;

namespace ClassicMac.Core;

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
        if (bytes.Length > 255)
        {
            throw new ArgumentException("A Pascal string holds at most 255 bytes.", nameof(bytes));
        }

        this.bytes = bytes.ToArray();
    }

    /// <summary>The raw bytes.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>The length in bytes.</summary>
    public int Length => bytes?.Length ?? 0;

    /// <summary>A string from Unicode text encoded as Mac OS Roman; throws if it does not fit or cannot be encoded.</summary>
    public static MacString FromMacRoman(string text) => new(MacRoman.Encode(text));

    /// <summary>The text decoded as Mac OS Roman, control characters included.</summary>
    public string ToMacRoman() => MacRoman.Decode(Bytes);

    /// <summary>
    /// The string <see cref="ToString"/> gave: Mac OS Roman characters, and <c>\xHH</c> for a byte written as its hex
    /// code (control characters, DEL, backslash). Throws for a character Mac OS Roman cannot encode.
    /// </summary>
    public static MacString Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new System.Collections.Generic.List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 3 < text.Length && text[i + 1] == 'x'
                && byte.TryParse(text.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var escaped))
            {
                bytes.Add(escaped);
                i += 3;
            }
            else
            {
                bytes.AddRange(MacRoman.Encode(text[i].ToString()));
            }
        }
        return new MacString(bytes.ToArray());
    }

    /// <summary>
    /// Display text: Mac OS Roman, with control characters and backslash as <c>\xHH</c>. Other Mac encodings need the
    /// file's script and come with the text encodings.
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
