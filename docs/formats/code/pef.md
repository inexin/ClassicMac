# PEF containers

The Preferred Executable Format: the container of a PowerPC (or CFM-68K) code fragment, which the Code Fragment
Manager loads. A 40-byte header, the section headers, a section name table and the sections: code, data (stored as
is or as pattern-initialization instructions) and the loader section, which holds the entry points, the imports, the
relocations and the exports with their hash table. A fragment lives in a data fork (located by a `'cfrg'` member,
[cfrg.md](cfrg.md)), in a resource of its own, or behind a routine descriptor in a fat code resource
([code-resources.md](code-resources.md)). ClassicMac reads the container and the loader section, builds each
section's image, runs the relocations, finds exports through the hash table, reads transition vectors and finds
traceback tables in the code.

| | |
| --- | --- |
| Identified by | `Joy!peff` at +$00. In a data fork at a `'cfrg'` member's offset; native code resources (`ncod`, `ndrv`, `nlib`, …) at offset 0 |
| ClassicMac | Reads; `ClassicMac.Code.Ppc` (`PefContainer`, `PefLoader`, `PatternData`, `PefRelocator`, `TracebackTable`) |
| Verified against | The Mac OS 9 System file's fragments (its data fork and native resources); Mac OS 9.2.2's 90 fragments; Disk Copy 6.1.2 and Disk Copy 6.5 |
| Sources | *Mac OS Runtime Architectures* (Apple, 1997), "PEF Structure" and the CFM-based runtime chapters; `PEFBinaryFormat.h` and `CodeFragments.h` (Universal Interfaces). The traceback table is AIX's `tbtable.h` layout |

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

### 1.1 Container header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | tag1 | `'Joy!'` |
| +$04 | 4 | tag2 | `'peff'` |
| +$08 | 4 | architecture | `'pwpc'` (PowerPC) or `'m68k'` (CFM-68K) |
| +$0C | 4 | formatVersion | 1 |
| +$10 | 4 | dateTimeStamp | Mac date; 0 when not set |
| +$14 | 4 | oldDefVersion | The oldest definition version the fragment is compatible with |
| +$18 | 4 | oldImpVersion | The oldest implementation version |
| +$1C | 4 | currentVersion | Equals the `'cfrg'` member's currentVersion [Verified: the Mac OS 9 System file] |
| +$20 | 2 | sectionCount | |
| +$22 | 2 | instSectionCount | The sections placed in memory: the first this many |
| +$24 | 4 | reservedA | 0 |

[Doc: Mac OS Runtime Architectures]

The section headers follow at +$28, 28 bytes each; the section name table follows them, at
$28 + 28 × sectionCount [Doc: Mac OS Runtime Architectures].

### 1.2 Section header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | nameOffset | `i32`: into the section name table (a C string); −1 for no name |
| +$04 | 4 | defaultAddress | The address the section was built for |
| +$08 | 4 | totalLength | The section's length in memory |
| +$0C | 4 | unpackedLength | The initialized part; the rest, to totalLength, is zero (bss) |
| +$10 | 4 | containerLength | The stored contents' length |
| +$14 | 4 | containerOffset | From the start of the container |
| +$18 | 1 | sectionKind | §1.3 |
| +$19 | 1 | shareKind | §1.3 |
| +$1A | 1 | alignment | A power of 2 |
| +$1B | 1 | reservedA | 0 |

[Doc: Mac OS Runtime Architectures] Every section in the samples has nameOffset −1 [Verified: Mac OS 9.2.2's 90
fragments].

### 1.3 Section kinds and share kinds

| sectionKind | Name | Placed in memory | Contents |
| --- | --- | --- | --- |
| 0 | Code | Yes | Read-only, executable |
| 1 | UnpackedData | Yes | Stored as is |
| 2 | PatternInitData | Yes | Pattern-initialization instructions (§1.4) |
| 3 | Constant | Yes | Read-only data |
| 4 | Loader | No | §1.5 |
| 5 | Debug | No | Reserved |
| 6 | ExecutableData | Yes | Data that is also executable |
| 7 | Exception | No | Exception-handling tables |
| 8 | Traceback | No | Traceback tables |

| shareKind | Name | Meaning |
| --- | --- | --- |
| 1 | ProcessShare | One copy per process |
| 4 | GlobalShare | One copy for the whole system |
| 5 | ProtectedShare | One copy for the whole system, writable only by privileged code |

