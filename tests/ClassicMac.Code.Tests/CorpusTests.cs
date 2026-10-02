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

    // The System file's resources that hold a PEF container from offset 0 ('ncod', 'nlib', 'ndrv', 'nift', 'ntrb',
    // 'fovr', 'vdig', 'cdek', 'sfvr', …): ID, and fixups counted by an independent reader.
    private static readonly Dictionary<string, int> SystemNativeResources = new()
    {
        ["cdek -19131"] = 241, ["cdek -20565"] = 88, ["cdek -20676"] = 61, ["cdek -21003"] = 78, ["dcod -20221"] = 26,
        ["fovr -16400"] = 187, ["fovr -16404"] = 106, ["fovr -20157"] = 664, ["fovr -20186"] = 125, ["fovr 10"] = 33,
        ["fovr 35"] = 172, ["fovr 50"] = 996, ["fovr 51"] = 84, ["fovr 6"] = 1797, ["hqda 1"] = 68, ["ncmp 0"] = 11,
        ["ncmp 2"] = 4, ["ncod 2"] = 5, ["ncod 3"] = 494, ["ndmc -20034"] = 18, ["ndrv -16511"] = 88, ["ndrv -16800"] = 52,
        ["ndrv -20156"] = 468, ["ndrv -20164"] = 312, ["ndrv -20192"] = 3, ["ndrv -20193"] = 3, ["ndrv -20194"] = 3,
        ["ndrv -20195"] = 3, ["ndrv -20413"] = 244, ["ndrv -20535"] = 43, ["ndrv -20921"] = 42, ["ndrv -20963"] = 50,
        ["ndrv 100"] = 372, ["ndrv 101"] = 407, ["ndrv 103"] = 251, ["ndrv 505"] = 58, ["ndrv 99"] = 283,
        ["nift -16557"] = 32, ["nift -16558"] = 32, ["nift -16559"] = 31, ["nift -16561"] = 45, ["nift -16566"] = 32,
        ["nift -16589"] = 31, ["nift -16593"] = 31, ["nift -16594"] = 32, ["nift -16595"] = 33, ["nift -16597"] = 32,
        ["nift -20027"] = 30, ["nift -20489"] = 121, ["nift -20490"] = 131, ["nift -20493"] = 1862, ["nitt 43"] = 743,
        ["nitt 44"] = 145, ["nlib -16399"] = 54, ["nlib -16402"] = 176, ["nlib -16403"] = 39, ["nlib -16410"] = 23,
        ["nlib -16412"] = 55, ["nlib -16415"] = 157, ["nlib -16417"] = 206, ["nlib -16420"] = 94, ["nlib -20216"] = 62,
        ["nlib -20264"] = 33, ["nlib -20536"] = 174, ["nlib -20537"] = 279, ["nlib -20673"] = 493, ["nlib -20696"] = 211,
        ["nlib -20878"] = 88, ["nlib -20918"] = 171, ["nlib -20919"] = 137, ["nlib -20920"] = 95, ["nlib -20933"] = 215,
        ["nlib -20960"] = 18, ["nlib -20987"] = 469, ["nlib -20989"] = 109, ["nlib -20990"] = 194, ["nlib -21145"] = 148,
        ["nlib 11"] = 171, ["nlib 198"] = 175, ["nlib 666"] = 140, ["nlib 8"] = 0, ["nsnd -16500"] = 67,
        ["ntrb -16389"] = 20, ["ntrb -16391"] = 137, ["ntrb -16398"] = 72, ["ntrb -16400"] = 37, ["ntrb -20414"] = 56,
        ["ntrb -20931"] = 10, ["ntrb -20987"] = 24, ["ntrb 11"] = 214, ["ntrb 12"] = 37, ["ntrb 15"] = 92, ["ntrb 17"] = 64,
        ["ntrb 18"] = 32, ["ntrb 19"] = 85, ["ntrb 198"] = 19, ["ntrb 33"] = 80, ["ntrb 42"] = 55, ["ppct 1"] = 47,
        ["pthg -16501"] = 9, ["qtcm -19071"] = 47, ["scal -20223"] = 108, ["sfvr -16401"] = 319, ["sfvr 0"] = 433,
        ["sfvr 1"] = 619, ["sfvr 8"] = 66, ["vdig -16732"] = 109, ["vdig -20152"] = 119, ["vdig -20153"] = 184,
        ["vdig -20219"] = 538, ["vdig -20445"] = 544, ["vdig -20549"] = 575, ["vdig -20754"] = 930, ["vdig -20929"] = 547,
    };

    private static IEnumerable<(string Name, ReadOnlyMemory<byte> Data)> SystemNativePefs(ResourceFork fork) =>
        fork.Resources.Where(r => PefContainer.IsPef(r.GetData().Span)).Select(r => ($"{r.Type} {r.Id}", r.GetData()));

    [Fact]
    public void Mac_OS_9_System_native_PEF_resources_verify()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var found = new Dictionary<string, int>();
        foreach (var (name, data) in SystemNativePefs(fork))
            found[name] = Verify(data, name).GetFixups([]).Count;
        Assert.Equal(SystemNativeResources.OrderBy(kv => kv.Key, StringComparer.Ordinal), found.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(114, found.Count);
        Assert.Equal(21784, found.Values.Sum());
    }

    // 'ncod' 3 holds the only Constant section and the only SECN of the samples: SECN 2 repeated twice by an RPT.
    [Fact]
    public void Mac_OS_9_System_ncod_3_has_a_constant_section_and_SECN()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var pef = Verify(fork.Find(FourCC.FromString("ncod"), 3)!.GetData(), "ncod 3");
        Assert.Equal([PefSectionKind.Code, PefSectionKind.PatternInitData, PefSectionKind.Constant, PefSectionKind.Loader],
            pef.Sections.Select(s => s.Kind));
        Assert.Equal(0x7044u, pef.Sections[0].TotalLength);
        Assert.Equal((0xDA8u, 0xDA4u), (pef.Sections[1].TotalLength, pef.Sections[1].UnpackedLength));
        var constant = pef.Sections[2];
        Assert.Equal((0x41u, PefShareKind.Process, (byte)3), (constant.TotalLength, constant.ShareKind, constant.Alignment));
        var secn = pef.GetFixups([]).Where(f => f.Opcode == PefRelocationOpcode.BySection).ToList();
        Assert.Equal([0x188L, 0x18CL, 0x190L], secn.Select(f => f.Offset));
        Assert.All(secn, f => Assert.Equal((PefFixupTarget.Section, 2), (f.Target, f.TargetIndex)));
        var image = new BigEndianReader(pef.GetImage(secn[0].Section, []));
        Assert.Equal([0u, 0x10u, 0x18u], secn.Select(f => image.ReadUInt32At((int)f.Offset)));
    }

    // 'ntrb' resources are data-only: a pidata section and the loader, with main in section 0.
    [Fact]
    public void Mac_OS_9_System_ntrb_is_data_only()
    {
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var pef = Verify(fork.Find(FourCC.FromString("ntrb"), -20987)!.GetData(), "ntrb -20987");
        Assert.Equal([PefSectionKind.PatternInitData, PefSectionKind.Loader], pef.Sections.Select(s => s.Kind));
        Assert.Equal(new PefEntryPoint(0, 0), pef.Loader!.Main);
        Assert.Equal(PefRelocationOpcode.SetSectD, PefRelocator.Decode(pef.Loader.RelocationHeaders[0].Instructions, [])[0].Opcode);
    }

    // Every container the independent reader counted: Mac OS 9.2.2's 90 fragments, NQD and its other build,
    // FontManager, the two Disk Copies, the System file's data-fork fragments (each offset once) and its native
    // resources.
    [Fact]
    public void All_288_sample_containers_make_117236_fixups()
    {
        if (CodeCorpus.Roots.Count == 0) Assert.Skip("Set CLASSICMAC_CODE_CORPUS to run this.");
        var containers = new List<ReadOnlyMemory<byte>>();
        foreach (var facts in FragmentFacts.MacOS922)
            containers.Add(CodeCorpus.Require(facts.Name, facts.Length));
        foreach (var (name, length) in new[] { ("NQD.pef", 272110L), ("NQD_altbuild.pef", 275574L), ("FontManager.pef", 196388L), ("DC612.pef", 288984L), ("DC65.pef", 907621L) })
            containers.Add(CodeCorpus.Require(name, length));
        var fork = ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));
        var dataFork = CodeCorpus.Require("MacOS9_System_datafork.bin", 6284692);
        var offsets = new HashSet<uint>();
        foreach (var resource in fork.Resources.Where(r => r.Type == CfrgType))
            foreach (var member in Cfrg.Read(resource.GetData(), []).Members)
                if (offsets.Add(member.Offset))
                    containers.Add(dataFork.AsMemory((int)member.Offset, member.Length == 0 ? dataFork.Length - (int)member.Offset : (int)member.Length));
        containers.AddRange(SystemNativePefs(fork).Select(p => p.Data));
        Assert.Equal(288, containers.Count);
        Assert.Equal(117236, containers.Sum(c => Verify(c, "sample").GetFixups([]).Count));
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
