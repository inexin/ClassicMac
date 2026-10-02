using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using static ClassicMac.Code.Ppc.PefFixupTarget;
using static ClassicMac.Code.Ppc.PefRelocationOpcode;

namespace ClassicMac.Code.Tests;

// The relocation machine (Mac OS Runtime Architectures ch. 8), one opcode at a time. Each program relocates data
// section 1, whose word at offset o holds o before relocation; sections sit at 0x10000000, 0x20000000 and 0x30000000
// and import i at 0x40000000 + 0x100·i.
public class PefRelocatorTests
{
    private const uint C0 = 0x10000000, D1 = 0x20000000, S2 = 0x30000000, Imports = 0x40000000;
    private const int DataLength = 0x100;

    private static PefBuilder Builder(params ushort[] program)
    {
        var data = new BigEndianWriter();
        for (uint o = 0; o < DataLength; o += 4) data.WriteUInt32(o);
        var b = new PefBuilder();
        b.AddSection(PefSectionKind.Code, new byte[0x40]);
        b.AddSection(PefSectionKind.UnpackedData, data.ToArray());
        b.AddSection(PefSectionKind.UnpackedData, new byte[0x40]);
        b.Libraries.Add(new PefBuilder.Library("Lib", [new("i0"), new("i1"), new("i2")]));
        b.Relocations.Add((1, program));
        return b;
    }

