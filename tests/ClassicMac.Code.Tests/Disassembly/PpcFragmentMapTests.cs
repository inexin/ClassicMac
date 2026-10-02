using ClassicMac.Code.Disassembly;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests.Disassembly;

// A hand-built fragment: main calls an import through glue, a local function through bl, and loads a TOC slot.
public class PpcFragmentMapTests
{
    // Section 0 (code):
    //   00 mflr r0; 04 bl $18 (glue); 08 lwz r2,20(r1); 0C bl $30; 10 lwz r3,4(r2); 14 blr
    //   18 glue: lwz r12,0(r2); stw r2,20(r1); lwz r0,0(r12); lwz r2,4(r12); mtctr r0; bctr
    //   30 blr, then a traceback table naming ".Helper" (tb_offset 4), padded to $50
    // Section 1 (data): 00 tvector (code 0, TOC 8) = main; 08 TOC[0] = InterfaceLib::InitGraf; 0C TOC[4] = 1:$10;
    //   10 a word; 14 tvector (code $30, TOC 8) = export Helper
    internal static byte[] Fragment()
    {
        var code = PefBuilder.Words(
            0x7C0802A6, 0x48000015, 0x80410014, 0x48000025, 0x80620004, 0x4E800020,
            0x81820000, 0x90410014, 0x800C0000, 0x804C0004, 0x7C0903A6, 0x4E800420,
            0x4E800020, 0x00000000, 0x00002040, 0x00000000, 0x00000004, 0x00072E48, 0x656C7065, 0x72000000);
        var data = PefBuilder.Words(0x00000000, 0x00000008, 0x00000000, 0x00000010, 0x12345678, 0x00000030, 0x00000008);
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, code);
        b.AddSection(PefSectionKind.UnpackedData, data);
        b.Libraries.Add(new PefBuilder.Library("InterfaceLib", [new PefBuilder.Import("InitGraf")]));
        b.Exports.Add(new PefBuilder.Export("Helper", 0x00060D48, PefSymbolClass.TVector, 0x14, 1));
        // DSC2 1; SYMR 1; DATA 1; DELTA 4; DSC2 1
        b.Relocations.Add((1, [0x4600, 0x4A00, 0x4200, 0x8003, 0x4600]));
        b.Main = (1, 0);
        return b.Build();
    }

    internal static PefContainer Read(byte[] bytes)
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(bytes, diagnostics);
        Assert.Empty(diagnostics);
        return pef;
    }

    private static PpcFragmentMap Map() => PpcFragmentMap.Build(Read(Fragment()), new List<Diagnostic>());

    [Fact]
    public void Toc_base_from_the_transition_vectors()
    {
        var map = Map();
        Assert.Equal(1, map.TocSection);
        Assert.Equal(8u, map.TocBase);
        Assert.Equal([0], map.CodeSections);
    }

    [Fact]
    public void Functions_from_main_exports_tracebacks_and_glue()
    {
        var map = Map();
        Assert.Equal(
        [
            new CodeFunction(0, 0, "main", CodeFunctionSource.Main),
            new CodeFunction(0, 0x18, ".InitGraf", CodeFunctionSource.Glue),
            new CodeFunction(0, 0x30, "Helper", CodeFunctionSource.Export),
        ], map.Functions.Values);
    }

    [Fact]
    public void Glue_stubs_name_their_import()
    {
        var map = Map();
        Assert.Equal("InterfaceLib::InitGraf", map.Glue[(0, 0x18)]);
        Assert.Single(map.Glue);
    }

    [Fact]
    public void Transition_vectors_in_the_data()
    {
        var map = Map();
        Assert.Equal(
        [
            new PpcTransitionVectorEntry(1, 0, 0, 0, 1, 8, "main"),
            new PpcTransitionVectorEntry(1, 0x14, 0, 0x30, 1, 8, "Helper"),
        ], map.TransitionVectors);
    }

    [Fact]
    public void Traceback_tables_are_found_per_code_section()
    {
        var table = Assert.Single(Map().Tracebacks[0]);
        Assert.Equal((".Helper", 0x34, 0x30), (table.Name, table.Offset, table.FunctionStart));
    }

    private static List<(CodeReferenceKind, string)> At(uint offset)
    {
        var map = Map();
        var image = map.Pef.GetImage(0, new List<Diagnostic>());
        uint word = new BigEndianReader(image).ReadUInt32At((int)offset);
        return PpcAnnotator.Annotate(PpcDisassembler.Decode(word, offset), 0, map).Select(r => (r.Kind, r.Text)).ToList();
    }

    [Fact]
    public void Bl_to_glue_names_the_import() =>
        Assert.Equal([(CodeReferenceKind.Glue, "InterfaceLib::InitGraf")], At(4));

    [Fact]
    public void Bl_to_a_local_function_names_it() =>
        Assert.Equal([(CodeReferenceKind.Call, "Helper")], At(0x0C));

    [Fact]
    public void Toc_loads_name_the_slot()
    {
        Assert.Equal([(CodeReferenceKind.TocSlot, "1:0x10")], At(0x10));
        Assert.Equal([(CodeReferenceKind.TocSlot, "InterfaceLib::InitGraf")], At(0x18));
    }

    [Fact]
    public void Other_instructions_have_no_annotation()
    {
        Assert.Empty(At(0));
        Assert.Empty(At(8));   // lwz r2,20(r1): not from the TOC
    }

    [Fact]
    public void Section_pointers_name_a_function()
    {
        // TOC[4] points into the code at a labelled function: the slot names it.
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, PefBuilder.Words(0x80620000, 0x4E800020));
        b.AddSection(PefSectionKind.UnpackedData, PefBuilder.Words(0x00000000, 0x00000008, 0x00000000));
        b.Relocations.Add((1, [0x4600, 0x4000]));   // DSC2 1; CODE 1
        b.Main = (1, 0);
        var map = PpcFragmentMap.Build(Read(b.Build()), new List<Diagnostic>());
        var ins = PpcDisassembler.Decode(0x80620000, 0);
        Assert.Equal([(CodeReferenceKind.TocSlot, "0:0x0 main")], PpcAnnotator.Annotate(ins, 0, map).Select(r => (r.Kind, r.Text)));
    }

    [Fact]
    public void Glue_without_an_import_fixup_is_unnamed()
    {
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, PefBuilder.Words(0x81820004, 0x90410014, 0x800C0000, 0x804C0004, 0x7C0903A6, 0x4E800420));
        b.AddSection(PefSectionKind.UnpackedData, PefBuilder.Words(0x00000000, 0x00000008, 0, 0));
        b.Relocations.Add((1, [0x4600]));
        b.Main = (1, 0);
        var map = PpcFragmentMap.Build(Read(b.Build()), new List<Diagnostic>());
        Assert.Equal("?slot 1:0xC", map.Glue[(0, 0)]);
    }

    // A CodeWarrior-style fragment: the TOC base is past its slots, so they are reached at negative displacements.
    // Section 0 (code):
    //   00 bl $10 (glue); 04 lwz r3,-4(r2); 08 beq $28; 0C b $28 (a tail call)
    //   10 glue: lwz r12,-8(r2) ...; 28 mflr r0; 2C blr; 30 a traceback table naming ".Traced" (tb_offset 8)
    // Section 1 (data): 00 tvector (code 0, TOC $10) = main; 08 TOC[-8] = InterfaceLib::InitGraf; 0C TOC[-4] = 1:$14
    private static PpcFragmentMap CentredToc()
    {
        var code = PefBuilder.Words(
            0x48000011, 0x8062FFFC, 0x41820020, 0x4800001C,
            0x8182FFF8, 0x90410014, 0x800C0000, 0x804C0004, 0x7C0903A6, 0x4E800420,
            0x7C0802A6, 0x4E800020,
            0x00000000, 0x00002040, 0x00000000, 0x00000008, 0x00072E54, 0x72616365, 0x64000000);
        var data = PefBuilder.Words(0x00000000, 0x00000010, 0x00000000, 0x00000014, 0x00000000, 0x12345678);
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, code);
        b.AddSection(PefSectionKind.UnpackedData, data);
        b.Libraries.Add(new PefBuilder.Library("InterfaceLib", [new PefBuilder.Import("InitGraf")]));
        b.Relocations.Add((1, [0x4600, 0x4A00, 0x4200]));   // DSC2 1; SYMR 1; DATA 1
        b.Main = (1, 0);
        return PpcFragmentMap.Build(Read(b.Build()), new List<Diagnostic>());
    }

    private static List<(CodeReferenceKind, string)> CentredAt(PpcFragmentMap map, uint word, uint offset) =>
        PpcAnnotator.Annotate(PpcDisassembler.Decode(word, offset), 0, map).Select(r => (r.Kind, r.Text)).ToList();

    [Fact]
    public void A_centred_TOC_reaches_its_slots_at_negative_displacements()
    {
        var map = CentredToc();
        Assert.Equal(0x10u, map.TocBase);
        Assert.Equal("InterfaceLib::InitGraf", Assert.Single(map.Glue, g => g.Key == (0, 0x10)).Value);
        Assert.Equal([(CodeReferenceKind.Glue, "InterfaceLib::InitGraf")], CentredAt(map, 0x48000011, 0));
        Assert.Equal([(CodeReferenceKind.TocSlot, "1:0x14")], CentredAt(map, 0x8062FFFC, 4));
        Assert.Equal([(CodeReferenceKind.TocSlot, "InterfaceLib::InitGraf")], CentredAt(map, 0x8182FFF8, 0x10));
    }

    [Fact]
    public void A_function_named_only_by_its_traceback()
    {
        Assert.Equal(
        [
            new CodeFunction(0, 0, "main", CodeFunctionSource.Main),
            new CodeFunction(0, 0x10, ".InitGraf", CodeFunctionSource.Glue),
            new CodeFunction(0, 0x28, ".Traced", CodeFunctionSource.Traceback),
        ], CentredToc().Functions.Values);
    }

    // A conditional branch to a function is not a call and is not annotated; an unconditional b (a tail call) is.
    [Fact]
    public void Only_unconditional_branches_and_calls_name_their_target()
    {
        var map = CentredToc();
        Assert.Empty(CentredAt(map, 0x41820020, 8));
        Assert.Equal([(CodeReferenceKind.Call, ".Traced")], CentredAt(map, 0x4800001C, 0x0C));
        Assert.Equal([(CodeReferenceKind.Call, ".Traced")], CentredAt(map, 0x41820021, 8));   // beql
    }

    [Fact]
    public void A_fragment_without_transition_vectors_has_no_TOC()
    {
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, PefBuilder.Words(0x80620000, 0x4E800020));
        var map = PpcFragmentMap.Build(Read(b.Build()), new List<Diagnostic>());
        Assert.Null(map.TocSection);
        Assert.Empty(PpcAnnotator.Annotate(PpcDisassembler.Decode(0x80620000, 0), 0, map));
        Assert.Equal([new CodeFunction(0, 0, "sub_0000", CodeFunctionSource.Entry)], map.Functions.Values);
    }
}
