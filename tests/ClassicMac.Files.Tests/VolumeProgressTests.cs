using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// Progress and cancellation through First Aid, Defragment and Resize (docs/formats/file-systems/hfs.md §5.8): steps
// as the work goes, and a cancel that leaves the session's volume as it was.
public sealed class VolumeProgressTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cm-progress").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    // Reports kept as they come (Progress<T> would post them to a synchronization context).
    private sealed class Recorder : IProgress<VolumeProgress>
    {
        public List<VolumeProgress> Reports { get; } = [];

        public void Report(VolumeProgress value) => Reports.Add(value);
    }

    private static CancellationToken Cancelled() => new(canceled: true);

    [Fact]
    public void First_Aid_reports_each_stage_as_a_step_of_all()
    {
        var recorder = new Recorder();

        var report = HfsFirstAid.Verify(ForkData.FromBytes(HfsDefragmentTests.Fragmented()), recorder, TestContext.Current.CancellationToken);

        Assert.Equal(FirstAidVerdict.AppearsOk, report.Verdict);
        Assert.Equal(report.Stages, recorder.Reports.Select(r => r.Text));
        Assert.Equal(Enumerable.Range(1, recorder.Reports.Count), recorder.Reports.Select(r => r.Step));
        Assert.All(recorder.Reports, r => Assert.Equal(recorder.Reports.Count, r.Steps));       // the stages known before
    }

    [Fact]
    public void A_cancelled_First_Aid_throws()
    {
        Assert.Throws<OperationCanceledException>(() => HfsFirstAid.Verify(ForkData.FromBytes(HfsDefragmentTests.Fragmented()), null, Cancelled()));
    }

    [Fact]
    public void Defragment_and_resize_report_file_by_file()
    {
        var image = HfsDefragmentTests.Fragmented();
        var defragmenting = new Recorder();
        var resizing = new Recorder();

        HfsWriter.Defragment(ForkData.FromBytes(image), defragmenting, TestContext.Current.CancellationToken);
        HfsWriter.Resize(ForkData.FromBytes(image), 20 * 1024 * 1024, 1024, resizing, TestContext.Current.CancellationToken);   // laid out again

        foreach (var recorder in new[] { defragmenting, resizing })
        {
            var files = recorder.Reports.Where(r => r.Text.StartsWith("File ", StringComparison.Ordinal)).ToList();
            Assert.Equal(21, files.Count);                                                     // 20 pads and Spread
            Assert.Equal("File 21 of 21", files[^1].Text);
            Assert.All(recorder.Reports, r => Assert.InRange(r.Step, 0, r.Steps));
        }
    }

    [Fact]
    public void A_cancel_leaves_the_session_s_volume_as_it_was()
    {
        var path = Path.Combine(directory, "frag.img");
        File.WriteAllBytes(path, HfsDefragmentTests.Fragmented());
        var session = InputEditSession.Open(path);
        var before = session.Volume;

        Assert.Throws<OperationCanceledException>(() => session.Defragment("", null, Cancelled()));
        Assert.Throws<OperationCanceledException>(() => session.Resize(20 * 1024 * 1024, null, null, Cancelled()));
        Assert.Throws<OperationCanceledException>(() => session.Repair("", null, Cancelled()));

        Assert.False(session.HasChanges);
        Assert.Equal(before, session.Volume);
    }
}
