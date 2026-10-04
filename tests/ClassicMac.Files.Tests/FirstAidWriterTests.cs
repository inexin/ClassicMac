using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Tests;

namespace ClassicMac.Files.Tests;

// What ClassicMac writes passes First Aid, as Disk First Aid passes it [Verified: hfs.md §5.5], and so do the volumes
// Mac OS made (the corpus).
public class FirstAidWriterTests
{
    public static TheoryData<long> Sizes => [400 * 1024, 800 * 1024, 1440 * 1024, 20L * 1024 * 1024, 300L * 1024 * 1024];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_new_volume_appears_to_be_OK(long size)
    {
        var report = HfsFirstAid.Verify(ForkData.FromBytes(HfsWriter.Format(size, "New")));

        Assert.Empty(report.Problems);
        Assert.Equal(FirstAidVerdict.AppearsOk, report.Verdict);
    }

    [Fact]
    public void A_volume_after_growing_its_catalog_deleting_and_resizing_appears_to_be_OK()
    {
        var image = HfsWriter.Format(2L * 1024 * 1024, "Busy");
        for (var i = 0; i < 300; i++)
        {
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"File {i:D3}", new byte[i * 7], new byte[i % 3 == 0 ? 600 : 0], FinderInfo.Empty);
        }

        Assert.Empty(Problems(image));                                             // the catalog grew past its first extent
        for (var i = 0; i < 300; i += 3)
        {
            image = HfsWriter.DeleteFile(ForkData.FromBytes(image), $"File {i:D3}");
        }

        Assert.Empty(Problems(image));
        image = HfsWriter.Resize(ForkData.FromBytes(image), 3L * 1024 * 1024);
        Assert.Empty(Problems(image));
    }

    [Fact]
    public void Volumes_Mac_OS_made_appear_to_be_OK()
    {
        if (!CorpusFolders.Any)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of disk images to run this.");
        }

        var checkedVolumes = 0;
        foreach (var path in CorpusFolders.EnumerateFiles("*.hfv", SearchOption.AllDirectories)
                     .Concat(CorpusFolders.EnumerateFiles("*.dsk", SearchOption.AllDirectories))
                     .Where(p => !CorpusFolders.IsDamageTest(p)))
        {
            ForkData volume;
            try
            {
                volume = ForkData.FromBytes(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                continue;
            }

            var report = HfsFirstAid.Verify(volume);
            if (report.Verdict is FirstAidVerdict.NotHfs or FirstAidVerdict.NotChecked)
            {
                continue;
            }

            Assert.True(report.Verdict == FirstAidVerdict.AppearsOk, $"{path}: {string.Join("; ", report.Problems)}");
            checkedVolumes++;
        }

        TestContext.Current.SendDiagnosticMessage($"{checkedVolumes} volumes appear to be OK.");
    }

    private static IReadOnlyList<string> Problems(byte[] image) =>
        [.. HfsFirstAid.Verify(ForkData.FromBytes(image)).Problems.Select(p => p.ToString())];
}
