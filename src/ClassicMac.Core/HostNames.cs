using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassicMac.Core
{
    /// <summary>
    /// Names for the host's disk. <see cref="ToHostName(MacString, int)"/> gives names valid and distinct on Windows,
    /// macOS and Linux, and readable: Mac Roman becomes Unicode; control characters, <c>% / \ : * ? " &lt; &gt; |</c> and a
    /// trailing space or dot become <c>%XX</c> (the Mac Roman byte); a reserved Windows name (<c>CON</c>, <c>COM1</c>, …)
    /// gets its last character escaped (<c>COM%31</c>). <see cref="ToBasiliskName"/> gives the names SheepShaver's
    /// shared folders use instead.
    /// </summary>
    public static class HostNames
    {
        private const string Escaped = "%/\\:*?\"<>|";

        // SheepShaver escapes exactly these as %XX, and decodes any %XX it reads (harness, SheepShaver on Windows).
        private const string BasiliskEscaped = "%?*\"<>|";

        private static readonly HashSet<string> Reserved = new(
            ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
            StringComparer.OrdinalIgnoreCase);

        // Windows code page 1252 for bytes $80–$9F ('\0' where it has none); $A0–$FF are Latin-1.
        private static readonly char[] Cp1252High =
        [
            '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡',
            'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
            '\0', '‘', '’', '“', '”', '•', '–', '—',
            '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
        ];

        /// <summary>
        /// The host name for a Mac name, at most <paramref name="maxLength"/> characters (cut before the extension, never
        /// inside an escape).
        /// </summary>
        public static string ToHostName(MacString name, int maxLength = 255)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 8);
            var bytes = name.Bytes;
            if (bytes.Length == 0) return "%00";
            var parts = new List<string>(bytes.Length);
            for (var i = 0; i < bytes.Length; i++)
            {
                var c = MacRoman.ToChar(bytes[i]);
                var last = i == bytes.Length - 1;
                parts.Add(c < 0x20 || c == 0x7F || Escaped.Contains(c) || (last && c is ' ' or '.') ? Escape(bytes[i]) : c.ToString());
            }

            // A reserved device name, with or without an extension: escape the last character of its stem.
            var stemParts = ReservedStem(parts);
            if (stemParts > 0) parts[stemParts - 1] = Escape(bytes[stemParts - 1]);
            return Fit(parts, maxLength);
        }

        /// <summary>
        /// The host name SheepShaver (on Windows) gives a Mac name in its shared folder, so the emulator reads it back:
        /// each Mac Roman byte as the Windows-1252 character with the same value (it does no Unicode conversion), and
        /// <c>% ? * " &lt; &gt; |</c>, control characters and bytes 1252 lacks as <c>%XX</c>. What its folders cannot hold
        /// at all — <c>/ \ :</c>, a trailing space or dot, a reserved device name — is replaced (by <c>_</c>, or an
        /// appended <c>_</c>), because any <c>%XX</c> would be decoded back and fail; the name then differs from the Mac's.
        /// Fitted to the SheepShaver harness (Windows build); other hosts' builds may convert names differently.
        /// </summary>
        public static string ToBasiliskName(MacString name, int maxLength = 255)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 8);
            var bytes = name.Bytes;
            if (bytes.Length == 0) return "_";
            var parts = new List<string>(bytes.Length);
            foreach (var b in bytes)
            {
                var c = FromCp1252(b);
                parts.Add(c is '/' or '\\' or ':' ? "_" : c == '\0' || c < 0x20 || c == 0x7F || BasiliskEscaped.Contains(c) ? Escape(b) : c.ToString());
            }
            for (var i = parts.Count - 1; i >= 0 && parts[i] is " " or "."; i--) parts[i] = "_";
            var stemParts = ReservedStem(parts);
            if (stemParts > 0) parts.Insert(stemParts, "_");
            return Fit(parts, maxLength);
        }

        /// <summary>
        /// The Mac Roman byte for a character of a SheepShaver shared-folder name (its Windows-1252 byte), or false when
        /// Windows-1252 has no such character.
        /// </summary>
        public static bool TryGetBasiliskByte(char c, out byte value)
        {
            if (c < 0x80 || c is >= ' ' and <= 'ÿ')
            {
                value = (byte)c;
                return true;
            }
            var index = Array.IndexOf(Cp1252High, c);
            value = (byte)(0x80 + Math.Max(index, 0));
            return index >= 0 && c != '\0';
        }

        /// <summary>
        /// The folder name for a resource type in an export: its four bytes as a name (<c>snd </c> → <c>snd%20</c>), and,
        /// when another type in the same fork differs from it only in case (<paramref name="collides"/>), <c>~</c> and
        /// the type's bytes in hex (<c>PICT~50494354</c>, <c>pict~70696374</c>), since Windows and macOS disks ignore
        /// case.
        /// </summary>
        public static string TypeFolder(FourCC type, bool collides)
        {
            Span<byte> bytes = stackalloc byte[4];
            type.CopyTo(bytes);
            var name = ToHostName(new MacString(bytes), 255);
            return collides ? $"{name}~{type.Value:X8}" : name;
        }

        /// <summary>
        /// <paramref name="name"/>, or with <c> ~2</c>, <c> ~3</c> … before its extension, so that it differs from every
        /// name in <paramref name="taken"/> without regard to case; the result is added to <paramref name="taken"/>.
        /// </summary>
        public static string MakeUnique(string name, ISet<string> taken)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(taken);
            var candidate = name;
            var dot = name.LastIndexOf('.');
            var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
            for (var n = 2; Contains(taken, candidate); n++) candidate = $"{stem} ~{n}{extension}";
            taken.Add(candidate);
            return candidate;
        }

        private static char FromCp1252(byte b) => b is >= 0x80 and < 0xA0 ? Cp1252High[b - 0x80] : (char)b;

        // How many parts make up a reserved device name's stem (0 when the name is not one).
        private static int ReservedStem(List<string> parts)
        {
            var text = string.Concat(parts);
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var stem = dot < 0 ? text : text[..dot];
            if (!Reserved.Contains(stem.TrimEnd(' '))) return 0;
            var stemParts = 0;
            for (var length = 0; length < stem.Length; stemParts++) length += parts[stemParts].Length;
            return stemParts;
        }

        private static bool Contains(ISet<string> taken, string name) =>
            taken.Contains(name) || taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

        private static string Escape(byte b) => "%" + b.ToString("X2", CultureInfo.InvariantCulture);

        // Cuts whole parts (characters or escapes) from the end of the stem, keeping an extension of up to 5 parts.
        private static string Fit(List<string> parts, int maxLength)
        {
            var total = parts.Sum(p => p.Length);
            if (total <= maxLength) return string.Concat(parts);
            var dot = parts.LastIndexOf(".");
            var extension = dot > 0 && parts.Count - dot <= 5 ? parts.GetRange(dot, parts.Count - dot) : [];
            var stem = parts.GetRange(0, parts.Count - extension.Count);
            var budget = maxLength - extension.Sum(p => p.Length);
            var kept = new StringBuilder();
            foreach (var part in stem)
            {
                if (kept.Length + part.Length > budget) break;
                kept.Append(part);
            }
            // Never end on a space or dot, which Windows drops.
            var result = kept.ToString().TrimEnd(' ', '.');
            return result + string.Concat(extension);
        }
    }
}
