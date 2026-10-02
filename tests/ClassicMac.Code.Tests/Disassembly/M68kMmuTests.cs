using ClassicMac.Code.Disassembly;

namespace ClassicMac.Code.Tests.Disassembly;

// The 68030's and 68851's PMMU instructions (coprocessor 0, $F0xx), hand-built from the encodings [Doc: MC68030
// User's Manual (Motorola), 9 and 11; MC68851 Paged Memory Management Unit User's Manual (Motorola), 6].
public class M68kMmuTests
{
    private const uint At = 0x1000;

    private static M68kInstruction D(string hex) =>
        M68kDisassembler.Decode(M68kDisassemblerTests.Bytes(hex), 0, At);

    [Theory]
    // pmove, 68030 and 68851: format 010 (TC, DRP, SRP, CRP, CAL, VAL, SCC, AC); bit 9 register to memory; bit 8 FD
    [InlineData("F010 4000", "pmove (a0),tc")]
    [InlineData("F010 4200", "pmove tc,(a0)")]
    [InlineData("F010 4100", "pmovefd (a0),tc")]
    [InlineData("F010 4400", "pmove (a0),drp")]
    [InlineData("F010 4600", "pmove drp,(a0)")]
    [InlineData("F010 4800", "pmove (a0),srp")]
    [InlineData("F010 4900", "pmovefd (a0),srp")]
    [InlineData("F010 4A00", "pmove srp,(a0)")]
    [InlineData("F010 4C00", "pmove (a0),crp")]
    [InlineData("F010 4D00", "pmovefd (a0),crp")]
    [InlineData("F010 4E00", "pmove crp,(a0)")]
    [InlineData("F010 5000", "pmove (a0),cal")]
    [InlineData("F010 5200", "pmove cal,(a0)")]
    [InlineData("F010 5400", "pmove (a0),val")]
    [InlineData("F010 5600", "pmove val,(a0)")]
    [InlineData("F010 5800", "pmove (a0),scc")]
    [InlineData("F010 5A00", "pmove scc,(a0)")]
    [InlineData("F010 5C00", "pmove (a0),ac")]
    [InlineData("F010 5E00", "pmove ac,(a0)")]
    // format 000: the 68030's transparent translation registers
    [InlineData("F010 0800", "pmove (a0),tt0")]
    [InlineData("F010 0900", "pmovefd (a0),tt0")]
    [InlineData("F010 0A00", "pmove tt0,(a0)")]
    [InlineData("F010 0C00", "pmove (a0),tt1")]
    [InlineData("F010 0D00", "pmovefd (a0),tt1")]
    [InlineData("F010 0E00", "pmove tt1,(a0)")]
    // format 011: MMUSR (the 68851's PSR), PCSR, BADn, BACn
    [InlineData("F010 6000", "pmove (a0),mmusr")]
    [InlineData("F010 6200", "pmove mmusr,(a0)")]
    [InlineData("F010 6600", "pmove pcsr,(a0)")]
    [InlineData("F010 720C", "pmove bad3,(a0)")]
    [InlineData("F010 701C", "pmove (a0),bad7")]
    [InlineData("F010 7414", "pmove (a0),bac5")]
    [InlineData("F010 7600", "pmove bac0,(a0)")]
    // the 68851's other addressing modes
    [InlineData("F03C 4000 0000 8000", "pmove #$00008000,tc")]
    [InlineData("F03C 5000 0005", "pmove #5,cal")]
    [InlineData("F03C 5C00 0102", "pmove #258,ac")]
    [InlineData("F03C 4800 0000 0001 0000 0002", "pmove #$0000000100000002,srp")]
    [InlineData("F000 5C00", "pmove d0,ac")]
    [InlineData("F009 4200", "pmove tc,a1")]
    [InlineData("F02E 6200 FFFE", "pmove mmusr,-2(a6)")]
    [InlineData("F039 4A00 0000 1000", "pmove srp,($00001000).l")]
    // pflush: mode 001 all, 100 by function code, 101 shared, 110 by function code and address, 111 shared
    [InlineData("F000 2400", "pflusha")]
    [InlineData("F000 30F5", "pflush #5,#7")]
    [InlineData("F000 300A", "pflush d2,#0")]
    [InlineData("F000 3000", "pflush sfc,#0")]
    [InlineData("F000 3001", "pflush dfc,#0")]
    [InlineData("F000 3020", "pflush sfc,#1")]
    [InlineData("F010 3821", "pflush dfc,#1,(a0)")]
    [InlineData("F000 3411", "pflushs #1,#0")]
    [InlineData("F010 3C11", "pflushs #1,#0,(a0)")]
    [InlineData("F000 3110", "pflush #0,#8")]   // the 68851's four-bit mask
    [InlineData("F000 3018", "pflush #8,#0")]   // the 68851's four-bit function code
    [InlineData("F010 A000", "pflushr (a0)")]
    [InlineData("F03C A000 0000 0001 0000 0002", "pflushr #$0000000100000002")]
    // pload, pvalid
    [InlineData("F010 2210", "ploadr #0,(a0)")]
    [InlineData("F010 2008", "ploadw d0,(a0)")]
    [InlineData("F010 2001", "ploadw dfc,(a0)")]
    [InlineData("F010 2800", "pvalid val,(a0)")]
    [InlineData("F010 2C03", "pvalid a3,(a0)")]
    // ptest: level bits 12-10, R bit 9, A bit 8, An bits 7-5, function code bits 4-0
    [InlineData("F010 8210", "ptestr #0,(a0),#0")]
    [InlineData("F010 8213", "ptestr #3,(a0),#0")]
    [InlineData("F010 8F55", "ptestr #5,(a0),#3,a2")]
    [InlineData("F010 8409", "ptestw d1,(a0),#1")]
    [InlineData("F02E 9C00 0010", "ptestw sfc,16(a6),#7")]
    // the 68851's conditional instructions
    [InlineData("F050 0003", "pslc (a0)")]
    [InlineData("F040 000E", "pscs d0")]
    [InlineData("F048 0001 FFFC", "pdbbc d0,$1000")]
    [InlineData("F07A 0008 1234", "ptrapws.w #$1234")]
    [InlineData("F07B 0009 0001 0000", "ptrapwc.l #$00010000")]
    [InlineData("F07C 000F", "ptrapcc")]
    [InlineData("F081 FFFE", "pbbc.w $1000")]
    [InlineData("F0C2 0000 0010", "pbls.l $1012")]
    [InlineData("F08C 0010", "pbgs.w $1012")]
    // psave, prestore
    [InlineData("F110", "psave (a0)")]
    [InlineData("F127", "psave -(sp)")]
    [InlineData("F150", "prestore (a0)")]
    [InlineData("F15F", "prestore (sp)+")]
    public void Decodes(string hex, string expected)
    {
        var ins = D(hex);
        Assert.Equal(expected, ins.Text);
        Assert.Equal(hex.Replace(" ", "", StringComparison.Ordinal).Length / 2, ins.Length);
        Assert.False(ins.IsInvalid);
        Assert.True((ins.Flags & M68kFlags.FLine) != 0);
    }

