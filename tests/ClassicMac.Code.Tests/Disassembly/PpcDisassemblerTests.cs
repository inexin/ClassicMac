using ClassicMac.Code.Disassembly;

namespace ClassicMac.Code.Tests.Disassembly;

public class PpcDisassemblerTests
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
    // The PIC "get the PC" idiom: bcl 20,31 to the next word is a call that always branches.
    [InlineData(0x1000u, 0x429F0005u, "bcl 20,31,0x1004", PpcFlow.Branch | PpcFlow.Call, 0x1004u)]
    [InlineData(0x1000u, 0x42800003u, "bcla 20,0,0x0", PpcFlow.Branch | PpcFlow.Call, 0x0u)]
    // bclr/bcctr branch-always with BI non-zero keep BO and BI (only BO 20, BI 0 is blr/bctr).
    [InlineData(0x1000u, 0x4E810020u, "bclr 20,1", PpcFlow.Branch | PpcFlow.Return, null)]
    [InlineData(0x1000u, 0x4E810021u, "bclrl 20,1", PpcFlow.Branch | PpcFlow.Call, null)]
    [InlineData(0x1000u, 0x4E9F0420u, "bcctr 20,31", PpcFlow.Branch, null)]
    // A CTR-only BO (1z00y, 1z01y) ignores BI: a non-zero BI keeps the raw form too.
    [InlineData(0x1000u, 0x42410008u, "bc 18,1,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x42584765u, "bcl 18,24,0x5764", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, 0x5764u)]
    [InlineData(0x1000u, 0x426F6D72u, "bca 19,15,0x6D70", PpcFlow.Branch | PpcFlow.Conditional, 0x6D70u)]
    [InlineData(0x1000u, 0x4E010020u, "bclr 16,1", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4E400021u, "bdzlrl", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4D820021u, "beqlrl", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C820420u, "bnectr", PpcFlow.Branch | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4DA20420u, "beqctr+", PpcFlow.Branch | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4D800420u, "bltctr", PpcFlow.Branch | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4DA00421u, "bltctrl+", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C810420u, "blectr", PpcFlow.Branch | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C000020u, "bdnzflr lt", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C200020u, "bdnzflr+ lt", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4E400020u, "bdzlr", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4E200020u, "bdnzlr+", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4E000021u, "bdnzlrl", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C800020u, "bgelr", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    [InlineData(0x1000u, 0x4C9F0020u, "bnslr cr7", PpcFlow.Branch | PpcFlow.Return | PpcFlow.Conditional, null)]
    // The y bit with a backward bd.
    [InlineData(0x1000u, 0x4220FFF8u, "bdnz- 0xFF8", PpcFlow.Branch | PpcFlow.Conditional, 0xFF8u)]
    // bd = 0 counts as forward: y = 1 is "+".
    [InlineData(0x1000u, 0x41800000u, "blt 0x1000", PpcFlow.Branch | PpcFlow.Conditional, 0x1000u)]
    [InlineData(0x1000u, 0x41A00000u, "blt+ 0x1000", PpcFlow.Branch | PpcFlow.Conditional, 0x1000u)]
    [InlineData(0x1000u, 0x40000000u, "bdnzf lt,0x1000", PpcFlow.Branch | PpcFlow.Conditional, 0x1000u)]
    [InlineData(0x1000u, 0x4182FFF8u, "beq 0xFF8", PpcFlow.Branch | PpcFlow.Conditional, 0xFF8u)]
    [InlineData(0x1000u, 0x4182000Bu, "beqla 0x8", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, 0x8u)]
    [InlineData(0x1000u, 0x40A00008u, "bge+ 0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x40200008u, "bdnzf+ lt,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x4020FFF8u, "bdnzf- lt,0xFF8", PpcFlow.Branch | PpcFlow.Conditional, 0xFF8u)]
    [InlineData(0x1000u, 0x41200008u, "bdnzt+ lt,0x1008", PpcFlow.Branch | PpcFlow.Conditional, 0x1008u)]
    [InlineData(0x1000u, 0x4200FFF9u, "bdnzl 0xFF8", PpcFlow.Branch | PpcFlow.Call | PpcFlow.Conditional, 0xFF8u)]
    // LI extremes: the largest forward and backward displacements; a branch to itself.
    [InlineData(0x1000u, 0x49FFFFFCu, "b 0x2000FFC", PpcFlow.Branch, 0x2000FFCu)]
    [InlineData(0x1000u, 0x4A000000u, "b 0xFE001000", PpcFlow.Branch, 0xFE001000u)]
    [InlineData(0x1000u, 0x48000000u, "b 0x1000", PpcFlow.Branch, 0x1000u)]
    [InlineData(0x1000u, 0x4BFFFFFDu, "bl 0xFFC", PpcFlow.Branch | PpcFlow.Call, 0xFFCu)]
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

    // bcctr may not decrement CTR (BO bit 2 clear) [Doc: PEM, "bcctrx"]; bclr and bcctr with reserved bits 16–20 set.
    // A BO with a z bit set (001zy, 011zy, 1z00y, 1z01y, 1z1zz) [Doc: PEM, BO operand encodings: the z bits are to
    // be cleared; ClassicMac reads them as an invalid form].
    [Theory]
    [InlineData(0x40C00000u)]   // bc BO 00110
    [InlineData(0x41C20008u)]   // bc BO 01110
    [InlineData(0x41E20008u)]   // bc BO 01111
    [InlineData(0x43000000u)]   // bc BO 11000
    [InlineData(0x43200008u)]   // bc BO 11001
    [InlineData(0x436F6D72u)]   // bca BO 11011
    [InlineData(0x42A00000u)]   // bc BO 10101
    [InlineData(0x42C00000u)]   // bc BO 10110
    [InlineData(0x43800000u)]   // bc BO 11100
    [InlineData(0x4FE00020u)]   // bclr BO 11111
    [InlineData(0x4DC20020u)]   // bclr BO 01110
    [InlineData(0x4EA00421u)]   // bcctrl BO 10101
    [InlineData(0x4CC20420u)]   // bcctr BO 00110
    [InlineData(0x4E000420u)]
    [InlineData(0x4C000420u)]
    [InlineData(0x4C000421u)]
    [InlineData(0x4E800820u)]
    [InlineData(0x4E800C20u)]
    [InlineData(0x4E800021u | 0x8000u)]
    public void Invalid_branches_are_long(uint word)
    {
        var instruction = PpcDisassembler.Decode(word, 0);
        Assert.Equal($".long 0x{word:X}", instruction.Text);
        Assert.Null(instruction.Target);
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
    // Every part of each multi-bit reserved mask, one probe per field.
    [InlineData(0x4CA00000u)]   // mcrf with bit 10 set
    [InlineData(0x4CC00000u)]   // mcrf with bit 9 set
    [InlineData(0x4C820000u)]   // mcrf with bit 14 set
    [InlineData(0x4C800800u)]   // mcrf with bit 20 set
    [InlineData(0x4C800001u)]   // mcrf with bit 31 set
    [InlineData(0x7C600826u)]   // mfcr with rB set
    [InlineData(0x7C6100A6u)]   // mfmsr with rA set
    [InlineData(0x7C6008A6u)]   // mfmsr with rB set
    [InlineData(0x7C610124u)]   // mtmsr with rA set
    [InlineData(0x7C602124u)]   // mtmsr with rB set
    [InlineData(0x7C680920u)]   // mtcrf with bit 20 set
    [InlineData(0x7D010400u)]   // mcrxr with rA set
    [InlineData(0x7D200400u)]   // mcrxr with bit 10 set
    [InlineData(0x7D400400u)]   // mcrxr with bit 9 set
    [InlineData(0x7C7504A6u)]   // mfsr with bit 11 set (SR is 4 bits)
    [InlineData(0x7C7501A4u)]   // mtsr with bit 11 set
    [InlineData(0x7C6509A4u)]   // mtsr with rB set
    [InlineData(0x7C6121E4u)]   // mtsrin with rA set
    [InlineData(0x7C202264u)]   // tlbie with rS set
    [InlineData(0x7C0104ACu)]   // sync with rA set
    [InlineData(0xFFA0510Cu)]   // mtfsfi with bit 10 set
    [InlineData(0xFFC0510Cu)]   // mtfsfi with bit 9 set
    [InlineData(0xFF80590Cu)]   // mtfsfi with bit 20 set
    [InlineData(0xFFE0804Cu)]   // mtfsb1 with bit 16 set
    [InlineData(0xFFE0084Cu)]   // mtfsb1 with bit 20 set
    [InlineData(0xFFC1008Cu)]   // mtfsb0 with bit 15 set
    [InlineData(0xFCA00080u)]   // mcrfs with bit 10 set
    [InlineData(0xFC900880u)]   // mcrfs with bit 20 set
    [InlineData(0xFC900081u)]   // mcrfs with bit 31 set
    [InlineData(0xFC21048Eu)]   // mffs with frA set
    [InlineData(0xFDFF158Eu)]   // mtfsf with bit 15 set
    [InlineData(0xFC211000u)]   // fcmpu with bit 10 set
    [InlineData(0xFC011041u)]   // fcmpo with Rc
    [InlineData(0x7C432000u)]   // cmpw with bit 9 set
    [InlineData(0x7C032001u)]   // cmpw with Rc
    [InlineData(0x2C430000u)]   // cmpwi with bit 9 set
    [InlineData(0x7D0322ACu)]   // dst with bit 7 set
    [InlineData(0x7D0322ECu)]   // dstst with bit 7 set
    [InlineData(0x7C60166Cu)]   // dss with rB set
    [InlineData(0x7D00066Cu)]   // dss with bit 7 set
    [InlineData(0x7C2321CFu)]   // stvx with Rc
    [InlineData(0x7C6802A7u)]   // mfspr with Rc
    [InlineData(0x7C6C42E7u)]   // mftb with Rc
    [InlineData(0x7C0322ADu)]   // dst with Rc
    [InlineData(0x7C0027A5u)]   // tlbld with Rc
    [InlineData(0x7C2027A4u)]   // tlbld with rD set
    [InlineData(0x7C0127E4u)]   // tlbli with rA set
    public void Reserved_fields_and_undefined_opcodes_are_long(uint word)
    {
        var instruction = PpcDisassembler.Decode(word, 0);
        Assert.Equal($".long 0x{word:X}", instruction.Text);
        Assert.False(instruction.IsValid);
    }

    // mftb reads TBL (268) or TBU (269); any other TBR makes the form invalid [Doc: PEM, "mftb"].
    [Theory]
    [InlineData(0x7C6C42E6u, "mftb r3")]
    [InlineData(0x7C6D42E6u, "mftbu r3")]
    [InlineData(0x7C6E42E6u, ".long 0x7C6E42E6")]
    [InlineData(0x7C7C42E6u, ".long 0x7C7C42E6")]
    [InlineData(0x7C6042E6u, ".long 0x7C6042E6")]
    [InlineData(0x7C6C02E6u, ".long 0x7C6C02E6")]
    public void Mftb_takes_only_TBL_and_TBU(uint word, string expected) =>
        Assert.Equal(expected, PpcDisassembler.Decode(word, 0).Text);

    // An update form with rA = 0, or a load with update with rA = rD, is invalid [Doc: PEM, "lwzu" ... "stfdux":
    // "If rA = 0 (or rA = rD), the instruction form is invalid"]. A floating-point load's rD is an FPR, so rA = rD
    // there is valid, as it is for a store.
    [Theory]
    [InlineData(0x84600000u, ".long 0x84600000")]   // lwzu r3,0(r0)
    [InlineData(0x8C630000u, ".long 0x8C630000")]   // lbzu r3,0(r3)
    [InlineData(0xA4630002u, ".long 0xA4630002")]   // lhzu r3,2(r3)
    [InlineData(0xAC630002u, ".long 0xAC630002")]   // lhau r3,2(r3)
    [InlineData(0xAC600002u, ".long 0xAC600002")]   // lhau r3,2(r0)
    [InlineData(0x94600000u, ".long 0x94600000")]   // stwu r3,0(r0)
    [InlineData(0x9C600000u, ".long 0x9C600000")]   // stbu
    [InlineData(0xB4600000u, ".long 0xB4600000")]   // sthu
    [InlineData(0xC4600000u, ".long 0xC4600000")]   // lfsu f3,0(r0)
    [InlineData(0xCC200000u, ".long 0xCC200000")]   // lfdu
    [InlineData(0xD4200000u, ".long 0xD4200000")]   // stfsu
    [InlineData(0xDC200000u, ".long 0xDC200000")]   // stfdu
    [InlineData(0x7C60286Eu, ".long 0x7C60286E")]   // lwzux r3,r0,r5
    [InlineData(0x7C63286Eu, ".long 0x7C63286E")]   // lwzux r3,r3,r5
    [InlineData(0x7C6328EEu, ".long 0x7C6328EE")]   // lbzux r3,r3,r5
    [InlineData(0x7C632A6Eu, ".long 0x7C632A6E")]   // lhzux r3,r3,r5
    [InlineData(0x7C632AEEu, ".long 0x7C632AEE")]   // lhaux r3,r3,r5
    [InlineData(0x7C60296Eu, ".long 0x7C60296E")]   // stwux r3,r0,r5
    [InlineData(0x7C6029EEu, ".long 0x7C6029EE")]   // stbux
    [InlineData(0x7C602B6Eu, ".long 0x7C602B6E")]   // sthux
    [InlineData(0x7C20246Eu, ".long 0x7C20246E")]   // lfsux f1,r0,r4
    [InlineData(0x7C2024EEu, ".long 0x7C2024EE")]   // lfdux
    [InlineData(0x7C20256Eu, ".long 0x7C20256E")]   // stfsux
    [InlineData(0x7C2025EEu, ".long 0x7C2025EE")]   // stfdux
    [InlineData(0x94630000u, "stwu r3,0(r3)")]
    [InlineData(0x7C63296Eu, "stwux r3,r3,r5")]
    [InlineData(0xC4630000u, "lfsu f3,0(r3)")]
    [InlineData(0x7C6324EEu, "lfdux f3,r3,r4")]
    [InlineData(0x8C640000u, "lbzu r3,0(r4)")]
    public void Update_forms_with_rA_0_or_rA_rD_are_long(uint word, string expected) =>
        Assert.Equal(expected, PpcDisassembler.Decode(word, 0).Text);

    // lmw and lswi: rA in the range of registers to be loaded, including the case in which rA = 0, is invalid; the
    // lswi range wraps from r31 to r0 and holds ceil(NB/4) registers. lswx: rD = rA or rD = rB is invalid (the
    // range comes from XER at run time) [Doc: PEM, "lmw", "lswi", "lswx"]. The stores have no invalid form.
    [Theory]
    [InlineData(0xB8000000u, ".long 0xB8000000")]   // lmw r0,0(0): r0 is loaded
    [InlineData(0xB8630000u, ".long 0xB8630000")]   // lmw r3,0(r3)
    [InlineData(0xB87F0000u, ".long 0xB87F0000")]   // lmw r3,0(r31)
    [InlineData(0xB8620000u, "lmw r3,0(r2)")]
    [InlineData(0xB8600000u, "lmw r3,0(0)")]        // r0 is not in r3-r31
    [InlineData(0xBC630000u, "stmw r3,0(r3)")]
    [InlineData(0x7C6404AAu, ".long 0x7C6404AA")]   // lswi r3,r4,32: r3-r10
    [InlineData(0x7CA524AAu, ".long 0x7CA524AA")]   // lswi r5,r5,4: one register
    [InlineData(0x7CA62CAAu, ".long 0x7CA62CAA")]   // lswi r5,r6,5: two registers
    [InlineData(0x7FC084AAu, ".long 0x7FC084AA")]   // lswi r30,0,16: r30, r31, r0, r1
    [InlineData(0x7FC184AAu, ".long 0x7FC184AA")]   // lswi r30,r1,16
    [InlineData(0x7FC284AAu, "lswi r30,r2,16")]
    [InlineData(0x7CA624AAu, "lswi r5,r6,4")]
    [InlineData(0x7CAC04AAu, ".long 0x7CAC04AA")]   // lswi r5,r12,32: r5-r12
    [InlineData(0x7CAD04AAu, "lswi r5,r13,32")]
    [InlineData(0x7CA5342Au, ".long 0x7CA5342A")]   // lswx r5,r5,r6
    [InlineData(0x7CA42C2Au, ".long 0x7CA42C2A")]   // lswx r5,r4,r5
    [InlineData(0x7C00342Au, ".long 0x7C00342A")]   // lswx r0,0,r6: rD = rA
    [InlineData(0x7CA4342Au, "lswx r5,r4,r6")]
    [InlineData(0x7CA5352Au, "stswx r5,r5,r6")]
    [InlineData(0x7CA525AAu, "stswi r5,r5,4")]
    public void Multiple_and_string_loads_with_rA_in_range_are_long(uint word, string expected) =>
        Assert.Equal(expected, PpcDisassembler.Decode(word, 0).Text);

    // SPR names follow the direction: TBL and TBU (284, 285) are written with mtspr and read with mftb, PVR (287) is
    // read only [Doc: PEM, "mfspr", "mtspr"]; the other way keeps the number.
    [Theory]
    [InlineData(0x7C7C42A6u, "mfspr r3,284")]
    [InlineData(0x7C7D42A6u, "mfspr r3,285")]
    [InlineData(0x7C7C43A6u, "mtspr tbl,r3")]
    [InlineData(0x7C7D43A6u, "mtspr tbu,r3")]
    [InlineData(0x7C7F42A6u, "mfspr r3,pvr")]
    [InlineData(0x7C7F43A6u, "mtspr 287,r3")]
    public void Spr_names_follow_the_direction(uint word, string expected) =>
        Assert.Equal(expected, PpcDisassembler.Decode(word, 0).Text);

    // The 603's software table-walk operations [Doc: PEM, "tlbld", "tlbli" (603e-specific)].
    [Theory]
    [InlineData(0x7C0027A4u, "tlbld r4")]
    [InlineData(0x7C0027E4u, "tlbli r4")]
    public void Tlbld_and_tlbli(uint word, string expected) =>
        Assert.Equal(expected, PpcDisassembler.Decode(word, 0).Text);

    // vspltb, vsplth and vspltw take a 4-, 3- and 2-bit UIMM; the bits above it are reserved [Doc: AltiVec PEM,
    // "vspltb", "vsplth", "vspltw"].
    [Theory]
    [InlineData(0x102F120Cu, "vspltb v1,v2,15")]
    [InlineData(0x1030120Cu, ".long 0x1030120C")]
    [InlineData(0x1027124Cu, "vsplth v1,v2,7")]
    [InlineData(0x1028124Cu, ".long 0x1028124C")]
    [InlineData(0x1033124Cu, ".long 0x1033124C")]
    [InlineData(0x1023128Cu, "vspltw v1,v2,3")]
    [InlineData(0x1027128Cu, ".long 0x1027128C")]
    [InlineData(0x102B128Cu, ".long 0x102B128C")]
    [InlineData(0x1033128Cu, ".long 0x1033128C")]
    public void Vector_splat_reserved_UIMM_bits(uint word, string expected) =>
        Assert.Equal(expected, PpcDisassembler.Decode(word, 0).Text);

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
        // mtfsfi's crfD selects an FPSCR field, a number, not a CR field.
        Assert.Equal([new PpcOperand(PpcOperandKind.Immediate, 7), new PpcOperand(PpcOperandKind.Immediate, 5, Hex: true)],
            PpcDisassembler.Decode(0xFF80510C, 0).Operands);
        // An SPR without a name in that direction is a plain number.
        Assert.Equal([new PpcOperand(PpcOperandKind.Gpr, 3), new PpcOperand(PpcOperandKind.Immediate, 284)],
            PpcDisassembler.Decode(0x7C7C42A6, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Spr, 284), new PpcOperand(PpcOperandKind.Gpr, 3)],
            PpcDisassembler.Decode(0x7C7C43A6, 0).Operands);
        // bclr branch-always with BI kept: BO and BI as numbers, no target.
        Assert.Equal([new PpcOperand(PpcOperandKind.Immediate, 20), new PpcOperand(PpcOperandKind.Immediate, 1)],
            PpcDisassembler.Decode(0x4E810020, 0).Operands);
        Assert.Equal([new PpcOperand(PpcOperandKind.Gpr, 4)], PpcDisassembler.Decode(0x7C0027A4, 0).Operands);
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
    [InlineData(273, "sprg1")]
    [InlineData(274, "sprg2")]
    [InlineData(275, "sprg3")]
    [InlineData(529, "ibat0l")]
    [InlineData(530, "ibat1u")]
    [InlineData(533, "ibat2l")]
    [InlineData(534, "ibat3u")]
    [InlineData(537, "dbat0l")]
    [InlineData(538, "dbat1u")]
    [InlineData(541, "dbat2l")]
    [InlineData(542, "dbat3u")]
    [InlineData(268, null)]
    [InlineData(276, null)]
    [InlineData(527, null)]
    [InlineData(544, null)]
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
