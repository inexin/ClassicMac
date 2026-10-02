using ClassicMac.App.ViewModels;
using ClassicMac.Files;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

public class ExportTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-export-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // Answers every dialog with the test's output folder (or a file in it), recording what was asked.
    private sealed class Picker(string output) : IFilePicker
    {
        public List<string> Extensions { get; } = [];
        public string? SaveAs { get; set; }

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(output);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions)
        {
            Extensions.AddRange(extensions);
            return Task.FromResult<string?>(Path.Combine(output, SaveAs ?? suggestedName));
        }
    }

    // An HFS disk: "Picture" (PICT 128 "Logo" and STR# 128) at the top, and in Games a "Read Me" and "Icons" (ICN# 128, SICN 128 of two icons).
    private string Disk()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Picture", [], PreviewTests.Fork(("PICT", 128, "Logo", PreviewTests.Picture), ("STR#", 128, null, [0, 1, .. PreviewTests.Pascal])));
        var games = disk.Folder(HfsBuilder.Root, "Games");
        disk.File(games, "Read Me", "hello"u8.ToArray(), [], type: "TEXT", creator: "ttxt");
        disk.File(games, "Icons", [], PreviewTests.Fork(("ICN#", 128, null, new byte[256]), ("SICN", 128, null, new byte[64])));
        var path = Path.Combine(folder, "Disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private async Task<(MainViewModel Model, InputNode Input, Picker Picker, string Output)> Open()
    {
        var output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        var picker = new Picker(output);
        var model = new MainViewModel { FilePicker = picker };
        var input = (await model.OpenAsync(Disk()))!;
        return (model, input, picker, output);
    }

    private static async Task<FileNode> Loaded(NodeViewModel parent, string title)
    {
        var file = (FileNode)parent.Children.Single(c => c.Title == title);
        await file.EnsureLoadedAsync();
        return file;
    }

    private static string[] Files(string directory) =>
        [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).Order()];

    [Fact]
    public async Task Commands_are_enabled_for_their_node_kinds()
    {
        var (model, input, _, _) = await Open();
        var picture = await Loaded(input, "Picture");
        var type = picture.Children.OfType<ResourceTypeNode>().First();
        var resource = type.Children[0];
        var games = input.Children.OfType<FolderNode>().Single();

        (NodeViewModel Node, bool Save, bool Export, bool Extract, bool Unpack)[] cases =
        [
            (input, false, false, true, true),
            (games, false, false, true, true),
            (picture, false, true, false, true),
            (type, false, true, false, false),
            (resource, true, false, false, false),
        ];
        foreach (var (node, save, export, extract, unpack) in cases)
        {
            model.Selected = node;
            Assert.Equal(save, model.SaveResourceAsCommand.CanExecute(null));
            Assert.Equal(export, model.ExportResourcesCommand.CanExecute(null));
            Assert.Equal(extract, model.ExtractAllCommand.CanExecute(null));
            Assert.Equal(unpack, model.UnpackAppleDoubleCommand.CanExecute(null));
            Assert.Equal(unpack, model.UnpackBasiliskCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task A_resource_is_saved_decoded_or_raw()
    {
        var (model, input, picker, output) = await Open();
        var pict = (await Loaded(input, "Picture")).Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "PICT").Children.OfType<ResourceNode>().Single();
        model.Selected = pict;

        await model.SaveResourceAsCommand.ExecuteAsync(null);

        Assert.Equal([".png", ".bin"], picker.Extensions);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(Path.Combine(output, "128 Logo.png"))[..4]);

        picker.SaveAs = "logo.bin";
        await model.SaveResourceAsCommand.ExecuteAsync(null);

        Assert.Equal(PreviewTests.Picture, File.ReadAllBytes(Path.Combine(output, "logo.bin")));
    }

    // A list resource (SICN: two icons) offers its first image as a .png, and writes the PNG, not the raw data.
    [Fact]
    public async Task A_list_resource_saves_its_first_image()
    {
        var (model, input, picker, output) = await Open();
        var icons = await Loaded(input.Children.OfType<FolderNode>().Single(), "Icons");
        model.Selected = icons.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "SICN").Children.OfType<ResourceNode>().Single();

        await model.SaveResourceAsCommand.ExecuteAsync(null);

        Assert.Equal([".png", ".bin"], picker.Extensions);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], File.ReadAllBytes(Path.Combine(output, "128.png"))[..4]);
    }

    [Fact]
    public async Task A_type_exports_only_that_type_and_again_into_a_numbered_folder()
    {
        var (model, input, _, output) = await Open();
        var picture = await Loaded(input, "Picture");
        model.Selected = picture.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR#");

        await model.ExportResourcesCommand.ExecuteAsync(null);
        await model.ExportResourcesCommand.ExecuteAsync(null);

        var files = Files(Path.Combine(output, "Picture resources"));
        Assert.Contains("manifest.json", files);
        Assert.All(files.Where(f => f != "manifest.json"), f => Assert.StartsWith("STR#/", f));
        Assert.True(Directory.Exists(Path.Combine(output, "Picture resources 2")));
        Assert.Contains("resources to", model.Status);
    }

    [Fact]
    public async Task Extract_all_gives_each_file_a_folder()
    {
        var (model, input, _, output) = await Open();
        model.Selected = input;

        await model.ExtractAllCommand.ExecuteAsync(null);

        var files = Files(Path.Combine(output, "Disk resources"));
        Assert.Contains("Picture/manifest.json", files);
        Assert.Contains("Games/Icons/manifest.json", files);
        Assert.DoesNotContain(files, f => f.Contains("Read Me", StringComparison.Ordinal));
        Assert.StartsWith("4 resources from 2 files", model.Status, StringComparison.Ordinal);
    }

    // Containers on a disk open unread, but exporting the disk reads them: "Wrap.bin" is MacBinary holding "Inner" (a
    // fork with ICN# 128), next to "Other".
    [Fact]
    public async Task Extract_all_reads_the_containers_not_yet_expanded()
    {
        var inner = new MacFile
        {
            Name = ClassicMac.Core.MacString.FromMacRoman("Inner"),
            ResourceFork = ForkData.FromBytes(PreviewTests.Fork(("ICN#", 128, null, new byte[256]))),
        };
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Wrap.bin", ClassicMac.Files.Containers.MacBinaryWriter.ToArray(inner), []);
        disk.File(HfsBuilder.Root, "Other", "o"u8.ToArray(), []);
        var path = Path.Combine(folder, "Wrapped.img");
        File.WriteAllBytes(path, disk.Build("Wrapped"));
        var output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        var model = new MainViewModel { FilePicker = new Picker(output) };
        var input = (await model.OpenAsync(path))!;
        Assert.Equal(NodeKind.Loading, Assert.Single(input.Children.OfType<ContainerFileNode>().Single().Children).Kind);
        model.Selected = input;

        await model.ExtractAllCommand.ExecuteAsync(null);

        Assert.Contains("ICN#/128.png", Files(Path.Combine(output, "Wrapped resources")));
        Assert.StartsWith("1 resources from 1 files", model.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_unpacks_only_its_files()
    {
        var (model, input, _, output) = await Open();
        model.Selected = input.Children.OfType<FolderNode>().Single();

        await model.UnpackBasiliskCommand.ExecuteAsync(null);

        var target = Path.Combine(output, "Games unpacked");
        Assert.Equal(["Icons", "Read Me"], Directory.GetFiles(target).Select(Path.GetFileName).Order());
        var readMe = HostFiles.Read(Path.Combine(target, "Read Me"));
        Assert.Equal(HostLayout.BasiliskII, readMe.Layout);
        Assert.Equal("TEXT", readMe.File.FinderInfo.Type.ToString());
        Assert.Equal("hello"u8.ToArray(), readMe.File.DataFork.ToArray());
    }

    [Fact]
    public async Task A_file_unpacks_alone_as_AppleDouble()
    {
        var (model, input, _, output) = await Open();
        model.Selected = await Loaded(input, "Picture");

        await model.UnpackAppleDoubleCommand.ExecuteAsync(null);

        var target = Path.Combine(output, "Picture unpacked");
        Assert.Equal(["._Picture", "Picture"], Files(target));
        Assert.True(HostFiles.Read(Path.Combine(target, "Picture")).File.ResourceFork.Length > 0);
    }
}