    [Theory]
    [InlineData("F000 0000")]              // format 000 register 000
    [InlineData("F010 0080")]              // pmove tt0 with low bits set
    [InlineData("F010 4080")]              // pmove tc with low bits set
    [InlineData("F000 0800")]              // pmove tt0 from a data register (control alterable only)
    [InlineData("F018 4100")]              // pmovefd from (a0)+ (control alterable only)
    [InlineData("F010 0B00")]              // pmovefd to memory
    [InlineData("F010 4300")]              // pmovefd tc to memory
    [InlineData("F010 5100")]              // FD with a 68851 register
    [InlineData("F010 6100")]              // FD with mmusr
    [InlineData("F010 6400")]              // pmove to the read-only pcsr
    [InlineData("F010 6800")]              // format 011 register 010
    [InlineData("F010 6001")]              // pmove mmusr with low bits set
    [InlineData("F010 7201")]              // pmove bad with low bits set
    [InlineData("F03C 4200")]              // pmove tc to an immediate
    [InlineData("F03A 4200 0000")]         // pmove tc to PC space
    [InlineData("F000 4800")]              // pmove srp from a data register (64 bits)
    [InlineData("F008 4C00")]              // pmove crp from an address register
    [InlineData("F008 5000")]              // pmove cal (a byte) from an address register
    [InlineData("F03C 5000 0105")]         // pmove #imm,cal with the immediate's high byte set
    [InlineData("F010 2400")]              // pflusha with an effective address
    [InlineData("F000 2410")]              // pflusha with a function code
    [InlineData("F000 3002")]              // function code 00010
    [InlineData("F000 3200")]              // pflush with bit 9 set
    [InlineData("F000 3800")]              // pflush by address on a data register
    [InlineData("F018 3800")]              // pflush by address on (a0)+
    [InlineData("F010 3000")]              // pflush by function code with an effective address
    [InlineData("F000 A000")]              // pflushr from a data register
    [InlineData("F010 A001")]              // pflushr with low bits set
    [InlineData("F010 2020")]              // pload with bits 8-5 set
    [InlineData("F018 2010")]              // pload (a0)+
    [InlineData("F000 2010")]              // pload d0
    [InlineData("F010 2801")]              // pvalid val with low bits set
    [InlineData("F010 2C08")]              // pvalid An with bit 3 set
    [InlineData("F000 2800")]              // pvalid d0
    [InlineData("F010 8330")]              // ptest with A set at level 0
    [InlineData("F010 8220")]              // ptest without A but a register
    [InlineData("F000 8210")]              // ptest d0
    [InlineData("F010 E000")]              // extension word 111
    [InlineData("F010 C000")]              // extension word 110
    [InlineData("F050 0010")]              // pscc condition 16
    [InlineData("F050 0100")]              // pscc with bit 8 set
    [InlineData("F048 0040 0000")]         // pdbcc condition 64
    [InlineData("F07D 0000")]              // ptrapcc mode 5
    [InlineData("F078 0000")]              // ps.. (xxx).w cut short
    [InlineData("F090 FFFE")]              // pbcc condition 16
    [InlineData("F0A0 FFFE")]              // pbcc condition 32
    [InlineData("F118")]                   // psave (a0)+
    [InlineData("F100")]                   // psave d0
    [InlineData("F160")]                   // prestore -(a0)
    [InlineData("F140")]                   // prestore d0
    [InlineData("F180")]                   // coprocessor 0 type 6
    [InlineData("F1C0")]                   // coprocessor 0 type 7
    [InlineData("F010 40")]                // pmove cut short
    public void Invalid_words_are_dc_w(string hex)
    {
        var ins = D(hex);
        Assert.True(ins.IsInvalid);
        Assert.Equal("dc", ins.Mnemonic);
        Assert.Equal(2, ins.Length);
    }

