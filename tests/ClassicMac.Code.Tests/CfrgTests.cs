using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// 'cfrg' version 1 (CodeFragments.h CFragResource): a 32-byte header, then members of memberSize bytes each.
public class CfrgTests
{
    private static byte[] Header(int memberCount, ushort version = 1)
    {
        var w = new BigEndianWriter();
        w.WriteZeros(10);
        w.WriteUInt16(version);
        w.WriteZeros(18);
        w.WriteUInt16(memberCount);
        return w.ToArray();
    }

    // One member: the 42 fixed bytes, the name, then (with extensions) padding to 4 and the extensions; padded to 4.
    private static byte[] Member(string name, CfrgUsage usage = CfrgUsage.ImportLibrary, CfrgWhere where = CfrgWhere.DataFork,
        uint offset = 0, uint length = 0, uint where1 = 0, ushort where2 = 0, byte[][]? extensions = null, int? memberSize = null,
        int? extensionCount = null)
    {
        var w = new BigEndianWriter();
        w.WriteFourCC(FourCC.FromString("pwpc"));
        w.WriteUInt16(0);
        w.WriteByte(0);
        w.WriteByte(1);              // updateLevel
        w.WriteUInt32(0x01108000);   // currentVersion
        w.WriteUInt32(0x01000000);   // oldDefVersion
        w.WriteUInt32(0x10000);      // usage1 (stack size)
        w.WriteUInt16(7);            // usage2
        w.WriteByte((byte)usage);
        w.WriteByte((byte)where);
        w.WriteUInt32(offset);
        w.WriteUInt32(length);
        w.WriteUInt32(where1);
        w.WriteUInt16(where2);
        w.WriteUInt16(extensionCount ?? extensions?.Length ?? 0);
        int sizeAt = w.Length;
        w.WriteUInt16(0);
        w.WriteByte(name.Length);
        w.WriteBytes(MacRoman.Encode(name));
        if (extensions is { Length: > 0 })
        {
            while (w.Length % 4 != 0) w.WriteByte(0);
            foreach (var e in extensions) w.WriteBytes(e);
        }
        while (w.Length % 4 != 0) w.WriteByte(0);
        w.WriteUInt16At(sizeAt, memberSize ?? w.Length);
        return w.ToArray();
    }

