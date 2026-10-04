using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;

namespace ClassicMac.App.Tests;

// First Aid's dialog (design/boards/volume-tools.md §5, V3): it checks as it opens, says the verdict in a banner, keeps
// one log of what happened, repairs in place, and copies the report.
public sealed class FirstAidDialogTests : EditTestsBase
{
    // A floppy whose root folder's valence is one too many: Disk First Aid repairs it.
    private static byte[] Damaged()
    {
        var image = HfsWriter.Format(800 * 1024, "Floppy");
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Read Me", "hello"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
        image[1024 + 0x57]++;                                                              // drFilCnt one too many
        return image;
    }

    private static FirstAidViewModel Model(byte[] image) => new("Floppy",
        (progress, token) => Task.FromResult(HfsFirstAid.Verify(ForkData.FromBytes(image), progress, token)),
        (progress, token) => Task.FromResult(HfsFirstAid.Repair(ForkData.FromBytes(image), progress, token)));

    [Fact]
    public async Task A_sound_volume_appears_to_be_OK()
    {
        var model = Model(HfsWriter.Format(800 * 1024, "Floppy"));
        Assert.Equal("First Aid · “Floppy”", model.Title);
        Assert.Null(model.Outcome);

        await model.CheckCommand.ExecuteAsync(null);

        Assert.Equal(FirstAidOutcome.Ok, model.Outcome);
        Assert.Equal(("Appears to be OK", "First Aid found no problems."), (model.Headline, model.Sentence));
        Assert.Empty(model.Sections);
        Assert.False(model.RepairCommand.CanExecute(null));
        Assert.Equal("The volume “Floppy” appears to be OK.", model.Summary);
    }

    [Fact]
    public async Task A_volume_with_problems_is_repaired_and_checked_again()
    {
        var model = Model(Damaged());

        await model.CheckCommand.ExecuteAsync(null);

        Assert.Equal(FirstAidOutcome.NeedsRepair, model.Outcome);
        Assert.Equal("Needs repair", model.Headline);
        Assert.Matches("^First Aid found [0-9]+ problems? it can repair\\.$", model.Sentence);
        var found = Assert.Single(model.Sections);
        Assert.Equal("Problems found", found.Heading);
        Assert.All(found.Lines, line => Assert.StartsWith("Problem:  ", line, StringComparison.Ordinal));
        Assert.True(model.RepairCommand.CanExecute(null));

        await model.RepairCommand.ExecuteAsync(null);

        Assert.Equal(FirstAidOutcome.Repaired, model.Outcome);
        Assert.Equal(("Repaired · the volume appears to be OK", "Changes stay in this session until you Save As."), (model.Headline, model.Sentence));
        Assert.Equal(["Problems found", "Repaired", "Checked again"], model.Sections.Select(s => s.Heading));
        Assert.NotEmpty(model.Sections[1].Lines);
        Assert.Equal("The volume “Floppy” appears to be OK.", model.Sections[2].Lines[^1]);
        Assert.False(model.RepairCommand.CanExecute(null));

        var report = model.ReportText;
        Assert.StartsWith("First Aid · “Floppy”\nRepaired · the volume appears to be OK\n", report, StringComparison.Ordinal);
        Assert.Contains("\nProblems found\n", report, StringComparison.Ordinal);
        Assert.Contains("\nChecked again\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_cannot_be_repaired_offers_Extract_All()
    {
        var model = Model(new byte[800 * 1024]);
        model.ExtractAll = () => Task.CompletedTask;

        await model.CheckCommand.ExecuteAsync(null);

        Assert.Equal(FirstAidOutcome.CannotRepair, model.Outcome);
        Assert.Equal(("Can't repair", "Copy the files you need with Extract All, then make a new volume."), (model.Headline, model.Sentence));
        Assert.False(model.RepairCommand.CanExecute(null));
        Assert.True(model.ExtractAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_stops_the_check_with_nothing_changed()
    {
        var started = new TaskCompletionSource();
        var model = new FirstAidViewModel("Floppy", async (progress, token) =>
        {
            progress.Report(new VolumeProgress(3, 10, "Checking the catalog B-tree."));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null!;
        }, (_, _) => throw new InvalidOperationException("not run"));

        var checking = model.CheckCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(SpinWait.SpinUntil(() => model.DetailText.Length > 0, TimeSpan.FromSeconds(5)));   // Progress reports on the pool
        Assert.Equal(("Checking…", "Step 3 of 10 · Checking the catalog B-tree."), (model.StepText, model.DetailText));
        Assert.Equal("Cancel stops the check; nothing has changed yet.", model.CancelNote);
        model.CancelCommand.Execute(null);
        await checking;

        Assert.Equal((VolumeOperationState.Ready, null), (model.State, model.Outcome));
    }

    [Fact]
    public async Task Volume_First_Aid_checks_and_repairs_in_the_session()
    {
        var path = Path.Combine(folder, "Floppy.hfs");
        File.WriteAllBytes(path, Damaged());
        var dialogs = new Dialogs { FirstAid = Dialogs.Repairing };
        var model = new MainViewModel { FilePicker = new Picker(folder), EditDialogs = dialogs };
        model.Selected = await model.OpenAsync(path);

        await model.VolumeActions.FirstAidCommand.ExecuteAsync(null);

        var shown = Assert.Single(dialogs.FirstAidShown);
        Assert.Equal(FirstAidOutcome.Repaired, shown.Outcome);
        Assert.True(model.EditActions.HasUnsavedChanges);
        Assert.Contains("Save As ▸ HFS Volume Image writes the repairs.", model.Status);
    }
}
