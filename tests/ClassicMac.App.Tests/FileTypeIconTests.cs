using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// A file's icon by what it is (design/boards/browse-tree.md, T4): its type, else its name's extension; the tree
// always shows it, the inspector's header shows the file's own Finder icon when it has one.
public class FileTypeIconTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-type-icons-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Theory]
    [InlineData("APPL", TreeIconKind.Application)]
    [InlineData("appe", TreeIconKind.Application)]
    [InlineData("TEXT", TreeIconKind.Document)]
    [InlineData("ttro", TreeIconKind.Document)]
    [InlineData("PICT", TreeIconKind.Picture)]
    [InlineData("JPEG", TreeIconKind.Picture)]
    [InlineData("GIFf", TreeIconKind.Picture)]
    [InlineData("PNGf", TreeIconKind.Picture)]
    [InlineData("8BPS", TreeIconKind.Picture)]
    [InlineData("PNTG", TreeIconKind.Picture)]
    [InlineData("snd ", TreeIconKind.Sound)]
    [InlineData("sfil", TreeIconKind.Sound)]
    [InlineData("AIFF", TreeIconKind.Sound)]
    [InlineData("MooV", TreeIconKind.Movie)]
    [InlineData("PDF ", TreeIconKind.Pdf)]
    [InlineData("HTML", TreeIconKind.WebPage)]
    [InlineData("WDBN", TreeIconKind.WordProcessor)]
    [InlineData("W8BN", TreeIconKind.WordProcessor)]
    [InlineData("XLS8", TreeIconKind.Spreadsheet)]
    [InlineData("FFIL", TreeIconKind.Font)]
    [InlineData("LWFN", TreeIconKind.Font)]
    [InlineData("INIT", TreeIconKind.Extension)]
    [InlineData("cdev", TreeIconKind.ControlPanel)]
    [InlineData("pref", TreeIconKind.Preferences)]
    [InlineData("zsys", TreeIconKind.SystemFile)]
    [InlineData("FNDR", TreeIconKind.SystemFile)]
    [InlineData("SIT!", TreeIconKind.Parcel)]
    [InlineData("SIT5", TreeIconKind.Parcel)]
    [InlineData("PACT", TreeIconKind.Parcel)]
    [InlineData("dImg", TreeIconKind.Floppy)]
    [InlineData("rohd", TreeIconKind.Floppy)]
    [InlineData("abcd", TreeIconKind.Document)]
    public void A_type_gives_its_icon(string type, TreeIconKind kind) =>
        Assert.Equal(kind, FileTypeIcons.For(FourCC.FromString(type), "File"));

    [Theory]
    [InlineData("photo.JPG", TreeIconKind.Picture)]
    [InlineData("song.mp3", TreeIconKind.Sound)]
    [InlineData("clip.mov", TreeIconKind.Movie)]
    [InlineData("manual.pdf", TreeIconKind.Pdf)]
    [InlineData("index.htm", TreeIconKind.WebPage)]
    [InlineData("letter.doc", TreeIconKind.WordProcessor)]
    [InlineData("sheet.csv", TreeIconKind.Spreadsheet)]
    [InlineData("face.ttf", TreeIconKind.Font)]
    [InlineData("game.sit", TreeIconKind.Parcel)]
    [InlineData("files.zip", TreeIconKind.Parcel)]
    [InlineData("disk.dmg", TreeIconKind.Floppy)]
    [InlineData("readme.txt", TreeIconKind.Document)]
    [InlineData("no extension", TreeIconKind.Document)]
    [InlineData(".jpg", TreeIconKind.Document)]
    public void A_file_without_a_type_is_known_by_its_extension(string name, TreeIconKind kind)
    {
        Assert.Equal(kind, FileTypeIcons.For(default, name));
        Assert.Equal(kind, FileTypeIcons.For(FourCC.FromString("????"), name));
    }

    [Fact]
    public void A_text_file_s_extension_says_what_text_it_is()
    {
        Assert.Equal(TreeIconKind.WebPage, FileTypeIcons.For(FourCC.FromString("TEXT"), "index.html"));
        Assert.Equal(TreeIconKind.Spreadsheet, FileTypeIcons.For(FourCC.FromString("TEXT"), "data.csv"));
        Assert.Equal(TreeIconKind.Document, FileTypeIcons.For(FourCC.FromString("TEXT"), "photo.jpg"));
        // A specific type wins over the name.
        Assert.Equal(TreeIconKind.Picture, FileTypeIcons.For(FourCC.FromString("PICT"), "notes.txt"));
    }

    [Fact]
    public void Every_kind_has_art_at_both_sizes()
    {
        foreach (var kind in Enum.GetValues<TreeIconKind>().Where(k => k != TreeIconKind.Loading))
        {
            Assert.Equal(16 * 16, ClassicMac.App.Controls.TreeIcons.Pixels(kind).Length);
            Assert.Equal(32 * 32, ClassicMac.App.Controls.TreeIcons.LargePixels(kind).Length);
        }
    }

    private string Disk()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Picture", [0], [], type: "PICT", creator: "ttxt");
        disk.File(HfsBuilder.Root, "Own", [0], PreviewTests.Fork(("ICN#", -16455, null, TreeRowTests.Icn())),
            info: TreeRowTests.Info("PICT", "ttxt", ClassicMac.Files.FinderFlags.HasCustomIcon));
        var path = Path.Combine(folder, "types.img");
        File.WriteAllBytes(path, disk.Build("Types"));
        return path;
    }

    [Fact]
    public async Task The_tree_shows_the_type_icon_even_for_a_file_with_its_own()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;
        var own = input.Children.OfType<FileNode>().Single(f => f.Title == "Own");

        await own.RequestIconAsync();

        Assert.Equal(TreeIconKind.Picture, own.IconKind);
        Assert.Null(own.IconPng);
    }

    [Fact]
    public async Task The_header_shows_a_file_s_own_icon_else_its_type_icon()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;

        model.Selected = input.Children.OfType<FileNode>().Single(f => f.Title == "Own");
        await model.InspectorActions.HeaderIconTask;
        Assert.NotNull(model.InspectorActions.HeaderIconPng);

        model.Selected = input.Children.OfType<FileNode>().Single(f => f.Title == "Picture");
        await model.InspectorActions.HeaderIconTask;
        Assert.Null(model.InspectorActions.HeaderIconPng);
        Assert.Equal(TreeIconKind.Picture, model.InspectorActions.Header!.Node.IconKind);
    }
}