    private static byte[] Extension(ushort kind, byte[] data, int? size = null)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(kind);
        w.WriteUInt16(size ?? data.Length + 4);
        w.WriteBytes(data);
        return w.ToArray();
    }

    private static byte[] SearchData(string kind, params string[] qualifiers)
    {
        var w = new BigEndianWriter();
        w.WriteFourCC(FourCC.FromString(kind));
        foreach (var q in qualifiers)
        {
            w.WriteByte(q.Length);
            w.WriteBytes(MacRoman.Encode(q));
        }
        return w.ToArray();
    }

    private static (Cfrg Cfrg, List<Diagnostic> Diagnostics) Read(params byte[][] parts)
    {
        var diagnostics = new List<Diagnostic>();
        return (Cfrg.Read(parts.SelectMany(p => p).ToArray(), diagnostics), diagnostics);
    }

    [Fact]
    public void Reads_every_member_field()
    {
        var (cfrg, diagnostics) = Read(Header(1), Member("DiskCopy.PPC", CfrgUsage.Application, CfrgWhere.DataFork, 0x200, 0x1000, 3, 4));
        Assert.Empty(diagnostics);
        Assert.Equal(1, cfrg.Version);
        var m = Assert.Single(cfrg.Members);
        Assert.Equal(FourCC.FromString("pwpc"), m.Architecture);
        Assert.Equal(1, m.UpdateLevel);
        Assert.Equal(0x01108000u, m.CurrentVersion);
        Assert.Equal(0x01000000u, m.OldDefVersion);
        Assert.Equal(0x10000u, m.Usage1);
        Assert.Equal(7, m.Usage2);
        Assert.Equal(CfrgUsage.Application, m.Usage);
        Assert.Equal(CfrgWhere.DataFork, m.Where);
        Assert.Equal(0x200u, m.Offset);
        Assert.Equal(0x1000u, m.Length);
        Assert.Equal(3u, m.Where1);
        Assert.Equal(4, m.Where2);
        Assert.Equal("DiskCopy.PPC", m.Name);
        Assert.Empty(m.Extensions);
        Assert.Null(m.Search);
        Assert.Equal(32, m.Position);
    }

    [Theory]
    [InlineData("", 44)]        // 43 → 44
    [InlineData("A", 44)]       // 44
    [InlineData("AB", 48)]      // 45 → 48
    [InlineData("ABCDE", 48)]   // 48
    [InlineData("ABCDEF", 52)]  // 49 → 52
    public void Members_without_extensions_are_43_plus_the_name_padded_to_4(string name, int size)
    {
        var (cfrg, diagnostics) = Read(Header(2), Member(name), Member("Next"));
        Assert.Empty(diagnostics);
        Assert.Equal(size, cfrg.Members[0].MemberSize);
        Assert.Equal(name, cfrg.Members[0].Name);
        Assert.Equal("Next", cfrg.Members[1].Name);
        Assert.Equal(32 + size, cfrg.Members[1].Position);
    }

    [Fact]
    public void Extensions_start_after_the_name_padded_to_4_and_the_search_extension_reads()
    {
        var search = Extension(CfrgExtension.SearchKind, SearchData("ndrv", "q1", "", "q3", "q4"));
        var other = Extension(0x1234, [9, 8, 7, 6]);
        var (cfrg, diagnostics) = Read(Header(2), Member("drv", extensions: [search, other]), Member("Next"));
        Assert.Empty(diagnostics);
        var m = cfrg.Members[0];
        Assert.Equal(2, m.Extensions.Count);
        Assert.Equal(CfrgExtension.SearchKind, m.Extensions[0].Kind);
        Assert.Equal(0x1234, m.Extensions[1].Kind);
        Assert.Equal([9, 8, 7, 6], m.Extensions[1].Data.ToArray());
        Assert.Equal(FourCC.FromString("ndrv"), m.Search!.LibraryKind);
        Assert.Equal(["q1", "", "q3", "q4"], m.Search.Qualifiers);
        Assert.Equal("Next", cfrg.Members[1].Name);
    }

    [Fact]
    public void A_search_extension_too_short_for_its_strings_has_no_search()
    {
        var search = Extension(CfrgExtension.SearchKind, SearchData("otan", "only one"));
        var (cfrg, diagnostics) = Read(Header(1), Member("x", extensions: [search]));
        Assert.Empty(diagnostics);
        Assert.Single(cfrg.Members[0].Extensions);
        Assert.Null(cfrg.Members[0].Search);
    }

    [Fact]
    public void A_search_extension_without_its_kind_has_no_search()
    {
        var (cfrg, _) = Read(Header(1), Member("x", extensions: [Extension(CfrgExtension.SearchKind, [1, 2])]));
        Assert.Null(cfrg.Members[0].Search);
    }

    [Fact]
    public void Unknown_usage_and_where_values_are_kept() =>
        Assert.Equal((CfrgWhere)9, Read(Header(1), Member("x", (CfrgUsage)7, (CfrgWhere)9)).Cfrg.Members[0].Where);

    [Fact]
    public void A_header_shorter_than_32_bytes_throws() =>
        Assert.Throws<InvalidDataException>(() => Cfrg.Read(new byte[31], []));

    [Fact]
    public void A_version_other_than_1_throws() =>
        Assert.Throws<InvalidDataException>(() => Cfrg.Read(Header(0, version: 2), []));

    [Fact]
    public void More_members_than_the_resource_holds_are_reported()
    {
        var (cfrg, diagnostics) = Read(Header(3), Member("A"), Member("B"));
        Assert.Equal(2, cfrg.Members.Count);
        Assert.Equal("cfrg.member-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_member_size_too_small_for_its_name_is_reported()
    {
        var (cfrg, diagnostics) = Read(Header(2), Member("Name", memberSize: 44), Member("B"));
        Assert.Empty(cfrg.Members);
        Assert.Equal("cfrg.member-size", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_member_size_past_the_resource_is_reported()
    {
        var (cfrg, diagnostics) = Read(Header(2), Member("A"), Member("B", memberSize: 400));
        Assert.Single(cfrg.Members);
        Assert.Equal("cfrg.member-size", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_extension_count_with_no_room_is_reported()
    {
        var (cfrg, diagnostics) = Read(Header(1), Member("x", extensionCount: 1));
        Assert.Empty(cfrg.Members[0].Extensions);
        Assert.Equal("cfrg.extension-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_extension_size_below_its_header_is_reported()
    {
        var (cfrg, diagnostics) = Read(Header(1), Member("x", extensions: [Extension(0x1111, [0, 0, 0, 0], size: 2)]));
        Assert.Empty(cfrg.Members[0].Extensions);
        Assert.Equal("cfrg.extension-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_extension_size_past_the_member_is_reported()
    {
        var (cfrg, diagnostics) = Read(Header(1), Member("x", extensions: [Extension(0x1111, [0, 0, 0, 0], size: 64)]));
        Assert.Empty(cfrg.Members[0].Extensions);
        Assert.Equal("cfrg.extension-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Extensions_read_before_a_damaged_one_are_kept()
    {
        var good = Extension(0x2222, [1, 2, 3, 4]);
        var (cfrg, diagnostics) = Read(Header(1), Member("x", extensions: [good], extensionCount: 2));
        Assert.Equal(0x2222, Assert.Single(cfrg.Members[0].Extensions).Kind);
        Assert.Equal("cfrg.extension-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Diagnostics_are_required() => Assert.Throws<ArgumentNullException>(() => Cfrg.Read(Header(0), null!));
}
