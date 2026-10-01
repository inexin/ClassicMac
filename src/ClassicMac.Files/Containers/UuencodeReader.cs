using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Containers
{
    /// <summary>
    /// uuencode (.uu, .uue): a file as 7-bit text, from the Unix utility of the same name, as POSIX (the Single UNIX
    /// Specification, <c>uuencode</c>) defines it. A line <c>begin mode name</c> opens a block; each line after it
    /// starts with a character giving its byte count and encodes 3 bytes in 4 characters ($20–$5F, or the backquote for
    /// 0); a line of count 0 and the line <c>end</c> close it. <c>begin-base64</c> opens the base64 variant
    /// (<c>uuencode -m</c>), closed by <c>====</c>. A block carries only a name and the bytes, so each becomes a file with
    /// a data fork alone; what it holds (often MacBinary or BinHex) is found by the unwrapper's recursion.
    /// </summary>
    public sealed class UuencodeReader : IContainerReader
    {
        // How far into a file the first begin line is looked for: mail and news headers precede it. The same bound as
        // BinHex's marker search; fitted to real downloads, not a limit to tune.
        private const int SearchLength = 64 * 1024;

        private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        private static readonly sbyte[] Base64Values = BuildBase64Values();

        /// <summary>The reader.</summary>
        public static UuencodeReader Instance { get; } = new();

        private UuencodeReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "uuencode";

        /// <inheritdoc/>
        public bool CanRead(ForkData input)
        {
            var text = input.ReadPrefix(SearchLength);
            foreach (var (start, end) in Lines(text))
            {
                if (ParseBegin(text.AsSpan(start, end - start), out _, out _)) return true;
            }
            return false;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            var text = input.ToArray(context.Options.MaxExpandedBytesPerInput);
            var files = new List<MacFile>();
            Block? block = null;
            foreach (var (start, end) in Lines(text))
            {
                var line = text.AsSpan(start, end - start);
                if (block is null)
                {
                    // Outside a block only begin lines matter: anything else is mail or news text. [Doc]
                    if (ParseBegin(line, out var base64, out var name))
                        block = new Block(base64, name);
                    continue;
                }
                if (block.Ended)
                {
                    // After the zero-count line the next line should be "end". [Doc]
                    if (IsBlank(line)) continue;
                    if (!line.TrimEnd(" \t"u8).SequenceEqual("end"u8))
                    {
                        context.Report(DiagnosticSeverity.Warning, "uuencode.missing-end",
                            $"The block for \"{block.NameText}\" has no \"end\" line after its last data line.", start);
                    }
                    files.Add(block.ToFile());
                    block = null;
                    if (ParseBegin(line, out var base64, out var name)) block = new Block(base64, name);
                    continue;
                }
                if (block.Base64) DecodeBase64Line(line, start, block, context);
                else DecodeUuLine(line, start, block, context);
                if (block.Ended && block.Base64)
                {
                    files.Add(block.ToFile());
                    block = null;
                }
            }
            if (block is not null)
            {
                if (block.Ended)
                {
                    context.Report(DiagnosticSeverity.Warning, "uuencode.missing-end",
                        $"The block for \"{block.NameText}\" has no \"end\" line after its last data line.", text.Length);
                }
                else
                {
                    context.Report(DiagnosticSeverity.Error, "uuencode.truncated",
                        $"The input ends inside the block for \"{block.NameText}\"; the rest of the file is missing.", text.Length);
                }
                files.Add(block.ToFile());
            }
            if (files.Count == 0) throw new InvalidDataException("No uuencode begin line found.");
            return files;
        }

        // One line of historical uuencode: a count character, then 4 characters per 3 bytes, each (c - $20) & $3F, so
        // the backquote and the space both stand for 0. [Doc]
        private static void DecodeUuLine(ReadOnlySpan<byte> line, long offset, Block block, ContainerContext context)
        {
            // A blank line is the zero-count line with its space stripped by a mailer. [Fitted]
            if (IsBlank(line))
            {
                block.Ended = true;
                return;
            }
            foreach (var c in line)
            {
                if (c is < 0x20 or > 0x60)
                {
                    context.Report(DiagnosticSeverity.Error, "uuencode.bad-line",
                        $"A line in \"{block.NameText}\" has character ${c:X2}, outside the uuencode range; the line is skipped.", offset);
                    return;
                }
            }
            var count = Value(line[0]);
            if (count == 0)
            {
                block.Ended = true;
                return;
            }
            // Characters a mailer stripped from the end (trailing spaces, so zeros) are taken as zeros; characters past
            // the count's groups (some encoders add a check character) are ignored. [Fitted]
            var groups = (count + 2) / 3;
            for (var g = 0; g < groups; g++)
            {
                var at = 1 + g * 4;
                int a = Char(line, at), b = Char(line, at + 1), c = Char(line, at + 2), d = Char(line, at + 3);
                var bytes = a << 18 | b << 12 | c << 6 | d;
                for (var k = 0; k < 3 && g * 3 + k < count; k++) block.Data.WriteByte((byte)(bytes >> (16 - 8 * k)));
            }
        }

        private static int Char(ReadOnlySpan<byte> line, int at) => at < line.Length ? Value(line[at]) : 0;

        private static int Value(byte c) => (c - 0x20) & 0x3F;

        // One line of base64 (RFC 2045's alphabet, as POSIX uuencode -m writes it); the line "====" ends the block. [Doc]
        private static void DecodeBase64Line(ReadOnlySpan<byte> line, long offset, Block block, ContainerContext context)
        {
            line = line.TrimEnd(" \t"u8);
            if (line.SequenceEqual("===="u8))
            {
                block.Ended = true;
                return;
            }
            foreach (var c in line)
            {
                if (c != '=' && (c >= Base64Values.Length || Base64Values[c] < 0))
                {
                    context.Report(DiagnosticSeverity.Error, "uuencode.bad-line",
                        $"A line in \"{block.NameText}\" has character ${c:X2}, outside the base64 alphabet; the line is skipped.", offset);
                    return;
                }
            }
            foreach (var c in line)
            {
                if (c == '=')
                {
                    // Padding ends a quantum; the bits left over are dropped. [Doc]
                    block.Bits = 0;
                    block.Buffer = 0;
                    continue;
                }
                block.Buffer = block.Buffer << 6 | (byte)Base64Values[c];
                block.Bits += 6;
                if (block.Bits >= 8)
                {
                    block.Bits -= 8;
                    block.Data.WriteByte((byte)(block.Buffer >> block.Bits));
                    block.Buffer &= (1 << block.Bits) - 1;
                }
            }
        }

        // "begin" or "begin-base64", one space, an octal mode, one space, the name (to the end of the line). [Doc]
        private static bool ParseBegin(ReadOnlySpan<byte> line, out bool base64, out byte[] name)
        {
            name = [];
            base64 = line.StartsWith("begin-base64 "u8);
            if (!base64 && !line.StartsWith("begin "u8)) return false;
            var rest = line[(base64 ? 13 : 6)..];
            var digits = 0;
            while (digits < rest.Length && rest[digits] is >= (byte)'0' and <= (byte)'7') digits++;
            if (digits is 0 or > 6 || digits >= rest.Length || rest[digits] != ' ') return false;
            var path = rest[(digits + 1)..].TrimEnd(" \t"u8);
            if (path.IsEmpty) return false;
            // The name is a host path; ClassicMac keeps its last component, cut to 255 bytes.
            var slash = path.LastIndexOf((byte)'/');
            if (slash >= 0 && slash < path.Length - 1) path = path[(slash + 1)..];
            name = path[..Math.Min(path.Length, 255)].ToArray();
            return true;
        }

        private static bool IsBlank(ReadOnlySpan<byte> line) => line.TrimEnd(" \t"u8).IsEmpty;

        // The lines of the text: (start, end without the line break). CR LF, CR and LF all end a line.
        private static IEnumerable<(int Start, int End)> Lines(byte[] text)
        {
            var start = 0;
            while (start < text.Length)
            {
                var length = text.AsSpan(start).IndexOfAny((byte)'\r', (byte)'\n');
                if (length < 0)
                {
                    yield return (start, text.Length);
                    yield break;
                }
                var end = start + length;
                var next = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
                yield return (start, end);
                start = next;
            }
        }

        private static sbyte[] BuildBase64Values()
        {
            var values = new sbyte[128];
            Array.Fill(values, (sbyte)-1);
            for (var i = 0; i < Base64Alphabet.Length; i++) values[Base64Alphabet[i]] = (sbyte)i;
            return values;
        }

        private sealed class Block(bool base64, byte[] name)
        {
            public bool Base64 { get; } = base64;
            public MemoryStream Data { get; } = new();
            public bool Ended { get; set; }
            public int Buffer { get; set; }
            public int Bits { get; set; }
            public string NameText => new MacString(name).ToMacRoman();

            public MacFile ToFile() => new()
            {
                Name = new MacString(name),
                DataFork = ForkData.FromBytes(Data.ToArray()),
            };
        }
    }
}
