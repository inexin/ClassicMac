using ClassicMac.Code.Disassembly;
using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// The PowerPC disassembler and the traceback-table scan over real fragments (CLASSICMAC_CODE_CORPUS). The counts and
// texts were matched word for word against an independent disassembler: every word decodes the same, apart from
// extended-mnemonic spelling (subi for addi with a negative immediate) and, in data, BO values with a z bit set and a
// cmplwi with reserved bit 9 set [Verified: the samples named in each test].
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

    // One instruction per word; every function the traceback tables locate starts with a valid instruction.
    private static IReadOnlyList<PpcInstruction> Disassemble(byte[] code, IReadOnlyList<TracebackTable> tables)
    {
        var instructions = PpcDisassembler.Disassemble(code, 0).ToList();
        Assert.Equal(code.Length / 4, instructions.Count);
        foreach (var table in tables.Where(t => t.FunctionStart is not null))
            Assert.True(instructions[table.FunctionStart!.Value / 4].IsValid, $"function at 0x{table.FunctionStart:X}");
        return instructions;
    }

    private static IReadOnlyList<TracebackTable> Tables(byte[] code)
    {
        var diagnostics = new List<Diagnostic>();
        var tables = TracebackTable.Find(code, diagnostics);
        Assert.Empty(diagnostics);
        return tables;
    }

    private static void Counts(IReadOnlyList<PpcInstruction> instructions, int words, int longs, int blr, int bl, int returns)
    {
        Assert.Equal(words, instructions.Count);
        Assert.Equal(longs, instructions.Count(i => !i.IsValid));
        Assert.Equal(blr, instructions.Count(i => i.Text == "blr"));
        Assert.Equal(bl, instructions.Count(i => i.Mnemonic == "bl"));
        Assert.Equal(returns, instructions.Count(i => i.IsReturn));
    }

    private static void Text(IReadOnlyList<PpcInstruction> instructions, int offset, string expected) =>
        Assert.Equal(expected, instructions[offset / 4].Text);

    // Mac OS 9's QuickDraw: no traceback tables, and every word decodes.
    [Fact]
    public void NQD()
    {
        var code = CodeSection("NQD.pef", 272110);
        var tables = Tables(code);
        Assert.Empty(tables);
        var instructions = Disassemble(code, tables);
        Counts(instructions, words: 63812, longs: 0, blr: 1579, bl: 2267, returns: 1713);
        Text(instructions, 0x00, "mflr r0");
        Text(instructions, 0x04, "stw r31,-4(r1)");
        Text(instructions, 0x0C, "stwu r1,-64(r1)");
        Text(instructions, 0x10, "li r12,0");
        Text(instructions, 0x14, "mr r31,r3");
        Text(instructions, 0x18, "sth r12,3438(0)");
        Text(instructions, 0x430, "rlwimi r4,r9,8,0,15");
    }

    // Disk Copy 6.1.2's data-fork fragment: one traceback table, after the library routine __uitrunc.
    [Fact]
    public void Disk_Copy_6_1_2()
    {
        var code = CodeSection("DC612.pef", 288984);
        var tables = Tables(code);
        var table = Assert.Single(tables);
        Assert.Equal(("__uitrunc", 0x3B064, 0x68u, (int?)0x3AFFC, 31),
            (table.Name, table.Offset, table.TbOffset, table.FunctionStart, table.Length));
        Assert.Equal(1, table.FloatParameterCount);
        Assert.Equal(0xC0000000u, table.ParameterInfo);
        var instructions = Disassemble(code, tables);
        Counts(instructions, words: 69229, longs: 4, blr: 704, bl: 5494, returns: 706);
        Text(instructions, 0x3AFF8, "blr");
        Text(instructions, 0x3AFFC, "stwu r1,-64(r1)");
        Text(instructions, 0x3B05C, "mtlr r12");
        Text(instructions, 0x3B060, "blr");
        Text(instructions, 0x3B064, ".long 0x0");
        Text(instructions, 0x3B068, ".long 0x2241");
        Text(instructions, 0x178, "srawi r6,r6,16");
        Text(instructions, 0x1950, "cntlzw r5,r5");
    }

    // Disk Copy 6.5: 1,619 named functions; its code section carries string data, which is where the .longs are.
    [Fact]
    public void Disk_Copy_6_5()
    {
        var code = CodeSection("DC65.pef", 907621);
        var tables = Tables(code);
        Assert.Equal(1619, tables.Count);
        Assert.All(tables, t => Assert.NotNull(t.Name));
        Assert.All(tables, t => Assert.NotNull(t.FunctionStart));
        Assert.Equal(".TradHighestUnitNumber", tables[0].Name);
        Assert.Equal(0x1C, tables[0].FunctionStart);
        Assert.Equal(0x28u, tables[0].TbOffset);
        Assert.Equal(0x44, tables[0].Offset);
        Assert.Equal((".HandleBurn", 0xB9AB8, (int?)0xB98BC), (tables[^1].Name, tables[^1].Offset, tables[^1].FunctionStart));
        var instructions = Disassemble(code, tables);
        Counts(instructions, words: 218214, longs: 9451, blr: 2045, bl: 17179, returns: 2069);
        Text(instructions, 0x1C, "mflr r0");
        Text(instructions, 0x28, "bl 0xCE9DC");
        Text(instructions, 0x30, "subi r3,r3,1");
        Text(instructions, 0x7C, "stwu r1,-336(r1)");
        Text(instructions, 0x84, "stb r4,367(r1)");
        Text(instructions, 0xC0640, "mfcr r23");
    }
}