[Doc: Mac OS Runtime Architectures] Kinds 0, 1, 2, 4 and 7 occur in the samples; a stub library is a single loader
section [Verified: Mac OS 9.2.2's 90 fragments]. Kind 3 occurs once, in the Mac OS 9 System file's `'ncod'` 3: a
$41-byte Constant section, process-shared, alignment 3 [Verified: the Mac OS 9 System file].

### 1.4 Pattern-initialized data

A PatternInitData section's contents are instructions that build the initialized part. Each instruction is an opcode
byte (the opcode in bits 5–7, a count in bits 0–4) and its arguments. A count of 0 means the count follows as an
argument. An argument is a big-endian run of 7-bit groups, bit 7 set on every byte but the last.

| Opcode | Name | Arguments after the count | Output |
| --- | --- | --- | --- |
| 0 | Zero | | `count` zero bytes |
| 1 | BlockCopy | | The next `count` bytes |
| 2 | RepeatedBlock | `repeatCount` | The next `count` bytes, `repeatCount` + 1 times |
| 3 | InterleaveRepeatBlockWithBlockCopy | `customSize`, `repeatCount` | A common block of `count` bytes, then `repeatCount` custom blocks of `customSize` bytes; out goes common, custom 1, common, custom 2, …, custom n, common |
| 4 | InterleaveRepeatBlockWithZero | `customSize`, `repeatCount` | As 3, with a common block of `count` zero bytes that is not stored |
| 5–7 | | | Undefined |

[Doc: Mac OS Runtime Architectures, "Pattern-Initialized Data"] The Code Fragment Manager's unpacker writes opcode 2's
block repeatCount + 1 times and opcodes 3 and 4 as common, then (custom, common) repeatCount times [Code: the Code
Fragment Manager in the Mac OS ROM]. Every opcode 0–4 occurs, and every pidata section unpacks to exactly its
unpackedLength [Verified: Mac OS 9.2.2's 90 fragments, the Mac OS 9 System file].

### 1.5 Loader section header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | mainSection | `i32`; −1 for none |
| +$04 | 4 | mainOffset | |
| +$08 | 4 | initSection | `i32`; −1 for none |
| +$0C | 4 | initOffset | |
| +$10 | 4 | termSection | `i32`; −1 for none |
| +$14 | 4 | termOffset | |
| +$18 | 4 | importedLibraryCount | |
| +$1C | 4 | totalImportedSymbolCount | |
| +$20 | 4 | relocSectionCount | The number of relocation headers |
| +$24 | 4 | relocInstrOffset | From the start of the loader section |
| +$28 | 4 | loaderStringsOffset | From the start of the loader section |
| +$2C | 4 | exportHashOffset | From the start of the loader section |
| +$30 | 4 | exportHashTablePower | The hash table has 2^power entries |
| +$34 | 4 | exportedSymbolCount | |

[Doc: Mac OS Runtime Architectures]

The entry points (main, init, term) are a section and an offset; each names a transition vector (§1.11) [Doc: Mac OS
Runtime Architectures]. In Mac OS 9.2.2's 90 fragments they are always in section 1 [Verified: Mac OS 9.2.2's 90
fragments]; the Mac OS 9 System file's `'ntrb'` resources are data-only containers (a pidata section, then the loader)
with main in section 0 [Verified: the Mac OS 9 System file].

After the header, in order: the imported libraries, the imported symbols, the relocation headers. The relocation
instructions, the string table and the export tables are where the header's offsets say [Doc: Mac OS Runtime
Architectures].

### 1.6 Imported libraries and symbols

An imported library, 24 bytes:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | nameOffset | Into the string table: a C string, the library's fragment name |
| +$04 | 4 | oldImpVersion | The oldest implementation version the importer accepts |
| +$08 | 4 | currentVersion | The version it was linked against |
| +$0C | 4 | importedSymbolCount | |
| +$10 | 4 | firstImportedSymbol | Index into the imported symbol table |
| +$14 | 1 | options | Bit 7 (`$80`): initialize the library before the importer. Bit 6 (`$40`): every import from it is weak |
| +$15 | 1 | reservedA | 0 |
| +$16 | 2 | reservedB | 0 |

An imported symbol, 4 bytes:

| Bits | Field | Notes |
| --- | --- | --- |
| 28–31 | flags | `$8` (bit 31): the symbol is weak: the fragment loads without it |
| 24–27 | symbolClass | 0 code, 1 data, 2 transition vector, 3 TOC, 4 glue |
| 0–23 | nameOffset | Into the string table: a C string |