    private static (PefInstance Instance, List<Diagnostic> Diagnostics, PefContainer Pef) Run(params ushort[] program)
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(Builder(program).Build(), diagnostics);
        Assert.Empty(diagnostics);
        var instance = pef.Instantiate([C0, D1, S2, 0], i => Imports + 0x100u * (uint)i, diagnostics);
        return (instance, diagnostics, pef);
    }

    private static uint Word(PefInstance instance, int offset) => new BigEndianReader(instance.Images[1]).ReadUInt32At(offset);

    // Runs a program and checks it makes exactly these fixups (offset, opcode, target, index) and no diagnostics.
    private static PefInstance Expect(ushort[] program, params (int Offset, PefRelocationOpcode Opcode, PefFixupTarget Target, int Index)[] fixups)
    {
        var (instance, diagnostics, _) = Run(program);
        Assert.Empty(diagnostics);
        Assert.Equal(fixups.Select(f => (f.Offset, f.Opcode, f.Target, f.Index)),
            instance.Fixups.Select(f => ((int)f.Offset, f.Opcode, f.Target, f.TargetIndex)));
        foreach (var f in instance.Fixups)
        {
            Assert.Equal(1, f.Section);
            uint amount = f.Target == Import ? Imports + 0x100u * (uint)f.TargetIndex : f.TargetIndex switch { 0 => C0, 1 => D1, _ => S2 };
            Assert.Equal(amount, f.Amount);
            Assert.Equal((uint)f.Offset + amount, Word(instance, (int)f.Offset));
        }
        return instance;
    }

    private static string Fails(ushort[] program, string code)
    {
        var (_, diagnostics, _) = Run(program);
        var d = Assert.Single(diagnostics, d => d.Code == code);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        return d.Message;
    }

    [Fact]
    public void DDAT_skips_words_then_adds_sectionD() =>
        Expect([0x0083], (8, BySectDWithSkip, Section, 1), (12, BySectDWithSkip, Section, 1), (16, BySectDWithSkip, Section, 1));

    [Fact]
    public void CODE_adds_sectionC_to_a_run() => Expect([0x4001], (0, BySectC, Section, 0), (4, BySectC, Section, 0));

    [Fact]
    public void DATA_adds_sectionD_to_a_run() => Expect([0x4200], (0, BySectD, Section, 1));

    [Fact]
    public void DESC_relocates_12_byte_transition_vectors() =>
        Expect([0x4401], (0, TVector12, Section, 0), (4, TVector12, Section, 1), (12, TVector12, Section, 0), (16, TVector12, Section, 1));

    [Fact]
    public void DSC2_relocates_8_byte_transition_vectors() =>
        Expect([0x4601], (0, TVector8, Section, 0), (4, TVector8, Section, 1), (8, TVector8, Section, 0), (12, TVector8, Section, 1));

    [Fact]
    public void VTBL_adds_sectionD_and_skips_a_word() => Expect([0x4801], (0, VTable8, Section, 1), (8, VTable8, Section, 1));

    [Fact]
    public void SYMR_adds_a_run_of_imports() =>
        Expect([0x4A02], (0, ImportRun, Import, 0), (4, ImportRun, Import, 1), (8, ImportRun, Import, 2));

    [Fact]
    public void SYMB_adds_one_import_and_sets_the_next() =>
        Expect([0x6001, 0x4A00], (0, ByImport, Import, 1), (4, ImportRun, Import, 2));

    [Fact]
    public void CDIS_sets_sectionC() => Expect([0x6202, 0x4000], (0, BySectC, Section, 2));

    [Fact]
    public void DTIS_sets_sectionD() => Expect([0x6402, 0x4200], (0, BySectD, Section, 2));

    [Fact]
    public void SECN_adds_a_section() => Expect([0x6602], (0, BySection, Section, 2));

    [Fact]
    public void DELTA_moves_on_bytes() => Expect([0x8007, 0x4200], (8, BySectD, Section, 1));

    [Fact]
    public void DELTA_can_leave_fixups_2_byte_aligned()
    {
        // The word at offset 2 straddles words 0 and 1 (bytes 00 00 | 00 00); words 0 and 1 keep their other halves.
        var (instance, diagnostics, _) = Run(0x8001, 0x4200);
        Assert.Empty(diagnostics);
        Assert.Equal(2, Assert.Single(instance.Fixups).Offset);
        Assert.Equal([0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x04], instance.Images[1][..8]);
    }

    [Fact]
    public void RPT_runs_the_instructions_before_it_again() =>
        // DATA; DELTA 4; RPT 2 instructions 3 more times.
        Expect([0x4200, 0x8003, 0x9102], (0, BySectD, Section, 1), (8, BySectD, Section, 1), (16, BySectD, Section, 1), (24, BySectD, Section, 1));

    [Fact]
    public void RPT_counts_instructions_not_words()
    {
        // LABS (two words) then DATA, both repeated once: the same word is relocated twice.
        var (instance, diagnostics, _) = Run(0xA000, 0x0010, 0x4200, 0x9100);
        Assert.Empty(diagnostics);
        Assert.Equal([0x10L, 0x10L], instance.Fixups.Select(f => f.Offset));
        Assert.Equal(0x10 + 2 * D1, Word(instance, 0x10));
    }

    [Fact]
    public void LABS_sets_the_position() => Expect([0xA000, 0x0020, 0x4200], (0x20, BySectD, Section, 1));

    [Fact]
    public void LSYM_adds_one_import_and_sets_the_next() =>
        Expect([0xA400, 0x0001, 0x4A00], (0, LgByImport, Import, 1), (4, ImportRun, Import, 2));

    [Fact]
    public void LRPT_runs_the_instructions_before_it_the_count_more_times() =>
        // DATA; DELTA 4; LRPT 2 instructions, 3 more times (not 3 + 1).
        Expect([0x4200, 0x8003, 0xB040, 0x0003], (0, BySectD, Section, 1), (8, BySectD, Section, 1), (16, BySectD, Section, 1), (24, BySectD, Section, 1));

    [Fact]
    public void LSEC_0_adds_a_section() => Expect([0xB400, 0x0002], (0, LgBySection, Section, 2));

    [Fact]
    public void LSEC_1_sets_sectionC() => Expect([0xB440, 0x0002, 0x4000], (0, BySectC, Section, 2));

    [Fact]
    public void LSEC_2_sets_sectionD() => Expect([0xB480, 0x0002, 0x4200], (0, BySectD, Section, 2));

    [Fact]
    public void Decodes_the_long_operands()
    {
        var decoded = PefRelocator.Decode([0xA3FF, 0xFFFF, 0xA7FF, 0xFFFF, 0xB3FF, 0xFFFF, 0xB43F, 0xFFFF], []);
        Assert.Equal(
        [
            new PefRelocationInstruction(SetPosition, 0, 0x3FFFFFF, 0),
            new PefRelocationInstruction(LgByImport, 2, 0x3FFFFFF, 0),
            new PefRelocationInstruction(LgRepeat, 4, 16, 0x3FFFFF),
            new PefRelocationInstruction(LgBySection, 6, 0x3FFFFF, 0),
        ], decoded);
    }

    [Fact]
    public void Decodes_the_short_operands()
    {
        var decoded = PefRelocator.Decode([0x3FFF, 0x41FF, 0x63FF, 0x8FFF, 0x9FFF], []);
        Assert.Equal(
        [
            new PefRelocationInstruction(BySectDWithSkip, 0, 0xFF, 0x3F),
            new PefRelocationInstruction(BySectC, 1, 0, 0x200),
            new PefRelocationInstruction(SetSectC, 2, 0x1FF, 0),
            new PefRelocationInstruction(IncrPosition, 3, 0, 0x1000),
            new PefRelocationInstruction(Repeat, 4, 16, 0x100),
        ], decoded);
    }

    [Theory]
    [InlineData((ushort)0x0000, "DDAT")]
    [InlineData((ushort)0x4000, "CODE")]
    [InlineData((ushort)0x4200, "DATA")]
    [InlineData((ushort)0x4400, "DESC")]
    [InlineData((ushort)0x4600, "DSC2")]
    [InlineData((ushort)0x4800, "VTBL")]
    [InlineData((ushort)0x4A00, "SYMR")]
    [InlineData((ushort)0x6000, "SYMB")]
    [InlineData((ushort)0x6200, "CDIS")]
    [InlineData((ushort)0x6400, "DTIS")]
    [InlineData((ushort)0x6600, "SECN")]
    [InlineData((ushort)0x8000, "DELTA")]
    [InlineData((ushort)0x9000, "RPT")]
    [InlineData((ushort)0xA000, "LABS")]
    [InlineData((ushort)0xA400, "LSYM")]
    [InlineData((ushort)0xB000, "LRPT")]
    [InlineData((ushort)0xB400, "LSEC")]
    [InlineData((ushort)0xB440, "LSEC")]
    [InlineData((ushort)0xB480, "LSEC")]
    public void Every_opcode_has_its_mnemonic(ushort word, string mnemonic) =>
        Assert.Equal(mnemonic, Assert.Single(PefRelocator.Decode([word, 0], []), i => i.Position == 0).Mnemonic);

    [Theory]
    [InlineData((ushort)0x4C00)] // 010 0110: small run sub-opcode 6
    [InlineData((ushort)0x5E00)] // 010 1111
    [InlineData((ushort)0x6800)] // 011 0100
    [InlineData((ushort)0x7000)]
    [InlineData((ushort)0x7FFF)]
    [InlineData((ushort)0xA800)]
    [InlineData((ushort)0xAC00)]
    [InlineData((ushort)0xB4C0)] // LSEC sub-opcode 3
    [InlineData((ushort)0xB800)]
    [InlineData((ushort)0xBC00)]
    [InlineData((ushort)0xC000)]
    [InlineData((ushort)0xFFFF)]
    public void Undefined_opcodes_are_errors_and_stop(ushort word)
    {
        var diagnostics = new List<Diagnostic>();
        var decoded = PefRelocator.Decode([0x4200, word, 0x0000, 0x4200], diagnostics);
        Assert.Single(decoded);
        Assert.Equal("pef.relocation-bad-opcode", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Fixups_before_a_bad_opcode_are_applied() =>
        Assert.Contains("0x7000", Fails([0x4200, 0x7000], "pef.relocation-bad-opcode"));

    [Fact]
    public void A_two_word_instruction_cut_short_is_an_error() => Fails([0x4200, 0xA000], "pef.relocation-truncated");

    [Fact]
    public void RPT_at_the_start_is_an_error() => Fails([0x9000], "pef.relocation-repeat-at-start");

    [Fact]
    public void LRPT_at_the_start_is_an_error() => Fails([0xB000, 0x0001], "pef.relocation-repeat-at-start");

    [Fact]
    public void RPT_of_more_instructions_than_precede_it_is_an_error() => Fails([0x4200, 0x9100], "pef.relocation-repeat-at-start");

    [Fact]
    public void RPT_of_a_repeat_is_an_error() => Fails([0x4200, 0x9000, 0x9100], "pef.relocation-nested-repeat");

    [Fact]
    public void A_word_outside_the_section_is_skipped_and_reported()
    {
        var (instance, diagnostics, _) = Run(0xA000, DataLength - 2, 0x4201);
        Assert.Empty(instance.Fixups);
        Assert.Contains("2 relocation", Assert.Single(diagnostics, d => d.Code == "pef.relocation-out-of-range").Message);
    }

    [Fact]
    public void A_word_ending_exactly_at_the_section_end_is_relocated()
    {
        var (instance, diagnostics, _) = Run(0xA000, DataLength - 4, 0x4200);
        Assert.Empty(diagnostics);
        Assert.Single(instance.Fixups);
    }

    [Fact]
    public void An_import_index_out_of_range_is_an_error() => Fails([0x6005, 0x4200], "pef.relocation-bad-import");

    [Fact]
    public void An_import_run_past_the_imports_is_an_error() => Fails([0x4A03], "pef.relocation-bad-import");

    [Theory]
    [InlineData(new ushort[] { 0x6209 })]
    [InlineData(new ushort[] { 0x6409 })]
    [InlineData(new ushort[] { 0x6609 })]
    [InlineData(new ushort[] { 0xB400, 0x0009 })]
    [InlineData(new ushort[] { 0xB440, 0x0009 })]
    [InlineData(new ushort[] { 0xB480, 0x0009 })]
    public void A_section_index_out_of_range_is_an_error(ushort[] program) => Fails(program, "pef.relocation-bad-section");

    [Fact]
    public void A_bad_section_index_leaves_the_section_unchanged()
    {
        var (instance, _, _) = Run(0x6209, 0x4000);
        Assert.Equal(0, Assert.Single(instance.Fixups).TargetIndex);
    }

    [Fact]
    public void A_header_for_a_missing_section_is_an_error()
    {
        var b = Builder(0x4200);
        b.Relocations[0] = (9, [0x4200]);
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(b.Build(), diagnostics);
        Assert.Empty(pef.GetFixups(diagnostics));
        Assert.Equal("pef.relocation-bad-section", Assert.Single(diagnostics).Code);
        Assert.Empty(pef.Instantiate([0, 0, 0, 0], _ => 0, diagnostics).Fixups);
    }

    [Fact]
    public void A_runaway_repeat_stops()
    {
        // LABS 0; DATA ×64 (the whole section); LRPT those 2 instructions 0x3FFFFF more times: far more fixups than words.
        Fails([0xA000, 0x0000, 0x423F, 0xB07F, 0xFFFF], "pef.relocation-runaway");
    }

    [Fact]
    public void A_runaway_out_of_the_section_stops()
    {
        // DATA ×512 repeated 0x3FFFFF times walks far past the section.
        Fails([0x43FF, 0xB03F, 0xFFFF], "pef.relocation-out-of-range");
    }

    [Fact]
    public void Listing_fixups_leaves_images_alone_and_adds_zero()
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(Builder(0x4601).Build(), diagnostics);
        var fixups = pef.GetFixups(diagnostics);
        Assert.Same(fixups, pef.GetFixups(diagnostics));
        Assert.Empty(diagnostics);
        Assert.Equal(4, fixups.Count);
        Assert.All(fixups, f => Assert.Equal(0u, f.Amount));
        Assert.Equal(4u, new BigEndianReader(pef.GetImage(1, diagnostics)).ReadUInt32At(4));
    }

    [Fact]
    public void Missing_section_addresses_count_as_zero()
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(Builder(0x6602).Build(), diagnostics);
        var instance = pef.Instantiate([C0], _ => 0, diagnostics);
        Assert.Equal(0u, Assert.Single(instance.Fixups).Amount);
    }

    [Fact]
    public void Run_checks_its_arguments()
    {
        var pef = Run(0x4200).Pef;
        var header = pef.Loader!.RelocationHeaders[0];
        Assert.Throws<ArgumentNullException>(() => PefRelocator.Run(null!, header, null, null, null, []));
        Assert.Throws<ArgumentNullException>(() => PefRelocator.Run(pef, null!, null, null, null, []));
        Assert.Throws<ArgumentNullException>(() => PefRelocator.Run(pef, header, null, null, null, null!));
        Assert.Throws<ArgumentNullException>(() => PefRelocator.Decode(null!, []));
        Assert.Throws<ArgumentNullException>(() => PefRelocator.Decode([], null!));
    }
}
