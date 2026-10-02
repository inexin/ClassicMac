using System;
using System.Collections.Generic;
using System.Text;

namespace ClassicMac.Core
{
    /// <summary>
    /// The Mac OS Roman text encoding, from Unicode's Apple mapping file <c>VENDORS/APPLE/ROMAN.TXT</c> (version c02,
    /// 2005): bytes 0–$7F are ASCII, $80–$FF map one to one onto Unicode, so decoding and encoding round-trip every byte.
    /// $DB is the euro sign (Mac OS 8.5 and later; earlier systems drew ¤ there) and $F0 the Apple logo (U+F8FF, private
    /// use).
    /// </summary>
    public static class MacRoman
    {
        // Unicode for bytes $80–$FF.
        private static readonly char[] High =
        [
            'Ä', 'Å', 'Ç', 'É', 'Ñ', 'Ö', 'Ü', 'á', 'à', 'â', 'ä', 'ã', 'å', 'ç', 'é', 'è',
            'ê', 'ë', 'í', 'ì', 'î', 'ï', 'ñ', 'ó', 'ò', 'ô', 'ö', 'õ', 'ú', 'ù', 'û', 'ü',
            '†', '°', '¢', '£', '§', '•', '¶', 'ß', '®', '©', '™', '´', '¨', '≠', 'Æ', 'Ø',
            '∞', '±', '≤', '≥', '¥', 'µ', '∂', '∑', '∏', 'π', '∫', 'ª', 'º', 'Ω', 'æ', 'ø',
            '¿', '¡', '¬', '√', 'ƒ', '≈', '∆', '«', '»', '…', ' ', 'À', 'Ã', 'Õ', 'Œ', 'œ',
            '–', '—', '“', '”', '‘', '’', '÷', '◊', 'ÿ', 'Ÿ', '⁄', '€', '‹', '›', 'ﬁ', 'ﬂ',
            '‡', '·', '‚', '„', '‰', 'Â', 'Ê', 'Á', 'Ë', 'È', 'Í', 'Î', 'Ï', 'Ì', 'Ó', 'Ô',
            '', 'Ò', 'Ú', 'Û', 'Ù', 'ı', 'ˆ', '˜', '¯', '˘', '˙', '˚', '¸', '˝', '˛', 'ˇ',
        ];

        private static readonly Dictionary<char, byte> Reverse = BuildReverse();

        /// <summary>The Unicode character for one byte.</summary>
        public static char ToChar(byte value) => value < 0x80 ? (char)value : High[value - 0x80];

        /// <summary>Decodes bytes to a string, one character per byte.</summary>
        public static string Decode(ReadOnlySpan<byte> bytes)
        {
            var text = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                text.Append(ToChar(b));
            }

            return text.ToString();
        }

        /// <summary>The byte for one character, if Mac OS Roman has it.</summary>
        public static bool TryGetByte(char c, out byte value)
        {
            if (c < 0x80)
            {
                value = (byte)c;
                return true;
            }
            return Reverse.TryGetValue(c, out value);
        }

        /// <summary>Encodes a string; false if a character has no Mac OS Roman byte.</summary>
        public static bool TryEncode(string text, out byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(text);
            bytes = new byte[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                if (!TryGetByte(text[i], out bytes[i]))
                {
                    bytes = [];
                    return false;
                }
            }
            return true;
        }

        /// <summary>Encodes a string, throwing <see cref="ArgumentException"/> on a character Mac OS Roman lacks.</summary>
        public static byte[] Encode(string text) =>
            TryEncode(text, out var bytes)
                ? bytes
                : throw new ArgumentException("The text has characters that Mac OS Roman cannot represent.", nameof(text));

        private static Dictionary<char, byte> BuildReverse()
        {
            var reverse = new Dictionary<char, byte>(High.Length + 2);
            for (var i = 0; i < High.Length; i++)
            {
                reverse[High[i]] = (byte)(0x80 + i);
            }
            // Older mappings, accepted when encoding: ¤ (currency sign, $DB before Mac OS 8.5) and Ω (ohm sign, $BD).
            reverse.TryAdd('¤', 0xDB);
            reverse.TryAdd('Ω', 0xBD);
            return reverse;
        }
    }
}
