using ClassicMac.Code.Disassembly;
using ClassicMac.Code.M68k;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests.M68k;

// The Mac OS 9 System file's 68k and fat code resources (CLASSICMAC_CODE_CORPUS), checked against numbers counted by an
// independent reader [Verified: Mac OS 9 System].
public class CodeResourceCorpusTests
{
    private static ResourceFork System() => ResourceFork.Read(CodeCorpus.Require("MacOS9_System.rsrc", 8021864));

    private static ReadOnlyMemory<byte> Data(ResourceFork fork, string type, short id) =>
        fork.Find(FourCC.FromString(type), id)!.GetData();

    [Fact]
    public void CDEF_0_is_a_standard_header_then_a_routine_descriptor_and_a_PEF()
    {
        var data = Data(System(), "CDEF", 0);
        var diagnostics = new List<Diagnostic>();
        var header = CodeResourceHeader.Read(data, FourCC.FromString("CDEF"), diagnostics)!;
        Assert.Equal((CodeResourceBranch.BraShort, 0x0C, (short)0, (ushort)0x0B), (header.Branch, header.BranchTarget, header.Id, header.Version));
        var descriptor = RoutineDescriptor.Read(data, diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.Equal((0x0C, (byte)7, (byte)0x20), (descriptor.Offset, descriptor.Version, descriptor.Flags));
        var routine = Assert.Single(descriptor.Routines);
        Assert.Equal((0x3BB0u, true, true, 0x2CL), (routine.ProcInfo, routine.IsPowerPC, routine.IsRelative, routine.TargetOffset));
        Assert.Equal(new PefEntryPoint(1, 0x28), routine.Pef!.Loader!.Main);
    }

    [Fact]
    public void LDEF_0_is_a_routine_descriptor_at_0()
    {
        var descriptor = RoutineDescriptor.Read(Data(System(), "LDEF", 0), [])!;
        Assert.Equal((0, (byte)0), (descriptor.Offset, descriptor.Flags));
        Assert.Equal(0x20L, Assert.Single(descriptor.Routines).TargetOffset);
        Assert.NotNull(descriptor.Routines[0].Pef);
    }

    [Fact]
    public void The_System_has_41_fat_descriptors_each_with_a_PEF()
    {
        // 29 + 7 + 5 × 1 = 41 (the count of 40 in the expected numbers was a slip; its own per-type list sums to 41).
        var fork = System();
        var diagnostics = new List<Diagnostic>();
        var descriptors = fork.Resources
            .Select(r => (Resource: r, Descriptor: RoutineDescriptor.Read(ResourceDecompression.Default.GetData(r, fork), diagnostics)))
            .Where(d => d.Descriptor is not null).ToList();
        Assert.Empty(diagnostics);
        Assert.Equal("CDEF=29 LDEF=1 MBDF=1 MDEF=1 WDEF=7 expt=1 nsrd=1", string.Join(" ", descriptors
            .GroupBy(d => d.Resource.Type.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}={g.Count()}")));
        Assert.All(descriptors, d => Assert.All(d.Descriptor!.Routines, r => Assert.NotNull(r.Pef)));
    }

    [Fact]
    public void The_System_DRVRs_and_the_non_standard_ATADisk()
    {
        var fork = System();
        var drivers = fork.OfType(FourCC.FromString("DRVR")).ToList();
        Assert.Equal(27, drivers.Count);
        Assert.Equal(2, drivers.Count(r => (r.Attributes & ResourceAttributes.Compressed) != 0));
        var headers = drivers.Select(r =>
        {
            var data = ResourceDecompression.Default.GetData(r, fork);
            return (Resource: r, Header: DriverHeader.Read(data, [])!);
        }).ToList();
        var nonStandard = headers.Where(h => !h.Header.IsStandard).Select(h => h.Resource.Id).Order().ToList();
        Assert.Equal([(short)-20268, (short)-20267, (short)53], nonStandard);
        Assert.All(nonStandard, id => Assert.Equal(".ATADisk", fork.Find(FourCC.FromString("DRVR"), id)!.Name!.Value.ToMacRoman()));
        var renamed = headers.Where(h => h.Header.IsStandard && h.Header.Name != h.Resource.Name?.ToMacRoman()).ToList();
        var (resource, header) = Assert.Single(renamed);
        Assert.Equal((".Display_Video_Apple_Planaria", ".Display_Video_Apple_Civic"), (resource.Name!.Value.ToMacRoman(), header.Name));
    }

    [Fact]
    public void Component_platforms_name_68k_code_and_PowerPC_fragments()
    {
        var fork = System();
        var diagnostics = new List<Diagnostic>();
        var things = fork.OfType(FourCC.FromString("thng")).Select(r => ComponentResource.Read(r.GetData(), diagnostics)).ToList();
        Assert.Empty(diagnostics);
        Assert.Equal(47, things.Count);
        var platforms = things.SelectMany(t => t.Platforms).ToList();
        Assert.Contains(platforms, p => p.PlatformType == ComponentPlatform.M68k && p.Code.Type == FourCC.FromString("sift"));
        Assert.Contains(platforms, p => p.PlatformType == ComponentPlatform.PowerPC && p.Code.Type == FourCC.FromString("nift"));
        foreach (var platform in platforms)
        {
            var code = fork.Find(platform.Code.Type, platform.Code.Id);
            Assert.NotNull(code);
            // 68k code ('sift', or a 68k 'vdig') is raw code; PowerPC code ('nift', 'cdek', 'vdig', 'scal', 'dcod') a PEF.
            Assert.Contains(platform.PlatformType, (short[])[ComponentPlatform.M68k, ComponentPlatform.PowerPC]);
            Assert.Equal(platform.PlatformType == ComponentPlatform.PowerPC,
                PefContainer.IsPef(ResourceDecompression.Default.GetData(code!, fork).Span));
        }
    }

    [Fact]
    public void The_A9FF_packages_read_their_dispatch_tables()
    {
        var fork = System();
        var diagnostics = new List<Diagnostic>();
        var packages = fork.Resources
            .Select(r => (Resource: r, Data: ResourceDecompression.Default.GetData(r, fork)))
            .Select(r => (r.Resource, r.Data, Header: PackageHeader.Read(r.Data, diagnostics)))
            .Where(p => p.Header is not null).ToList();
        Assert.Empty(diagnostics);
        Assert.Equal(14, packages.Count);
        Assert.All(packages, p => Assert.Equal(p.Resource.Type, p.Header!.Type));
        var pack15 = packages.Single(p => p.Resource.Type == FourCC.FromString("PACK") && p.Resource.Id == 15);
        Assert.Equal(7, pack15.Header!.Entries.Count);
        // Selector 0: offset $7E0, the routine at $0A + $7E0 = $7EA: LINK A6,#-4, right after the previous one's UNLK; RTS.
        var select0 = pack15.Header.Entries[0];
        Assert.Equal(((ushort)0x7E0, (long?)0x7EA), (select0.Offset, select0.TargetOffset));
        var reader = new BigEndianReader(pack15.Data);
        Assert.Equal((0x4E5Eu, 0x4E75u, 0x4E56FFFCu), (reader.ReadUInt16At(0x7E6), reader.ReadUInt16At(0x7E8), reader.ReadUInt32At(0x7EA)));

        // Every routine is even, inside the resource and a valid instruction; most are a LINK or follow a return
        // (RTS, RTD or JMP (A0)). Counted from the resource start or from the table, far fewer would.
        int targets = 0, links = 0, afterReturn = 0;
        foreach (var (resource, data, header) in packages)
        {
            var r = new BigEndianReader(data);
            foreach (var entry in header!.Entries)
            {
                if (entry.TargetOffset is not { } target) continue;
                targets++;
                Assert.True(target % 2 == 0 && target < data.Length, $"{resource}: selector {entry.Selector} at {target:X}");
                Assert.False(M68kDisassembler.Decode(data, (int)target).IsInvalid, $"{resource}: selector {entry.Selector} at {target:X}");
                if (r.ReadUInt16At((int)target) == 0x4E56) links++;
                else if (r.ReadUInt16At((int)target - 2) is 0x4E75 or 0x4ED0 || r.ReadUInt16At((int)target - 4) == 0x4E74) afterReturn++;
            }
        }
        Assert.Equal((277, 196, 22), (targets, links, afterReturn));
    }
}
