using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>Bytes the initializer copies into the globals.</summary>
    /// <param name="A5Offset">Where they go, from A5 (negative: below A5).</param>
    /// <param name="Bytes">The bytes.</param>
    public sealed record MpwDataRun(int A5Offset, ReadOnlyMemory<byte> Bytes);

    /// <summary>
    /// MPW's global-data initializer, the data at the end of the <c>%A5Init</c> segment: its trailer (the header's
    /// offset, then <c>'mpwd'</c>), the header, the packed initial values and the relocations
    /// [Code: the <c>%A5Init</c> routines of an MPW application; Verified: MPW near and far applications].
    /// </summary>
    public sealed class MpwA5Init
    {
        private const int TrailerLength = 8;
        private const int HeaderLength = 16;

        private MpwA5Init() { }

        /// <summary>Where the header is in the segment.</summary>
        public int HeaderOffset { get; private init; }

        /// <summary>How many bytes below A5 are cleared and initialized; they start at A5 minus this.</summary>
        public uint BelowA5Size { get; private init; }

        /// <summary>The header's version (1; any other makes the initializer fail).</summary>
        public ushort Version { get; private init; }

        /// <summary>Where the packed data starts in the segment.</summary>
        public long DataOffset { get; private init; }

        /// <summary>Where the relocations start in the segment.</summary>
        public long RelocationOffset { get; private init; }

        /// <summary>Where the packed data ended (its terminator read).</summary>
        public long DataEnd { get; private init; }

        /// <summary>Where the relocations ended (their terminator read).</summary>
        public long RelocationEnd { get; private init; }

        /// <summary>The copied runs, in order; a repeated run appears once per repeat.</summary>
        public IReadOnlyList<MpwDataRun> Runs { get; private init; } = [];

        /// <summary>The relocated longs, from A5: each gets A5 added.</summary>
        public IReadOnlyList<int> Relocations { get; private init; } = [];

        /// <summary>Whether the segment ends with the <c>'mpwd'</c> trailer.</summary>
        public static bool HasTrailer(ReadOnlySpan<byte> segment) =>
            segment.Length >= TrailerLength && segment[^4..].SequenceEqual("mpwd"u8);

        /// <summary>
        /// Reads the initializer data of a <c>%A5Init</c> segment. Damage past the trailer is reported (<c>m68k.a5init-*</c>)
        /// and read as far as it goes.
        /// </summary>
        /// <exception cref="InvalidDataException">The segment does not end with the <c>'mpwd'</c> trailer.</exception>
        public static MpwA5Init Read(ReadOnlyMemory<byte> segment, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            if (!HasTrailer(segment.Span))
            {
                throw new InvalidDataException("Not an MPW %A5Init segment: it does not end with the 'mpwd' trailer.");
            }

            var reader = new BigEndianReader(segment);
            int trailer = reader.Length - TrailerLength;
            uint headerOffset = reader.ReadUInt32At(trailer);
            if (trailer < HeaderLength || headerOffset > (uint)(trailer - HeaderLength))
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.a5init-header",
                    $"The %A5Init header at {headerOffset:X} does not fit before the trailer.", trailer));
                return new MpwA5Init { HeaderOffset = (int)Math.Min(headerOffset, int.MaxValue) };
            }
            int h = (int)headerOffset;
            reader.Position = h;
            uint below = reader.ReadUInt32();
            ushort version = reader.ReadUInt16();
            reader.Skip(2);
            long dataOffset = h + (long)reader.ReadUInt32();
            long relocationOffset = h + (long)reader.ReadUInt32();
            if (version != 1)
            {
                // The initializer returns −1 without touching the globals.
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.a5init-version",
                    $"The %A5Init header's version is {version}; only 1 is read (the initializer fails on others).", h));
                return new MpwA5Init
                {
                    HeaderOffset = h,
                    BelowA5Size = below,
                    Version = version,
                    DataOffset = dataOffset,
                    RelocationOffset = relocationOffset,
                };
            }
            var (runs, dataEnd) = ReadData(reader, dataOffset, below, diagnostics);
            var (relocations, relocationEnd) = ReadRelocations(reader, relocationOffset, below, diagnostics);
            return new MpwA5Init
            {
                HeaderOffset = h,
                BelowA5Size = below,
                Version = version,
                DataOffset = dataOffset,
                RelocationOffset = relocationOffset,
                DataEnd = dataEnd,
                RelocationEnd = relocationEnd,
                Runs = runs,
                Relocations = relocations,
            };
        }

        // The data unpacker: each run's first byte holds a count (low nibble, in words; 0 = a varint in bytes, 0 again
        // = the end) and a skip (high nibble, in words; 0 = a varint in bytes). A varint's repeat form repeats the
        // skip-and-copy. The destination starts at A5 − belowA5. A repeat of 0 is done once, then the routine's count
        // (SUBQ.L #1; BNE) wraps and it runs away: reported, and the data stops there.
        private static (List<MpwDataRun> Runs, long End) ReadData(BigEndianReader reader, long offset, uint below,
            ICollection<Diagnostic> diagnostics)
        {
            var runs = new List<MpwDataRun>();
            if (offset >= reader.Length)
            {
                Truncated(diagnostics, "packed data", offset);
                return (runs, offset);
            }
            reader.Position = (int)offset;
            long q = 0;
            try
            {
                while (true)
                {
                    uint repeat = 1;
                    int at = reader.Position;
                    byte b = reader.ReadByte();
                    long n = b & 0x0F;
                    if (n == 0)
                    {
                        n = ReadVarint(reader, out var r);
                        if (r is { } rn)
                        {
                            repeat = rn;
                        }

                        if (n == 0)
                        {
                            break;
                        }
                    }
                    else
                    {
                        n *= 2;
                    }

                    long skip = b & 0xF0;
                    if (skip == 0)
                    {
                        skip = ReadVarint(reader, out var r);
                        if (r is { } rs)
                        {
                            repeat = rs;
                        }
                    }
                    else
                    {
                        skip = (b >> 4) * 2;
                    }

                    for (uint i = 0; i < Math.Max(repeat, 1u); i++)
                    {
                        q += skip;
                        if (q + n > below)
                        {
                            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.a5init-data-range",
                                $"A data run at {q:X} from the globals' start runs past the {below}-byte globals; the data stopped.", at));
                            return (runs, reader.Position);
                        }
                        if (n > reader.Remaining)
                        {
                            throw new EndOfStreamException();
                        }

                        runs.Add(new MpwDataRun((int)(q - below), reader.Source.Slice(reader.Position, (int)n)));
                        reader.Skip((int)n);
                        q += n;
                    }
                    if (repeat == 0)
                    {
                        CountZero(diagnostics, "data run", at);
                        return (runs, reader.Position);
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Truncated(diagnostics, "packed data", reader.Position);
            }
            return (runs, reader.Position);
        }

        // The relocator: a position from A5 − belowA5 steps by twice each delta: a byte below $80; $80–$FF with the next
        // byte (15 bits); 0 then a byte below $80 and a varint repeat count; 0 then a 4-byte delta whose first byte has
        // its top bit set (doubled in 32 bits, so that bit drops out); 0 0 ends. A count of 0 patches once, then runs
        // away like a repeat of 0: reported, and the relocations stop there.
        private static (List<int> Relocations, long End) ReadRelocations(BigEndianReader reader, long offset, uint below,
            ICollection<Diagnostic> diagnostics)
        {
            var relocations = new List<int>();
            if (offset >= reader.Length)
            {
                Truncated(diagnostics, "relocations", offset);
                return (relocations, offset);
            }
            reader.Position = (int)offset;
            uint position = 0;
            int outside = 0;
            long firstOutside = 0;
            int firstOutsideAt = 0;
            try
            {
                while (true)
                {
                    int at = reader.Position;
                    uint count = 1;
                    uint delta;
                    byte b = reader.ReadByte();
                    if (b == 0)
                    {
                        b = reader.ReadByte();
                        if (b == 0)
                        {
                            break;
                        }

                        if ((b & 0x80) != 0)
                        {
                            reader.Position--;
                            delta = reader.ReadUInt32();
                        }
                        else
                        {
                            delta = b;
                            count = ReadVarint(reader, out _);
                        }
                    }
                    else if ((b & 0x80) != 0)
                    {
                        delta = (uint)((b & 0x7F) << 8) | reader.ReadByte();
                    }
                    else
                    {
                        delta = b;
                    }
                    // More positions than the globals have words (a delta of 0 repeated, say) cannot be real: reported, and
                    // reading stops.
                    if (count > below / 2)
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.a5init-reloc-range",
                            $"A relocation count of {count} is more than the {below}-byte globals hold; reading stopped.", at));
                        break;
                    }
                    for (uint i = 0; i < Math.Max(count, 1u); i++)
                    {
                        position = unchecked(position + 2 * delta);
                        if (position > below - 4L)
                        {
                            if (outside++ == 0)
                            {
                                (firstOutside, firstOutsideAt) = (position, at);
                            }

                            continue;
                        }
                        relocations.Add((int)(position - (long)below));
                    }
                    if (count == 0)
                    {
                        CountZero(diagnostics, "relocation", at);
                        break;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Truncated(diagnostics, "relocations", reader.Position);
            }
            if (outside > 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.a5init-reloc-range",
                    $"{outside} relocations, the first at {firstOutside:X} from the globals' start, lie outside the {below}-byte globals; left out.",
                    firstOutsideAt));
            }

            return (relocations, reader.Position);
        }

        private static void CountZero(ICollection<Diagnostic> diagnostics, string what, long at) =>
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.a5init-count-zero",
                $"A %A5Init {what} has a count of 0: the routine does it once, then its count wraps and it runs away; reading stopped.", at));

        private static void Truncated(ICollection<Diagnostic> diagnostics, string what, long at) =>
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.a5init-truncated",
                $"The %A5Init {what} run past the end of the segment.", at));

        // MPW's varint: 0xxxxxxx; 10xxxxxx + 1 byte (14 bits); 110xxxxx + 2 bytes (21 bits); 1110xxxx + 4 bytes; 1111xxxx:
        // a value varint then a repeat-count varint.
        internal static uint ReadVarint(BigEndianReader reader, out uint? repeat)
        {
            repeat = null;
            byte b = reader.ReadByte();
            if (b < 0x80)
            {
                return b;
            }

            if ((b & 0xC0) == 0x80)
            {
                return (uint)((b & 0x3F) << 8) | reader.ReadByte();
            }

            if ((b & 0xE0) == 0xC0)
            {
                return (uint)((b & 0x1F) << 16) | reader.ReadUInt16();
            }

            if ((b & 0xF0) == 0xE0)
            {
                return reader.ReadUInt32();
            }

            uint value = ReadVarint(reader, out _);
            repeat = ReadVarint(reader, out _);
            return value;
        }
    }
}
