using ClassicMac.Code.Disassembly;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests.Disassembly;

// MacsBug names in real applications' 'CODE' resources (CLASSICMAC_CODE_CORPUS), scanned over each whole resource.
// The names at given offsets were read off the bytes by hand; the counts pin the totals [Verified: the samples named
// in each test].
public class MacsBugCorpusTests
{
    private static readonly FourCC Code = FourCC.FromString("CODE");

    // Names in the CODE resources; compressed ones are decompressed unless uncompressedOnly.
    private static List<MacsBugName> All(ResourceFork fork, bool uncompressedOnly = false)
    {
        var names = new List<MacsBugName>();
        foreach (var resource in fork.Resources.Where(r => r.Type == Code))
        {
            if (uncompressedOnly && (resource.Attributes & ResourceAttributes.Compressed) != 0)
            {
                continue;
            }

            var data = ResourceDecompression.Default.GetData(resource, fork, null, new List<Diagnostic>());
            names.AddRange(MacsBugNames.Find(data));
        }
        return names;
    }

    // 341 variable-form names and 144 fixed-8 names whose first character has bit 7 clear (MADPlayer's library).
    [Fact]
    public void Realmz()
    {
        var names = All(ResourceFork.Read(CodeCorpus.Require("Realmz 7.1.2", 576955)));
        Assert.Equal(485, names.Count);
        Assert.Equal(341, names.Count(n => n.Form == MacsBugNameForm.Variable));
        Assert.Equal(144, names.Count(n => n.Form == MacsBugNameForm.Fixed8));
    }

    // Names read off Realmz's CODE 1 by hand: rts, then the encoding.
    [Theory]
    [InlineData(0x7F26, "cos", MacsBugNameForm.Variable, 6)]        // 4E75 83 'cos' 0000
    [InlineData(0x8076, "sqrt", MacsBugNameForm.Variable, 8)]
    [InlineData(0x838E, "IFILEOPE", MacsBugNameForm.Fixed8, 8)]     // then link a6,#-4
    [InlineData(0x842E, "MOT32", MacsBugNameForm.Fixed8, 8)]        // 'MOT32   ', then link a6,#0
    [InlineData(0x8EA6, "MADCREAT", MacsBugNameForm.Fixed8, 8)]
    [InlineData(0x1156A, "MADTICKR", MacsBugNameForm.Fixed8, 8)]    // then bsr.s
    public void Realmz_names_at(int offset, string name, MacsBugNameForm form, int length)
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("Realmz 7.1.2", 576955));
        var found = MacsBugNames.Read(fork.Find(Code, 1)!.GetData(), offset, offset - 2);
        Assert.NotNull(found);
        Assert.Equal((name, form, length), (found.Name, found.Form, found.Length));
    }

    // Text after an rts in Realmz's CODE 1 that is not a name: 'mToStrin' (lower case) is the inside of a string.
    [Fact]
    public void Realmz_not_names()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("Realmz 7.1.2", 576955));
        var names = MacsBugNames.Find(fork.Find(Code, 1)!.GetData());
        Assert.DoesNotContain(names, n => n.Offset == 0x4EB6E);
    }

    // Strings after a return in the Mac OS 9 System file that are not names.
    [Theory]
    [InlineData("DRVR", -20175, 0xBB8)]     // 'Apple_Driver…'
    [InlineData("ptch", -20217, 0x9DEE)]    // '0123456789…'
    [InlineData("ptch", -20217, 0x10862)]   // 'File_Mgr_S…'
    [InlineData("PACK", 11, 0x4A36)]        // 'prvwPICTTE…'
    [InlineData("DRVR", 10, 0xC32)]         // 'YJYJYJYJYJ…'
    public void System_not_names(string type, int id, int offset)
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var resource = fork.Find(FourCC.FromString(type), (short)id)!;
        var data = ResourceDecompression.Default.GetData(resource, fork, null, new List<Diagnostic>());
        Assert.DoesNotContain(MacsBugNames.Find(data), n => n.Offset == offset);
    }

    [Fact]
    public void ResEdit()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("ResEdit.rsrc", 670639));
        Assert.Equal(99, All(fork, uncompressedOnly: true).Count);
        Assert.Equal(102, All(fork).Count);
    }

    // The older QDHarness build (the current one has 312).
    [Fact]
    public void QDHarness() =>
        Assert.Equal(304, All(ResourceFork.Read(CodeCorpus.Require("QDHarness.APPL", 117059))).Count);

    [Fact]
    public void Disk_Copy_6_1_2() =>
        Assert.Equal(160, All(ResourceFork.Read(CodeCorpus.Require("DC612", 434333))).Count);

    [Fact]
    public void First_names()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("Realmz 7.1.2", 576955));
        var names = MacsBugNames.Find(fork.Find(Code, 1)!.GetData());
        Assert.Equal(["cos", "sin", "sqrt", "nanl"], names.Take(4).Select(n => n.Name));
    }
}
