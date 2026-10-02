// The decoding tables and opcode-line structure follow resource_dasm's M68KEmulator::decode_instruction (MIT,
// Copyright (c) 2023 Martin Michelsen; see THIRD-PARTY-NOTICES.md), checked field by field against Motorola's
// M68000 Family Programmer's Reference Manual (M68000PM/AD), the MC68881/MC68882 User's Manual and the MC68040
// User's Manual. Where they differ, Motorola's manual wins; the differences are noted where they are made.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Code.Disassembly;

/// <summary>
/// Decodes 68k instructions: the whole 68000, the 68020/030 integer extensions (32-bit multiply and divide,
/// <c>extb.l</c>, bit fields, <c>rtd</c>, <c>link.l</c>, <c>cas</c>/<c>cas2</c>, <c>chk2</c>/<c>cmp2</c>,
/// <c>pack</c>/<c>unpk</c>, <c>trapcc</c>, long branches, full-extension-word addressing with scale and memory
/// indirection, <c>callm</c>/<c>rtm</c>), the 68010's <c>movec</c>/<c>moves</c>, the 68040's
/// <c>cinv</c>/<c>cpush</c>/<c>move16</c>/<c>pflush</c>/<c>ptest</c>, the 68881/68882 FPU (coprocessor 1) and the
/// 68030 MMU and 68851 PMMU (coprocessor 0: <c>pmove</c>, <c>pflush</c>, <c>pload</c>, <c>ptest</c>, <c>pvalid</c>,
/// the PMMU's conditionals, <c>psave</c>/<c>prestore</c>). A-line words ($Axxx) are Mac OS traps. Other words
/// (among them the CPU32's <c>tbl</c> and <c>bgnd</c> and the 68060's <c>plpa</c>, <c>lpstop</c> and <c>movec</c> of
/// BUSCR and PCR, which no Macintosh processor has), instructions with an addressing mode they do not allow, and
/// words with a bit set that the instruction's format draws as 0, are <c>dc.w</c>. [Doc: M68000 Family Programmer's
/// Reference Manual (Motorola)]
/// </summary>
public static class M68kDisassembler
{
    /// <summary>Decodes the instruction at <paramref name="offset"/>.</summary>
    /// <param name="code">The code.</param>
    /// <param name="offset">The instruction's offset in <paramref name="code"/>.</param>
    /// <param name="baseAddress">The address of <paramref name="code"/>'s first byte.</param>
    /// <param name="trapName">Names an A-line trap word for the text (<see cref="TrapNames.Describe"/>); null
    /// writes the word (<c>_A9F0</c>).</param>
    /// <returns>The instruction. An instruction cut short by the end of <paramref name="code"/> is a
    /// <c>dc.w</c>; a last odd byte, or an odd address, is a <c>dc.b</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is not inside
    /// <paramref name="code"/>.</exception>
    public static M68kInstruction Decode(ReadOnlyMemory<byte> code, int offset, uint baseAddress = 0,
        Func<ushort, string>? trapName = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, code.Length);
        return new Decoder(new BigEndianReader(code), baseAddress, trapName).Decode(offset);
    }

    /// <summary>Decodes <paramref name="code"/> from start to end, each instruction following the last (a linear
    /// sweep: data between functions comes out as instructions or <c>dc.w</c>).</summary>
    /// <param name="code">The code.</param>
    /// <param name="baseAddress">The address of <paramref name="code"/>'s first byte.</param>
    /// <param name="trapName">Names an A-line trap word for the text; null writes the word.</param>
    /// <returns>The instructions.</returns>
    public static IEnumerable<M68kInstruction> Disassemble(ReadOnlyMemory<byte> code, uint baseAddress = 0,
        Func<ushort, string>? trapName = null)
    {
        var decoder = new Decoder(new BigEndianReader(code), baseAddress, trapName);
        for (int offset = 0; offset < code.Length;)
        {
            var instruction = decoder.Decode(offset);
            yield return instruction;
            offset += instruction.Length;
        }
    }

    // Addressing-mode classes, numbered as resource_dasm numbers them, for the "data", "memory", "control" and
    // "alterable" categories of Motorola's manual (table 2-4).
    private const int DReg = 0, AReg = 1, Ind = 2, PostInc = 3, PreDec = 4, Disp = 5, Index = 6, IndPost = 7,
        IndPre = 8, Abs = 9, PcDisp = 10, PcIndex = 11, PcIndPost = 12, PcIndPre = 13, Imm = 14;

    private readonly record struct Ea(int Mode, M68kOperand Operand)
    {
        public bool IsData => Mode != AReg;
        public bool IsMemory => Mode >= Ind;
        public bool IsControl => Mode == Ind || (Mode >= Disp && Mode <= PcIndPre);
        // resource_dasm counts the PC memory-indirect modes as alterable; Motorola's manual does not (no PC-relative
        // mode is alterable).
        public bool IsAlterable => Mode <= Abs;
        public bool IsDataAlterable => IsData && IsAlterable;
        public bool IsMemoryAlterable => IsMemory && IsAlterable;
        public bool IsControlAlterable => IsControl && IsAlterable;
    }

    private static readonly M68kSize[] Sizes = [M68kSize.Byte, M68kSize.Word, M68kSize.Long];

    // FPU source/destination format field [Doc: MC68881/MC68882 User's Manual, 4.4]; 7 is packed with a dynamic
    // k-factor on a store.
    private static readonly M68kSize[] FpuFormats =
    [
        M68kSize.Long, M68kSize.Single, M68kSize.Extended, M68kSize.Packed, M68kSize.Word, M68kSize.Double,
        M68kSize.Byte, M68kSize.Packed,
    ];

    private static readonly string[] Conditions =
        ["t", "f", "hi", "ls", "cc", "cs", "ne", "eq", "vc", "vs", "pl", "mi", "ge", "lt", "gt", "le"];

    private static readonly string[] FpuConditions =
    [
        "f", "eq", "ogt", "oge", "olt", "ole", "ogl", "or", "un", "ueq", "ugt", "uge", "ult", "ule", "ne", "t",
        "sf", "seq", "gt", "ge", "lt", "le", "gl", "gle", "ngle", "ngl", "nle", "nlt", "nge", "ngt", "sne", "st",
    ];

    // The 68851's PMMU conditions [Doc: MC68851 PMMU User's Manual, 6]: B bus error, L limit, S supervisor, A access
    // level, W write protected, I invalid, G gate, C globally shared; each set or clear.
    private static readonly string[] MmuConditions =
        ["bs", "bc", "ls", "lc", "ss", "sc", "as", "ac", "ws", "wc", "is", "ic", "gs", "gc", "cs", "cc"];

    private static readonly string[] BitOps = ["btst", "bchg", "bclr", "bset"];
    private static readonly string[] Shifts = ["asr", "asl", "lsr", "lsl", "roxr", "roxl", "ror", "rol"];
    private static readonly string[] BitFields =
        ["bftst", "bfextu", "bfchg", "bfexts", "bfclr", "bfffo", "bfset", "bfins"];

    // FPU general operations by the extension word's opmode (bits 6-0) [Doc: MC68881/MC68882 User's Manual, table
    // 4-1; MC68040 User's Manual for the single/double-rounding forms]. resource_dasm puts fmod at $2D; Motorola's
    // table has FMOD at $21.
    private static readonly Dictionary<int, string> FpuOperations = new()
    {
        [0x00] = "fmove", [0x01] = "fint", [0x02] = "fsinh", [0x03] = "fintrz", [0x04] = "fsqrt", [0x06] = "flognp1",
        [0x08] = "fetoxm1", [0x09] = "ftanh", [0x0A] = "fatan", [0x0C] = "fasin", [0x0D] = "fatanh", [0x0E] = "fsin",
        [0x0F] = "ftan", [0x10] = "fetox", [0x11] = "ftwotox", [0x12] = "ftentox", [0x14] = "flogn",
        [0x15] = "flog10", [0x16] = "flog2", [0x18] = "fabs", [0x19] = "fcosh", [0x1A] = "fneg", [0x1C] = "facos",
        [0x1D] = "fcos", [0x1E] = "fgetexp", [0x1F] = "fgetman", [0x20] = "fdiv", [0x21] = "fmod", [0x22] = "fadd",
        [0x23] = "fmul", [0x24] = "fsgldiv", [0x25] = "frem", [0x26] = "fscale", [0x27] = "fsglmul", [0x28] = "fsub",
        [0x38] = "fcmp", [0x3A] = "ftst", [0x40] = "fsmove", [0x41] = "fssqrt", [0x44] = "fdmove", [0x45] = "fdsqrt",
        [0x58] = "fsabs", [0x5A] = "fsneg", [0x5C] = "fdabs", [0x5E] = "fdneg", [0x60] = "fsdiv", [0x62] = "fsadd",
        [0x63] = "fsmul", [0x64] = "fddiv", [0x66] = "fdadd", [0x67] = "fdmul", [0x68] = "fssub", [0x6C] = "fdsub",
    };

    // movec's control registers [Doc: M68000 Family Programmer's Reference Manual, MOVEC].
    private static readonly Dictionary<int, string> ControlRegisters = new()
    {
        [0x000] = "sfc", [0x001] = "dfc", [0x002] = "cacr", [0x003] = "tc", [0x004] = "itt0", [0x005] = "itt1",
        [0x006] = "dtt0", [0x007] = "dtt1", [0x800] = "usp", [0x801] = "vbr", [0x802] = "caar", [0x803] = "msp",
        [0x804] = "isp", [0x805] = "mmusr", [0x806] = "urp", [0x807] = "srp",
    };

    private sealed class Decoder(BigEndianReader r, uint baseAddress, Func<ushort, string>? trapName)
    {
        private readonly List<M68kReference> references = [];
        private readonly List<string> comments = [];
        private int start;
        private uint address;
        private M68kFlags flags;
        private bool hex;        // the immediates of a logical operation are written in hex
        private bool shortForm;  // a Byte-sized branch is written .s

        public M68kInstruction Decode(int offset)
        {
            start = offset;
            address = unchecked(baseAddress + (uint)offset);
            if ((address & 1) != 0 || r.Length - offset < 2)
                return DataByte();
            references.Clear();
            comments.Clear();
            flags = M68kFlags.None;
            hex = false;
            shortForm = false;
            r.Position = offset;
            M68kInstruction? instruction;
            try
            {
                instruction = DecodeOpcode(r.ReadUInt16());
            }
            catch (EndOfStreamException)
            {
                instruction = null;
            }
            return instruction ?? DataWord();
        }

        private M68kInstruction DataWord()
        {
            ushort word = r.ReadUInt16At(start);
            var flagsOut = M68kFlags.Invalid | ((word & 0xF000) == 0xF000 ? M68kFlags.FLine : 0);
            return new M68kInstruction(address, 2, [word], "dc", M68kSize.Word,
                [new M68kImmediate(word, M68kSize.Word, [(byte)(word >> 8), (byte)word])], flagsOut, [],
                "dc.w $" + Hex(word, 4), null);
        }

        private M68kInstruction DataByte()
        {
            byte value = r.ReadByteAt(start);
            return new M68kInstruction(address, 1, [], "dc", M68kSize.Byte, [new M68kImmediate(value, M68kSize.Byte, [value])],
                M68kFlags.Invalid, [], "dc.b $" + Hex(value, 2), null);
        }

        private M68kInstruction? DecodeOpcode(ushort op)
        {
            int a = (op >> 9) & 7, b = (op >> 6) & 7, m = (op >> 3) & 7, xn = op & 7;
            return (op >> 12) switch
            {
                0x0 => Line0(op, a, b, m, xn),
                0x1 or 0x2 or 0x3 => Move(op, a, b, m, xn),
                0x4 => Line4(op, a, b, m, xn),
                0x5 => Line5(op, a, b, m, xn),
                0x6 => Branch(op),
                0x7 => (op & 0x0100) != 0 ? null : Make("moveq", M68kSize.None, Quick((sbyte)op), Dr(a)),
                0x8 => Line8(a, b, m, xn),
                0x9 => AddSub("sub", a, b, m, xn),
                0xA => ALine(op),
                0xB => LineB(a, b, m, xn),
                0xC => LineC(a, b, m, xn),
                0xD => AddSub("add", a, b, m, xn),
                0xE => LineE(op, a, b, m, xn),
                _ => LineF(op, a, b, m, xn),
            };
        }

        // ---- line 0: immediate, bit and the 68010/020 additions ----

        private M68kInstruction? Line0(ushort op, int a, int b, int m, int xn)
        {
            if ((op & 0xF5BF) == 0x003C)
            {
                // ori/andi/eori to CCR (byte) or SR (word)
                string? name = a switch { 0 => "ori", 1 => "andi", 5 => "eori", _ => null };
                if (name is null)
                    return null;
                var size = b == 0 ? M68kSize.Byte : M68kSize.Word;
                var imm = ReadImmediate(size)!;
                if (size == M68kSize.Byte && imm.Bytes[0] != 0)
                    return null;
                hex = true;
                return Make(name, size, imm,
                    new M68kRegisterOperand(b == 0 ? M68kRegisterKind.ConditionCodes : M68kRegisterKind.StatusRegister, 0));
            }

            if ((b & 4) != 0 && m == 1)
            {
                // movep; resource_dasm passes the opmode as the data register, the register is bits 11-9.
                var size = (b & 1) != 0 ? M68kSize.Long : M68kSize.Word;
                var ea = new M68kEffectiveAddress(M68kAddressingMode.Displacement, xn, r.ReadInt16(), null, 0, false, false, null);
                return (b & 2) != 0 ? Make("movep", size, Dr(a), ea) : Make("movep", size, ea, Dr(a));
            }

            if ((b & 4) != 0 || a == 4)
            {
                // btst/bchg/bclr/bset with the bit number in Dn or an immediate
                bool dynamic = (b & 4) != 0;
                M68kOperand bit;
                if (dynamic)
                    bit = Dr(a);
                else
                {
                    var imm = ReadImmediate(M68kSize.Byte)!;
                    if (imm.Bytes[0] != 0)
                        return null;
                    bit = imm;
                }
                if (ReadEa(m, xn, M68kSize.Byte) is not { } ea)
                    return null;
                int which = b & 3;
                bool ok = which == 0 ? ea.IsData && (dynamic || ea.Mode != Imm) : ea.IsDataAlterable;
                return ok ? Make(BitOps[which], M68kSize.None, bit, ea.Operand) : null;
            }

            if (b != 3)
            {
                var size = Sizes[b];
                if (a == 7)
                {
                    // moves (68010)
                    ushort ext = r.ReadUInt16();
                    if (ReadEa(m, xn, M68kSize.None) is not { } mea || (ext & 0x07FF) != 0 || !mea.IsMemoryAlterable)
                        return null;
                    var reg = GeneralRegister(ext >> 12);
                    return (ext & 0x0800) != 0 ? Make("moves", size, reg, mea.Operand) : Make("moves", size, mea.Operand, reg);
                }
                string? name = a switch
                {
                    0 => "ori", 1 => "andi", 2 => "subi", 3 => "addi", 5 => "eori", 6 => "cmpi", _ => null,
                };
                if (name is null)
                    return null;
                var imm = ReadImmediate(size)!;
                if (ReadEa(m, xn, M68kSize.None) is not { } ea)
                    return null;
                // The 68020 lets cmpi compare with PC-relative data.
                bool ok = a == 6 ? ea.IsData && ea.Mode != Imm : ea.IsDataAlterable;
                hex = a is 0 or 1 or 5;
                return ok ? Make(name, size, imm, ea.Operand) : null;
            }

            if (a == 3)
            {
                if (m < 2)
                {
                    // rtm Rn (68020): bit 3 selects An; resource_dasm takes only mode 1.
                    flags |= M68kFlags.Return;
                    return Make("rtm", M68kSize.None, m == 0 ? Dr(xn) : Ar(xn));
                }
                var count = ReadImmediate(M68kSize.Byte)!;
                if (count.Bytes[0] != 0 || ReadEa(m, xn, M68kSize.None) is not { IsControl: true } cea)
                    return null;
                return Make("callm", M68kSize.None, new M68kImmediate(count.Value, M68kSize.None, count.Bytes), cea.Operand);
            }

            if ((a & 4) != 0)
            {
                var size = Sizes[(a & 3) - 1];
                if (m == 7 && xn == 4)
                {
                    // cas2.w/.l Dc1:Dc2,Du1:Du2,(Rn1):(Rn2)
                    if (size == M68kSize.Byte)
                        return null;
                    ushort e1 = r.ReadUInt16(), e2 = r.ReadUInt16();
                    if (((e1 | e2) & 0x0E38) != 0)
                        return null;
                    return Make("cas2", size, Pair(Dr(e1 & 7), Dr(e2 & 7)), Pair(Dr((e1 >> 6) & 7), Dr((e2 >> 6) & 7)),
                        new M68kRegisterPair(GeneralRegister(e1 >> 12), GeneralRegister(e2 >> 12), true));
                }
                ushort ext = r.ReadUInt16();
                if (ReadEa(m, xn, M68kSize.None) is not { } ea || (ext & 0xFE38) != 0 || !ea.IsMemoryAlterable)
                    return null;
                return Make("cas", size, Dr(ext & 7), Dr((ext >> 6) & 7), ea.Operand);
            }

            {
                // cmp2/chk2: the size is bits 10-9 (resource_dasm subtracts one, as for cas).
                var size = Sizes[a];
                ushort ext = r.ReadUInt16();
                if (ReadEa(m, xn, M68kSize.None) is not { IsControl: true } ea || (ext & 0x07FF) != 0)
                    return null;
                return Make((ext & 0x0800) != 0 ? "chk2" : "cmp2", size, ea.Operand, GeneralRegister(ext >> 12));
            }
        }

        // ---- lines 1-3: move, movea ----

        private M68kInstruction? Move(ushort op, int a, int b, int m, int xn)
        {
            var size = ((op >> 12) & 3) switch { 1 => M68kSize.Byte, 3 => M68kSize.Word, _ => M68kSize.Long };
            if (ReadEa(m, xn, size) is not { } src)
                return null;
            if (b == 1)
                return size == M68kSize.Byte ? null : Make("movea", size, src.Operand, Ar(a));
            if (ReadEa(b, a, M68kSize.None) is not { IsDataAlterable: true } dst)
                return null;
            return Make("move", size, src.Operand, dst.Operand);
        }

        // ---- line 4: miscellaneous ----

        private M68kInstruction? Line4(ushort op, int a, int b, int m, int xn)
        {
            if ((a & 5) == 4 && (b & 6) == 2 && m >= 2)
            {
                var size = (b & 1) != 0 ? M68kSize.Long : M68kSize.Word;
                ushort mask = r.ReadUInt16();
                if (ReadEa(m, xn, M68kSize.None) is not { } ea)
                    return null;
                bool toRegisters = (a & 2) != 0;
                bool ok = toRegisters ? ea.IsControl || ea.Mode == PostInc : ea.IsControlAlterable || ea.Mode == PreDec;
                if (!ok)
                    return null;
                // In predecrement mode the mask runs the other way: bit 0 is A7, bit 15 is D0.
                var list = new M68kRegisterList(ea.Mode == PreDec ? Reverse(mask, 16) : mask, M68kRegisterListKind.Integer);
                return toRegisters ? Make("movem", size, ea.Operand, list) : Make("movem", size, list, ea.Operand);
            }
            if (b == 7 && (m == 2 || m >= 5))
                return ReadEa(m, xn, M68kSize.None) is { IsControl: true } lea ? Make("lea", M68kSize.None, lea.Operand, Ar(a)) : null;
            if ((b & 5) == 4 && m != 1)
            {
                var size = (b & 2) != 0 ? M68kSize.Word : M68kSize.Long;
                return ReadEa(m, xn, size) is { IsData: true } chk ? Make("chk", size, chk.Operand, Dr(a)) : null;
            }
            if (a == 4 && m == 0 && b is 2 or 3 or 7)
                return b switch
                {
                    2 => Make("ext", M68kSize.Word, Dr(xn)),
                    3 => Make("ext", M68kSize.Long, Dr(xn)),
                    _ => Make("extb", M68kSize.Long, Dr(xn)),
                };
            if ((op & 0x0100) != 0)
                return null;
            if (a < 4 && b == 3)
            {
                if (ReadEa(m, xn, M68kSize.Word) is not { } ea)
                    return null;
                var sr = new M68kRegisterOperand(M68kRegisterKind.StatusRegister, 0);
                var ccr = new M68kRegisterOperand(M68kRegisterKind.ConditionCodes, 0);
                return a switch
                {
                    0 => ea.IsDataAlterable ? Make("move", M68kSize.Word, sr, ea.Operand) : null,
                    1 => ea.IsDataAlterable ? Make("move", M68kSize.Word, ccr, ea.Operand) : null,
                    2 => ea.IsData ? Make("move", M68kSize.Word, ea.Operand, ccr) : null,
                    _ => ea.IsData ? Make("move", M68kSize.Word, ea.Operand, sr) : null,
                };
            }
            if (a < 4)
            {
                string name = a switch { 0 => "negx", 1 => "clr", 2 => "neg", _ => "not" };
                return ReadEa(m, xn, M68kSize.None) is { IsDataAlterable: true } ea ? Make(name, Sizes[b], ea.Operand) : null;
            }
            switch (a)
            {
                case 4:
                    if (b == 0)
                    {
                        if (m == 1)
                            return Make("link", M68kSize.Long, Ar(xn), ReadSigned(M68kSize.Long));
                        return ReadEa(m, xn, M68kSize.None) is { IsDataAlterable: true } ea ? Make("nbcd", M68kSize.None, ea.Operand) : null;
                    }
                    if (b == 1)
                    {
                        if (m == 0)
                            return Make("swap", M68kSize.None, Dr(xn));
                        if (m == 1)
                            return Make("bkpt", M68kSize.None, Quick(xn));
                        if (m == 2 || m >= 5)
                            return ReadEa(m, xn, M68kSize.None) is { IsControl: true } ea ? Make("pea", M68kSize.None, ea.Operand) : null;
                    }
                    return null;
                case 5:
                    if (b != 3)
                    {
                        // tst: the 68020 allows every mode; An is word and long only.
                        var size = Sizes[b];
                        if (ReadEa(m, xn, size) is not { } ea || (size == M68kSize.Byte && ea.Mode == AReg))
                            return null;
                        return Make("tst", size, ea.Operand);
                    }
                    if (m != 7 || xn < 2)
                        return ReadEa(m, xn, M68kSize.None) is { IsDataAlterable: true } tas ? Make("tas", M68kSize.None, tas.Operand) : null;
                    // $4AFA is the CPU32's bgnd, which no Macintosh processor has.
                    return xn == 4 ? Make("illegal", M68kSize.None) : null;
                case 6:
                    return b <= 1 ? MulDivLong(b, m, xn) : null;
                default:
                    return Line4E(op, b, m, xn);
            }
        }

        private M68kInstruction? MulDivLong(int b, int m, int xn)
        {
            ushort ext = r.ReadUInt16();
            if (ReadEa(m, xn, M68kSize.Long) is not { IsData: true } ea)
                return null;
            // Bits 9-3 are reserved; the processor ignores them, and so do Ghidra and the specification's vector
            // $4C43 $4D44 (fitted to those, not to Motorola's text, which only marks them 0). Bit 15 is kept 0.
            if ((ext & 0x8000) != 0)
                return null;
            int low = (ext >> 12) & 7, high = ext & 7;
            bool signed = (ext & 0x0800) != 0, quad = (ext & 0x0400) != 0;
            if (b == 0)
                return Make(signed ? "muls" : "mulu", M68kSize.Long, ea.Operand, quad ? Pair(Dr(high), Dr(low)) : Dr(low));
            // DIVS.L <ea>,Dq (32/32); DIVS.L <ea>,Dr:Dq (64/32); DIVSL.L <ea>,Dr:Dq (32/32 with remainder).
            if (quad)
                return Make(signed ? "divs" : "divu", M68kSize.Long, ea.Operand, Pair(Dr(high), Dr(low)));
            if (high != low)
                return Make(signed ? "divsl" : "divul", M68kSize.Long, ea.Operand, Pair(Dr(high), Dr(low)));
            return Make(signed ? "divs" : "divu", M68kSize.Long, ea.Operand, Dr(low));
        }

        private M68kInstruction? Line4E(ushort op, int b, int m, int xn)
        {
            if (b is 2 or 3)
            {
                if (ReadEa(m, xn, M68kSize.None) is not { IsControl: true } ea)
                    return null;
                bool call = b == 2;
                flags |= call ? M68kFlags.Call : M68kFlags.Branch;
                var target = (M68kEffectiveAddress)ea.Operand;
                if (target.Mode is M68kAddressingMode.AbsoluteShort or M68kAddressingMode.AbsoluteLong
                        or M68kAddressingMode.PcDisplacement or M68kAddressingMode.PcIndexed
                    && target.Address is uint to)
                {
                    references.Clear();
                    AddFlowReference(to, call ? M68kReferenceKind.Call : M68kReferenceKind.Branch);
                }
                return Make(call ? "jsr" : "jmp", M68kSize.None, ea.Operand);
            }
            if (b != 1)
                return null;
            switch (m)
            {
                case 0:
                case 1:
                    return Make("trap", M68kSize.None, Quick(op & 15));
                case 2:
                    return Make("link", M68kSize.None, Ar(xn), ReadSigned(M68kSize.Word));
                case 3:
                    return Make("unlk", M68kSize.None, Ar(xn));
                case 4:
                    return Make("move", M68kSize.Long, Ar(xn), new M68kRegisterOperand(M68kRegisterKind.UserStackPointer, 0));
                case 5:
                    return Make("move", M68kSize.Long, new M68kRegisterOperand(M68kRegisterKind.UserStackPointer, 0), Ar(xn));
                case 6:
                    switch (xn)
                    {
                        case 0: return Make("reset", M68kSize.None);
                        case 1: return Make("nop", M68kSize.None);
                        case 2:
                            hex = true;
                            return Make("stop", M68kSize.None, ReadImmediate(M68kSize.Word)!);
                        case 3:
                            flags |= M68kFlags.Return;
                            return Make("rte", M68kSize.None);
                        case 4:
                            flags |= M68kFlags.Return;
                            return Make("rtd", M68kSize.None, ReadSigned(M68kSize.Word));
                        case 5:
                            flags |= M68kFlags.Return;
                            return Make("rts", M68kSize.None);
                        case 6: return Make("trapv", M68kSize.None);
                        default:
                            flags |= M68kFlags.Return;
                            return Make("rtr", M68kSize.None);
                    }
                default:
                    if (xn is not (2 or 3))
                        return null;
                    ushort ext = r.ReadUInt16();
                    if (!ControlRegisters.ContainsKey(ext & 0x0FFF))
                        return null;
                    var cr = new M68kRegisterOperand(M68kRegisterKind.Control, ext & 0x0FFF);
                    var rn = GeneralRegister(ext >> 12);
                    return xn == 2 ? Make("movec", M68kSize.None, cr, rn) : Make("movec", M68kSize.None, rn, cr);
            }
        }

        // ---- line 5: addq/subq, dbcc, trapcc, scc ----

        private M68kInstruction? Line5(ushort op, int a, int b, int m, int xn)
        {
            if ((b & 3) != 3)
            {
                var size = Sizes[b & 3];
                if (ReadEa(m, xn, M68kSize.None) is not { IsAlterable: true } ea || (size == M68kSize.Byte && ea.Mode == AReg))
                    return null;
                return Make((b & 4) != 0 ? "subq" : "addq", size, Quick(a == 0 ? 8 : a), ea.Operand);
            }
            string cc = Conditions[(op >> 8) & 15];
            if (m == 1)
            {
                uint target = unchecked(address + 2 + (uint)r.ReadInt16());
                flags |= M68kFlags.Branch | M68kFlags.Conditional;
                AddFlowReference(target, M68kReferenceKind.Branch);
                return Make("db" + cc, M68kSize.None, Dr(xn), new M68kBranchTarget(target));
            }
            if (m == 7 && xn is >= 2 and <= 4)
                return TrapCc("trap" + cc, xn);
            return ReadEa(m, xn, M68kSize.None) is { IsDataAlterable: true } sea ? Make("s" + cc, M68kSize.None, sea.Operand) : null;
        }

        // TRAPcc/FTRAPcc opmode: 2 a word operand, 3 a long operand, 4 none. resource_dasm reads 2 as none and 4 as
        // long.
        private M68kInstruction TrapCc(string name, int opmode) => opmode switch
        {
            2 => Make(name, M68kSize.Word, ReadImmediate(M68kSize.Word)!),
            3 => Make(name, M68kSize.Long, ReadImmediate(M68kSize.Long)!),
            _ => Make(name, M68kSize.None),
        };

        // ---- line 6: bra, bsr, bcc ----

        private M68kInstruction Branch(ushort op)
        {
            int cond = (op >> 8) & 15;
            int d8 = (sbyte)op;
            M68kSize size;
            int disp;
            if (d8 == 0)
                (disp, size) = (r.ReadInt16(), M68kSize.Word);
            else if (d8 == -1)
                (disp, size) = (r.ReadInt32(), M68kSize.Long);
            else
                (disp, size) = (d8, M68kSize.Byte);
            uint target = unchecked(address + 2 + (uint)disp);
            flags |= cond switch
            {
                0 => M68kFlags.Branch,
                1 => M68kFlags.Call,
                _ => M68kFlags.Branch | M68kFlags.Conditional,
            };
            AddFlowReference(target, cond == 1 ? M68kReferenceKind.Call : M68kReferenceKind.Branch);
            shortForm = true;
            string name = cond switch { 0 => "bra", 1 => "bsr", _ => "b" + Conditions[cond] };
            return Make(name, size, new M68kBranchTarget(target));
        }

        // ---- line 8: or, divide word, sbcd, pack, unpk ----

        private M68kInstruction? Line8(int a, int b, int m, int xn)
        {
            if (m < 2 && b == 4)
                return Make("sbcd", M68kSize.None, XOperand(m, xn), XOperand(m, a));
            if (m < 2 && b is 5 or 6)
            {
                var src = XOperand(m, xn);
                var dst = XOperand(m, a);
                return Make(b == 5 ? "pack" : "unpk", M68kSize.None, src, dst, ReadSigned(M68kSize.Word));
            }
            if ((b & 3) == 3)
            {
                bool signed = (b & 4) != 0;
                return ReadEa(m, xn, M68kSize.Word) is { IsData: true } ea
                    ? Make(signed ? "divs" : "divu", M68kSize.Word, ea.Operand, Dr(a)) : null;
            }
            return Logical("or", a, b, m, xn);
        }

        // or/and: Dn,<ea> (memory alterable) when bit 8 is set, else <ea>,Dn (data).
        private M68kInstruction? Logical(string name, int a, int b, int m, int xn)
        {
            var size = Sizes[b & 3];
            if ((b & 4) != 0)
                return ReadEa(m, xn, M68kSize.None) is { IsMemoryAlterable: true } dst ? Make(name, size, Dr(a), dst.Operand) : null;
            return ReadEa(m, xn, size) is { IsData: true } src ? Make(name, size, src.Operand, Dr(a)) : null;
        }

        // ---- lines 9 and D: sub, add and their a/x forms ----

        private M68kInstruction? AddSub(string name, int a, int b, int m, int xn)
        {
            if (m < 2 && (b & 4) != 0 && b != 7)
                return Make(name + "x", Sizes[b & 3], XOperand(m, xn), XOperand(m, a));
            if ((b & 3) == 3)
            {
                var asize = (b & 4) != 0 ? M68kSize.Long : M68kSize.Word;
                return ReadEa(m, xn, asize) is { } aea ? Make(name + "a", asize, aea.Operand, Ar(a)) : null;
            }
            var size = Sizes[b & 3];
            if ((b & 4) != 0)
                return ReadEa(m, xn, M68kSize.None) is { IsMemoryAlterable: true } dst ? Make(name, size, Dr(a), dst.Operand) : null;
            if (ReadEa(m, xn, size) is not { } src || (size == M68kSize.Byte && src.Mode == AReg))
                return null;
            return Make(name, size, src.Operand, Dr(a));
        }

        // ---- line A: Mac OS traps ----

        private M68kInstruction ALine(ushort op)
        {
            flags |= M68kFlags.ALine;
            string text = trapName?.Invoke(op) ?? "_" + Hex(op, 4);
            return Build("aline", M68kSize.None, text, [new M68kImmediate(op, M68kSize.Word, [(byte)(op >> 8), (byte)op])]);
        }

        // ---- line B: cmp, cmpa, cmpm, eor ----

        private M68kInstruction? LineB(int a, int b, int m, int xn)
        {
            if (m == 1 && (b & 4) != 0 && b != 7)
                return Make("cmpm", Sizes[b & 3], PostIncrement(xn), PostIncrement(a));
            if (b < 3)
            {
                var size = Sizes[b];
                if (ReadEa(m, xn, size) is not { } ea || (size == M68kSize.Byte && ea.Mode == AReg))
                    return null;
                return Make("cmp", size, ea.Operand, Dr(a));
            }
            if (b is 3 or 7)
            {
                var size = b == 7 ? M68kSize.Long : M68kSize.Word;
                return ReadEa(m, xn, size) is { } ea ? Make("cmpa", size, ea.Operand, Ar(a)) : null;
            }
            return ReadEa(m, xn, M68kSize.None) is { IsDataAlterable: true } dst ? Make("eor", Sizes[b & 3], Dr(a), dst.Operand) : null;
        }

        // ---- line C: and, multiply word, abcd, exg ----

        private M68kInstruction? LineC(int a, int b, int m, int xn)
        {
            if ((b & 3) == 3)
            {
                bool signed = (b & 4) != 0;
                return ReadEa(m, xn, M68kSize.Word) is { IsData: true } ea
                    ? Make(signed ? "muls" : "mulu", M68kSize.Word, ea.Operand, Dr(a)) : null;
            }
            if (b == 4 && m < 2)
                return Make("abcd", M68kSize.None, XOperand(m, xn), XOperand(m, a));
            if (b == 5 && m == 0)
                return Make("exg", M68kSize.None, Dr(a), Dr(xn));
            if (b == 5 && m == 1)
                return Make("exg", M68kSize.None, Ar(a), Ar(xn));
            if (b == 6 && m == 1)
                return Make("exg", M68kSize.None, Dr(a), Ar(xn));
            return Logical("and", a, b, m, xn);
        }

        // ---- line E: shifts, rotates, bit fields ----

        private M68kInstruction? LineE(ushort op, int a, int b, int m, int xn)
        {
            if ((b & 3) == 3 && (a & 4) != 0)
            {
                int which = (op >> 8) & 7;
                ushort ext = r.ReadUInt16();
                if (ReadEa(m, xn, M68kSize.None) is not { } ea || (ext & 0x8000) != 0)
                    return null;
                bool alterable = which is 2 or 4 or 6 or 7;
                if (ea.Mode != DReg && !(alterable ? ea.IsControlAlterable : ea.IsControl))
                    return null;
                // Offset: Do (bit 11) and bits 10-6; width: Dw (bit 5) and bits 4-0, 0 meaning 32. resource_dasm tests
                // bit 6 for Dw. The bits Motorola's format draws as 0 must be: bits 14-12 of bftst, bfchg, bfclr and
                // bfset (no register), bits 10-9 with Do set and bits 4-3 with Dw set [Doc: M68000 Family
                // Programmer's Reference Manual, BFTST ... BFINS].
                bool offsetIsRegister = (ext & 0x0800) != 0, widthIsRegister = (ext & 0x0020) != 0;
                if ((which is 0 or 2 or 4 or 6 && (ext & 0x7000) != 0)
                    || (offsetIsRegister && (ext & 0x0600) != 0) || (widthIsRegister && (ext & 0x0018) != 0))
                    return null;
                int offset = offsetIsRegister ? (ext >> 6) & 7 : (ext >> 6) & 31;
                int width = widthIsRegister ? ext & 7 : ((ext & 31) == 0 ? 32 : ext & 31);
                var field = new M68kBitField(offset, offsetIsRegister, width, widthIsRegister);
                var reg = Dr((ext >> 12) & 7);
                return which switch
                {
                    1 or 3 or 5 => Make(BitFields[which], M68kSize.None, ea.Operand, field, reg),
                    7 => Make(BitFields[which], M68kSize.None, reg, ea.Operand, field),
                    _ => Make(BitFields[which], M68kSize.None, ea.Operand, field),
                };
            }
            if ((b & 3) == 3)
                return ReadEa(m, xn, M68kSize.None) is { IsMemoryAlterable: true } ea
                    ? Make(Shifts[(op >> 8) & 7], M68kSize.Word, ea.Operand) : null;
            int kind = ((op >> 2) & 6) | ((op >> 8) & 1);
            M68kOperand count = (op & 0x0020) != 0 ? Dr(a) : Quick(a == 0 ? 8 : a);
            return Make(Shifts[kind], Sizes[b & 3], count, Dr(xn));
        }

        // ---- line F: coprocessors and the 68040 ----

        private M68kInstruction? LineF(ushort op, int a, int b, int m, int xn)
        {
            flags |= M68kFlags.FLine;
            if (a == 0)
                return Mmu(op, b, m, xn);
            if (a == 1)
                return Fpu(op, b, m, xn);
            // resource_dasm decodes no F-line word other than the FPU's; the rest is from the MC68040 User's Manual.
            if (a == 2 && (op & 0x0100) == 0)
            {
                // cinv/cpush: cache bits 7-6 (1 DC, 2 IC, 3 both), push bit 5, scope bits 4-3 (1 line, 2 page, 3 all).
                int cache = (op >> 6) & 3, scope = (op >> 3) & 3;
                if (cache == 0 || scope == 0)
                    return null;
                string name = ((op & 0x20) != 0 ? "cpush" : "cinv") + "?lpa"[scope];
                var caches = new M68kRegisterOperand(M68kRegisterKind.Cache, cache);
                return scope == 3 ? Make(name, M68kSize.None, caches) : Make(name, M68kSize.None, caches, Indirect(xn));
            }
            if ((op & 0xFFE0) == 0xF500)
            {
                int opmode = (op >> 3) & 3;
                string name = opmode switch { 0 => "pflushn", 1 => "pflush", 2 => "pflushan", _ => "pflusha" };
                return opmode < 2 ? Make(name, M68kSize.None, Indirect(xn)) : Make(name, M68kSize.None);
            }
            if ((op & 0xFFD8) == 0xF548)
                return Make((op & 0x20) != 0 ? "ptestr" : "ptestw", M68kSize.None, Indirect(xn));
            if ((op & 0xFFF8) == 0xF620)
            {
                ushort ext = r.ReadUInt16();
                return (ext & 0x8FFF) == 0x8000 ? Make("move16", M68kSize.None, PostIncrement(xn), PostIncrement((ext >> 12) & 7)) : null;
            }
            if ((op & 0xFFE0) == 0xF600)
            {
                int opmode = (op >> 3) & 3;
                uint absolute = r.ReadUInt32();
                var abs = new M68kEffectiveAddress(M68kAddressingMode.AbsoluteLong, 0, unchecked((int)absolute), null, 0, false, false, absolute);
                M68kOperand an = opmode < 2 ? PostIncrement(xn) : Indirect(xn);
                return (opmode & 1) == 0 ? Make("move16", M68kSize.None, an, abs) : Make("move16", M68kSize.None, abs, an);
            }
            return null;
        }

        // ---- coprocessor 0: the 68030's MMU and the 68851 PMMU [Doc: MC68030 User's Manual, 9; MC68851 PMMU User's
        // Manual, 6 and appendix A]. A word that is an instruction on either processor is decoded: the 68851's wider
        // forms (4-bit masks and function codes, every addressing mode for pmove from memory) and the 68030's own
        // (TT0/TT1, pmovefd, which allow control alterable modes only). ----

        private M68kInstruction? Mmu(ushort op, int b, int m, int xn)
        {
            switch (b)
            {
                case 0:
                    return MmuGeneral(op, m, xn);
                case 1:
                {
                    // pscc, pdbcc, ptrapcc (68851): the condition is the extension word's bits 5-0, 0-15.
                    ushort ext = r.ReadUInt16();
                    if ((ext & 0xFFF0) != 0)
                        return null;
                    string cc = MmuConditions[ext];
                    if (m == 1)
                    {
                        // pdbcc: the displacement is from its own word, as fdbcc's: the CPU runs it as cpDBcc and adds
                        // it to the scanPC, which points at the displacement [Doc: MC68030 User's Manual, 10.2.2.3,
                        // 10.4.1]. (The 68851 manual's A-4 says the instruction's address plus two.)
                        uint target = unchecked(PcHere() + (uint)r.ReadInt16());
                        flags |= M68kFlags.Branch | M68kFlags.Conditional;
                        AddFlowReference(target, M68kReferenceKind.Branch);
                        return Make("pdb" + cc, M68kSize.None, Dr(xn), new M68kBranchTarget(target));
                    }
                    if (m == 7 && xn is >= 2 and <= 4)
                        return TrapCc("ptrap" + cc, xn);
                    return ReadEa(m, xn, M68kSize.None) is { IsDataAlterable: true } ea ? Make("ps" + cc, M68kSize.None, ea.Operand) : null;
                }
                case 2:
                case 3:
                {
                    // pbcc.w/.l (68851): the condition is the opcode's bits 5-0, 0-15.
                    if ((op & 0x30) != 0)
                        return null;
                    uint pc = PcHere();
                    int disp = b == 2 ? r.ReadInt16() : r.ReadInt32();
                    uint target = unchecked(pc + (uint)disp);
                    flags |= M68kFlags.Branch | M68kFlags.Conditional;
                    AddFlowReference(target, M68kReferenceKind.Branch);
                    return Make("pb" + MmuConditions[op & 15], b == 2 ? M68kSize.Word : M68kSize.Long, new M68kBranchTarget(target));
                }
                case 4:
                    return ReadEa(m, xn, M68kSize.None) is { } save && (save.IsControlAlterable || save.Mode == PreDec)
                        ? Make("psave", M68kSize.None, save.Operand) : null;
                case 5:
                    return ReadEa(m, xn, M68kSize.None) is { } restore && (restore.IsControl || restore.Mode == PostInc)
                        ? Make("prestore", M68kSize.None, restore.Operand) : null;
                default:
                    return null;
            }
        }

        private M68kInstruction? MmuGeneral(ushort op, int m, int xn)
        {
            ushort ext = r.ReadUInt16();
            int field = (ext >> 10) & 7;
            bool toMemory = (ext & 0x0200) != 0;
            switch (ext >> 13)
            {
                case 0:
                    // pmove TT0/TT1 (68030): 000 PPP R FD 00000000.
                    if (field is not (2 or 3) || (ext & 0xFF) != 0)
                        return null;
                    return PMove(field, toMemory, (ext & 0x0100) != 0, M68kSize.Long, true, m, xn);
                case 1:
                    return LoadValidFlush(op, ext, field, m, xn);
                case 2:
                {
                    // pmove TC, DRP, SRP, CRP, CAL, VAL, SCC, AC: 010 PPP R FD 00000000. FD is the 68030's, only
                    // in the forms that write TC, SRP and CRP [Doc: MC68030 User's Manual, 9.7.5.1]; the 68851's
                    // format 1 has bit 8 zero [Doc: MC68851 PMMU User's Manual, A-11].
                    if ((ext & 0xFF) != 0)
                        return null;
                    bool fd = (ext & 0x0100) != 0;
                    if (fd && field is not (0 or 2 or 3))
                        return null;
                    var size = field switch { 0 => M68kSize.Long, < 4 => M68kSize.Double, 7 => M68kSize.Word, _ => M68kSize.Byte };
                    return PMove(0x10 | field, toMemory, fd, size, fd, m, xn);
                }
                case 3:
                {
                    // pmove MMUSR (PSR), PCSR: 011 PPP R 000000000; BADn, BACn: 011 PPP R 0000 NNN 00. No FD (bit 8
                    // zero) [Doc: MC68851 PMMU User's Manual, A-13].
                    if (field is 0 or 1)
                    {
                        // PCSR is read-only: R/W must be 1 [Doc: MC68851 PMMU User's Manual, A-13].
                        if ((ext & 0x01FF) != 0 || (field == 1 && !toMemory))
                            return null;
                        return PMove(0x18 | field, toMemory, false, M68kSize.Word, false, m, xn);
                    }
                    if (field is not (4 or 5) || (ext & 0x01E3) != 0)
                        return null;
                    return PMove((0x18 | field) + (((ext >> 2) & 7) << 6), toMemory, false, M68kSize.Word, false, m, xn);
                }
                case 4:
                {
                    // ptest: 100 LLL R A RRR FFFFF; R 1 is ptestr. The A register field is 0xxx (none) or 1RRR, and
                    // 0000 at level 0 [Doc: MC68851 PMMU User's Manual, A-22].
                    int level = field, an = (ext >> 5) & 7;
                    bool hasAn = (ext & 0x0100) != 0;
                    if ((level == 0 && (ext & 0x01E0) != 0) || FunctionCode(ext) is not { } fc
                        || ReadEa(m, xn, M68kSize.None) is not { IsControlAlterable: true } ea)
                        return null;
                    string name = toMemory ? "ptestr" : "ptestw";
                    return hasAn
                        ? Make(name, M68kSize.None, fc, ea.Operand, Quick(level), Ar(an))
                        : Make(name, M68kSize.None, fc, ea.Operand, Quick(level));
                }
                case 5:
                    // pflushr <ea> (68851): the 64-bit root pointer to flush by.
                    if (ext != 0xA000 || ReadEa(m, xn, M68kSize.Double) is not { IsMemory: true } root)
                        return null;
                    return Make("pflushr", M68kSize.None, root.Operand);
                default:
                    return null;
            }
        }

        // Extension word 001: pload (mode 000), pvalid (010 VAL, 011 An) and pflush (001 all, 100/101 by function
        // code, 110/111 by function code and address; the odd modes are the 68851's pflushs).
        private M68kInstruction? LoadValidFlush(ushort op, ushort ext, int mode, int m, int xn)
        {
            if (mode == 0)
            {
                // pload: 001000 R 0000 FFFFF
                if ((ext & 0x01E0) != 0 || FunctionCode(ext) is not { } fc
                    || ReadEa(m, xn, M68kSize.None) is not { IsControlAlterable: true } ea)
                    return null;
                return Make((ext & 0x0200) != 0 ? "ploadr" : "ploadw", M68kSize.None, fc, ea.Operand);
            }
            if (mode is 2 or 3)
            {
                // pvalid VAL,<ea>: $2800; pvalid An,<ea>: $2C00 + n.
                if ((mode == 2 ? ext != 0x2800 : (ext & 0xFFF8) != 0x2C00)
                    || ReadEa(m, xn, M68kSize.None) is not { IsControlAlterable: true } ea)
                    return null;
                M68kOperand against = mode == 2 ? new M68kRegisterOperand(M68kRegisterKind.MemoryManagement, 0x15) : Ar(ext & 7);
                return Make("pvalid", M68kSize.None, against, ea.Operand);
            }
            if ((ext & 0x0200) != 0)
                return null;
            // pflusha: mask 0000 and function code 00000 [Doc: MC68851 PMMU User's Manual, A-7].
            if (mode == 1)
                return ext == 0x2400 && (op & 0x3F) == 0 ? Make("pflusha", M68kSize.None) : null;
            // pflush: 001 MMM 0 MMMM FFFFF; the 68030's mask is 3 bits (bit 8 zero), the 68851's 4.
            string name = (mode & 1) != 0 ? "pflushs" : "pflush";
            if (FunctionCode(ext) is not { } code)
                return null;
            var mask = Quick((ext >> 5) & 15);
            if (mode < 6)
                return (op & 0x3F) == 0 ? Make(name, M68kSize.None, code, mask) : null;
            return ReadEa(m, xn, M68kSize.None) is { IsControlAlterable: true } at ? Make(name, M68kSize.None, code, mask, at.Operand) : null;
        }

        // pmove <ea>,MRn or MRn,<ea>. The 68030's forms (TT0/TT1 and pmovefd) take control alterable modes only
        // [Doc: MC68030 User's Manual, 9]; the 68851 takes every mode from memory and alterable ones to it, and no
        // Dn or An for a 64-bit root pointer [Doc: MC68851 PMMU User's Manual, A-12].
        private M68kInstruction? PMove(int register, bool toMemory, bool fd, M68kSize size, bool controlOnly, int m, int xn)
        {
            if (fd && toMemory)
                return null;
            if (ReadEa(m, xn, toMemory ? M68kSize.None : size) is not { } ea)
                return null;
            bool ok = controlOnly
                ? ea.IsControlAlterable
                : (!toMemory || ea.IsAlterable)
                    && !(size == M68kSize.Double && ea.Mode is DReg or AReg);
            if (!ok)
                return null;
            var mr = new M68kRegisterOperand(M68kRegisterKind.MemoryManagement, register);
            string name = fd ? "pmovefd" : "pmove";
            return toMemory ? Make(name, M68kSize.None, mr, ea.Operand) : Make(name, M68kSize.None, ea.Operand, mr);
        }

        // The function code field of pload, pflush and ptest: 00000 SFC, 00001 DFC, 01RRR Dn, 1DDDD #DDDD (the
        // 68030 has 10DDD only).
        private static M68kOperand? FunctionCode(ushort ext)
        {
            int fc = ext & 0x1F;
            if ((fc & 0x10) != 0)
                return Quick(fc & 15);
            if ((fc & 0x18) == 0x08)
                return Dr(fc & 7);
            return fc <= 1 ? new M68kRegisterOperand(M68kRegisterKind.Control, fc) : null;
        }

        private M68kInstruction? Fpu(ushort op, int b, int m, int xn)
        {
            switch (b)
            {
                case 0:
                    return FpuGeneral(op, m, xn);
                case 1:
                {
                    ushort args = r.ReadUInt16();
                    if ((args & 0xFFE0) != 0)
                        return null;
                    string cc = FpuConditions[args];
                    if (m == 1)
                    {
                        // fdbcc: the displacement is from its own word.
                        uint target = unchecked(PcHere() + (uint)r.ReadInt16());
                        flags |= M68kFlags.Branch | M68kFlags.Conditional;
                        AddFlowReference(target, M68kReferenceKind.Branch);
                        return Make("fdb" + cc, M68kSize.None, Dr(xn), new M68kBranchTarget(target));
                    }
                    if (m == 7 && xn is >= 2 and <= 4)
                        return TrapCc("ftrap" + cc, xn);
                    return ReadEa(m, xn, M68kSize.None) is { IsDataAlterable: true } ea ? Make("fs" + cc, M68kSize.None, ea.Operand) : null;
                }
                case 2:
                case 3:
                {
                    if ((op & 0x20) != 0)
                        return null;
                    uint pc = PcHere();
                    int disp = b == 2 ? r.ReadInt16() : r.ReadInt32();
                    // FNOP is FBF.W with a zero displacement.
                    if (op == 0xF280 && disp == 0)
                        return Make("fnop", M68kSize.None);
                    uint target = unchecked(pc + (uint)disp);
                    flags |= M68kFlags.Branch | M68kFlags.Conditional;
                    AddFlowReference(target, M68kReferenceKind.Branch);
                    return Make("fb" + FpuConditions[op & 0x1F], b == 2 ? M68kSize.Word : M68kSize.Long, new M68kBranchTarget(target));
                }
                case 4:
                    return ReadEa(m, xn, M68kSize.None) is { } save && (save.IsControlAlterable || save.Mode == PreDec)
                        ? Make("fsave", M68kSize.None, save.Operand) : null;
                case 5:
                    return ReadEa(m, xn, M68kSize.None) is { } restore && (restore.IsControl || restore.Mode == PostInc)
                        ? Make("frestore", M68kSize.None, restore.Operand) : null;
                default:
                    return null;
            }
        }

        private M68kInstruction? FpuGeneral(ushort op, int m, int xn)
        {
            ushort args = r.ReadUInt16();
            int which = args >> 13, u = (args >> 10) & 7, fp = (args >> 7) & 7, k = args & 0x7F;

            if (which == 2 && u == 7)
            {
                if ((op & 0x3F) != 0)
                    return null;
                hex = true;
                return Make("fmovecr", M68kSize.None, Quick(k), Fp(fp));
            }

            if (which == 3)
            {
                // fmove FPn,<ea>
                var format = FpuFormats[u];
                if (ReadEa(m, xn, M68kSize.None) is not { IsDataAlterable: true } ea || (ea.Mode == DReg && !IsIntegerOrSingle(u)))
                    return null;
                if (u == 3)
                {
                    int factor = (sbyte)(k << 1) >> 1;
                    return Build("fmove", format, Join(Fp(fp), ea.Operand) + "{#" + factor.ToString(CultureInfo.InvariantCulture) + "}",
                        [Fp(fp), ea.Operand, Quick(factor)]);
                }
                if (u == 7)
                {
                    if ((k & 0x0F) != 0)
                        return null;
                    var dn = Dr((k >> 4) & 7);
                    return Build("fmove", format, Join(Fp(fp), ea.Operand) + "{" + Format(dn) + "}", [Fp(fp), ea.Operand, dn]);
                }
                return Make("fmove", format, Fp(fp), ea.Operand);
            }

            if ((which & 5) == 0)
            {
                if (!FpuOperations.TryGetValue(k, out string? name) && (k & 0x78) != 0x30)
                    return null;
                M68kOperand source;
                M68kSize format;
                if ((which & 2) == 0)
                {
                    // FPm,FPn: the effective-address field is unused and 0.
                    if ((op & 0x3F) != 0)
                        return null;
                    (source, format) = (Fp(u), M68kSize.Extended);
                }
                else
                {
                    format = FpuFormats[u];
                    if (u == 7 || ReadEa(m, xn, format) is not { IsData: true } ea || (ea.Mode == DReg && !IsIntegerOrSingle(u)))
                        return null;
                    source = ea.Operand;
                }
                if ((k & 0x78) == 0x30)
                    return Make("fsincos", format, source, Pair(Fp(k & 7), Fp(fp)));
                if (k == 0x3A)
                    return Make("ftst", format, source);
                return Make(name!, format, source, Fp(fp));
            }

            if ((which & 6) == 4)
            {
                // fmove/fmovem of FPCR (bit 12), FPSR (11), FPIAR (10)
                bool toMemory = (which & 1) != 0;
                if ((args & 0x03FF) != 0 || u == 0)
                    return null;
                bool single = (u & (u - 1)) == 0;
                M68kOperand registers = single
                    ? new M68kRegisterOperand(M68kRegisterKind.FloatingPointControl, u)
                    : new M68kRegisterList((ushort)u, M68kRegisterListKind.FloatingPointControl);
                if (!single && !toMemory && m == 7 && xn == 4)
                {
                    // An immediate for two or three registers holds one long per register, FPCR first, then FPSR,
                    // then FPIAR: the FPU asks for 4 bytes per register and the CPU reads them from the instruction
                    // stream [Doc: MC68881/MC68882 User's Manual, FMOVEM]. (GNU as and Ghidra take one long.)
                    var operands = new List<M68kOperand>(4);
                    for (int bit = 4; bit != 0; bit >>= 1)
                        if ((u & bit) != 0)
                            operands.Add(ReadImmediate(M68kSize.Long)!);
                    operands.Add(registers);
                    return Make("fmovem", M68kSize.Long, [.. operands]);
                }
                if (ReadEa(m, xn, single ? M68kSize.Long : M68kSize.None) is not { } ea)
                    return null;
                // One register: any mode from memory, an alterable one to it, An only for FPIAR. Two or three: any
                // memory mode from memory, a memory alterable one to it [Doc: MC68881/MC68882 User's Manual, FMOVE
                // and FMOVEM of the control registers].
                bool ok = single
                    ? (!toMemory || ea.IsAlterable) && (ea.Mode != AReg || u == 1)
                    : toMemory ? ea.IsMemoryAlterable : ea.IsMemory;
                if (!ok)
                    return null;
                string name = single ? "fmove" : "fmovem";
                return toMemory ? Make(name, M68kSize.Long, registers, ea.Operand) : Make(name, M68kSize.Long, ea.Operand, registers);
            }

            if ((which & 6) != 6)
                return null;
            {
                // fmovem.x: mode bits 12-11 (bit 12 clear: predecrement; bit 11 set: the list in Dn bits 6-4).
                bool toMemory = (which & 1) != 0;
                int mode = (args >> 11) & 3;
                if ((args & 0x0700) != 0 || ReadEa(m, xn, M68kSize.None) is not { } ea)
                    return null;
                bool ok = toMemory ? ea.IsControlAlterable || ea.Mode == PreDec : ea.IsControl || ea.Mode == PostInc;
                bool predecrement = (mode & 2) == 0;
                if (!ok || predecrement != (ea.Mode == PreDec))
                    return null;
                // The postincrement/control mask has FP0 in bit 7; the predecrement mask has FP0 in bit 0.
                M68kOperand registers = (mode & 1) != 0
                    ? Dr((args >> 4) & 7)
                    : new M68kRegisterList(predecrement ? (ushort)(args & 0xFF) : Reverse(args & 0xFF, 8), M68kRegisterListKind.FloatingPoint);
                return toMemory ? Make("fmovem", M68kSize.Extended, registers, ea.Operand) : Make("fmovem", M68kSize.Extended, ea.Operand, registers);
            }
        }

        private static bool IsIntegerOrSingle(int format) => format is 0 or 1 or 4 or 6;

        // ---- effective addresses ----

        private uint PcHere() => unchecked(baseAddress + (uint)r.Position);

        // Reads the effective address in mode m, register xn; immediateSize is the size of an immediate operand, None
        // where the instruction does not allow one. Null for a mode that does not exist.
        private Ea? ReadEa(int m, int xn, M68kSize immediateSize)
        {
            switch (m)
            {
                case 0: return new Ea(DReg, Dr(xn));
                case 1: return new Ea(AReg, Ar(xn));
                case 2: return new Ea(Ind, Indirect(xn));
                case 3: return new Ea(PostInc, PostIncrement(xn));
                case 4: return new Ea(PreDec, new M68kEffectiveAddress(M68kAddressingMode.PreDecrement, xn, 0, null, 0, false, false, null));
                case 5: return new Ea(Disp, new M68kEffectiveAddress(M68kAddressingMode.Displacement, xn, r.ReadInt16(), null, 0, false, false, null));
                case 6: return ReadIndexed(false, xn);
            }
            switch (xn)
            {
                case 0:
                {
                    short value = r.ReadInt16();
                    return new Ea(Abs, new M68kEffectiveAddress(M68kAddressingMode.AbsoluteShort, 0, value, null, 0, false, false, unchecked((uint)value)));
                }
                case 1:
                {
                    int value = r.ReadInt32();
                    return new Ea(Abs, new M68kEffectiveAddress(M68kAddressingMode.AbsoluteLong, 0, value, null, 0, false, false, unchecked((uint)value)));
                }
                case 2:
                {
                    uint pc = PcHere();
                    short disp = r.ReadInt16();
                    uint target = unchecked(pc + (uint)disp);
                    AddDataReference(target);
                    return new Ea(PcDisp, new M68kEffectiveAddress(M68kAddressingMode.PcDisplacement, 0, disp, null, 0, false, false, target));
                }
                case 3:
                    return ReadIndexed(true, 0);
                case 4:
                    return ReadImmediate(immediateSize) is { } imm ? new Ea(Imm, imm) : null;
                default:
                    return null;
            }
        }

        // Mode 6 and mode 7 register 3: a brief or (68020) full extension word [Doc: M68000 Family Programmer's
        // Reference Manual, 2.2.6-2.2.17].
        private Ea? ReadIndexed(bool pcBased, int reg)
        {
            uint pc = PcHere();
            ushort ext = r.ReadUInt16();
            var index = new M68kIndex(GeneralRegister(ext >> 12), (ext & 0x0800) != 0, 1 << ((ext >> 9) & 3));
            if ((ext & 0x0100) == 0)
                return new Ea(pcBased ? PcIndex : Index, new M68kEffectiveAddress(
                    pcBased ? M68kAddressingMode.PcIndexed : M68kAddressingMode.Indexed, reg, (sbyte)ext, index, 0, false, false, null));

            // Bit 3 of the full extension word is drawn as 0.
            if ((ext & 0x08) != 0)
                return null;
            bool baseSuppressed = (ext & 0x80) != 0, indexSuppressed = (ext & 0x40) != 0;
            int bd;
            switch ((ext >> 4) & 3)
            {
                case 0: return null;
                case 1: bd = 0; break;
                case 2: bd = r.ReadInt16(); break;
                default: bd = r.ReadInt32(); break;
            }
            uint fixedBase = unchecked((pcBased && !baseSuppressed ? pc : 0) + (uint)bd);
            bool baseIsFixed = pcBased || baseSuppressed;
            var idx = indexSuppressed ? (M68kIndex?)null : index;
            int iis = ext & 7;
            if (iis == 0)
            {
                uint? where = baseIsFixed && idx is null ? fixedBase : null;
                if (pcBased && !baseSuppressed && where is uint w)
                    AddDataReference(w);
                return new Ea(pcBased ? PcIndex : Index, new M68kEffectiveAddress(
                    pcBased ? M68kAddressingMode.PcIndexed : M68kAddressingMode.Indexed, reg, bd, idx, 0, baseSuppressed, true, where));
            }
            if (iis == 4 || (indexSuppressed && iis > 4))
                return null;
            int od = (iis & 3) switch { 1 => 0, 2 => r.ReadInt16(), _ => r.ReadInt32() };
            bool post = (iis & 4) != 0;
            // The pointer's own address is fixed when nothing but the base displacement is added before it is read.
            uint? pointer = baseIsFixed && (post || idx is null) ? fixedBase : null;
            if (pcBased && !baseSuppressed && pointer is uint p)
                AddDataReference(p);
            var mode = pcBased
                ? (post ? M68kAddressingMode.PcMemoryIndirectPostIndexed : M68kAddressingMode.PcMemoryIndirectPreIndexed)
                : (post ? M68kAddressingMode.MemoryIndirectPostIndexed : M68kAddressingMode.MemoryIndirectPreIndexed);
            int cls = pcBased ? (post ? PcIndPost : PcIndPre) : (post ? IndPost : IndPre);
            return new Ea(cls, new M68kEffectiveAddress(mode, reg, bd, idx, od, baseSuppressed, true, pointer));
        }

        // An immediate operand of the given size; null when the instruction allows none. A byte immediate is the low
        // byte of its word; the high byte is ignored, as the processor ignores it [Doc: M68000 Family Programmer's
        // Reference Manual, "Immediate Data"]. Where an instruction's format draws the high byte as zeros
        // (ori/andi/eori to CCR, a static bit number, callm's argument count), the caller rejects a nonzero one.
        private M68kImmediate? ReadImmediate(M68kSize size)
        {
            int position = r.Position;
            long value;
            switch (size)
            {
                case M68kSize.Byte: value = r.ReadUInt16() & 0xFF; break;
                case M68kSize.Word: value = r.ReadUInt16(); break;
                case M68kSize.Long:
                case M68kSize.Single: value = r.ReadUInt32(); break;
                case M68kSize.Double: value = r.ReadInt64(); break;
                case M68kSize.Extended:
                case M68kSize.Packed:
                    r.ReadBytes(12);
                    value = 0;
                    break;
                default: return null;
            }
            byte[] bytes = r.Source.Slice(position, r.Position - position).ToArray();
            if (size == M68kSize.Long && Array.TrueForAll(bytes, c => c is >= 0x20 and <= 0x7E))
                comments.Add("'" + Encoding.ASCII.GetString(bytes) + "'");
            return new M68kImmediate(value, size, bytes);
        }

        // A signed word or long from the extension words (link, rtd, pack/unpk adjustments).
        private M68kImmediate ReadSigned(M68kSize size)
        {
            int position = r.Position;
            long value = size == M68kSize.Long ? r.ReadInt32() : r.ReadInt16();
            return new M68kImmediate(value, size, r.Source.Slice(position, r.Position - position).ToArray());
        }

        private void AddDataReference(uint target)
        {
            references.Add(new M68kReference(target, M68kReferenceKind.Data));
            comments.Add("$" + Hex(target, 4));
        }

        // A branch to an odd address is an address error; it is not a target.
        private void AddFlowReference(uint target, M68kReferenceKind kind)
        {
            if ((target & 1) == 0)
                references.Add(new M68kReference(target, kind));
        }

        // ---- building the instruction ----

        private M68kInstruction Make(string mnemonic, M68kSize size, params M68kOperand[] operands) =>
            Build(mnemonic, size, null, operands);

        private M68kInstruction Build(string mnemonic, M68kSize size, string? operandText, M68kOperand[] operands)
        {
            int length = r.Position - start;
            var words = new ushort[length / 2];
            for (int i = 0; i < words.Length; i++)
                words[i] = r.ReadUInt16At(start + 2 * i);
            string? comment = comments.Count > 0 ? string.Join(", ", comments) : null;
            string text;
            if ((flags & M68kFlags.ALine) != 0)
                text = operandText!;
            else
            {
                operandText ??= Join(operands);
                text = mnemonic + Suffix(size) + (operandText.Length > 0 ? " " + operandText : "");
            }
            if (comment is not null)
                text += "  ; " + comment;
            return new M68kInstruction(address, length, words, mnemonic, size, operands, flags, references.ToArray(), text, comment);
        }

        private string Suffix(M68kSize size) => size switch
        {
            M68kSize.None => "",
            M68kSize.Byte => shortForm ? ".s" : ".b",
            M68kSize.Word => ".w",
            M68kSize.Long => ".l",
            M68kSize.Single => ".s",
            M68kSize.Double => ".d",
            M68kSize.Extended => ".x",
            _ => ".p",
        };

        private string Join(params M68kOperand[] operands)
        {
            var sb = new StringBuilder();
            foreach (var operand in operands)
            {
                if (sb.Length > 0 && operand is not M68kBitField)
                    sb.Append(',');
                sb.Append(Format(operand));
            }
            return sb.ToString();
        }

        private string Format(M68kOperand operand) => operand switch
        {
            M68kRegisterOperand reg => RegisterName(reg),
            M68kRegisterPair pair => pair.Indirect
                ? "(" + RegisterName(pair.First) + "):(" + RegisterName(pair.Second) + ")"
                : RegisterName(pair.First) + ":" + RegisterName(pair.Second),
            M68kRegisterList list => FormatList(list),
            M68kImmediate imm => "#" + FormatImmediate(imm),
            M68kBranchTarget target => "$" + Hex(target.Address, 4),
            M68kBitField f => "{" + (f.OffsetIsRegister ? "d" : "") + f.Offset.ToString(CultureInfo.InvariantCulture) + ":"
                + (f.WidthIsRegister ? "d" : "") + f.Width.ToString(CultureInfo.InvariantCulture) + "}",
            M68kEffectiveAddress ea => FormatEa(ea),
            _ => throw new InvalidOperationException(),
        };

        // Integers in decimal from -4096 to 4095 and in hex beyond (and always in hex for a logical operation);
        // FPU single, double, extended and packed values as their bytes in hex.
        private string FormatImmediate(M68kImmediate imm)
        {
            if (imm.Size is M68kSize.Single or M68kSize.Double or M68kSize.Extended or M68kSize.Packed)
                return "$" + Convert.ToHexString([.. imm.Bytes]);
            int digits = imm.Size switch { M68kSize.Byte => 2, M68kSize.Word => 4, M68kSize.Long => 8, _ => 2 };
            if (hex)
                return "$" + Hex(imm.Value, digits);
            long signed = imm.Size switch
            {
                M68kSize.Byte => (sbyte)imm.Value,
                M68kSize.Word => (short)imm.Value,
                M68kSize.Long => (int)imm.Value,
                _ => imm.Value,
            };
            return signed is >= -4096 and <= 4095 ? signed.ToString(CultureInfo.InvariantCulture) : "$" + Hex(imm.Value, digits);
        }
    }

    // ---- operands and their text ----

    private static M68kRegisterOperand Dr(int n) => new(M68kRegisterKind.Data, n);
    private static M68kRegisterOperand Ar(int n) => new(M68kRegisterKind.Address, n);
    private static M68kRegisterOperand Fp(int n) => new(M68kRegisterKind.FloatingPoint, n);
    private static M68kRegisterOperand GeneralRegister(int field) => (field & 8) != 0 ? Ar(field & 7) : Dr(field & 7);
    private static M68kRegisterPair Pair(M68kRegisterOperand first, M68kRegisterOperand second) => new(first, second, false);
    private static M68kImmediate Quick(long value) => new(value, M68kSize.None, []);

    private static M68kEffectiveAddress Indirect(int reg) =>
        new(M68kAddressingMode.Indirect, reg, 0, null, 0, false, false, null);

    private static M68kEffectiveAddress PostIncrement(int reg) =>
        new(M68kAddressingMode.PostIncrement, reg, 0, null, 0, false, false, null);

    private static M68kEffectiveAddress PreDecrement(int reg) =>
        new(M68kAddressingMode.PreDecrement, reg, 0, null, 0, false, false, null);

    // abcd/sbcd/addx/subx/pack/unpk: Dn, or -(An) when the R/M bit (3) is set.
    private static M68kOperand XOperand(int m, int reg) => m == 1 ? PreDecrement(reg) : Dr(reg);

    private static ushort Reverse(int mask, int bits)
    {
        int result = 0;
        for (int i = 0; i < bits; i++)
            if ((mask & (1 << i)) != 0)
                result |= 1 << (bits - 1 - i);
        return (ushort)result;
    }

    private static string Hex(long value, int digits) =>
        (digits >= 8 ? ((uint)value).ToString("X8", CultureInfo.InvariantCulture)
            : value.ToString("X" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

    private static string Decimal(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string RegisterName(M68kRegisterOperand reg) => reg.Kind switch
    {
        M68kRegisterKind.Data => "d" + Decimal(reg.Number),
        M68kRegisterKind.Address => reg.Number == 7 ? "sp" : "a" + Decimal(reg.Number),
        M68kRegisterKind.FloatingPoint => "fp" + Decimal(reg.Number),
        M68kRegisterKind.StatusRegister => "sr",
        M68kRegisterKind.ConditionCodes => "ccr",
        M68kRegisterKind.UserStackPointer => "usp",
        M68kRegisterKind.Control => ControlRegisters[reg.Number],
        M68kRegisterKind.FloatingPointControl => reg.Number switch { 4 => "fpcr", 2 => "fpsr", _ => "fpiar" },
        M68kRegisterKind.MemoryManagement => (reg.Number & 0x3F) switch
        {
            0x02 => "tt0", 0x03 => "tt1", 0x10 => "tc", 0x11 => "drp", 0x12 => "srp", 0x13 => "crp", 0x14 => "cal",
            0x15 => "val", 0x16 => "scc", 0x17 => "ac", 0x18 => "mmusr", 0x19 => "pcsr",
            0x1C => "bad" + Decimal(reg.Number >> 6), _ => "bac" + Decimal(reg.Number >> 6),
        },
        _ => reg.Number switch { 1 => "dc", 2 => "ic", _ => "bc" },
    };

    // d3-d7/a2-a4, fp0-fp3/fp7, fpcr/fpsr/fpiar; A7 is written a7 in a list. An empty list (a mask of 0, which
    // moves nothing) is written #0, so the operand still shows.
    private static string FormatList(M68kRegisterList list)
    {
        if (list.Mask == 0)
            return "#0";
        if (list.Kind == M68kRegisterListKind.FloatingPointControl)
        {
            var names = new List<string>(3);
            if ((list.Mask & 4) != 0) names.Add("fpcr");
            if ((list.Mask & 2) != 0) names.Add("fpsr");
            if ((list.Mask & 1) != 0) names.Add("fpiar");
            return string.Join('/', names);
        }
        var parts = new List<string>();
        int groups = list.Kind == M68kRegisterListKind.Integer ? 2 : 1;
        for (int g = 0; g < groups; g++)
        {
            string prefix = list.Kind == M68kRegisterListKind.FloatingPoint ? "fp" : g == 0 ? "d" : "a";
            for (int i = 0; i < 8; i++)
            {
                if ((list.Mask & (1 << (g * 8 + i))) == 0)
                    continue;
                int j = i;
                while (j < 7 && (list.Mask & (1 << (g * 8 + j + 1))) != 0)
                    j++;
                parts.Add(j == i ? prefix + Decimal(i) : prefix + Decimal(i) + "-" + prefix + Decimal(j));
                i = j;
            }
        }
        return string.Join('/', parts);
    }

    private static string FormatIndex(M68kIndex index) =>
        RegisterName(index.Register) + (index.IsLong ? ".l" : ".w") + (index.Scale > 1 ? "*" + Decimal(index.Scale) : "");

    private static string FormatEa(M68kEffectiveAddress ea)
    {
        string an = ea.IsPcRelative ? (ea.BaseSuppressed ? "zpc" : "pc") : RegisterName(Ar(ea.Register));
        switch (ea.Mode)
        {
            case M68kAddressingMode.Indirect: return "(" + an + ")";
            case M68kAddressingMode.PostIncrement: return "(" + an + ")+";
            case M68kAddressingMode.PreDecrement: return "-(" + an + ")";
            case M68kAddressingMode.Displacement:
            case M68kAddressingMode.PcDisplacement:
                return Decimal(ea.BaseDisplacement) + "(" + an + ")";
            case M68kAddressingMode.AbsoluteShort: return "($" + Hex((ushort)ea.BaseDisplacement, 4) + ").w";
            case M68kAddressingMode.AbsoluteLong: return "($" + Hex(ea.BaseDisplacement, 8) + ").l";
        }

        // A suppressed address register is left out; a suppressed PC is written zpc so the mode still shows.
        string? baseName = ea.BaseSuppressed && !ea.IsPcRelative ? null : an;
        string? bd = ea.BaseDisplacement != 0 || (baseName is null && ea.Index is null)
            ? (ea.BaseSuppressed ? "$" + Hex(unchecked((uint)ea.BaseDisplacement), 4) : Decimal(ea.BaseDisplacement))
            : null;
        string? index = ea.Index is { } i ? FormatIndex(i) : null;
        string? od = ea.OuterDisplacement != 0 ? Decimal(ea.OuterDisplacement) : null;
        switch (ea.Mode)
        {
            case M68kAddressingMode.Indexed:
            case M68kAddressingMode.PcIndexed:
                if (!ea.FullExtension)
                    return Decimal(ea.BaseDisplacement) + "(" + an + "," + index + ")";
                return "(" + Parts(bd, baseName, index) + ")";
            case M68kAddressingMode.MemoryIndirectPostIndexed:
            case M68kAddressingMode.PcMemoryIndirectPostIndexed:
                return "([" + Parts(bd, baseName) + "]" + (index is null ? "" : "," + index) + (od is null ? "" : "," + od) + ")";
            default:
                return "([" + Parts(bd, baseName, index) + "]" + (od is null ? "" : "," + od) + ")";
        }
    }

    private static string Parts(params string?[] parts)
    {
        string joined = string.Join(',', Array.FindAll(parts, p => p is not null));
        return joined.Length > 0 ? joined : "0";
    }
}
