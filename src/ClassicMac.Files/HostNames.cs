using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// Names for the host's disk: valid and distinct on Windows, macOS and Linux, and readable. Mac Roman becomes
    /// Unicode; control characters, <c>% / \ : * ? " &lt; &gt; |</c> and a trailing space or dot become <c>%XX</c> (the
    /// Mac Roman byte); a reserved Windows name (<c>CON</c>, <c>COM1</c>, …) gets its last character escaped
    /// (<c>COM%31</c>). Basilisk II and SheepShaver read <c>%XX</c> back as the byte, and so does
    /// <see cref="HostFiles.ToMacName"/>, so these names round-trip through their shared folders.
    /// </summary>
    public static class HostNames
    {
        private const string Escaped = "%/\\:*?\"<>|";
        private static readonly HashSet<string> Reserved = new(
            ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
            StringComparer.OrdinalIgnoreCase);

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
            var text = string.Concat(parts);
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            var stem = dot < 0 ? text : text[..dot];
            if (Reserved.Contains(stem.TrimEnd(' ')))
            {
                var stemParts = 0;
                for (var length = 0; length < stem.Length; stemParts++) length += parts[stemParts].Length;
                parts[stemParts - 1] = Escape(bytes[stemParts - 1]);
            }
            return Fit(parts, maxLength);
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
