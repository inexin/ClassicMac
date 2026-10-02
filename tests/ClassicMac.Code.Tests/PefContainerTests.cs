using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// The PEF container header and section headers (Mac OS Runtime Architectures ch. 8).
public class PefContainerTests
{
    private static (PefContainer Pef, List<Diagnostic> Diagnostics) Read(byte[] data)
    {
        var diagnostics = new List<Diagnostic>();
        return (PefContainer.Read(data, diagnostics), diagnostics);
    }

    private static PefBuilder CodeAndData() => new PefBuilder()
        .AddSection(PefSectionKind.Code, PefBuilder.Words(0x7C0802A6, 0x4E800020))
        .AddSection(PefSectionKind.UnpackedData, PefBuilder.Words(1, 2), total: 16);

    [Fact]
    public void Reads_the_header()
    {
        var (pef, diagnostics) = Read(CodeAndData().Build());
        Assert.Empty(diagnostics);
        Assert.Equal(FourCC.FromString("pwpc"), pef.Architecture);
        Assert.Equal(1u, pef.FormatVersion);
        Assert.Equal(0xB5000000u, pef.DateTimeStamp);
        Assert.Equal(0x01000000u, pef.OldDefVersion);
        Assert.Equal(0x01008000u, pef.OldImpVersion);
        Assert.Equal(0x01108000u, pef.CurrentVersion);
        Assert.Equal(2, pef.InstantiatedSectionCount);
        Assert.Equal(3, pef.Sections.Count);
        Assert.NotNull(pef.Loader);
        Assert.Equal(2, pef.Loader.SectionIndex);
    }

    [Fact]
    public void Reads_the_section_headers()
    {
        var builder = CodeAndData();
        builder.Sections[1] = builder.Sections[1] with { Share = PefShareKind.Global, Alignment = 3 };
        var (pef, _) = Read(builder.Build());
        var code = pef.Sections[0];
        Assert.Equal(0, code.Index);
        Assert.Null(code.Name);
        Assert.Equal(0u, code.DefaultAddress);
        Assert.Equal(PefSectionKind.Code, code.Kind);
        Assert.Equal(8u, code.TotalLength);
        Assert.Equal(8u, code.UnpackedLength);
        Assert.Equal(8u, code.ContainerLength);
        Assert.Equal(0, (int)code.ContainerOffset % 16);
        var data = pef.Sections[1];
        Assert.Equal(16u, data.TotalLength);
        Assert.Equal(PefShareKind.Global, data.ShareKind);
        Assert.Equal(3, data.Alignment);
        Assert.Equal(PefSectionKind.Loader, pef.Sections[2].Kind);
    }

    [Theory]
    [InlineData(PefSectionKind.Code, true)]
    [InlineData(PefSectionKind.UnpackedData, true)]
    [InlineData(PefSectionKind.PatternInitData, true)]
    [InlineData(PefSectionKind.Constant, true)]
    [InlineData(PefSectionKind.ExecutableData, true)]
    [InlineData(PefSectionKind.Loader, false)]
    [InlineData(PefSectionKind.Debug, false)]
    [InlineData(PefSectionKind.Exception, false)]
    [InlineData(PefSectionKind.Traceback, false)]
    public void Only_code_and_data_kinds_are_instantiable(PefSectionKind kind, bool instantiable) =>
        Assert.Equal(instantiable, new PefSection(0, null, 0, 0, 0, 0, 0, kind, PefShareKind.Process, 0).IsInstantiable);

    [Fact]
    public void Section_names_come_from_the_name_table()
    {
        var builder = new PefBuilder().AddSection(PefSectionKind.Code, [0, 0, 0, 0], name: "text")
            .AddSection(PefSectionKind.UnpackedData, [0, 0, 0, 0], name: "data");
        var (pef, diagnostics) = Read(builder.Build());
        Assert.Empty(diagnostics);
        Assert.Equal("text", pef.Sections[0].Name);
        Assert.Equal("data", pef.Sections[1].Name);
    }

