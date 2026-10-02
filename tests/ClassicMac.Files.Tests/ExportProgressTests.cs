using ClassicMac.Core;
using ClassicMac.Files.Export;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// Converting documents reports how many files it has looked at, for a progress bar.
public sealed class ExportProgressTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-progress-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // Reports synchronously, in order.
    private sealed class Recorder : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }

    [Fact]
    public void Converting_documents_reports_each_file_looked_at()
    {
        var files = Enumerable.Range(1, 3).Select(i => new ContainerNode("HFS volume",
            new MacFile { Name = MacString.FromMacRoman($"File {i}"), DataFork = ForkData.FromBytes(new byte[] { 1 }) }, [])).ToList();
        var root = new ContainerNode("host file", new MacFile { Name = MacString.FromMacRoman("Disk") }, files);
        var forks = files.Select(f => new ForkToExtract(f, [f.Format], new ResourceFork())).ToList();
        var progress = new Recorder();

        DocumentConverter.Convert(root, forks, Path.Combine(folder, "out"), [], progress: progress);

        Assert.Equal([1, 2, 3], progress.Values);
    }
}
