using ClassicMac.Core;

namespace ClassicMac.Resources.Compression;

// System 'dcmp' 0 and 1 (version-8 form). Read in the Mac OS 9.0 System's dcmp 0/1 and checked by emulation;
// both share their memo-table and extension code and differ in the opcode map and constant table:
//   dcmp 0: 00 varint v → (2v)&$FFFF literal bytes; 01–0F n×2; 10 = 00 + remember; 11–1F (n−$10)×2 + remember;
//           20 slot u8+$28; 21 slot u8+$128; 22 slot (u16+$28)&$FFFF; 23–4A slot n−$23; 4B–FD constant word.
//   dcmp 1: 00–0F n+1 literal; 10–1F n−$0F + remember; 20–CF slot n−$20; D0 varint v → v&$FFFF literal; D1 + remember;
//           D2 slot u8+$B0; D3 slot u8+$1B0; D4 slot (u16+$B0)&$FFFF; D5–FD constant word.
//   Both: FE extension, FF end (the only terminator; the declared size is never checked).
internal sealed class Dcmp01(short id) : IResourceDecompressor
{
    // dcmp 0's words for opcodes 4B–FD (immediates in the dispatch table at 0x200; identical to resource_dasm).
    private static readonly ushort[] Table0 =
    [
        0x0000, 0x4EBA, 0x0008, 0x4E75, 0x000C, 0x4EAD, 0x2053, 0x2F0B, 0x6100, 0x0010, 0x7000, 0x2F00, 0x486E, 0x2050, 0x206E, 0x2F2E,
        0xFFFC, 0x48E7, 0x3F3C, 0x0004, 0xFFF8, 0x2F0C, 0x2006, 0x4EED, 0x4E56, 0x2068, 0x4E5E, 0x0001, 0x588F, 0x4FEF, 0x0002, 0x0018,
        0x6000, 0xFFFF, 0x508F, 0x4E90, 0x0006, 0x266E, 0x0014, 0xFFF4, 0x4CEE, 0x000A, 0x000E, 0x41EE, 0x4CDF, 0x48C0, 0xFFF0, 0x2D40,
        0x0012, 0x302E, 0x7001, 0x2F28, 0x2054, 0x6700, 0x0020, 0x001C, 0x205F, 0x1800, 0x266F, 0x4878, 0x0016, 0x41FA, 0x303C, 0x2840,
        0x7200, 0x286E, 0x200C, 0x6600, 0x206B, 0x2F07, 0x558F, 0x0028, 0xFFFE, 0xFFEC, 0x22D8, 0x200B, 0x000F, 0x598F, 0x2F3C, 0xFF00,
        0x0118, 0x81E1, 0x4A00, 0x4EB0, 0xFFE8, 0x48C7, 0x0003, 0x0022, 0x0007, 0x001A, 0x6706, 0x6708, 0x4EF9, 0x0024, 0x2078, 0x0800,
        0x6604, 0x002A, 0x4ED0, 0x3028, 0x265F, 0x6704, 0x0030, 0x43EE, 0x3F00, 0x201F, 0x001E, 0xFFF6, 0x202E, 0x42A7, 0x2007, 0xFFFA,
        0x6002, 0x3D40, 0x0C40, 0x6606, 0x0026, 0x2D48, 0x2F01, 0x70FF, 0x6004, 0x1880, 0x4A40, 0x0040, 0x002C, 0x2F08, 0x0011, 0xFFE4,
        0x2140, 0x2640, 0xFFF2, 0x426E, 0x4EB9, 0x3D7C, 0x0038, 0x000D, 0x6006, 0x422E, 0x203C, 0x670C, 0x2D68, 0x6608, 0x4A2E, 0x4AAE,
        0x002E, 0x4840, 0x225F, 0x2200, 0x670A, 0x3007, 0x4267, 0x0032, 0x2028, 0x0009, 0x487A, 0x0200, 0x2F2B, 0x0005, 0x226E, 0x6602,
        0xE580, 0x670E, 0x660A, 0x0050, 0x3E00, 0x660C, 0x2E00, 0xFFEE, 0x206D, 0x2040, 0xFFE0, 0x5340, 0x6008, 0x0480, 0x0068, 0x0B7C,
        0x4400, 0x41E8, 0x4841,
    ];

    // dcmp 1's words for opcodes D5–FD.
    private static readonly ushort[] Table1 =
    [
        0x0000, 0x0001, 0x0002, 0x0003, 0x2E01, 0x3E01, 0x0101, 0x1E01, 0xFFFF, 0x0E01, 0x3100, 0x1112, 0x0107, 0x3332, 0x1239, 0xED10,
        0x0127, 0x2322, 0x0137, 0x0706, 0x0117, 0x0123, 0x00FF, 0x002F, 0x070E, 0xFD3C, 0x0135, 0x0115, 0x0102, 0x0007, 0x003E, 0x05D5,
        0x0201, 0x0607, 0x0708, 0x3001, 0x0133, 0x0010, 0x1716, 0x373E, 0x3637,
    ];

    public short Id => id;

