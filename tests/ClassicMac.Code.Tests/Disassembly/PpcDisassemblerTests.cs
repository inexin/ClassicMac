using ClassicMac.Code.Disassembly;

namespace ClassicMac.Code.Tests.Disassembly;

public partial class PpcDisassemblerTests
{
    // The prologue, epilogue and cross-fragment glue of every PEF function [Doc: Mac OS Runtime Architectures,
    // ch. 3 "PowerPC Runtime Conventions"].
    [Theory]
    [InlineData(0x7C0802A6u, "mflr r0")]
    [InlineData(0x90010008u, "stw r0,8(r1)")]
    [InlineData(0x9421FFC0u, "stwu r1,-64(r1)")]
    [InlineData(0x80410014u, "lwz r2,20(r1)")]
    [InlineData(0x4E800020u, "blr")]
    [InlineData(0x4E800420u, "bctr")]
    [InlineData(0x7C0903A6u, "mtctr r0")]
    [InlineData(0x800C0000u, "lwz r0,0(r12)")]
    [InlineData(0x804C0004u, "lwz r2,4(r12)")]
    [InlineData(0x3863FFFFu, "subi r3,r3,1")]
    public void Spec_vectors(uint word, string expected) => Assert.Equal(expected, PpcDisassembler.Decode(word, 0).Text);

    // b, bc, bclr and bcctr: the extended mnemonics, the CR field, the a/l suffixes, the +/- hints (y = 1: forward bc
    // and bclr/bcctr predicted taken "+", backward bc predicted not taken "-"), the flow flags and the target.
    [Theory]
    [InlineData(0x1000u, 0x48000010u, "b 0x1010", PpcFlow.Branch, 0x1010u)]
    [InlineData(0x1000u, 0x4BFFFFF0u, "b 0xFF0", PpcFlow.Branch, 0xFF0u)]
    [InlineData(0x1000u, 0x48000101u, "bl 0x1100", PpcFlow.Branch | PpcFlow.Call, 0x1100u)]
    [InlineData(0x1000u, 0x48000202u, "ba 0x200", PpcFlow.Branch, 0x200u)]
    [InlineData(0x1000u, 0x48000203u, "bla 0x200", PpcFlow.Branch | PpcFlow.Call, 0x200u)]
    [InlineData(0x0u, 0x4BFFFFFCu, "b 0xFFFFFFFC", PpcFlow.Branch, 0xFFFFFFFCu)]
    [InlineData(0x1000u, 0x4A000002u, "ba 0xFE000000", PpcFlow.Branch, 0xFE000000u)]
    [InlineData(0x1000u, 0x41820008u, "beq 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x40860008u, "bne cr1,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x419CFFF8u, "blt cr7,0xFF8", PpcFlow.Branch | PpcFlow.Conditional, 0xFF8u)]
    [InlineData(0x1000u, 0x41810008u, "bgt 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x40800008u, "bge 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x40810008u, "ble 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x41830008u, "bso 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x40830008u, "bns 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x41A20008u, "beq+ 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x41A2FFF8u, "beq- 0xFF8", PpcFlow.Branch | PpcFlow.Conditional, 0xFF8u)]
    [InlineData(0x1000u, 0x40A6FFF8u, "bne- cr1,0xFF8", PpcFlow.Branch | PpcFlow.Conditional, 0xFF8u)]
    [InlineData(0x1000u, 0x4200FFF8u, "bdnz 0xFF8", PpcFlow.Branch | PpcFlow.Conditional, 0xFF8u)]
    [InlineData(0x1000u, 0x42400008u, "bdz 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x42200008u, "bdnz+ 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x40020008u, "bdnzf eq,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x41460008u, "bdzt 4*cr1+eq,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x41000008u, "bdnzt lt,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x40410008u, "bdzf gt,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x42800008u, "bc 20,0,0x1008", PpcFlow.Branch, 0x1008u)]
    [InlineData(0x1000u, 0x41820009u, "beql 0x1008", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x4182000Au, "beqa 0x8", PpcFlow.Branch | PpcFlow.Conditional, 0x8u)]
    [InlineData(0x1000u, 0x4182FFFBu, "beqla 0xFFFFFFF8", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, 0xFFFFFFF8u)]
    [InlineData(0x1000u, 0x4E800020u, "blr", PpcFlow.Branch | PpcFlow.Return, null)]
    [InlineData(0x1000u, 0x4E800021u, "blrl", PpcFlow.Branch | PpcFlow.Call, null)]
    [InlineData(0x1000u, 0x4D820020u, "beqlr", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C860020u, "bnelr cr1", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4DA20020u, "beqlr+", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4E000020u, "bdnzlr", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4E800420u, "bctr", PpcFlow.Branch, null)]
    [InlineData(0x1000u, 0x4E800421u, "bctrl", PpcFlow.Branch | PpcFlow.Call, null)]
    [InlineData(0x1000u, 0x4D8A0420u, "beqctr cr2", PpcFlow.Branch | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C820421u, "bnectrl", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, null)]
    public void Branches(uint address, uint word, string expected, PpcFlow flow, uint? target)
    {
        var instruction = PpcDisassembler.Decode(word, address);
        Assert.Equal(expected, instruction.Text);
        Assert.Equal(flow, instruction.Flow);
        Assert.Equal(target, instruction.Target);
        Assert.True(instruction.IsBranch);
        Assert.Equal((flow & PpcFlow.Call) != 0, instruction.IsCall);
        Assert.Equal((flow & PpcFlow.Return) != 0, instruction.IsReturn);
        Assert.Equal((flow & PpcFlow.Conditional) != 0, instruction.IsConditional);
    }

