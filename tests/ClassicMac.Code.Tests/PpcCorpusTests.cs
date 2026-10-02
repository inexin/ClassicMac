using ClassicMac.Code.Disassembly;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// The PowerPC disassembler and the traceback-table scan over real fragments (CLASSICMAC_CODE_CORPUS), checked against
// numbers counted by an independent reader [Verified: the samples named in each test].
public class PpcCorpusTests
{
    private static byte[] CodeSection(string name, long length)
    {
        var diagnostics = new List<Diagnostic>();
        var pef = PefContainer.Read(CodeCorpus.Require(name, length), diagnostics);
        int index = pef.Sections.ToList().FindIndex(s => s.Kind == PefSectionKind.Code);
        var image = pef.GetImage(index, diagnostics).ToArray();
        Assert.Empty(diagnostics);
        return image;
    }

    // Every word decodes, and every function the traceback tables locate starts with a valid instruction and ends
    // with the blr before its table.
    private static IReadOnlyList<PpcInstruction> Disassemble(byte[] code, IReadOnlyList<TracebackTable> tables)
    {
        var instructions = PpcDisassembler.Disassemble(code, 0).ToList();
        Assert.Equal(code.Length / 4, instructions.Count);
        foreach (var table in tables.Where(t => t.FunctionStart is not null))
        {
            Assert.True(instructions[table.FunctionStart!.Value / 4].IsValid, $"function at 0x{table.FunctionStart:X}");
            var last = instructions[table.Offset / 4 - 1];
            Assert.Equal("blr", last.Text);
            Assert.True(last.IsReturn);
        }
        return instructions;
    }

    private static IReadOnlyList<TracebackTable> Tables(byte[] code)
    {
        var diagnostics = new List<Diagnostic>();
        var tables = TracebackTable.Find(code, diagnostics);
        Assert.Empty(diagnostics);
        return tables;
    }

    [Fact]
    public void NQD()
    {
        var code = CodeSection("NQD.pef", 272110);
        var tables = Tables(code);
        Assert.DoesNotContain(tables, t => t.Name is not null);
        Disassemble(code, tables);
    }

    [Fact]
    public void Disk_Copy_6_1_2()
    {
        var code = CodeSection("DC612.pef", 288984);
        var tables = Tables(code);
        Assert.Single(tables, t => t.Name is not null);
        Disassemble(code, tables);
    }

    [Fact]
    public void Disk_Copy_6_5()
    {
        var code = CodeSection("DC65.pef", 907621);
        var tables = Tables(code);
        var named = tables.Where(t => t.Name is not null).ToList();
        Assert.Equal(1619, named.Count);
        Assert.Equal(".TradHighestUnitNumber", named[0].Name);
        Assert.Equal(0x1C, named[0].FunctionStart);
        Assert.Equal(0x28u, named[0].TbOffset);
        Assert.Equal(0x44, named[0].Offset);
        Disassemble(code, tables);
    }
}
