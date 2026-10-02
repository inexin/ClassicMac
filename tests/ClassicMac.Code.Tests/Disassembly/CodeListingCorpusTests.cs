using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests.Disassembly;

// Listings of every segment and fragment of the code corpus (CLASSICMAC_CODE_CORPUS): none throws, and the annotations
// match independent counts [Verified: the samples named in each test].
public class CodeListingCorpusTests
{
    private static readonly FourCC Code = FourCC.FromString("CODE");

    private static CodeApplication App(string name, long length, out ResourceFork fork)
    {
        fork = ResourceFork.Read(CodeCorpus.Require(name, length));
        return CodeApplication.Read(fork, new List<Diagnostic>());
    }

    // Every segment lists, and every instruction byte of a readable segment is either an instruction or data.
    private static Dictionary<short, CodeListing> All(CodeApplication app)
    {
        var listings = new Dictionary<short, CodeListing>();
        foreach (var segment in app.Segments)
        {
            var listing = CodeListing.ForSegment(app, segment.Id);
            Assert.False(string.IsNullOrEmpty(listing.Text));
            listings[segment.Id] = listing;
        }
        return listings;
    }

    [Fact]
    public void ResEdit()
    {
        var app = App("ResEdit.rsrc", 670639, out _);
        var listings = All(app);
        Assert.Equal(25, listings.Count);
        // All 145 jsr n(A5) of CODE 1 reach a segment through the jump table.
        var code1 = listings[1];
        var data = app.FindSegment(1)!.Data;
        var jsrs = code1.References.Where(r => r.Kind == CodeReferenceKind.JumpTable
            && M68kDisassembler.Decode(data, (int)r.Offset).Mnemonic == "jsr").ToList();
        Assert.Equal(145, jsrs.Count);
        Assert.All(jsrs, r => Assert.StartsWith("CODE ", r.Text, StringComparison.Ordinal));
        Assert.All(code1.References.Where(r => r.Kind == CodeReferenceKind.JumpTable),
            r => Assert.StartsWith("CODE ", r.Text, StringComparison.Ordinal));
        Assert.Contains("; Entry: CODE 66:+$10\n; Original entry: CODE 1:+$3638\n", code1.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Realmz()
    {
        var app = App("Realmz 7.1.2", 576955, out _);
        var listing = Assert.Single(All(app)).Value;
        // MacsBug names label functions, read off CODE 1 by hand: ICLOSE's name ends at $8426, where MOT32's routine
        // (link a6; unlk a6; rts; 'MOT32   ') starts.
        Assert.Contains(listing.Functions, f => f.Name == "sqrt");
        Assert.Contains(listing.Functions, f => f.Name == "cos");
        Assert.Contains(listing.Functions, f => f.Name == "MADTICKR");
        Assert.Contains(listing.Functions, f => f.Offset == 0x8426 && f.Name == "MOT32");
        Assert.Contains("MOT32:\n", listing.Text, StringComparison.Ordinal);
        Assert.Contains("; MacsBug name MOT32;", listing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void QDHarness()
    {
        All(App("QDHarness.APPL", 117059, out _));
        var current = CodeCorpus.Find("QDHarness.APPL", 121903);
        if (current is not null)
        {
            All(CodeApplication.Read(ResourceFork.Read(File.ReadAllBytes(current)), new List<Diagnostic>()));
        }
    }

    [Fact]
    public void Disk_Copy_6_1_2()
    {
        var listings = All(App("DC612", 434333, out _));
        Assert.Equal(30, listings.Count);
        Assert.Contains(listings.Values, l => l.References.Any(r => r.Kind == CodeReferenceKind.Relocation));
    }

    private static CodeListing Fragment(string path)
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(File.ReadAllBytes(path), diagnostics);
        var listing = CodeListing.ForFragment(pef, Path.GetFileName(path));
        Assert.False(string.IsNullOrEmpty(listing.Text));
        return listing;
    }

    [Theory]
    [InlineData("NQD.pef", 272110, 173)]
    [InlineData("DC612.pef", 288984, 440)]
    [InlineData("DC65.pef", 907621, 686)]
    public void Fragment_glue_is_named(string name, long length, int glue)
    {
        CodeCorpus.Require(name, length);
        var listing = Fragment(CodeCorpus.Find(name, length)!);
        Assert.Equal(glue, listing.Functions.Count(f => f.Source == CodeFunctionSource.Glue));
        Assert.DoesNotContain(listing.References, r => r.Kind == CodeReferenceKind.Glue && r.Text.StartsWith('?'));
    }

    [Fact]
    public void Every_fragment_lists()
    {
        CodeCorpus.Require("NQD.pef", 272110);
        int count = 0;
        foreach (var path in CodeCorpus.Files("*.pef"))
        {
            Fragment(path);
            count++;
        }
        Assert.True(count >= 4);
    }

    // The Mac OS 9 System's 68k, fat and native code resources.
    [Fact]
    public void System_code_resources()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc"));
        string[] types =
        [
            "CDEF", "WDEF", "MDEF", "LDEF", "MBDF", "DRVR", "PACK", "INIT", "ptch", "PTCH", "proc", "sift", "snth", "dcmp",
            "ADBS", "boot", "AINI", "ltlk", "lmgr", "osl ", "ncod", "nlib", "ndrv", "nift", "FKEY", "ncmp", "expt", "nsrd",
        ];
        int listed = 0, fragments = 0;
        foreach (var resource in fork.Resources.Where(r => types.Contains(r.Type.ToString())))
        {
            var data = ResourceDecompression.Default.GetData(resource, fork, null, new List<Diagnostic>());
            if (CompressedResourceHeader.HasSignature(data))
            {
                continue;
            }

            var listing = CodeListing.ForCodeResource(resource.Type, resource.Id, data, fork);
            Assert.False(string.IsNullOrEmpty(listing.Text));
            listed++;
            fragments += listing.Fragments.Count;
        }
        Assert.True(listed > 100, $"{listed} listed");
        // The 41 fat descriptors' fragments (CDEF 29, WDEF 7, LDEF, MBDF, MDEF, expt, nsrd).
        Assert.Equal(41, fragments);
    }
}
