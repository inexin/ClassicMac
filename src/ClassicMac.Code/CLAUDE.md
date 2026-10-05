# CLAUDE.md: ClassicMac.Code

Guidance for `src/ClassicMac.Code`, its decoders in `ClassicMac.Resources.Decoders/Code` and `tests/ClassicMac.Code.Tests`.
The repository-wide rules are in the root `CLAUDE.md`.

`src/ClassicMac.Code` (on Core and Resources) reads classic Mac code; the specs are `docs/formats/code/*.md` and
`docs/formats/output/disassembly.md`.

- `ClassicMac.Code.Ppc`: `PefContainer` (sections, `PatternData`, `GetImage`, `GetFixups`, transition vectors),
  `PefLoader` (imports, exports and their hash, relocation headers), `PefRelocator`, `TracebackTable`, `Cfrg`.
- `ClassicMac.Code.M68k`: `CodeApplication` (`'CODE'` 0, the jump table, segments, entry point, build model),
  `SegmentHeader`, `FarRelocations`, `MpwA5Init`, `CodeWarriorData`, `Retro68Relocations`, and the code-resource
  headers (`CodeResourceHeader`, `DriverHeader`, `PackageHeader`, `ComponentResource`, `RoutineDescriptor`).
- `ClassicMac.Code.Disassembly`: `M68kDisassembler` and `PpcDisassembler` (ported from resource_dasm), the generated
  `TrapNames`, `SelectorNames` and `LowMemoryGlobals` (`tools/TrapTables`, from Multiversal Interfaces),
  `MacsBugNames`, the code maps (`M68kCodeMap`: descent, switch tables, gap sweep; `PpcFragmentMap`: TOC, glue,
  functions), the annotators, and `CodeListing` (the `.s` text with its `Functions` and `References`).
- `ClassicMac.Resources.Decoders.Code` turns it into exports (`code.segment`, `code.cfrg`, `code.resource`; the main
  file stays the data, `.bin`) and whole-file listings (`CodeExport`, the CLI's `disasm`).
- Corpus tests are gated by `CLASSICMAC_CODE_CORPUS` and assert facts as constants; real binaries and listings are
  never committed.
