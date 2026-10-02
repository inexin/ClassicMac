// The instruction tables, field layouts and reserved-bit checks are ported from resource_dasm's PPC32Emulator
// (src/Emulators/PPC32Emulator.cc, the dasm_* half; MIT, Copyright (c) 2023 Martin Michelsen; see
// THIRD-PARTY-NOTICES.md), rewritten for standard assembler syntax and structured operands, with AltiVec and the
// extended mnemonics added and its slips fixed: it rejects srw. and stwcx., swaps orc's operands, prints the
// multiply-adds and fsel as frD,frA,frB,frC, and treats part of the X-form opcode of fmr, fneg, fabs and fnabs
// as reserved [Doc: PowerPC Microprocessor Family: The Programming Environments for 32-Bit Microprocessors; AltiVec
// Technology Programming Environments Manual]. Checked against Ghidra over Disk Copy 6.5's code section: every one of
// its 172 278 instructions has the same mnemonic family and operands [Verified: Disk Copy 6.5].
using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Code.Disassembly;

/// <summary>
/// Decodes 32-bit PowerPC instructions: the user instruction set (integer, logical, rotate, loads and stores,
/// branches, CR and floating point), the operating-environment and virtual-environment operations drivers use (cache,
/// TLB, segment registers, MSR, time base, synchronisation) and AltiVec. Extended mnemonics are used where they apply
/// (<c>mflr</c>, <c>li</c>, <c>mr</c>, <c>subi</c>, <c>cmpwi</c>, <c>nop</c>, <c>blr</c>, <c>beq+</c> ...). POWER-only
/// and 601-only operations, 64-bit operations, undefined opcodes, words with reserved bits set and the forms the PEM
/// calls invalid (an update with rA = 0, mftb of another TBR ...) decode as <c>.long</c>
/// (docs/formats/output/disassembly.md §2.6).
/// </summary>
public static class PpcDisassembler
{
    /// <summary>Decodes one instruction word fetched from <paramref name="address"/>.</summary>
    public static PpcInstruction Decode(uint word, uint address)
    {
        int primary = (int)(word >> 26);
        if (primary is 16 or 18 || primary == 19 && Xo10(word) is 16 or 528)
            if (DecodeBranch(word, address) is { } branch) return branch;
        if (DecodeOther(word) is { } other)
            return new PpcInstruction(address, word, other.Item1, other.Item2, PpcFlow.None, null);
        return new PpcInstruction(address, word, ".long", [Hex(word)], PpcFlow.None, null);
    }

    /// <summary>Decodes the big-endian words of <paramref name="code"/>, the first at <paramref name="address"/>. A
    /// trailing partial word is left out.</summary>
    public static IEnumerable<PpcInstruction> Disassemble(ReadOnlyMemory<byte> code, uint address)
    {
        var reader = new BigEndianReader(code);
        for (uint pc = address; reader.Remaining >= 4; pc += 4)
            yield return Decode(reader.ReadUInt32(), pc);
    }

    /// <summary>The name of special-purpose register <paramref name="spr"/> (<c>xer</c>, <c>lr</c>, <c>ctr</c>,
    /// <c>srr0</c>, <c>sprg0</c>, <c>vrsave</c>, <c>ibat0u</c> ...), or null for one without a name here.</summary>
    public static string? SprName(int spr) => spr switch
    {
        1 => "xer", 8 => "lr", 9 => "ctr", 18 => "dsisr", 19 => "dar", 22 => "dec", 25 => "sdr1", 26 => "srr0",
        27 => "srr1", 256 => "vrsave", 272 => "sprg0", 273 => "sprg1", 274 => "sprg2", 275 => "sprg3", 282 => "ear",
        284 => "tbl", 285 => "tbu", 287 => "pvr",
        >= 528 and <= 543 => ((spr & 8) != 0 ? "dbat" : "ibat") + ((spr >> 1) & 3) + ((spr & 1) != 0 ? "l" : "u"),
        1013 => "dabr",
        _ => null,
    };

    // ---- fields (the PEM's bit numbering counts from the most significant bit; these are shifts from the least) ----

    private static int D(uint w) => (int)(w >> 21) & 31;
    private static int A(uint w) => (int)(w >> 16) & 31;
    private static int B(uint w) => (int)(w >> 11) & 31;
    private static int C(uint w) => (int)(w >> 6) & 31;
    private static int Xo10(uint w) => (int)(w >> 1) & 0x3FF;
    private static int Xo5(uint w) => (int)(w >> 1) & 31;
    private static int CrfD(uint w) => (int)(w >> 23) & 7;
    private static short Simm(uint w) => (short)w;
    private static ushort Uimm(uint w) => (ushort)w;
    private static bool Rc(uint w) => (w & 1) != 0;
    private static string Dot(uint w) => Rc(w) ? "." : "";

