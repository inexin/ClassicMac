# Disassembly listings and the code model

What ClassicMac writes for classic Mac code: an annotated listing (`.s`) of a 68k segment, a code resource or a
PowerPC fragment, and a structured model of it (`.json`). `extract` writes both beside each code resource's data
(`.bin`), `disasm` writes a listing per segment, code resource and fragment of a whole file plus `code.json`, and the
viewer shows the listing. The format is ClassicMac's own: no Mac software reads it. How the code itself is laid out is
in [code-segments.md](../code/code-segments.md), [code-data.md](../code/code-data.md),
[code-resources.md](../code/code-resources.md), [cfrg.md](../code/cfrg.md) and [pef.md](../code/pef.md); this document
covers what ClassicMac adds: the listing's text, the annotations, the names, how the code is told from data, and the
JSON.

| | |
| --- | --- |
| Identified by | A `.s` file whose first line starts `; ` and names the code (`; 'CODE' 1 "Main": 68k segment, near header`); a `.json` beside it; `code.json` in a `disasm` folder |
| ClassicMac | Writes; `ClassicMac.Code.Disassembly` (`CodeListing`, `CodeFunction`, `CodeReference`, `MacsBugNames`, `TrapNames`, `SelectorNames`, `LowMemoryGlobals`), `ClassicMac.Resources.Decoders.Code` (the decoders, `CodeExport`) |
| Verified against | ResEdit 2.1.3 (MPW near model), Realmz 7.1.2 (CodeWarrior), a Retro68 application, Disk Copy 6.1.2 (MPW far model and a data-fork fragment), Disk Copy 6.5, the Mac OS 9 System file and 90 Mac OS 9.2.2 fragments |
| Sources | *M68000 Family Programmer's Reference Manual* (Motorola); *PowerPC Microprocessor Family: The Programming Environments* and *AltiVec Technology Programming Environments Manual* (IBM, Motorola); *Mac OS Runtime Architectures*; *Inside Macintosh* (the Segment Loader, the Trap Manager, each manager's dispatch selectors); *MacsBug Reference and Debugging Guide*; Multiversal Interfaces (trap, selector and low-memory names); resource_dasm (the disassemblers, ported; MIT) |

Contents

