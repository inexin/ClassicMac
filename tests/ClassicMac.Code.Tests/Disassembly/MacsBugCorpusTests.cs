using ClassicMac.Code.Disassembly;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests.Disassembly;

// MacsBug names in real applications' 'CODE' resources (CLASSICMAC_CODE_CORPUS), scanned over each whole resource; the
// counts match an independent scan [Verified: the samples named in each test].
public class MacsBugCorpusTests
{
    private static readonly FourCC Code = FourCC.FromString("CODE");

    // Names in the CODE resources; compressed ones are decompressed unless uncompressedOnly.
    private static int Count(ResourceFork fork, bool uncompressedOnly = false)
    {
        int count = 0;
        foreach (var resource in fork.Resources.Where(r => r.Type == Code))
        {
            if (uncompressedOnly && (resource.Attributes & ResourceAttributes.Compressed) != 0)
                continue;
            var data = ResourceDecompression.Default.GetData(resource, fork, null, new List<Diagnostic>());
            count += MacsBugNames.Find(data).Count;
        }
        return count;
    }

    [Fact]
    public void Realmz() =>
        Assert.Equal(341, Count(ResourceFork.Read(CodeCorpus.Require("Realmz 7.1.2", 576955))));

    [Fact]
    public void ResEdit()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("ResEdit.rsrc", 670639));
        Assert.Equal(99, Count(fork, uncompressedOnly: true));
        Assert.Equal(102, Count(fork));
    }

    // The older QDHarness build (the current one has 312).
    [Fact]
    public void QDHarness() =>
        Assert.Equal(304, Count(ResourceFork.Read(CodeCorpus.Require("QDHarness.APPL", 117059))));

    [Fact]
    public void Disk_Copy_6_1_2() =>
        Assert.Equal(160, Count(ResourceFork.Read(CodeCorpus.Require("DC612", 434333))));

    [Fact]
    public void First_names()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("Realmz 7.1.2", 576955));
        var names = MacsBugNames.Find(fork.Find(Code, 1)!.GetData());
        Assert.Equal(["cos", "sin", "sqrt", "nanl"], names.Take(4).Select(n => n.Name));
    }
}
