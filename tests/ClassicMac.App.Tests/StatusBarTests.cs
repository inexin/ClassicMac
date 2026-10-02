using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

// The status bar (design/boards/main-window.md, S4): the selected input's summary with its error and warning counts on
// the left; on the right, while work runs, what it is doing with a count and a progress bar, else the status text.
public sealed class StatusBarTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-status").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private sealed class Picker(string output) : IFilePicker
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(output);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) =>
            Task.FromResult<string?>(Path.Combine(output, suggestedName));
    }

    private string Disk(string name, int files)
    {
        var disk = new HfsBuilder();
        var games = disk.Folder(HfsBuilder.Root, "Games");
        for (var i = 0; i < files; i++)
        {
            disk.File(i % 2 == 0 ? HfsBuilder.Root : games, $"File {i}", [1], PreviewTests.Fork(("STR ", 128, null, [2, .. "hi"u8])));
        }

        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private static DiagnosticEntry Entry(DiagnosticSeverity severity, NodeViewModel? node, string source = "x") =>
        new(new Diagnostic(severity, "test.code", "message"), source, node);

    [Fact]
    public async Task The_summary_names_the_selected_input_its_format_and_files()
    {
        var model = new MainViewModel();
        Assert.Null(model.Summary);
        var first = (await model.OpenAsync(Disk("first.img", 3)))!;
        var second = (await model.OpenAsync(Disk("second.img", 5)))!;
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        model.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);

        model.Selected = first.Children.OfType<FileNode>().First();

        Assert.Equal(new StatusSummary("first.img", "HFS volume", 3, 0, 0), model.Summary);
        Assert.Equal("first.img · HFS volume · 3 files", model.Summary!.Text);
        Assert.Contains(nameof(MainViewModel.Summary), changed);
        model.Selected = second;
        Assert.Equal("second.img · HFS volume · 5 files", model.Summary!.Text);
        model.Selected = null;
        Assert.Equal("first.img", model.Summary!.Name); // nothing selected: the first input
    }

    [Theory]
    [InlineData(1, "1 file")]
    [InlineData(4982, "4,982 files")]
    public void Files_are_counted_in_words(int files, string text) =>
        Assert.Equal($"Disk · HFS · {text}", new StatusSummary("Disk", "HFS", files, 0, 0).Text);

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 error")]
    [InlineData(2, "2 errors")]
    public void Errors_read_in_words(int errors, string? text) => Assert.Equal(text, new StatusSummary("D", "F", 1, errors, 0).ErrorText);

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 warning")]
    [InlineData(1200, "1,200 warnings")]
    public void Warnings_read_in_words(int warnings, string? text) => Assert.Equal(text, new StatusSummary("D", "F", 1, 0, warnings).WarningText);

    [Fact]
    public async Task Errors_and_warnings_are_those_of_the_selected_input()
    {
        var model = new MainViewModel();
        var first = (await model.OpenAsync(Disk("first.img", 1)))!;
        var second = (await model.OpenAsync(Disk("second.img", 1)))!;
        model.Selected = first;
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        model.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);

        model.DiagnosticsPanel.Add(Entry(DiagnosticSeverity.Error, first.Children[0]));
        model.DiagnosticsPanel.Add(Entry(DiagnosticSeverity.Error, first));
        model.DiagnosticsPanel.Add(Entry(DiagnosticSeverity.Warning, first));
        model.DiagnosticsPanel.Add(Entry(DiagnosticSeverity.Info, first));
        model.DiagnosticsPanel.Add(Entry(DiagnosticSeverity.Error, second));
        model.DiagnosticsPanel.Add(Entry(DiagnosticSeverity.Warning, null, "first.img")); // about the file, before it had a node

        Assert.Equal((2, 2), (model.Summary!.Errors, model.Summary.Warnings));
        Assert.Contains(nameof(MainViewModel.Summary), changed);
        model.Selected = second;
        Assert.Equal((1, 0), (model.Summary!.Errors, model.Summary.Warnings));
    }

    [Fact]
    public async Task Progress_shows_what_runs_and_how_far()
    {
        var model = new MainViewModel();
        Assert.False(model.IsWorking);
        Assert.Null(model.ProgressCount);

        var progress = model.BeginProgress("Extracting Disk…", 3906);

        Assert.True(model.IsWorking);
        Assert.Equal(("Extracting Disk…", 0, 3906), (model.ProgressText, model.ProgressValue, model.ProgressMaximum));
        Assert.Equal("0 of 3,906", model.ProgressCount);
        Assert.False(model.IsProgressIndeterminate);
        progress.Apply(1240);
        Assert.Equal("1,240 of 3,906", model.ProgressCount);
        Assert.Equal(1240, model.ProgressValue);

        progress.Finish("Done.");
        Assert.False(model.IsWorking);
        Assert.Null(model.ProgressText);
        Assert.Equal("Done.", model.Status);
        progress.Apply(3000); // a late report changes nothing
        Assert.False(model.IsWorking);
        Assert.Equal(1240, model.ProgressValue);
        await Task.CompletedTask;
    }

    [Fact]
    public void Work_without_a_count_is_indeterminate()
    {
        var model = new MainViewModel { Status = "Before" };
        var progress = model.BeginProgress("Reading Disk…", 0);
        Assert.True(model.IsProgressIndeterminate);
        Assert.Null(model.ProgressCount);
        progress.Finish(null);
        Assert.False(model.IsWorking);
        Assert.False(model.IsProgressIndeterminate);
        Assert.Equal("Before", model.Status);
    }

    [Fact]
    public async Task Opening_a_file_shows_progress_until_it_is_read()
    {
        var model = new MainViewModel();
        var texts = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ProgressText))
            {
                texts.Enqueue(model.ProgressText);
            }
        };

        await model.OpenAsync(Disk("open.img", 1));

        Assert.Equal(["Reading open.img…", null], texts);
        Assert.False(model.IsWorking);
    }

    // The long exports show progress with their total, and leave the result in the status.
    [Theory]
    [InlineData("extract")]
    [InlineData("unpack")]
    [InlineData("convert")]
    [InlineData("export")]
    public async Task Long_exports_show_their_progress(string which)
    {
        var output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        var model = new MainViewModel { FilePicker = new Picker(output) };
        var input = (await model.OpenAsync(Disk("export.img", 4)))!;
        var file = input.Children.OfType<FileNode>().First();
        await file.EnsureLoadedAsync();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string? Text, int Maximum)>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ProgressText))
            {
                seen.Enqueue((model.ProgressText, model.ProgressMaximum));
            }
        };

        model.Selected = which == "export" ? file : input;
        await (which switch
        {
            "extract" => model.ExtractAllCommand.ExecuteAsync(null),
            "unpack" => model.UnpackAppleDoubleCommand.ExecuteAsync(null),
            "convert" => model.ConvertDocumentsCommand.ExecuteAsync(null),
            _ => model.ExportResourcesCommand.ExecuteAsync(null),
        });
        await model.ExportTask;

        var (text, maximum) = seen.First();
        Assert.Equal(which switch
        {
            "extract" => ("Extracting export…", 4),
            "unpack" => ("Unpacking export…", 4),
            "convert" => ("Converting export…", 4),
            _ => ("Exporting File 0…", 1),
        }, (text, maximum));
        Assert.Null(seen.Last().Text);
        Assert.False(model.IsWorking);
        Assert.NotNull(model.Status);
    }
}
