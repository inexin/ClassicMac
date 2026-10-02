using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// A catalog too big for one index node (HfsBuilder's index levels): every file is read, in order.
public sealed class HfsBigCatalogTests
{
    [Fact]
    public void A_folder_of_five_thousand_files_reads()
    {
        var disk = new HfsBuilder { CatalogLeaves = 1300 };
        var many = disk.Folder(HfsBuilder.Root, "Many");
        for (var i = 0; i < 5000; i++)
        {
            disk.File(many, $"File {i:D4}", [], []);
        }

        var diagnostics = new List<ClassicMac.Core.Diagnostic>();
        var files = HfsReader.Instance.Read(ForkData.FromBytes(disk.Build("Big")), new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(5000, files.Count);
        Assert.Equal("Many:File 0000", files[0].MacPath);
        Assert.Equal("Many:File 4999", files[^1].MacPath);
        Assert.DoesNotContain(diagnostics, d => d.Severity != ClassicMac.Core.DiagnosticSeverity.Info);
    }
}
