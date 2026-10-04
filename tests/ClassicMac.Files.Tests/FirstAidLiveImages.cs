using System.Globalization;
using System.Text;
using ClassicMac.Files.Hfs;
using static ClassicMac.Files.Tests.FirstAidImages;

namespace ClassicMac.Files.Tests;

// The images to check against Disk First Aid 8.5.5 live (hfs.md §5.6): each fault as a volume, the volume ClassicMac's
// repair writes, and ClassicMac's lines for both in expected.txt. Runs only with CLASSICMAC_FIRSTAID_OUT set to a folder.
public class FirstAidLiveImages
{
    private static IEnumerable<(string Name, Action<byte[]> Fault)> Faults()
    {
        yield return ("rc1-reserved", image => Put16(image, Record(image, 2, "T1") + 0x18, 5));
        yield return ("rc6-valence", image => Put16(image, Record(image, 2, "D1") + 4, U16(image, Record(image, 2, "D1") + 4) + 1));
        yield return ("rb6-file-count", image => Put32(image, Primary + 0x54, U32(image, Primary + 0x54) + 1));
        yield return ("rd1-leaked-block", image =>
        {
            uint n = U16(image, Primary + 0x12);
            image[U16(image, Primary + 0x0E) * Sector + (int)((n - 1) / 8)] |= (byte)(0x80 >> (int)((n - 1) % 8));
        });
        yield return ("rc7-root-name-lock", image => Put16(image, Record(image, 1, "First Aid") + 0x1E, U16(image, Record(image, 1, "First Aid") + 0x1E) | 0x1000));
        yield return ("custom-icon", image => Put16(image, Record(image, 2, "D1") + 0x1E, U16(image, Record(image, 2, "D1") + 0x1E) | 0x0400));
        yield return ("used-block-free", image =>
        {
            uint block = U16(image, Record(image, 2, "T1") + 0x4A);
            image[U16(image, Primary + 0x0E) * Sector + (int)(block / 8)] &= (byte)~(0x80 >> (int)(block % 8));
        });
        yield return ("dangling-file-thread", image =>
        {
            int thread = Record(image, D2, "");
            image[thread] = 4;
            image[thread + 0x0F] = (byte)'X';
        });
        yield return ("missing-folder", image => RemoveRecord(image, D1, "D2"));
        yield return ("missing-file-thread", image => image[Record(image, 2, "T1") + 2] |= 0x02);
        yield return ("header-nrecs", image => Put32(image, CatalogNode(image, 0) + 14 + 6, U32(image, CatalogNode(image, 0) + 14 + 6) + 1));
        yield return ("extra-alternate-missing", image => image.AsSpan(Alternate(image), Sector).Clear());
        yield return ("extra-root-file-count", image => Put16(image, Primary + 0x0C, U16(image, Primary + 0x0C) + 3));
        yield return ("extra-free-blocks", image => Put16(image, Primary + 0x22, U16(image, Primary + 0x22) - 1));
        yield return ("extra-short-peof", image =>
        {
            int file = Record(image, 2, "T1");
            Put32(image, file + 0x1A, 100);
            Put32(image, file + 0x1E, 512);
        });
    }

    [Fact]
    public void Write_the_images_for_Disk_First_Aid()
    {
        var folder = Environment.GetEnvironmentVariable("CLASSICMAC_FIRSTAID_OUT");
        if (string.IsNullOrEmpty(folder))
        {
            Assert.Skip("Set CLASSICMAC_FIRSTAID_OUT to a folder to write the images.");
        }

        Directory.CreateDirectory(folder);
        var expected = new StringBuilder();
        foreach (var (name, fault) in Faults())
        {
            var image = Base();
            fault(image);
            File.WriteAllBytes(Path.Combine(folder, name + ".img"), image);
            var result = HfsFirstAid.Repair(ForkData.FromBytes(image));
            expected.AppendLine(CultureInfo.InvariantCulture, $"{name}.img");
            Lines(expected, result.Before, result.Before.Summary);
            if (result.Volume is { } repaired)
            {
                File.WriteAllBytes(Path.Combine(folder, name + ".repaired.img"), repaired);
                expected.AppendLine(CultureInfo.InvariantCulture, $"{name}.repaired.img");
                Lines(expected, HfsFirstAid.Verify(ForkData.FromBytes(repaired)), null);
            }

            expected.AppendLine();
        }

        File.WriteAllText(Path.Combine(folder, "expected.txt"), expected.ToString());
    }

    private static void Lines(StringBuilder text, FirstAidReport report, string? summary)
    {
        foreach (var problem in report.Problems)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(problem.Origin == FirstAidOrigin.ClassicMac ? "[ClassicMac] " : "")}{problem}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"  {summary ?? report.Summary}");
    }
}