    [Theory]
    [InlineData(0x7FFF0000)]
    [InlineData(-2)]
    public void A_section_name_outside_the_container_is_reported(int nameOffset)
    {
        var data = new PefBuilder { WithLoader = false }.AddSection(PefSectionKind.Code, [0, 0, 0, 0]).Build();
        new BigEndianWriter(data).WriteInt32At(40, nameOffset);
        var (pef, diagnostics) = Read(data);
        Assert.Null(pef.Sections[0].Name);
        Assert.Equal("pef.section-name-out-of-range", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_section_name_without_a_NUL_is_reported()
    {
        // Cut the container just before the name's NUL (the header, one section header, "abc"); the empty section
        // moves to offset 0 so only the name is damaged.
        var data = new PefBuilder { WithLoader = false }.AddSection(PefSectionKind.Code, [], name: "abc").Build()[..(40 + 28 + 3)];
        new BigEndianWriter(data).WriteUInt32At(40 + 20, 0u);
        var (pef, diagnostics) = Read(data);
        Assert.Equal("abc", pef.Sections[0].Name);
        Assert.Equal("pef.string-unterminated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_header_shorter_than_40_bytes_throws() =>
        Assert.Throws<InvalidDataException>(() => PefContainer.Read(CodeAndData().Build()[..39], []));

    [Fact]
    public void A_missing_tag_throws()
    {
        var data = CodeAndData().Build();
        data[3] = (byte)'?';
        Assert.Throws<InvalidDataException>(() => PefContainer.Read(data, []));
    }

    [Fact]
    public void IsPef_checks_the_tag()
    {
        Assert.True(PefContainer.IsPef("Joy!peffpwpc"u8));
        Assert.False(PefContainer.IsPef("Joy!pef"u8));
        Assert.False(PefContainer.IsPef("Joy?peff"u8));
    }

    [Fact]
    public void Another_format_version_is_a_warning()
    {
        var (pef, diagnostics) = Read(new PefBuilder { FormatVersion = 2 }.Build());
        Assert.Equal(2u, pef.FormatVersion);
        var d = Assert.Single(diagnostics);
        Assert.Equal(("pef.format-version", DiagnosticSeverity.Warning), (d.Code, d.Severity));
    }

    [Fact]
    public void Section_headers_past_the_end_are_reported()
    {
        var data = CodeAndData().Build()[..(40 + 28 + 10)];
        var (pef, diagnostics) = Read(data);
        Assert.Single(pef.Sections);
        Assert.Null(pef.Loader);
        Assert.Contains(diagnostics, d => d.Code == "pef.sections-truncated");
    }

    [Fact]
    public void Section_contents_past_the_end_are_reported_and_clipped()
    {
        var data = CodeAndData().Build();
        new BigEndianWriter(data).WriteUInt32At(40 + 16, 0x10000u); // code containerLength
        var (pef, diagnostics) = Read(data);
        Assert.Equal("pef.section-out-of-range", Assert.Single(diagnostics).Code);
        Assert.Equal(data.Length - (int)pef.Sections[0].ContainerOffset, pef.GetContents(0).Length);
    }

    [Fact]
    public void A_section_starting_past_the_end_has_no_contents()
    {
        var data = CodeAndData().Build();
        new BigEndianWriter(data).WriteUInt32At(40 + 20, 0x10000u); // code containerOffset
        var (pef, diagnostics) = Read(data);
        Assert.Equal("pef.section-out-of-range", Assert.Single(diagnostics).Code);
        Assert.Equal(0, pef.GetContents(0).Length);
    }

    [Fact]
    public void Images_are_zero_filled_to_the_total_length()
    {
        var (pef, _) = Read(CodeAndData().Build());
        var diagnostics = new List<Diagnostic>();
        Assert.Equal(PefBuilder.Words(1, 2, 0, 0), pef.GetImage(1, diagnostics).ToArray());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Images_copy_only_the_unpacked_length()
    {
        var builder = new PefBuilder().AddSection(PefSectionKind.UnpackedData, [1, 2, 3, 4], unpacked: 2, total: 4);
        var (pef, _) = Read(builder.Build());
        Assert.Equal([1, 2, 0, 0], pef.GetImage(0, []).ToArray());
    }

    [Fact]
    public void Images_are_cached()
    {
        var (pef, _) = Read(CodeAndData().Build());
        Assert.True(pef.GetImage(0, []).Equals(pef.GetImage(0, [])));
    }

    [Fact]
    public void Pattern_data_sections_are_unpacked()
    {
        var builder = new PefBuilder().AddSection(PefSectionKind.PatternInitData, [0x22, 7, 8, 0x02], unpacked: 4, total: 6);
        var (pef, diagnostics) = Read(builder.Build());
        var imageDiagnostics = new List<Diagnostic>();
        Assert.Equal([7, 8, 0, 0, 0, 0], pef.GetImage(0, imageDiagnostics).ToArray());
        Assert.Empty(diagnostics);
        Assert.Empty(imageDiagnostics);
    }

    [Fact]
    public void Pattern_data_of_the_wrong_length_is_reported()
    {
        var builder = new PefBuilder().AddSection(PefSectionKind.PatternInitData, [0x03], unpacked: 4, total: 4);
        var (pef, _) = Read(builder.Build());
        var diagnostics = new List<Diagnostic>();
        Assert.Equal(4, pef.GetImage(0, diagnostics).Length);
        Assert.Equal("pef.pidata-length", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_huge_total_length_is_capped_rather_than_allocated()
    {
        var builder = new PefBuilder().AddSection(PefSectionKind.UnpackedData, [1], unpacked: 1, total: 0xFFFFFFF0);
        var (pef, _) = Read(builder.Build());
        Assert.Equal(1 + (1 << 24), pef.GetImage(0, []).Length);
    }

    [Fact]
    public void A_container_without_a_loader_has_no_loader_and_no_fixups()
    {
        var (pef, diagnostics) = Read(new PefBuilder { WithLoader = false }.AddSection(PefSectionKind.Code, [0, 0, 0, 0]).Build());
        Assert.Empty(diagnostics);
        Assert.Null(pef.Loader);
        Assert.Empty(pef.GetFixups([]));
        Assert.Empty(pef.Instantiate([0], _ => 0, []).Fixups);
    }

    [Fact]
    public void Data_is_the_container()
    {
        var data = CodeAndData().Build();
        Assert.Equal(data, Read(data).Pef.Data.ToArray());
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var data = CodeAndData().Build();
        var pef = Read(data).Pef;
        Assert.Throws<ArgumentNullException>(() => PefContainer.Read(data, null!));
        Assert.Throws<ArgumentNullException>(() => pef.GetImage(0, null!));
        Assert.Throws<ArgumentNullException>(() => pef.GetFixups(null!));
        Assert.Throws<ArgumentNullException>(() => pef.Instantiate(null!, _ => 0, []));
        Assert.Throws<ArgumentNullException>(() => pef.Instantiate([], null!, []));
        Assert.Throws<ArgumentNullException>(() => pef.Instantiate([], _ => 0, null!));
        Assert.Throws<ArgumentNullException>(() => pef.GetTransitionVector(0, 0, null!));
    }

    [Fact]
    public void PefEntryPoint_prints_as_section_and_offset() => Assert.Equal("1:0x1470", new PefEntryPoint(1, 0x1470).ToString());
}
