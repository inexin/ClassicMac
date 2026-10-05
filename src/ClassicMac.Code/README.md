# ClassicMac.Code

Readers for classic Mac OS executable code, and 68k and PowerPC disassemblers.

- **PowerPC** (`ClassicMac.Code.Ppc`): `PefContainer` (sections, pattern-initialised data, the loader's imports,
  exports and relocations, transition vectors), `PefRelocator`, traceback tables, `Cfrg` code fragment resources.
- **68k** (`ClassicMac.Code.M68k`): `CodeApplication` (`CODE` 0, the jump table, segments, the entry point and the
  build tool that made it: MPW, THINK, CodeWarrior, Retro68), far relocations, A5 data initialisers, and the headers
  of code resources, drivers, packages, components and routine descriptors.
- **Disassembly** (`ClassicMac.Code.Disassembly`): `M68kDisassembler` and `PpcDisassembler` (ported from
  resource_dasm), trap, selector and low-memory names from Multiversal Interfaces, MacsBug names, code maps that find
  functions, switch tables and glue, and `CodeListing`: a segment, code resource or fragment as `.s` text with its
  functions and references.

```csharp
var diagnostics = new List<Diagnostic>();
var fragment = PefContainer.Read(dataFork, diagnostics);
Console.Write(CodeListing.ForFragment(fragment, "MyApp").Text);

var application = CodeApplication.Read(fork, diagnostics);
Console.Write(CodeListing.ForSegment(application, 1).Text);
```

The formats: [code](https://github.com/inexin/ClassicMac/tree/main/docs/formats/code),
[disassembly listings](https://github.com/inexin/ClassicMac/blob/main/docs/formats/output/disassembly.md).

Part of [ClassicMac](https://github.com/inexin/ClassicMac). MIT. The disassemblers keep resource_dasm's notice in
THIRD-PARTY-NOTICES.md.