    private static PpcOperand R(int n) => new(PpcOperandKind.Gpr, n);
    private static PpcOperand F(int n) => new(PpcOperandKind.Fpr, n);
    private static PpcOperand V(int n) => new(PpcOperandKind.Vr, n);
    private static PpcOperand Cr(int n) => new(PpcOperandKind.CrField, n);
    private static PpcOperand Bit(int n) => new(PpcOperandKind.CrBit, n);
    private static PpcOperand Imm(long n) => new(PpcOperandKind.Immediate, n);
    private static PpcOperand Hex(long n) => new(PpcOperandKind.Immediate, n, Hex: true);
    private static PpcOperand Disp(int d, int baseRegister) => new(PpcOperandKind.Displacement, d, baseRegister);
    private static PpcOperand Spr(int n) => new(PpcOperandKind.Spr, n);
    // rA|0: an rA field of 0 means the value 0, not r0.
    private static PpcOperand RA0(int n) => n == 0 ? Imm(0) : R(n);

    private static (string, PpcOperand[])? Op(string mnemonic, params PpcOperand[] operands) => (mnemonic, operands);

    // ---- branches: b (18), bc (16), bclr and bcctr (19) ----

    private static readonly string[] TrueConditions = ["lt", "gt", "eq", "so"];
    private static readonly string[] FalseConditions = ["ge", "le", "ne", "ns"];

    private static PpcInstruction? DecodeBranch(uint w, uint address)
    {
        bool link = (w & 1) != 0;
        bool absolute = (w & 2) != 0;
        int primary = (int)(w >> 26);
        if (primary == 18)
        {
            // LI: a 24-bit word displacement, sign-extended from bit 25.
            int li = (int)(w << 6) >> 6 & ~3;
            uint target = absolute ? (uint)li : address + (uint)li;
            var flow = PpcFlow.Branch | (link ? PpcFlow.Call : 0);
            return new PpcInstruction(address, w, "b" + (link ? "l" : "") + (absolute ? "a" : ""),
                [new PpcOperand(PpcOperandKind.BranchTarget, target)], flow, target);
        }

        int bo = D(w), bi = A(w);
        bool toLr = primary == 19 && Xo10(w) == 16;
        bool toCtr = primary == 19 && Xo10(w) == 528;
        if (toLr || toCtr)
        {
            if ((w & 0x0000F800) != 0) return null;     // bits 16-20 reserved
            if (toCtr && (bo & 4) == 0) return null;    // bcctr may not decrement CTR
            absolute = false;
        }
        int bd = (short)(w & 0xFFFC);
        uint? branchTarget = primary == 16 ? (absolute ? (uint)bd : address + (uint)bd) : null;
        string register = toLr ? "lr" : toCtr ? "ctr" : "";
        string suffix = register + (link ? "l" : "") + (absolute ? "a" : "");

        var operands = new List<PpcOperand>();
        string mnemonic;
        bool always = (bo & 0x14) == 0x14;
        if (always && primary != 16 && bo == 20 && bi == 0)
            mnemonic = "b" + suffix;   // blr, bctr
        else if (always || (bo & 0x10) != 0 && bi != 0)
        {
            // BO = 1z1zz (branch always) and the CTR-only BOs 1z00y and 1z01y ignore BI. Where BI is not 0, or (branch
            // always) BO is not 20, the extended mnemonic would lose it: the raw form keeps BO and BI, so the text
            // gives the word back. A branch-always bc is always raw (resource_dasm's choice: b is the I-form).
            mnemonic = "bc" + suffix;
            operands.Add(Imm(bo));
            operands.Add(Imm(bi));
        }
        else
        {
            string condition;
            if ((bo & 0x10) != 0)       // 1z00y / 1z01y: CTR only
                condition = (bo & 2) != 0 ? "dz" : "dnz";
            else if ((bo & 0x04) != 0)  // 001zy / 011zy: the CR bit only
            {
                condition = ((bo & 8) != 0 ? TrueConditions : FalseConditions)[bi & 3];
                if (bi >> 2 != 0) operands.Add(Cr(bi >> 2));
            }
            else                        // 0000y / 0001y / 0100y / 0101y: CTR and the CR bit
            {
                condition = ((bo & 2) != 0 ? "dz" : "dnz") + ((bo & 8) != 0 ? "t" : "f");
                operands.Add(Bit(bi));
            }
            // The y bit reverses the static prediction: a backward bc is predicted taken, a forward bc and bclr/bcctr
            // not taken. With y set, "+" marks a branch now predicted taken, "-" one predicted not taken.
            string hint = (bo & 1) == 0 ? "" : primary == 16 && bd < 0 ? "-" : "+";
            mnemonic = "b" + condition + suffix + hint;
        }
        if (branchTarget is { } t) operands.Add(new PpcOperand(PpcOperandKind.BranchTarget, t));
        var branchFlow = PpcFlow.Branch | (link ? PpcFlow.Call : 0) | (toLr && !link ? PpcFlow.Return : 0)
            | (always ? 0 : PpcFlow.Conditional);
        return new PpcInstruction(address, w, mnemonic, operands, branchFlow, branchTarget);
    }

