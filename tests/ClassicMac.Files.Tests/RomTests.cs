using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Compression;
using ClassicMac.Files.Rom;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

public class RomTests
{
    private sealed record Entry(byte[] Mask, string Type, short Id, string? Name, byte[] Data, byte Attributes = 0x58, int SizeCorrection = 0);

    // A ROM image laid out as docs/formats/ROM.md describes: version word at +8, table pointer at +$1A, table header,
    // then for each entry a 12-byte block header, the data (padded to 16) and the entry; the list is linked last first.
    private static byte[] BuildRom(IReadOnlyList<Entry> entries, int length = 64 * 1024, ushort version = 0x077D,
        int maxIndex = 4, int fieldSize = 8, ushort tableVersion = 1)
    {
        var rom = new BigEndianWriter(new byte[length]);
        rom.WriteUInt16At(8, version);
        const int table = 0x100;
        rom.WriteUInt32At(0x1A, table);
        rom.WriteByteAt(table + 4, (byte)maxIndex);
        rom.WriteByteAt(table + 5, (byte)fieldSize);
        rom.WriteUInt16At(table + 6, tableVersion);
        rom.WriteUInt16At(table + 8, 12);

        var at = 0x200;
        uint next = 0;
        foreach (var entry in entries)
        {
            rom.WriteByteAt(at, 0xC0);
            rom.WriteByteAt(at + 1, 0xA0);
            rom.WriteByteAt(at + 3, (byte)entry.SizeCorrection);
            rom.WriteUInt32At(at + 4, 12 + entry.Data.Length + entry.SizeCorrection);
            var data = at + 12;
            rom.WriteBytesAt(data, entry.Data);
            var e = (data + entry.Data.Length + 15) & ~15;
            rom.WriteBytesAt(e, entry.Mask);
            var f = e + entry.Mask.Length;
            rom.WriteUInt32At(f, next);
            rom.WriteUInt32At(f + 4, data);
            rom.WriteFourCCAt(f + 8, FourCC.FromString(entry.Type));
            rom.WriteInt16At(f + 12, entry.Id);
            rom.WriteByteAt(f + 14, entry.Attributes);
            var name = Encoding.ASCII.GetBytes(entry.Name ?? "");
            rom.WriteByteAt(f + 15, (byte)name.Length);
            rom.WriteBytesAt(f + 16, name);
            next = (uint)e;
            at = (f + 16 + name.Length + 15) & ~15;
        }
        rom.WriteUInt32At(table, next);
        return rom.ToArray();
    }

    private static readonly byte[] All = [0x78, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] Only4 = [0x08, 0, 0, 0, 0, 0, 0, 0];

    private static IReadOnlyList<Entry> Sample() =>
    [
        new(All, "CURS", 1, null, [1, 2, 3, 4, 5]),
        new(Only4, "PACK", 4, "Main", [0x4E, 0x75], SizeCorrection: 2),
        new(All, "STR ", -16396, "Name", Encoding.ASCII.GetBytes("\x05Hello")),
    ];

    private static (IReadOnlyList<MacFile> Files, List<Diagnostic> Diagnostics) Read(byte[] rom, string? host = null)
    {
        var input = ForkData.FromBytes(rom);
        Assert.True(MacRomReader.Instance.CanRead(input));
        var diagnostics = new List<Diagnostic>();
        var context = new ContainerContext(null, diagnostics, host is null ? null : MacString.FromMacRoman(host));
        return (MacRomReader.Instance.Read(input, context), diagnostics);
    }

    [Fact]
    public void Lists_every_entry_as_a_resource_of_one_file()
    {
        var (files, diagnostics) = Read(BuildRom(Sample()));
        var file = Assert.Single(files);
        Assert.Equal("ROM $077D", file.Name.ToMacRoman());
        Assert.Equal(0, file.DataFork.Length);
        var fork = ResourceFork.Read(file.ResourceFork.ToArray());
        Assert.Empty(fork.Diagnostics);
        Assert.Equal(3, fork.Resources.Count);

        // The list runs from the last entry written to the first.
        Assert.Equal(["STR ", "PACK", "CURS"], fork.Resources.Select(r => r.Type.ToString()));
        var pack = fork.Find(FourCC.FromString("PACK"), 4)!;
        Assert.Equal([0x4E, 0x75], pack.GetData().ToArray()); // physical size − 12 − size correction
        Assert.Equal("Main", pack.Name!.Value.ToMacRoman());
        Assert.Equal((ResourceAttributes)0x58, pack.Attributes);
        Assert.Null(fork.Find(FourCC.FromString("CURS"), 1)!.Name);
        Assert.Equal([1, 2, 3, 4, 5], fork.Find(FourCC.FromString("CURS"), 1)!.GetData().ToArray());

        var combos = Assert.Single(diagnostics);
        Assert.Equal("rom.combinations", combos.Code);
        Assert.Equal(DiagnosticSeverity.Info, combos.Severity);
        Assert.Contains("'PACK' 4 is only in combinations 4 (mask $0800000000000000)", combos.Message);
    }

