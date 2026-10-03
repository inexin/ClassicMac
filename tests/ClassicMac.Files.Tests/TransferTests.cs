using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Commands;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;

namespace ClassicMac.Files.Tests;

// Transfers that convert (docs/cli.md §2.5, §3.1): a file out as UTF-8 text or BinHex, a host text file or BinHex file in.
public sealed class TransferTests : IDisposable
{
    // "Café" CR "§ 1" CR in Mac OS Roman: é is $8E, § is $A4.
    private static readonly byte[] MacText = [(byte)'C', (byte)'a', (byte)'f', 0x8E, 0x0D, 0xA4, (byte)' ', (byte)'1', 0x0D];

    private readonly string folder = Directory.CreateTempSubdirectory("cm-transfer").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private MacPathTree Open()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Notes", MacText, "rsrc"u8.ToArray(), type: "TEXT", creator: "ttxt");
        builder.File(HfsBuilder.Root, "Report.txt", MacText, [], type: "TEXT", creator: "ttxt");
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, builder.Build("Disk"));
        return MacPathTree.Open(path);
    }

    private string Out() => Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;

    [Fact]
    public void Get_as_text_writes_the_data_fork_as_UTF8_with_LF_line_ends()
    {
        using var tree = Open();
        var output = Out();

        var written = MacCommands.Get(tree, tree.Resolve("Notes")!, output, MacGetFormat.Text, overwrite: false);

        Assert.Equal([Path.Combine(output, "Notes.txt")], written);                      // .txt added to a name without one
        Assert.Equal(Encoding.UTF8.GetBytes("Café\n§ 1\n"), File.ReadAllBytes(written[0]));   // no byte order mark
        Assert.Equal([Path.Combine(output, "Report.txt")], MacCommands.Get(tree, tree.Resolve("Report.txt")!, output, MacGetFormat.Text, overwrite: false));
    }

    [Fact]
    public void Get_as_BinHex_writes_an_hqx_file_with_both_forks_and_the_Finder_info()
    {
        using var tree = Open();
        var output = Out();

        var written = MacCommands.Get(tree, tree.Resolve("Notes")!, output, MacGetFormat.BinHex, overwrite: false);

        Assert.Equal([Path.Combine(output, "Notes.hqx")], written);
        var back = BinHexReader.Instance.Read(ForkData.FromBytes(File.ReadAllBytes(written[0])), new ContainerContext()).Single();
        Assert.Equal(("Notes", "TEXT", "ttxt"), (back.Name.ToMacRoman(), back.FinderInfo.Type.ToString(), back.FinderInfo.Creator.ToString()));
        Assert.Equal(MacText, back.DataFork.ToArray());
        Assert.Equal("rsrc"u8.ToArray(), back.ResourceFork.ToArray());
    }

    [Fact]
    public void A_BinHex_file_is_read_as_the_file_it_holds()
    {
        using var tree = Open();
        var hqx = MacCommands.Get(tree, tree.Resolve("Notes")!, Out(), MacGetFormat.BinHex, overwrite: false)[0];

        var file = HostImport.Read(hqx);

        Assert.Equal(("Notes", "TEXT"), (file.Name.ToMacRoman(), file.FinderInfo.Type.ToString()));
        Assert.Equal("rsrc"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void A_host_text_file_is_read_as_Mac_OS_Roman_with_CR_line_ends()
    {
        var path = Path.Combine(folder, "notes.txt");
        File.WriteAllBytes(path, [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("Café\r\n§ 1\n")]);

        var file = HostImport.ReadText(path);

        Assert.Equal("notes.txt", file.Name.ToMacRoman());
        Assert.Equal(MacText, file.DataFork.ToArray());                                     // BOM gone, CRLF and LF made CR
        Assert.Equal(("TEXT", "ttxt"), (file.FinderInfo.Type.ToString(), file.FinderInfo.Creator.ToString()));
        Assert.Equal(0, file.ResourceFork.Length);
    }

    [Fact]
    public void Text_Mac_OS_Roman_cannot_hold_is_refused_naming_the_line()
    {
        var path = Path.Combine(folder, "emoji.txt");
        File.WriteAllText(path, "fine\nnot ✓ here\n");

        var error = Assert.Throws<InvalidDataException>(() => HostImport.ReadText(path));

        Assert.Contains("line 2", error.Message);
        Assert.Contains("✓", error.Message);
    }
}
