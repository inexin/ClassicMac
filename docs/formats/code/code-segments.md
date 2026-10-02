# 68k applications: `'CODE'` segments and the jump table

A classic 68k application keeps its code in `'CODE'` resources. `'CODE'` 0 gives the sizes of the A5 world and holds
the jump table, through which every call between segments goes; `'CODE'` 1 and up are the segments the Segment Loader
loads on demand. The near model (the original) has 4-byte segment headers and 16-bit entries; the far model ("32-bit
everything", used by MPW and Retro68) marks the table with a far marker, gives segments a `$28`-byte header and
relocates them by lists stored in the segment. ClassicMac reads `'CODE'` 0 and the jump table, the segments and their
headers, the far relocation lists, the entry point (including a decompression bootstrap's saved entry), resolves
`n(A5)` to jump-table entries, detects how the code was built and reads the global-data initializer that build uses
([code-data.md](code-data.md)).

| | |
| --- | --- |
| Identified by | A resource fork with `'CODE'` 0 of at least 16 bytes. Usually file type `APPL` |
| ClassicMac | Reads; `ClassicMac.Code.M68k` (`CodeApplication`, `JumpTableEntry`, `SegmentHeader`, `FarRelocations`) |
| Verified against | ResEdit 2.1.3 (MPW near); Disk Copy 6.1.2 (MPW far, fat); a CodeWarrior application; a Retro68 application |
| Sources | *Inside Macintosh II*, the Segment Loader; *Mac OS Runtime Architectures* (Apple, 1997), the classic 68k runtime architecture (the A5 world, the jump table, the far model and its segment header) |

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

### 1.1 The A5 world

A running application's globals are addressed from register A5:

| From A5 | Contents |
| --- | --- |
| −belowA5 … −1 | The application's globals and QuickDraw's globals |
| 0 | A pointer to QuickDraw's globals |
| 0 … $1F | The application parameters (32 bytes) |
| jtOffset (`$20`) … | The jump table |

[Doc: Inside Macintosh II, the Segment Loader] aboveA5 is `$20` + the jump table's size in MPW and Retro68
applications; CodeWarrior also puts globals above A5, so its aboveA5 is larger ([code-data.md §1.2](code-data.md#12-codewarrior-data-0))
[Verified: ResEdit 2.1.3, Disk Copy 6.1.2, a CodeWarrior application, a Retro68 application].

### 1.2 `'CODE'` 0

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | aboveA5 | Bytes above A5: the application parameters and the jump table |
| +$04 | 4 | belowA5 | Bytes below A5: the globals |
| +$08 | 4 | jtSize | The jump table's size; a multiple of 8 |
| +$0C | 4 | jtOffset | The jump table's offset from A5: `$20` |
| +$10 | jtSize | Jump table | 8-byte entries; entry i is at A5 + jtOffset + 8i |

[Doc: Inside Macintosh II, the Segment Loader] jtOffset is `$20` in every sample [Verified: ResEdit 2.1.3, Disk Copy
6.1.2, a CodeWarrior application, a Retro68 application].

### 1.3 Jump-table entries

Each entry is 8 bytes, written here as words:

| Form | Words | Meaning |
| --- | --- | --- |
| Near, unloaded (in the file) | offset, `$3F3C` seg, `$A9F0` | `MOVE.W #seg,-(SP)`; `_LoadSeg`. offset is from the segment's code, after its 4-byte header |
| Near, loaded (in memory) | seg, `$4EF9` addr.l | `JMP addr` |
| Far marker | `$0000 $FFFF $0000 $0000` | Entry 1 of a far table |
| Far, unloaded (in the file) | seg, `$A9F0`, offset.l | `_LoadSeg`; offset is from the start of the resource, the far header included |
| Far, loaded (in memory) | seg, `$4EF9` addr.l | `JMP addr` |

[Doc: Inside Macintosh II, the Segment Loader; Mac OS Runtime Architectures, the far model] A table is far when entry
1 is the far marker; entry 0, the entry point, stays in the near form [Verified: Disk Copy 6.1.2, a Retro68
application]. The first routine of a far segment is at `$28`, just after its header [Verified: Disk Copy 6.1.2]. A
far entry's offset counts from the resource start also when it points into a near segment: Retro68's far entries into
`'CODE'` 1 `Runtime` land on routine starts that way [Verified: a Retro68 application]. The marker is only the marker at
entry 1; the same bytes anywhere else are none of the forms.

A call goes to the entry plus 2 (the `MOVE.W` or the `_LoadSeg`): `JSR n(A5)` with n = jtOffset + 8i + 2. `PEA` or
`LEA` of the same address is a procedure pointer [Doc: Inside Macintosh II, the Segment Loader] [Verified: ResEdit
2.1.3: all 145 `JSR n(A5)` in `'CODE'` 1].

### 1.4 Segment headers

The near header, 4 bytes:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | firstEntryOffset | The segment's first jump-table entry, as a byte offset from the table's start (8 × index) |
| +$02 | 2 | entryCount | |

The far header, `$28` bytes:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | marker | `$FFFF` |
| +$02 | 2 | reserved | 0 |
| +$04 | 4 | nearEntryOffset | The first "near" entry, from the table's start |
| +$08 | 4 | nearEntryCount | |
| +$0C | 4 | farEntryOffset | The first "far" entry, from the table's start |
| +$10 | 4 | farEntryCount | |
| +$14 | 4 | a5RelocOffset | The A5 relocation list's offset in the resource; 0 for none |
| +$18 | 4 | a5AtLastReloc | The A5 the segment was last relocated for; 0 in the file |
| +$1C | 4 | pcRelocOffset | The PC relocation list's offset in the resource; 0 for none |
| +$20 | 4 | addressAtLastReloc | The address the segment was last relocated for; 0 in the file |
| +$24 | 4 | reserved | 0 |

[Doc: Mac OS Runtime Architectures, the 32-bit everything segment header] MPW fills the first pair, Retro68 the second
[Verified: Disk Copy 6.1.2, a Retro68 application]. A segment owns the entries of each pair whose count is not 0 (both
pairs' when both are set) [Verified: Disk Copy 6.1.2, a Retro68 application].

The code starts after the header: at +$04 in a near segment, +$28 in a far one.

### 1.5 Far relocation lists

At a5RelocOffset and pcRelocOffset in a far segment, MPW stores lists of the longs to relocate when the segment is
loaded: each long in the A5 list gets A5 added, each in the PC list the segment's address. A list is a run of deltas
from a running offset that starts at 0, the resource's start:

| Bytes | Delta |
| --- | --- |
| `$00` | The end of the list |
| `$01`–`$7F` | 2 × the byte |
| `$80`–`$FF`, then one byte | 2 × (((first AND `$7F`) << 8) OR second) |

[Doc: Mac OS Runtime Architectures, the 32-bit everything segment header] [Verified: Disk Copy 6.1.2: each list ends
exactly at its `$00`]. A form for deltas of 64 KB and more, `$80 $00` and a long, is expected but was not seen.

### 1.6 The entry point

The application starts at jump-table entry 0 [Doc: Inside Macintosh II, the Segment Loader]. An unloaded entry 0
gives the segment and the offset: in a near segment, 4 + offset into the resource.

A decompression bootstrap can take entry 0 over. ResEdit 2.1.3's entry 0 is `'CODE'` 66 +$0C (resource +$10), a
segment that installs a patch, copies the original entry 0 back into the table and jumps to it
[Code: ResEdit 2.1.3's 'CODE' 66]. Its shape:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Near header | |
| +$04 | 4 | jtOffset | Equals `'CODE'` 0's (`$20`) |
| +$08 | 8 | The original entry 0 | A near unloaded entry: offset, `$3F3C` seg, `$A9F0` |
| +$10 | | Code | Where entry 0 points |

[Fitted: ResEdit 2.1.3] ResEdit's saved entry is `'CODE'` 1 +$3634. `'CODE'` 1's header still claims entry 0
[Verified: ResEdit 2.1.3].

### 1.7 Resolving `n(A5)`

1. d = n − 2 − jtOffset.
2. If d ≥ 0, d is a multiple of 8, d / 8 is below the entry count and that entry is in one of the entry forms (not
   the far marker), `n(A5)` is the call target of entry d / 8: its segment and offset.
3. Otherwise `n(A5)` is data: a global when n < 0, an application parameter when n is 0 to `$1F` (`0(A5)` is the
   QuickDraw globals pointer).

[Doc: Inside Macintosh II, the Segment Loader] [Verified: ResEdit 2.1.3, Disk Copy 6.1.2]

### 1.8 Models

How the code was built shows in its resources:

| Model | Shape |
| --- | --- |
| MPW near | No far headers; a segment ends with MPW's `%A5Init` trailer ([code-data.md §1.1](code-data.md#11-mpw-a5init)) |
| MPW far | The far marker at entry 1; far headers filling the first pair |
| Retro68 | `'RELA'` resources ([code-data.md §1.3](code-data.md#13-retro68-rela)); the far marker; far headers filling the second pair; `'CODE'` 1 `Runtime` near |
| CodeWarrior | `'DATA'` 0 ([code-data.md §1.2](code-data.md#12-codewarrior-data-0)) and `'CODE'` 1's code starting with CodeWarrior's startup: `$9DCE $598F $2F3C 'CODE'` (`SUBA.L A6,A6`; `SUBQ.L #4,SP`; `MOVE.L #'CODE',-(SP)`) |
| Fat | `'cfrg'` 0 beside the `'CODE'` resources ([cfrg.md](cfrg.md)) |

[Fitted: ResEdit 2.1.3, Disk Copy 6.1.2, a CodeWarrior application, a Retro68 application]

## 2. Reading

1. Read `'CODE'` 0's 16-byte header and jtSize / 8 entries; take each entry's form (§1.3), in this order: entry 1
   equal to the far marker; words 1 and 3 `$3F3C` and `$A9F0` (near unloaded); word 1 `$A9F0` (far unloaded); word 1
   `$4EF9` (loaded: far after the marker, else near).
2. Read every `'CODE'` 1 and up in ID order, decompressing a compressed one
   ([compressed-resources.md](../resources/compressed-resources.md)). A segment starting `$FFFF` has the far header,
   any other the near one (§1.4).
3. In a far segment, read the A5 and PC relocation lists (§1.5).
4. Check every unloaded entry names a segment that exists and lands inside it.
5. Take the entry point from entry 0, and a bootstrap's saved entry (§1.6).
6. Detect the model (§2.1) and read its initializer ([code-data.md](code-data.md)).

### 2.1 Detecting the model

1. Any `'RELA'` resource: Retro68.
2. Else `'DATA'` 0 and `'CODE'` 1 whose code, after its 4-byte header, starts with CodeWarrior's startup: CodeWarrior.
3. Else the far marker and a far header with a nonzero first-pair count: MPW far.
4. Else no far header and a segment with the `%A5Init` trailer: MPW near.
5. Else unknown.

The `%A5Init` segment is the first segment, in ID order, ending with the trailer. A file with `'cfrg'` 0 is also fat.
[Fitted: ResEdit 2.1.3, Disk Copy 6.1.2, a CodeWarrior application, a Retro68 application]

### 2.2 A bootstrap's saved entry

Entry 0 is a bootstrap's when all hold [Fitted: ResEdit 2.1.3]:

1. It is near unloaded and points at resource offset `$10`.
2. Its segment is a readable near segment of at least `$10` bytes.
3. The long at +$04 equals jtOffset; the words at +$0A and +$0E are `$3F3C` and `$A9F0`.

The saved entry is the near entry at +$08: segment the word at +$0C, offset the word at +$08.

## 3. Writing

None.

## 4. Variants

- A segment may be stored compressed (resource attribute bit 0); ResEdit 2.1.3 has 18 of 25, attributes `$21`
  [Verified: ResEdit 2.1.3].
- An MPW far application may keep near segments: Disk Copy 6.1.2's entry 0 is `'CODE'` 30 +0, a near segment among 29
  far ones [Verified: Disk Copy 6.1.2].
- Retro68 keeps `'CODE'` 1 `Runtime` near and the rest far; segments with nothing in them have both counts 0
  [Verified: a Retro68 application].
- A single-segment CodeWarrior application has one near entry, `'CODE'` 1 +0 [Verified: a CodeWarrior application].
- Segment names are resource names (`%A5Init`, `Main`) [Verified: ResEdit 2.1.3, a Retro68 application].

## 5. ClassicMac

- `CodeApplication.Read` takes a resource fork. No `'CODE'` 0, one shorter than 16 bytes, or one that cannot be
  decompressed throws. [ClassicMac]
- A segment that cannot be decompressed is reported and kept without a header (`IsReadable` false); its entries are not
  checked against it. [ClassicMac]
- Jump-table bytes past the last whole entry are ignored; entries past `'CODE'` 0's end are not read. [ClassicMac]
- A loaded entry is reported as far only after the far marker, from entry 2. [ClassicMac]
- `JumpTableEntry.ResourceOffset` gives an unloaded entry's offset in its resource (near offset + 4). [ClassicMac]
- `ResolveA5` resolves a displacement by §1.7; an unrecognized entry is not a call target either. [ClassicMac]
- An unloaded entry pointing into its segment's header (below +$04 near, +$28 far) is reported and kept. [ClassicMac]
- The globals an initializer writes (MPW's belowA5Size, Retro68's `'DATA'` 0 image) larger than `'CODE'` 0's belowA5
  are reported. [ClassicMac]
- A far relocation list using the `$80 $00` form is reported and reading stops there, rather than guessing at it.
  Relocated longs outside the segment are left out and reported once per list, with their count; the list goes on.
  A long patched inside the far header is kept without a diagnostic. [ClassicMac]
- `SegmentHeader.EntryIndices` caps a pair at 65,536 entries. [ClassicMac]
- When both CodeWarrior and Retro68 shapes are present, Retro68 wins (§2.1's order). [ClassicMac]

Segments are exported with a listing and a model as [disassembly.md](../output/disassembly.md) describes.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `code.compressed` | Error | A segment, `'RELA'` or `'DATA'` 0 is compressed and cannot be decompressed | Leaves it unread | [compressed-resources.md §2.5](../resources/compressed-resources.md#25-failures) |
| `m68k.above-a5` | Warning | aboveA5 is smaller than jtOffset + jtSize | Reads on | Not traced |
| `m68k.below-a5` | Warning | An initializer's globals (MPW's belowA5Size, Retro68's `'DATA'` 0) are larger than belowA5 | Reads on | Not traced |
| `m68k.entry-header` | Warning | An unloaded entry's offset is inside its segment's header | Keeps the entry | Not traced |
| `m68k.entry-missing` | Warning | The jump table is empty | No entry point | Not traced |
| `m68k.entry-range` | Error | An unloaded entry's offset is past its segment's end | Keeps the entry | Not traced |
| `m68k.far-reloc-escape` | Error | A far relocation list uses `$80 $00` | Stops the list | Not traced |
| `m68k.far-reloc-offset` | Error | A relocation list's offset is outside the segment | Reads no list | Not traced |
| `m68k.far-reloc-range` | Error | Relocated longs lie outside the segment | Leaves them out, reported once per list; reads on | Not traced |
| `m68k.far-reloc-truncated` | Error | A list runs past the segment without its `$00` | Keeps what was read | Not traced |
| `m68k.jt-entry` | Warning | An entry is none of the forms | Keeps it as unrecognized | Not traced |
| `m68k.jt-size` | Warning | jtSize is not a multiple of 8 | Ignores the remainder | Not traced |
| `m68k.jt-truncated` | Error | The jump table runs past `'CODE'` 0 | Reads the entries that fit | Not traced |
| `m68k.segment-entry-offset` | Warning | A header's first-entry offset is not a multiple of 8 | Keeps it | Not traced |
| `m68k.segment-header` | Error | A segment is shorter than its header | No header | Not traced |
| `m68k.segment-missing` | Error | An entry names a segment that does not exist | Keeps the entry | Not traced |

The data initializers' codes are in [code-data.md §6](code-data.md#6-diagnostics).

## 7. Verification

- Hand-built forks (`tests/ClassicMac.Code.Tests/M68k`, built with `CodeBuilder`):
  - `CodeApplicationTests`: `'CODE'` 0; near unloaded and loaded entries; the far marker, and its bytes away from entry
    1 or with a nonzero last word; far entries into far and near segments (from the resource start); far segments with
    their relocation lists (landing in the code, and one in the header); entry 0 and a bootstrap's saved entry, with
    another jtOffset and each part of the shape broken; `n(A5)` resolution, also with jtOffset `$30`, and the marker
    and unrecognized entries refused; entries into a header; segment names, attributes and headers; compressed
    `'CODE'` 0 and segments, ones that cannot be decompressed, and the attribute without the signature; each model's
    detection, Retro68 over CodeWarrior, far headers with only the second pair and no `'RELA'`, and the unknown model;
    belowA5 against the initializers; `'cfrg'` 0 making a fat application; each damage case. `CodeBuilder.Far` writes
    distinct nonzero longs at +$18, +$20 and +$24.
  - `SegmentHeaderTests`: the near header; the far header with each pair, both and neither, every field at its own
    offset; damage.
  - `FarRelocationsTests`: one- and two-byte deltas (`$80 $05`, `$FF $FF`), the empty list, lists back to back,
    truncation, the `$80 $00` escape, out-of-range longs (reported once) and list offsets.
- Gated on `CLASSICMAC_CODE_CORPUS` (`M68kCorpusTests`; skipped without it), each read with no warnings:
  - ResEdit 2.1.3: MPW near; aboveA5 `$EC0`, belowA5 `$AA0`, 468 entries; 25 near segments, 18 compressed; entry 0
    `'CODE'` 66 +$0C (resource +$10), saved entry `'CODE'` 1 +$3634; `'CODE'` 2's header (`$2E8`, 15); the `%A5Init`
    in `'CODE'` 5; all 145 `JSR n(A5)` in `'CODE'` 1 resolve to entries naming existing segments.
  - Disk Copy 6.1.2: MPW far and fat; 464 entries; 30 segments, 29 far; entry 0 `'CODE'` 30 +0; `'CODE'` 1's far
    header with 88 A5 and 33 PC relocations, among them the operand of `JSR $0992` at +$1B3C, which resolves to an
    entry into `'CODE'` 17; the `%A5Init` in `'CODE'` 2.
  - A CodeWarrior application: one entry, one near segment of 485,276 bytes, entry `'CODE'` 1 +0 (resource +4); no
    `'cfrg'`.
  - A Retro68 application, the build the numbers were counted on (found by its length, 117,059 bytes): the far marker;
    `'CODE'` 1 `Runtime` near, the rest far; `'CODE'` 2 `Main` filling the second pair (`$D8`, 1); aboveA5 `$100`,
    belowA5 `$17A4`, 28 entries and 8 segments.

## 8. Not covered

- Writing applications.
- The `$80 $00` long-delta form of the far relocation lists, and the loaded far entry form: documented, not seen.
- Running the code: loading segments, applying relocations, building the A5 world.
- Other bootstrap and patch shapes in entry 0.
- What the Segment Loader does with damaged tables (the "Not traced" entries above).

## 9. References

1. Apple Computer, *Inside Macintosh, Volume II*, "The Segment Loader".
2. Apple Computer, *Mac OS Runtime Architectures* (1997), the classic 68k runtime architecture: the A5 world, the jump
   table, the far model ("32-bit everything") and its segment header.