    // bcctr may not decrement CTR (BO bit 2 clear) [Doc: PEM, "bcctrx"]; bclr with reserved bits 16–20 set.
    [Theory]
    [InlineData(0x4E000420u)]
    [InlineData(0x4E800820u)]
    public void Invalid_branches_are_long(uint word)
    {
        var instruction = PpcDisassembler.Decode(word, 0);
        Assert.False(instruction.IsValid);
        Assert.Equal(PpcFlow.None, instruction.Flow);
        Assert.False(instruction.IsBranch);
    }

    // Each reserved-field and undefined-opcode check, one word per branch of the decoder.
    [Theory]
    [InlineData(0x10221D2Cu)]   // vsldoi with bit 21 set
    [InlineData(0x10221923u)]   // VA-form xo 35
    [InlineData(0x1022192Du)]   // VA-form xo 45
    [InlineData(0x10250B8Cu)]   // vspltisw with vB set
    [InlineData(0x10201604u)]   // mfvscr with vB set
    [InlineData(0x10201644u)]   // mtvscr with vD set
    [InlineData(0x7C6428D0u)]   // neg with rB set
    [InlineData(0x7C832834u)]   // cntlzw with rB set
    [InlineData(0x7C2327ECu)]   // dcbz with rD set
    [InlineData(0x7C610026u)]   // mfcr with rA set
    [InlineData(0x7C780120u)]   // mtcrf with bit 11 set
    [InlineData(0x7D000C00u)]   // mcrxr with rB set
    [InlineData(0x7C650CA6u)]   // mfsr with rB set
    [InlineData(0x7C612526u)]   // mfsrin with rA set
    [InlineData(0x7C012264u)]   // tlbie with rA set
    [InlineData(0x7C000AE4u)]   // tlbia with rB set
    [InlineData(0x7C000C6Cu)]   // tlbsync with rB set
    [InlineData(0x7C000EACu)]   // eieio with rB set
    [InlineData(0x7CC322ACu)]   // dst with reserved bits
    [InlineData(0x7C23066Cu)]   // dss with rA set
    [InlineData(0x7C232040u)]   // cmpl with L = 1
    [InlineData(0x7C642C96u)]   // mulhw with OE
    [InlineData(0x7C642802u)]   // undefined primary-31 xo
    [InlineData(0xFC011001u)]   // fcmpu with Rc
    [InlineData(0xFC231090u)]   // fmr with frA set
    [InlineData(0xFFE1004Cu)]   // mtfsb1 with frA set
    [InlineData(0xFC910080u)]   // mcrfs with low bits set
    [InlineData(0xFF81510Cu)]   // mtfsfi with rA set
    [InlineData(0xFC200C8Eu)]   // mffs with frB set
    [InlineData(0xFFFE158Eu)]   // mtfsf with bit 6 set
    [InlineData(0xFC22192Au)]   // fadd with frC set
    [InlineData(0xFC22182Cu)]   // fsqrt with frA set
    [InlineData(0xFC221932u)]   // fmul with frB set
    [InlineData(0xEC22192Eu)]   // primary 59 xo 23
    [InlineData(0xFC201830u)]   // primary 63 A-form xo 24
    [InlineData(0xFC201002u)]   // primary 63 X-form xo 1
    [InlineData(0x4C011203u)]   // crand with Rc
    [InlineData(0x4C810000u)]   // mcrf with low bits set
    [InlineData(0x4C000864u)]   // rfi with rB set
    [InlineData(0x4C00092Cu)]   // isync with rB set
    [InlineData(0x4C000002u)]   // undefined primary-19 xo
    [InlineData(0x7C832009u)]   // tw with Rc
    [InlineData(0x7C64282Fu)]   // lwzx with Rc (bit 31 reserved)
    [InlineData(0x28230000u)]   // cmplwi with L = 1
    public void Reserved_fields_and_undefined_opcodes_are_long(uint word)
    {
        var instruction = PpcDisassembler.Decode(word, 0);
        Assert.Equal($".long 0x{word:X}", instruction.Text);
        Assert.False(instruction.IsValid);
    }