    // ---- everything else ----

    private static (string, PpcOperand[])? DecodeOther(uint w)
    {
        int d = D(w), a = A(w), b = B(w);
        switch (w >> 26)
        {
            case 3: return Trap(w, true);
            case 4: return AltiVec(w);
            case 7: return Op("mulli", R(d), R(a), Imm(Simm(w)));
            case 8: return Op("subfic", R(d), R(a), Imm(Simm(w)));
            case 10:
                if ((w & 0x00600000) != 0) return null;   // bit 9 reserved, L = 1 is cmpldi
                return CrfD(w) == 0 ? Op("cmplwi", R(a), Imm(Uimm(w))) : Op("cmplwi", Cr(CrfD(w)), R(a), Imm(Uimm(w)));
            case 11:
                if ((w & 0x00600000) != 0) return null;
                return CrfD(w) == 0 ? Op("cmpwi", R(a), Imm(Simm(w))) : Op("cmpwi", Cr(CrfD(w)), R(a), Imm(Simm(w)));
            case 12 or 13:
            {
                string dot = (w >> 26) == 13 ? "." : "";
                return Simm(w) < 0 ? Op("subic" + dot, R(d), R(a), Imm(-Simm(w))) : Op("addic" + dot, R(d), R(a), Imm(Simm(w)));
            }
            case 14:
                if (a == 0) return Op("li", R(d), Imm(Simm(w)));
                return Simm(w) < 0 ? Op("subi", R(d), R(a), Imm(-Simm(w))) : Op("addi", R(d), R(a), Imm(Simm(w)));
            case 15:
                if (a == 0) return Op("lis", R(d), Imm(Simm(w)));
                return Simm(w) < 0 ? Op("subis", R(d), R(a), Imm(-Simm(w))) : Op("addis", R(d), R(a), Imm(Simm(w)));
            case 17: return w == 0x44000002 ? Op("sc") : null;
            case 19: return Primary19(w);
            case 20: return Op("rlwimi" + Dot(w), R(a), R(d), Imm(b), Imm(C(w)), Imm(Xo5(w)));
            case 21: return Rlwinm(w);
            case 23:
                return C(w) == 0 && Xo5(w) == 31 ? Op("rotlw" + Dot(w), R(a), R(d), R(b))
                    : Op("rlwnm" + Dot(w), R(a), R(d), R(b), Imm(C(w)), Imm(Xo5(w)));
            case 24: return w == 0x60000000 ? Op("nop") : Op("ori", R(a), R(d), Hex(Uimm(w)));
            case 25: return Op("oris", R(a), R(d), Hex(Uimm(w)));
            case 26: return Op("xori", R(a), R(d), Hex(Uimm(w)));
            case 27: return Op("xoris", R(a), R(d), Hex(Uimm(w)));
            case 28: return Op("andi.", R(a), R(d), Hex(Uimm(w)));
            case 29: return Op("andis.", R(a), R(d), Hex(Uimm(w)));
            case 31: return Primary31(w);
            case >= 32 and <= 55:
            {
                int primary = (int)(w >> 26);
                if (primary != 47 && (primary & 1) != 0 && !ValidUpdate(a, d, IntegerLoad(primary)))
                    return null;
                var data = primary >= 48 ? F(d) : R(d);
                return Op(LoadStoreNames[primary - 32], data, Disp(Simm(w), a));
            }
            case 59: return FloatSingle(w);
            case 63: return FloatDouble(w);
            default: return null;   // 0-2, 5, 6, 9 (dozi), 22 (rlmi), 30 and 58/62 (64-bit), 56, 57, 60, 61
        }
    }

    // An update form with rA = 0, or an integer load with update with rA = rD, is invalid [Doc: PEM, "lwzu" ...
    // "stfdux"].
    private static bool ValidUpdate(int a, int d, bool integerLoad) => a != 0 && !(integerLoad && a == d);

    // lwzu, lbzu, lhzu, lhau (primaries 33, 35, 41, 43).
    private static bool IntegerLoad(int primary) => primary is 33 or 35 or 41 or 43;

    private static readonly string[] LoadStoreNames =
    [
        "lwz", "lwzu", "lbz", "lbzu", "stw", "stwu", "stb", "stbu", "lhz", "lhzu", "lha", "lhau", "sth", "sthu", "lmw",
        "stmw", "lfs", "lfsu", "lfd", "lfdu", "stfs", "stfsu", "stfd", "stfdu",
    ];

