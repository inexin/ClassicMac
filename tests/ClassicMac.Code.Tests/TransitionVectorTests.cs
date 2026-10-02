using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// Transition vectors {code, TOC} (Mac OS Runtime Architectures ch. 1): the loader's entry points and tvector exports
// point at them in a data section; the relocations add the code section to the first word and the TOC's section to
// the second, so before relocation the second word is the TOC base's offset.
public class TransitionVectorTests
{
    private static PefContainer Read(ushort[] program, out List<Diagnostic> diagnostics)
    {
        var data = PefBuilder.Words(0, 0, 0, 0, 0x40, 0x800, 0x44, 0x800);
        var b = new PefBuilder { HashPower = 0, Main = (1, 0x10), Init = (1, 0x18) };
        b.AddSection(PefSectionKind.Code, new byte[0x80]);
        b.AddSection(PefSectionKind.UnpackedData, data, total: 0x1000);
        b.AddSection(PefSectionKind.UnpackedData, new byte[0x40]);
        b.Libraries.Add(new PefBuilder.Library("Lib", [new("i0")]));
        b.Exports.Add(new("f", 0x00010066, PefSymbolClass.TVector, 0x18, 1));
        b.Relocations.Add((1, program));
        diagnostics = [];
        return PefContainer.Read(b.Build(), diagnostics);
    }

    [Fact]
    public void Main_gives_the_code_offset_and_the_TOC_base()
    {
        var pef = Read([0x800F, 0x4601], out var diagnostics);   // DELTA 16; DSC2 ×2
        Assert.Equal(new PefTransitionVector(0, 0x40, 1, 0x800), pef.GetTransitionVector(pef.Loader!.Main, diagnostics));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Init_and_tvector_exports_read_the_same_way()
    {
        var pef = Read([0x800F, 0x4601], out var diagnostics);
        Assert.Equal(new PefTransitionVector(0, 0x44, 1, 0x800), pef.GetTransitionVector(pef.Loader!.Init, diagnostics));
        var f = pef.Loader.FindExport("f")!;
        Assert.Equal(new PefTransitionVector(0, 0x44, 1, 0x800), pef.GetTransitionVector(f.SectionIndex, f.Value, diagnostics));
    }

    [Fact]
    public void A_TOC_in_another_section_is_named_by_DTIS()
    {
        var pef = Read([0x6402, 0x800F, 0x4600], out var diagnostics);   // DTIS 2; DELTA 16; DSC2
        Assert.Equal(new PefTransitionVector(0, 0x40, 2, 0x800), pef.GetTransitionVector(pef.Loader!.Main, diagnostics));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_12_byte_vector_from_DESC_reads_the_same_way()
    {
        // DELTA 16; DESC: code at $10, TOC at $14, the third word ($18, init's code word) skipped.
        var pef = Read([0x800F, 0x4400], out var diagnostics);
        Assert.Equal(new PefTransitionVector(0, 0x40, 1, 0x800), pef.GetTransitionVector(pef.Loader!.Main, diagnostics));
        Assert.Equal(new PefTransitionVector(-1, 0x44, -1, 0x800), pef.GetTransitionVector(pef.Loader.Init, diagnostics));
    }

    [Fact]
    public void Words_no_relocation_touches_have_section_minus_1()
    {
        var pef = Read([], out var diagnostics);
        Assert.Equal(new PefTransitionVector(-1, 0x40, -1, 0x800), pef.GetTransitionVector(pef.Loader!.Main, diagnostics));
    }

    [Fact]
    public void An_import_fixup_is_not_a_section()
    {
        var pef = Read([0x800F, 0x6000, 0x4200], out var diagnostics);   // DELTA 16; SYMB 0; DATA
        Assert.Equal(new PefTransitionVector(-1, 0x40, 1, 0x800), pef.GetTransitionVector(pef.Loader!.Main, diagnostics));
    }

    [Fact]
    public void A_vector_with_no_entry_point_or_outside_a_section_is_null()
    {
        var pef = Read([], out var diagnostics);
        Assert.Null(pef.GetTransitionVector(pef.Loader!.Term, diagnostics));
        Assert.Null(pef.GetTransitionVector(9, 0, diagnostics));
        Assert.Null(pef.GetTransitionVector(-1, 0, diagnostics));
        Assert.Null(pef.GetTransitionVector(pef.Loader.SectionIndex, 0, diagnostics));   // the loader is not in memory
        Assert.Null(pef.GetTransitionVector(1, 0x1000 - 4, diagnostics));
        Assert.Null(pef.GetTransitionVector(1, 0x2000, diagnostics));
        Assert.NotNull(pef.GetTransitionVector(1, 0x1000 - 8, diagnostics));
    }
}
