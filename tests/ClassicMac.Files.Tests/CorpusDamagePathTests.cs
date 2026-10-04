using ClassicMac.Tests;

namespace ClassicMac.Files.Tests;

// A corpus file can hold files damaged on purpose (malformed images kept in a disk image for the readers' damage
// tests): a .classicmac-damage-paths file beside it lists them, and the tests that expect clean reads expect their
// diagnostics.
public sealed class CorpusDamagePathTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-damage").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Disk(string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void Listed_folders_and_files_inside_a_corpus_file_are_damage_tests()
    {
        var disk = Disk("MacOS9_word.hfv");
        var other = Disk("Other.hfv");
        File.WriteAllLines(Path.Combine(folder, ".classicmac-damage-paths"),
        [
            "# Hand-made NDIF images, malformed on purpose",
            "MacOS9_word.hfv | V2:",
            "MacOS9_word.hfv | Tools:bad.img",
        ]);

        Assert.True(CorpusFolders.IsDamageTest(disk, "Macintosh HD:V2:v2 adc.img"));         // in a listed folder
        Assert.True(CorpusFolders.IsDamageTest(disk, "Macintosh HD:Tools:bad.img"));         // a listed file
        Assert.True(CorpusFolders.IsDamageTest(disk, "Macintosh HD:V2:w excl.img > Read Me")); // inside one of them
        Assert.False(CorpusFolders.IsDamageTest(disk, "Macintosh HD:Tools:good.img"));
        Assert.False(CorpusFolders.IsDamageTest(disk, "Macintosh HD:V20:x.img"));            // a folder's whole name
        Assert.False(CorpusFolders.IsDamageTest(disk, null));                                // the corpus file itself
        Assert.False(CorpusFolders.IsDamageTest(other, "Macintosh HD:V2:v2 adc.img"));        // another corpus file
    }

    [Fact]
    public void Without_a_list_nothing_inside_is_a_damage_test()
    {
        var disk = Disk("MacOS9_word.hfv");
        Assert.False(CorpusFolders.IsDamageTest(disk, "Macintosh HD:V2:v2 adc.img"));
    }
}
