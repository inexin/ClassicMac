using ClassicMac.Code.Disassembly;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests.Disassembly;

// Cross-TOC glue in real fragments (CLASSICMAC_CODE_CORPUS): every stub found is named by the import relocation of its
// TOC slot; the counts match an independent scan [Verified: the samples named in each test].
public class PpcGlueCorpusTests
{
    private static PpcFragmentMap Map(string name, long length)
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(CodeCorpus.Require(name, length), diagnostics);
        var map = PpcFragmentMap.Build(pef, diagnostics);
        Assert.Empty(diagnostics);
        return map;
    }

    private static (int Stubs, int Named) Count(PpcFragmentMap map) =>
        (map.Glue.Count, map.Glue.Values.Count(v => !v.StartsWith('?')));

    [Fact]
    public void NQD()
    {
        var map = Map("NQD.pef", 272110);
        Assert.Equal((173, 173), Count(map));
        Assert.Contains("InterfaceLib::InitGraf", map.Glue.Values);
        Assert.Equal((1, 0x87Cu), (map.TocSection, map.TocBase));
    }

    [Fact]
    public void Disk_Copy_6_1_2() => Assert.Equal((440, 440), Count(Map("DC612.pef", 288984)));

    [Fact]
    public void Disk_Copy_6_5()
    {
        var map = Map("DC65.pef", 907621);
        Assert.Equal((686, 686), Count(map));
        Assert.Equal(0x8000u, map.TocBase);
        Assert.Equal(".TradHighestUnitNumber", map.Functions[(0, 0x1C)].Name);
    }
}