    [Fact]
    public void Names_the_file_after_the_host_file()
    {
        var (files, _) = Read(BuildRom(Sample()), "Quadra ROM");
        Assert.Equal("Quadra ROM", Assert.Single(files).Name.ToMacRoman());
    }

    [Fact]
    public void Table_exposes_the_header_and_combination_masks()
    {
        var table = RomResourceTable.Read(BuildRom(Sample()))!;
        Assert.Equal(0x077D, table.RomVersion);
        Assert.Equal(4, table.MaxComboIndex);
        Assert.Equal(8, table.ComboFieldSize);
        Assert.Equal(1, table.ComboVersion);
        Assert.Equal(12, table.HeaderSize);
        var pack = table.Entries[1];
        Assert.Equal("$0800000000000000", pack.ComboMaskHex);
        Assert.Equal([false, false, false, false, true, false], Enumerable.Range(0, 6).Select(pack.IsInCombination));
        Assert.True(table.Entries[0].IsInCombination(1));
        Assert.False(table.Entries[0].IsInCombination(64));
    }

    [Fact]
    public void Keeps_the_first_of_a_type_and_ID_listed_again()
    {
        var (files, diagnostics) = Read(BuildRom(
        [
            new([0x60, 0, 0, 0, 0, 0, 0, 0], "ndrv", -20000, "old", [9]),
            new([0x18, 0, 0, 0, 0, 0, 0, 0], "ndrv", -20000, "new", [7, 7]),
        ]));
        var fork = ResourceFork.Read(Assert.Single(files).ResourceFork.ToArray());
        var kept = Assert.Single(fork.Resources);
        Assert.Equal("new", kept.Name!.Value.ToMacRoman()); // first in the list
        var duplicate = Assert.Single(diagnostics, d => d.Code == "rom.duplicate");
        Assert.Equal(DiagnosticSeverity.Warning, duplicate.Severity);
        Assert.Contains("combinations 1, 2", duplicate.Message);
        Assert.Contains("combinations 3, 4, is kept", duplicate.Message);
    }

    [Fact]
    public void Data_outside_the_image_is_reported_and_left_empty()
    {
        var rom = BuildRom(Sample());
        var table = RomResourceTable.Read(rom)!;
        var w = new BigEndianWriter(rom);
        w.WriteUInt32At((int)table.Entries[2].DataOffset - 8, 0x7FFFFFFF); // CURS 1's physical size
        var (files, diagnostics) = Read(rom);
        Assert.Contains(diagnostics, d => d.Code == "rom.bad-entry" && d.Severity == DiagnosticSeverity.Error);
        var fork = ResourceFork.Read(Assert.Single(files).ResourceFork.ToArray());
        Assert.Equal(0, fork.Find(FourCC.FromString("CURS"), 1)!.Length);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("pointer")]
    [InlineData("version")]
    [InlineData("field")]
    [InlineData("loop")]
    [InlineData("type")]
    [InlineData("empty")]
    public void Rejects_an_implausible_image(string damage)
    {
        var rom = damage switch
        {
            "length" => BuildRom(Sample(), length: 96 * 1024),
            "version" => BuildRom(Sample(), tableVersion: 2),
            "field" => BuildRom(Sample(), fieldSize: 9),
            "empty" => BuildRom([]),
            _ => BuildRom(Sample()),
        };
        var w = new BigEndianWriter(rom);
        var table = damage is "loop" or "type" or "pointer" ? RomResourceTable.Read(rom) : null;
        if (damage == "pointer") w.WriteUInt32At(0x1A, (uint)rom.Length - 4);
        if (damage == "loop") w.WriteUInt32At((int)table!.Entries[2].EntryOffset + 8, (uint)table.Entries[0].EntryOffset);
        if (damage == "type") w.WriteByteAt((int)table!.Entries[1].EntryOffset + 8 + 8, 0x01);
        Assert.False(MacRomReader.Instance.CanRead(ForkData.FromBytes(rom)));
        if (damage != "length") Assert.Null(RomResourceTable.Read(rom));
    }

    // Okumura's LZSS with literals only (flag $FF before every 8 bytes), plus one hand-made match.
    private static byte[] LiteralLzss(byte[] data)
    {
        var output = new List<byte>();
        for (var i = 0; i < data.Length; i += 8)
        {
            output.Add(0xFF);
            output.AddRange(data.Skip(i).Take(8));
        }
        return [.. output];
    }

    [Fact]
    public void Lzss_matches_copy_from_the_window_and_may_overlap()
    {
        // Flags %11111011 (LSB first): 'A','B', a match, then 5 literals. The match is at 4078 (where writing starts),
        // 7 + 3 = 10 bytes: it overlaps what it writes ("AB" repeated). Then a match into the space-filled window.
        byte[] input = [0xFB, (byte)'A', (byte)'B', 0xEE, 0xF7, (byte)'x', (byte)'y', (byte)'z', (byte)'1', (byte)'2', 0xFE, 0x00, 0x00];
        var output = Encoding.ASCII.GetString(Lzss.Decompress(input, 1000));
        Assert.Equal("ABABABABABABxyz12" + "   ", output);
    }

