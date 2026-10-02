# Resource forks

The resource fork is the second fork of a classic Mac OS file: a 16-byte header, 240 reserved bytes, a data area and
a resource map that lists every resource by type, ID, name and attributes. Applications, the System, fonts, sounds and
most documents keep their structured data there, and every container ClassicMac unwraps carries one. Two Resource
Managers read and write it: the native one of Mac OS 9 and the 68k one in ROM; they agree on every well-formed fork
and differ on damaged ones. This document says what each accepts, rejects and misreads when it opens a fork, and how
each lays a fork out when it writes one. ClassicMac reads forks tolerantly, reports where a Mac would refuse them, and
writes them as the Resource Manager leaves them after compacting. Compressed resources are in
[compressed-resources.md](compressed-resources.md).

| | |
| --- | --- |
| Identified by | No signature: the resource fork of a file, a `.rsrc` or `.rsf` file, or the fork carried by a container. The header's four offsets must fit the fork (§2.2) |
| ClassicMac | Reads and writes; `ClassicMac.Resources.ResourceFork` (`Read`, `Write`, `ToArray`) |
| Verified against | Mac OS 9.0 in SheepShaver (its native Resource Manager): crafted forks opened, and forks written and grown through the Resource Manager<br>Real forks from the corpus |
| Sources | *Inside Macintosh: More Macintosh Toolbox*, "Resource File Format"; the Mac OS 9.0 System's native Resource Manager and the 68k Resource Manager in ROM `$077D` (disassembly) |

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

### 1.1 Conventions

