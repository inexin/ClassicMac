using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;

namespace ClassicMac.App.Tests;

// Defragment's dialog (design/boards/volume-tools.md §3, V5): it explains itself with the map and the Now and After
// figures, runs with progress and Cancel, and shows the result in the same dialog.
public sealed class DefragmentDialogTests : EditTestsBase
{
    private static VolumeLayout Fragmented() =>
        new(4090, 512, 21, 1, 1, 17, [new(10, 2), new(20, 1), new(40, 2), new(90, 3)], [new(100, 50)], 640 * 1024, 403 * 1024);

    private static VolumeLayout InOrder() => new(4090, 512, 21, 0, 0, 1, [new(4082, 8)], [], 403 * 1024, 403 * 1024);

    [Fact]
    public async Task It_explains_itself_then_shows_what_it_did()
    {
        var (before, after) = (Fragmented(), InOrder());
        var model = new DefragmentViewModel("Macintosh HD", before, (_, _) => Task.FromResult<VolumeLayout?>(after));

        Assert.Equal(VolumeOperationState.Ready, model.State);
        Assert.Equal("Defragment “Macintosh HD”", model.Title);
        Assert.StartsWith("Moves every file into one piece", model.Lead, StringComparison.Ordinal);
        Assert.Equal(("Now", before), (model.MapTitle, model.MapLayout));
        Assert.Equal(
            [("Split files", "1 of 21", "None"), ("Free space", "4 runs · largest 3 blocks", "1 run · 8 blocks"), ("Can shrink to", "640 KB", "403 KB")],
            model.Figures.Select(f => (f.Label, f.Now, f.After)));
        Assert.True(model.StartCommand.CanExecute(null));

        await model.StartCommand.ExecuteAsync(null);

        Assert.Equal(VolumeOperationState.Done, model.State);
        Assert.Equal("Done. Every file is in one piece and the free space is one run.", model.Lead);
        Assert.Equal(("After", after), (model.MapTitle, model.MapLayout));
        Assert.Equal(("None", "1 run · 8 blocks", "403 KB"), (model.Figures[0].After, model.Figures[1].After, model.Figures[2].After));
        Assert.EndsWith("Save As ▸ HFS Volume Image writes the result.", model.DoneNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_volume_in_order_says_so_and_offers_only_Done()
    {
        var model = new DefragmentViewModel("Macintosh HD", InOrder(), (_, _) => throw new InvalidOperationException("not run"));

        Assert.True(model.IsInOrder);
        Assert.Equal("This volume is already in order.", model.Lead);
        Assert.False(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Running_shows_progress_and_Cancel_stops_it_with_nothing_changed()
    {
        var started = new TaskCompletionSource();
        var model = new DefragmentViewModel("Macintosh HD", Fragmented(), async (progress, token) =>
        {
            progress.Report(new VolumeProgress(12, 21, "File 12 of 21"));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        });

        var running = model.StartCommand.ExecuteAsync(null);
        await started.Task;
        Assert.Equal((VolumeOperationState.Running, "Defragmenting…"), (model.State, model.StepText));
        Assert.Equal("Cancel stops it; nothing has changed yet.", model.CancelNote);
        model.CancelCommand.Execute(null);
        await running;

        Assert.Equal(VolumeOperationState.Ready, model.State);
        Assert.Null(model.After);
    }

    [Fact]
    public void Progress_reads_as_a_share_and_a_detail_line()
    {
        Assert.Equal((12 / 21.0, "File 12 of 21"), VolumeOperation.Describe(new VolumeProgress(12, 21, "File 12 of 21")));
        Assert.Equal((0.3, "Step 3 of 10 · Checking catalog file."), VolumeOperation.Describe(new VolumeProgress(3, 10, "Checking catalog file.")));
    }

    [Fact]
    public async Task Volume_Defragment_opens_the_dialog_and_the_volume_changes_when_it_runs()
    {
        var image = HfsDefragmentFixtures.Fragmented();
        var path = Path.Combine(folder, "Frag.hfs");
        File.WriteAllBytes(path, image);
        var dialogs = new Dialogs();
        var model = new MainViewModel { FilePicker = new Picker(folder), EditDialogs = dialogs };
        model.Selected = await model.OpenAsync(path);

        await model.VolumeActions.DefragmentCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialogs.DefragmentShown);
        Assert.Equal(1, shown.Before.SplitFiles);
        Assert.Equal(VolumeOperationState.Done, shown.State);
        Assert.Equal(0, shown.After!.SplitFiles);
        Assert.True(model.EditActions.HasUnsavedChanges);
    }
}

// A volume with a file in pieces: small files with every other one deleted, then a file over the gaps.
internal static class HfsDefragmentFixtures
{
    public static byte[] Fragmented()
    {
        var image = HfsWriter.Format(2 * 1024 * 1024, "Frag");
        for (var i = 0; i < 20; i++)
        {
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"Pad {i:D2}", new byte[1024], Array.Empty<byte>(), FinderInfo.Empty);
        }

        for (var i = 0; i < 20; i += 2)
        {
            image = HfsWriter.DeleteFile(ForkData.FromBytes(image), $"Pad {i:D2}");
        }

        var free = new BigEndianReader(image).ReadUInt16At(1024 + 0x22);
        return HfsWriter.CreateFile(ForkData.FromBytes(image), "Spread", new byte[(free - 4) * 512], Array.Empty<byte>(), FinderInfo.Empty);
    }
}