    // rlwinm and its extended forms, in the order the PEM's appendix F lists them.
    private static (string, PpcOperand[])? Rlwinm(uint w)
    {
        int s = D(w), a = A(w), sh = B(w), mb = C(w), me = Xo5(w);
        string dot = Dot(w);
        if (mb == 0 && me == 31) return Op("rotlwi" + dot, R(a), R(s), Imm(sh));
        if (sh == 0 && me == 31) return Op("clrlwi" + dot, R(a), R(s), Imm(mb));
        if (sh == 0 && mb == 0) return Op("clrrwi" + dot, R(a), R(s), Imm(31 - me));
        if (mb == 0 && me == 31 - sh) return Op("slwi" + dot, R(a), R(s), Imm(sh));
        if (me == 31 && sh == 32 - mb) return Op("srwi" + dot, R(a), R(s), Imm(mb));
        return Op("rlwinm" + dot, R(a), R(s), Imm(sh), Imm(mb), Imm(me));
    }

    // tw/twi with the TO extended mnemonics [Doc: PEM appendix F.6].
    private static (string, PpcOperand[])? Trap(uint w, bool immediate)
    {
        int to = D(w), a = A(w);
        if (!immediate && (w & 1) != 0) return null;
        if (!immediate && to == 31 && a == 0 && B(w) == 0) return Op("trap");
        var last = immediate ? Imm(Simm(w)) : R(B(w));
        string? condition = to switch
        {
            16 => "lt", 20 => "le", 4 => "eq", 12 => "ge", 8 => "gt", 24 => "ne",
            2 => "llt", 6 => "lle", 5 => "lge", 1 => "lgt", _ => null,
        };
        string i = immediate ? "i" : "";
        return condition is null ? Op("tw" + i, Imm(to), R(a), last) : Op("tw" + condition + i, R(a), last);
    }

    // ---- primary 19: CR operations, isync, rfi (bclr and bcctr are branches) ----

    private static (string, PpcOperand[])? Primary19(uint w)
    {
        int d = D(w), a = A(w), b = B(w);
        switch (Xo10(w))
        {
            case 0: return (w & 0x0063F801) != 0 ? null : Op("mcrf", Cr(CrfD(w)), Cr(a >> 2));
            case 50: return w == 0x4C000064 ? Op("rfi") : null;
            case 150: return w == 0x4C00012C ? Op("isync") : null;
        }
        string? name = Xo10(w) switch
        {
            33 => "crnor", 129 => "crandc", 193 => "crxor", 225 => "crnand", 257 => "crand", 289 => "creqv",
            417 => "crorc", 449 => "cror", _ => null,
        };
        if (name is null || Rc(w)) return null;
        if (d == a && a == b && name == "crxor") return Op("crclr", Bit(d));
        if (d == a && a == b && name == "creqv") return Op("crset", Bit(d));
        if (a == b && name == "cror") return Op("crmove", Bit(d), Bit(a));
        if (a == b && name == "crnor") return Op("crnot", Bit(d), Bit(a));
        return Op(name, Bit(d), Bit(a), Bit(b));
    }

    // ---- primary 31 ----

    private enum Form { Indexed, Update, Float, FloatUpdate, Vector }

    private static readonly Dictionary<int, (string Name, Form Form)> IndexedLoadsAndStores = new()
    {
        [20] = ("lwarx", Form.Indexed), [23] = ("lwzx", Form.Indexed), [55] = ("lwzux", Form.Update),
        [87] = ("lbzx", Form.Indexed), [119] = ("lbzux", Form.Update), [151] = ("stwx", Form.Indexed),
        [183] = ("stwux", Form.Update), [215] = ("stbx", Form.Indexed), [247] = ("stbux", Form.Update),
        [279] = ("lhzx", Form.Indexed), [311] = ("lhzux", Form.Update), [343] = ("lhax", Form.Indexed),
        [375] = ("lhaux", Form.Update), [407] = ("sthx", Form.Indexed), [439] = ("sthux", Form.Update),
        [533] = ("lswx", Form.Indexed), [534] = ("lwbrx", Form.Indexed), [661] = ("stswx", Form.Indexed),
        [662] = ("stwbrx", Form.Indexed), [790] = ("lhbrx", Form.Indexed), [918] = ("sthbrx", Form.Indexed),
        [310] = ("eciwx", Form.Indexed), [438] = ("ecowx", Form.Indexed),
        [535] = ("lfsx", Form.Float), [567] = ("lfsux", Form.FloatUpdate), [599] = ("lfdx", Form.Float),
        [631] = ("lfdux", Form.FloatUpdate), [663] = ("stfsx", Form.Float), [695] = ("stfsux", Form.FloatUpdate),
        [727] = ("stfdx", Form.Float), [759] = ("stfdux", Form.FloatUpdate), [983] = ("stfiwx", Form.Float),
        [6] = ("lvsl", Form.Vector), [38] = ("lvsr", Form.Vector), [7] = ("lvebx", Form.Vector),
        [39] = ("lvehx", Form.Vector), [71] = ("lvewx", Form.Vector), [103] = ("lvx", Form.Vector),
        [359] = ("lvxl", Form.Vector), [135] = ("stvebx", Form.Vector), [167] = ("stvehx", Form.Vector),
        [199] = ("stvewx", Form.Vector), [231] = ("stvx", Form.Vector), [487] = ("stvxl", Form.Vector),
    };