    public int Decompress(DecompressionContext context)
    {
        var header = context.Header;
        if (!header.IsVersion8)
        {
            throw new DecompressionFormException($"'dcmp' {id} has the version-8 entry point but the resource uses a version-9 header.");
        }

        var cursor = new BlockCursor(context);
        var memo = new MemoTable(WorkSize(header, context.Options.ResourceManager), cursor, context);
        if (id == 0)
        {
            Run0(cursor, memo);
        }
        else
        {
            Run1(cursor, memo);
        }

        return cursor.Written;
    }

    // The working buffer: n = frac ? (block·(frac+1))>>8 : 0, block including the expansion bytes. ROM
    // ($FFC7A34A) passes n+4 (32-bit multiply, logical shift); Mac OS 9 ($1000198C) passes n+2 (mullw, srawi).
    private static long WorkSize(CompressedResourceHeader header, ResourceManagerModel model)
    {
        var block = (uint)(header.DecompressedSize + header.ExpansionBytes);
        var frac = header.WorkingBufferFraction;
        if (model == ResourceManagerModel.Rom68k)
        {
            return (frac == 0 ? 0 : unchecked(block * (uint)(frac + 1)) >> 8) + 4;
        }

        return (frac == 0 ? 0 : unchecked((int)(block * (uint)(frac + 1))) >> 8) + 2;
    }

    private static void Run0(BlockCursor cursor, MemoTable memo)
    {
        for (; ; )
        {
            var op = cursor.ReadByte();
            switch (op)
            {
                case 0x00:
                    cursor.CopyLiteral((2 * ReadVarint(cursor)) & 0xFFFF);
                    break;
                case < 0x10:
                    cursor.CopyLiteral(op * 2);
                    break;
                case 0x10:
                    RememberAndCopy(cursor, memo, (2 * ReadVarint(cursor)) & 0xFFFF);
                    break;
                case < 0x20:
                    RememberAndCopy(cursor, memo, (op - 0x10) * 2);
                    break;
                case 0x20:
                    memo.Recall(cursor.ReadByte() + 0x28);
                    break;
                case 0x21:
                    memo.Recall(cursor.ReadByte() + 0x128);
                    break;
                case 0x22:
                    memo.Recall((cursor.ReadU16() + 0x28) & 0xFFFF);
                    break;
                case < 0x4B:
                    memo.Recall(op - 0x23);
                    break;
                case < 0xFE:
                    cursor.WriteU16(Table0[op - 0x4B]);
                    break;
                case 0xFE:
                    Extension(cursor);
                    break;
                default:
                    return;
            }
        }
    }

    private static void Run1(BlockCursor cursor, MemoTable memo)
    {
        for (; ; )
        {
            var op = cursor.ReadByte();
            switch (op)
            {
                case < 0x10:
                    cursor.CopyLiteral(op + 1);
                    break;
                case < 0x20:
                    RememberAndCopy(cursor, memo, op - 0x0F);
                    break;
                case < 0xD0:
                    memo.Recall(op - 0x20);
                    break;
                case 0xD0:
                    cursor.CopyLiteral(ReadVarint(cursor) & 0xFFFF);
                    break;
                case 0xD1:
                    RememberAndCopy(cursor, memo, ReadVarint(cursor) & 0xFFFF);
                    break;
                case 0xD2:
                    memo.Recall(cursor.ReadByte() + 0xB0);
                    break;
                case 0xD3:
                    memo.Recall(cursor.ReadByte() + 0x1B0);
                    break;
                case 0xD4:
                    memo.Recall((cursor.ReadU16() + 0xB0) & 0xFFFF);
                    break;
                case < 0xFE:
                    cursor.WriteU16(Table1[op - 0xD5]);
                    break;
                case 0xFE:
                    Extension(cursor);
                    break;
                default:
                    return;
            }
        }
    }

    // The string is copied into the memo table from the source before the literal is emitted.
    private static void RememberAndCopy(BlockCursor cursor, MemoTable memo, int length)
    {
        memo.Remember(length);
        cursor.CopyLiteral(length);
    }

    // b < $80 → b; $FF → next four bytes; otherwise (b − $C0)·256 + next byte, signed: 80–BF give −$4000..−1.
    internal static int ReadVarint(BlockCursor cursor)
    {
        var b = cursor.ReadByte();
        if (b < 0x80)
        {
            return b;
        }

        if (b == 0xFF)
        {
            return (int)cursor.ReadU32();
        }

        return (b - 0xC0) * 256 + cursor.ReadByte();
    }

