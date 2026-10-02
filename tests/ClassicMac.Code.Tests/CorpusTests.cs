using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests;

// Real fragments and 'cfrg' resources (CLASSICMAC_CODE_CORPUS), checked against numbers counted by an independent
// reader [Verified: the samples named in each test].
public class CorpusTests
{
    private static readonly FourCC CfrgType = FourCC.FromString("cfrg");

    // Reads a fragment and checks it the way the Code Fragment Manager would use it: every section image builds to
    // its length, every relocation applies inside its section, every export is found through the hash table.
    private static PefContainer Verify(ReadOnlyMemory<byte> data, string what)
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(data, diagnostics);
        for (int i = 0; i < pef.Sections.Count; i++)
            if (pef.Sections[i].IsInstantiable)
                Assert.Equal(pef.Sections[i].TotalLength, (uint)pef.GetImage(i, diagnostics).Length);
        var addresses = pef.Sections.Select((_, i) => 0x10000000u * (uint)(i + 1)).ToList();
        var instance = pef.Instantiate(addresses, i => 0xF0000000u + 4u * (uint)i, diagnostics);
        Assert.True(diagnostics.Count == 0, $"{what}: {string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message))}");
        foreach (var fixup in instance.Fixups)
            Assert.InRange(fixup.Offset, 0, pef.Sections[fixup.Section].TotalLength - 4);
        if (pef.Loader is { } loader)
            foreach (var export in loader.Exports)
                Assert.Same(export, loader.FindExport(export.Name));
        return pef;
    }

    private static Dictionary<string, int> OpCounts(PefContainer pef) =>
        pef.Loader!.RelocationHeaders.SelectMany(h => PefRelocator.Decode(h.Instructions, []))
            .GroupBy(i => i.Mnemonic).ToDictionary(g => g.Key, g => g.Count());

    private static string OpString(PefContainer pef) =>
        string.Join(",", OpCounts(pef).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    private static uint TocBase(PefContainer pef, PefEntryPoint? entry) => pef.GetTransitionVector(entry, [])!.Value.TocOffset;

    [Fact]
    public void NQD()
    {
        var pef = Verify(CodeCorpus.Require("NQD.pef", 272110), "NQD.pef");
        Assert.Equal([PefSectionKind.Code, PefSectionKind.PatternInitData, PefSectionKind.Loader], pef.Sections.Select(s => s.Kind));
        var loader = pef.Loader!;
        Assert.Equal(199, loader.ImportedSymbols.Count);
        Assert.Equal(["QuickdrawLib", "NQDResidentCursor", "PrivateInterfaceLib", "InterfaceLib"], loader.ImportedLibraries.Select(l => l.Name));
        Assert.Equal(269, loader.Exports.Count);
        Assert.Equal(239, loader.Exports.Count(e => !e.IsReexport && e.Class == PefSymbolClass.TVector));
        Assert.Equal(4, loader.Exports.Count(e => !e.IsReexport && e.Class == PefSymbolClass.Data));
        Assert.Equal(26, loader.Exports.Count(e => e.IsReexport));
        foreach (var reexport in loader.Exports.Where(e => e.IsReexport))
            Assert.Equal(reexport.Name, loader.ReexportedImport(reexport)!.Name);
        Assert.Equal(5, loader.ExportHashTablePower);
        Assert.Null(loader.Main);
        Assert.Equal(new PefEntryPoint(1, 0x1470), loader.Init);
        Assert.Null(loader.Term);
        Assert.Equal(1715, pef.GetFixups([]).Count);
        Assert.Equal(0x87Cu, TocBase(pef, loader.Init));
        var init = pef.GetTransitionVector(loader.Init, [])!.Value;
        Assert.Equal((0, 1), (init.CodeSection, init.TocSection));
        foreach (var export in loader.Exports.Where(e => e.Class == PefSymbolClass.TVector && !e.IsReexport))
            Assert.Equal(0, pef.GetTransitionVector(export.SectionIndex, export.Value, [])!.Value.CodeSection);
    }

    [Fact]
    public void NQD_alternative_build()
    {
        var pef = Verify(CodeCorpus.Require("NQD_altbuild.pef", 275574), "NQD_altbuild.pef");
        Assert.Equal(200, pef.Loader!.ImportedSymbols.Count);
        Assert.Equal(269, pef.Loader.Exports.Count);
        Assert.Equal(new PefEntryPoint(1, 0x1478), pef.Loader.Init);
    }

    [Fact]
    public void FontManager()
    {
        var pef = Verify(CodeCorpus.Require("FontManager.pef", 196388), "FontManager.pef");
        var loader = pef.Loader!;
        Assert.Equal(114, loader.Exports.Count);
        Assert.Equal(231, loader.ImportedSymbols.Count);
        Assert.Equal(14, loader.ImportedLibraries.Count);
        Assert.Equal(4, loader.ExportHashTablePower);
        Assert.Equal(new PefEntryPoint(1, 3336), loader.Init);
        Assert.Equal(1312, pef.GetFixups([]).Count);
    }

    [Fact]
    public void Disk_Copy_6_1_2()
    {
        var pef = Verify(CodeCorpus.Require("DC612.pef", 288984), "DC612.pef");
        var loader = pef.Loader!;
        Assert.Equal(new PefEntryPoint(1, 0xBB8), loader.Main);
        Assert.Null(loader.Init);
        Assert.Null(loader.Term);
        Assert.Equal(446, loader.ImportedSymbols.Count);
        Assert.Equal(["InterfaceLib", "StdCLib", "MathLib", "ObjectSupportLib", "AOCELib", "SpeechLib", "DragLib", "AppleScriptLib"],
            loader.ImportedLibraries.Select(l => l.Name));
        Assert.Empty(loader.Exports);
        Assert.Equal(871, pef.GetFixups([]).Count);
    }

    [Fact]
    public void Disk_Copy_6_5()
    {
        var pef = Verify(CodeCorpus.Require("DC65.pef", 907621), "DC65.pef");
        var loader = pef.Loader!;
        Assert.Equal(new PefEntryPoint(1, 0x22D0), loader.Main);
        Assert.Equal(688, loader.ImportedSymbols.Count);
        Assert.Equal(14, loader.ImportedLibraries.Count);
        Assert.Empty(loader.Exports);
        Assert.Equal(2921, pef.GetFixups([]).Count);
        // CodeWarrior centres the TOC: the base is 0x8000 into the data section.
        Assert.Equal(0x8000u, TocBase(pef, loader.Main));
    }

    [Fact]
    public void Every_Mac_OS_9_2_2_fragment_verifies_and_matches_its_facts()
    {
        if (CodeCorpus.Roots.Count == 0) Assert.Skip("Set CLASSICMAC_CODE_CORPUS to run this.");
        int checkedCount = 0;
        foreach (var facts in FragmentFacts.MacOS922)
        {
            var path = CodeCorpus.Find(facts.Name, facts.Length);
            if (path is null) continue;
            var pef = Verify(File.ReadAllBytes(path), facts.Name);
            var loader = pef.Loader!;
            Assert.Equal(facts.Imports, loader.ImportedSymbols.Count);
            Assert.Equal(facts.Libraries, loader.ImportedLibraries.Count);
            Assert.Equal(facts.Exports, loader.Exports.Count);
            Assert.Equal(facts.Reexports, loader.Exports.Count(e => e.IsReexport));
            Assert.Equal(facts.HashPower, loader.ExportHashTablePower);
            Assert.Equal(facts.Main, loader.Main);
            Assert.Equal(facts.Init, loader.Init);
            Assert.Equal(facts.Term, loader.Term);
            Assert.Equal(facts.Fixups, pef.GetFixups([]).Count);
            Assert.Equal(facts.RelocationOps, OpString(pef));
            checkedCount++;
        }
        if (checkedCount == 0) Assert.Skip("No Mac OS 9.2.2 fragment is in the code corpus.");
        Assert.Equal(FragmentFacts.MacOS922.Length, checkedCount);
    }

    [Fact]
    public void Every_corpus_PEF_verifies()
    {
        if (CodeCorpus.Roots.Count == 0) Assert.Skip("Set CLASSICMAC_CODE_CORPUS to run this.");
        var files = CodeCorpus.Files("*.pef").ToList();
        if (files.Count == 0) Assert.Skip("No .pef file is in the code corpus.");
        foreach (var file in files)
            Verify(File.ReadAllBytes(file), file);
    }

    // The 'cfrg' resources of the Mac OS 9 System file: resource ID, members, extensions.
    private static readonly (short Id, int Members, int Extensions)[] SystemCfrgs =
    [
        (0, 10, 9), (1, 12, 0), (4, 13, 0), (16, 15, 0), (25, 21, 6), (34, 18, 3), (49, 15, 0), (52, 22, 0), (55, 14, 0),
        (61, 6, 4), (64, 15, 0), (70, 1, 0),
    ];

    [Fact]
    public void Mac_OS_9_System_cfrg_members_land_on_fragments()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var dataFork = CodeCorpus.Require("MacOS9_System_datafork.bin", 6284692);
        var cfrgs = fork.Resources.Where(r => r.Type == CfrgType).OrderBy(r => r.Id).ToList();
        Assert.Equal(SystemCfrgs.Select(c => c.Id), cfrgs.Select(r => r.Id));
        int members = 0;
        foreach (var (resource, expected) in cfrgs.Zip(SystemCfrgs))
        {
            var diagnostics = new List<Diagnostic>();
            var cfrg = Cfrg.Read(resource.GetData(), diagnostics);
            Assert.Empty(diagnostics);
            Assert.Equal(expected.Members, cfrg.Members.Count);
            Assert.Equal(expected.Extensions, cfrg.Members.Sum(m => m.Extensions.Count));
            foreach (var member in cfrg.Members)
            {
                Assert.Equal(CfrgWhere.DataFork, member.Where);
                Assert.Equal(CfrgUsage.ImportLibrary, member.Usage);
                Assert.Equal(FourCC.FromString("pwpc"), member.Architecture);
                foreach (var extension in member.Extensions.Where(e => e.Kind == CfrgExtension.SearchKind))
                    Assert.NotNull(member.Search);
                Assert.True(PefContainer.IsPef(dataFork.AsSpan((int)member.Offset)), $"{member.Name} at 0x{member.Offset:X}");
                int length = member.Length == 0 ? dataFork.Length - (int)member.Offset : (int)member.Length;
                var pef = Verify(dataFork.AsMemory((int)member.Offset, length), member.Name);
                Assert.Equal(member.CurrentVersion, pef.CurrentVersion);
            }
            members += cfrg.Members.Count;
        }
        Assert.Equal(162, members);
    }

    [Fact]
    public void Mac_OS_9_System_cfrg_1_starts_with_Resources()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var cfrg = Cfrg.Read(fork.Find(CfrgType, 1)!.GetData(), []);
        var first = cfrg.Members[0];
        Assert.Equal(("Resources", 0x85E10u, 0xCAC0u), (first.Name, first.Offset, first.Length));
    }

    [Fact]
    public void Disk_Copy_6_1_2_cfrg_names_its_data_fork()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("DC612"));
        var cfrg = Cfrg.Read(fork.Find(CfrgType, 0)!.GetData(), []);
        var member = Assert.Single(cfrg.Members);
        Assert.Equal(("DiskCopy.PPC", CfrgUsage.Application, CfrgWhere.DataFork, 0u, 0u),
            (member.Name, member.Usage, member.Where, member.Offset, member.Length));
    }
}