    // XO-form arithmetic, by the 9-bit extended opcode (bit 21, OE, is the 10th): true for rD,rA,rB, false for rD,rA.
    private static readonly Dictionary<int, (string Name, bool ThreeOperands)> Arithmetic = new()
    {
        [8] = ("subfc", true), [10] = ("addc", true), [40] = ("subf", true), [104] = ("neg", false),
        [136] = ("subfe", true), [138] = ("adde", true), [200] = ("subfze", false), [202] = ("addze", false),
        [232] = ("subfme", false), [234] = ("addme", false), [235] = ("mullw", true), [266] = ("add", true),
        [459] = ("divwu", true), [491] = ("divw", true),
    };

    // X-form logical operations rA,rS,rB with Rc.
    private static readonly Dictionary<int, string> Logical = new()
    {
        [24] = "slw", [28] = "and", [60] = "andc", [124] = "nor", [284] = "eqv", [316] = "xor", [412] = "orc",
        [444] = "or", [476] = "nand", [536] = "srw", [792] = "sraw",
    };

    // Cache operations RA|0,rB.
    private static readonly Dictionary<int, string> CacheOps = new()
    {
        [54] = "dcbst", [86] = "dcbf", [246] = "dcbtst", [278] = "dcbt", [470] = "dcbi", [758] = "dcba",
        [982] = "icbi", [1014] = "dcbz",
    };

