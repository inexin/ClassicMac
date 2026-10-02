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
        {
            bytes[i] = byte.Parse(digits.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

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
        {
            Assert.Equal(M68kFlags.Return, D(hex).Flags);
        }

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
        var div = D("4C43 4C04");
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
    // 68020 additions and more forms of each line [Verified: Ghidra and objdump decode each the same way]
    [InlineData("0CD0 0081", "cas.w d1,d2,(a0)")]
    [InlineData("0ED0 0081", "cas.l d1,d2,(a0)")]
    [InlineData("0EF9 0081 0000 1000", "cas.l d1,d2,($00001000).l")]
    [InlineData("0CFC 8080 90C1", "cas2.w d0:d1,d2:d3,(a0):(a1)")]
    [InlineData("0EFC 0080 10C1", "cas2.l d0:d1,d2:d3,(d0):(d1)")]
    [InlineData("00D0 1800", "chk2.b (a0),d1")]
    [InlineData("02D0 9800", "chk2.w (a0),a1")]
    [InlineData("04D0 1000", "cmp2.l (a0),d1")]
    [InlineData("8348 0000", "pack -(a0),-(a1),#0")]
    [InlineData("8380 FFFF", "unpk d0,d1,#-1")]
    [InlineData("0E10 1000", "moves.b (a0),d1")]
    [InlineData("0E91 8800", "moves.l a0,(a1)")]
    [InlineData("06FA 0002 0010", "callm #2,16(pc)  ; $1014")]
    [InlineData("480E 0000 0010", "link.l a6,#16")]
    [InlineData("484F", "bkpt #7")]
    [InlineData("4E40", "trap #0")]
    [InlineData("4E74 FFFC", "rtd #-4")]
    [InlineData("41BC 0010", "chk.w #16,d0")]
    [InlineData("4A48", "tst.w a0")]
    [InlineData("4A3C 0001", "tst.b #1")]
    [InlineData("50FC", "trapt")]
    [InlineData("51FA 1234", "trapf.w #$1234")]
    [InlineData("50C8 FFFE", "dbt d0,$1000")]
    [InlineData("61FF 0000 0010", "bsr.l $1012")]
    [InlineData("80FC 000A", "divu.w #10,d0")]
    [InlineData("4C00 0C01", "muls.l d0,d1:d0")]
    [InlineData("4C10 2000", "mulu.l (a0),d2")]
    [InlineData("4C7C 0800 0000 0002", "divs.l #2,d0")]
    [InlineData("4C40 0C01", "divs.l d0,d1:d0")]
    [InlineData("49C0", "extb.l d0")]
    [InlineData("5000", "addq.b #8,d0")]
    [InlineData("5388", "subq.l #1,a0")]
    [InlineData("D308", "addx.b -(a0),-(a1)")]
    [InlineData("4010", "negx.b (a0)")]
    [InlineData("E3A8", "lsl.l d1,d0")]
    [InlineData("E107", "asl.b #8,d7")]
    [InlineData("E573", "roxl.w d2,d3")]
    [InlineData("E69C", "ror.l #3,d4")]
    [InlineData("E1F9 0001 0000", "asl.w ($00010000).l")]
    [InlineData("E0F8 1234", "asr.w ($1234).w")]
    [InlineData("4C98 0103", "movem.w (a0)+,d0-d1/a0")]
    [InlineData("48E7 8080", "movem.l d0/a0,-(sp)")]
    [InlineData("48E7 FFFE", "movem.l d0-d7/a0-a6,-(sp)")]
    [InlineData("48A8 0003 0010", "movem.w d0-d1,16(a0)")]
    [InlineData("4C9F 4000", "movem.w (sp)+,a6")]
    [InlineData("41F8 0904", "lea ($0904).w,a0")]
    [InlineData("487B 0000", "pea 0(pc,d0.w)")]
    [InlineData("57D0", "seq (a0)")]
    [InlineData("4AD0", "tas (a0)")]
    [InlineData("4820", "nbcd -(a0)")]
    [InlineData("42D0", "move.w ccr,(a0)")]
    [InlineData("007C 0700", "ori.w #$0700,sr")]
    [InlineData("0A7C 2000", "eori.w #$2000,sr")]
    [InlineData("023C 00FE", "andi.b #$FE,ccr")]
    [InlineData("0618 0001", "addi.b #1,(a0)+")]
    [InlineData("0C00 0080", "cmpi.b #-128,d0")]
    [InlineData("0C40 8000", "cmpi.w #$8000,d0")]
    [InlineData("0C80 FFFF F000", "cmpi.l #-4096,d0")]
    [InlineData("907C 0010", "sub.w #16,d0")]
    [InlineData("D1FC 0001 0000", "adda.l #$00010000,a0")]
    [InlineData("B0FC 0010", "cmpa.w #16,a0")]
    [InlineData("B101", "eor.b d0,d1")]
    [InlineData("8110", "or.b d0,(a0)")]
    [InlineData("C07C 00FF", "and.w #255,d0")]   // and/or with <ea>,Dn keep decimal: only the *i forms are hex
    [InlineData("C188", "exg d0,a0")]
    [InlineData("0100", "btst d0,d0")]
    [InlineData("EFC0 1108", "bfins d1,d0{4:8}")]
    [InlineData("E9C0 18A3", "bfextu d0{d2:d3},d1")]
    [InlineData("EDC0 37C1", "bfffo d0{31:1},d3")]
    [InlineData("EFE8 2863 0010", "bfins d2,16(a0){d1:d3}")]
    // movec: each control register of the 68010, 68020, 68030 and 68040
    [InlineData("4E7A 0000", "movec sfc,d0")]
    [InlineData("4E7A 0001", "movec dfc,d0")]
    [InlineData("4E7A 0003", "movec tc,d0")]
    [InlineData("4E7A 0004", "movec itt0,d0")]
    [InlineData("4E7A 0005", "movec itt1,d0")]
    [InlineData("4E7A 0006", "movec dtt0,d0")]
    [InlineData("4E7A 0007", "movec dtt1,d0")]
    [InlineData("4E7A 0800", "movec usp,d0")]
    [InlineData("4E7A 0801", "movec vbr,d0")]
    [InlineData("4E7A 0802", "movec caar,d0")]
    [InlineData("4E7A 0803", "movec msp,d0")]
    [InlineData("4E7A 0804", "movec isp,d0")]
    [InlineData("4E7A 0805", "movec mmusr,d0")]
    [InlineData("4E7A 0806", "movec urp,d0")]
    [InlineData("4E7B 0807", "movec d0,srp")]
    [InlineData("4E7A 9801", "movec vbr,a1")]
    [InlineData("4E7B 0003", "movec d0,tc")]
    // FPU: registers other than fp0 in bits 9-7 (the destination, or the source of fmove FPn,<ea>) and 12-10
    [InlineData("F200 0080", "fmove.x fp0,fp1")]
    [InlineData("F200 5080", "fmove.w d0,fp1")]
    [InlineData("F203 6500", "fmove.s fp2,d3")]
    [InlineData("F210 5531", "fsincos.d (a0),fp1:fp2")]
    [InlineData("F200 4431", "fsincos.s d0,fp1:fp0")]
    [InlineData("F200 5C80", "fmovecr #$00,fp1")]
    [InlineData("F200 1E80", "fmove.x fp7,fp5")]
    [InlineData("F210 7B80", "fmove.b fp7,(a0)")]
    [InlineData("F212 4D00", "fmove.p (a2),fp2")]
    [InlineData("F210 6EC0", "fmove.p fp5,(a0){#-64}")]
    [InlineData("F210 7FF0", "fmove.p fp7,(a0){d7}")]
    [InlineData("F205 5B22", "fadd.b d5,fp6")]
    [InlineData("F200 1E38", "fcmp.x fp7,fp4")]
    [InlineData("F200 1C3A", "ftst.x fp7")]
    [InlineData("F200 5F80", "fmovecr #$00,fp7")]
    // FPU: the other formats and forms
    [InlineData("F210 7800", "fmove.b fp0,(a0)")]
    [InlineData("F210 7000", "fmove.w fp0,(a0)")]
    [InlineData("F210 7400", "fmove.d fp0,(a0)")]
    [InlineData("F210 4800", "fmove.x (a0),fp0")]
    [InlineData("F210 4C00", "fmove.p (a0),fp0")]
    [InlineData("F210 6C40", "fmove.p fp0,(a0){#-64}")]
    [InlineData("F200 4400", "fmove.s d0,fp0")]
    [InlineData("F200 403A", "ftst.l d0")]
    [InlineData("F210 483A", "ftst.x (a0)")]
    [InlineData("F23C 4038 0000 0005", "fcmp.l #5,fp0")]
    [InlineData("F201 4418", "fabs.s d1,fp0")]
    [InlineData("F210 4822", "fadd.x (a0),fp0")]
    [InlineData("F218 D810", "fmovem.x (a0)+,d1")]
    [InlineData("F210 F0FF", "fmovem.x fp0-fp7,(a0)")]
    [InlineData("F210 F001", "fmovem.x fp7,(a0)")]       // control mode: FP0 is bit 7
    [InlineData("F227 E001", "fmovem.x fp0,-(sp)")]      // predecrement: FP0 is bit 0
    [InlineData("F23C 9000 0000 0000", "fmove.l #0,fpcr")]
    [InlineData("F23C 8400 0000 1234", "fmove.l #$00001234,fpiar")]
    [InlineData("F208 A400", "fmove.l fpiar,a0")]
    [InlineData("F281 0000", "fbeq.w $1002")]
    [InlineData("F2C1 0000 0010", "fbeq.l $1012")]
    [InlineData("F29F 0010", "fbst.w $1012")]
    [InlineData("F240 001F", "fsst d0")]
    [InlineData("F250 0001", "fseq (a0)")]
    [InlineData("F278 0001 1234", "fseq ($1234).w")]
    [InlineData("F27B 0001 0000 0005", "ftrapeq.l #5")]
    [InlineData("F310", "fsave (a0)")]
    [InlineData("F350", "frestore (a0)")]
    [InlineData("F448", "cinvl dc,(a0)")]
    [InlineData("F4B0", "cpushp ic,(a0)")]
    public void Opcode_lines(string hex, string expected) => Text(hex, expected);

    // Each opmode with FP2 as the source (bits 12-10) and FP5 as the destination (bits 9-7), so a swapped or ignored
    // register field shows.
    [Theory]
    [InlineData(0x00, "fmove")]
    [InlineData(0x01, "fint")]
    [InlineData(0x02, "fsinh")]
    [InlineData(0x03, "fintrz")]
    [InlineData(0x04, "fsqrt")]
    [InlineData(0x06, "flognp1")]
    [InlineData(0x08, "fetoxm1")]
    [InlineData(0x09, "ftanh")]
    [InlineData(0x0A, "fatan")]
    [InlineData(0x0C, "fasin")]
    [InlineData(0x0D, "fatanh")]
    [InlineData(0x0E, "fsin")]
    [InlineData(0x0F, "ftan")]
    [InlineData(0x10, "fetox")]
    [InlineData(0x11, "ftwotox")]
    [InlineData(0x12, "ftentox")]
    [InlineData(0x14, "flogn")]
    [InlineData(0x15, "flog10")]
    [InlineData(0x16, "flog2")]
    [InlineData(0x18, "fabs")]
    [InlineData(0x19, "fcosh")]
    [InlineData(0x1A, "fneg")]
    [InlineData(0x1C, "facos")]
    [InlineData(0x1D, "fcos")]
    [InlineData(0x1E, "fgetexp")]
    [InlineData(0x1F, "fgetman")]
    [InlineData(0x20, "fdiv")]
    [InlineData(0x21, "fmod")]
    [InlineData(0x22, "fadd")]
    [InlineData(0x23, "fmul")]
    [InlineData(0x24, "fsgldiv")]
    [InlineData(0x25, "frem")]
    [InlineData(0x26, "fscale")]
    [InlineData(0x27, "fsglmul")]
    [InlineData(0x28, "fsub")]
    [InlineData(0x40, "fsmove")]
    [InlineData(0x41, "fssqrt")]
    [InlineData(0x44, "fdmove")]
    [InlineData(0x45, "fdsqrt")]
    [InlineData(0x58, "fsabs")]
    [InlineData(0x5A, "fsneg")]
    [InlineData(0x5C, "fdabs")]
    [InlineData(0x5E, "fdneg")]
    [InlineData(0x60, "fsdiv")]
    [InlineData(0x62, "fsadd")]
    [InlineData(0x63, "fsmul")]
    [InlineData(0x64, "fddiv")]
    [InlineData(0x66, "fdadd")]
    [InlineData(0x67, "fdmul")]
    [InlineData(0x68, "fssub")]
    [InlineData(0x6C, "fdsub")]
    public void Fpu_general_operations(int opmode, string mnemonic)
    {
        var ins = D($"F200 {0x0A80 | opmode:X4}");
        Assert.Equal((mnemonic, M68kSize.Extended), (ins.Mnemonic, ins.Size));
        Assert.Equal($"{mnemonic}.x fp2,fp5", ins.Text);
        Assert.Equal(M68kFlags.FLine, ins.Flags);
        Assert.Equal(4, ins.Length);
        Assert.Equal(
            [new M68kRegisterOperand(M68kRegisterKind.FloatingPoint, 2), new M68kRegisterOperand(M68kRegisterKind.FloatingPoint, 5)],
            ins.Operands);
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
    // A full extension word with only a base register is written with its base displacement, so it is not read as
    // (An) or (PC): (0,a1), (0,pc), ($0000,zpc); memory indirect keeps its brackets [ClassicMac].
    [InlineData("3031 0150", "move.w (0,a1),d0")]
    [InlineData("3031 0160 0000", "move.w (0,a1),d0")]
    [InlineData("303B 0150", "move.w (0,pc),d0  ; $1002")]
    [InlineData("303B 01D0", "move.w ($0000,zpc),d0")]
    [InlineData("3031 0151", "move.w ([a1]),d0")]
    [InlineData("303B 0151", "move.w ([pc]),d0  ; $1002")]
    [InlineData("303B 0161 FFFE", "move.w ([-2,pc]),d0  ; $1000")]
    [InlineData("303B 0160 0010", "move.w (16,pc),d0  ; $1012")]
    [InlineData("303B 01E0 1234", "move.w ($1234,zpc),d0")]
    [InlineData("3030 0E00", "move.w 0(a0,d0.l*8),d0")]
    [InlineData("3030 9800", "move.w 0(a0,a1.l),d0")]
    [InlineData("3038 FFFE", "move.w ($FFFE).w,d0")]
    [InlineData("3039 FFFF FFFE", "move.w ($FFFFFFFE).l,d0")]
    [InlineData("303B 10FE", "move.w -2(pc,d1.w),d0")]
    [InlineData("3030 0990", "move.w (d0.l),d0")]                      // base suppressed, index only
    [InlineData("3030 0130 0000 1000", "move.w (4096,a0,d0.w),d0")]    // long base displacement
    [InlineData("303B 0170 0000 0010", "move.w (16,pc),d0  ; $1012")]
    // Pre-indexed (I/IS 001-011) and post-indexed (101-111) [Doc: M68000 Family Programmer's Reference Manual, table
    // 2-2; Verified: objdump]
    [InlineData("3030 0122 0010 0004", "move.w ([16,a0,d0.w],4),d0")]
    [InlineData("3030 0126 0010 0004", "move.w ([16,a0],d0.w,4),d0")]
    [InlineData("3030 0163 0010 0000 0004", "move.w ([16,a0],4),d0")]  // index suppressed, long outer displacement
    [InlineData("3030 0161 FFFE", "move.w ([-2,a0]),d0")]
    [InlineData("3030 01D1", "move.w ([$0000]),d0")]                   // base and index suppressed, null displacement
    [InlineData("303B 0122 0010 0004", "move.w ([16,pc,d0.w],4),d0")]  // the pointer's address depends on d0
    [InlineData("303B 0126 0010 0004", "move.w ([16,pc],d0.w,4),d0  ; $1012")]
    [InlineData("303B 01E1 1234", "move.w ([$1234,zpc]),d0")]
    public void Effective_address_modes(string hex, string expected) => Text(hex, expected);

    // A PC-relative operand's base is the address of its own extension word, after any words the instruction has
    // before it (a bit number, an immediate, a register mask, a bit-field or coprocessor command word) [Doc: M68000
    // Family Programmer's Reference Manual, "Program Counter Indirect"; Verified: objdump].
    [Theory]
    [InlineData("083A 0003 0010", "btst #3,16(pc)  ; $1014")]
    [InlineData("013A 0010", "btst d0,16(pc)  ; $1012")]
    [InlineData("0C3A 0001 0010", "cmpi.b #1,16(pc)  ; $1014")]
    [InlineData("0C7A 0001 0010", "cmpi.w #1,16(pc)  ; $1014")]
    [InlineData("0CBA 0000 0001 0010", "cmpi.l #1,16(pc)  ; $1016")]
    [InlineData("00FA 1000 0010", "cmp2.b 16(pc),d1  ; $1014")]
    [InlineData("4A7A 0010", "tst.w 16(pc)  ; $1012")]
    [InlineData("4CFA 0001 0010", "movem.l 16(pc),d0  ; $1014")]
    [InlineData("4CBB 0003 0010", "movem.w 16(pc,d0.w),d0-d1")]
    [InlineData("4CBB 0003 0170 0000 0010", "movem.w (16,pc),d0-d1  ; $1014")]
    [InlineData("46FA 0010", "move.w 16(pc),sr  ; $1012")]
    [InlineData("90FA 0010", "suba.w 16(pc),a0  ; $1012")]
    [InlineData("EBFA 3000 0010", "bfexts 16(pc){0:32},d3  ; $1014")]
    [InlineData("F23A 5400 0010", "fmove.d 16(pc),fp0  ; $1014")]
    [InlineData("F37A 0010", "frestore 16(pc)  ; $1012")]
    [InlineData("4EBA 0010", "jsr 16(pc)  ; $1012")]
    [InlineData("4EFA 0010", "jmp 16(pc)  ; $1012")]
    [InlineData("4EBB 0170 0000 0010", "jsr (16,pc)  ; $1012")]
    [InlineData("4EB8 1234", "jsr ($1234).w")]
    [InlineData("4EB0 0161 0010", "jsr ([16,a0])")]
    public void Pc_relative_bases(string hex, string expected) => Text(hex, expected);

    // (d8,PC,Xn) after a register mask: the base is the extension word at $1004, so the table is at $1014 + Xn; with
    // the index suppressed (a full extension word) the address is fixed and shows.
    [Fact]
    public void Pc_indexed_base_after_extra_words()
    {
        var brief = (M68kEffectiveAddress)D("4CBB 0003 0010").Operands[0];
        Assert.Equal((M68kAddressingMode.PcIndexed, 16), (brief.Mode, brief.BaseDisplacement));
        Assert.Null(brief.Address);
        var full = D("4CBB 0003 0170 0000 0010");
        Assert.Equal(0x1014u, ((M68kEffectiveAddress)full.Operands[0]).Address);
        Assert.Equal([new M68kReference(0x1014, M68kReferenceKind.Data)], full.References);
    }

    // A call or jump to a fixed address is a Call or Branch reference, not Data; the text keeps the address note.
    [Fact]
    public void Calls_keep_the_address_note()
    {
        var jsr = D("4EBA 0010");
        Assert.Equal(("$1012", M68kFlags.Call), (jsr.Comment, jsr.Flags));
        Assert.Equal([new M68kReference(0x1012, M68kReferenceKind.Call)], jsr.References);
        Assert.Equal([new M68kReference(0x1012, M68kReferenceKind.Call)], D("4EBB 0170 0000 0010").References);
        Assert.Equal([new M68kReference(0x1234, M68kReferenceKind.Call)], D("4EB8 1234").References);
        Assert.Empty(D("4EB0 0161 0010").References);
    }

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
    [InlineData("F000 0000")]              // PMMU extension word 000, register 000
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
    // The 32-bit multiply and divide extension words draw bit 15 and bits 9-3 as 0 [Doc: M68000 Family
    // Programmer's Reference Manual, MULS, MULU, DIVS/DIVSL, DIVU/DIVUL].
    [InlineData("4C00 0008")]              // mulu.l with bit 3 set
    [InlineData("4C00 0040")]              // mulu.l with bit 6 set
    [InlineData("4C00 0200")]              // mulu.l with bit 9 set
    [InlineData("4C00 0C48")]              // muls.l 64-bit with bits 6 and 3 set
    [InlineData("4C40 8000")]              // divu.l with bit 15 set
    [InlineData("4C40 0008")]              // divu.l with bit 3 set
    [InlineData("4C40 0200")]              // divu.l with bit 9 set
    [InlineData("4C43 4D44")]              // divs.l with bits 8 and 6 set
    // fmove FPn,<ea>: the k-factor field (bits 6-0) is used only by the packed formats and is drawn as 0 for the
    // others [Doc: MC68881/MC68882 User's Manual, FMOVE].
    [InlineData("F200 6001")]              // fmove.l fp0,d0 with a k-factor
    [InlineData("F200 6440")]              // fmove.s fp0,d0 with bit 6 set
    [InlineData("F210 6801")]              // fmove.x fp0,(a0) with a k-factor
    [InlineData("F200 7001")]              // fmove.w fp0,d0 with a k-factor
    [InlineData("F210 7401")]              // fmove.d fp0,(a0) with a k-factor
    [InlineData("F200 7801")]              // fmove.b fp0,d0 with a k-factor
    // move.b from an address register [Doc: M68000 Family Programmer's Reference Manual, MOVE: "for byte size
    // operation, address register direct is not allowed"].
    [InlineData("1008")]                   // move.b a0,d0
    [InlineData("1088")]                   // move.b a0,(a0)
    // fmovem.x with a dynamic list: the low byte is 0rrr0000 [Doc: MC68881/MC68882 User's Manual, FMOVEM].
    [InlineData("F210 D801")]              // fmovem.x (a0),d0 with bit 0 set
    [InlineData("F210 D808")]              // bit 3 set
    [InlineData("F210 D880")]              // bit 7 set
    [InlineData("F210 F801")]              // fmovem.x d0,(a0) with bit 0 set
    [InlineData("F220 E801")]              // fmovem.x d0,-(a0) with bit 0 set
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
    [InlineData("4A08")]                   // tst.b a0
    [InlineData("F33A 0010")]              // fsave (d16,pc): not alterable
    [InlineData("F208 B000")]              // fmove.l fpcr,a0: only FPIAR takes an address register
    [InlineData("F208 4022")]              // fadd.l a0,fp0
    [InlineData("F200 5400")]              // fmove.d d0,fp0: a data register holds b, w, l or s only
    [InlineData("F200 7C00")]              // fmove.p fp0,d0{d0}
    [InlineData("F200 0005")]              // FPU opmode 5
    [InlineData("F200 0039")]              // FPU opmode $39
    [InlineData("F200 003B")]              // FPU opmode $3B
    [InlineData("F588")]                   // the 68060's plpa
    [InlineData("F800 01C0")]              // the 68060's lpstop
    [InlineData("4E7A 0008")]              // movec of the 68060's BUSCR
    [InlineData("4E7A 0808")]              // movec of the 68060's PCR
    [InlineData("F208 9800")]              // fmovem.l of two control registers from an address register
    [InlineData("F23A B800 0010")]         // fmovem.l of two control registers to PC space
    [InlineData("F23C B800")]              // fmovem.l of two control registers to an immediate
    [InlineData("F23C 9800 0000 0000")]    // fmovem.l #imm of two control registers cut short (a long each)
    [InlineData("F23C 8000 0000 0000")]    // fmovem.l #imm with an empty register list: undefined
    [InlineData("E8C0 1000")]              // bftst with a register in the reserved bits 14-12
    [InlineData("EAC0 7000")]              // bfchg with a register in the reserved bits 14-12
    [InlineData("ECC0 1000")]              // bfclr with a register in the reserved bits 14-12
    [InlineData("EEC0 1000")]              // bfset with a register in the reserved bits 14-12
    [InlineData("E9C0 1E08")]              // bfextu with Do set and bits 10-9 set
    [InlineData("E9C0 1038")]              // bfextu with Dw set and bits 4-3 set
    [InlineData("3030 0118")]              // full extension word with the reserved bit 3 set
    public void Invalid_words_are_dc_w(string hex)
    {
        var bytes = Bytes(hex);
        var ins = M68kDisassembler.Decode(bytes, 0, At);
        Assert.True(ins.IsInvalid, ins.Text);
        Assert.Equal(("dc", M68kSize.Word, 2), (ins.Mnemonic, ins.Size, ins.Length));
        Assert.Equal($"dc.w ${hex[..4]}", ins.Text);
        Assert.Equal(M68kFlags.Invalid | (bytes[0] >= 0xF0 ? M68kFlags.FLine : 0), ins.Flags);
        Assert.Equal([new M68kImmediate(bytes[0] << 8 | bytes[1], M68kSize.Word, [bytes[0], bytes[1]])], ins.Operands);
        Assert.Equal([(ushort)(bytes[0] << 8 | bytes[1])], ins.Words);
        Assert.Empty(ins.References);
    }

    // fmovem.l of two or three FPU control registers: from memory, every memory mode, an immediate holding one long
    // per register in the order FPCR, FPSR, FPIAR; to memory, the memory alterable modes. Dn and An only with one
    // register [Doc: MC68881/MC68882 User's Manual, FMOVEM; Verified: GNU as and objdump for -(An) and (An)+].
    [Theory]
    [InlineData("F220 9800", "fmovem.l -(a0),fpcr/fpsr")]
    [InlineData("F218 B800", "fmovem.l fpcr/fpsr,(a0)+")]
    [InlineData("F220 B800", "fmovem.l fpcr/fpsr,-(a0)")]
    [InlineData("F218 9800", "fmovem.l (a0)+,fpcr/fpsr")]
    [InlineData("F23A 9800 0010", "fmovem.l 16(pc),fpcr/fpsr  ; $1014")]
    [InlineData("F23C 9800 0000 0000 0000 0001", "fmovem.l #0,#1,fpcr/fpsr")]
    [InlineData("F23C 8C00 0000 0002 0000 0003", "fmovem.l #2,#3,fpsr/fpiar")]
    [InlineData("F23C 9C00 0000 0001 0000 0002 0000 0003", "fmovem.l #1,#2,#3,fpcr/fpsr/fpiar")]
    public void Fmovem_of_control_registers(string hex, string expected) => Text(hex, expected);

    // The forms next to the dc.w cases above that stay instructions: move of a word or long from An; fmove FPn,<ea>
    // with a k-factor in the packed formats; fmovem.x with a dynamic list, from a PC-relative source too (memory to
    // registers takes the control modes and (An)+, and the control modes include (d16,PC) and (d8,PC,Xn)) [Doc: M68000
    // Family Programmer's Reference Manual, MOVE; MC68881/MC68882 User's Manual, FMOVE, FMOVEM].
    [Theory]
    [InlineData("3008", "move.w a0,d0")]
    [InlineData("2088", "move.l a0,(a0)")]
    [InlineData("F210 6C7F", "fmove.p fp0,(a0){#-1}")]
    [InlineData("F210 7C70", "fmove.p fp0,(a0){d7}")]
    [InlineData("F200 6000", "fmove.l fp0,d0")]
    [InlineData("F210 D830", "fmovem.x (a0),d3")]
    [InlineData("F210 F870", "fmovem.x d7,(a0)")]
    [InlineData("F220 E820", "fmovem.x d2,-(a0)")]
    [InlineData("F23A D830 0010", "fmovem.x 16(pc),d3  ; $1014")]
    [InlineData("F23B D830 0010", "fmovem.x 16(pc,d0.w),d3")]
    public void Forms_next_to_reserved_bits_decode(string hex, string expected) => Text(hex, expected);

    [Fact]
    public void Fmovem_immediate_is_one_long_per_register()
    {
        var ins = D("F23C 9C00 0000 0001 0000 0002 0000 0003");
        Assert.Equal(("fmovem", M68kSize.Long, 16), (ins.Mnemonic, ins.Size, ins.Length));
        Assert.Equal(
        [
            new M68kImmediate(1, M68kSize.Long, [0, 0, 0, 1]), new M68kImmediate(2, M68kSize.Long, [0, 0, 0, 2]),
            new M68kImmediate(3, M68kSize.Long, [0, 0, 0, 3]),
            new M68kRegisterList(7, M68kRegisterListKind.FloatingPointControl),
        ], ins.Operands);
    }

    // A byte immediate is the low byte of its word: where Motorola's encoding draws the high byte as zeros (ori, andi
    // and eori to CCR, a static bit number, callm's argument count) a nonzero high byte is dc.w
    // (Invalid_words_are_dc_w); an ordinary byte immediate (<ea> mode 7 register 4) ignores it, as the processor does
    // [Doc: M68000 Family Programmer's Reference Manual, "Immediate Data" and each instruction's format].
    [Theory]
    [InlineData("0000 01FF", "ori.b #$FF,d0")]
    [InlineData("0C00 8001", "cmpi.b #1,d0")]
    [InlineData("103C 01FF", "move.b #-1,d0")]
    [InlineData("4A3C 01FF", "tst.b #-1")]
    [InlineData("B03C FF41", "cmp.b #65,d0")]
    [InlineData("F23C 5800 FF01", "fmove.b #1,fp0")]
    public void Byte_immediates_ignore_the_high_byte(string hex, string expected)
    {
        Text(hex, expected);
        var imm = D(hex).Operands.OfType<M68kImmediate>().First();
        Assert.Equal(M68kSize.Byte, imm.Size);
        Assert.Equal(2, imm.Bytes.Count);
    }

    // An empty register list is written #0, the mask [Verified: objdump writes #0].
    [Theory]
    [InlineData("48E7 0000", "movem.l #0,-(sp)")]
    [InlineData("4CDF 0000", "movem.l (sp)+,#0")]
    [InlineData("48D0 0000", "movem.l #0,(a0)")]
    [InlineData("F227 E000", "fmovem.x #0,-(sp)")]
    [InlineData("F21F D000", "fmovem.x (sp)+,#0")]
    public void Empty_register_lists(string hex, string expected)
    {
        Text(hex, expected);
        Assert.Contains(D(hex).Operands, o => o is M68kRegisterList { Mask: 0 });
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