    // FE sub-ops (shared code, dcmp 0 @0xF8, dcmp 1 @0xF4). Counts come from dbf: the low 16 bits, unsigned.
    private static void Extension(BlockCursor cursor)
    {
        switch (cursor.ReadByte())
        {
            case 0: // export table: seg, cnt, cnt × delta; exactly cnt entries, then a trailing 3F3C seg A9F0
                {
                    var segment = ReadVarint(cursor);
                    var count = ReadVarint(cursor) & 0xFFFF;
                    var index = 6;
                    for (var i = 0; i < count; i++)
                    {
                        index = (index + ReadVarint(cursor) - 6) & 0xFFFF;
                        cursor.WriteU16(0x3F3C);
                        cursor.WriteU16(segment);
                        cursor.WriteU16(0xA9F0);
                        cursor.WriteU16(index);
                    }
                    cursor.WriteU16(0x3F3C);
                    cursor.WriteU16(segment);
                    cursor.WriteU16(0xA9F0);
                    break;
                }
            case 1: // jump table: target, a5 delta, cnt, a5 offset; (cnt & $FFFF) + 1 entries of 6100 target 4EED a5
                {
                    var target = ReadVarint(cursor);
                    var a5Delta = ReadVarint(cursor);
                    var count = (ReadVarint(cursor) & 0xFFFF) + 1;
                    var a5 = ReadVarint(cursor);
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            target -= 8;
                            if ((a5Delta & 0xFFFF) == 0)
                            {
                                a5 = ReadVarint(cursor);
                            }
                            else
                            {
                                a5 += a5Delta;
                            }
                        }
                        cursor.WriteU16(0x6100);
                        cursor.WriteU16(target);
                        cursor.WriteU16(0x4EED);
                        cursor.WriteU16(a5);
                    }
                    break;
                }
            case 2: // byte run
                {
                    var value = (byte)ReadVarint(cursor);
                    var count = (ReadVarint(cursor) & 0xFFFF) + 1;
                    for (var i = 0; i < count; i++)
                    {
                        cursor.WriteByte(value);
                    }

                    break;
                }
            case 3: // word run
                {
                    var value = ReadVarint(cursor);
                    var count = (ReadVarint(cursor) & 0xFFFF) + 1;
                    for (var i = 0; i < count; i++)
                    {
                        cursor.WriteU16(value);
                    }

                    break;
                }
            case 4: // words, signed-byte deltas
                {
                    var value = ReadVarint(cursor);
                    var count = (ReadVarint(cursor) & 0xFFFF) + 1;
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            value += (sbyte)cursor.ReadByte();
                        }

                        cursor.WriteU16(value);
                    }
                    break;
                }
            case 5: // words, varint deltas
                {
                    var value = ReadVarint(cursor);
                    var count = (ReadVarint(cursor) & 0xFFFF) + 1;
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            value += ReadVarint(cursor);
                        }

                        cursor.WriteU16(value);
                    }
                    break;
                }
            case 6: // longs, varint deltas
                {
                    var value = (uint)ReadVarint(cursor);
                    var count = (ReadVarint(cursor) & 0xFFFF) + 1;
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            value += (uint)ReadVarint(cursor);
                        }

                        cursor.WriteU32(value);
                    }
                    break;
                }
            default: // 7 and above: only the sub-op byte is consumed
                break;
        }
    }

    // The memo table lives in the working buffer: word 0 = offset of the next slot (starts at 4), word 1 = the
    // buffer size & $FFFF; slot i's start is the word at 4 + 2i and its end the word before. Strings grow down from
    // the end, slots up from 4, all in 16 bits, with no capacity check: when full they overwrite each other, as on
    // the Mac. The buffer starts zeroed (the Mac's is whatever the heap held).
    private sealed class MemoTable
    {
        private readonly byte[] work;
        private readonly BlockCursor cursor;
        private readonly DecompressionContext context;
        private int count;
        private bool undefinedReported;

        public MemoTable(long size, BlockCursor cursor, DecompressionContext context)
        {
            // Offsets are 16-bit, so nothing past 64 KiB is reachable; the two header words always exist.
            work = new byte[System.Math.Clamp(size, 4, 0x10000)];
            this.cursor = cursor;
            this.context = context;
            SetWord(0, 4);
            SetWord(2, (ushort)size);
        }

        public void Remember(int length)
        {
            var slot = Word(0);
            var start = (ushort)(Word((ushort)(slot - 2)) - length);
            SetWord(slot, start);
            SetWord(0, (ushort)(slot + 2));
            for (var i = 0; i < length; i++)
            {
                Set(start + i, cursor.PeekByte(i));
            }

            count++;
        }

        public void Recall(int index)
        {
            if (index >= count && !undefinedReported)
            {
                undefinedReported = true;
                context.Report(DiagnosticSeverity.Warning, "resource.dcmp-undefined-slot",
                    $"uses memo slot {index} of {count}; the Mac would copy whatever its buffer held (zeros here).");
            }
            var at = (ushort)(4 + 2 * index);
            var start = Word(at);
            var length = (ushort)(Word((ushort)(at - 2)) - start);
            for (var i = 0; i < length; i++)
            {
                cursor.WriteByte(Get(start + i));
            }
        }

        private ushort Word(int at) => (ushort)(Get(at) << 8 | Get(at + 1));

        private void SetWord(int at, ushort value)
        {
            Set(at, (byte)(value >> 8));
            Set(at + 1, (byte)value);
        }

        private byte Get(int at)
        {
            if (at >= work.Length)
            {
                throw new DecompressionOverrunException("A memo string lies outside the working buffer.");
            }

            return work[at];
        }

        private void Set(int at, byte value)
        {
            if (at >= work.Length)
            {
                throw new DecompressionOverrunException("A memo string lies outside the working buffer.");
            }

            work[at] = value;
        }
    }
}
