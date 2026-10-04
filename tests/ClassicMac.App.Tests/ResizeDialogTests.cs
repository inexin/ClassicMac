using ClassicMac.App.Controls;
using ClassicMac.App.ViewModels;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;

namespace ClassicMac.App.Tests;

// Resize's dialog (design/boards/volume-tools.md §4, V4): the size as a number and a unit, a logarithmic slider with snap
// points, the block size, and a note as you type that says what the size means.
public sealed class ResizeDialogTests
{
    private const long K = 1024, M = 1024 * 1024;

    // An 800K floppy in 512-byte blocks whose files need 403K, but whose free space in 3 runs allows only 640K now.
    private static VolumeLayout Floppy(long smallestNow = 640 * K) =>
        new(1594, 512, 21, 1, 1, 4, [new(10, 2), new(20, 1), new(1500, 94)], [new(100, 50)], smallestNow, 403 * K);

    private static ResizeViewModel Model(VolumeLayout? layout = null, Func<long, uint?, IProgress<VolumeProgress>, CancellationToken, Task>? resize = null) =>
        new("Macintosh HD", 800 * K, layout ?? Floppy(), HfsWriter.MaximumFormatSize, resize ?? ((_, _, _, _) => Task.CompletedTask));

    [Fact]
    public void It_starts_at_the_volume_s_size_with_its_limits()
    {
        var model = Model();

        Assert.Equal("Resize “Macintosh HD”", model.Title);
        Assert.Equal(("800", "KB", 800 * K), (model.Text, model.Unit, model.Size));
        Assert.Equal(["KB", "MB", "GB", "bytes"], model.Units);
        Assert.Equal(("Smallest 403K", "Now 800K", "Largest 2G"), (model.SmallestLabel, model.NowLabel, model.LargestLabel));
        Assert.Equal((403 * K, 640 * K, 800 * K, HfsWriter.MaximumFormatSize), (model.Smallest, model.SmallestNow, model.Current, model.Largest));
        Assert.Equal(NoteSeverity.None, model.NoteSeverity);
        Assert.False(model.StartCommand.CanExecute(null));                              // the size it is already
    }

    [Theory]
    [InlineData("800K", "800", "KB", 800 * K)]
    [InlineData("20M", "20", "MB", 20 * M)]
    [InlineData("1.4 MB", "1.4", "MB", 1467904)]                                        // 1.4 × 2²⁰ to whole 512-byte blocks
    [InlineData("1g", "1", "GB", 1024 * M)]
    [InlineData("819200 bytes", "819200", "bytes", 800 * K)]
    [InlineData("1440 kb", "1440", "KB", 1440 * K)]
    public void A_typed_suffix_moves_into_the_unit_select(string typed, string text, string unit, long size)
    {
        var model = Model();

        model.Text = typed;

        Assert.Equal((text, unit, size), (model.Text, model.Unit, model.Size));
    }

    [Fact]
    public void Decimals_are_for_MB_and_GB_and_the_unit_select_rereads_the_number()
    {
        var model = Model();
        model.Text = "1.5";
        Assert.Null(model.Size);                                                         // in KB
        Assert.Equal((NoteSeverity.Error, "Type a size, such as 800K or 20M."), (model.NoteSeverity, model.NoteText));
        Assert.True(model.IsInvalid);

        model.Unit = "MB";
        Assert.Equal(1536 * K, model.Size);
        Assert.False(model.IsInvalid);
        model.Unit = "GB";
        Assert.Equal(1536 * M, model.Size);
    }