The symbols are numbered from 0 in table order (the import index the relocations use); each library owns
importedSymbolCount of them from firstImportedSymbol [Doc: Mac OS Runtime Architectures]. Options `$00`, `$40`, `$80`
and `$C0` all occur; imports are of class 1 or 2, with flags 0 or `$8` [Verified: Mac OS 9.2.2's 90 fragments]. A
symbol is weak when its own flag is set or its library's options have `$40`; the symbols of a `$40` library keep flags
0 (Disk Copy 6.1.2's AOCELib, SpeechLib and DragLib) [Doc: Mac OS Runtime Architectures] [Verified: Disk Copy 6.1.2].

### 1.7 Relocation headers

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | sectionIndex | The section the instructions relocate |
| +$02 | 2 | reservedA | 0; ignored. `$CBCC` in some of the Mac OS 9 System file's `'ndrv'` resources [Verified: the Mac OS 9 System file] |
| +$04 | 4 | relocCount | The number of 16-bit instruction words |
| +$08 | 4 | firstRelocOffset | From relocInstrOffset |

[Doc: Mac OS Runtime Architectures]

A section's instructions are those of the first header that names it; a later header for the same section is never
run [Code: the Code Fragment Manager in the Mac OS ROM].

### 1.8 Relocation instructions

The instructions run a small machine over the target section. Its state starts as: relocAddress at the start of the
section, importIndex 0, sectionC the address of section 0, sectionD the address of section 1. "Add X" adds X to the
32-bit word at relocAddress and moves relocAddress on 4 bytes [Doc: Mac OS Runtime Architectures, "Relocation
Instruction Set"].

The encodings, bits from the top of the first word (the mnemonics are ClassicMac's short names):

| Encoding | Mnemonic | Name | Action |
| --- | --- | --- | --- |
| `00` skip:8 count:6 | DDAT | RelocBySectDWithSkip | relocAddress += 4 × skip; add sectionD, count times |
| `010 0000` n:9 | CODE | RelocBySectC | Add sectionC, n + 1 times |
| `010 0001` n:9 | DATA | RelocBySectD | Add sectionD, n + 1 times |
| `010 0010` n:9 | DESC | RelocTVector12 | n + 1 times: add sectionC, add sectionD, relocAddress += 4 |
| `010 0011` n:9 | DSC2 | RelocTVector8 | n + 1 times: add sectionC, add sectionD |
| `010 0100` n:9 | VTBL | RelocVTable8 | n + 1 times: add sectionD, relocAddress += 4 |
| `010 0101` n:9 | SYMR | RelocImportRun | n + 1 times: add import importIndex; importIndex += 1 |
| `011 0000` i:9 | SYMB | RelocSmByImport | Add import i; importIndex = i + 1 |
| `011 0001` i:9 | CDIS | RelocSmSetSectC | sectionC = section i's address |
| `011 0010` i:9 | DTIS | RelocSmSetSectD | sectionD = section i's address |
| `011 0011` i:9 | SECN | RelocSmBySection | Add section i's address |
| `1000` b:12 | DELTA | RelocIncrPosition | relocAddress += b + 1 |
| `1001` c:4 r:8 | RPT | RelocSmRepeat | Run the c + 1 words before this one r + 1 more times (below) |
| `101000` o:26 | LABS | RelocSetPosition | relocAddress = o (two words) |
| `101001` i:26 | LSYM | RelocLgByImport | Add import i; importIndex = i + 1 (two words) |
| `101100` c:4 r:22 | LRPT | RelocLgRepeat | Run the c + 1 words before this one r more times (two words; r is not stored minus 1; below) |
| `101101 0000` i:22 | LSEC | RelocLgBySection | Add section i's address (two words) |
| `101101 0001` i:22 | LSEC | RelocLgSetSectC | sectionC = section i's address (two words) |
| `101101 0010` i:22 | LSEC | RelocLgSetSectD | sectionD = section i's address (two words) |

[Doc: Mac OS Runtime Architectures, "Relocation Instruction Set"] Every other encoding is undefined.

The instructions are 16-bit "relocation blocks", and a repeat counts blocks, not instructions [Doc: Mac OS Runtime
Architectures; `PEFBinaryFormat.h`, PEFRelocChunk]. The Code Fragment Manager runs them through a word pointer
[Code: the Code Fragment Manager in the Mac OS ROM, RelocSmRepeat and RelocLgRepeat]:

1. A repeat moves the pointer back c + 1 words from its own first word; decoding goes on from there, even when that
   word is the second word of a two-word instruction.
2. One counter serves every repeat, 0 when none is running. A repeat reached with the counter at 0 sets it to r + 1
   (RPT) or r (LRPT). Each time a repeat is reached the counter goes down by 1 first; while it is not 0 the pointer
   goes back, and when it reaches 0 decoding goes on after the repeat.
3. So RPT runs its block r + 1 more times and LRPT r more times. LRPT with r = 0, or a repeat inside another's
   block, never brings the counter to 0: the load does not end.

None of the samples' 150 repeats has a two-word instruction in its block, so they read the same either way [Verified:
Mac OS 9.2.2's 90 fragments, the Mac OS 9 System file].

SECN, like every "add", moves relocAddress on 4 [Code: the Code Fragment Manager in the Mac OS ROM]. The Mac OS 9
System file's `'ncod'` 3 has one SECN 2, repeated twice by an RPT: the words at $188, $18C and $190 hold $0, $10 and
$18 before relocation, offsets into its $41-byte Constant section [Verified: the Mac OS 9 System file].

DELTA moves by bytes, so relocated words are 2-byte aligned in places, not always 4: 1,110 of the samples' fixups are
[Verified: Mac OS 9.2.2's 90 fragments, the Mac OS 9 System file]. CDIS, LRPT and LSEC occur in none of the samples.

### 1.9 String table

C strings in Mac OS Roman: library names, import names. Export names are in the same table but are **not**
NUL-terminated; their length is the high 16 bits of their hash word (§1.10) [Doc: Mac OS Runtime Architectures]
[Verified: Mac OS 9.2.2's 90 fragments].

### 1.10 Exports

At exportHashOffset, three tables:

| Table | Entry size | Count | Entry |
| --- | --- | --- | --- |
| Hash table | 4 | 2^exportHashTablePower | Bits 18–31: the chain's length. Bits 0–17: the index of its first export |
| Key table | 4 | exportedSymbolCount | The export's hash word |
| Symbol table | 10 | exportedSymbolCount | Below |

A symbol table entry:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | classAndName | Top byte: the symbol class (§1.6). Low 24 bits: the name's offset in the string table |
| +$04 | 4 | symbolValue | An offset in the section; for sectionIndex −2 or −3, below |
| +$08 | 2 | sectionIndex | `i16`. −2: absolute (the value is the address). −3: a re-exported import (the value is the import index) |

[Doc: Mac OS Runtime Architectures] A re-export has the same name as the import it passes on [Verified: the Mac OS 9
System file's NQD, 26 re-exports]; a stub library's exports are all re-exports [Verified: Mac OS 9.2.2's 90
fragments].

The hash word of a name of n bytes:

1. hash = 0, as a signed 32-bit value.
2. For each byte c of the name, stopping at a NUL: hash = (hash << 1) − (hash >> 16), the shift right arithmetic;
   then hash = hash XOR c. n is the number of bytes taken.
3. The word is (n << 16) OR ((hash XOR (hash >> 16)) AND $FFFF).

The hash table index of a word w for a table of 2^p entries is (w XOR (w >> p)) AND (2^p − 1)
[Doc: Mac OS Runtime Architectures, "Hash Word", PEFComputeHashWord]. With a logical shift instead of the arithmetic
one, 1,258 of the samples' keys would come out different [Verified: Mac OS 9.2.2's 90 fragments, the Mac OS 9 System
file]. Different names can share a whole hash word in one chain (MathLib's `exp` and `nan` are both $00030114,
StdCLib's `feof` and `open` both $0004021C), so the name compare in the lookup below is needed [Verified: Mac OS
9.2.2's 90 fragments].

The power is the linker's choice and follows no one rule of the export count: the smallest p with n / 2^p under 10
fits 198 of the 288 sample containers, not AppearanceLib (218 exports, power 7) or a stub with no exports (power 1)
[Verified: Mac OS 9.2.2's 90 fragments, the Mac OS 9 System file]. A reader takes the stored power. An empty slot's first index is 0 or the next
chain's first; it is not used [Verified: the Mac OS 9 System file].

To find an export by name, as the Code Fragment Manager does:

1. Compute the name's hash word and its index; read that hash table entry.
2. Walk the chain's exports, from its first index, its length long.
3. The export is the one whose key equals the hash word and whose name bytes equal the name.

[Doc: Mac OS Runtime Architectures] Every key equals its name's hash word, every export is found this way, and the
chains, taken in table order, tile the export table in order [Verified: Mac OS 9.2.2's 90 fragments].

### 1.11 Transition vectors and the TOC

A transition vector is two words in a data section: the code address, then the TOC base the function runs with
(r2). A call across fragments goes through one [Doc: Mac OS Runtime Architectures, "Transition Vectors"]. In the
file, before relocation, they hold offsets: the first into the code section, the second into the data section; DESC
or DSC2 then add sectionC and sectionD [Verified: Mac OS 9.2.2's 90 fragments]. The main, init and term entry points
and every export of class 2 point at one.

The TOC base is that second word: an offset into the data section (`$87C` in Mac OS 9's NQD; 0 in some Apple
libraries). CodeWarrior centres the TOC, so its base is `$8000` into the data section [Verified: Disk Copy 6.5].

### 1.12 Traceback tables

The compilers put a traceback table after each function's final `blr` (`$4E800020`), in the code section: a zero
word, 8 flag bytes, then optional fields in this order [Doc: Mac OS Runtime Architectures; the layout is AIX's
`tbtable.h`]:

| Field | Size | Present when |
| --- | --- | --- |
| Zero word | 4 | Always |
| Flags | 8 | Always. Byte 0: version (0). Byte 1: language (0 C, 1 FORTRAN, 2 Pascal, 9 C++). Byte 2: `$20` has tb_offset, `$08` has ctl_info. Byte 3: `$80` interrupt handler, `$40` has the name, `$20` uses alloca. Byte 4: `$80` stores_bc, `$40` fixup, bits 0–5 the FPRs saved. Byte 5: `$80` has_ext_table, `$40` has_vec, bits 0–5 the GPRs saved. Byte 6: fixed-point parameter count. Byte 7: bits 1–7 the floating-point parameter count, bit 0 parmsonstk (parameters on the stack; not a count) |
| parminfo | 4 | There are fixed-point or floating-point parameters |
| tb_offset | 4 | Byte 2 bit `$20`: the distance from the function's first instruction to the zero word |
| hand_mask | 4 | Byte 3 bit `$80` |
| ctl_info | 4 + 4n | Byte 2 bit `$08`: a count n, then n words |
| Name | 2 + n | Byte 3 bit `$40`: a 16-bit length, then the name in Mac OS Roman |
| alloca_reg | 1 | Byte 3 bit `$20` |
| vec_ext | 6 | Byte 5 bit `$40`: the AltiVec extension (vector registers saved, vector parameters) |
| ext_table | 1 | Byte 5 bit `$80` |

Bytes 4 and 5's register counts and bits describe the frame and add no field. No table in the corpus sets has_vec or
has_ext_table, Mac OS 9.2.2's vecLib included [Verified: 2,449 tables in Mac OS 9.2.2's fragments, NQD and Disk Copy].

Apple's libraries carry no names; Disk Copy 6.5 names 1,619 functions [Verified: the Mac OS 9 System file, Disk
Copy 6.5].

## 2. Reading

### 2.1 The container

1. Check the 40-byte header and the `Joy!peff` tag.
2. Read sectionCount section headers from +$28 and their names from the name table.
3. Read the loader section (the first of kind 4), §2.3.
4. A section's stored contents are containerLength bytes at containerOffset.

[Doc: Mac OS Runtime Architectures]

### 2.2 A section's image

1. A PatternInitData section: unpack its contents (§1.4); the result should be unpackedLength bytes.
2. Any other kind: take the first unpackedLength bytes of its contents.
3. Zero-fill to totalLength.

[Doc: Mac OS Runtime Architectures]

### 2.3 The loader section

1. Read the 56-byte header.
2. From +$38, read the imported libraries, then the imported symbols, then the relocation headers. Each relocation
   header's words are at relocInstrOffset + firstRelocOffset.
3. Read the names from the string table at loaderStringsOffset (§1.9).
4. At exportHashOffset, read the hash table, then the key table, then the symbol table (§1.10).

[Doc: Mac OS Runtime Architectures]

### 2.4 Relocating

For each relocation header, run its instructions (§1.8) over a copy of its section's image, with each section at its
chosen address and each import resolved to its symbol's address. Every relocated word must lie inside its section
[Doc: Mac OS Runtime Architectures]. Before relocation every word an import is added to holds 0, and no word is
relocated twice [Verified: Mac OS 9.2.2's 90 fragments].

## 3. Writing

None.

## 4. Variants

- CFM-68K containers have architecture `'m68k'` and the same structure [Doc: Mac OS Runtime Architectures]; none was
  read.
- Mac OS 9.2.2's fragments are newer builds of 9.0's (NQD, FontManager and the others; the
  [reference builds](../README.md#reference-builds)); the container format is the same.

## 5. ClassicMac

- `PefContainer.Read` takes the bytes from the `Joy!peff` tag; slicing a data fork at a `'cfrg'` member's offset is the
  caller's. A header shorter than 40 bytes or without the tag throws; everything after is read as far as it goes and
  reported. [ClassicMac]
- A section is placed in memory by its kind (§1.3), not by instSectionCount. [ClassicMac]
- A section's contents are clipped to the container. An image is built at most max(bytes unpacked, stored length) +
  16 MB long (by the bytes actually unpacked, not the declared unpackedLength), and pidata unpacking stops at 256 MB, so a damaged length is not allocated. An image is never shorter
  than its unpacked pidata. Images are cached and built once. [ClassicMac]
- Pidata that unpacks to more or to less than unpackedLength is reported either way (the Code Fragment Manager
  accepts less), and an argument wider than 32 bits is damage rather than wrapped. [ClassicMac]
- The relocator lists fixups (where each word is, what is added) without an image, with every section and import at
  0; given addresses, it applies them to copies (`Instantiate`). [ClassicMac]
- A relocated word outside its section is skipped and the run goes on; the out-of-range words are reported once per
  section. [ClassicMac]
- Where the Mac would never finish, ClassicMac reports and moves on: a repeat reached while another runs stops the
  run; LRPT with r = 0 is not repeated. A repeat reaching back before the first word stops the run. [ClassicMac]
- Every undefined encoding stops the header's run, LSEC sub-opcodes 3–15 included (the Code Fragment Manager skips
  those). [ClassicMac]
- A run making more fixups than half the section's length in bytes plus 4,096 is a runaway and stops. [ClassicMac]
- An export hash power above 18 cannot index an 18-bit first-export field: it is reported and the exports are not
  read. [ClassicMac]
- `GetTransitionVector` returns the two words before relocation with the section each one's fixup adds (−1 when none
  does, or when an import is added). [ClassicMac]
- Traceback tables are found by a scan: every word-aligned zero word after a `blr`, with version byte 0 and its 8
  flag bytes inside the code [Verified: Disk Copy 6.5]. A tb_offset reaching before the code leaves the function
  start unknown. [ClassicMac]
- Mnemonics (DDAT, CODE, …) are ClassicMac's names for the opcodes. [ClassicMac]

Fragments are listed and modelled as [disassembly.md](../output/disassembly.md) describes.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `pef.export-hash-chains` | Warning | The hash chains do not tile the exports in order, or cover a different number of them | Keeps the exports; lookup still walks the chains | Not traced |
| `pef.export-hash-mismatch` | Warning | An export's key is not its name's hash word | Keeps the export; the hash lookup will not find it | Not traced |
| `pef.export-name-out-of-range` | Error | An export's name lies outside the loader section | Keeps the export with an empty name | Not traced |
| `pef.export-reexport-out-of-range` | Error | A re-export names an import that does not exist | Keeps the export | Not traced |
| `pef.format-version` | Warning | formatVersion is not 1 | Reads on | Not traced |
| `pef.loader-hash-power` | Error | exportHashTablePower is above 18 | Reads no exports | Not traced |
| `pef.loader-library-symbols` | Error | A library's symbols run past the imported symbols | Keeps the library | Not traced |
| `pef.loader-string-out-of-range` | Error | A library or import name lies outside the loader section | Uses an empty name | Not traced |
| `pef.loader-truncated` | Error | The loader section is shorter than its header, a table, or a relocation header's words | Reads what fits | Not traced |
| `pef.pidata-bad-opcode` | Error | A pidata opcode is 5–7 | Stops unpacking; keeps the image so far | Not traced |
| `pef.pidata-length` | Error | The unpacked pidata is not unpackedLength bytes | Keeps what was unpacked | Unpacking past unpackedLength fails the load; ending short of it is accepted [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.pidata-too-long` | Error | The pidata would unpack past the limit (§5) | Stops unpacking | Not traced |
| `pef.pidata-truncated` | Error | A pidata instruction runs past the contents, or an argument is wider than 32 bits | Stops unpacking | Reads at most 5 argument bytes, the fifth whole, and keeps the low 32 bits [Code: the Code Fragment Manager in the Mac OS ROM]; running past the contents not traced |
| `pef.relocation-bad-import` | Error | An instruction names an import that does not exist | Skips the word | No check [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.relocation-bad-opcode` | Error | An undefined encoding | Stops the header's run; the fixups before it stand | Marks the load failed (−4) and decodes on from the next word; LSEC sub-opcodes 3–15 it skips, both words, with no error [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.relocation-bad-section` | Error | An instruction or a relocation header names a section that does not exist | Skips the instruction (or the header) | No check on an instruction's section; a header for no section is never looked up [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.relocation-duplicate-header` | Warning | A second relocation header names a section | Runs only the first | Runs only the first [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.relocation-nested-repeat` | Error | A repeat is reached while another runs | Stops the run | Never finishes: one counter serves every repeat (§1.8) [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.relocation-out-of-range` | Error | Relocated words fall outside the section | Skips them; one report per section with the count and the first | No check: writes where relocAddress points [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.relocation-repeat-at-start` | Error | A repeat goes back more words than precede it | Stops the run | Not traced |
| `pef.relocation-repeat-zero` | Error | LRPT with r = 0 | Does not repeat; runs on | Never finishes (§1.8) [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.relocation-runaway` | Error | A run makes more fixups than the limit (§5) | Stops the run | Not traced |
| `pef.relocation-truncated` | Error | A two-word instruction is the last word | Stops the run | Reads the word after the list as its second word [Code: the Code Fragment Manager in the Mac OS ROM] |
| `pef.section-name-out-of-range` | Warning | A section's nameOffset is outside the container | No name | Not traced |
| `pef.section-out-of-range` | Error | A section's contents run past the container | Clips them | Not traced |
| `pef.sections-truncated` | Error | The section headers run past the container | Reads the headers that fit | Not traced |
| `pef.string-unterminated` | Warning | A section name or loader string has no NUL before the end | Takes the bytes to the end | Not traced |
| `traceback.bad-offset` | Warning | A tb_offset reaches before the start of the code | Keeps the table without a function start | Not traced |
| `traceback.extension-unread` | Warning | A table sets has_vec or has_ext_table | Keeps the table; its length stops at alloca_reg | Not traced |
| `traceback.truncated` | Error | A traceback table runs past the code | Leaves the table out | Not traced |

## 7. Verification

Hand-built containers (`tests/ClassicMac.Code.Tests`, built with `PefBuilder`):

- `PefContainerTests`: the header and section headers (every field a distinct value), names, which kinds are placed
  in memory (by kind, whatever instSectionCount says), the first loader section wherever it is, a loader with lengths
  0, images zero-filled to totalLength, copying only unpackedLength or nothing, pidata sections unpacked and checked
  in both directions, a huge totalLength capped, no sections, a `'m68k'` container, the header and tag checks,
  damaged section tables.
- `PatternDataTests`: every opcode, the count argument (with opcodes 3 and 4 too), big-endian 7-bit arguments of 1–5
  bytes, the largest 5-bit count, empty repeats and interleaves, and each damage case (undefined opcodes, truncation,
  the length limit without allocating it).
- `PefLoaderTests`: entry points, libraries, imports with class, weak flag and library, relocation headers, exports
  with keys, absolute exports and re-exports, the hash word (stopping at a NUL) and index against hand-worked
  values, lookup through colliding chains and between names with the same hash word, names read by the key's length,
  a weak library's symbols, import classes and flags kept as stored, a relocation header's reserved field, empty
  slots, the largest hash power, and each damage case. `PefBuilder` takes each export's hash word as a literal, so
  the loader's own hash does not build its test input.
- `PefRelocatorTests`: every opcode's action and operand decoding, the opcode boundaries, mnemonics, every "add"
  moving on 4, DDAT, DESC, DSC2 and VTBL with sectionC and sectionD changed, the largest SYMR and a large LSYM,
  2-byte-aligned DELTA fixups; repeats counting words (over two-word instructions, starting inside one, of SECN, the
  largest block and count, the import index carried through), each against the Code Fragment Manager's own result;
  undefined and truncated instructions (LSEC 3 among them), repeats at the start, of repeats and LRPT 0, out-of-range
  words, imports and sections, runaways, a second header for a section, listing without an image.
- `TransitionVectorTests`: main, init and tvector exports read as code and TOC offsets with their sections, a TOC
  in another section (DTIS), 12-byte vectors (DESC).
- `TracebackTableTests`: each optional field alone and all in order, bytes 4 and 5 and parmsonstk adding no field,
  has_vec and has_ext_table reported, the scan after `blr` (and not after `b` or `bctr`), truncation (an offset at the
  end of the code included) and a tb_offset before the code.

Real fragments, gated on `CLASSICMAC_CODE_CORPUS` (they skip without it; Apple's files are never committed).
`CorpusTests` checks each fragment the way the Code Fragment Manager would use it: every section image builds to
totalLength, every relocation applies inside its section with no diagnostic, every export is found through the hash
table. On top of that:

- the Mac OS 9 System file's NQD fragment: its three sections (code, pidata, loader), its 199 imports from four
  libraries, 269 exports (239 transition vectors, 4 data, 26 re-exports, each named as its import), hash power 5,
  init 1:$1470 and no main or term, 1,715 fixups, TOC base `$87C`, every transition vector export in the code
  section; a second NQD build (200 imports, 269 exports, init 1:$1478);
- the Mac OS 9 System file's FontManager: 114 exports, 231 imports from 14 libraries, hash power 4, 1,312 fixups;
- Disk Copy 6.1.2's PowerPC application: main 1:$BB8, 446 imports from eight named libraries, no exports, 871 fixups;
- Disk Copy 6.5's: main 1:$22D0, 688 imports, 2,921 fixups, TOC base `$8000`;
- Mac OS 9.2.2's 90 fragments (`FragmentFacts`, counted by an independent reader): imports, libraries, exports,
  re-exports, hash power, main, init, term, the fixup count and the count of each relocation mnemonic, for every one;
- the Mac OS 9 System file's 114 resources that hold a container from offset 0 (`'ncod'`, `'nlib'`, `'ndrv'`,
  `'nift'`, `'ntrb'`, `'fovr'`, `'vdig'`, `'cdek'`, `'sfvr'` and others) each verify, with their fixup counts, 21,784
  in all; `'ncod'` 3's Constant section and its repeated SECN (fixups at $188, $18C and $190 to section 2, over the
  words $0, $10 and $18); `'ntrb'` -20987, a pidata section and the loader with main 0:$0 and DTIS first;
- all 288 sample containers (the 90 fragments, NQD's two builds, FontManager, both Disk Copies, the System file's
  data-fork fragments, each offset once, and its native resources) make 117,236 fixups, the count the Code Fragment
  Manager's relocation interpreter gives for them;
- every `.pef` in the corpus verifies.
- `PpcCorpusTests`: NQD has no traceback table; Disk Copy 6.1.2 has one (`__uitrunc` at `$3B064`, tb_offset `$68`,
  one floating-point parameter); Disk Copy 6.5 has 1,619, all named, the first `.TradHighestUnitNumber` at `$44`
  (function `$1C`) and the last `.HandleBurn` at `$B9AB8` (function `$B98BC`).

`PpcCorpusTests` scans the code sections of NQD (no names), Disk Copy 6.1.2 (one) and Disk Copy 6.5 (1,619 names,
the first `.TradHighestUnitNumber` at `$1C` with tb_offset `$28`): every located function starts on a valid
instruction and ends with the `blr` before its table. The System file's `'cfrg'` members and fat code resources are
in [cfrg.md §7](cfrg.md#7-verification) and [code-resources.md §7](code-resources.md#7-verification).

## 8. Not covered

- Writing PEF.
- CFM-68K containers: read as the same structure, never seen.
- Section kinds 5, 6 and 8, named sections and the CDIS, LRPT and LSEC opcodes: read as documented, not seen in a
  sample.
- The exception section's contents.
- A traceback table's vec_ext and ext_table: reported, not read (§1.12).
- A traceback table after a function that does not end in `blr` (a tail call `b`, or `bctr`): the scan does not find
  it.
- What the Code Fragment Manager does with the damage marked "Not traced" in §6.
- Resolving imports against other fragments: addresses are the caller's.

## 9. References

1. Apple Computer, *Mac OS Runtime Architectures* (1997): "PEF Structure", "Transition Vectors", the CFM-based
   runtime architecture.
2. Apple Computer, Universal Interfaces 3.4: `PEFBinaryFormat.h`, `CodeFragments.h`.
3. IBM, AIX `sys/debug.h` / `tbtable.h`: the traceback table layout the Mac OS compilers use.