    private static (string, PpcOperand[])? Primary31(uint w)
    {
        int d = D(w), a = A(w), b = B(w), xo = Xo10(w);
        if (Arithmetic.TryGetValue(xo & 0x1FF, out var arith))
        {
            string name = arith.Name + ((w & 0x400) != 0 ? "o" : "") + Dot(w);
            if (arith.ThreeOperands) return Op(name, R(d), R(a), R(b));
            return b != 0 ? null : Op(name, R(d), R(a));
        }
        if (xo is 11 or 75) return Op((xo == 11 ? "mulhwu" : "mulhw") + Dot(w), R(d), R(a), R(b));
        if (Logical.TryGetValue(xo, out var logical))
        {
            if (xo == 444 && d == b) return Op("mr" + Dot(w), R(a), R(d));
            if (xo == 124 && d == b) return Op("not" + Dot(w), R(a), R(d));
            return Op(logical + Dot(w), R(a), R(d), R(b));
        }
        switch (xo)
        {
            case 26 or 922 or 954:
                return b != 0 ? null : Op((xo == 26 ? "cntlzw" : xo == 922 ? "extsh" : "extsb") + Dot(w), R(a), R(d));
            case 824: return Op("srawi" + Dot(w), R(a), R(d), Imm(b));
            case 150: return Rc(w) ? Op("stwcx.", R(d), RA0(a), R(b)) : null;
        }
        // Everything below has bit 31 reserved.
        if (Rc(w)) return null;
        if (IndexedLoadsAndStores.TryGetValue(xo, out var ls))
        {
            var data = ls.Form switch { Form.Float or Form.FloatUpdate => F(d), Form.Vector => V(d), _ => R(d) };
            bool update = ls.Form is Form.Update or Form.FloatUpdate;
            if (update && !ValidUpdate(a, d, ls.Form == Form.Update && ls.Name[0] == 'l')) return null;
            var baseRegister = update ? R(a) : RA0(a);
            return Op(ls.Name, data, baseRegister, R(b));
        }
        if (CacheOps.TryGetValue(xo, out var cache)) return d != 0 ? null : Op(cache, RA0(a), R(b));
        int spr = a | b << 5;
        switch (xo)
        {
            case 0 or 32:
                if ((w & 0x00600000) != 0) return null;   // bit 9 reserved, L = 1 is cmpd/cmpld
                string cmp = xo == 0 ? "cmpw" : "cmplw";
                return CrfD(w) == 0 ? Op(cmp, R(a), R(b)) : Op(cmp, Cr(CrfD(w)), R(a), R(b));
            case 4: return Trap(w, false);
            case 19: return (w & 0x001FF800) != 0 ? null : Op("mfcr", R(d));
            case 83: return (w & 0x001FF800) != 0 ? null : Op("mfmsr", R(d));
            case 146: return (w & 0x001FF800) != 0 ? null : Op("mtmsr", R(d));
            case 144:
            {
                if ((w & 0x00100800) != 0) return null;
                int crm = (int)(w >> 12) & 0xFF;
                return crm == 0xFF ? Op("mtcr", R(d)) : Op("mtcrf", Hex(crm), R(d));
            }
            case 512: return (w & 0x007FF800) != 0 ? null : Op("mcrxr", Cr(CrfD(w)));
            case 339:
                return spr switch
                {
                    1 => Op("mfxer", R(d)), 8 => Op("mflr", R(d)), 9 => Op("mfctr", R(d)),
                    // TBL and TBU are written with mtspr, read with mftb [Doc: PEM, "mfspr"]
                    284 or 285 => Op("mfspr", R(d), Imm(spr)),
                    _ => Op("mfspr", R(d), Spr(spr)),
                };
            case 467:
                return spr switch
                {
                    1 => Op("mtxer", R(d)), 8 => Op("mtlr", R(d)), 9 => Op("mtctr", R(d)),
                    287 => Op("mtspr", Imm(spr), R(d)),   // PVR is read only [Doc: PEM, "mtspr"]
                    _ => Op("mtspr", Spr(spr), R(d)),
                };
            // Any TBR but TBL (268) and TBU (269) makes the form invalid [Doc: PEM, "mftb"].
            case 371: return spr switch { 268 => Op("mftb", R(d)), 269 => Op("mftbu", R(d)), _ => null };
            case 595: return (w & 0x0010F800) != 0 ? null : Op("mfsr", R(d), Imm(a & 15));
            case 210: return (w & 0x0010F800) != 0 ? null : Op("mtsr", Imm(a & 15), R(d));
            case 659: return a != 0 ? null : Op("mfsrin", R(d), R(b));
            case 242: return a != 0 ? null : Op("mtsrin", R(d), R(b));
            case 306: return (w & 0x03FF0000) != 0 ? null : Op("tlbie", R(b));
            // The 603's software table walk [Doc: PEM, "tlbld", "tlbli" (603e)]: rD and rA reserved.
            case 978: return (w & 0x03FF0000) != 0 ? null : Op("tlbld", R(b));
            case 1010: return (w & 0x03FF0000) != 0 ? null : Op("tlbli", R(b));
            case 370: return w == 0x7C0002E4 ? Op("tlbia") : null;
            case 566: return w == 0x7C00046C ? Op("tlbsync") : null;
            case 598: return w == 0x7C0004AC ? Op("sync") : null;
            case 854: return w == 0x7C0006AC ? Op("eieio") : null;
            case 597: return Op("lswi", R(d), RA0(a), Imm(b == 0 ? 32 : b));
            case 725: return Op("stswi", R(d), RA0(a), Imm(b == 0 ? 32 : b));
            case 342 or 374:
            {
                // dst/dstst rA,rB,STRM: T in the rD field's top bit, STRM in its low two, the two between reserved.
                if ((d & 0x0C) != 0) return null;
                string name = (xo == 342 ? "dst" : "dstst") + ((d & 0x10) != 0 ? "t" : "");
                return Op(name, R(a), R(b), Imm(d & 3));
            }
            case 822:
                // dss STRM / dssall (A = 1): the rA and rB fields and bits 7-8 reserved.
                if ((d & 0x0C) != 0 || a != 0 || b != 0) return null;
                return (d & 0x10) != 0 ? Op("dssall") : Op("dss", Imm(d & 3));
        }
        return null;
    }

    // ---- floating point: primary 59 (single) and 63 (double, compares, FPSCR) ----

    private static (string, PpcOperand[])? FloatArithmetic(uint w, string name)
    {
        int d = D(w), a = A(w), b = B(w), c = C(w);
        name += Dot(w);
        return Xo5(w) switch
        {
            18 or 20 or 21 => c != 0 ? null : Op(name, F(d), F(a), F(b)),
            22 or 24 or 26 => a != 0 || c != 0 ? null : Op(name, F(d), F(b)),
            25 => b != 0 ? null : Op(name, F(d), F(a), F(c)),
            _ => Op(name, F(d), F(a), F(c), F(b)),   // fsel and the multiply-adds: frD,frA,frC,frB
        };
    }

    private static (string, PpcOperand[])? FloatSingle(uint w)
    {
        string? name = Xo5(w) switch
        {
            18 => "fdivs", 20 => "fsubs", 21 => "fadds", 22 => "fsqrts", 24 => "fres", 25 => "fmuls", 28 => "fmsubs",
            29 => "fmadds", 30 => "fnmsubs", 31 => "fnmadds", _ => null,
        };
        return name is null ? null : FloatArithmetic(w, name);
    }

