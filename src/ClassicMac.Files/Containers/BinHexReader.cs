using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Containers
{
    /// <summary>
    /// BinHex 4.0 (.hqx): a Mac file as 7-bit text. There is no Apple specification and no Mac OS code for it; the
    /// layout follows Peter Lewis's BinHex 4.0 definition (also RFC 1741). After the line
    /// "(This file must be converted with BinHex 4.0)" the text between two colons encodes 6 bits per character; the
    /// bytes are run-length encoded with $90; the result is a header (name, type, creator, flags, fork lengths), the
    /// data fork and the resource fork, each followed by a CRC-16/XMODEM.
    /// </summary>
    public sealed class BinHexReader : IContainerReader
    {
        private const string Marker = "(This file must be converted with BinHex";

        // How far into a file the marker line is looked for: mail and news headers precede it. Fitted to real
        // downloads, not a limit to tune.
        private const int SearchLength = 64 * 1024;

        private const string Alphabet = "!\"#$%&'()*+,-012345689@ABCDEFGHIJKLMNPQRSTUVXYZ[`abcdefhijklmpqr";
        private const byte RunMarker = 0x90;
        private static readonly sbyte[] Values = BuildValues();

        /// <summary>The reader.</summary>
        public static BinHexReader Instance { get; } = new();

        private BinHexReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "BinHex 4.0";

        /// <inheritdoc/>
        public bool CanRead(ForkData input)
        {
            // Only text may precede the marker: one after binary data is a .hqx file inside a disk image or archive.
            // docs/formats/containers/binhex.md §5. [ClassicMac]
            var text = input.ReadPrefix(SearchLength);
            return FindStart(text, out var marker) >= 0 && PlainText.IsText(text.AsSpan(0, marker));
        }

        // The position just after the colon that opens the encoded text, or -1; and where the marker is.
        private static int FindStart(ReadOnlySpan<byte> text, out int marker)
        {
            marker = text.IndexOf(Encoding.ASCII.GetBytes(Marker));
            if (marker < 0)
            {
                return -1;
            }

            var lineEnd = text[marker..].IndexOfAny((byte)'\r', (byte)'\n');
            if (lineEnd < 0)
            {
                return -1;
            }

            var colon = text[(marker + lineEnd)..].IndexOf((byte)':');
            return colon < 0 ? -1 : marker + lineEnd + colon + 1;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            var limit = context.Options.MaxExpandedBytesPerInput;
            var text = input.ToArray(limit);
            var start = FindStart(text.AsSpan(0, Math.Min(text.Length, SearchLength)), out _);
            if (start < 0)
            {
                throw new InvalidDataException("No BinHex 4.0 data found.");
            }

            var decoded = Expand(Decode(text, start, context), limit, context);
            var d = decoded.AsSpan();

            // Header: name length, name, version (0), type, creator, flags, data length, resource length, CRC.
            if (d.Length < 1 || d[0] is < 1 or > 63 || d.Length < 1 + d[0] + 1 + 20)
            {
                throw new InvalidDataException("The BinHex header is missing or has no valid name.");
            }

            var nameLength = d[0];
            var header = 1 + nameLength + 1;
            if (d[1 + nameLength] != 0)
            {
                context.Report(DiagnosticSeverity.Info, "binhex.version",
                    $"The header's version byte is {d[1 + nameLength]}, not 0.");
            }
            var fields = decoded.AsMemory(header);
            var reader = new BigEndianReader(fields);
            var finderInfo = new FinderInfo
            {
                Type = new FourCC(fields.Span[..4]),
                Creator = new FourCC(fields.Span[4..8]),
                Flags = (FinderFlags)reader.ReadUInt16At(8),
            };
            long dataLength = reader.ReadUInt32At(10);
            long resourceLength = reader.ReadUInt32At(14);
            var headerEnd = header + 18;
            CheckCrc(d[..headerEnd], decoded, headerEnd, "header", context);

            var dataStart = headerEnd + 2;
            var data = Fork(decoded, dataStart, dataLength, "data", context);
            var resourceStart = dataStart + dataLength + 2;
            var resource = Fork(decoded, resourceStart, resourceLength, "resource", context);

            return
            [
                new MacFile
                {
                    Name = new MacString(d.Slice(1, nameLength)),
                    FinderInfo = finderInfo,
                    DataFork = data,
                    ResourceFork = resource,
                },
            ];
        }

        // Six bits per character from the alphabet; line breaks and other whitespace are skipped; a colon ends it.
        private static byte[] Decode(byte[] text, int start, ContainerContext context)
        {
            var output = new MemoryStream(text.Length * 3 / 4);
            int buffer = 0, bits = 0;
            for (var i = start; ; i++)
            {
                if (i >= text.Length)
                {
                    context.Report(DiagnosticSeverity.Error, "binhex.truncated",
                        "The encoded text ends without its closing colon.", i);
                    break;
                }
                var c = text[i];
                if (c == ':')
                {
                    break;
                }

                if (c is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
                {
                    continue;
                }

                var value = c < Values.Length ? Values[c] : -1;
                if (value < 0)
                {
                    context.Report(DiagnosticSeverity.Error, "binhex.bad-character",
                        $"Character ${c:X2} is not in the BinHex alphabet; decoding stops.", i);
                    break;
                }
                buffer = buffer << 6 | value;
                bits += 6;
                if (bits >= 8)
                {
                    bits -= 8;
                    output.WriteByte((byte)(buffer >> bits));
                }
            }
            return output.ToArray();
        }

        // $90 n repeats the previous byte to n copies in all; $90 0 is a literal $90.
        private static byte[] Expand(byte[] packed, long limit, ContainerContext context)
        {
            var output = new MemoryStream(packed.Length);
            byte last = 0;
            for (var i = 0; i < packed.Length; i++)
            {
                var b = packed[i];
                if (b != RunMarker)
                {
                    output.WriteByte(b);
                    last = b;
                    continue;
                }
                if (i + 1 >= packed.Length)
                {
                    context.Report(DiagnosticSeverity.Error, "binhex.truncated", "The data ends inside a run.");
                    break;
                }
                var count = packed[++i];
                if (count == 0)
                {
                    output.WriteByte(RunMarker);
                    last = RunMarker;
                }
                else
                {
                    for (var n = 1; n < count; n++)
                    {
                        output.WriteByte(last);
                    }
                }
                if (output.Length > limit)
                {
                    throw new InvalidDataException($"BinHex data expands past the {limit}-byte limit.");
                }
            }
            return output.ToArray();
        }

        private static ForkData Fork(byte[] decoded, long start, long length, string which, ContainerContext context)
        {
            var available = Math.Max(0, decoded.Length - start);
            if (length > available)
            {
                context.Report(DiagnosticSeverity.Error, "binhex.fork-truncated",
                    $"The {which} fork claims {length} bytes but only {available} were decoded; the rest is missing.");
                return ForkData.FromBytes(decoded.AsMemory((int)Math.Min(start, decoded.Length), (int)available));
            }
            CheckCrc(decoded.AsSpan((int)start, (int)length), decoded, start + length, $"{which} fork", context);
            return ForkData.FromBytes(decoded.AsMemory((int)start, (int)length));
        }

        private static void CheckCrc(ReadOnlySpan<byte> covered, ReadOnlyMemory<byte> all, long at, string what, ContainerContext context)
        {
            if (at + 2 > all.Length)
            {
                context.Report(DiagnosticSeverity.Error, "binhex.truncated", $"The {what} CRC is missing.");
                return;
            }
            var stored = new BigEndianReader(all).ReadUInt16At((int)at);
            var computed = Crc16.Compute(covered);
            if (stored != computed)
            {
                context.Report(DiagnosticSeverity.Warning, "binhex.crc",
                    $"The {what} CRC is ${stored:X4} but the data gives ${computed:X4}.");
            }
        }

        private static sbyte[] BuildValues()
        {
            var values = new sbyte[128];
            Array.Fill(values, (sbyte)-1);
            for (var i = 0; i < Alphabet.Length; i++)
            {
                values[Alphabet[i]] = (sbyte)i;
            }

            return values;
        }
    }
}
