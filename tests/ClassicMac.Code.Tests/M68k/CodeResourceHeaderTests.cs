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
        var header = CodeResourceHeader.Read(Header(0x600A, 0, "CDEF", 0, 0x0B, 0x4E, 0x75), Cdef, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(new CodeResourceHeader(CodeResourceBranch.BraShort, 0x0C, 0, Cdef, 0, 0x0B), header);
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