    private static (string, PpcOperand[])? FloatDouble(uint w)
    {
        if ((Xo5(w) & 0x10) != 0)
        {
            string? name = Xo5(w) switch
            {
                18 => "fdiv", 20 => "fsub", 21 => "fadd", 22 => "fsqrt", 23 => "fsel", 25 => "fmul", 26 => "frsqrte",
                28 => "fmsub", 29 => "fmadd", 30 => "fnmsub", 31 => "fnmadd", _ => null,
            };
            return name is null ? null : FloatArithmetic(w, name);
        }
        int d = D(w), a = A(w), b = B(w), xo = Xo10(w);
        string dot = Dot(w);
        switch (xo)
        {
            case 0 or 32:
                return (w & 0x00600001) != 0 ? null : Op(xo == 0 ? "fcmpu" : "fcmpo", Cr(CrfD(w)), F(a), F(b));
            case 12 or 14 or 15 or 40 or 72 or 136 or 264:
            {
                string name = xo switch
                {
                    12 => "frsp", 14 => "fctiw", 15 => "fctiwz", 40 => "fneg", 72 => "fmr", 136 => "fnabs", _ => "fabs",
                };
                return a != 0 ? null : Op(name + dot, F(d), F(b));
            }
            case 38 or 70:
                return (w & 0x001FF800) != 0 ? null : Op((xo == 38 ? "mtfsb1" : "mtfsb0") + dot, Imm(d));
            case 64: return (w & 0x0063F801) != 0 ? null : Op("mcrfs", Cr(CrfD(w)), Cr(a >> 2));
            // crfD here numbers an FPSCR field, so it prints as a number, not cr7 [Doc: PEM, "mtfsfi"].
            case 134: return (w & 0x007F0800) != 0 ? null : Op("mtfsfi" + dot, Imm(CrfD(w)), Hex((w >> 12) & 15));
            case 583: return (w & 0x001FF800) != 0 ? null : Op("mffs" + dot, F(d));
            case 711: return (w & 0x02010000) != 0 ? null : Op("mtfsf" + dot, Hex((w >> 17) & 0xFF), F(b));
        }
        return null;   // fctid, fctidz, fcfid (64-bit) and undefined
    }

    // ---- AltiVec: primary 4 [Doc: AltiVec PEM, ch. 6 and appendix A] ----

    private enum VectorForm { DAB, DB, DBUimm, DSimm, D, B }

    private static readonly Dictionary<int, (string Name, VectorForm Form)> VectorVx = BuildVx();

    private static Dictionary<int, (string, VectorForm)> BuildVx()
    {
        var t = new Dictionary<int, (string, VectorForm)>();
        void Add(VectorForm form, params (int Xo, string Name)[] ops)
        {
            foreach (var (xo, name) in ops) t.Add(xo, (name, form));
        }
        Add(VectorForm.DAB,
            (0, "vaddubm"), (2, "vmaxub"), (4, "vrlb"), (8, "vmuloub"), (10, "vaddfp"), (12, "vmrghb"), (14, "vpkuhum"),
            (64, "vadduhm"), (66, "vmaxuh"), (68, "vrlh"), (72, "vmulouh"), (74, "vsubfp"), (76, "vmrghh"), (78, "vpkuwum"),
            (128, "vadduwm"), (130, "vmaxuw"), (132, "vrlw"), (140, "vmrghw"), (142, "vpkuhus"), (206, "vpkuwus"),
            (258, "vmaxsb"), (260, "vslb"), (264, "vmulosb"), (268, "vmrglb"), (270, "vpkshus"),
            (322, "vmaxsh"), (324, "vslh"), (328, "vmulosh"), (332, "vmrglh"), (334, "vpkswus"),
            (384, "vaddcuw"), (386, "vmaxsw"), (388, "vslw"), (396, "vmrglw"), (398, "vpkshss"),
            (452, "vsl"), (462, "vpkswss"),
            (512, "vaddubs"), (514, "vminub"), (516, "vsrb"), (520, "vmuleub"),
            (576, "vadduhs"), (578, "vminuh"), (580, "vsrh"), (584, "vmuleuh"),
            (640, "vadduws"), (642, "vminuw"), (644, "vsrw"), (708, "vsr"),
            (768, "vaddsbs"), (770, "vminsb"), (772, "vsrab"), (776, "vmulesb"), (782, "vpkpx"),
            (832, "vaddshs"), (834, "vminsh"), (836, "vsrah"), (840, "vmulesh"),
            (896, "vaddsws"), (898, "vminsw"), (900, "vsraw"),
            (1024, "vsububm"), (1026, "vavgub"), (1028, "vand"), (1034, "vmaxfp"), (1036, "vslo"),
            (1088, "vsubuhm"), (1090, "vavguh"), (1092, "vandc"), (1098, "vminfp"), (1100, "vsro"),
            (1152, "vsubuwm"), (1154, "vavguw"), (1156, "vor"), (1220, "vxor"),
            (1282, "vavgsb"), (1284, "vnor"), (1346, "vavgsh"), (1408, "vsubcuw"), (1410, "vavgsw"),
            (1536, "vsububs"), (1544, "vsum4ubs"), (1600, "vsubuhs"), (1608, "vsum4shs"),
            (1664, "vsubuws"), (1672, "vsum2sws"), (1792, "vsubsbs"), (1800, "vsum4sbs"),
            (1856, "vsubshs"), (1920, "vsubsws"), (1928, "vsumsws"));
        Add(VectorForm.DB,
            (266, "vrefp"), (330, "vrsqrtefp"), (394, "vexptefp"), (458, "vlogefp"), (522, "vrfin"), (586, "vrfiz"),
            (650, "vrfip"), (714, "vrfim"), (526, "vupkhsb"), (590, "vupkhsh"), (654, "vupklsb"), (718, "vupklsh"),
            (846, "vupkhpx"), (974, "vupklpx"));
        Add(VectorForm.DBUimm,
            (524, "vspltb"), (588, "vsplth"), (652, "vspltw"), (778, "vcfux"), (842, "vcfsx"), (906, "vctuxs"),
            (970, "vctsxs"));
        Add(VectorForm.DSimm, (780, "vspltisb"), (844, "vspltish"), (908, "vspltisw"));
        Add(VectorForm.D, (1540, "mfvscr"));
        Add(VectorForm.B, (1604, "mtvscr"));
        return t;
    }

