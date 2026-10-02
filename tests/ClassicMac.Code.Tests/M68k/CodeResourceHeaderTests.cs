using ClassicMac.Code.M68k;
using ClassicMac.Core;
using static ClassicMac.Code.Tests.M68k.CodeBuilder;

namespace ClassicMac.Code.Tests.M68k;

// The standard header: branch | flags.w | type | id.w | version.w | code. Raw code otherwise.
public class CodeResourceHeaderTests
{
    private static readonly FourCC Cdef = FourCC.FromString("CDEF");

    private static byte[] Header(ushort w0, ushort w1, string type, short id, ushort version, params byte[] code)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(w0);
        w.WriteUInt16(w1);
        w.WriteBytes(MacRoman.Encode(type));
        w.WriteInt16(id);
        w.WriteUInt16(version);
        w.WriteBytes(code);
        return w.ToArray();
    }

    [Fact]
    public void Reads_the_BRA_S_form()
    {
        var diagnostics = new List<Diagnostic>();
        // The System's CDEF 1: 600A 0001 'CDEF' 0001 000B; its flags word is 1.
        var header = CodeResourceHeader.Read(Header(0x600A, 0x0001, "CDEF", 1, 0x0B, 0x4E, 0x75), Cdef, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(new CodeResourceHeader(CodeResourceBranch.BraShort, 0x0C, 0x0001, Cdef, 1, 0x0B), header);
    }

    [Fact]
    public void A_BRA_S_past_the_header_lands_further_in()
    {
        // PACK 3's shape: 604E over a longer header, to $50.
        var diagnostics = new List<Diagnostic>();
        var header = CodeResourceHeader.Read(Header(0x604E, 0x8000, "PACK", 3, 1, new byte[0x50]), FourCC.FromString("PACK"), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.Equal((0x50, (ushort)0x8000), (header.BranchTarget, header.Flags));
    }

    [Theory]
    [InlineData(0x0014, 0x16)]
    [InlineData(0x0786, 0x788)]
    public void A_BRA_W_lands_at_2_plus_its_displacement(int displacement, int target)
    {
        var header = CodeResourceHeader.Read(Header(0x6000, (ushort)displacement, "dcmp", 0, 1, new byte[0x800]), FourCC.FromString("dcmp"), [])!;
        Assert.Equal(target, header.BranchTarget);
    }

    [Theory]
    [InlineData(0x6002)]
    [InlineData(0x6008)]
    [InlineData(0x60FE)]
    public void A_branch_into_the_header_is_reported(int branch)
    {
        var diagnostics = new List<Diagnostic>();
        var header = CodeResourceHeader.Read(Header((ushort)branch, 0, "CDEF", 0, 1, 0x4E, 0x75), Cdef, diagnostics);
        Assert.NotNull(header);
        Assert.Equal("m68k.code-header-branch", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_branch_to_the_end_of_the_header_is_not_reported()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Equal(0x0C, CodeResourceHeader.Read(Header(0x600A, 0, "CDEF", 0, 1, 0x4E, 0x75), Cdef, diagnostics)!.BranchTarget);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Reads_the_BRA_W_form()
    {
        // The displacement word takes the flags word's place: 6000 000A 'dcmp' id version.
        var header = CodeResourceHeader.Read(Header(0x6000, 0x000A, "dcmp", 1, 2, 0x4E, 0x75), FourCC.FromString("dcmp"), [])!;
        Assert.Equal((CodeResourceBranch.BraWord, 0x0C, (short)1, (ushort)2), (header.Branch, header.BranchTarget, header.Id, header.Version));
        Assert.Equal((ushort)0, header.Flags);
    }

    [Fact]
    public void Reads_the_JMP_PC_relative_form()
    {
        var header = CodeResourceHeader.Read(Header(0x4EFA, 0x000A, "ptch", -5, 1, 0x4E, 0x75), FourCC.FromString("ptch"), [])!;
        Assert.Equal((CodeResourceBranch.JmpPcRelative, 0x0C, (short)-5), (header.Branch, header.BranchTarget, header.Id));
    }

    [Fact]
    public void Code_without_the_header_is_raw()
    {
        Assert.Null(CodeResourceHeader.Read(Words(0x4E56, 0, 0x4E5E, 0x4E75, 0x4E71, 0x4E71), null, []));
    }

    [Fact]
    public void A_header_naming_another_type_is_raw_code_when_a_type_is_given()
    {
        // The System's proc -8224 starts 6010 0000 'MDEF' DFE0 0000: read as 'proc' it is raw code (entered at 0, on
        // the branch); read without a type the header is found.
        var proc = Header(0x6010, 0, "MDEF", unchecked((short)0xDFE0), 0, new byte[8]);
        Assert.Null(CodeResourceHeader.Read(proc, FourCC.FromString("proc"), []));
        Assert.Equal((FourCC.FromString("MDEF"), (short)-8224, 0x12), CodeResourceHeader.Read(proc, null, []) is { } h ? (h.Type, h.Id, h.BranchTarget) : default);

        var data = Header(0x600A, 0, "MDEF", 0, 1, 0x4E, 0x75);
        Assert.Null(CodeResourceHeader.Read(data, FourCC.FromString("proc"), []));
        Assert.NotNull(CodeResourceHeader.Read(data, null, []));
    }

    [Fact]
    public void Without_a_type_the_header_must_name_printable_characters()
    {
        Assert.Null(CodeResourceHeader.Read(Words(0x6004, 0, 0, 0x2057, 0x0C98, 0x31C3, 0x4E75), null, []));
    }

    [Fact]
    public void BRA_L_and_short_resources_are_not_headers()
    {
        Assert.Null(CodeResourceHeader.Read(Header(0x60FF, 0, "CDEF", 0, 1, 0x4E, 0x75), Cdef, []));
        Assert.Null(CodeResourceHeader.Read(Header(0x600A, 0, "CDEF", 0, 1)[..11], Cdef, []));
    }

    [Fact]
    public void A_branch_past_the_resource_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var header = CodeResourceHeader.Read(Header(0x607E, 0, "CDEF", 0, 1, 0x4E, 0x75), Cdef, diagnostics);
        Assert.Equal(0x80, header!.BranchTarget);
        Assert.Equal("m68k.code-header-branch", Assert.Single(diagnostics).Code);
    }
}