    [Fact]
    public void Pmove_carries_its_register()
    {
        var ins = D("F010 720C");
        Assert.Equal(("pmove", M68kSize.None), (ins.Mnemonic, ins.Size));
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.MemoryManagement, 0x1C | (3 << 6)), ins.Operands[0]);
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.MemoryManagement, 0x10), D("F010 4000").Operands[1]);
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.MemoryManagement, 0x02), D("F010 0800").Operands[1]);
        Assert.Equal(M68kFlags.FLine, ins.Flags);
    }

    [Fact]
    public void Function_codes_are_operands()
    {
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.Control, 0), D("F000 3000").Operands[0]);
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.Control, 1), D("F000 3001").Operands[0]);
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.Data, 2), D("F000 300A").Operands[0]);
        Assert.Equal(5, ((M68kImmediate)D("F000 30F5").Operands[0]).Value);
        Assert.Equal(7, ((M68kImmediate)D("F000 30F5").Operands[1]).Value);
        Assert.Equal(3, ((M68kImmediate)D("F010 8F55").Operands[2]).Value);
        Assert.Equal(new M68kRegisterOperand(M68kRegisterKind.Address, 2), D("F010 8F55").Operands[3]);
    }

    [Fact]
    public void Pmmu_branches_are_flow()
    {
        var pb = D("F0C2 0000 0010");
        Assert.Equal(("pbls", M68kSize.Long), (pb.Mnemonic, pb.Size));
        Assert.Equal(M68kFlags.Branch | M68kFlags.Conditional | M68kFlags.FLine, pb.Flags);
        Assert.Equal([new M68kReference(0x1012, M68kReferenceKind.Branch)], pb.References);
        var pdb = D("F048 0001 FFFC");
        Assert.Equal(M68kFlags.Branch | M68kFlags.Conditional | M68kFlags.FLine, pdb.Flags);
        Assert.Equal([new M68kReference(0x1000, M68kReferenceKind.Branch)], pdb.References);
        Assert.Equal(M68kFlags.FLine, D("F07C 000F").Flags);
    }

    [Fact]
    public void Pmove_from_pc_space_is_a_data_reference()
    {
        var ins = D("F03A 4000 0010");
        Assert.Equal("pmove 16(pc),tc  ; $1014", ins.Text);
        Assert.Equal([new M68kReference(0x1014, M68kReferenceKind.Data)], ins.References);
    }
}
