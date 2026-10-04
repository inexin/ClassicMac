using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// The Volume menu (design/boards/volume-tools.md §1, V1): items that do not apply say why; the maintenance commands
// sit on the volume's own node in the tree's context menu; Del deletes a resource, or else a file or folder.
public sealed class VolumeMenuTests : EditTestsBase
{
    private async Task<(MainViewModel Model, InputNode Input)> Open(string name, byte[] data)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        var model = new MainViewModel { FilePicker = new Picker(folder), EditDialogs = new Dialogs() };
        var input = (await model.OpenAsync(path))!;
        model.Selected = input;
        return (model, input);
    }

    private static byte[] Hfs()
    {
        var image = HfsWriter.Format(800 * 1024, "Floppy");
        return HfsWriter.CreateFile(ForkData.FromBytes(image), "Read Me", "hello"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
    }

    [Fact]
    public async Task A_plain_HFS_volume_offers_everything()
    {
        var (model, _) = await Open("Floppy.hfs", Hfs());
        var actions = model.VolumeActions;

        Assert.Equal((null, null, null), (actions.EditReason, actions.ResizeReason, actions.FirstAidReason));
        Assert.True(actions.IsVolumeNode);
    }

    [Fact]
    public async Task An_HFS_Plus_volume_offers_only_First_Aid_and_says_so()
    {
        var builder = new HfsPlusBuilder();
        builder.File(HfsPlusBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var (model, _) = await Open("Plus.img", builder.Build("Plus"));
        var actions = model.VolumeActions;

        Assert.False(actions.NewFolderCommand.CanExecute(null));
        Assert.False(actions.ResizeCommand.CanExecute(null));
        Assert.True(actions.FirstAidCommand.CanExecute(null));
        Assert.Equal("Only First Aid works on HFS Plus volumes", actions.EditReason);
        Assert.Equal("Only First Aid works on HFS Plus volumes", actions.ResizeReason);
        Assert.Null(actions.FirstAidReason);
        Assert.True(actions.IsVolumeNode);
    }

    [Fact]
    public async Task A_partition_cannot_be_resized_and_says_why()
    {
        var (model, input) = await Open("Disk.img", Fixtures.PartitionMap(("Floppy", "Apple_HFS", Hfs())));
        var actions = model.VolumeActions;
        model.Selected = input.Children.OfType<ContainerFileNode>().Single();

        Assert.Null(actions.EditReason);
        Assert.False(actions.ResizeCommand.CanExecute(null));
        Assert.Equal("A partition can't be resized: its map would change", actions.ResizeReason);
        Assert.True(actions.IsVolumeNode);
    }

    [Fact]
    public async Task A_file_that_is_no_volume_cannot_be_changed_and_its_items_are_not_volume_nodes()
    {
        var model = new MainViewModel { EditDialogs = new Dialogs() };
        model.Selected = await model.OpenAsync(MacBinary());
        var actions = model.VolumeActions;

        Assert.Equal(("This volume can't be changed", "This volume can't be changed", "This volume can't be changed"),
            (actions.EditReason, actions.ResizeReason, actions.FirstAidReason));
        Assert.False(actions.IsVolumeNode);
    }

    [Fact]
    public async Task Files_and_folders_are_not_volume_nodes()
    {
        var (model, input) = await Open("Floppy.hfs", Hfs());
        model.Selected = input.Children.Single(n => n.Title == "Read Me");

        Assert.False(model.VolumeActions.IsVolumeNode);
        Assert.True(model.VolumeActions.FirstAidCommand.CanExecute(null));                // the menu bar still offers it
    }

    [Fact]
    public async Task Delete_removes_the_selected_file_when_no_resource_is_selected()
    {
        var (model, input) = await Open("Floppy.hfs", Hfs());
        model.Selected = input.Children.Single(n => n.Title == "Read Me");
        Assert.True(model.EditActions.DeleteSelectionCommand.CanExecute(null));

        await model.EditActions.DeleteSelectionCommand.ExecuteAsync(null);

        Assert.DoesNotContain(input.Children, n => n.Title == "Read Me");
        Assert.True(model.EditActions.HasUnsavedChanges);
    }
}