The conventions and source tags of [README.md](../README.md#conventions) hold. In addition:

- Header fields are abbreviated: `dO` data offset, `mO` map offset, `dL` data length, `mL` map length (all `u32`,
  from the fork header); `mT` and `mN` are the map's type-list and name-list offsets. `EOF` is the fork's length.
- Offsets are from the start of the fork unless a table says otherwise: map fields from the start of the map,
  reference-list offsets from the start of the type list, name offsets from the start of the name list, data offsets
  from the start of the data area.
- [Code: Mac OS 9.0] is the native Resource Manager (the `'Resources'` code fragment) of the Mac OS 9.0 System;
  [Code: 68k ROM] is the 68k Resource Manager in Mac OS ROM `$077D`, the last revision of the code Macs ran before Mac
  OS 9. [Verified] means SheepShaver running Mac OS 9.0, which runs the native Resource Manager, so no ROM rule is
  [Verified].
- Error codes: `dupFNErr` −48, `eofErr` −39, `posErr` −40, `paramErr` −50, `memFullErr` −108, `badExtResource` −185,
  `CantDecompress` −186, `resNotFound` −192, `mapReadErr` −199.

### 1.2 Fork layout

A fork has four parts [Doc: *More Macintosh Toolbox*, "Resource File Format"]. The header locates the data area and
the map; nothing else is at a fixed place. The layout every Resource Manager writes (§3.3) is:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 16 | Header | `dO`, `mO`, `dL`, `mL` (§1.3) |
| +$010 | 112 | System area | Reserved for the system |
| +$080 | 128 | Application area | Available to the application |
| `dO` (+$100) | `dL` | Data area | One item per resource, no padding (§1.4) |
| `mO` (`dO` + `dL`) | 28 | Map header | §1.5 |
| `mO` + `mT` | 2 + 8 per type | Type list | §1.8 |
| after the type list | 12 per resource | Reference lists | Grouped by type (§1.9) |
| `mO` + `mN` | | Name list | Pascal strings (§1.10); ends at `mO` + `mL` = `EOF` |

- Data-then-map and a data offset of 256 are conventions, not requirements: both Resource Managers accept a map before
  the data (but see §3.2), and a data area that starts before the map and runs into it [Code: Mac OS 9.0] [Verified].
- A file with no resource fork has a fork of length 0; Mac OS 9 refuses to open it with `eofErr` [Verified].

### 1.3 The fork header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `dO` | Offset of the data area |
| +$04 | 4 | `mO` | Offset of the resource map |
| +$08 | 4 | `dL` | Length of the data area |
| +$0C | 4 | `mL` | Length of the resource map |
| +$10 | 112 | System area | Reserved for the system |
| +$80 | 128 | Application area | Available to the application |

[Doc: *More Macintosh Toolbox*]

- The 68k ROM reads the four fields as signed longs, Mac OS 9 as unsigned [Code: 68k ROM] [Code: Mac OS 9.0]. Only
  damaged forks tell the difference (§2.2, §2.3).
- Neither Resource Manager reads the system or application area. Real files carry data in both, so a writer that
  round-trips should keep them. What a new fork holds there is in §3.1.

### 1.4 The data area

A sequence of items, one per resource [Doc: *More Macintosh Toolbox*]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Length | `u32`, the length of the resource data |
| +$04 | length | Data | |

- A reference's data offset (§1.9) points at the item's length word, relative to `dO` [Doc].
- Items are packed with no padding or alignment [Verified].
- The area may hold gaps and stale bytes: a resource rewritten without growing is written in place and leaves the
  rest of its old item behind, until the fork is compacted [Verified].
- Two references may point at the same item. Both load the same bytes [Verified]; preloading and compaction visit only
  the first reference at a given offset [Verified].
- GetMaxResourceSize of a resource not yet loaded is the distance from its item to the next item's offset, including
  the 4-byte length word (for the last item, length + 4) [Verified].

### 1.5 The map header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 16 | Header copy | A copy of the fork header, or zeros [Doc: *More Macintosh Toolbox*] |
| +$10 | 4 | `mNext` | Handle to the next map in the chain; reserved on disk [Doc] |
| +$14 | 2 | `refNum` | `i16`, the file's reference number; reserved on disk [Doc] |
| +$16 | 1 | `mAttr` | Map attributes (§1.6) |
| +$17 | 1 | `mInMemoryAttr` | In-memory map flags (§1.7) |
| +$18 | 2 | `mT` | Offset of the type list from the start of the map [Doc] |
| +$1A | 2 | `mN` | Offset of the name list from the start of the map [Doc] |

- *Inside Macintosh* shows +$16 as one 2-byte attributes field. Both Resource Managers treat it as two separate bytes,
  `mAttr` and `mInMemoryAttr` [Code: 68k ROM] [Code: Mac OS 9.0]; GetResFileAttrs returns `mAttr` in the low byte of
  its result [Verified]. A writer must keep them apart.
- The header copy is never checked: neither Resource Manager compares it with the header [Verified] [Code: 68k ROM].
- `mNext`, `refNum` and the reference handles (§1.9) hold memory in real files. The Resource Manager writes the map
  straight from its in-memory copy, so these fields carry whatever handle and reference number the map had when it
  was written [Code: Mac OS 9.0] [Code: 68k ROM] [Verified]. They are meaningless on disk; a comparer should mask
  them.

### 1.6 Map attributes (`mAttr`, +$16)

| Bit | Mask | Name | Meaning | Source |
| --- | --- | --- | --- | --- |
| 7 | $80 | `mapReadOnly` | The file is read-only: no changes are written | [Doc] |
| 6 | $40 | `mapCompact` | Compact the file when it is next written | [Doc] |
| 5 | $20 | `mapChanged` | The map has changed and must be written | [Doc] |
| 4–1 | — | — | Unused | |
| 0 | $01 | `mapForceSysHeap` | Load the map into the system heap (Apple's private name) | [Code: 68k ROM] [Code: Mac OS 9.0] |

- The Resource Manager keeps `mAttr` as stored on disk, `mapChanged` included: a file opened for writing with
  `mapChanged` already set on disk is rewritten when it is closed [Code: Mac OS 9.0].
- With `mapReadOnly` set on disk, UpdateResFile returns `mapReadErr` and writes nothing [Verified].
- How the byte is written is in §3.2.

### 1.7 In-memory map flags (`mInMemoryAttr`, +$17)

Flags the Resource Manager keeps with an open map. The byte is written out with the map, so some files carry them on
disk.

| Bit | Mask | Name (Apple's equates) | Meaning |
| --- | --- | --- | --- |
| 7–5 | — | — | Unnamed |
| 4 | $10 | `preventFileFromBeingClosedBit` | Not traced |
| 3 | $08 | `twoDeepBit` | Not traced |
| 2 | $04 | `dontCountOrIndexDuplicatesBit` | Not traced |
| 1 | $02 | `overrideNextMapBit` | Not traced |
| 0 | $01 | `decompressionPasswordBit` | The 68k ROM searches a map for `'dcmp'` resources only when this bit is set ([compressed-resources.md §2.4](compressed-resources.md#24-finding-the-decompressor)) |

- When a map is read from disk the byte is masked: the 68k ROM keeps bits 0, 6 and 7 (`& $C1`), Mac OS 9 bits 0 and
  5–7 (`& $E1`) [Code: 68k ROM] [Code: Mac OS 9.0].
- Only bit 0 was traced to a use [Code: 68k ROM]; Mac OS 9 ignores it for decompression [Code: Mac OS 9.0]
  [Verified].

### 1.8 The type list

At map offset `mT` (28 in every fork the Resource Manager writes [Code: Mac OS 9.0] [Code: 68k ROM]):

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Type count − 1 | `u16`; `$FFFF` means no types [Doc: *More Macintosh Toolbox*] |
| +$02 | 8 × n | Type entries | One per type |

Each type entry [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Type | `OSType` |
| +$04 | 2 | Resource count − 1 | `u16` |
| +$06 | 2 | Reference-list offset | From the start of the type list (its count word) |

- Mac OS 9 reads the type count as signed: `$FFFF` (no types) passes, any other value from `$8000` up is rejected
  (§2.2) [Code: Mac OS 9.0] [Verified].
- The reference lists must follow the type list contiguously, in type order. The Resource Manager assumes they do:
  Mac OS 9's open checks walk the references as one run starting right after the type list (§2.2) [Code: Mac OS 9.0].
  A fork whose lists are elsewhere or in another order opens, but GetResInfo on a later type returns ID −1 with
  `resNotFound`, and names are attributed to the wrong resources [Verified].

### 1.9 Reference lists

One 12-byte entry per resource, grouped by type [Doc: *More Macintosh Toolbox*]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | ID | `i16` |
| +$02 | 2 | Name offset | `u16`, from the start of the name list; `$FFFF` = no name |
| +$04 | 1 | Attributes | §1.12 |
| +$05 | 3 | Data offset | `u24`, from the start of the data area |
| +$08 | 4 | Handle | Reserved on disk; in memory, the resource's handle (§1.5) |

- Within a type, references are in the order the resources were added, not sorted by ID [Code: Mac OS 9.0]
  [Code: 68k ROM] [Verified].

### 1.10 The name list

At map offset `mN`: Pascal strings (a length byte and that many bytes), packed with no padding [Doc: *More Macintosh
Toolbox*]. A reference's name offset points at the length byte.

- An empty map puts `mN` at the end of the map (`$1E` in a new fork) [Verified].
- Mac OS 9 also accepts `mN = $FFFF`, meaning no name list [Code: Mac OS 9.0]; `mN = 0` is rejected (§2.2).
- Names are appended in the order they are set; SetResInfo with a name always moves it to the end; removing a name
  repacks the list [Code: Mac OS 9.0] [Code: 68k ROM] [Verified].

### 1.11 Types, IDs and names

- A type is an `OSType`, compared as four bytes, case-sensitively (`'PICT'` and `'pict'` are different types) [Doc:
  *More Macintosh Toolbox*].
- An ID is a signed 16-bit value, unique within its type. *Inside Macintosh* reserves −32768 to −16385, gives −16384
  to −1 to resources owned by other system resources (drivers, desk accessories), 0 to 127 to system resources, and
  leaves 128 to 32767 to applications [Doc: *More Macintosh Toolbox*]. The format does not enforce any of this.
- A name is at most 255 bytes, in the file's script (Mac OS Roman unless the file says otherwise) [Doc].
- An empty name is not the same as no name: the name offset is `$FFFF` for none, or points at a zero length byte for
  an empty name [Doc].
- GetNamedResource compares names case-insensitively and returns the first match in map order [Verified].

### 1.12 Resource attributes

The byte at reference +$04 [Doc: *More Macintosh Toolbox*, except bit 0]:

| Bit | Mask | Name | Meaning |
| --- | --- | --- | --- |
| 7 | $80 | `resSysRef` | Reserved |
| 6 | $40 | `resSysHeap` | Load into the system heap [Verified] |
| 5 | $20 | `resPurgeable` | The handle is purgeable [Verified] |
| 4 | $10 | `resLocked` | The handle is locked [Verified] |
| 3 | $08 | `resProtected` | The resource cannot be changed or removed |
| 2 | $04 | `resPreload` | Load when the file is opened (only while ResLoad is on) [Verified] |
| 1 | $02 | `resChanged` | Changed in memory; never set on disk (§3.2) |
| 0 | $01 | `resCompressed` | The data is compressed ([compressed-resources.md](compressed-resources.md)) [Code: 68k ROM] [Code: Mac OS 9.0] |

- Bit 0 is `resExtended` in Apple's private equates of the System 7 era and was later published as `resCompressed`
  [Code: 68k ROM].

### 1.13 Size limits

| Limit | Value | Why | Source |
| --- | --- | --- | --- |
| Offset of a data item | ≤ `$FFFFFF` from `dO` | 24-bit data offsets | [Doc] |
| End of the data area and of the map | ≤ `$FFFFFE` (Mac OS 9), < `$FFFFFF` (68k ROM) | Checked at open (§2.2, §2.3) | [Code: Mac OS 9.0] [Code: 68k ROM] [Verified: Mac OS 9.0] |
| Map header, type list and reference lists | Within the first 64 KiB of the map | `mN` and reference-list offsets are 16-bit | [Doc] |
| Name list | A name must start within 64 KiB of `mN` | 16-bit name offsets | [Doc] |
| Types; resources per type | 65536 each | 16-bit counts minus one | [Doc] |
| Resources in practice | About 5,400 in total | 12 bytes each in the 64 KiB before the name list | [Doc] |
| Resource count `$FFFF` (65536) in one type | Hangs Mac OS 9 | Preload loops forever after the fork opens (§2.2) | [Verified] |

- A fork is effectively capped at 16 MiB. Mac OS 9 will not open a fork whose data area or map ends past `$FFFFFE`
  [Code: Mac OS 9.0] [Verified: a fork ending at `$FFFFFE` opens, one ending at `$FFFFFF` gives −199]; the 68k ROM
  rejects `$FFFFFF` and beyond [Code: 68k ROM].
- The Resource Manager does not grow a fork that far. AddResource and ChangedResource return `eofErr` when the fork's
  end + the resource's size + `$118` reaches `$1000000` [Code: Mac OS 9.0] [Verified: to a fork ending at `$FFFE00`,
  adding 512 bytes gives −39 and adding 3 bytes succeeds, and the fork is then laid out as the code predicts].
  SetResourceSize returns −195 for a size of `$FFFFFF` or more, and −39 when moving the data would pass that limit
  [Code: Mac OS 9.0].
- Where an item offset does pass 24 bits, every store keeps `offset & $FFFFFF` and UpdateResFile reports nothing
  [Code: Mac OS 9.0], but normal calls on a fork that opens cannot get there. The one larger fork seen is the
  corruption of §3.2.

## 2. Reading

### 2.1 Reading a fork

1. Read the header (§1.3). OpenResFile then checks the header and the map (§2.2 on Mac OS 9, §2.3 on the 68k ROM);
   the checks decide whether the fork opens at all.
2. Read the map header at `mO` (§1.5).
3. Read the type count at `mO` + `mT`: the number of types is the word + 1, so `$FFFF` is none (§1.8).
4. For each type entry, read its resource count (word + 1) and its reference list at `mO` + `mT` + the list offset.
5. For each reference (§1.9): the item is at `dO` + the data offset, a `u32` length and that many bytes (§1.4); the
   name, unless the offset is `$FFFF`, is the Pascal string at `mO` + `mN` + the name offset (§1.10).

### 2.2 What Mac OS 9 checks at open

Mac OS 9 checks the header (in vNewMap), then the map (in CheckMap), in this order, all arithmetic unsigned 32-bit
[Code: Mac OS 9.0] [Verified: the model of these checks predicts SheepShaver's result on every test fork]:

| # | Check | Failure |
| --- | --- | --- |
| 1 | `EOF` ≥ 70 (the header and a minimal map), and GetEOF succeeds | `eofErr` −39 [Verified: forks of 0, 15, 16 and 69 bytes] |
| 2 | `mO` ≥ 40, `mO` ≤ `EOF` − 30, `mL` ≥ 30, `mL` ≤ `EOF` − 40, `mO` + `mL` ≤ `EOF` | `mapReadErr` −199 |
| 3 | `dO` ≥ 40, `dO` ≤ `EOF`, `dL` ≤ `EOF` − 40, `dO` + `dL` ≤ `EOF` | −199 [Verified: `dO` = 16 is rejected] |
| 4 | The data area does not start inside the map (`mO` < `dO` < `mO` + `mL` fails; starting before the map and running into it passes) | −199 |
| 5 | max(`dO` + `dL`, `mO` + `mL`) ≤ `$FFFFFE` | −199 [Verified: a fork ending at `$FFFFFE` opens, at `$FFFFFF` gives −199] |
| 6 | `mT` < `mL` | −199 |
| 7 | `mN` = `$FFFF`, or `mT` < `mN` ≤ `mL` (so `mN` = 0 fails) | −199 |
| 8 | `mT` is even | −199 |
| 9 | The type count word lies inside the map, and (sign-extended (types − 1)) × 20 + 20 ≤ the type area | −199 |
| 10 | Every type's reference-list offset + 8 ≤ the type area | −199 |
| 11 | The total of the reference counts, summed as a signed 16-bit word, is not negative, and the references it implies end inside the map | −199 |
| 12 | Every reference, walked contiguously from the end of the type list: name offset `$FFFF` or < the name-list size; data offset ≤ `dL` (equal passes) | −199 |
| 13 | `mN` ≥ the end of the references | −199 |

- The type area is `mN` − `mT` (or `mL` − `mT` when `mN` = `$FFFF`); the name-list size is `mL` − `mN` (0 when
  `mN` = `$FFFF`).
- Check 9: a type count of `$FFFF` gives (−1) × 20 + 20 = 0 and passes; `$8000`–`$FFFE` are negative and fail
  [Verified].
- Check 11: each type contributes its count − 1, plus 1, to a 16-bit sum, so a resource count of `$FFFF` wraps to
  zero and passes. Such a fork opens and then hangs the Mac: Preload loops forever [Verified].
- Not checked at open [Code: Mac OS 9.0] [Verified]: name lengths (a name may read past the end of the map); data
  lengths (§2.4); the header copy; where the reference lists actually are.

### 2.3 What the 68k ROM checks at open

The 68k ROM reads the header as signed longs and checks little [Code: 68k ROM]:

| # | Check | Failure |
| --- | --- | --- |
| 1 | The first 36 bytes can be read | `eofErr` −39 |
| 2 | `mL` ≥ 12 (the read count stays positive) | `paramErr` −50 |
| 3 | `mO` + 12 ≥ 0 | `posErr` −40 |
| 4 | `mO` + `mL` ≤ `EOF` (the map can be read) | −39 |
| 5 | `mL` ≥ 28 | Opens, but reads memory past the map |
| 6 | max(`dO` + `dL`, `mO` + `mL`) < `$FFFFFF` | −199 |
| 7 | `mT` is even | −199 |
| 8 | 0 ≤ `mT` ≤ `mL` − 2 | Opens, but reads memory past the map |
| 9 | When types − 1 ≥ 0 (signed): every type entry lies inside the map; reference counts summed as an unsigned 16-bit word | A type entry past the map: opens, but reads memory past it |
| 10 | When `mN` > 0 (signed): `mN` ≥ the end of the references; otherwise the references end ≤ `mL` | −199 |

- The ROM therefore opens many forks Mac OS 9 rejects: data at offset 16, a data offset past `dL`, a name offset past
  the names, `mN` = 0, a type count of `$8000`. They fail later or return garbage [Code: 68k ROM].

### 2.4 Loading from a fork that opened

On Mac OS 9 [Verified]:

- A data length that runs past `EOF`: the fork opens; loading the resource fails with `eofErr` and the handle is
  disposed. GetResourceSizeOnDisk (SizeRsrc) still returns the stored length. A length that runs into the map loads
  the map's bytes.
- A huge data length fails with `memFullErr`. A length of 0 gives an empty handle.
- GetResource and Get1Resource of a missing resource return nil with ResError 0; Get1NamedResource returns nil with
  `resNotFound`; Get1IndResource past the count returns nil with `resNotFound`.

### 2.5 Duplicates

| Case | Mac OS 9 | 68k ROM |
| --- | --- | --- |
| Two references with the same type and ID | Get1Resource and GetNamedResource return the first; the Ind calls list both [Verified] | Not traced |
| A second one loaded through Get*Resource | Takes the first one's cached length [Code: Mac OS 9.0] | — |
| A type listed twice | Count1Types, Get1IndType, Count1Resources and Get1IndResource see only the first list [Verified]; Get1Resource and GetResource search all lists [Verified] | Get1Resource and GetResource search only the first list [Code: 68k ROM] |
| Two names equal apart from case | The first matches [Verified] | Not traced |

## 3. Writing

UpdateResFile and CloseResFile write the fork. The rules below were traced in both Resource Managers and checked in
SheepShaver [Code: Mac OS 9.0] [Code: 68k ROM] [Verified], except where marked; where the two differ is in §4.

### 3.1 A new fork

CreateResFile writes a 286-byte fork [Verified]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 16 | Header | `dO` = `$100`, `mO` = `$100`, `dL` = 0, `mL` = `$1E` |
| +$010 | 240 | System and application areas | Zeros on Mac OS 9 [Verified]; not written by the 68k ROM, so they hold whatever the file's blocks held [Code: 68k ROM] |
| +$100 | 16 | Header copy | |
| +$110 | 6 | `mNext`, `refNum` | Zero on Mac OS 9 |
| +$116 | 1 | `mAttr` | 0 |
| +$117 | 1 | `mInMemoryAttr` | 0 |
| +$118 | 2 | `mT` | `$001C` |
| +$11A | 2 | `mN` | `$001E` |
| +$11C | 2 | Type count − 1 | `$FFFF` |

CreateResFile on a file that already has a resource fork returns `dupFNErr` and leaves it untouched [Verified].

### 3.2 Updating a fork

1. Types stay in the order they were first added; resources within a type in the order added; names in the order set
   (§1.10).
2. New data, and data that grew, is written at the end, after the old map.
3. Data that changed without growing is rewritten in place, leaving the rest of its old item as stale bytes
   [Verified].
4. Compaction happens after a resource is added or removed, after a size change, or when `mapCompact` is set with
   SetResFileAttrs. It keeps the untouched prefix of the data area, then packs every item in ascending order of its
   old offset, with no padding [Verified]. Shared items stay shared (§1.4).
5. The map is always written straight after the data; then the header is written at 0 and the fork is cut to the end
   of the map. The data offset stays `$100`.
6. The map is written from memory: `mAttr` with `mapCompact` and `mapChanged` cleared (`& ~$60`), every attribute byte
   with `resChanged` cleared, and `mNext`, `refNum` and the reference handles as whatever they held (§1.5)
   [Code: Mac OS 9.0] [Code: 68k ROM] [Verified].

Mac OS 9 corrupts a fork whose map comes before its data when it compacts it: the item offsets are rebased but `dO`
is not, and a length read past the end from an uncleared buffer is copied. Adding one 3-byte resource to such a fork
produced a 63 MB fork with a garbage data offset [Code: Mac OS 9.0] [Verified], which Mac OS 9 then refuses to open
(`mapReadErr`, check 5 of §2.2) [Code: Mac OS 9.0] [Verified: reopening it gives −199]. The 68k ROM rebases the
offsets correctly [Code: 68k ROM].

### 3.3 The compacted layout

A fork after a compacting update, the layout a writer should produce:

| Part | Contents |
| --- | --- |
| Header | `dO` = 256, `mO` = 256 + `dL`, `dL`, `mL` |
| Bytes 16–255 | The system and application areas, kept (zeros for a new fork) |
| Data area | One item per distinct data block, packed with no padding, in ascending order of old offset, data added or grown after it in the order it happened (§3.2) |
| Map header | The header again; `mNext` and `refNum` as kept (zeros for a new fork); `mAttr` & `~$60`; `mInMemoryAttr` as kept; `mT` = 28; `mN` = 28 + type list + references |
| Type list | At map +28: count − 1 (`$FFFF` for none), then the types in the order each was first added |
| Reference lists | Straight after the type list, contiguous in type order; within a type, in the order the resources were added; each attribute byte without `resChanged`; the handle field as kept (zero for new resources) |
| Name list | The names in the order they were set, each as length + bytes |

An empty fork in this layout is the 286 bytes of §3.1.

## 4. Variants

ClassicMac follows Mac OS 9 by default (§5.1). Where the two Resource Managers differ:

| Area | Mac OS 9 | 68k ROM | § |
| --- | --- | --- | --- |
| Open checks | Header and full map checks; rejects much | Almost none; opens most damaged forks | 2.2, 2.3 |
| Header fields | Unsigned | Signed | 1.3 |
| Fork end limit | ≤ `$FFFFFE` | < `$FFFFFF` | 1.13 |
| A type listed twice | GetResource searches every list | First list only | 2.5 |
| Resource count `$FFFF` | Opens, then hangs in Preload | Not traced | 2.2 |
| `mInMemoryAttr` from disk | `& $E1` | `& $C1` | 1.7 |
| CreateResFile, bytes 16–255 | Zeros [Verified] | Not written [Code: 68k ROM] | 3.1 |
| Removing the last resource | Sets `dL` to 0 [Verified] | Keeps the old data [Code: 68k ROM] | 3.2 |
| Compacting a fork whose map precedes its data | Corrupts it | Rebases it correctly | 3.2 |
| SetResourceSize moving a resource's data | Copies one number of bytes | Copies another [Code: Mac OS 9.0] [Code: 68k ROM] | — |
| Compressed resources | | | [compressed-resources.md §4](compressed-resources.md#4-variants) |

The decompressors themselves are the same 68k code under both ([compressed-resources.md
§2.4](compressed-resources.md#24-finding-the-decompressor)).

## 5. ClassicMac

### 5.1 The Resource Manager model

- `ReadOptions.ResourceManager` selects the model: `MacOS9` (the default) or `Rom68k`. It changes the open verdict
  (`fork.mac-rejects`, `fork.mac-misreads`), `fork.mac-hangs` (Mac OS 9 only) and decompression
  ([compressed-resources.md §5](compressed-resources.md#5-classicmac)). [ClassicMac]

### 5.2 Reading

- `ResourceFork.Read(Stream)` reads from the stream's position to its end and refuses input over 32 MiB
  (`ResourceFork.MaxForkLength`: 16 MiB of data plus room for a map) with `InvalidDataException`.
  `Read(ReadOnlyMemory<byte>)` has no such cap, so over-large forks such as the 63 MB one of §3.2 can be read from
  memory; they get `fork.mac-rejects`. The resources' data are slices of the input buffer, which must not change
  afterwards. [ClassicMac]
- The reader is tolerant: it reads what it can, reports every problem as a diagnostic (§6) with the fork offset of the
  problem where there is one, and throws `InvalidDataException` only when the header, the map header or the type list
  cannot be found, or the fork is too damaged (§5.3). [ClassicMac]
- An empty input gives an empty fork with no diagnostics, where Mac OS 9 refuses it (§1.2). [ClassicMac]
- The system and application areas are kept as read (`SystemData`, `ApplicationData`) when the fork is at least 256
  bytes long, even when `dO` is not 256 and the bytes are really resource data; shorter forks get zeros.
  [ClassicMac]
- `mNext` and `refNum` are kept as six bytes (`MapReservedData`), `mAttr` with any `mapChanged` or `mapCompact` bits
  (`Attributes`), `mInMemoryAttr` exactly as read, unmasked (`MapFlags`), and each reference's handle field.
  [ClassicMac]
- A resource's `Name` is `null` for no name and empty for an empty name (§1.11). References that share a name are
  read. IDs are not checked against *Inside Macintosh*'s ranges. [ClassicMac]
- Each resource keeps, for the writer, its data offset and name offset as read (its placement, §5.4) and whether its
  data is unchanged since reading. [ClassicMac]

### 5.3 Damaged forks

The reader's steps, and what it reports [ClassicMac]:

1. An empty input gives an empty fork; under 16 bytes throws.
2. The verdict: the checks of §2.2 (or §2.3 with `Rom68k`) run on the whole fork. A refusal adds `fork.mac-rejects`
   ("Mac OS 9 would not open this fork (mapReadErr −199): …. Read anyway."); a ROM case that opens but reads past the
   map adds `fork.mac-misreads`. Reading continues either way, and the verdict is appended to any exception thrown
   later.
3. Map header. When `mO` + 28 > `EOF`, the reader first tries the recovery ResEdit applies: if a map whose type-list
   offset is 28 or more, with its type count inside the fork, sits at `dO` + `dL`, that is taken as the map (to the end
   of the fork) and `fork.map-recovered` is reported; otherwise it throws. If `mL` < 28 or `mO` + `mL` > `EOF`,
   `fork.map-length` is reported and the map is taken to run to the end of the fork.
4. Data area. If `dO` + `dL` > `EOF`, `fork.data-length` is reported and the data area is taken to end at `EOF`.
5. Header copy. One that is neither all zeros nor equal to the header gives `fork.header-mismatch` (Info; the Mac
   ignores it).
6. Type list. A count word beyond the map throws. For each type:
   - an entry past the map: `fork.type-list-truncated`, and the remaining types are dropped;
   - a type already seen: `fork.duplicate-type`, and its resources are merged with the first list's;
   - a count − 1 of `$7FFF` or more, with the Mac OS 9 model: `fork.mac-hangs`;
   - a reference list that does not start where the previous one ended (starting from the end of the type list):
     `fork.ref-lists-out-of-order`, once per fork.
7. References, in list order, each:
   - past the map: `fork.ref-list-out-of-range`, and the rest of that type's list is dropped;
   - a type and ID already read: `resource.duplicate`, and the first is kept, as GetResource returns it;
   - an item whose length word is not wholly inside the data area: `resource.data-out-of-range`, resource skipped;
   - a length over `ReadOptions.MaxResourceSize`: `resource.too-large`, resource skipped;
   - a length past the end of the data area: `resource.data-truncated`, and the bytes that are there are kept;
   - a name whose length byte or bytes lie outside the map: `resource.name-out-of-range`, and the resource is kept
     unnamed.
8. Overlaps. Items that overlap or are shared give `resource.data-overlap` (Info) for each overlap.
9. Too damaged. Bytes of another format read as a fork can promise 65536 types of 65536 references each. Past 1000
   Error diagnostics raised in step 7 (`fork.type-list-truncated` is not counted), the reader stops and throws
   `InvalidDataException` ("The fork is too damaged to read: over 1000 errors in its map"), instead of reporting
   millions.

`fork.unreadable` is reported by `ClassicMac.Files` (`MacFileResources`) and the app when `ResourceFork.Read` throws;
the file is then shown without resources. [ClassicMac]

### 5.4 Writing

`ResourceFork.Write` and `ToArray` write the compacted layout of §3.3, with the types in the order each first appears
among the fork's resources and, within a type, the resources in the order they were read or added. A new, empty
`ResourceFork` writes exactly the 286 bytes of Mac OS 9's CreateResFile (§3.1). [ClassicMac]

Every resource carries a data placement and a name placement: the offset it was read from, or, for data or names
added later, a key beyond every real offset, increasing in the order the changes happen. The data area and the name
list are written in placement order, which reproduces the Resource Manager's "new things go at the end, then
compaction packs by old offset" (§3.2) [ClassicMac]:

- Read resources keep their order in the data area and the name list.
- `SetData` with longer data moves the item after all old data; shorter or equal data keeps its place.
- A new name (`Name` set to a different value) moves the name to the end of the name list, as SetResInfo does; setting
  the same name again changes nothing.
- A new resource gets new data and name placements, after everything else; `Add` appends it to its type's list, and a
  new type goes after the existing ones. `Insert` puts a removed resource back at a position.
- `Renumber` changes the ID in place and keeps the resource's position.
- Sharing: resources whose data are unchanged since reading and were read from the same offset are written as one
  shared item again. Any other data, including data that only partly overlapped another item, is written as its own
  item. Names are never shared on output; each is written separately.

The in-memory `resChanged` bits and `mapChanged`/`mapCompact` are dropped on output only; the model keeps them. The
writer does not compress: a compressed resource's stored bytes are written as they are. [ClassicMac]

### 5.5 Limits

- `ReadOptions.MaxResourceSize` (default 64 MiB) caps one resource, stored (`resource.too-large` when reading) and
  decompressed ([compressed-resources.md §5](compressed-resources.md#5-classicmac)). [ClassicMac]
- The writer throws `InvalidOperationException` for more than 65536 types or 65536 resources in one type, a name that
  would start at `$FFFF` or later, type and reference lists that pass 64 KiB, or a fork that would end past `$FFFFFE`,
  the Resource Manager's own limit (§1.13): it writes nothing Mac OS 9 would refuse to open, and never truncates an
  offset as the Resource Manager's stores would. [ClassicMac]

## 6. Diagnostics

Decompression codes are in [compressed-resources.md §6](compressed-resources.md#6-diagnostics).

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `fork.data-length` | Warning | The data area runs past the fork | Cuts it at the end of the fork | Mac OS 9: `mapReadErr`; 68k ROM: opens, loads fail at `EOF` |
| `fork.duplicate-type` | Warning | A type appears twice in the type list | Merges its lists | Counting and indexing see the first list; GetResource searches all (Mac OS 9) or the first (68k ROM) (§2.5) |
| `fork.header-mismatch` | Info | The map's header copy is neither zero nor equal to the header | Reads on | Ignores it |
| `fork.mac-hangs` | Warning | (Mac OS 9 model) a type's count − 1 is `$7FFF` or more | Reads on | At `$FFFF` the fork opens and Preload loops forever; other such counts fail check 11 (§2.2) |
| `fork.mac-misreads` | Warning | (68k ROM model) the ROM would open the fork but read memory past its map | Reads on | Opens; later calls see garbage (§2.3) |
| `fork.mac-rejects` | Warning | The modelled Resource Manager would refuse to open the fork; the message names the error and the failed check | Reads the fork anyway | OpenResFile fails with `eofErr`, `posErr`, `paramErr` or `mapReadErr` (§2.2, §2.3) |
| `fork.map-length` | Warning | `mL` is under 28 or runs past the fork | Takes the map to run to the end of the fork | Mac OS 9: `mapReadErr`; 68k ROM: `eofErr`, or a misread map |
| `fork.map-recovered` | Warning | The map offset lies outside the fork and a map was found right after the data area | Uses that map | Mac OS 9: `mapReadErr`; 68k ROM: `eofErr` |
| `fork.ref-list-out-of-range` | Error | A reference list runs past the map | Drops the rest of it | Mac OS 9: `mapReadErr`; 68k ROM: `mapReadErr` or reads past the map |
| `fork.ref-lists-out-of-order` | Warning | The reference lists are not contiguous in type order (reported once) | Reads every list where it is | Opens; wrong IDs, `resNotFound` and mixed-up names for later types (§1.8) |
| `fork.type-list-truncated` | Error | The type count promises more types than the map holds | Drops the rest | Mac OS 9: `mapReadErr`; 68k ROM: reads past the map |
| `fork.unreadable` | Error | `ResourceFork.Read` threw (no header or map, over 32 MiB, or too damaged) | Shows the file without resources | — |
| `resource.data-out-of-range` | Error | The item's length word lies outside the data area | Skips the resource | Mac OS 9: `mapReadErr` when the offset is past `dL`; otherwise loading fails |
| `resource.data-overlap` | Info | Two items overlap or are shared | Reads both; writes them separately unless exactly shared | Both load their bytes (§1.4) |
| `resource.data-truncated` | Error | The item runs past the data area | Keeps the bytes that are there | Opens; loading fails with `eofErr` (§2.4) |
| `resource.duplicate` | Warning | A second resource with the same type and ID | Keeps the first | Get1Resource returns the first; the Ind calls list both (§2.5) |
| `resource.name-out-of-range` | Warning | The name lies outside the map | Keeps the resource unnamed | Mac OS 9: `mapReadErr` when the offset is past the name list; else a name read from beyond the map |
| `resource.too-large` | Error | A stored length over `ReadOptions.MaxResourceSize` | Skips the resource | Loads it if memory allows, else `memFullErr` |

## 7. Verification

- The open checks of §2.2 were modelled from the code and checked against SheepShaver on 78 crafted forks; the model
  predicts Mac OS 9.0's result on all of them [Verified]. The 68k ROM's checks (§2.3) were traced in code only; the
  ROM was never run.
- Forks written by Mac OS 9.0's Resource Manager in SheepShaver established §3.1–§3.2 and the growth limits of
  §1.13. The compacted layout of §3.3 reproduces 29 of 33 such forks byte for byte; the rest hold stale bytes from
  in-place rewrites, duplicate IDs, or the fork Mac OS 9 corrupts (§3.2).
- `tests/ClassicMac.Resources.Tests/ResourceForkReadWriteTests.cs`: a small fork assembled by hand from *Inside
  Macintosh*'s description (two types, a named, an unnamed and an empty-named resource, data in the reserved areas,
  memory in `mNext`/`refNum`, the password bit) read and written byte for byte; an empty fork and CreateResFile's 286
  bytes; reading from a stream; the `$FFFFFE` writer limit; inputs that throw; one test per reading diagnostic of
  §5.3, including the map recovery and a zeroed header copy that is not reported; the write layout of §5.4 (grown
  data moves to the end, a new name moves to the end, shared data stays shared, `mapCompact`/`mapChanged` dropped,
  handle fields kept); the open verdicts (a data offset of 16 and a type count of `$8000` rejected by Mac OS 9 only, a
  resource count of `$FFFF` that opens and hangs, reference lists out of type order, well-formed forks accepted by
  both models).
- `tests/ClassicMac.Resources.Tests/ResourceForkTests.cs`: another format's bytes read as a fork (an AppleDouble file
  named `.rsrc` in the corpus) refused after 1000 errors; the model's order, lookup, duplicates, `Remove`, `Renumber`,
  `SetData` and the reserved areas' sizes.
- `ResourceForkReadWriteTests.Corpus_forks_round_trip` reads every fork under `CLASSICMAC_CORPUS` (not in the
  repository; skipped without it), writes it, rereads it and requires the same model and no new diagnostics. 142 of
  152 real forks come back byte for byte; the rest hold stale bytes, duplicate IDs or the corrupt fork of §3.2.

## 8. Not covered

- The Ind-call size cache for duplicate IDs (§2.5) was read in code only.
- The 68k ROM's behaviour was traced in code only; it was never run (SheepShaver runs the native Resource Manager).
- The trap patches that extensions install on the Resource Manager (Multiple Users, Apple Menu Options, language
  packs, the Process Manager): they change what running applications see, not what a file contains.
- The use of `mInMemoryAttr` bits 1–4 (§1.7) was not traced.
- No rule in this document is fitted to data alone.

## 9. References

1. Apple, *Inside Macintosh: More Macintosh Toolbox* (1993), chapter 1 "Resource Manager", "Resource File Format":
   the header, the data area, the map, the attributes and the ID ranges.
2. The Mac OS 9.0 System's native Resource Manager (the `'Resources'` code fragment: vNewMap, CheckMap, CheckGrow,
   UpdateResFile), traced in disassembly.
3. The 68k Resource Manager in Mac OS ROM `$077D` (vNewMap, CheckMap, UpdateResFile), traced in disassembly.
4. Apple, ResEdit: its recovery of a fork whose map offset lies outside the fork, which ClassicMac follows (§5.3).
5. [compressed-resources.md](compressed-resources.md): compressed resources and the `'dcmp'` decompressors.
