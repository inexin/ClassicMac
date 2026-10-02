using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Code.M68k
{
    /// <summary>Bytes CodeWarrior's startup writes into the globals.</summary>
    /// <param name="A5Offset">Where they go, from A5.</param>
    /// <param name="Bytes">The bytes.</param>
    public sealed record CodeWarriorRun(int A5Offset, ReadOnlyMemory<byte> Bytes);

    /// <summary>One block of packed globals.</summary>
    /// <param name="Start">Where the block starts, from A5.</param>
    /// <param name="End">Where its last op left the position, from A5.</param>
    /// <param name="Runs">The bytes written, in order.</param>
    public sealed record CodeWarriorBlock(int Start, int End, IReadOnlyList<CodeWarriorRun> Runs);

    /// <summary>Which of the six relocation lists: what is patched and what is added.</summary>
    public enum CodeWarriorRelocationKind
    {
        /// <summary>Longs in the globals that get A5 added.</summary>
        GlobalsPlusA5,
        /// <summary>Longs in the globals that get the code's address added.</summary>
        GlobalsPlusCode,
        /// <summary>Longs in the globals that get the globals' address added.</summary>
        GlobalsPlusGlobals,
        /// <summary>Longs in the code that get A5 added.</summary>
        CodePlusA5,
        /// <summary>Longs in the code that get the code's address added.</summary>
        CodePlusCode,
        /// <summary>The sixth list: longs in the code that get the code's address added.</summary>
        CodePlusCodeSecond,
    }

    /// <summary>One relocation list.</summary>
    /// <param name="Kind">Which list.</param>
    /// <param name="Offsets">The patched longs' offsets, as decoded (code offsets are resource offsets in <c>'CODE'</c> 1).</param>
    public sealed record CodeWarriorRelocations(CodeWarriorRelocationKind Kind, IReadOnlyList<int> Offsets);

    /// <summary>
    /// CodeWarrior 68k's <c>'DATA'</c> 0: the offset of the code relocations, three blocks of packed globals and six
    /// relocation lists [Code: the CodeWarrior startup of a single-segment 68k application; Verified: one such application].
    /// </summary>
    public sealed class CodeWarriorData
    {
        private const int BlockCount = 3;
        private const int RelocationListCount = 6;

        private CodeWarriorData() { }

        /// <summary>The first long: where the code relocation lists (the fourth list on) start in the resource.</summary>
        public uint CodeRelocationOffset { get; private init; }

        /// <summary>The three blocks.</summary>
        public IReadOnlyList<CodeWarriorBlock> Blocks { get; private init; } = [];

        /// <summary>The six relocation lists, in stored order.</summary>
        public IReadOnlyList<CodeWarriorRelocations> Relocations { get; private init; } = [];

        /// <summary>How many bytes were read.</summary>
        public long End { get; private init; }

        /// <summary>
        /// Reads a CodeWarrior <c>'DATA'</c> 0. Damage is reported (<c>m68k.cw-*</c>) and read as far as it goes.
        /// </summary>
        /// <exception cref="System.IO.InvalidDataException">The resource is shorter than its first long.</exception>
        public static CodeWarriorData Read(ReadOnlyMemory<byte> data, ICollection<Diagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(diagnostics);
            var reader = new BigEndianReader(data);
            if (reader.Length < 4)
                throw new InvalidDataException($"A CodeWarrior 'DATA' 0 starts with a long; this one has {reader.Length} bytes.");
            uint codeRelocationOffset = reader.ReadUInt32();
            var blocks = new List<CodeWarriorBlock>(BlockCount);
            var relocations = new List<CodeWarriorRelocations>(RelocationListCount);
            try
            {
                for (int i = 0; i < BlockCount; i++)
                {
                    var (block, ok) = ReadBlock(reader, diagnostics);
                    blocks.Add(block);
                    if (!ok) return Result();
                }
                for (int i = 0; i < RelocationListCount; i++)
                {
                    if (i == (int)CodeWarriorRelocationKind.CodePlusA5 && reader.Position != codeRelocationOffset)
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.cw-code-reloc-offset",
                            $"The code relocations start at {reader.Position:X}, not at {codeRelocationOffset:X} as the first long says.", reader.Position));
                    relocations.Add(new CodeWarriorRelocations((CodeWarriorRelocationKind)i, ReadRelocations(reader, diagnostics)));
                }
            }
            catch (EndOfStreamException)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.cw-data-truncated",
                    $"The 'DATA' 0 ends inside its {(blocks.Count < BlockCount ? "packed globals" : "relocation lists")}.", reader.Position));
            }
            return Result();

            CodeWarriorData Result() => new()
            {
                CodeRelocationOffset = codeRelocationOffset, Blocks = blocks, Relocations = relocations, End = reader.Position,
            };
        }

        // A block: its start from A5, then ops to 00: 1xxxxxxx copy (b & $7F) + 1 bytes; 01xxxxxx skip (b & $3F) + 1;
        // 001xxxxx fill the next byte (b & $1F) + 2 times; 0001xxxx fill $FF (b & $0F) + 1 times; 01–04 fixed 8-byte
        // shapes; 05–0F make the startup call SysError 15. Returns false after such an op.
        private static (CodeWarriorBlock Block, bool Ok) ReadBlock(BigEndianReader reader, ICollection<Diagnostic> diagnostics)
        {
            int start = reader.ReadInt32();
            long q = start;
            var runs = new List<CodeWarriorRun>();
            while (true)
            {
                int at = reader.Position;
                byte b = reader.ReadByte();
                if ((b & 0x80) != 0) Literal((b & 0x7F) + 1, 0);
                else if ((b & 0x40) != 0) q += (b & 0x3F) + 1;
                else if ((b & 0x20) != 0)
                {
                    var fill = new byte[(b & 0x1F) + 2];
                    Array.Fill(fill, reader.ReadByte());
                    Write(fill);
                }
                else if ((b & 0x10) != 0)
                {
                    var fill = new byte[(b & 0x0F) + 1];
                    Array.Fill(fill, (byte)0xFF);
                    Write(fill);
                }
                else if (b == 0) break;
                else if (b == 1) { q += 4; Prefixed([0xFF, 0xFF], 2); }       // skip 4, FF FF, 2 literals
                else if (b == 2) { q += 4; Prefixed([0xFF], 3); }             // skip 4, FF, 3 literals
                else if (b == 3) { Write([0xA9, 0xF0]); Literal(2, 2); Literal(1, 1); } // A9F0, skip 2, 2, skip 1, 1
                else if (b == 4) { Write([0xA9, 0xF0]); Literal(3, 1); Literal(1, 1); } // A9F0, skip 1, 3, skip 1, 1
                else
                {
                    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "m68k.cw-data-op",
                        $"Packed-globals op {b:X2} is not defined (the startup calls SysError 15); reading stopped.", at));
                    return (new CodeWarriorBlock(start, (int)q, runs), false);
                }
            }
            return (new CodeWarriorBlock(start, (int)q, runs), true);

            void Write(byte[] bytes)
            {
                runs.Add(new CodeWarriorRun((int)q, bytes));
                q += bytes.Length;
            }

            void Literal(int count, int skipFirst)
            {
                q += skipFirst;
                if (count > reader.Remaining) throw new EndOfStreamException();
                runs.Add(new CodeWarriorRun((int)q, reader.Source.Slice(reader.Position, count)));
                reader.Skip(count);
                q += count;
            }

            void Prefixed(byte[] prefix, int count)
            {
                var bytes = new byte[prefix.Length + count];
                prefix.CopyTo(bytes, 0);
                reader.ReadBytes(count).CopyTo(bytes.AsSpan(prefix.Length));
                Write(bytes);
            }
        }

        // A list: a count, then per entry 1xxxxxxx (a signed 7-bit delta, doubled), 01xxxxxx xxxxxxxx (a signed 14-bit
        // delta, doubled) or 00xxxxxx and 3 bytes (a signed 30-bit offset, doubled: absolute). The startup loops while the
        // count is above 0, signed (TST.L; BGT): a count with its top bit set reads no entries.
        private static IReadOnlyList<int> ReadRelocations(BigEndianReader reader, ICollection<Diagnostic> diagnostics)
        {
            int at = reader.Position;
            int count = reader.ReadInt32();
            if (count < 0)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "m68k.cw-reloc-count",
                    $"A relocation list's count {(uint)count:X8} is negative; the startup reads no entries for it.", at));
                return [];
            }
            if (count > reader.Remaining) throw new EndOfStreamException();
            var offsets = new List<int>(count);
            int d = 0;
            for (int i = 0; i < count; i++)
            {
                byte b = reader.ReadByte();
                if ((b & 0x80) != 0) d += (sbyte)(b << 1);
                else if ((b & 0x40) != 0) d += (short)(((b << 8) | reader.ReadByte()) << 2) >> 1;
                else
                {
                    reader.Position--;
                    d = (int)(reader.ReadUInt32() << 2) >> 1;
                }
                offsets.Add(d);
            }
            return offsets;
        }
    }
}
