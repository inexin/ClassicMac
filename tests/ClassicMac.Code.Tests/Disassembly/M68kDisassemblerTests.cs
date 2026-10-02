using System.Globalization;
using ClassicMac.Code.Disassembly;

namespace ClassicMac.Code.Tests.Disassembly;

// Hand-built encodings [Doc: M68000 Family Programmer's Reference Manual (Motorola), MC68040 User's Manual].
public class M68kDisassemblerTests
{
    private const uint At = 0x1000;

    internal static byte[] Bytes(string hex)
    {
        var digits = hex.Replace(" ", "", StringComparison.Ordinal);
        var bytes = new byte[digits.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(digits.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    private static M68kInstruction D(string hex, Func<ushort, string>? trapName = null) =>
        M68kDisassembler.Decode(Bytes(hex), 0, At, trapName);

    private static void Text(string hex, string expected)
    {
        var ins = D(hex);
        Assert.Equal(expected, ins.Text);
        Assert.Equal(hex.Replace(" ", "", StringComparison.Ordinal).Length / 2, ins.Length);
    }

    // The specification's vectors, checked against a Ghidra listing of Realmz, ResEdit and Disk Copy.
    [Theory]
    [InlineData("4E56 0000", "link a6,#0")]
    [InlineData("4E75", "rts")]
    [InlineData("4E74 0008", "rtd #8")]
    [InlineData("3F3C 0001", "move.w #1,-(sp)")]
    [InlineData("4EAD 053A", "jsr 1338(a5)")]
    [InlineData("2F3C 434F 4445", "move.l #$434F4445,-(sp)  ; 'CODE'")]
    [InlineData("43F5 7800", "lea 0(a5,d7.l),a1")]
    [InlineData("49C7", "extb.l d7")]
    // The specification writes divsl.l d3,d4; the size bit (10) is set, which Motorola's manual names
    // DIVS.L <ea>,Dr:Dq (64/32). Bits 8 and 6 of the extension word are reserved; the processor ignores them.
    [InlineData("4C43 4D44", "divs.l d3,d4:d4")]
    [InlineData("4C01 0000", "mulu.l d1,d0")]
    [InlineData("4870 0400", "pea 0(a0,d0.w*4)")]
    [InlineData("4EFB 0002", "jmp 2(pc,d0.w)")]
    [InlineData("F478", "cpusha dc")]
    public void Specification_vectors(string hex, string expected) => Text(hex, expected);

    [Fact]
    public void Link_carries_its_register_and_displacement()
    {
        var ins = D("4E56 FFF8");
        Assert.Equal(("link", M68kSize.None), (ins.Mnemonic, ins.Size));
        Assert.Equal([new M68kRegisterOperand(M68kRegisterKind.Address, 6)], ins.Operands.Take(1));
        Assert.Equal(-8, ((M68kImmediate)ins.Operands[1]).Value);
        Assert.Equal("link a6,#-8", ins.Text);
    }

    [Fact]
    public void Return_instructions_are_flagged()
    {
        foreach (var hex in new[] { "4E75", "4E74 0008", "4E73", "4E77", "06C8" })
            Assert.Equal(M68kFlags.Return, D(hex).Flags);
        Assert.Equal(8, ((M68kImmediate)D("4E74 0008").Operands[0]).Value);
    }

    [Fact]
    public void Move_immediate_to_predecrement()
    {
        var ins = D("3F3C 0001");
        Assert.Equal(("move", M68kSize.Word), (ins.Mnemonic, ins.Size));
        var imm = Assert.IsType<M68kImmediate>(ins.Operands[0]);
        Assert.Equal((1L, M68kSize.Word), (imm.Value, imm.Size));
        Assert.Equal(new byte[] { 0, 1 }, imm.Bytes);
        var ea = Assert.IsType<M68kEffectiveAddress>(ins.Operands[1]);
        Assert.Equal((M68kAddressingMode.PreDecrement, 7), (ea.Mode, ea.Register));
        Assert.Equal(new ushort[] { 0x3F3C, 0x0001 }, ins.Words);
        Assert.Equal((At, 4), (ins.Address, ins.Length));
        Assert.Equal(M68kFlags.None, ins.Flags);
        Assert.Null(ins.Comment);
    }

    [Fact]
    public void Jsr_through_the_jump_table()
    {
        var ins = D("4EAD 053A");
        Assert.Equal(M68kFlags.Call, ins.Flags);
        Assert.Equal(new M68kEffectiveAddress(M68kAddressingMode.Displacement, 5, 1338, null, 0, false, false, null),
            ins.Operands[0]);
        Assert.Empty(ins.References);
    }

    [Fact]
    public void FourCC_immediate_is_shown_as_a_comment()
    {
        var ins = D("2F3C 434F 4445");
        Assert.Equal("'CODE'", ins.Comment);
        Assert.Equal(0x434F4445, ((M68kImmediate)ins.Operands[0]).Value);
        Assert.Null(D("2F3C 434F 4401").Comment);
    }

    [Fact]
    public void Indexed_operands_carry_index_size_and_scale()
    {
        var lea = (M68kEffectiveAddress)D("43F5 7800").Operands[0];
        Assert.Equal(M68kAddressingMode.Indexed, lea.Mode);
        Assert.Equal(5, lea.Register);
        Assert.Equal(new M68kIndex(new M68kRegisterOperand(M68kRegisterKind.Data, 7), true, 1), lea.Index);
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.Address, 1), D("43F5 7800").Operands[1]);
        var pea = (M68kEffectiveAddress)D("4870 0400").Operands[0];
        Assert.Equal(new M68kIndex(new M68kRegisterOperand(M68kRegisterKind.Data, 0), false, 4), pea.Index);
        Assert.False(pea.FullExtension);
    }

    [Fact]
    public void Pc_indexed_jump_has_no_fixed_target()
    {
        var ins = D("4EFB 0002");
        Assert.Equal(M68kFlags.Branch, ins.Flags);
        var ea = (M68kEffectiveAddress)ins.Operands[0];
        Assert.Equal(M68kAddressingMode.PcIndexed, ea.Mode);
        Assert.True(ea.IsPcRelative);
        Assert.Null(ea.Address);
        Assert.Empty(ins.References);
    }

    [Fact]
    public void Long_multiply_and_divide_forms()
    {
        Assert.Equal(("extb", M68kSize.Long), (D("49C7").Mnemonic, D("49C7").Size));
        var div = D("4C43 4D44");
        Assert.Equal(("divs", M68kSize.Long), (div.Mnemonic, div.Size));
        Assert.Equal(new M68kRegisterPair(new(M68kRegisterKind.Data, 4), new(M68kRegisterKind.Data, 4), false),
            div.Operands[1]);
        Text("4C00 1C02", "muls.l d0,d2:d1");
        Text("4C41 0802", "divsl.l d1,d2:d0");
        Text("4C41 0000", "divu.l d1,d0");
        Text("4C41 1400", "divu.l d1,d0:d1");
        Text("4C41 0002", "divul.l d1,d2:d0");
        Text("4C3C 0000 0000 000A", "mulu.l #10,d0");
        Assert.True(D("4C48 0000").IsInvalid);          // mulu.l a0,d0: an address register is not data
    }

    [Fact]
    public void Cache_instructions_are_F_line()
    {
        var ins = D("F478");
        Assert.Equal(("cpusha", M68kFlags.FLine), (ins.Mnemonic, ins.Flags));
        Assert.Equal([new M68kRegisterOperand(M68kRegisterKind.Cache, 1)], ins.Operands);
        Text("F4F8", "cpusha bc");
        Text("F468", "cpushl dc,(a0)");
        Text("F4D0", "cinvp bc,(a0)");
        Text("F498", "cinva ic");
        Assert.True(D("F438").IsInvalid);               // no cache selected
        Assert.True(D("F440").IsInvalid);               // scope 0
    }

    // One or more encodings of each opcode line.
    [Theory]
    // line 0: immediate, bit, movep, moves, cas, cmp2/chk2, callm/rtm
    [InlineData("0000 0012", "ori.b #$12,d0")]
    [InlineData("0079 1234 0001 0000", "ori.w #$1234,($00010000).l")]
    [InlineData("003C 0004", "ori.b #$04,ccr")]
    [InlineData("027C F8FF", "andi.w #$F8FF,sr")]
    [InlineData("0A3C 0001", "eori.b #$01,ccr")]
    [InlineData("0A40 FFFF", "eori.w #$FFFF,d0")]
    [InlineData("0480 0000 0010", "subi.l #16,d0")]
    [InlineData("0641 0100", "addi.w #256,d1")]
    [InlineData("0C6E 0005 FFFC", "cmpi.w #5,-4(a6)")]
    [InlineData("0800 0003", "btst #3,d0")]
    [InlineData("08D0 0007", "bset #7,(a0)")]
    [InlineData("0890 0001", "bclr #1,(a0)")]
    [InlineData("0310", "btst d1,(a0)")]
    [InlineData("0150", "bchg d0,(a0)")]
    [InlineData("033C 0001", "btst d1,#1")]
    [InlineData("0388 0004", "movep.w d1,4(a0)")]
    [InlineData("0149 0002", "movep.l 2(a1),d0")]
    [InlineData("0E90 8000", "moves.l (a0),a0")]
    [InlineData("0E50 0800", "moves.w d0,(a0)")]
    [InlineData("06C8", "rtm a0")]
    [InlineData("06C1", "rtm d1")]
    [InlineData("06D0 0002", "callm #2,(a0)")]
    [InlineData("0AD0 0081", "cas.b d1,d2,(a0)")]
    [InlineData("0EFC 8080 90C1", "cas2.l d0:d1,d2:d3,(a0):(a1)")]
    [InlineData("00D0 1000", "cmp2.b (a0),d1")]
    [InlineData("02D0 1000", "cmp2.w (a0),d1")]
    [InlineData("04D0 9800", "chk2.l (a0),a1")]
    // lines 1-3: move, movea
    [InlineData("1018", "move.b (a0)+,d0")]
    [InlineData("2A6E 0008", "movea.l 8(a6),a5")]
    [InlineData("3E7C 0010", "movea.w #16,sp")]
    [InlineData("23C8 0001 0000", "move.l a0,($00010000).l")]
    [InlineData("31C0 0904", "move.w d0,($0904).w")]
    [InlineData("2E80", "move.l d0,(sp)")]
    [InlineData("303C 1234", "move.w #$1234,d0")]
    [InlineData("103C 00FF", "move.b #-1,d0")]
    // line 4
    [InlineData("4E5E", "unlk a6")]
    [InlineData("4E73", "rte")]
    [InlineData("4E77", "rtr")]
    [InlineData("4E71", "nop")]
    [InlineData("4E70", "reset")]
    [InlineData("4E76", "trapv")]
    [InlineData("4E72 2700", "stop #$2700")]
    [InlineData("4E4F", "trap #15")]
    [InlineData("4E60", "move.l a0,usp")]
    [InlineData("4E69", "move.l usp,a1")]
    [InlineData("4E7A 0002", "movec cacr,d0")]
    [InlineData("4E7B 8801", "movec a0,vbr")]
    [InlineData("4E7B 1004", "movec d1,itt0")]
    [InlineData("4808 FFFF FF00", "link.l a0,#-256")]
    [InlineData("4848", "bkpt #0")]
    [InlineData("4840", "swap d0")]
    [InlineData("4880", "ext.w d0")]
    [InlineData("48C0", "ext.l d0")]
    [InlineData("4AFC", "illegal")]
    [InlineData("4AC0", "tas d0")]
    [InlineData("4A40", "tst.w d0")]
    [InlineData("4A6E FFFE", "tst.w -2(a6)")]
    [InlineData("4200", "clr.b d0")]
    [InlineData("4440", "neg.w d0")]
    [InlineData("4080", "negx.l d0")]
    [InlineData("4680", "not.l d0")]
    [InlineData("4800", "nbcd d0")]
    [InlineData("40C0", "move.w sr,d0")]
    [InlineData("42C0", "move.w ccr,d0")]
    [InlineData("44FC 0004", "move.w #4,ccr")]
    [InlineData("46DF", "move.w (sp)+,sr")]
    [InlineData("48E7 1F38", "movem.l d3-d7/a2-a4,-(sp)")]
    [InlineData("4CDF 1CF8", "movem.l (sp)+,d3-d7/a2-a4")]
    [InlineData("4C90 0001", "movem.w (a0),d0")]
    [InlineData("48A7 8000", "movem.w d0,-(sp)")]
    [InlineData("48D0 FFFF", "movem.l d0-d7/a0-a7,(a0)")]
    [InlineData("4FEF 000C", "lea 12(sp),sp")]
    [InlineData("4181", "chk.w d1,d0")]
    [InlineData("4101", "chk.l d1,d0")]
    [InlineData("4EB9 0000 1234", "jsr ($00001234).l")]
    [InlineData("4ED0", "jmp (a0)")]
    // line 5
    [InlineData("5280", "addq.l #1,d0")]
    [InlineData("5F48", "subq.w #7,a0")]
    [InlineData("508F", "addq.l #8,sp")]
    [InlineData("51C8 FFFE", "dbf d0,$1000")]
    [InlineData("57C0", "seq d0")]
    [InlineData("56FC", "trapne")]
    [InlineData("56FA 0001", "trapne.w #1")]
    [InlineData("56FB 0000 0001", "trapne.l #1")]
    // line 6
    [InlineData("6000 0010", "bra.w $1012")]
    [InlineData("6604", "bne.s $1006")]
    [InlineData("61FE", "bsr.s $1000")]
    [InlineData("67FF 0000 0100", "beq.l $1102")]
    [InlineData("6100 FFFE", "bsr.w $1000")]
    [InlineData("6200 0002", "bhi.w $1004")]
    // line 7
    [InlineData("70FF", "moveq #-1,d0")]
    [InlineData("7E10", "moveq #16,d7")]
    // line 8
    [InlineData("8041", "or.w d1,d0")]
    [InlineData("8150", "or.w d0,(a0)")]
    [InlineData("80C1", "divu.w d1,d0")]
    [InlineData("81C1", "divs.w d1,d0")]
    [InlineData("8101", "sbcd d1,d0")]
    [InlineData("8109", "sbcd -(a1),-(a0)")]
    [InlineData("8340 0005", "pack d0,d1,#5")]
    [InlineData("8389 000A", "unpk -(a1),-(a1),#10")]
    // lines 9 and D
    [InlineData("9081", "sub.l d1,d0")]
    [InlineData("D1C1", "adda.l d1,a0")]
    [InlineData("D0FC 0010", "adda.w #16,a0")]
    [InlineData("91C8", "suba.l a0,a0")]
    [InlineData("D350", "add.w d1,(a0)")]
    [InlineData("D048", "add.w a0,d0")]
    [InlineData("D181", "addx.l d1,d0")]
    [InlineData("9189", "subx.l -(a1),-(a0)")]
    // line B
    [InlineData("B041", "cmp.w d1,d0")]
    [InlineData("B03C 0041", "cmp.b #65,d0")]
    [InlineData("B1C8", "cmpa.l a0,a0")]
    [InlineData("B0C8", "cmpa.w a0,a0")]
    [InlineData("B348", "cmpm.w (a0)+,(a1)+")]
    [InlineData("B350", "eor.w d1,(a0)")]
    // line C
    [InlineData("C041", "and.w d1,d0")]
    [InlineData("C350", "and.w d1,(a0)")]
    [InlineData("C0C1", "mulu.w d1,d0")]
    [InlineData("C1C1", "muls.w d1,d0")]
    [InlineData("C101", "abcd d1,d0")]
    [InlineData("C109", "abcd -(a1),-(a0)")]
    [InlineData("C141", "exg d0,d1")]
    [InlineData("C149", "exg a0,a1")]
    [InlineData("C189", "exg d0,a1")]
    // line E: shifts, rotates, bit fields
    [InlineData("E388", "lsl.l #1,d0")]
    [InlineData("E268", "lsr.w d1,d0")]
    [InlineData("E040", "asr.w #8,d0")]
    [InlineData("E318", "rol.b #1,d0")]
    [InlineData("E290", "roxr.l #1,d0")]
    [InlineData("E1D0", "asl.w (a0)")]
    [InlineData("E7D8", "rol.w (a0)+")]
    [InlineData("E4D0", "roxr.w (a0)")]
    [InlineData("E2D0", "lsr.w (a0)")]
    [InlineData("E9C0 1108", "bfextu d0{4:8},d1")]
    [InlineData("E8D0 0800", "bftst (a0){d0:32}")]
    [InlineData("ECD0 0022", "bfclr (a0){0:d2}")]
    [InlineData("EFD0 2005", "bfins d2,(a0){0:5}")]
    [InlineData("EBC0 3000", "bfexts d0{0:32},d3")]
    [InlineData("EDC0 3000", "bfffo d0{0:32},d3")]
    [InlineData("EAD0 0000", "bfchg (a0){0:32}")]
    [InlineData("EED0 0000", "bfset (a0){0:32}")]
    [InlineData("E8FA 0000 0010", "bftst 16(pc){0:32}  ; $1014")]
    // line F: 68040, FPU
    [InlineData("F620 8000", "move16 (a0)+,(a0)+")]
    [InlineData("F600 0001 0000", "move16 (a0)+,($00010000).l")]
    [InlineData("F609 0001 0000", "move16 ($00010000).l,(a1)+")]
    [InlineData("F612 0001 0000", "move16 (a2),($00010000).l")]
    [InlineData("F61B 0001 0000", "move16 ($00010000).l,(a3)")]
    [InlineData("F518", "pflusha")]
    [InlineData("F508", "pflush (a0)")]
    [InlineData("F500", "pflushn (a0)")]
    [InlineData("F510", "pflushan")]
    [InlineData("F548", "ptestw (a0)")]
    [InlineData("F568", "ptestr (a0)")]
    [InlineData("F200 0422", "fadd.x fp1,fp0")]
    [InlineData("F200 0C38", "fcmp.x fp3,fp0")]
    [InlineData("F200 003A", "ftst.x fp0")]
    [InlineData("F200 1031", "fsincos.x fp4,fp1:fp0")]
    [InlineData("F22E 5400 FFF8", "fmove.d -8(a6),fp0")]
    [InlineData("F200 4000", "fmove.l d0,fp0")]
    [InlineData("F23C 4400 3F80 0000", "fmove.s #$3F800000,fp0")]
    [InlineData("F23C 5400 3FF0 0000 0000 0000", "fmove.d #$3FF0000000000000,fp0")]
    [InlineData("F23C 4800 3FFF 0000 8000 0000 0000 0000", "fmove.x #$3FFF00008000000000000000,fp0")]
    [InlineData("F23C 5800 0001", "fmove.b #1,fp0")]
    [InlineData("F227 6800", "fmove.x fp0,-(sp)")]
    [InlineData("F210 6C02", "fmove.p fp0,(a0){#2}")]
    [InlineData("F210 7C30", "fmove.p fp0,(a0){d3}")]
    [InlineData("F200 6000", "fmove.l fp0,d0")]
    [InlineData("F200 5C32", "fmovecr #$32,fp0")]
    [InlineData("F227 E00C", "fmovem.x fp2-fp3,-(sp)")]
    [InlineData("F21F D030", "fmovem.x (sp)+,fp2-fp3")]
    [InlineData("F227 E810", "fmovem.x d1,-(sp)")]
    [InlineData("F200 9000", "fmove.l d0,fpcr")]
    [InlineData("F200 A800", "fmove.l fpsr,d0")]
    [InlineData("F208 8400", "fmove.l a0,fpiar")]
    [InlineData("F227 BC00", "fmovem.l fpcr/fpsr/fpiar,-(sp)")]
    [InlineData("F21F 9800", "fmovem.l (sp)+,fpcr/fpsr")]
    [InlineData("F28E 0010", "fbne.w $1012")]
    [InlineData("F2CF 0000 0100", "fbt.l $1102")]
    [InlineData("F280 0000", "fnop")]
    [InlineData("F280 0002", "fbf.w $1004")]
    [InlineData("F248 000E FFFC", "fdbne d0,$1000")]
    [InlineData("F240 0001", "fseq d0")]
    [InlineData("F27C 000F", "ftrapt")]
    [InlineData("F27A 0001 0005", "ftrapeq.w #5")]
    [InlineData("F327", "fsave -(sp)")]
    [InlineData("F35F", "frestore (sp)+")]
    public void Opcode_lines(string hex, string expected) => Text(hex, expected);

    [Theory]
    [InlineData("F200 0001", "fint")]
    [InlineData("F200 0002", "fsinh")]
    [InlineData("F200 0003", "fintrz")]
    [InlineData("F200 0004", "fsqrt")]
    [InlineData("F200 0006", "flognp1")]
    [InlineData("F200 0008", "fetoxm1")]
    [InlineData("F200 0009", "ftanh")]
    [InlineData("F200 000A", "fatan")]
    [InlineData("F200 000C", "fasin")]
    [InlineData("F200 000D", "fatanh")]
    [InlineData("F200 000E", "fsin")]
    [InlineData("F200 000F", "ftan")]
    [InlineData("F200 0010", "fetox")]
    [InlineData("F200 0011", "ftwotox")]
    [InlineData("F200 0012", "ftentox")]
    [InlineData("F200 0014", "flogn")]
    [InlineData("F200 0015", "flog10")]
    [InlineData("F200 0016", "flog2")]
    [InlineData("F200 0018", "fabs")]
    [InlineData("F200 0019", "fcosh")]
    [InlineData("F200 001A", "fneg")]
    [InlineData("F200 001C", "facos")]
    [InlineData("F200 001D", "fcos")]
    [InlineData("F200 001E", "fgetexp")]
    [InlineData("F200 001F", "fgetman")]
    [InlineData("F200 0020", "fdiv")]
    [InlineData("F200 0021", "fmod")]
    [InlineData("F200 0023", "fmul")]
    [InlineData("F200 0024", "fsgldiv")]
    [InlineData("F200 0025", "frem")]
    [InlineData("F200 0026", "fscale")]
    [InlineData("F200 0027", "fsglmul")]
    [InlineData("F200 0028", "fsub")]
    [InlineData("F200 0040", "fsmove")]
    [InlineData("F200 0041", "fssqrt")]
    [InlineData("F200 0044", "fdmove")]
    [InlineData("F200 0045", "fdsqrt")]
    [InlineData("F200 0058", "fsabs")]
    [InlineData("F200 005A", "fsneg")]
    [InlineData("F200 005C", "fdabs")]
    [InlineData("F200 005E", "fdneg")]
    [InlineData("F200 0060", "fsdiv")]
    [InlineData("F200 0062", "fsadd")]
    [InlineData("F200 0063", "fsmul")]
    [InlineData("F200 0064", "fddiv")]
    [InlineData("F200 0066", "fdadd")]
    [InlineData("F200 0067", "fdmul")]
    [InlineData("F200 0068", "fssub")]
    [InlineData("F200 006C", "fdsub")]
    public void Fpu_general_operations(string hex, string mnemonic)
    {
        var ins = D(hex);
        Assert.Equal((mnemonic, M68kSize.Extended), (ins.Mnemonic, ins.Size));
        Assert.Equal($"{mnemonic}.x fp0,fp0", ins.Text);
        Assert.Equal(M68kFlags.FLine, ins.Flags);
    }

    // Each effective-address mode, as the source of move.w <ea>,d0.
    [Theory]
    [InlineData("3001", "move.w d1,d0")]
    [InlineData("3009", "move.w a1,d0")]
    [InlineData("3011", "move.w (a1),d0")]
    [InlineData("3019", "move.w (a1)+,d0")]
    [InlineData("3021", "move.w -(a1),d0")]
    [InlineData("3029 0010", "move.w 16(a1),d0")]
    [InlineData("3031 1004", "move.w 4(a1,d1.w),d0")]
    [InlineData("3031 9AFC", "move.w -4(a1,a1.l*2),d0")]
    [InlineData("3038 0904", "move.w ($0904).w,d0")]
    [InlineData("3038 8000", "move.w ($8000).w,d0")]
    [InlineData("3039 0001 0000", "move.w ($00010000).l,d0")]
    [InlineData("303A 0010", "move.w 16(pc),d0  ; $1012")]
    [InlineData("303B 1004", "move.w 4(pc,d1.w),d0")]
    [InlineData("303C 1234", "move.w #$1234,d0")]
    [InlineData("3031 1D20 0100", "move.w (256,a1,d1.l*4),d0")]
    [InlineData("3031 1D10", "move.w (a1,d1.l*4),d0")]
    [InlineData("3031 1D26 0010 0008", "move.w ([16,a1],d1.l*4,8),d0")]
    [InlineData("3031 1D22 0010 0008", "move.w ([16,a1,d1.l*4],8),d0")]
    [InlineData("3031 1D37 0001 0000 0000 0002", "move.w ([65536,a1],d1.l*4,2),d0")]
    [InlineData("3031 01F1 0000 1234", "move.w ([$1234]),d0")]
    [InlineData("3031 0150", "move.w (a1),d0")]
    [InlineData("303B 0161 FFFE", "move.w ([-2,pc]),d0  ; $1000")]
    [InlineData("303B 0160 0010", "move.w (16,pc),d0  ; $1012")]
    [InlineData("303B 01E0 1234", "move.w ($1234,zpc),d0")]
    public void Effective_address_modes(string hex, string expected) => Text(hex, expected);

    [Fact]
    public void Memory_indirect_operand_structure()
    {
        var ea = (M68kEffectiveAddress)D("3031 1D26 0010 0008").Operands[0];
        Assert.Equal(M68kAddressingMode.MemoryIndirectPostIndexed, ea.Mode);
        Assert.Equal((1, 16, 8, true, false), (ea.Register, ea.BaseDisplacement, ea.OuterDisplacement, ea.FullExtension, ea.BaseSuppressed));
        Assert.Equal(new M68kIndex(new M68kRegisterOperand(M68kRegisterKind.Data, 1), true, 4), ea.Index);
        Assert.Null(ea.Address);
        Assert.Equal(M68kAddressingMode.MemoryIndirectPreIndexed, ((M68kEffectiveAddress)D("3031 1D22 0010 0008").Operands[0]).Mode);

        var pc = D("303B 0161 FFFE");
        var pcEa = (M68kEffectiveAddress)pc.Operands[0];
        Assert.Equal(M68kAddressingMode.PcMemoryIndirectPreIndexed, pcEa.Mode);
        Assert.Null(pcEa.Index);
        Assert.Equal(0x1000u, pcEa.Address);
        Assert.Equal([new M68kReference(0x1000, M68kReferenceKind.Data)], pc.References);
        Assert.Equal(M68kAddressingMode.PcMemoryIndirectPostIndexed,
            ((M68kEffectiveAddress)D("303B 1D26 0010 0008").Operands[0]).Mode);

        var abs = (M68kEffectiveAddress)D("3031 01F1 0000 1234").Operands[0];
        Assert.True(abs.BaseSuppressed);
        Assert.Equal(0x1234, abs.BaseDisplacement);
    }

    [Fact]
    public void Absolute_and_pc_relative_addresses()
    {
        var shortAbs = (M68kEffectiveAddress)D("3038 8000").Operands[0];
        Assert.Equal((M68kAddressingMode.AbsoluteShort, 0xFFFF8000u), (shortAbs.Mode, shortAbs.Address));
        var pcDisp = D("303A 0010");
        Assert.Equal(0x1012u, ((M68kEffectiveAddress)pcDisp.Operands[0]).Address);
        Assert.Equal([new M68kReference(0x1012, M68kReferenceKind.Data)], pcDisp.References);
        Assert.Equal("$1012", pcDisp.Comment);
        // The PC of a PC-relative operand is its own extension word's address.
        Assert.Equal([new M68kReference(0x1014, M68kReferenceKind.Data)], D("E8FA 0000 0010").References);
        // Data at an absolute address is not a PC-relative reference; a call to one is.
        Assert.Empty(D("3039 0001 0000").References);
        Assert.Equal([new M68kReference(0x1234, M68kReferenceKind.Call)], D("4EB9 0000 1234").References);
        Assert.Equal([new M68kReference(0x1012, M68kReferenceKind.Call)], D("4EBA 0010").References);
        Assert.Equal([new M68kReference(0x1000, M68kReferenceKind.Data)], D("41FA FFFE").References);
        Assert.Equal("lea -2(pc),a0  ; $1000", D("41FA FFFE").Text);
        Assert.Equal([new M68kReference(0x1012, M68kReferenceKind.Branch)], D("4EFA 0010").References);
    }

    [Fact]
    public void Branches_carry_their_target()
    {
        var bra = D("6000 0010");
        Assert.Equal(("bra", M68kSize.Word, M68kFlags.Branch), (bra.Mnemonic, bra.Size, bra.Flags));
        Assert.Equal([new M68kBranchTarget(0x1012)], bra.Operands);
        Assert.Equal([new M68kReference(0x1012, M68kReferenceKind.Branch)], bra.References);
        var bne = D("6604");
        Assert.Equal((M68kSize.Byte, M68kFlags.Branch | M68kFlags.Conditional), (bne.Size, bne.Flags));
        var bsr = D("61FE");
        Assert.Equal(M68kFlags.Call, bsr.Flags);
        Assert.Equal([new M68kReference(0x1000, M68kReferenceKind.Call)], bsr.References);
        Assert.Equal(M68kSize.Long, D("67FF 0000 0100").Size);
        var dbf = D("51C8 FFFE");
        Assert.Equal(M68kFlags.Branch | M68kFlags.Conditional, dbf.Flags);
        Assert.Equal([new M68kReference(0x1000, M68kReferenceKind.Branch)], dbf.References);
        var fdb = D("F248 000E FFFC");
        Assert.Equal(M68kFlags.Branch | M68kFlags.Conditional | M68kFlags.FLine, fdb.Flags);
        Assert.Equal([new M68kReference(0x1000, M68kReferenceKind.Branch)], fdb.References);
        Assert.Equal(M68kFlags.Branch | M68kFlags.Conditional | M68kFlags.FLine, D("F28E 0010").Flags);
        Assert.Equal(M68kFlags.Branch, D("4ED0").Flags);
        Assert.Equal(M68kFlags.FLine, D("F280 0000").Flags);
    }

    [Fact]
    public void Branch_to_an_odd_address_is_not_a_reference()
    {
        var ins = D("6001");
        Assert.Equal("bra.s $1003", ins.Text);
        Assert.Empty(ins.References);
    }

    [Fact]
    public void Register_lists_are_normalized()
    {
        var push = (M68kRegisterList)D("48E7 1F38").Operands[0];
        var pop = (M68kRegisterList)D("4CDF 1CF8").Operands[1];
        Assert.Equal(new M68kRegisterList(0x1CF8, M68kRegisterListKind.Integer), push);
        Assert.Equal(push, pop);
        Assert.Equal(new M68kRegisterList(0x0C, M68kRegisterListKind.FloatingPoint), D("F227 E00C").Operands[0]);
        Assert.Equal(new M68kRegisterList(0x0C, M68kRegisterListKind.FloatingPoint), D("F21F D030").Operands[1]);
        Assert.Equal(new M68kRegisterList(7, M68kRegisterListKind.FloatingPointControl), D("F227 BC00").Operands[0]);
    }

    [Fact]
    public void Bit_field_operand()
    {
        var ins = D("E9C0 1108");
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.Data, 0), ins.Operands[0]);
        Assert.Equal(new M68kBitField(4, false, 8, false), ins.Operands[1]);
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.Data, 1), ins.Operands[2]);
        Assert.Equal(new M68kBitField(0, false, 2, true), D("ECD0 0022").Operands[1]);
    }

    [Fact]
    public void A_line_traps()
    {
        var ins = D("A9F0");
        Assert.Equal(("aline", "_A9F0", M68kFlags.ALine), (ins.Mnemonic, ins.Text, ins.Flags));
        Assert.Equal((ushort)0xA9F0, ins.TrapWord);
        Assert.Equal(0xA9F0, ((M68kImmediate)ins.Operands[0]).Value);
        Assert.Equal("_LoadSeg", D("A9F0", TrapNames.Describe).Text);
        Assert.Equal("_NewPtr ,SYS", D("A51E", TrapNames.Describe).Text);
        Assert.Equal("_GetResource ,AUTOPOP", D("ADA0", TrapNames.Describe).Text);
        Assert.Null(D("4E75").TrapWord);
    }

    // Words that are not instructions, including encodings with an effective address the instruction does not
    // allow, are one dc.w each.
    [Theory]
    [InlineData("0800 0100")]              // btst #imm with the immediate's high byte set
    [InlineData("08FC 0001")]              // bset #1,#imm
    [InlineData("0E3C 0000")]              // moves to an immediate
    [InlineData("0E90 0001")]              // moves with reserved bits set
    [InlineData("1040")]                   // movea.b
    [InlineData("25C0")]                   // move.l d0,d16(pc): not alterable
    [InlineData("4AFB")]
    [InlineData("4E7C")]
    [InlineData("4E7A 0FFF")]              // movec with an unknown control register
    [InlineData("41C0")]                   // lea d0
    [InlineData("4ED8")]                   // jmp (a0)+
    [InlineData("48C8")]
    [InlineData("4E00")]
    [InlineData("5108")]                   // subq.b to an address register
    [InlineData("7100")]                   // moveq with bit 8 set
    [InlineData("8048")]                   // or.w a0,d0
    [InlineData("D008")]                   // add.b a0,d0
    [InlineData("C180")]                   // and.l d0,d0 with the memory-destination opmode
    [InlineData("E1C0")]                   // memory shift of a data register
    [InlineData("EAFA 0000 0000")]         // bfchg in PC space
    [InlineData("303D")]                   // mode 7 register 5
    [InlineData("3031 0104")]              // full extension: base displacement size 0
    [InlineData("3031 0114")]              // full extension: I/IS 4
    [InlineData("3031 0155")]              // full extension: index suppressed with I/IS 5
    [InlineData("F000 0000")]              // 68030 MMU
    [InlineData("FE00")]                   // coprocessor 7
    [InlineData("F200 007F")]              // unknown FPU operation
    [InlineData("F2A0 0000")]              // FPU condition above 31
    [InlineData("F600 0001")]              // move16 cut short
    [InlineData("F620 0000")]              // move16 without bit 15
    [InlineData("F200 9800")]              // fmovem.l of two control registers from a data register
    [InlineData("F200 8000")]              // fmove with no control register
    [InlineData("003C 0100")]              // ori to CCR with the immediate's high byte set
    [InlineData("083C 0001")]              // the CCR/SR form with bits 11-9 = 4
    [InlineData("043C 0000")]              // subi.b to an immediate
    [InlineData("06D0 0102")]              // callm with the argument count's high byte set
    [InlineData("0AFC 0000 0000")]         // cas2.b
    [InlineData("0EFC 8088 90C1")]         // cas2 with reserved bits set
    [InlineData("0AD0 0089")]              // cas with reserved bits set
    [InlineData("0AC0 0081")]              // cas on a data register
    [InlineData("00D0 1001")]              // cmp2 with reserved bits set
    [InlineData("00C0 1000")]              // cmp2 on a data register
    [InlineData("4AFA")]                   // the CPU32's bgnd
    [InlineData("4C00 8000")]              // mul.l with bit 15 set
    [InlineData("E9C0 9108")]              // bit field with bit 15 set
    [InlineData("F210 7C31")]              // fmove.p with a dynamic k-factor and low bits set
    [InlineData("F23C 7C00")]              // fmovecr with an effective address
    [InlineData("F200 2000")]              // FPU extension word 001
    [InlineData("F201 0422")]              // register-to-register FPU operation with an effective address
    [InlineData("F200 4800")]              // fmove.x from a data register
    [InlineData("F208 4000")]              // fmove.l from an address register
    [InlineData("F248 0020 0000")]         // fdbcc condition 32
    [InlineData("F208 9000")]              // fmove.l a0,fpcr: only FPIAR takes an address register
    [InlineData("F21F E00C")]              // fmovem.x to (sp)+
    [InlineData("F227 D030")]              // fmovem.x from -(sp)
    [InlineData("F21F C030")]              // fmovem.x predecrement mode with (sp)+
    [InlineData("F367")]                   // frestore -(sp)
    [InlineData("F31F")]                   // fsave (sp)+
    [InlineData("F200 6800")]              // fmove.x fp0,d0
    public void Invalid_words_are_dc_w(string hex)
    {
        var bytes = Bytes(hex);
        var ins = M68kDisassembler.Decode(bytes, 0, At);
        Assert.True(ins.IsInvalid, ins.Text);
        Assert.Equal(("dc", M68kSize.Word, 2), (ins.Mnemonic, ins.Size, ins.Length));
        Assert.Equal($"dc.w ${hex[..4]}", ins.Text);
        Assert.Equal([new M68kImmediate(bytes[0] << 8 | bytes[1], M68kSize.Word, [bytes[0], bytes[1]])], ins.Operands);
        Assert.Equal([(ushort)(bytes[0] << 8 | bytes[1])], ins.Words);
        Assert.Empty(ins.References);
    }

    [Fact]
    public void Invalid_F_line_word_keeps_the_F_line_flag() =>
        Assert.Equal(M68kFlags.Invalid | M68kFlags.FLine, D("FE00").Flags);

    [Fact]
    public void Truncated_instruction_at_the_end_is_dc_w()
    {
        var ins = D("4EB9 0000");
        Assert.Equal(("dc.w $4EB9", 2), (ins.Text, ins.Length));
        Assert.True(ins.IsInvalid);
        Assert.Equal("dc.w $F600", D("F600 0001").Text);
    }

    [Fact]
    public void Last_odd_byte_is_dc_b()
    {
        var ins = D("4E");
        Assert.Equal(("dc", M68kSize.Byte, 1, "dc.b $4E"), (ins.Mnemonic, ins.Size, ins.Length, ins.Text));
        Assert.Empty(ins.Words);
        Assert.True(ins.IsInvalid);
    }

    [Fact]
    public void Odd_address_is_dc_b()
    {
        var code = Bytes("004E 75");
        var ins = M68kDisassembler.Decode(code, 1, At);
        Assert.Equal((At + 1, 1, "dc.b $4E"), (ins.Address, ins.Length, ins.Text));
        Assert.True(ins.IsInvalid);
        // An odd base address makes even offsets odd.
        Assert.Equal("dc.b $4E", M68kDisassembler.Decode(Bytes("4E75"), 0, 0x1001).Text);
    }

    [Fact]
    public void Offset_outside_the_code_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => M68kDisassembler.Decode(Bytes("4E75"), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => M68kDisassembler.Decode(Bytes("4E75"), -1));
    }

    [Fact]
    public void Disassemble_walks_the_code()
    {
        var list = M68kDisassembler.Disassemble(Bytes("4E56 0000 3F3C 0001 A9F0 FFFF 4E5E 4E75 00"), 0x100,
            TrapNames.Describe).ToList();
        Assert.Equal(["link a6,#0", "move.w #1,-(sp)", "_LoadSeg", "dc.w $FFFF", "unlk a6", "rts", "dc.b $00"],
            list.Select(i => i.Text));
        Assert.Equal([0x100u, 0x104u, 0x108u, 0x10Au, 0x10Cu, 0x10Eu, 0x110u], list.Select(i => i.Address));
        Assert.Empty(M68kDisassembler.Disassemble(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Instruction_and_immediate_members()
    {
        var ins = D("4E75");
        Assert.Equal("rts", ins.ToString());
        Assert.False(ins.IsInvalid);
        var a = new M68kImmediate(1, M68kSize.Word, [0, 1]);
        var b = new M68kImmediate(1, M68kSize.Word, new List<byte> { 0, 1 });
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new M68kImmediate(1, M68kSize.Word, [0, 2]));
        Assert.NotEqual(a, new M68kImmediate(1, M68kSize.Long, [0, 1]));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Immediates_switch_to_hex_beyond_4095()
    {
        Text("303C 0FFF", "move.w #4095,d0");
        Text("303C F000", "move.w #-4096,d0");
        Text("303C EFFF", "move.w #$EFFF,d0");
        Text("203C 0000 1000", "move.l #$00001000,d0");
    }
}