    [Fact]
    public void Lzss_stops_at_the_limit()
    {
        Assert.Throws<InvalidDataException>(() => Lzss.Decompress(LiteralLzss(new byte[100]), 50));
    }

    private static byte[] NewWorldFile(byte[] image, string? offsetText = null)
    {
        var compressed = LiteralLzss(image);
        const int offset = 0x1000;
        var script = "<CHRP-BOOT>\r<COMPATIBLE>\riMac,1\r</COMPATIBLE>\r<BOOT-SCRIPT>\r" +
            $"h# {offsetText ?? offset.ToString("X6")} constant lzss-offset\rh# {compressed.Length:X6} constant lzss-size\r" +
            "</BOOT-SCRIPT>\r</CHRP-BOOT>\r";
        var file = new byte[offset + compressed.Length];
        Encoding.ASCII.GetBytes(script).CopyTo(file, 0);
        compressed.CopyTo(file, offset);
        return file;
    }

    [Fact]
    public void NewWorld_file_unwraps_to_the_image_and_its_resources()
    {
        var file = NewWorldFile(BuildRom(Sample()));
        Assert.True(NewWorldRomReader.Instance.CanRead(ForkData.FromBytes(file)));
        var diagnostics = new List<Diagnostic>();
        var root = ContainerUnwrapper.Default.Unwrap(
            new MacFile { Name = MacString.FromMacRoman("Mac OS ROM"), DataFork = ForkData.FromBytes(file) },
            "host", new ContainerContext(null, diagnostics));
        var image = Assert.Single(root.Children);
        Assert.Equal("NewWorld ROM", image.Format);
        Assert.Equal("ROM $077D", image.File.Name.ToMacRoman());
        Assert.Equal(64 * 1024, image.File.DataFork.Length);
        var leaf = Assert.Single(root.Leaves());
        Assert.Equal("Mac ROM", leaf.Format);
        Assert.Equal(3, ResourceFork.Read(leaf.File.ResourceFork.ToArray()).Resources.Count);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void NewWorld_file_without_the_constants_or_out_of_range_is_not_recognised()
    {
        var image = BuildRom(Sample());
        Assert.False(NewWorldRomReader.Instance.CanRead(ForkData.FromBytes(NewWorldFile(image, "FFFFFF"))));
        var noScript = NewWorldFile(image);
        Encoding.ASCII.GetBytes("<CHRP-BOOX>").CopyTo(noScript, 0);
        Assert.False(NewWorldRomReader.Instance.CanRead(ForkData.FromBytes(noScript)));
    }

    // CLASSICMAC_ROM_IMAGE: a $077D ROM image (4 MiB) or a NewWorld "Mac OS ROM" file. Never committed.
    [Fact]
    public void Rom_077D_corpus()
    {
        var path = Environment.GetEnvironmentVariable("CLASSICMAC_ROM_IMAGE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Assert.Skip("Set CLASSICMAC_ROM_IMAGE to a $077D ROM image or a NewWorld Mac OS ROM file.");

        var diagnostics = new List<Diagnostic>();
        var root = ContainerUnwrapper.Default.Unwrap(path, diagnostics: diagnostics);
        var leaf = Assert.Single(root.Leaves());
        Assert.Equal("Mac ROM", leaf.Format);
        Assert.DoesNotContain(diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);

        var rom = (root.Children[0].Format == "NewWorld ROM" ? root.Children[0].File : root.File).DataFork.ToArray();
        var table = RomResourceTable.Read(rom)!;
        Assert.Equal(0x077D, table.RomVersion);
        Assert.Equal(142, table.Entries.Count);
        Assert.Equal(4, table.MaxComboIndex);
        Assert.Equal("$0800000000000000", table.Entries.Single(e => e.Type == FourCC.FromString("PACK") && e.Id == 4).ComboMaskHex);
        Assert.Equal("$0800000000000000", table.Entries.Single(e => e.Type == FourCC.FromString("PACK") && e.Id == 5).ComboMaskHex);
        Assert.Equal(2, diagnostics.Count(d => d.Code == "rom.combinations"));

        var fork = ResourceFork.Read(leaf.File.ResourceFork.ToArray());
        Assert.Equal(142, fork.Resources.Count);
        int pefs = 0, exact = 0;
        foreach (var resource in fork.Resources)
        {
            var data = resource.GetData();
            if (data.Length < 40 || !data.Span[..8].SequenceEqual("Joy!peff"u8)) continue;
            // A PEF container ends with its last section (header 40 bytes, 28 per section header).
            var reader = new BigEndianReader(data);
            int sections = reader.ReadUInt16At(32);
            long end = 40 + 28 * sections;
            for (var i = 0; i < sections; i++)
                end = Math.Max(end, (long)reader.ReadUInt32At(40 + 28 * i + 20) + reader.ReadUInt32At(40 + 28 * i + 16));
            // Most end exactly at the resource's end; the rest are padded to a multiple of 16 bytes.
            Assert.InRange(data.Length - end, 0, 15);
            if (data.Length == end) exact++;
            else Assert.Equal(0, data.Length % 16);
            pefs++;
        }
        Assert.Equal((51, 45), (pefs, exact));
    }
}