1. [Layout](#1-layout)
2. [Reading](#2-reading)
3. [Writing](#3-writing)
4. [Variants](#4-variants)
5. [ClassicMac](#5-classicmac)
6. [Diagnostics](#6-diagnostics)
7. [Verification](#7-verification)
8. [Not covered](#8-not-covered)
9. [References](#9-references)

## 1. Layout

### 1.1 The listing

A listing is UTF-8 text, lines ending in LF, in three parts [ClassicMac]:

1. The header: lines starting `; `, what the code is and what was found about it, then an empty line.
2. The body: label lines (`name:`), instruction lines and data lines, in address order. A label is preceded by an
   empty line, except before the first line of the body.
3. For a fat code resource, the listing of each PowerPC fragment behind its routine descriptor, after an empty line
   (§1.5).

An instruction or data line is:

| Column | Content |
| --- | --- |
| Address | 68k: the offset in the resource, 8 hex digits (`0000001C`). PowerPC: `section:offset`, the offset in the section's image, 8 hex digits (`0:00000018`) |
| Two spaces | |
| Hex | 68k: the instruction's words, 4 hex digits each, separated by spaces, padded to 24 characters. PowerPC: the word, 8 hex digits; data lines up to four words |
| Two spaces | |
| Text | The instruction in Motorola syntax (68k) or IBM syntax (PowerPC), lowercase, hex immediates as `$` (68k) or `0x` (PowerPC) |
| `  ; ` and notes | The notes, separated by `; `: the decoder's operand comment, then the annotations (§1.3) |

In the 68k text, an empty register list (a mask of 0) is written `#0` (`movem.l #0,-(sp)`), and the immediate of an
`fmovem.l` of two or three control registers as one `#n` per register (`fmovem.l #0,#1,fpcr/fpsr`) [ClassicMac].

A 68k data line holds up to 8 bytes as `dc.w $xxxx,$xxxx…` (a byte at an odd offset, or a last odd byte, as
`dc.b $xx`), with what the data is (§1.4) on the run's first line and an ASCII preview (`'Main'`, `.` for bytes outside
`$20`–`$7E`) on every line. A PowerPC data line is `dc.l $xxxxxxxx,…`. An embedded fragment is one line,
`ds.b <length>  ; PowerPC fragment (routine n), listed below`.

### 1.2 The header lines

| Code | Lines, in order |
| --- | --- |
| A segment of an application | `'CODE' n "name": 68k segment, near header` (or `far header`, `too short for its header`, `compressed and not readable`); `Model: MPW near` (`MPW far`, `Retro68`, `CodeWarrior`, `unknown`); `Entry: CODE s:+$off`; `Original entry: CODE s:+$off` (a bootstrap segment, [code-segments.md](../code/code-segments.md)); `Also PowerPC code: 'cfrg' 0`; `Jump-table entries: n` (those in this segment); `Relocations: n` |
| A segment with no application | `'CODE' n "name": 68k segment, near header` |
| `'CODE'` 0 | `'CODE' 0: the jump table`; the body is the table as data |
| A 68k code resource | `'TYPE' n "name": 68k code resource`; then one of `Driver "name": flags $xxxx`, `Package: 'TYPE' n, selectors a to b`, `Standard header: 'TYPE' n, version $xxxx`; then `Routine descriptor at $off: n routines` for a fat resource |
| A PowerPC fragment | `title: PowerPC fragment ('pwpc'), n sections` (the title is the resource, or the `'cfrg'` member's name in quotes); `Section i: Kind, 0xlength bytes "name"`; `Main: s:0xoff -> s:0xoff` (the transition vector, then the code it points to), `Init:`, `Term:`; `TOC base: s:0xoff`; `Imports: n from m libraries`; `Exports: n` |
| A CFM-68K fragment | The fragment lines up to the sections, then `Not PowerPC code: not disassembled` |

### 1.3 Annotations

Each annotation is a `CodeReference`: the instruction's address, a kind, and the text written after `; `.

| Kind | On | Text | Example |
| --- | --- | --- | --- |
| `jumpTable` | `jsr`, `jmp`, `pea`, `lea` with `n(a5)` that is a jump-table entry plus 2 | `CODE s:+$off` and the function's name there when known; `JT i` for an entry that is not an unloaded one | `jsr 42(a5)  ; CODE 1:+$18 Helper` |
| `a5Global` | Any other `n(a5)` | `A5+$n` above A5 (application parameters, the jump table), `A5-$n` below (globals) | `move.w -$1F3A(a5),d0  ; A5-$1F3A` |
| `lowMemory` | An absolute address inside a low-memory global | Its name, `+offset` when inside it | `move.l ($016A).w,d0  ; Ticks` |
| `trap` | An A-line word | Not written: the mnemonic is the trap's name (§2.4) | `_GetResource ,AUTOPOP` |
| `selector` | A dispatcher trap whose selector was found (§2.4) | The routine's name; `'code' name` for `_Gestalt`; `selector $n` when the number has no name | `_Pack7  ; NumToString` |
| `relocation` | An absolute long or long immediate the loader patches (§2.5) | `A5+$n` (with the jump-table entry when it is one), `CODE s+$n` and the function there, `DATA+$n`, `BSS+$n` | `lea ($00000100).l,a0  ; A5+$100` |
| `string` | A PC-relative operand (not `jsr`/`jmp`) at a Pascal or C string | `P'text'` or `C'text'`, cut after 40 characters with `...` | `pea 12(pc)  ; P'Hello'` |
| `call` | A call or unconditional branch to a labelled function | The label | `bsr.w $0034  ; sub_0034` |
| `glue` | A PowerPC call or branch to a cross-TOC glue stub | `library::symbol` | `bl 0x18  ; InterfaceLib::InitGraf` |
| `tocSlot` | `lwz rD,d(r2)` whose slot a relocation fills | `library::symbol` for an import; `s:0xoff` and its label for a section address | `lwz r3,4(r2)  ; 1:0x10` |

[ClassicMac] The forms and texts are ClassicMac's. A `relocation` note replaces the `call` note of the same
instruction.

### 1.4 What data is labelled as

| Note | What |
| --- | --- |
| `segment header`, `code resource header`, `driver header`, `package header`, `jump table` | The header before the code |
| `MacsBug name NAME` | A MacsBug procedure name (§2.3) |
| `literals` | The constants a MacsBug name's literal-size word announces |
| `switch table` | A switch statement's table of offsets (§2.2) |
| `string` | A Pascal or C string a PC-relative operand points at |
| `relocations` | A far segment's A5 or PC relocation list |
| `%A5Init data` | MPW's global-data initializer ([code-data.md](../code/code-data.md)) |
| `routine descriptor` | A Mixed Mode routine descriptor and what follows it |
| `traceback table NAME` | A PowerPC traceback table |
| (none) | Bytes that do not decode, or no path reaches and the sweep could not decode |

### 1.5 Labels

| Label | Function source (`source`) | Where it comes from |
| --- | --- | --- |
| A MacsBug name | `macsBug` | The name after the routine (§2.3) |
| `entry` | `entry` | The application's entry point in its segment; a code resource's start; a standard header's offset 0 |
| `original_entry` | `entry` | A bootstrap segment's saved entry point |
| `main` | `entry` (68k), `main` (PowerPC) | A standard header's branch target; a fragment's main entry point |
| `init`, `term` | `init`, `term` | A fragment's initialization and termination routines |
| `JTn` | `jumpTable` | Jump-table entry n, in its segment |
| `Open`, `Prime`, `Control`, `Status`, `Close` | `driverRoutine` | A driver's routines |
| `selector_n`, `selector_mn` | `packageRoutine` | A package's routine for selector n (`m` for a negative selector) |
| An export's name | `export` | A fragment's exported code or transition vector |
| A traceback name | `traceback` | The name in a traceback table |
| `.symbol` | `glue` | A cross-TOC glue stub calling `symbol` |
| `sub_XXXX` | `call`, `gap` | A call target, or the start of code found in a gap; XXXX the offset in hex, at least 4 digits |

A function has one label. When sources disagree: 68k, a MacsBug name first, then the name given with the entry, then
`sub_XXXX`; PowerPC, an export, then a traceback name, then main/init/term, then glue, then `sub_XXXX` [ClassicMac].

### 1.6 The model (`.json`)

Every model is a JSON object, indented by two spaces, LF line ends, names and enumeration values in camelCase, numbers
as JSON numbers (offsets are decimal there). Shared parts:

| Member | Content |
| --- | --- |
| `functions` | Array, in address order: `offset`, `name`, `source` (§1.5); `section` for PowerPC code |
| `references` | Array, in address order: `offset`, `kind` (§1.3), `text`; `section` for PowerPC code |
| `relocations` | Array: `offset` (of the patched long, in the resource), `base`: `a5`, `segment`, `initializedData`, `uninitializedData` |
| A fragment | `architecture`, `formatVersion`, `currentVersion`, `oldDefVersion`, `oldImpVersion`; `sections` (`index`, `name` when it has one, `kind`, `share`, `totalLength`, `unpackedLength`, `containerLength`, `containerOffset`, `alignment`); `main`, `init`, `term` (`section`, `offset`: the transition vector) when present; `imports` (`library`, `name`, `class`, `weak`); `exports` (`name`, `class`, `section`, `value`) |

The models per output:

| Output | Members |
| --- | --- |
| `'CODE'` 0 | `aboveA5`, `belowA5`, `jumpTableSize`, `jumpTableOffset`; `model` (`mpwNear`, `mpwFar`, `retro68`, `codeWarrior`, `unknown`); `farModel`; `powerPC` (a `'cfrg'` 0 exists); `entry` and, for a bootstrap, `originalEntry` (`segment`, `offset` as the entry gives it, `resourceOffset`); `a5Init` (`segment`, `belowA5Size`, `runs`, `relocations`) for MPW; `codeWarriorData` (`relocations`: `kind`, `count` per list); `dataRelocations` (Retro68's `'RELA'` 0 count); `entries`: `index`, `a5Offset`, `kind` (`nearUnloaded`, `nearLoaded`, `farMarker`, `farUnloaded`, `farLoaded`, `unrecognized`), `segment`, `offset` (unloaded), `resourceOffset` (unloaded), `address` (loaded), `raw` (unrecognized, 16 hex digits) |
| `'CODE'` n | `segment`, `name`; `model` (with an application); `readable`; `header` (`far`, `firstNearOffset`, `nearCount`, and when far `firstFarOffset`, `farCount`, `a5RelocationOffset`, `pcRelocationOffset`; `null` when the segment is too short); `jumpTableEntries` (`index`, `resourceOffset`); `relocations`; `functions`; `references` |
| `'cfrg'` | `version`; `members`: `name`, `architecture`, `updateLevel`, `currentVersion`, `oldDefVersion`, `usage` (`importLibrary`, `application`, `dropIn`, `stubLibrary`, `weakStubLibrary`), `usage1`, `usage2`, `where` (`memory`, `dataFork`, `resource`, `byteStream`, `namedFragment`), `offset`, `length`, `where1`, `where2`, `resourceType` and `resourceId` (where = resource), `search` (`libraryKind`, `qualifiers`), `extensions` (`kind`, `length`) |
| A code resource | `type`, `id`, `format` (`pef` or `68k`). PEF: `fragment`, `functions`, `references`. 68k: `driver` (`name`, `flags`, `delay`, `eventMask`, `menu`, `open`, `prime`, `control`, `status`, `close`), `package` (`type`, `id`, `version`, `flags`, `firstSelector`, `lastSelector`, `entries`: `selector`, `offset`) or `standardHeader` (`type`, `id`, `version`, `flags`, `entry`: the branch target); `routineDescriptor` (`offset`, `version`, `routines`: `procInfo`, `powerPC`, `flags`, `targetOffset` or `procDescriptor`, `pef`); `functions`; `references`; `fragments` (each a fragment with its `functions` and `references`) |
| `code.json` (`disasm`) | `application` (the `'CODE'` 0 model, or `null`); `segments` (`id`, `name`, `file`, `readable`, `functions`); `codeResources` (`type`, `id`, `name`, `format`, `file`, `functions`); `fragments` (`cfrg`: the `'cfrg'` resource's ID, `index` in it, `name`, `where`, `offset`, `length`, `resourceType`/`resourceId`, `file` or `null`, the fragment's members, `functions`) |

## 2. Reading

How ClassicMac tells code from data and what it names. The code's own formats are read as the code documents say
(the summary's links); everything here is a heuristic of the listing.

### 2.1 68k: recursive descent

1. The entry points: a segment's jump-table entries, the application's entry and original entry when they are in it,
   and its code start; a code resource's start, or its standard header's offset 0 and branch target, a driver's five
   routines, or a package's routines. Headers, far relocation lists and `%A5Init` data are marked as data first.
2. MacsBug names are found (§2.3) and marked as data with their literals.
3. From each entry, instructions are decoded in sequence. Each path follows branches, calls and PC-relative jumps
   (not absolute addresses: they are not this code's), a `jsr`/`jmp (xxx).l` whose long the loader relocates by this
   segment's address, and switch tables (§2.2). A path stops at a return (`rts`, `rtd`, `rte`, `rtr`), an
   unconditional branch or jump, a word that does not decode, `_ExitToShell`, an auto-pop Toolbox trap (it returns
   to the caller's caller), `$AAFE` (a routine descriptor: data from there on), or a byte already used. [Fitted]
4. Each MacsBug name labels the routine it ends (§2.3); descent continues from those routines.
5. Pascal and C strings that PC-relative operands point at become data.
6. The gaps no path reached are swept linearly: what decodes is code (a function starts at the gap's first
   instruction and after each return or unconditional jump, `gap`), a byte at an odd offset and what does not decode
   is data. [Fitted]

### 2.2 Switch tables

A `jmp d(pc,Dn.w)` right after `move.w d(pc,Dn.w),Dn` with the same index register reads a table of signed word
offsets at the `move`'s base; each target is the `jmp`'s base plus the offset [Verified: MPW and CodeWarrior switch
statements]. The number of cases is n + 1 from a
`cmpi.w`/`cmp.w`/`cmp.l #n,Dn` in the four instructions before the `move`; without one, the table ends at the first
entry that is not free, leads to an odd or outside address, or reaches the lowest target found so far. At most 1024
entries. [Fitted] Any other `jmp d(pc,Dn.w)` is followed by a run of branches,
each a case (at most 256). [Fitted]

### 2.3 MacsBug names

[Doc: MacsBug Reference and Debugging Guide, "Procedure names"] After a return (`rts` `$4E75`, `jmp (a0)` `$4ED0`, or
`rtd #n` `$4E74 nnnn`) at an even offset, a compiler may put the routine's name in one of three forms; the characters
are `A`–`Z`, `a`–`z`, `0`–`9`, `_`, `%`, `.`, space and `$`:

| Form | Encoding | Then |
| --- | --- | --- |
| Variable | A byte `$80` + n (n 1–31) and n characters; or `$80`, a length byte n (1–255) and n characters | A pad byte to an even offset, then a word: the size of the literals (constants) that follow |
| Fixed, 8 | 8 characters, the first with bit 7 set (`$A0`–`$FA`) | Nothing; trailing spaces are not part of the name |
| Fixed, 16 | 16 characters, the first two with bit 7 set | Nothing |

A name whose return lies inside the previous name's literals, or whose bytes are already data, is not taken. A name
labels the routine it ends: the last function known (an entry or a call target) between the end of the previous name
(or the start of the code) and this name's return; else the code right after the previous name. [ClassicMac]

Across segments, a jump-table call's annotation names the routine in the other segment by the same rule, without
descent: the code from the end of the previous name (or the segment's code start) to the name. [ClassicMac]

### 2.4 Traps and selectors

[Doc: Inside Macintosh: Operating System Utilities, the Trap Manager] An A-line word `$Axxx` is a trap, written
`_Name` with its modifiers:

- A Toolbox trap (bit 11 set) is numbered by bits 0–9; bit 10 is auto-pop, written `,AUTOPOP` (`_GetResource ,AUTOPOP`).
- An OS trap (bit 11 clear) is numbered by bits 0–7; bit 8 means A0 is not preserved; bits 9 and 10 are flags whose
  names depend on the manager: `,SYS` and `,CLEAR` for the Memory Manager, `,ASYNC` and `,HFS` for the File Manager,
  `$200`/`$400` where the trap names none. A table entry may itself carry modifier bits (`$A11E` `_NewPtr`): the
  most specific entry whose bits are all in the word is used, and only the remaining bits are written.
- An unknown trap is written as its word, `_A0FF`.

A dispatcher trap (`_Pack0`–`_Pack15`, `_OSDispatch`, `_ScriptUtil`, `_HFSDispatch`, `_Gestalt`, `_PrGlue`, …) takes
a selector on the stack (`move.w #n,-(sp)`, `move.l #n,-(sp)`, `clr.w -(sp)`) or in D0 (`moveq #n,d0`,
`move.w #n,d0`, `move.l #n,d0`), as each manager's chapter of *Inside Macintosh* says. The selector is looked for in
the three instructions before the trap, within its basic block: a branch, return, call or other trap, a block start, or
another push (another write to D0) ends the search. The bits of the value that select the routine are masked (the
rest are parameter sizes). The look-back of three is [ClassicMac].

The trap, selector and low-memory names come from Multiversal Interfaces (MIT), with Apple's trap-macro names added
where Multiversal has none; `tools/TrapTables` generates `TrapNames.g.cs`, `SelectorNames.g.cs` and
`LowMemoryGlobals.g.cs`.

### 2.5 Relocations

What the loader patches is annotated where the patched long lies inside an instruction and holds the operand's value:
an MPW far segment's A5 and PC lists ([code-segments.md](../code/code-segments.md)), Retro68's `'RELA'` and
CodeWarrior's `'DATA'` 0 lists for `'CODE'` 1 ([code-data.md](../code/code-data.md)). A segment's own PC relocations
make `jsr`/`jmp (xxx).l` a call into the segment.

### 2.6 PowerPC

1. Each code section's image (unpacked) is listed from its start, word by word. Traceback tables are data.
2. Functions: exported code and transition vectors, traceback names, main/init/term, cross-TOC glue stubs and `bl`
   targets (§1.5).
3. Transition vectors: a section relocation into a code section followed by one into a data section, the pair being
   {code, TOC}. The TOC base is the one most vectors give (CodeWarrior centres it, so it need not be the section's
   start). [Verified: Disk Copy 6.5]
4. Glue: `lwz r12,N(r2)`, `stw r2,20(r1)`, `lwz r0,0(r12)`, `lwz r2,4(r12)`, `mtctr r0`, `bctr`
   [Doc: Mac OS Runtime Architectures, "Cross-TOC glue"]; the import is the one whose relocation fills the TOC slot at
   TOC base + N. A slot no import fills is written `?slot s:0xoff`.
5. A `lwz` from `d(r2)` is annotated with what the slot at TOC base + d holds.
6. Each word decodes by the field layouts of the PEM and the AltiVec PEM, with the extended mnemonics of the PEM's
   appendix F where they apply (`mflr`, `li`, `subi`, `slwi`, `beq+`, `blr` …) [Doc: PEM; AltiVec PEM]. A word is
   `.long 0x…` when it is not a 32-bit PowerPC or AltiVec instruction or the PEM calls its form invalid [Doc: PEM,
   each instruction's page]:
   - a reserved field or bit is set, bit 31 included where there is no Rc; the bits of vspltb's, vsplth's and
     vspltw's UIMM above its 4, 3 and 2 bits;
   - a load or store with update with rA = 0, or an integer load with update (`lwzu`, `lbzu`, `lhzu`, `lhau` and
     their indexed forms) with rA = rD;
   - `mftb` with a TBR other than 268 (TBL) or 269 (TBU);
   - `bcctr` with BO bit 2 clear (it would decrement CTR);
   - 64-bit, POWER-only and 601-only operations, and later extensions (`mtocrf`, `lwsync`, L = 1 compares, BH).
7. A branch is written raw when its extended mnemonic would lose a field: `bc BO,BI,target`, `bclr BO,BI`,
   `bcctr BO,BI` when BO ignores BI (branch always, or CTR only) and BI is not 0, or a branch-always `bclr`/`bcctr`
   has a BO other than 20. A branch-always `bc` is always raw; `b` is the I-form. [ClassicMac]
8. An SPR prints by name in the direction the PEM gives it: `mfspr` of TBL and TBU (284, 285, written with `mtspr`
   and read with `mftb`) and `mtspr` of PVR (287, read only) print the number [Doc: PEM, "mfspr", "mtspr"]. The 603
   and 750 implementation SPRs (HID0 1008, L2CR 1017 …) print their number: the same number names other registers
   on other processors. [ClassicMac]
9. `mtfsfi`'s first operand is an FPSCR field, printed as a number (`mtfsfi 7,0x5`) [Doc: PEM, "mtfsfi"]. The 603's
   `tlbld` and `tlbli` decode [Doc: PEM, "tlbld", "tlbli"].

### 2.7 68k instructions

`M68kDisassembler` decodes the 68000 to 68040 integer instructions, the 68881/68882 FPU (coprocessor 1) and the
68030 MMU and 68851 PMMU (coprocessor 0) [Doc: M68000 Family Programmer's Reference Manual; MC68881/MC68882 User's
Manual; MC68030 User's Manual; MC68851 PMMU User's Manual; MC68040 User's Manual]. A word is `dc.w` when it is no
instruction on those processors, when the instruction does not allow its addressing mode, or when a bit is set that
the instruction's format draws as 0:

- bit 15 of a bit-field extension word; bits 14–12 of `bftst`, `bfchg`, `bfclr` and `bfset`; bits 10–9 when Do is
  set and bits 4–3 when Dw is set;
- bit 3 of a full extension word;
- the reserved bits of `cas`, `cas2`, `chk2`/`cmp2`, `moves` and the PMMU extension words;
- the high byte of the byte immediate of `ori`/`andi`/`eori` to CCR, of a static bit number and of `callm`'s argument
  count.

An ordinary byte immediate (`<ea>` mode 7, register 4, byte size: `ori.b`, `move.b`, `cmp.b`, `fmove.b`, `pmove` of a
byte register …) is the low byte of its word; the high byte is ignored, as the processor ignores it [Doc: M68000
Family Programmer's Reference Manual, "Immediate Data"]. Reserved bits 9–3 of a 32-bit multiply or divide extension
word are ignored too [Reference: Ghidra].

The 68060's own forms (`plpa`, `lpstop`, `movec` of BUSCR and PCR) and the CPU32's (`tbl`, `bgnd`) are `dc.w`: no
Macintosh has those processors [ClassicMac].

A PC-relative operand's base is the address of its own extension word, after any words before it in the instruction
(a bit number, an immediate, a register mask, a bit-field or coprocessor command word): `btst #3,16(pc)` at $1000 reads
$1014, `movem.w 16(pc,d0.w),d0-d1` reads $1014 + d0 [Doc: M68000 Family Programmer's Reference Manual, "Program
Counter Indirect"; Verified: an independent disassembler]. `fdbcc` and `pdbcc` branch from their displacement
word (§7).

`fmovem.l` of two or three FPU control registers takes every memory mode from memory and the memory alterable modes
to it (a data or address register only for one register). Its immediate holds one long per register, in the order
FPCR, FPSR, FPIAR, so the instruction is 4 + 4n bytes [Doc: MC68881/MC68882 User's Manual, FMOVEM]; some
assemblers and disassemblers read one long.

## 3. Writing

### 3.1 Files

| Writer | Files |
| --- | --- |
| `extract`, decoder `code.segment` (`'CODE'`) | `<id> <name>.bin` (the data, the main file), `.s`, `.json`; `'CODE'` 0: `.bin`, `.json` |
| `extract`, decoder `code.cfrg` (`'cfrg'`) | `.bin`, `.json` |
| `extract`, decoder `code.resource` (§5.1) | `.bin`, `.s`, `.json` |
| `disasm` | Per Mac file with code: `CODE-<id> <name>.s` per segment, `<type>-<id> <name>.s` per code resource, `fragment-<i> <name>.s` per data-fork fragment of `'cfrg'` 0 (i its index there) and `fragment-<id>.<i> <name>.s` for `'cfrg'` <id>, and `code.json`; names made host-safe ([export-manifest.md §3.3](export-manifest.md#33-host-safe-names)) and unique without regard to case |

The main file of the code decoders is the data itself, so `pack` takes it back as it takes a raw resource's
([export-manifest.md §2.2](export-manifest.md#22-rebuilding-a-fork)).

### 3.2 The disasm command

```
classicmac disasm <input> [-o <dir>] [--cpu 68k|ppc|both] [--overwrite]
```

| Option | Effect |
| --- | --- |
| `-o`, `--output` | The output folder (default: `<input> code` beside the input). One Mac file with code is written into it; several get a folder each, placed as `unpack` places files |
| `--cpu` | `68k`: the segments and the 68k code resources (fat ones with their fragments); `ppc`: native code resources and the fragments the `'cfrg'` resources name; `both` (default) |
| `--overwrite` | Write into a folder that already holds files (refused otherwise) |

The input is read as `extract` reads it (containers, disk images, archives, a raw fork); the limit options and
`--strict`/`--quiet` apply. Exit codes are `extract`'s: 0 done, 1 an error diagnostic (or a warning with `--strict`),
2 usage, 3 not a Mac container or fork, 4 a file-system error. A file with no code writes nothing; an input with none
prints `No code in <input>.`

Every `'cfrg'` resource's members (an application's or library's is `'cfrg'` 0; the System file has others) are listed
by where they are: in the data fork at the member's offset for its length (0: to the
end of the fork); in a resource (type `where1`, ID `where2`) as that code resource; elsewhere (memory, a byte stream,
another fragment) not, with `code.fragment-elsewhere`.

## 4. Variants

- A segment with no `'CODE'` 0 (a resource copied out of its application) is listed on its own: its header, a far
  segment's A5 relocations, entered at its code start.
- A CFM-68K fragment (`'m68k'`) is described, not disassembled.
- A 68k code resource of a type not in §5.1 is not listed by `extract`; `disasm` lists one that a `'cfrg'` names.

## 5. ClassicMac

### 5.1 The decoded types

| Decoder | Types |
| --- | --- |
| `code.segment` | `CODE` |
| `code.cfrg` | `cfrg` |
| `code.resource`, native (PEF from offset 0) | `ncod`, `nlib`, `ndrv`, `nift`, `fovr`, `ncmp`, `cdek`, `dcod`, `scal`, `vdig`, `ntrb`, `nitt`, `sfvr`, `ppct`, `pthg`, `qtcm`, `hqda`, `ndmc`, `nsnd` |
| `code.resource`, 68k (some fat) | `CDEF`, `WDEF`, `MDEF`, `MBDF`, `LDEF`, `expt`, `nsrd`, `DRVR`, `PACK`, `proc`, `sift`, `FKEY`, `INIT`, `cdev`, `RDEV`, `XCMD`, `XFCN`, `ADBS`, `PTCH`, `ptch`, `dcmp`, `snth` |

A resource of these types is listed by its shape, not its type: a PEF container from offset 0 is a fragment whatever
the type; `DRVR` alone is read for the driver header. [ClassicMac]

### 5.2 The application behind a segment

A decoder sees one resource at a time. The `'CODE'` decoder rebuilds the application from the fork's `'CODE'`,
`'DATA'`, `'RELA'` and `'cfrg'` resources (decompressed) and keeps it while the same resources come back, compared by
content. The application's diagnostics go to `'CODE'` 0's warnings only; a segment that could not be decompressed is
reported there (`code.compressed`) and listed as `compressed and not readable`. [ClassicMac]

### 5.3 Names in the code

| What | Where |
| --- | --- |
| Listings | `CodeListing.ForSegment(CodeApplication, id)`, `CodeListing.ForCodeResource(type, id, data, fork)`, `CodeListing.ForFragment(PefContainer, name)`; `Text`, `Functions`, `References`, `Diagnostics`, `Fragments` |
| MacsBug names | `MacsBugNames.Find`, `MacsBugNames.Read` |
| Trap, selector and low-memory names | `TrapNames.Describe`/`Lookup`, `SelectorNames.TryGetConvention`/`TryGet`, `LowMemoryGlobals.TryFind` |
| The decoders | `ClassicMac.Resources.Decoders.Code`: `CodeSegmentDecoder`, `CfrgDecoder`, `CodeResourceDecoder` (internal, from `ResourceDecoders.Create`) |
| A whole file | `CodeExport.Disassemble(fork, dataFork, CodeCpu, ReadOptions, diagnostics)` → `CodeFile` list |

## 6. Diagnostics

The listing passes on the diagnostics of the readers it uses (`code.*`, `m68k.*`, `pef.*`, `cfrg.*`: see the code
documents). These are its own and the decoders':

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `code.cfrg-unreadable` | Warning | `'cfrg'` is shorter than its header or not version 1 | `extract`: writes its data only; `disasm`: lists no fragments | Not traced |
| `code.fragment-elsewhere` | Info | `disasm`: a `'cfrg'` member is in memory, a byte stream or another fragment | Does not list it | Not applicable |
| `code.fragment-missing` | Warning | `disasm`: a `'cfrg'` member's resource is not in the file | Does not list it | Not traced |
| `code.fragment-range` | Warning | `disasm`: a data-fork member runs past the data fork | Does not list it | Not traced |
| `code.fragment-unreadable` | Warning | `disasm`: a data-fork member is not a PEF container | Does not list it | Not traced |
| `code.jump-table-unreadable` | Warning | `'CODE'` 0 is shorter than 16 bytes, or compressed and could not be decompressed | `extract`: writes its data only; `disasm`: lists the segments on their own | Not traced |
| `code.listing-application` | Warning | A segment's fork has a `'CODE'` 0 that does not read | Lists the segment on its own | Not traced |
| `code.pef-unreadable` | Warning | A resource starts `Joy!peff` but is shorter than the PEF header | `extract`: writes its data only; `disasm`: does not list it | Not traced |

## 7. Verification

- `tests/ClassicMac.Code.Tests/Disassembly/CodeListingTests.cs`: golden listings of hand-built near and far segments,
  each code-resource form, `'CODE'` 0, fragments with glue and TOC slots, a fat resource, a CFM-68K fragment, the
  bootstrap entry, Retro68 and CodeWarrior models.
- `M68kDisassemblerTests`, `M68kMmuTests`, `PpcDisassemblerTests`, `PpcDisassemblerFormTests`: the instruction
  vectors. For the 68k, every opcode line, every `movec` register, FPU register fields other than FP0, PC-relative
  bases after extension words, the reserved bits and byte immediates of §2.7, empty register lists and `fmovem.l` of
  the control registers; each valid 68k vector decodes the same way in at least one independent disassembler. For
  PowerPC, every form and extended mnemonic, each part of every multi-bit reserved field, the invalid update forms,
  `mftb`'s TBRs, the raw branch forms and every named SPR in both directions.
- `M68kCodeMapTests`, `M68kAnnotatorTests`, `MacsBugNamesTests`, `PpcFragmentMapTests`, `TrapTableTests`: §2.
- `tests/ClassicMac.Resources.Decoders.Tests/CodeDecoderTests.cs`: each decoder's files and model, the application
  rebuilt from the fork, and each diagnostic; `CodeExportTests.cs`: `disasm`'s files, the CPU choice, every fragment
  location and its diagnostics, host-safe names. `GoldenTests` keep every code type's `.s` and `.json` in `Golden/`.
- `tests/ClassicMac.Resources.Cli.Tests/DisasmTests.cs`: the command on a raw fork and an HFS disk with a fat
  application (data-fork fragment), `--cpu`, exit codes. `PackTests.Decoded_code_packs_back_from_its_bin_files`.
- `tests/ClassicMac.App.Tests/PreviewTests.cs`: a `'CODE'` resource previews as its listing.
- Compared outside the repository with an independent disassembler [Verified]: every PowerPC instruction both decode
  in Disk Copy 6.5's code (172,278) has the same mnemonic and operands; all 19,562 AltiVec words of Mac OS 9.2.2's
  vecLib and ASPAltivecPlug-in match, apart from writing a base register of 0 as `0`; the 68k decoder gives the same
  instruction lengths at every address of a CodeWarrior application and Disk Copy 6.1.2. The 68030 PMMU forms agree
  where that disassembler decodes them; the 68851-only forms rest on Motorola's manuals (the MC68851 PMMU User's
  Manual's appendix A; a `pdbcc` branches from its displacement word, the CPU's cpDBcc rule).
- `tests/ClassicMac.Resources.Decoders.Tests/CodeCorpusTests.cs`, with `CLASSICMAC_CODE_CORPUS` set: every code
  resource of ResEdit, Realmz, QDHarness, Disk Copy 6.1.2 and the Mac OS 9 System file decodes and `disasm` lists it
  (the System's 162 data-fork fragments included) with no error.
- With `CLASSICMAC_CODE_CORPUS` set (not committed): `CodeListingCorpusTests` lists every `'CODE'`, code resource and
  fragment of the corpus with no decoder error; `PpcCorpusTests` checks the word, `.long`, `blr`, `bl` and return
  counts and sample texts of NQD (no `.long`), Disk Copy 6.1.2 (4) and Disk Copy 6.5 (9,451), which
  agree word for word with an independent disassembler apart from mnemonic spelling and, in data, BO values with a z
  bit set [Verified]; `M68kCorpusTests`, `MacsBugCorpusTests`, `PpcGlueCorpusTests` check
  the counts in [PLAN.md](../../PLAN.md)'s phase 11 exit.

## 8. Not covered

- Data sections are not disassembled; PowerPC code is listed linearly, not by descent.
- CFM-68K fragments are not disassembled.
- Code reached only through computed addresses (function pointers in data, `jsr (a0)`) is found by the gap sweep, not
  named.
- Selectors set further back than three instructions, or through registers, are not named.
- PowerPC forms the PEM calls invalid for a register range are decoded: `lmw` with rA among the registers loaded,
  `lswi`/`lswx` whose registers overlap rA or rB.
- No re-assembly: the listing is for reading; `pack` takes the `.bin`.
- The 68060's and the CPU32's own instructions are `dc.w` (§2.7).

## 9. References

1. *M68000 Family Programmer's Reference Manual*, Motorola, 1992.
2. *PowerPC Microprocessor Family: The Programming Environments for 32-Bit Microprocessors*, IBM/Motorola; *AltiVec
   Technology Programming Environments Manual*, Motorola.
3. *Mac OS Runtime Architectures*, Apple, 1997: cross-TOC glue, transition vectors, the far model.
4. *Inside Macintosh: Operating System Utilities* (the Trap Manager); *Inside Macintosh II* (the Segment Loader).
5. *MacsBug Reference and Debugging Guide*, Apple: procedure names.
6. Multiversal Interfaces (Executor), MIT: trap, selector and low-memory names.
7. resource_dasm (Martin Michelsen), MIT: the 68k and PowerPC disassemblers ClassicMac's are ported from.
8. *MC68881/MC68882 Floating-Point Coprocessor User's Manual*, *MC68030 User's Manual*, *MC68040 User's Manual*,
   *MC68851 Paged Memory Management Unit User's Manual*, Motorola.
9. Ghidra (NSA), Apache 2.0: a behavioural reference for the 68k decoder (the reserved bits of a 32-bit divide).
