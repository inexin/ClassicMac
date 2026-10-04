using TrapTables;

// Writes the trap, selector and low-memory tables of ClassicMac.Code's disassembler from Multiversal Interfaces
// (https://github.com/autc04/multiversal, MIT): TrapTable.g.cs, SelectorTable.g.cs and LowMemoryTable.g.cs.
// Usage: dotnet run --project tools/TrapTables <multiversal checkout> [output directory]
// The output directory defaults to src/ClassicMac.Code/Disassembly. The tables are checked in; rerun this tool only
// when Multiversal or the supplements (Supplements.cs) change.
if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: TrapTables <multiversal checkout> [output directory]");
    return 1;
}
string outDir = args.Length > 1 ? args[1] : Path.Combine("src", "ClassicMac.Code", "Disassembly");
Console.WriteLine(TrapTablesGenerator.Generate(args[0], outDir));
return 0;