    [Fact]
    public void The_note_says_what_the_size_means_as_you_type()
    {
        var model = Model();

        model.Text = "3G";
        Assert.Equal((NoteSeverity.Error, "Too large: the largest HFS volume here is 2 GB."), (model.NoteSeverity, model.NoteText));
        model.Text = "300K";
        Assert.Equal((NoteSeverity.Error, "Too small: the files need at least 403K."), (model.NoteSeverity, model.NoteText));
        model.Text = "500K";
        Assert.Equal((NoteSeverity.Warning, "The free space lies in 3 runs, so this volume can shrink only to 640K now. Defragment first to reach 403K."),
            (model.NoteSeverity, model.NoteText));
        Assert.False(model.IsInvalid);
        Assert.False(model.StartCommand.CanExecute(null));
        Assert.Equal("500 KB, needs Defragment", model.ValueText);
        model.Text = "700K";
        Assert.Equal(NoteSeverity.None, model.NoteSeverity);
        Assert.True(model.StartCommand.CanExecute(null));
        Assert.Equal("700 KB", model.ValueText);
        model.Text = "40M";
        Assert.Equal((NoteSeverity.Info, "Above 32 MB the blocks must grow to 1,024 bytes, so every file is laid out again, as Defragment does."),
            (model.NoteSeverity, model.NoteText));
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public void The_block_size_select_offers_Automatic_and_the_valid_larger_sizes()
    {
        var model = Model();

        Assert.Equal("Automatic · 512 bytes", model.BlockSizes[0].Label);
        Assert.Null(model.BlockSizes[0].Size);
        Assert.Equal([1024u, 1536u, 2048u], model.BlockSizes.Skip(1).Take(3).Select(c => c.Size!.Value));
        Assert.Equal(32768u, model.BlockSizes[^1].Size);
        Assert.Equal("1,024 bytes", model.BlockSizes[1].Label);
        Assert.Same(model.BlockSizes[0], model.SelectedBlockSize);

        model.SelectedBlockSize = model.BlockSizes[1];
        Assert.Equal((NoteSeverity.Info, "Changing the block size lays every file out again, as Defragment does."), (model.NoteSeverity, model.NoteText));
        Assert.True(model.StartCommand.CanExecute(null));                               // the same size in other blocks
        model.Text = "500K";
        Assert.Equal(NoteSeverity.Info, model.NoteSeverity);                             // laid out again, so no need to defragment

        model.Text = "40M";                                                              // 1,024-byte blocks or larger
        Assert.Equal("Automatic · 1,024 bytes", model.BlockSizes[0].Label);
        Assert.Equal(1536u, model.BlockSizes[1].Size);
        Assert.Same(model.BlockSizes[0], model.SelectedBlockSize);                       // 1,024 is Automatic now
    }

    [Fact]
    public async Task Resize_runs_with_the_size_and_block_size_chosen()
    {
        var calls = new List<(long, uint?)>();
        var model = Model(resize: (size, blockSize, progress, _) =>
        {
            progress.Report(new VolumeProgress(1, 3, "Planning the moves."));
            calls.Add((size, blockSize));
            return Task.CompletedTask;
        });
        model.Text = "1440K";
        model.SelectedBlockSize = model.BlockSizes[1];

        await model.StartCommand.ExecuteAsync(null);

        Assert.Equal([(1440 * K, 1024u)], calls);
        Assert.Equal(VolumeOperationState.Done, model.State);
        Assert.Equal("Resizing…", model.StepText);
    }

    [Fact]
    public async Task Defragment_first_takes_the_new_layout_and_keeps_the_typed_size()
    {
        var model = Model();
        Assert.False(model.DefragmentFirstCommand.CanExecute(null));
        model.DefragmentFirst = () => Task.FromResult<VolumeLayout?>(Floppy(smallestNow: 403 * K));
        model.Text = "500K";
        Assert.True(model.DefragmentFirstCommand.CanExecute(null));

        await model.DefragmentFirstCommand.ExecuteAsync(null);

        Assert.Equal(("500", 403 * K), (model.Text, model.SmallestNow));
        Assert.Equal(NoteSeverity.None, model.NoteSeverity);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public void The_slider_sets_the_size_in_a_unit_that_reads_well()
    {
        var model = Model();

        model.SetSize(1440 * K);
        Assert.Equal(("1440", "KB", 1440 * K), (model.Text, model.Unit, model.Size));
        model.SetSize(20 * M + 3 * M / 10 / 512 * 512);
        Assert.Equal(("20.3", "MB"), (model.Text, model.Unit));
        Assert.Equal(20 * M + 3 * M / 10 / 512 * 512, model.Size);                       // exact, not read back from the text
        model.SetSize(HfsWriter.MaximumFormatSize);
        Assert.Equal(("2", "GB", HfsWriter.MaximumFormatSize), (model.Text, model.Unit, model.Size));
    }

    [Fact]
    public void Snap_points_are_the_current_size_and_the_classic_disks_in_range()
    {
        var model = Model();

        Assert.Equal([(800 * K, null), (1440 * K, "1.4M"), (20 * M, "20M"), (100 * M, "100M")],
            model.Marks.Select(m => (m.Size, m.Label)));
        Assert.Equal("1.4 MB floppy disk", model.Marks[1].Tip);

        var big = new ResizeViewModel("Big", 200 * M, new VolumeLayout(51200, 4096, 2, 0, 0, 1, [new(10, 51190)], [], 50 * M, 50 * M),
            HfsWriter.MaximumFormatSize, (_, _, _, _) => Task.CompletedTask);
        Assert.Equal([(100 * M, "100M"), (200 * M, null)], big.Marks.Select(m => (m.Size, m.Label)));
    }

    [Fact]
    public void The_scale_is_logarithmic_and_snaps_as_dragged()
    {
        var scale = new SizeScale(100 * K, 10000 * K);

        Assert.Equal(0, scale.Position(100 * K), 6);
        Assert.Equal(0.5, scale.Position(1000 * K), 6);
        Assert.Equal(1, scale.Position(10000 * K), 6);
        Assert.InRange(scale.ValueAt(0.5, free: true), 1000 * K - 512, 1000 * K + 512);
        Assert.Equal(0, scale.ValueAt(0.5, free: true) % 512);

        Assert.Equal(1001 * K, SizeScale.Snap(1001 * K + 300, free: false));             // whole K below 1 MB
        Assert.Equal(1001 * K + 512, SizeScale.Snap(1001 * K + 300, free: true));
        Assert.Equal(2 * M + M / 10 / 512 * 512 + 512, SizeScale.Snap(2 * M + M / 10 + 1000, free: false));   // 0.1 MB above
    }

    [Fact]
    public void Keys_step_by_64K_or_1_MB_snap_points_and_ends()
    {
        var scale = new SizeScale(403 * K, HfsWriter.MaximumFormatSize);
        long[] marks = [800 * K, 1440 * K, 20 * M];

        Assert.Equal(864 * K, scale.Step(800 * K, SizeKey.Up, marks));
        Assert.Equal(736 * K, scale.Step(800 * K, SizeKey.Down, marks));
        Assert.Equal(21 * M, scale.Step(20 * M, SizeKey.Up, marks));
        Assert.Equal(1440 * K, scale.Step(800 * K, SizeKey.PageUp, marks));
        Assert.Equal(800 * K, scale.Step(1440 * K, SizeKey.PageDown, marks));
        Assert.Equal(403 * K, scale.Step(800 * K, SizeKey.PageDown, marks));
        Assert.Equal(HfsWriter.MaximumFormatSize, scale.Step(20 * M, SizeKey.PageUp, marks));
        Assert.Equal((403 * K, HfsWriter.MaximumFormatSize), (scale.Step(1440 * K, SizeKey.Home, marks), scale.Step(1440 * K, SizeKey.End, marks)));
        Assert.Equal(403 * K, scale.Step(410 * K, SizeKey.Down, marks));                 // kept in range
    }
}
