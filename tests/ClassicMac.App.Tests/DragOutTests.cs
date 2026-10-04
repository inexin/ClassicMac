using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// Dragging files and resources out of the tree: what is written for the platform's drag.
public sealed class DragOutTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-drag").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static readonly FourCC Str = FourCC.FromString("STR ");

    private sealed class Picker(string folder) : IFilePicker
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(folder);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) =>
            Task.FromResult<string?>(Path.Combine(folder, suggestedName));
    }

    // A MacBinary file holding "Prefs" (type TEXT) with data and STR 128, 129.
    private async Task<(MainViewModel Model, FileNode File, ResourceFork Fork)> Open()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, "\u0005hello"u8.ToArray()));
        fork.Add(new Resource(Str, 129, "\u0002hi"u8.ToArray()));
        var file = new MacFile
        {
            Name = MacString.FromMacRoman("Prefs"),
            DataFork = ForkData.FromBytes("data"u8.ToArray()),
            ResourceFork = ForkData.FromBytes(fork.ToArray()),
            FinderInfo = FinderInfo.Empty with { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt") },
        };
        var path = Path.Combine(folder, "Prefs.bin");
        File.WriteAllBytes(path, MacBinaryWriter.ToArray(file));
        var model = new MainViewModel { FilePicker = new Picker(Path.Combine(folder, "saved")) };
        model.DragOut.DragFolder = Path.Combine(folder, "drag");
        var input = (await model.OpenAsync(path))!;
        var node = input.Children.OfType<FileNode>().Single();
        await node.EnsureLoadedAsync();
        return (model, node, fork);
    }

    // While the temporary file is written the status bar says so (boards/browse-tree.md, drag source), then goes back.
    [Fact]
    public async Task The_status_says_what_is_written_for_the_drag()
    {
        var (model, file, _) = await Open();
        model.Status = "Before";
        var statuses = new List<string?>();
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Status)) { statuses.Add(model.Status); } };

        await model.DragOut.PrepareDragOutAsync(file);
        model.DragOut.DragOutAsMacBinary = true;
        await model.DragOut.PrepareDragOutAsync(file);
        await model.DragOut.PrepareDragOutAsync(file.Children.OfType<ResourceTypeNode>().Single().Children[0]);

        Assert.Equal(["Writing AppleDouble…", "Before", "Writing MacBinary…", "Before", "Writing 128…", "Before"], statuses);
    }

    [Fact]
    public async Task A_file_drags_out_as_its_data_fork_and_an_AppleDouble_header()
    {
        var (model, file, fork) = await Open();
        Assert.True(DragOut.CanDragOut(file));

        var paths = await model.DragOut.PrepareDragOutAsync(file);

        Assert.Equal(["Prefs", "._Prefs"], paths.Select(Path.GetFileName));
        Assert.All(paths, p => Assert.StartsWith(model.DragOut.DragFolder, p));
        Assert.Equal("data"u8.ToArray(), File.ReadAllBytes(paths[0]));
        var back = HostFiles.Read(paths[0]);
        Assert.Equal(HostLayout.AppleDouble, back.Layout);
        Assert.Equal(fork.ToArray(), back.File.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), back.File.FinderInfo.Type);

        // A second drag of the same file goes into a folder of its own.
        var again = await model.DragOut.PrepareDragOutAsync(file);
        Assert.NotEqual(Path.GetDirectoryName(paths[0]), Path.GetDirectoryName(again[0]));
    }

    [Fact]
    public async Task As_MacBinary_a_file_drags_out_as_one_file_with_its_unsaved_edits()
    {
        var (model, file, _) = await Open();
        model.DragOut.DragOutAsMacBinary = true;
        Assert.Equal(DragOutFormat.MacBinary, model.DragOut.DragOutFormat);
        model.Selected = file.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().Single(r => r.Resource.Id == 129);
        model.DeleteResourceCommand.Execute(null);

        var paths = await model.DragOut.PrepareDragOutAsync(file);

        var path = Assert.Single(paths);
        Assert.Equal("Prefs.bin", Path.GetFileName(path));
        var back = Assert.Single(MacBinaryReader.III.Read(ForkData.FromFile(path), new ContainerContext()));
        Assert.Equal("data"u8.ToArray(), back.DataFork.ToArray());
        var resources = ResourceFork.Read(back.ResourceFork.ToArray());
        Assert.NotNull(resources.Find(Str, 128));
        Assert.Null(resources.Find(Str, 129));
    }

    [Fact]
    public async Task A_resource_drags_out_as_save_resource_as_writes_it()
    {
        var (model, file, _) = await Open();
        var resource = file.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().Single(r => r.Resource.Id == 128);
        Assert.False(DragOut.CanDragOut(resource.Parent));
        Assert.True(DragOut.CanDragOut(resource));

        var dragged = Assert.Single(await model.DragOut.PrepareDragOutAsync(resource));

        Directory.CreateDirectory(Path.Combine(folder, "saved"));
        model.Selected = resource;
        await model.ExportActions.SaveResourceAsCommand.ExecuteAsync(null);
        var saved = Assert.Single(Directory.GetFiles(Path.Combine(folder, "saved")));
        Assert.Equal(Path.GetFileName(saved), Path.GetFileName(dragged));
        Assert.Equal(File.ReadAllBytes(saved), File.ReadAllBytes(dragged));

        model.DragOut.CleanUpDragOut();
        Assert.False(Directory.Exists(model.DragOut.DragFolder));
    }
}