    // VXR-form compares, by the 10-bit extended opcode; bit 21 is Rc.
    private static readonly Dictionary<int, string> VectorCompares = new()
    {
        [6] = "vcmpequb", [70] = "vcmpequh", [134] = "vcmpequw", [198] = "vcmpeqfp", [454] = "vcmpgefp",
        [518] = "vcmpgtub", [582] = "vcmpgtuh", [646] = "vcmpgtuw", [710] = "vcmpgtfp", [774] = "vcmpgtsb",
        [838] = "vcmpgtsh", [902] = "vcmpgtsw", [966] = "vcmpbfp",
    };

    private static int SplatReserved(string name) =>
        name switch { "vspltb" => 0x10, "vsplth" => 0x18, "vspltw" => 0x1C, _ => 0 };

    private static (string, PpcOperand[])? AltiVec(uint w)
    {
        int d = D(w), a = A(w), b = B(w), c = C(w);
        switch ((int)(w & 0x3F))
        {
            case 32: return Op("vmhaddshs", V(d), V(a), V(b), V(c));
            case 33: return Op("vmhraddshs", V(d), V(a), V(b), V(c));
            case 34: return Op("vmladduhm", V(d), V(a), V(b), V(c));
            case 36: return Op("vmsumubm", V(d), V(a), V(b), V(c));
            case 37: return Op("vmsummbm", V(d), V(a), V(b), V(c));
            case 38: return Op("vmsumuhm", V(d), V(a), V(b), V(c));
            case 39: return Op("vmsumuhs", V(d), V(a), V(b), V(c));
            case 40: return Op("vmsumshm", V(d), V(a), V(b), V(c));
            case 41: return Op("vmsumshs", V(d), V(a), V(b), V(c));
            case 42: return Op("vsel", V(d), V(a), V(b), V(c));
            case 43: return Op("vperm", V(d), V(a), V(b), V(c));
            case 44: return (c & 0x10) != 0 ? null : Op("vsldoi", V(d), V(a), V(b), Imm(c & 15));
            case 46: return Op("vmaddfp", V(d), V(a), V(c), V(b));
            case 47: return Op("vnmsubfp", V(d), V(a), V(c), V(b));
            case 35 or 45: return null;
        }
        if (VectorCompares.TryGetValue((int)(w & 0x3FF), out var compare))
            return Op(compare + ((w & 0x400) != 0 ? "." : ""), V(d), V(a), V(b));
        if (!VectorVx.TryGetValue((int)(w & 0x7FF), out var vx)) return null;
        return vx.Form switch
        {
            VectorForm.DAB => Op(vx.Name, V(d), V(a), V(b)),
            VectorForm.DB => a != 0 ? null : Op(vx.Name, V(d), V(b)),
            // vspltb, vsplth and vspltw take a 4-, 3- and 2-bit UIMM; the bits above it are reserved.
            VectorForm.DBUimm => (a & SplatReserved(vx.Name)) != 0 ? null : Op(vx.Name, V(d), V(b), Imm(a)),
            VectorForm.DSimm => b != 0 ? null : Op(vx.Name, V(d), Imm((a ^ 0x10) - 0x10)),
            VectorForm.D => a != 0 || b != 0 ? null : Op(vx.Name, V(d)),
            _ => d != 0 || a != 0 ? null : Op(vx.Name, V(b)),
        };
    }
}