    // A time-base register other than TBL (268) and TBU (269) keeps its number.
    [Fact]
    public void Mftb_of_another_register_keeps_the_number() =>
        Assert.Equal("mftb r3,270", PpcDisassembler.Decode(0x7C6E42E6, 0).Text);

    [Fact]
    public void Operands_are_structured()
    {
        Assert.Equal([new PpcOperand(PpcOperandKind.Gpr, 0), new PpcOperand(PpcOperandKind.Displacement, 8, 1)],
            PpcDisassembler.Decode(0x90010008, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Gpr, 3), new PpcOperand(PpcOperandKind.Gpr, 3), new PpcOperand(PpcOperandKind.Immediate, 1)],
            PpcDisassembler.Decode(0x3863FFFF, 0).Operands);
        Assert.Equal("subi", PpcDisassembler.Decode(0x3863FFFF, 0).Mnemonic);
        Assert.Equal([new PpcOperand(PpcOperandKind.Gpr, 0)], PpcDisassembler.Decode(0x7C0802A6, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.CrField, 7), new PpcOperand(PpcOperandKind.Gpr, 3), new PpcOperand(PpcOperandKind.Immediate, -1)],
            PpcDisassembler.Decode(0x2F83FFFF, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Gpr, 3), new PpcOperand(PpcOperandKind.Gpr, 4), new PpcOperand(PpcOperandKind.Immediate, 0x8000, Hex: true)],
            PpcDisassembler.Decode(0x60838000, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Fpr, 1), new PpcOperand(PpcOperandKind.Fpr, 2), new PpcOperand(PpcOperandKind.Fpr, 4), new PpcOperand(PpcOperandKind.Fpr, 3)],
            PpcDisassembler.Decode(0xFC22193A, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Vr, 1), new PpcOperand(PpcOperandKind.Immediate, 0), new PpcOperand(PpcOperandKind.Gpr, 4)],
            PpcDisassembler.Decode(0x7C2020CE, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Spr, 26), new PpcOperand(PpcOperandKind.Gpr, 3)],
            PpcDisassembler.Decode(0x7C7A03A6, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.CrBit, 6)], PpcDisassembler.Decode(0x4CC63182, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.CrField, 1), new PpcOperand(PpcOperandKind.BranchTarget, 0x1008)],
            PpcDisassembler.Decode(0x40860008, 0x1000).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Immediate, 0x00000000, Hex: true)], PpcDisassembler.Decode(0, 0).Operands);
        Assert.Empty(PpcDisassembler.Decode(0x60000000, 0).Operands);
    }

    [Theory]
    [InlineData(PpcOperandKind.Gpr, 31, 0, false, "r31")]
    [InlineData(PpcOperandKind.Fpr, 0, 0, false, "f0")]
    [InlineData(PpcOperandKind.Vr, 9, 0, false, "v9")]
    [InlineData(PpcOperandKind.CrField, 7, 0, false, "cr7")]
    [InlineData(PpcOperandKind.CrBit, 3, 0, false, "so")]
    [InlineData(PpcOperandKind.CrBit, 29, 0, false, "4*cr7+gt")]
    [InlineData(PpcOperandKind.Spr, 8, 0, false, "lr")]
    [InlineData(PpcOperandKind.Spr, 1013, 0, false, "dabr")]
    [InlineData(PpcOperandKind.Spr, 1008, 0, false, "1008")]
    [InlineData(PpcOperandKind.Immediate, -5, 0, false, "-5")]
    [InlineData(PpcOperandKind.Immediate, 255, 0, true, "0xFF")]
    [InlineData(PpcOperandKind.Displacement, -8, 31, false, "-8(r31)")]
    [InlineData(PpcOperandKind.Displacement, 4, 0, false, "4(0)")]
    [InlineData(PpcOperandKind.BranchTarget, 0x10000, 0, false, "0x10000")]
    public void Operand_text(PpcOperandKind kind, long value, int @base, bool hex, string expected) =>
        Assert.Equal(expected, new PpcOperand(kind, value, @base, hex).ToString());

    [Fact]
    public void Operand_of_an_unknown_kind_throws() =>
        Assert.Throws<InvalidOperationException>(() => new PpcOperand((PpcOperandKind)99, 0).ToString());

    [Fact]
    public void Instruction_prints_its_address_word_and_text() =>
        Assert.Equal("00001000  7C0802A6  mflr r0", PpcDisassembler.Decode(0x7C0802A6, 0x1000).ToString());

    [Fact]
    public void Disassemble_reads_big_endian_words_and_ignores_a_trailing_partial_word()
    {
        byte[] code = [0x7C, 0x08, 0x02, 0xA6, 0x4E, 0x80, 0x00, 0x20, 0x00, 0x00];
        var instructions = PpcDisassembler.Disassemble(code, 0x2000).ToList();
        Assert.Equal(["mflr r0", "blr"], instructions.Select(i => i.Text));
        Assert.Equal([0x2000u, 0x2004u], instructions.Select(i => i.Address));
        Assert.Empty(PpcDisassembler.Disassemble(ReadOnlyMemory<byte>.Empty, 0));
    }

    [Theory]
    [InlineData(1, "xer")]
    [InlineData(8, "lr")]
    [InlineData(9, "ctr")]
    [InlineData(18, "dsisr")]
    [InlineData(19, "dar")]
    [InlineData(22, "dec")]
    [InlineData(25, "sdr1")]
    [InlineData(26, "srr0")]
    [InlineData(27, "srr1")]
    [InlineData(256, "vrsave")]
    [InlineData(272, "sprg0")]
    [InlineData(275, "sprg3")]
    [InlineData(282, "ear")]
    [InlineData(284, "tbl")]
    [InlineData(285, "tbu")]
    [InlineData(287, "pvr")]
    [InlineData(528, "ibat0u")]
    [InlineData(535, "ibat3l")]
    [InlineData(536, "dbat0u")]
    [InlineData(543, "dbat3l")]
    [InlineData(1013, "dabr")]
    [InlineData(0, null)]
    [InlineData(1008, null)]
    public void Spr_names(int spr, string? expected) => Assert.Equal(expected, PpcDisassembler.SprName(spr));
}
