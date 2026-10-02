# 68k global-data initializers

A 68k application's globals live below A5 ([code-segments.md §1.1](code-segments.md#11-the-a5-world)). Each
development system stores their initial values, and the relocations they need, its own way: MPW in data at the end of
the `%A5Init` segment, run by MPW's initialization routines; CodeWarrior in `'DATA'` 0, unpacked by its startup code;
Retro68 in `'DATA'` 0 as a raw image with `'RELA'` relocation streams. ClassicMac decodes all three into the bytes
written (with their A5 offsets) and the relocated longs; which one a file uses is detected with its model
([code-segments.md §2.1](code-segments.md#21-detecting-the-model)).

| | |
| --- | --- |
| Identified by | MPW: a `'CODE'` segment ending with `'mpwd'`. CodeWarrior: `'DATA'` 0 with CodeWarrior's startup in `'CODE'` 1. Retro68: `'RELA'` resources |
| ClassicMac | Reads; `ClassicMac.Code.M68k` (`MpwA5Init`, `CodeWarriorData`, `Retro68Relocations`) |
| Verified against | ResEdit 2.1.3, Disk Copy 6.1.2 and 13 of the Mac OS 9 System file's resources (MPW); a CodeWarrior application; two builds of a Retro68 application |
| Sources | The code that reads each: MPW's `%A5Init` routines, CodeWarrior's 68k startup, Retro68's runtime (`Retro68ApplyRelocations`, `Retro68Relocate`) as linked into a Retro68 application |

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

### 1.1 MPW `%A5Init`

The segment named `%A5Init` ends with an 8-byte trailer:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| end − 8 | 4 | headerOffset | The header's offset in the segment |
| end − 4 | 4 | signature | `'mpwd'` |

The header, at headerOffset:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | belowA5Size | The bytes below A5 the routines clear and initialize; they start at A5 − belowA5Size |
| +$04 | 2 | version | 1; on any other the routine returns −1 and leaves the globals alone |
| +$06 | 2 | reserved | |
| +$08 | 4 | dataOffset | The packed data, from the header |
| +$0C | 4 | relocOffset | The relocations, from the header |

[Code: the %A5Init routines of an MPW application] belowA5Size equals `'CODE'` 0's belowA5 in Disk Copy 6.1.2;
ResEdit 2.1.3's is 2 bytes less [Verified: ResEdit 2.1.3, Disk Copy 6.1.2].

A varint:

| First byte | Value |
| --- | --- |
| `0xxxxxxx` | The byte |
| `10xxxxxx`, 1 byte | The 14 bits |
| `110xxxxx`, 2 bytes | The 21 bits |
| `1110xxxx`, 4 bytes | The 4 bytes after the first |
| `1111xxxx` | Two varints follow: the value, then a repeat count |

[Code: the %A5Init routines of an MPW application] The low nibble of `1110xxxx` and `1111xxxx` is not used.

### 1.2 CodeWarrior `'DATA'` 0

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | codeRelocOffset | Where the fourth relocation list (the first of the code's) starts in the resource |
| +$04 | … | Three blocks | Below |
| … | … | Six relocation lists | |

A block: a signed long, its start from A5, then ops to a `$00`:

| Op | Writes |
| --- | --- |
| `1xxxxxxx` | Copy the next (b AND `$7F`) + 1 bytes |
| `01xxxxxx` | Skip (b AND `$3F`) + 1 bytes |
| `001xxxxx`, then a byte | That byte, (b AND `$1F`) + 2 times |
| `0001xxxx` | `$FF`, (b AND `$0F`) + 1 times |
| `$00` | The end of the block |
| `$01` | Skip 4; `$FF $FF`; copy 2 |
| `$02` | Skip 4; `$FF`; copy 3 |
| `$03` | `$A9 $F0`; skip 2; copy 2; skip 1; copy 1 |
| `$04` | `$A9 $F0`; skip 1; copy 3; skip 1; copy 1 |
| `$05`–`$0F` | The startup calls `SysError` 15 |

The six relocation lists, in order: longs in the globals that get A5 added, the code's address, the globals' address;
then longs in the code that get A5 added, the code's address, and again the code's address. Each list is a long count,
then that many entries, each moving a running offset d (from 0). The startup loops while the count, signed, is above
0 (`TST.L`; `BGT`): a count with its top bit set reads no entries.

| Bytes | d becomes |
| --- | --- |
| `1xxxxxxx` | d + 2 × (the low 7 bits, signed) |
| `01xxxxxx xxxxxxxx` | d + 2 × (the low 14 bits, signed) |
| `00xxxxxx` and 3 bytes | 2 × (the low 30 bits, signed): an absolute offset |

[Code: the startup of a single-segment CodeWarrior 68k application] Code offsets are offsets in `'CODE'` 1, its
4-byte header included [Verified: a CodeWarrior application].

### 1.3 Retro68 `'RELA'`

`'RELA'` n relocates `'CODE'` n; `'RELA'` 0 relocates `'DATA'` 0. A `'RELA'` holds one or two lists of unsigned LEB128
values (little-endian 7-bit groups, bit 7 set on every byte but the last), each list ended by a 0 byte. A running
position starts at −1; each value v moves it by v >> 2, and v AND 3 is the kind. The long at the position (from the
segment's code start, or `'DATA'` 0's start) gets the kind's displacement added:

| Kind | Added | The stored long |
| --- | --- | --- |
| 0 | The segment's address: its resource start (in `'DATA'` 0, nothing) | An offset in the `'CODE'` resource |
| 1 | The A5 displacement | An A5 offset into the initialized data |
| 2 | The A5 displacement | An A5 offset into the zero-filled data |
| 3 | The A5 displacement | An A5 offset above A5: a jump-table entry + 2 (jtOffset + 2 + 8k) |

[Code: Retro68's runtime, Retro68Relocate and its segment loader] The A5 displacement is A5 less what the long was
linked for: for a far segment A5 less the far header's +$18 (0 in the file, so A5; the loader then stores A5 there);
for `'CODE'` 1 `Runtime` A5 less the link-time end of the zero-filled data, which a multisegment application links
at 0; for `'DATA'` 0 A5. The segment's address replaces the far header's +$20 the same way. So in the file kinds 1 to
3 are A5 offsets and kind 0 a resource offset [Verified: a Retro68 application: every relocated long in two builds].
Kind 0 occurs in `Runtime` and in other segments [Verified: a Retro68 application].

The first list is absolute. When the byte after its 0 is not 0, that 0 is skipped and a second list follows, read the
same way from −1, whose longs are PC-relative: the long becomes long − its own address + the displacement
[Code: Retro68's runtime, Retro68ApplyRelocations]. A `'RELA'` with no second list ends `$00 $00` [Verified: a
Retro68 application]. A list ends on a 0 byte, not on a value of 0: `$80 $00` is a step of 0 with kind 0
[Code: Retro68ApplyRelocations].

The code start is +$04 in a near segment, +$28 in a far one. Every position in the samples is even and inside its
target, and every stream is read to its `$00 $00` [Verified: a Retro68 application].

`'DATA'` 0 is the initialized data's raw image, placed at A5 − belowA5; the rest of belowA5 is zero-filled
[Fitted: a Retro68 application].

## 2. Reading

### 2.1 MPW `%A5Init`

1. Read the trailer; read the header at headerOffset. Stop if version is not 1.
2. The data, from header + dataOffset, with q = 0 (the destination is A5 − belowA5Size + q):
   1. repeat = 1. Read a byte b.
   2. n = b AND `$0F`. If n is 0, n is a varint (which may set repeat); if that is 0 too, the data ends. Else n = 2n.
   3. skip = b AND `$F0`. If it is 0, skip is a varint (which may set repeat). Else skip = 2 × (b >> 4).
   4. repeat times: q += skip; copy the next n bytes to q; q += n. The count is a `SUBQ.L #1; BNE` loop: a repeat of
      0 copies once, then wraps and runs on for 2³² − 1 more.
3. The relocations, from header + relocOffset, with pos = 0 (from A5 − belowA5Size):
   1. count = 1. Read a byte b.
   2. If b is 0, read the next byte b2: 0 ends the list; with bit 7 set, delta is the long starting at b2; else delta
      = b2 and count is a varint.
   3. Else, with bit 7 set, delta = ((b AND `$7F`) << 8) OR the next byte. Else delta = b.
   4. count times: pos += 2 × delta (in 32 bits, so a long delta's top bit drops out); the long at pos gets A5 added.
      As in the data, a count of 0 patches once, then runs on.

[Code: the %A5Init routines of an MPW application] Both applications' data and relocations end exactly at the
trailer [Verified: ResEdit 2.1.3, Disk Copy 6.1.2]; in the System file's resources they end at the trailer or one pad
byte before it [Verified: the Mac OS 9 System file].

### 2.2 CodeWarrior `'DATA'` 0

1. Read codeRelocOffset.
2. Read three blocks (§1.2): each sets q to its start from A5 and writes at q as its ops say, to its `$00`.
3. Read the six relocation lists; the fourth should start at codeRelocOffset. A count below 0 (signed) reads no
   entries.

[Code: the startup of a single-segment CodeWarrior 68k application] The sample's three blocks and six lists end exactly
at the end of the resource [Verified: a CodeWarrior application].

### 2.3 Retro68 `'RELA'`

1. position = −1; the list is absolute.
2. If the next byte is 0, the list ends: go to 4.
3. Read a LEB128 value v; position += v >> 2; the long at code start + position gets kind v AND 3's displacement
   added (less its own address in the relative list). Go to 2.
4. After the absolute list: if the byte after the 0 is not 0, skip the 0, set position = −1, and read the relative
   list from step 2. Otherwise, or after the relative list, stop.

[Code: Retro68's runtime, Retro68ApplyRelocations]

## 3. Writing

None.

## 4. Variants

- MPW near (ResEdit 2.1.3, `'CODE'` 5) and far (Disk Copy 6.1.2, `'CODE'` 2) applications carry the same `%A5Init`
  format; the segment's own header is near or far [Verified: ResEdit 2.1.3, Disk Copy 6.1.2].
- The same initializer data ends 13 of the Mac OS 9 System file's resources: `'scod'` −16476, −16472 and −16467,
  `'ptch'` −20917, `'wart'` 1, `'otlm'` 9, `'otdr'` 9, `'AINI'` 2017, `'enet'` 1648 and `'DRVR'` 22, 53, −20267 and
  −20268 [Verified: the Mac OS 9 System file]. ClassicMac reads it with `MpwA5Init.Read`; the application reader does not
  look for it there.
- CodeWarrior's globals reach above A5: the sample's third block runs from A5 + `$28` to A5 + `$3BD54`, and its
  aboveA5 is `$3F578` [Verified: a CodeWarrior application].

## 5. ClassicMac

- The runs are reported as bytes and A5 offsets; a repeated MPW run appears once per repeat. Relocations are reported
  as offsets (from A5 for MPW; as decoded for CodeWarrior; in the resource for Retro68), not applied. [ClassicMac]
- MPW: a header that does not fit before the trailer, or a version other than 1, is reported and nothing more is
  read. A run past belowA5Size stops the data; relocations past it are left out, reported once with their count, and
  the list goes on; a relocation count above half of belowA5Size cannot be real and stops the relocations. A repeat
  or count of 0 is done once, as the routine does, then reported, and that part stops (the routine would run away).
  [ClassicMac]
- CodeWarrior: an op `$05`–`$0F` stops reading (the blocks so far are kept, no relocations); a `'DATA'` 0 shorter than
  its first long is reported by the application reader and not read; a negative list count is reported and the list
  read as empty, as the startup does. [ClassicMac]
- Retro68: both lists are read, each relocation marked `Relative` or not; a value wider than 32 bits stops the list; a
  position outside the target is left out and the list goes on; an odd position is reported (a 68000 would fault on
  it) and kept. A `'RELA'` whose target does not exist is reported and not read; one whose target cannot be
  decompressed is not read (the target is reported). [ClassicMac]

The initializers are summarised in the jump table's model and marked as data in listings as [disassembly.md](../output/disassembly.md) describes.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `m68k.a5init-count-zero` | Error | A data repeat or a relocation count is 0 | Does it once, then stops that part | Does it once, then the count wraps and the routine runs on |
| `m68k.a5init-data-range` | Error | A data run goes past belowA5Size | Stops the data | Not traced |
| `m68k.a5init-header` | Error | The header does not fit before the trailer | Reads nothing more | Not traced |
| `m68k.a5init-reloc-range` | Error | Relocations lie outside belowA5Size (reported once per list), or a count is more than the globals hold | Leaves them out; stops on such a count | Not traced |
| `m68k.a5init-truncated` | Error | The data or relocations run past the segment | Keeps what was read | Not traced |
| `m68k.a5init-version` | Error | The header's version is not 1 | Reads nothing more | The routine returns −1 without touching the globals |
| `m68k.cw-code-reloc-offset` | Warning | The fourth list does not start at codeRelocOffset | Reads on | Not traced |
| `m68k.cw-data-op` | Error | A block op is `$05`–`$0F` | Stops reading | The startup calls `SysError` 15 |
| `m68k.cw-data-truncated` | Error | `'DATA'` 0 ends in a block or a list, or is shorter than its first long | Keeps what was read | Not traced |
| `m68k.cw-reloc-count` | Warning | A relocation list's count is negative (top bit set) | Reads the list as empty; goes on | Reads no entries; goes on |
| `m68k.rela-odd` | Warning | A Retro68 relocation is at an odd offset | Keeps it | A 68000 faults |
| `m68k.rela-range` | Error | A Retro68 relocation lies outside its target | Leaves it out | Not traced |
| `m68k.rela-target` | Warning | A `'RELA'` n has no `'CODE'` n, or `'RELA'` 0 no `'DATA'` 0 | Does not read it | Not traced |
| `m68k.rela-truncated` | Error | A `'RELA'` ends without its 0 | Keeps what was read | Not traced |
| `m68k.rela-value` | Error | A `'RELA'` value is wider than 32 bits | Stops the list | Not traced |

## 7. Verification

- Hand-built data (`tests/ClassicMac.Code.Tests/M68k`):
  - `MpwA5InitTests`: the trailer and header; every count and skip form of the data, every relocation form, every
    varint form and its limits (`$BF $FF`, `$DF $FF $FF`, `$E5…` with the nibble set); a repeat on the skip varint,
    on both (the second wins) and on a relocation count (not used); repeats and counts of 0; runs and relocations at
    the edge of the globals; the version check; each damage case.
  - `CodeWarriorDataTests`: the three blocks with every op, each short op at its limits, the six lists with every
    delta form and its limits, a negative count, the codeRelocOffset check, ops `$05`–`$0F`, truncation in a block, a
    literal and a list.
  - `Retro68RelocationsTests`: near and far code starts, the −1 start and the step, `'DATA'` positions, the terminator,
    the relative list (after an empty first list too, and cut short), `$00 $00`, the non-canonical `$80 $00`,
    truncation, wide values, odd and out-of-range positions.
  - `CodeApplicationTests`: each initializer found by its model; `'RELA'` streams ending `$00 $00`, kind 0 in a far
    segment; `'RELA'` without a target or with one that cannot be decompressed.
- Gated on `CLASSICMAC_CODE_CORPUS` (`M68kCorpusTests`; skipped without it):
  - ResEdit 2.1.3's `'CODE'` 5: header at `$1B2`, belowA5Size `$A9E`, 55 runs, 58 relocations (the first at A5 −
    `$A92`), the data ending at 915 and the relocations at 940, 8 bytes before the end.
  - Disk Copy 6.1.2's `'CODE'` 2: header at `$1D6`, belowA5Size `$158C`, 60 runs, 36 relocations (the first at A5 −
    2,624), ending at 861 and 886.
  - A CodeWarrior application's `'DATA'` 0: codeRelocOffset `$52D2`; blocks A5 − `$F10` to A5, A5 + `$28` (empty), A5 +
    `$28` to A5 + `$3BD54`; lists of 25, 10, 0, 26,862, 391 and 0 entries; read to its end (`$DA4C`).
  - The Mac OS 9 System file: the 13 resources of §4, each with its header, belowA5Size, run and relocation counts,
    ends and first relocation (`'scod'` −16467: header `$1AE`, belowA5Size `$EFA`, 235 runs, 172 relocations, ending
    at 1,614 and 1,625; `'DRVR'` 53: header `$3550`, `$660`, 38 runs, 9 relocations, the first at A5 − `$5B0`).
  - A Retro68 application, the build the numbers were counted on (117,059 bytes): `'CODE'` 1's 1,291 relocations are
    863, 256, 97 and 75 of kinds 0 to 3, `'CODE'` 2's 497 are 0, 189, 38 and 270, `'RELA'` 0's 18 are 0, 12, 4 and 2;
    no relative lists; every kind-0 long is an offset in its resource (the largest, `$13F70`, is `Runtime`'s length),
    every kind-1 long an A5 offset into the initialized data, kind-2 into the BSS, kind-3 a jump-table entry + 2;
    no diagnostics. Another build (121,214 bytes) has one kind-0 relocation in `'CODE'` 2 (`$3D2E`).

## 8. Not covered

- Applying the initializers: building the globals and relocating the code.
- What the CodeWarrior globals lists' offsets count from (A5, or the globals' start); they are reported as decoded.
- Retro68 single-segment applications (no `_MULTISEG_APP`), whose runtime takes another path; and the relative
  list, which no sample has.
- Multi-segment CodeWarrior applications and other development systems (THINK C, Symantec).
- `'scod'` and other System resources that carry the MPW initializer.

## 9. References

1. Apple Computer, *Mac OS Runtime Architectures* (1997), the classic 68k runtime architecture: the A5 world.
2. Retro68 (Wolfgang Thaller), the GCC-based toolchain that writes `'RELA'`; GPL. Its runtime's relocation routines
   were read in a Retro68 application's own code, as the code that consumes the format; nothing is ported.
