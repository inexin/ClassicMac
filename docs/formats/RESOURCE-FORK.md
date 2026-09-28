# Resource forks — an implementer's specification

This document describes the classic Mac OS resource fork completely enough to write a reader that opens, rejects and
misreads forks exactly as a Macintosh does, a writer whose output the Resource Manager accepts and would itself have
produced, and the four System decompressors (`'dcmp'` 0–3) for compressed resources. It is the behaviour implemented by
`ClassicMac.Resources`, written so that the code never has to be read.

There are two reference implementations. They agree on every well-formed fork and differ only on damaged forks and
malformed compressed resources:

- **Mac OS 9** replaced the Resource Manager with native PowerPC code (the `'Resources'` code fragment in the Mac OS 9.0
  System file). This is what SheepShaver and any Mac running Mac OS 9 show, and it is ClassicMac's **default**
  (`ReadOptions.ResourceManager = MacOS9`). On Mac OS 9 the decompressors themselves still run as 68k code.
- **The 68k ROM** holds the 68k Resource Manager (Mac OS ROM `$077D`, the last revision of the code that Macs ran before
  Mac OS 9). ClassicMac follows it when `ReadOptions.ResourceManager = Rom68k`.

Sections 2–7 describe the format, section 8 what each Resource Manager accepts when it opens a fork, sections 9–11
reading and writing, sections 12–16 compressed resources. Section 17 lists every point where the two Resource Managers
differ, and section 18 every diagnostic ClassicMac reports.

Contents

1. [Conventions](#1-conventions)
2. [Fork layout](#2-fork-layout)
3. [The fork header and reserved areas](#3-the-fork-header-and-reserved-areas)
4. [The data area](#4-the-data-area)
5. [The resource map](#5-the-resource-map)
6. [Resources: types, IDs, names and attributes](#6-resources-types-ids-names-and-attributes)
7. [Size limits](#7-size-limits)
8. [Opening a fork: what the Resource Manager checks](#8-opening-a-fork-what-the-resource-manager-checks)
9. [How ClassicMac reads a fork](#9-how-classicmac-reads-a-fork)
10. [How the Resource Manager writes a fork](#10-how-the-resource-manager-writes-a-fork)
11. [What ClassicMac writes](#11-what-classicmac-writes)
12. [Compressed resources](#12-compressed-resources)
13. [`'dcmp'` 0 and 1](#13-dcmp-0-and-1)
14. [`'dcmp'` 2](#14-dcmp-2)
15. [`'dcmp'` 3](#15-dcmp-3)
16. [Application-supplied decompressors](#16-application-supplied-decompressors)
17. [Mac OS 9 and 68k ROM differences](#17-mac-os-9-and-68k-rom-differences)
18. [Diagnostics](#18-diagnostics)
19. [Not covered](#19-not-covered)

---

## 1. Conventions

The conventions and source tags of [README.md](README.md) hold. In addition:

- **Header fields** are abbreviated: `dO` data offset, `mO` map offset, `dL` data length, `mL` map length (all `u32`
  from the fork header); `mT` and `mN` are the map's type-list and name-list offsets. `EOF` is the fork's length.
- **Offsets** are from the start of the fork unless a structure says otherwise. Map fields are offsets from the start of
  the map, reference-list offsets from the start of the type list, name offsets from the start of the name list, data
  offsets from the start of the data area.
- **[Code] names the implementation:** *Mac OS 9.0* is the native Resource Manager of the Mac OS 9.0 System; *68k ROM*
  is the 68k Resource Manager in Mac OS ROM `$077D`; *`'dcmp'` n* is the decompressor of that ID in the Mac OS 9.0
  System (68k code; every rule tagged so was also checked by running Apple's code in a 68k emulator and comparing its
  output byte for byte). **[Verified]** means SheepShaver, Mac OS 9.0, which runs the native Resource Manager, so no ROM
  rule is ever [Verified].
- **ClassicMac's own choices** (its diagnostics, limits and fallbacks where the Mac would crash or read stray memory)
  are marked **ClassicMac** and carry no tag: they are design, not format.
- **Error codes:** `dupFNErr` −48, `eofErr` −39, `posErr` −40, `paramErr` −50, `memFullErr` −108, `badExtResource`
  −185, `CantDecompress` −186, `resNotFound` −192, `mapReadErr` −199.

---

## 2. Fork layout

A resource fork has four parts: a 16-byte header, 240 reserved bytes, the data area and the resource map
[Doc: *Inside Macintosh: More Macintosh Toolbox*, "Resource File Format"]. The header locates the other two; nothing
else in the format is at a fixed place. The layout every Resource Manager writes, and ClassicMac's writer too, is:

```
fork offset
$000   +-------------------------------+
       | header: dO, mO, dL, mL        |  16 bytes
$010   | system area                   |  112 bytes, reserved
$080   | application area              |  128 bytes
$100   +-------------------------------+  <- dO (256)
       | item: u32 length, data        |
       | item: u32 length, data        |  dL bytes, no padding
       | ...                           |
dO+dL  +-------------------------------+  <- mO
       | map header (28)               |
       |   header copy, mNext, refNum, |
       |   mAttr, mInMemoryAttr, mT, mN|
mO+mT  | type list                     |  u16 count-1, 8 bytes per type
       | reference lists               |  12 bytes per resource, grouped by type
mO+mN  | name list                     |  Pascal strings
mO+mL  +-------------------------------+  = EOF
```

- The order data-then-map and the data offset 256 are conventions, not requirements: both Resource Managers accept a
  map before the data (but see §10.4), and a data area that starts before the map and runs into it
  [Code: Mac OS 9.0; Verified].
- A file with no resource fork has a fork of length 0. Mac OS 9 refuses to open it with `eofErr` [Verified];
  **ClassicMac** reads an empty input as an empty fork with no diagnostics.

---

## 3. The fork header and reserved areas

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | u32 | `dO`: offset of the data area |
| +$04 | 4 | u32 | `mO`: offset of the resource map |
| +$08 | 4 | u32 | `dL`: length of the data area |
| +$0C | 4 | u32 | `mL`: length of the resource map |
| +$10 | 112 | — | **system area**, reserved for the system |
| +$80 | 128 | — | **application area**, available to the application |

[Doc: *More Macintosh Toolbox*]

- The 68k ROM reads the four header fields as **signed** longs; Mac OS 9 as unsigned [Code: 68k ROM; Mac OS 9.0]. Only
  damaged forks tell the difference (§8).
- **The reserved areas.** Mac OS 9's CreateResFile writes zeros there [Verified]; the 68k ROM's never writes them, so a
  new fork holds whatever the file's blocks held [Code: 68k ROM]. Neither Resource Manager reads them. Real files carry
  data in both, so a writer that round-trips should keep them.
- **ClassicMac** keeps both areas as read (`SystemData`, `ApplicationData`) when the fork is at least 256 bytes long,
  even when `dO` is not 256 and the bytes are really resource data; shorter forks get zeros.

---

## 4. The data area

The data area is a sequence of **items**, one per resource [Doc: *More Macintosh Toolbox*]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | u32 | length of the resource data |
| +$04 | length | — | the data |

- A reference's data offset (§5.4) points at the item's length word, relative to `dO` [Doc].
- Items are packed with **no padding or alignment** [Verified].
- The area may hold gaps and stale bytes: a resource rewritten without growing is written in place and leaves the rest
  of its old item behind, until the fork is compacted [Verified].
- Two references may point at the same item; both load the same bytes [Verified], and preloading and compaction visit
  only the first reference at a given offset [Verified].
- **GetMaxResourceSize** of a resource not yet loaded is the distance from its item to the next item's offset,
  **including the 4-byte length word** (for the last item, length + 4) [Verified].

---

## 5. The resource map

### 5.1 Map header

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 16 | — | copy of the fork header, or zeros [Doc: *More Macintosh Toolbox*] |
| +$10 | 4 | u32 | `mNext`: handle to the next map in the chain, reserved on disk [Doc] |
| +$14 | 2 | i16 | `refNum`: the file's reference number, reserved on disk [Doc] |
| +$16 | 1 | u8 | `mAttr`: map attributes (§5.2) |
| +$17 | 1 | u8 | `mInMemoryAttr`: in-memory map flags (§5.3) |
| +$18 | 2 | u16 | `mT`: offset of the type list from the start of the map [Doc] |
| +$1A | 2 | u16 | `mN`: offset of the name list from the start of the map [Doc] |

- *Inside Macintosh* shows +$16 as one 2-byte attributes field. Both Resource Managers treat it as **two separate
  bytes**, `mAttr` and `mInMemoryAttr` [Code: 68k ROM; Mac OS 9.0]. GetResFileAttrs returns `mAttr`, in the low byte of
  its result [Verified]. A writer must keep them apart.
- **The header copy is never checked**: neither Resource Manager compares it with the header [Verified; Code: 68k ROM].
- **`mNext`, `refNum` and the reference handles (§5.4) hold memory in real files.** The Resource Manager writes the map
  straight from its in-memory copy, so these fields carry whatever handle and reference number the map had when it was
  written [Code: Mac OS 9.0, 68k ROM; Verified]. They are meaningless on disk. A comparer should mask them.

### 5.2 Map attributes (`mAttr`, +$16)

| Bit | Mask | Name | Meaning | Source |
| --- | --- | --- | --- | --- |
| 7 | $80 | `mapReadOnly` | The file is read-only: no changes are written | [Doc] |
| 6 | $40 | `mapCompact` | Compact the file when it is next written | [Doc] |
| 5 | $20 | `mapChanged` | The map has changed and must be written | [Doc] |
| 4–1 | — | — | unused | |
| 0 | $01 | `mapForceSysHeap` | Load the map into the system heap (Apple's private name) | [Code: 68k ROM; Mac OS 9.0] |

- The Resource Manager keeps `mAttr` as stored on disk, `mapChanged` included: a file opened for writing with
  `mapChanged` already set on disk is rewritten when it is closed [Code: Mac OS 9.0].
- With `mapReadOnly` set on disk, UpdateResFile returns `mapReadErr` and writes nothing [Verified].
- The byte is written with `mapCompact` and `mapChanged` cleared (`& ~$60`) [Code: Mac OS 9.0, 68k ROM; Verified].

### 5.3 In-memory map flags (`mInMemoryAttr`, +$17)

Flags the Resource Manager keeps with an open map. The byte is written out with the map, so some files carry them on
disk.

| Bit | Mask | Name (Apple's equates) | Meaning |
| --- | --- | --- | --- |
| 7–5 | — | — | unnamed |
| 4 | $10 | `preventFileFromBeingClosedBit` | not traced |
| 3 | $08 | `twoDeepBit` | not traced |
| 2 | $04 | `dontCountOrIndexDuplicatesBit` | not traced |
| 1 | $02 | `overrideNextMapBit` | not traced |
| 0 | $01 | `decompressionPasswordBit` | The 68k ROM searches a map for `'dcmp'` resources only when this bit is set (§12.5) |

- When a map is read from disk, the byte is masked: the 68k ROM keeps bits 0, 6 and 7 (`& $C1`), Mac OS 9 bits 0 and
  5–7 (`& $E1`) [Code: 68k ROM; Mac OS 9.0]. Only bit 0 was traced to a use [Code: 68k ROM]; Mac OS 9 ignores it for
  decompression [Code: Mac OS 9.0; Verified].
- **ClassicMac** keeps the byte exactly as read (`ResourceFork.MapFlags`) and writes it back unchanged.

### 5.4 Type list

At map offset `mT` (28 in every fork the Resource Manager writes [Code: Mac OS 9.0, 68k ROM]):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | number of types − 1; `$FFFF` means no types [Doc: *More Macintosh Toolbox*] |
| +$02 | 8 × n | — | one entry per type |

Each type entry [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | OSType | resource type |
| +$04 | 2 | u16 | number of resources of this type − 1 |
| +$06 | 2 | u16 | offset of this type's reference list, **from the start of the type list** (its count word) |

- Mac OS 9 reads the type count as **signed**: `$FFFF` (no types) passes, any other value from `$8000` up is rejected
  (§8.1) [Code: Mac OS 9.0; Verified].
- **Reference lists must follow the type list contiguously, in type order.** The Resource Manager assumes they do:
  Mac OS 9's open checks walk the references as one run starting right after the type list (§8.1) [Code: Mac OS 9.0].
  A fork whose lists are elsewhere or in another order opens, but GetResInfo on a later type returns ID −1 with
  `resNotFound`, and names are attributed to the wrong resources [Verified].

### 5.5 Reference lists

One 12-byte entry per resource, grouped by type [Doc: *More Macintosh Toolbox*]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | i16 | resource ID |
| +$02 | 2 | u16 | offset of the name from the start of the name list; `$FFFF` = no name |
| +$04 | 1 | u8 | resource attributes (§6.4) |
| +$05 | 3 | u24 | offset of the data item from the start of the data area |
| +$08 | 4 | u32 | handle: reserved on disk; in memory the resource's handle (§5.1) |

- Within a type, references are in the order the resources were added, **not sorted by ID** [Code: Mac OS 9.0, 68k ROM;
  Verified].

### 5.6 Name list

At map offset `mN`: Pascal strings (a length byte and that many bytes), packed with no padding [Doc: *More Macintosh
Toolbox*]. A reference's name offset points at the length byte.

- An empty map puts `mN` at the end of the map (`$1E` in a new fork) [Verified].
- Mac OS 9 also accepts `mN = $FFFF`, meaning no name list [Code: Mac OS 9.0]; `mN = 0` is rejected (§8.1).
- Names are appended in the order they are set; SetResInfo with a name always moves it to the end; removing a name
  repacks the list [Code: Mac OS 9.0, 68k ROM; Verified].
- **ClassicMac** reads references that share a name, and writes each name separately.

---

## 6. Resources: types, IDs, names and attributes

### 6.1 Types

An `OSType`, compared as four bytes, case-sensitively (`'PICT'` and `'pict'` are different types) [Doc: *More
Macintosh Toolbox*].

### 6.2 IDs

A signed 16-bit ID, unique within its type. *Inside Macintosh* reserves −32768 to −16385, gives −16384 to −1 to
resources owned by other system resources (drivers, desk accessories), 0 to 127 to system resources, and leaves 128 to
32767 to applications [Doc: *More Macintosh Toolbox*]. The format does not enforce any of this, and neither does
ClassicMac.

### 6.3 Names

- A name is at most 255 bytes, in the file's script (Mac OS Roman unless the file says otherwise) [Doc].
- **An empty name is not the same as no name**: the name offset is `$FFFF` for none, or points at a zero length byte
  for an empty name [Doc]. ClassicMac keeps the distinction (`Name` is `null` or empty).
- GetNamedResource compares names case-insensitively and returns the first match in map order [Verified].

### 6.4 Resource attributes

The byte at reference +$04 [Doc: *More Macintosh Toolbox*, except bit 0]:

| Bit | Mask | Name | Meaning |
| --- | --- | --- | --- |
| 7 | $80 | `resSysRef` | reserved |
| 6 | $40 | `resSysHeap` | load into the system heap [Verified] |
| 5 | $20 | `resPurgeable` | the handle is purgeable [Verified] |
| 4 | $10 | `resLocked` | the handle is locked [Verified] |
| 3 | $08 | `resProtected` | the resource cannot be changed or removed |
| 2 | $04 | `resPreload` | load when the file is opened (only while ResLoad is on) [Verified] |
| 1 | $02 | `resChanged` | changed in memory; **never set on disk** |
| 0 | $01 | `resCompressed` | the data is compressed (§12) [Code: 68k ROM; Mac OS 9.0] |

- Bit 0 is `resExtended` in Apple's private equates of the System 7 era and was later published as `resCompressed`
  [Code: 68k ROM].
- The Resource Manager writes every attribute byte with `resChanged` cleared [Code: Mac OS 9.0, 68k ROM; Verified].

---

## 7. Size limits

| Limit | Value | Why | Source |
| --- | --- | --- | --- |
| Offset of a data item | ≤ `$FFFFFF` from `dO` | 24-bit data offsets | [Doc] |
| End of the data area and of the map | ≤ `$FFFFFE` (Mac OS 9), < `$FFFFFF` (68k ROM) | checked at open (§8) | [Code: Mac OS 9.0; 68k ROM] |
| Map header, type list and reference lists | within the first 64 KiB of the map | `mN` is a 16-bit offset; reference-list offsets are 16-bit | [Doc] |
| Name list | a name must start within 64 KiB of `mN` | 16-bit name offsets | [Doc] |
| Types; resources per type | 65536 each | 16-bit counts minus one | [Doc] |
| Resources in practice | about 5,400 in total | 12 bytes each in the 64 KiB before the name list | [Doc] |
| Resource count `$FFFF` (65536) in one type | hangs Mac OS 9 | Preload loops forever after the fork opens | [Verified] |

- **A fork is effectively capped at 16 MiB.** Mac OS 9 will not open a fork whose data area or map ends past
  `$FFFFFE` [Code: Mac OS 9.0]; the 68k ROM rejects `$FFFFFF` and beyond [Code: 68k ROM].
- **The Resource Manager does not grow a fork that far.** AddResource and ChangedResource return `eofErr` (−39)
  when the fork's end + the resource's size + `$118` reaches `$1000000`, and SetResourceSize returns −195 for a size of
  `$FFFFFF` or more, and −39 when moving the data would pass that limit [Code: Mac OS 9.0]. Where an item offset
  does pass 24 bits, every store keeps `offset & $FFFFFF` and UpdateResFile reports nothing [Code: Mac OS 9.0], but
  normal calls on a fork that opens cannot get there.
- **The one larger fork seen is corruption.** Mac OS 9 corrupts a fork whose map comes before its data when it
  compacts it: its item offsets are rebased but `dO` is not, and a length read past the end from an uncleared buffer
  is copied. Adding one 3-byte resource to such a fork produced a 63 MB fork with a garbage data offset
  [Code: Mac OS 9.0; Verified], which Mac OS 9 then refuses to open (`mapReadErr`, check 5 of §8.1)
  [Code: Mac OS 9.0]. The 68k ROM rebases the offsets correctly [Code: 68k ROM].

**ClassicMac's limits:**

- `ResourceFork.Read(Stream)` refuses input over **32 MiB** (16 MiB of data plus room for a map) with
  `InvalidDataException`. `Read(ReadOnlyMemory<byte>)` has no such cap, so over-large forks like the one above can be
  read from memory; they get `fork.mac-rejects`.
- `ReadOptions.MaxResourceSize` (default **64 MiB**) caps one resource, stored (`resource.too-large` when reading) and
  decompressed (`resource.too-large` when decompressing).
- **Too damaged.** Bytes of another format read as a fork can promise 65536 types of 65536 references each. Past
  **1000 Error diagnostics** from the reference lists, the reader stops and throws `InvalidDataException` ("The fork is
  too damaged to read: over 1000 errors in its map"), instead of reporting millions.
- The writer throws `InvalidOperationException` for more than 65536 types or 65536 resources in one type, a name
  that would start at `$FFFF` or later, type and reference lists that pass 64 KiB, or a fork that would end past
  `$FFFFFE`, the Resource Manager's own limit: it writes nothing Mac OS 9 would refuse to open, and never truncates an
  offset as the Resource Manager's stores would.

---

## 8. Opening a fork: what the Resource Manager checks

OpenResFile reads the header, reads the map, and checks it; the checks decide whether the fork opens at all. The two
Resource Managers check very different things.

### 8.1 Mac OS 9

Mac OS 9 checks the header (in vNewMap), then the map (in CheckMap), in this order, all arithmetic unsigned 32-bit
[Code: Mac OS 9.0; Verified: the model of these checks predicts SheepShaver's result on every test fork]:

| # | Check | Failure |
| --- | --- | --- |
| 1 | `EOF` ≥ 70 (the header and a minimal map), and GetEOF succeeds | `eofErr` −39 [Verified: forks of 0, 15, 16 and 69 bytes] |
| 2 | `mO` ≥ 40, `mO` ≤ `EOF` − 30, `mL` ≥ 30, `mL` ≤ `EOF` − 40, `mO` + `mL` ≤ `EOF` | `mapReadErr` −199 |
| 3 | `dO` ≥ 40, `dO` ≤ `EOF`, `dL` ≤ `EOF` − 40, `dO` + `dL` ≤ `EOF` | −199 [Verified: `dO` = 16 is rejected] |
| 4 | the data area does not **start** inside the map (`mO` < `dO` < `mO` + `mL` fails; starting before the map and running into it passes) | −199 |
| 5 | max(`dO` + `dL`, `mO` + `mL`) ≤ `$FFFFFE` | −199 |
| 6 | `mT` < `mL` | −199 |
| 7 | `mN` = `$FFFF`, or `mT` < `mN` ≤ `mL` (so `mN` = 0 fails) | −199 |
| 8 | `mT` is even | −199 |
| 9 | the type count word lies inside the map, and (sign-extended (types − 1)) × 20 + 20 ≤ the type area | −199 |
| 10 | every type's reference-list offset + 8 ≤ the type area | −199 |
| 11 | the total of the reference counts, summed as a **signed 16-bit** word, is not negative, and the references it implies end inside the map | −199 |
| 12 | every reference, walked **contiguously from the end of the type list**: name offset `$FFFF` or < the name-list size; data offset ≤ `dL` (equal passes) | −199 |
| 13 | `mN` ≥ the end of the references | −199 |

- The **type area** is `mN` − `mT` (or `mL` − `mT` when `mN` = `$FFFF`); the **name-list size** is `mL` − `mN` (0 when
  `mN` = `$FFFF`).
- Check 9: a type count of `$FFFF` gives (−1) × 20 + 20 = 0 and passes; `$8000`–`$FFFE` are negative and fail
  [Verified].
- Check 11: each type contributes its count − 1, plus 1, to a 16-bit sum, so a resource count of `$FFFF` wraps to zero
  and passes. Such a fork opens and then **hangs** the Mac: Preload loops forever [Verified].
- **Not checked at open** [Code: Mac OS 9.0; Verified]: name lengths (a name may read past the end of the map); data
  lengths (see §8.3); the header copy; where the reference lists actually are.

### 8.2 The 68k ROM

The 68k ROM reads the header as signed longs and checks little [Code: 68k ROM]:

| # | Check | Failure |
| --- | --- | --- |
| 1 | the first 36 bytes can be read | `eofErr` −39 |
| 2 | `mL` ≥ 12 (the read count stays positive) | `paramErr` −50 |
| 3 | `mO` + 12 ≥ 0 | `posErr` −40 |
| 4 | `mO` + `mL` ≤ `EOF` (the map can be read) | −39 |
| 5 | `mL` ≥ 28 | opens, but reads memory past the map |
| 6 | max(`dO` + `dL`, `mO` + `mL`) < `$FFFFFF` | −199 |
| 7 | `mT` is even | −199 |
| 8 | 0 ≤ `mT` ≤ `mL` − 2 | opens, but reads memory past the map |
| 9 | when types − 1 ≥ 0 (signed): every type entry lies inside the map; reference counts summed as an unsigned 16-bit word | a type entry past the map: opens, but reads memory past it |
| 10 | when `mN` > 0 (signed): `mN` ≥ the end of the references; otherwise the references end ≤ `mL` | −199 |

- The ROM therefore **opens many forks Mac OS 9 rejects**: data at offset 16, a data offset past `dL`, a name offset
  past the names, `mN` = 0, a type count of `$8000`. They fail later or return garbage [Code: 68k ROM].

### 8.3 Loading from a fork that opened

On Mac OS 9 [Verified]:

- A data length that runs past `EOF`: the fork opens; loading the resource fails with `eofErr` and the handle is
  disposed. GetResourceSizeOnDisk (SizeRsrc) still returns the stored length. A length that runs into the map loads the
  map's bytes.
- A huge data length fails with `memFullErr`. A length of 0 gives an empty handle.
- GetResource and Get1Resource of a missing resource return nil with ResError **0**; Get1NamedResource returns nil
  with `resNotFound`; Get1IndResource past the count returns nil with `resNotFound`.

### 8.4 Duplicates

| Case | Mac OS 9 | 68k ROM |
| --- | --- | --- |
| Two references with the same type and ID | Get1Resource and GetNamedResource return the **first**; the Ind calls list both [Verified] | not traced |
| A second one loaded through Get*Resource | takes the first one's cached length [Code: Mac OS 9.0] | — |
| A type listed twice | Count1Types, Get1IndType, Count1Resources, Get1IndResource see only the **first** list [Verified]; Get1Resource and GetResource search **all** lists [Verified] | Get1Resource and GetResource search only the first list [Code] |
| Two names equal apart from case | the first matches [Verified] | not traced |

---

## 9. How ClassicMac reads a fork

The reader is tolerant: it reads what it can, reports every problem as a diagnostic (§18), and throws
`InvalidDataException` only when the header, the map header or the type list cannot be found.

1. **Empty input** gives an empty fork with no diagnostics.
2. **Under 16 bytes** throws.
3. **The verdict.** The checks of §8.1 (or §8.2 with `Rom68k`) run on the whole fork. A refusal adds
   `fork.mac-rejects` ("Mac OS 9 would not open this fork (mapReadErr −199): …. Read anyway."); a ROM case that opens
   but reads past the map adds `fork.mac-misreads`. Reading continues either way, and the verdict is appended to any
   exception thrown later.
4. **Map header.** `mO` + 28 > `EOF` throws. If `mL` < 28 or `mO` + `mL` > `EOF`, `fork.map-length` is reported and the
   map is taken to run to the end of the fork.
5. **Data area.** If `dO` + `dL` > `EOF`, `fork.data-length` is reported and the data area is taken to end at `EOF`.
6. **Reserved areas and header copy.** Bytes 16–255 are kept (when `EOF` ≥ 256). A header copy that is neither all
   zeros nor equal to the header gives `fork.header-mismatch` (Info; the Mac ignores it).
7. **Map fields** are kept as read: the six bytes of `mNext` and `refNum`, `mAttr` (with any `mapChanged` or
   `mapCompact` bits), `mInMemoryAttr`.
8. **Type list.** A count word beyond the map throws. The count is (u16)(word + 1), so `$FFFF` is 0 types. For each
   type:
   - an entry past the map: `fork.type-list-truncated`, and the remaining types are dropped;
   - a type already seen: `fork.duplicate-type`, and its resources are merged with the first list's;
   - a count − 1 of `$7FFF` or more, with the Mac OS 9 model: `fork.mac-hangs`;
   - a reference list that does not start where the previous one ended (starting from the end of the type list):
     `fork.ref-lists-out-of-order`, once per fork.
9. **References**, in list order, each:
   - past the map: `fork.ref-list-out-of-range`, and the rest of that type's list is dropped;
   - a type and ID already read: `resource.duplicate`, and **the first is kept**, as GetResource returns it;
   - data item whose length word is not wholly inside the data area: `resource.data-out-of-range`, resource skipped;
   - a length over `MaxResourceSize`: `resource.too-large`, resource skipped;
   - a length past the end of the data area: `resource.data-truncated`, and the bytes that are there are kept;
   - a name whose length byte or bytes lie outside the map: `resource.name-out-of-range`, and the resource is kept
     unnamed.
10. **Overlaps.** Items that overlap or are shared give `resource.data-overlap` (Info) for each overlap.
11. **Too damaged.** Past 1000 Error diagnostics raised in step 9 (not counting `fork.type-list-truncated`), the reader
    throws (§7).

Each resource keeps, for the writer: its data offset and name offset as read (its **placement**), whether its data is
unchanged, and the 4-byte handle field. The data are slices of the input buffer, which must not change afterwards.

---

## 10. How the Resource Manager writes a fork

UpdateResFile and CloseResFile write the fork; the layout below was traced in both Resource Managers and checked in
SheepShaver [Code: Mac OS 9.0, 68k ROM; Verified], except where marked.

### 10.1 A new fork

CreateResFile writes a 286-byte fork [Verified]:

- the header `dO` = `$100`, `mO` = `$100`, `dL` = 0, `mL` = `$1E`;
- bytes 16–255: zeros on Mac OS 9 [Verified]; not written by the 68k ROM [Code: 68k ROM];
- at `$100`, the empty map: the header copy, `mNext` and `refNum` (zero on Mac OS 9), `mAttr` 0, `mInMemoryAttr` 0,
  `mT` = `$001C`, `mN` = `$001E`, type count − 1 = `$FFFF`.

CreateResFile on a file that already has a resource fork returns `dupFNErr` and leaves it untouched [Verified].

### 10.2 Order

- Types stay in the order they were first added; resources within a type in the order added; names in the order set
  (§5.6).

### 10.3 Where data goes

- New data, and data that grew, is written **at the end**, after the old map.
- Data that changed without growing is rewritten in place, leaving the rest of its old item as stale bytes [Verified].
- **Compaction** happens after a resource is added or removed, after a size change, or when `mapCompact` is set with
  SetResFileAttrs. It keeps the untouched prefix of the data area, then packs every item **in ascending order of its old
  offset**, with no padding [Verified]. Shared items stay shared (§4).
- The map is always written straight after the data; then the header is written at 0 and the fork is cut to the end of
  the map. The data offset stays `$100`.
- The map is written from memory: `mAttr` without `$60`, attributes without `resChanged`, and `mNext`, `refNum` and the
  reference handles as whatever they held (§5.1).

### 10.4 Where the two differ

| Case | Mac OS 9 | 68k ROM |
| --- | --- | --- |
| Bytes 16–255 at CreateResFile | zeros [Verified] | not written [Code] |
| Removing the last resource | sets `dL` to 0 [Verified] | keeps the old data [Code] |
| Compacting a fork whose map precedes its data | corrupts it (a 63 MB fork with a garbage data offset) [Verified] | rebases it correctly [Code] |

When SetResourceSize moves a resource's data, the two copy a different number of bytes [Code: Mac OS 9.0; 68k ROM].

---

## 11. What ClassicMac writes

`ResourceFork.Write` and `ToArray` write the fork as the Resource Manager leaves it **after a compacting update**
(§10.3). A fork the Mac left compacted comes back byte for byte: 142 of 152 real forks in the test corpus, and 29 of
33 forks written by Mac OS 9 in SheepShaver; the rest hold stale bytes from in-place rewrites, duplicate IDs, or the
fork Mac OS 9 corrupts [Verified]. Round trips are otherwise judged on the model.

### 11.1 Layout

| Part | Contents |
| --- | --- |
| Header | `dO` = 256, `mO` = 256 + `dL`, `dL`, `mL` |
| Bytes 16–255 | `SystemData` and `ApplicationData` as kept (zeros for a new fork) |
| Data area | one item per distinct data block, packed with no padding, in **placement order** (§11.2) |
| Map header | the header again; the six kept bytes of `mNext` and `refNum` (zeros for a new fork); `mAttr` & `~$60`; `mInMemoryAttr` as kept; `mT` = 28; `mN` = 28 + type list + references |
| Type list | at map +28: count − 1 (`$FFFF` for none), then the types **in the order each first appears** among the fork's resources |
| Reference lists | straight after the type list, contiguous in type order; within a type, **in the order the resources were read or added**; each attribute byte without `resChanged`; the handle field as read (zero for new resources) |
| Name list | the names in **placement order** (§11.2), each as length + bytes |

A new, empty `ResourceFork` writes exactly the 286 bytes of Mac OS 9's CreateResFile (§10.1).

### 11.2 Placement

Every resource carries a data placement and a name placement: the offset it was read from, or, for data or names
added later, a key beyond every real offset, increasing in the order the changes happen. This reproduces the Resource
Manager's "new things go at the end, then compaction packs by old offset":

- **Read resources** keep their order in the data area and the name list.
- **`SetData` with longer data** moves the item after all old data; shorter or equal data keeps its place.
- **A new name** (`Name` set to a different value) moves the name to the end of the name list, as SetResInfo does;
  setting the same name again changes nothing.
- **A new resource** gets new data and name placements, after everything else; it is appended to its type's list, and
  a new type goes after the existing ones.
- **`Renumber`** changes the ID in place and keeps the resource's position.
- **Sharing.** Resources whose data are unchanged since reading and were read from the same offset are written as one
  shared item again. Any other data, including data that only partly overlapped another item, is written as its own
  item. Names are never shared on output.

The in-memory `resChanged` bits and `mapChanged`/`mapCompact` are dropped on output only; the model keeps them. The
writer does not compress: a compressed resource's stored bytes (§12) are written as they are.

---

## 12. Compressed resources

System 7 added compressed resources: a resource whose attribute bit 0 is set and whose data begins with an extended
header. The Resource Manager decompresses it when it loads the resource (CheckLoad), with a `'dcmp'` resource whose ID
the header names [Code: 68k ROM; Mac OS 9.0]. Apple never documented the format; everything here comes from the code.

**ClassicMac** keeps the stored bytes in the model and decompresses on request: `ResourceDecompression.GetData`
returns what the Resource Manager would hand an application.

### 12.1 The extended header

18 bytes, then the compressed data. A common part, then one of two tails chosen by the version byte [Code: 68k ROM;
Mac OS 9.0]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | u32 | robustness signature `$A89F6572` |
| +$04 | 2 | u16 | header length (18); **never read** |
| +$06 | 1 | u8 | version: 8 or 9 |
| +$07 | 1 | u8 | header attributes; bit 0 = `resCompressed` |
| +$08 | 4 | u32 | decompressed size |

Version 8 (for decompressors with a working buffer):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$0C | 1 | u8 | working-buffer fraction, out of 256 (§12.4) |
| +$0D | 1 | u8 | expansion bytes |
| +$0E | 2 | i16 | `'dcmp'` ID |
| +$10 | 2 | u16 | reserved, must be 0 |

Version 9:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$0C | 2 | i16 | `'dcmp'` ID |
| +$0E | 2 | u16 | expansion bytes |
| +$10 | 1 | u8 | param1, for the decompressor |
| +$11 | 1 | u8 | param2, for the decompressor |

- The compressed data **always starts at +18**, whatever the header-length field says [Code: 68k ROM; Mac OS 9.0].
- **Expansion bytes** are the margin the block is enlarged by so that decompressing in place does not overwrite unread
  input (§12.3).
- param1 and param2 are not read by the Resource Manager; it passes the header to the decompressor [Code].
- **Version.** The 68k ROM takes 8 as version 8, 9 as version 9, and fails anything else with `CantDecompress`; Mac OS 9
  reads **any value other than 8 as version 9** [Code: 68k ROM; Mac OS 9.0; Verified].
- **Version 8's reserved word** must be zero; otherwise both fail with `CantDecompress` [Code; Verified].
- `badExtResource` (−185) is defined but never returned [Code: 68k ROM; Mac OS 9.0].

### 12.2 When data is decompressed

| Resource attribute bit 0 | Signature | Header attribute bit 0 | Result |
| --- | --- | --- | --- |
| clear | any | any | loaded as stored, header included [Verified] |
| set | absent | — | loaded as stored [Verified] |
| set | present | clear | **"extended, uncompressed"**: 68k ROM: length − 12 bytes from +12 (the first 12 header bytes stripped) [Code]; Mac OS 9: length − 12 bytes from +0 (the header kept, the last 12 bytes lost) [Verified] |
| set | present | set | decompressed (§12.3) |

- Decompression happens only when ResLoad is on and the resource is not already loaded; otherwise later, in
  LoadResource [Code].
- **Sizes of an unloaded compressed resource:** GetResourceSizeOnDisk gives the decompressed size on the 68k ROM and
  the **decompressed size + expansion bytes** on Mac OS 9 [Code; Verified]; GetMaxResourceSize the on-disk gap (§4)
  [Verified]; ReadPartialResource reads the raw stored bytes, header included [Verified].

### 12.3 The block and in-place decompression

[Code: 68k ROM; Mac OS 9.0; Verified]

1. A block of **decompressed size + expansion bytes** is allocated.
2. The compressed bytes (stored length − 18) are read into the **tail** of the block.
3. The decompressor writes its output from the start of the block, reading from the tail: output may overtake the
   input, which is then corrupted exactly as the decompressor wrote it. Nothing pads the input; nothing after the block
   is cleared.
4. Afterwards the handle is set to the **declared decompressed size**, whatever was written, and any error from that is
   ignored: extra output is cut off, and short output is extended with the block's leftover contents [Verified].

**ClassicMac** reproduces this: the block is followed by **2048 zero bytes** standing in for the memory after it
(enough for `'dcmp'` 3's largest overshoot, §15.5; the emulator's memory there was zero). A decompressor may read or
write that area; anything beyond it, or before the block, stops decompression with `resource.dcmp-overrun`. Compressed
data longer than the block is refused the same way. The result is the first *decompressed size* bytes of the block.

### 12.4 The working buffer (version 8)

Version-8 decompressors get a working buffer [Code: 68k ROM; Mac OS 9.0]:

- n = 0 when the fraction is 0, else (block × (fraction + 1)) >> 8, with *block* including the expansion bytes.
- **68k ROM:** a 32-bit multiply and a logical shift; the size passed is **n + 4** (a locked handle) [Code: 68k ROM].
- **Mac OS 9:** a 32-bit multiply and an arithmetic shift; the size passed is **n + 2** (NewPtr) [Code: Mac OS 9.0;
  Verified: sizes 29, 2 and 110 for a 108-byte block with fractions `$3F`, `$00`, `$FF`].
- The buffer is not cleared.

Version 9 has no working buffer.

### 12.5 Finding the decompressor

| | 68k ROM [Code] | Mac OS 9 [Code; Verified] |
| --- | --- | --- |
| Where | from the resource's **own file** down the map chain, Get1Resource(`'dcmp'`, ID) in each map | a native `'ncmp'` table first, then GetResource(`'dcmp'`, ID) through the **whole chain from the current map** |
| Password bit | only maps with `decompressionPasswordBit` (§5.3) are searched | ignored |
| Not found | nil with ResError 0 or −192 goes on to the next map; any other error is `CantDecompress` | see §12.6 |
| Caching | none | |

- On Mac OS 9.0, `'ncmp'` 0 and 2 (native versions of `'dcmp'` 0 and 2) exist but are never used: they lack an entry
  point, and a file's own `'dcmp'` 0 overrode the System one [Verified]. So **every decompressor runs as 68k code**,
  and a file or application can supply or override any ID [Verified: overrides of 0, 1 and new IDs].
- **The entry form is chosen by the resource header's version**, not by the decompressor [Code]:
  - **Version 8:** the entry is at byte 0 of the `'dcmp'`; Pascal convention, arguments pushed as source, destination,
    working buffer, working size; the callee pops 16. The 68k ROM passes the entry in A0 and the resource handle in A5,
    and needs D3–D7 and A2–A6 preserved; Mac OS 9 calls it through CallUniversalProc with procInfo `$3FC0`.
  - **Version 9:** the `'dcmp'` starts with three u16 offsets to Prepare(header), Decompress(source, destination,
    header) (pops 12) and Done(header). *header* points to the Resource Manager's copy of the 18-byte header. Mac OS 9
    passes garbage to Prepare and Done; the System's decompressors ignore them.
- **ClassicMac** runs its own implementations of `'dcmp'` 0–3 (§13–15) and any registered with it (§16). When the fork
  holds a `'dcmp'` of the needed ID that the Mac would run instead (any, on Mac OS 9; only in a map with the password
  bit, on the 68k ROM), it notes `resource.dcmp-overridden` and still uses its own. It does not search other files.
  A header version that does not match the decompressor's form (version 9 for `'dcmp'` 0 or 1, version 8 for 2 or 3)
  is `resource.dcmp-form`: the Mac would jump into the wrong entry point.

### 12.6 Failures

| Case | 68k ROM [Code] | Mac OS 9 [Code; Verified] | ClassicMac |
| --- | --- | --- | --- |
| No `'dcmp'` of that ID | nil, `CantDecompress` | a handle of the decompressed size holding the **untouched compressed input**, ResError 0 | stored bytes, `resource.dcmp-unknown` |
| Version not 8 or 9 | `CantDecompress` | decoded as version 9 | as the model says; `resource.dcmp-version` |
| Version-8 reserved word ≠ 0 | nil, `CantDecompress` | nil, `CantDecompress` | stored bytes, `resource.dcmp-header` |
| Working buffer cannot be allocated | nil, `memFullErr` (the map entry is left dangling) | nil, `memFullErr` | — |
| I/O error | a garbage handle and the error | nil and the error | — |

ClassicMac returns the **stored bytes** whenever it cannot decompress, whatever the Mac would have returned.

---

## 13. `'dcmp'` 0 and 1

Two byte-oriented decompressors for 68k code and data, both in the version-8 form, sharing their memo-table and
extension code and differing in the opcode map and the constant table [Code: `'dcmp'` 0, 1]. resource_dasm (MIT)
implements the same two; its constant tables and opcode maps match Apple's.

### 13.1 Opcodes

Each opcode is one byte; decoding runs until opcode `$FF`, **the only terminator**. The declared size is never
checked, and neither the input nor the output is bounds-checked [Code].

| `'dcmp'` 0 | `'dcmp'` 1 | Action |
| --- | --- | --- |
| `00` v | `D0` v | literal: copy (2v) & `$FFFF` bytes (`'dcmp'` 0) or v & `$FFFF` bytes (`'dcmp'` 1) from the input |
| `01`–`0F` | `00`–`0F` | literal: copy 2n bytes (`'dcmp'` 0) or n + 1 bytes (`'dcmp'` 1) |
| `10` v | `D1` v | as `00`/`D0`, and **remember** the string (§13.3) |
| `11`–`1F` | `10`–`1F` | literal of 2(n − `$10`) bytes (`'dcmp'` 0) or n − `$0F` bytes (`'dcmp'` 1), remembered |
| `20` b | `D2` b | recall slot b + `$28` (`'dcmp'` 0) or b + `$B0` (`'dcmp'` 1) |
| `21` b | `D3` b | recall slot b + `$128` or b + `$1B0` |
| `22` w | `D4` w | recall slot (w + `$28`) & `$FFFF` or (w + `$B0`) & `$FFFF` |
| `23`–`4A` | `20`–`CF` | recall slot n − `$23` or n − `$20` |
| `4B`–`FD` | `D5`–`FD` | write the constant word *table*[n − `$4B`] or *table*[n − `$D5`] (§13.5) |
| `FE` | `FE` | extension (§13.4) |
| `FF` | `FF` | end |

*v* is a varint, *b* a byte, *w* a big-endian u16.

**Varint** [Code: `'dcmp'` 0, 1]: read a byte *x*:

- *x* < `$80`: *x*;
- *x* = `$FF`: the next four bytes, as a 32-bit value;
- otherwise: (*x* − `$C0`) × 256 + the next byte. So first bytes `$C0`–`$FE` give 0 to `$3EFF`, and **`$80`–`$BF`
  give −`$4000` to −1**.

### 13.2 The working buffer and memo table

The remembered strings live in the working buffer of §12.4 [Code: `'dcmp'` 0, 1]:

- word 0 = offset of the next free slot, starting at 4;
- word 1 = the working size & `$FFFF`;
- slot *i*'s start is the word at 4 + 2*i*, and its end the word before it (for slot 0, word 1).

All values are 16-bit. Strings grow down from the end of the buffer, slot words up from offset 4, with **no capacity
check**: when the table fills, they overwrite each other, as on the Mac.

### 13.3 Remember and recall

- **Remember** a string of *length* bytes: *slot* = word 0; *start* = (word at *slot* − 2) − *length*; store *start*
  at *slot*; word 0 = *slot* + 2; copy the *length* input bytes into the buffer at *start* **before** they are copied
  to the output.
- **Recall** slot *i*: *start* = word at 4 + 2*i*; *length* = (word at 4 + 2*i* − 2) − *start*, 16-bit; copy those
  buffer bytes to the output.
- A slot that was never filled reads whatever the buffer held [Code]. **ClassicMac** starts the buffer zeroed, reports
  `resource.dcmp-undefined-slot` once, and stops with `resource.dcmp-overrun` if a string lies beyond the buffer
  (which it caps at 64 KiB, all a 16-bit offset can reach).

### 13.4 Extensions (`FE`)

`FE` is followed by a sub-opcode byte. Counts are taken as their **low 16 bits, unsigned** (a `dbf` loop) [Code:
`'dcmp'` 0, 1]. Values written as words are their low 16 bits.

| Sub-op | Operands (varints unless noted) | Output |
| --- | --- | --- |
| 0 | *seg*, *cnt*, then *cnt* deltas | an export table: *index* starts at 6; for each delta, *index* = (*index* + delta − 6) & `$FFFF`, then the words `3F3C` *seg* `A9F0` *index*. After exactly *cnt* entries, one more `3F3C` *seg* `A9F0` |
| 1 | *target*, *a5Δ*, *cnt*, *a5* | a jump table of (*cnt* & `$FFFF`) + 1 entries `6100` *target* `4EED` *a5*; before each entry after the first, *target* −= 8 and *a5* += *a5Δ*, or, when *a5Δ* & `$FFFF` = 0, *a5* = the next varint |
| 2 | *value*, *cnt* | (*cnt* & `$FFFF`) + 1 bytes of *value* (its low byte) |
| 3 | *value*, *cnt* | (*cnt* & `$FFFF`) + 1 words of *value* |
| 4 | *value*, *cnt*, then signed bytes | (*cnt* & `$FFFF`) + 1 words: *value*, then each next one plus a signed byte from the input |
| 5 | *value*, *cnt*, then varints | as 4, with varint deltas |
| 6 | *value*, *cnt*, then varints | (*cnt* & `$FFFF`) + 1 **longs**, with varint deltas (32-bit) |
| 7–255 | — | nothing: only the sub-opcode byte is consumed, and decoding continues |

### 13.5 Constant tables

The words are immediates in the decompressors' dispatch tables [Code: `'dcmp'` 0, 1]; each row starts at the opcode
given.

`'dcmp'` 0, opcodes `4B`–`FD` (179 words):

```
4B: 0000 4EBA 0008 4E75 000C 4EAD 2053 2F0B 6100 0010 7000 2F00 486E 2050 206E 2F2E
5B: FFFC 48E7 3F3C 0004 FFF8 2F0C 2006 4EED 4E56 2068 4E5E 0001 588F 4FEF 0002 0018
6B: 6000 FFFF 508F 4E90 0006 266E 0014 FFF4 4CEE 000A 000E 41EE 4CDF 48C0 FFF0 2D40
7B: 0012 302E 7001 2F28 2054 6700 0020 001C 205F 1800 266F 4878 0016 41FA 303C 2840
8B: 7200 286E 200C 6600 206B 2F07 558F 0028 FFFE FFEC 22D8 200B 000F 598F 2F3C FF00
9B: 0118 81E1 4A00 4EB0 FFE8 48C7 0003 0022 0007 001A 6706 6708 4EF9 0024 2078 0800
AB: 6604 002A 4ED0 3028 265F 6704 0030 43EE 3F00 201F 001E FFF6 202E 42A7 2007 FFFA
BB: 6002 3D40 0C40 6606 0026 2D48 2F01 70FF 6004 1880 4A40 0040 002C 2F08 0011 FFE4
CB: 2140 2640 FFF2 426E 4EB9 3D7C 0038 000D 6006 422E 203C 670C 2D68 6608 4A2E 4AAE
DB: 002E 4840 225F 2200 670A 3007 4267 0032 2028 0009 487A 0200 2F2B 0005 226E 6602
EB: E580 670E 660A 0050 3E00 660C 2E00 FFEE 206D 2040 FFE0 5340 6008 0480 0068 0B7C
FB: 4400 41E8 4841
```

`'dcmp'` 1, opcodes `D5`–`FD` (41 words):

```
D5: 0000 0001 0002 0003 2E01 3E01 0101 1E01 FFFF 0E01 3100 1112 0107 3332 1239 ED10
E5: 0127 2322 0137 0706 0117 0123 00FF 002F 070E FD3C 0135 0115 0102 0007 003E 05D5
F5: 0201 0607 0708 3001 0133 0010 1716 373E 3637
```

---

## 14. `'dcmp'` 2

"GreggyBits": 16-bit words looked up in a 256-word table, optionally mixed with literal words; version-9 form
[Code: `'dcmp'` 2]. macresources (MIT) and resource_dasm implement it too.

1. **Custom table.** If param2 bit 0 is set, (param1 + 1) big-endian words follow at the start of the input and are
   copied into a 256-word **custom table kept inside the decompressor's code**, which is used instead of the default
   table. The table is never cleared: entries above param1 keep the words an earlier resource put there (zero after the
   decompressor is freshly loaded) [Code; emulated]. Indexes are **not checked** against param1.
2. **Words.** *words* = decompressed size >> 1.
   - **param2 bit 1 clear:** each input byte is a table index; write that table word. The loop is a do-while, so a
     declared size of 0 or 1 still writes **one word** [Code; emulated].
   - **param2 bit 1 set:** for each group of 8 words, one flag byte, most significant bit first: 1 = an index byte
     (write the table word), 0 = two literal bytes. The (*words* & 7) leftover words use the top bits of one more flag
     byte.
3. **Odd size.** If the decompressed size is odd, one final input byte is copied as it is, in both modes.

No other param2 bits are used.

**ClassicMac** models a freshly loaded `'dcmp'` 2: custom-table entries above param1 are zero, and using one gives
`resource.dcmp2-stale-table` (Info). resource_dasm's `'dcmp'` 2 has a bug (it compares a byte count with a word count
and decodes only half the output, then falls back to emulating Apple's code); it also ignores table persistence and
declared sizes 0 and 1.

The default table [Code: `'dcmp'` 2]:

```
00: 0000 0008 4EBA 206E 4E75 000C 0004 7000 0010 0002 486E FFFC 6000 0001 48E7 2F2E
10: 4E56 0006 4E5E 2F00 6100 FFF8 2F0B FFFF 0014 000A 0018 205F 000E 2050 3F3C FFF4
20: 4CEE 302E 6700 4CDF 266E 0012 001C 4267 FFF0 303C 2F0C 0003 4ED0 0020 7001 0016
30: 2D40 48C0 2078 7200 588F 6600 4FEF 42A7 6706 FFFA 558F 286E 3F00 FFFE 2F3C 6704
40: 598F 206B 0024 201F 41FA 81E1 6604 6708 001A 4EB9 508F 202E 0007 4EB0 FFF2 3D40
50: 001E 2068 6606 FFF6 4EF9 0800 0C40 3D7C FFEC 0005 203C FFE8 DEFC 4A2E 0030 0028
60: 2F08 200B 6002 426E 2D48 2053 2040 1800 6004 41EE 2F28 2F01 670A 4840 2007 6608
70: 0118 2F07 3028 3F2E 302B 226E 2F2B 002C 670C 225F 6006 00FF 3007 FFEE 5340 0040
80: FFE4 4A40 660A 000F 4EAD 70FF 22D8 486B 0022 204B 670E 4AAE 4E90 FFE0 FFC0 002A
90: 2740 6702 51C8 02B6 487A 2278 B06E FFE6 0009 322E 3E00 4841 FFEA 43EE 4E71 7400
A0: 2F2C 206C 003C 0026 0050 1880 301F 2200 660C FFDA 0038 6602 302C 200C 2D6E 4240
B0: FFE2 A9F0 FF00 377C E580 FFDC 4868 594F 0034 3E1F 6008 2F06 FFDE 600A 7002 0032
C0: FFCC 0080 2251 101F 317C A029 FFD8 5240 0100 6710 A023 FFCE FFD4 2006 4878 002E
D0: 504F 43FA 6712 7600 41E8 4A6E 20D9 005A 7FFF 51CA 005C 2E00 0240 48C7 6714 0C80
E0: 2E9F FFD6 8000 1000 4842 4A6B FFD2 0048 4A47 4ED1 206F 0041 600C 2A78 422E 3200
F0: 6574 6716 0044 486D 2008 486C 0B7C 2640 0400 0068 206D 000D 2A40 000B 003E 0220
```

---

## 15. `'dcmp'` 3

A bit-stream LZ77 codec in the version-9 form. **Every compressed resource in the Mac OS 9.0 System uses it** (34
resources) [Code: `'dcmp'` 3]. ClassicMac's implementation was first ported from resource_dasm's `System3.cc` (MIT; see
`THIRD-PARTY-NOTICES.md`) and then checked against the disassembly of the Mac OS 9.0 System's `'dcmp'` 3; Apple's code,
run in an emulator, gives the same output on all 34 System resources and on crafted inputs. Disk Copy's KenCode is the
same codec (see [DISK-IMAGES.md](DISK-IMAGES.md)).

Only the header's decompressed size (+8) is read; version, ID, expansion bytes, param1 and param2 are ignored [Code].

### 15.1 Bits

Bits are read **most significant first**. A literal byte is the next 8 bits of the stream, **not byte-aligned**
[Code]. (Apple's reader for 9 or more bits prefetches up to three bytes; reading a byte at a time gives the same
values.) Past the end of the input the Mac reads whatever memory follows: nothing checks it [Code].

### 15.2 Commands

A flag, *literal allowed*, starts true. While *written* < decompressed size (unsigned) [Code: `'dcmp'` 3]:

1. Read a copy length *L* (§15.3, 0–2042).
2. If *L* = 0 and *literal allowed*: a **literal run**. Read *n* (§15.3, 1–63) and copy *n* 8-bit values from the
   stream. *literal allowed* = (*n* = 63): only a full 63-byte run may be followed by another.
3. Otherwise a **back-reference** of *L* + 2 bytes, **plus 1 if *literal allowed* was false** (so a back-reference
   straight after a short literal run is at least 3 bytes). *literal allowed* = true. Read an offset (§15.4) chosen by
   *written* before this command, and copy forward, a byte at a time, from *written* − offset: an offset shorter than
   the length repeats the pattern.

Offset 0 cannot be encoded. An offset greater than *written* is not checked: the Mac reads the bytes before the output
[Code]. No real resource does this; **ClassicMac** stops with `resource.dcmp-overrun`.

### 15.3 Length codes

**Copy length, 0–2042.** Count up to ten leading 1 bits (*k*); the 0 that ends them is consumed only when *k* < 10
[Code: `'dcmp'` 3]:

| *k* | Then | Length |
| --- | --- | --- |
| 0 | 1 bit *x* | *x* (0 or 1) |
| 1 | 1 bit: 0 | 2 |
| 1 | 1 bit: 1, then 1 bit *x* | 3 + *x* |
| 2 | 1 bit: 0, then 1 bit *x* | 5 + *x* |
| 2 | 1 bit: 1, then 2 bits *x* | 7 + *x* |
| 3 | 3 bits *x* | 11 + *x* |
| 4 | 3 bits *x* | 19 + *x* |
| 5–10 | *k* bits *x* | 2^*k* − 5 + *x* (27, 59, 123, 251, 507, 1019) |

**Literal length, 1–63** [Code: `'dcmp'` 3]:

| Code | Length |
| --- | --- |
| `0` | 1 |
| `100` | 2 |
| `101` | 3 |
| `110` + 2 bits *x* | 4 + *x* |
| `111` + 4 bits *s*, *s* < 8 | 8 + *s* |
| `111` + 4 bits *s*, 8 ≤ *s* < 12, + 2 bits *x* | 16 + 4(*s* − 8) + *x* |
| `111` + 4 bits *s*, *s* ≥ 12, + 3 bits *x* | 32 + 8(*s* − 12) + *x* |

### 15.4 Offset codes

The offset code depends on *written*, the bytes output before the command. Three forms [Code: `'dcmp'` 3]:

- `0` + *a* bits *x* → 1 + *x*
- `10` + (*a* + 2) bits *x* → 1 + 2^*a* + *x*
- `11` + *w* bits *x* → *base* + *x*, with *base* = 1 + 2^*a* + 2^(*a*+2) and the width *w* chosen by *written*

| *written* | *a* | `10`: base | `11`: base | `11`: width by *written* |
| --- | --- | --- | --- | --- |
| 0–`$A` | 0 | 2 | 6 | ≤ 7: 1, ≤ 9: 2, else 3 |
| `$B`–`$14` | 1 | 3 | `$B` | ≤ `$C`: 1, ≤ `$E`: 2, ≤ `$12`: 3, else 4 |
| `$15`–`$28` | 2 | 5 | `$15` | ≤ `$16`: 1, ≤ `$18`: 2, ≤ `$1C`: 3, ≤ `$24`: 4, else 5 |
| `$29`–`$50` | 3 | 9 | `$29` | ≤ `$2A`: 1, ≤ `$2C`: 2, ≤ `$30`: 3, ≤ `$38`: 4, ≤ `$48`: 5, else 6 |
| `$51`–`$A0` | 4 | `$11` | `$51` | ≤ `$52`: 1, ≤ `$54`: 2, ≤ `$58`: 3, ≤ `$60`: 4, ≤ `$70`: 5, ≤ `$90`: 6, else 7 |
| `$A1`–`$2A0` | 5 | `$21` | `$A1` | ≤ `$A2`: 1, ≤ `$A4`: 2, ≤ `$A8`: 3, ≤ `$B0`: 4, ≤ `$C0`: 5, ≤ `$E0`: 6, ≤ `$120`: 7, ≤ `$1A0`: 8, else 9 |
| `$2A1`–`$3E8` | 6 | `$41` | `$141` | ≤ `$340`: 9, else 10 |
| `$3E9`–`$A80` | 7 | `$81` | `$281` | ≤ `$480`: 9, **≤ `$66C`: 10**, else 11 |
| `$A81`–`$1500` | 8 | `$101` | `$501` | ≤ `$D00`: 11, else 12 |
| `$1501`–`$2A00` | 9 | `$201` | `$A01` | ≤ `$1A00`: 12, else 13 |
| `$2A01`–`$5400` | 10 | `$401` | `$1401` | ≤ `$3400`: 13, else 14 |
| `$5401`–`$A800` | 11 | `$801` | `$2801` | ≤ `$6800`: 14, else 15 |
| `$A801`–`$11170` | 12 | `$1001` | `$5001` | ≤ `$D000`: 15, else 16 |
| `$11171`–`$2A000` | 13 | `$2001` | `$A001` | ≤ `$12000`: 15, ≤ `$1A000`: 16, else 17 |
| above `$2A000` | 14 | `$4001` | `$14001` | ≤ `$34000`: 17, else 18 |

- The regular rule is: width *k* while *written* ≤ *base* − 1 + 2^*k*, for *k* = 1 … *a* + 3, else *a* + 4; the table
  shows the widths reachable in each range. Note the odd range limits `$3E8` and `$11170`.
- **`$66C`** replaces the regular `$680` for *a* = 7 and is live: with *written* from `$66D` to `$680` the third form
  reads **11 bits** [Code: `'dcmp'` 3; emulated].
- Two other irregular thresholds exist in the code but can never apply: `$200C` (7 bits) for *a* = 14, which runs only
  above `$2A000`; and `$288` for *a* = 7, which runs only from `$3E9` (Apple's code reads 3 bits there, as the regular
  rule gives; resource_dasm reads 4) [Code].
- The last width of each form is an unconditional else: there is no range check [Code].

### 15.5 The end and the overshoot

- There is **no end code**: decoding stops when *written* reaches the declared size, checked only between commands. The
  last command is completed, so output may run **up to 2044 bytes** past the declared size (a back-reference of
  2042 + 3 bytes starting one byte short of it), or 62 for a literal run [Code: `'dcmp'` 3; emulated]. It lands in the
  expansion bytes and, beyond them, in whatever memory follows the block; the Resource Manager then cuts the handle to
  the declared size (§12.3).
- A declared size of 0 writes nothing.
- All 34 System resources consume exactly their input, to the last partial byte.

**ClassicMac** allows the overshoot into its 2 KiB after the block (`resource.dcmp-wrote-past-block`, Info, when it
passes the expansion bytes) and reads zeros past the input (`resource.dcmp-read-past-input`, Warning). resource_dasm
throws in both cases, and on an offset past the output.

---

## 16. Application-supplied decompressors

Applications compressed their own resources with private `'dcmp'` resources (§12.5 says how the Mac finds them).
ClassicMac cannot run 68k code; an application of ClassicMac supplies a decompressor instead:

- implement `IResourceDecompressor`: an `Id` (the `'dcmp'` ID) and `int Decompress(DecompressionContext context)`;
- pass it to `new ResourceDecompression(extra)`. A decompressor with a built-in ID (0–3) **replaces** the built-in one,
  as a file's own `'dcmp'` would on the Mac; among several with one ID, the last wins.

The context models the Mac's call:

| Member | Meaning |
| --- | --- |
| `Header` | the parsed 18-byte header (§12.1), including param1/param2 or the version-8 fields |
| `Block` | the block (decompressed size + expansion bytes) followed by 2048 zero bytes |
| `BlockLength` | the length of the block proper |
| `SourceOffset` | where the compressed bytes start; they end at `BlockLength` |
| `Options` | the `ReadOptions`, including which Resource Manager is modelled |
| `Report(severity, code, message)` | adds a diagnostic without stopping |

Decompress in place, writing from offset 0, and return the number of bytes written. The result is cut or padded to the
declared size as in §12.3. Throw `InvalidDataException` for bad input; it, `IndexOutOfRangeException` and
`ArgumentException` become `resource.dcmp-failed` and the stored bytes are returned.

---

## 17. Mac OS 9 and 68k ROM differences

`ReadOptions.ResourceManager` selects the model; Mac OS 9 is the default.

| Area | Mac OS 9 | 68k ROM | § |
| --- | --- | --- | --- |
| Open checks | header and full map checks; rejects much | almost none; opens most damaged forks | 8.1, 8.2 |
| Header fields | unsigned | signed | 3 |
| Fork end limit | ≤ `$FFFFFE` | < `$FFFFFF` | 7 |
| A type listed twice | GetResource searches every list | first list only | 8.4 |
| Resource count `$FFFF` | opens, then hangs in Preload | not traced | 8.1 |
| `mInMemoryAttr` from disk | `& $E1` | `& $C1` | 5.3 |
| CreateResFile, bytes 16–255 | zeros | not written | 10.1 |
| Removing the last resource | `dL` = 0 | old data kept | 10.4 |
| Compacting map-before-data | corrupts the fork | rebases correctly | 10.4 |
| Header version not 8 or 9 | read as version 9 | `CantDecompress` | 12.1 |
| Extended, uncompressed | header kept, last 12 bytes lost | first 12 bytes stripped | 12.2 |
| GetResourceSizeOnDisk (compressed) | decompressed size + expansion | decompressed size | 12.2 |
| Working buffer | n + 2, arithmetic shift | n + 4, logical shift | 12.4 |
| `'dcmp'` search | whole chain from the current map; password bit ignored | own file downward; password-bit maps only | 12.5 |
| Missing `'dcmp'` | the compressed input in a decompressed-size handle, no error | nil, `CantDecompress` | 12.6 |

In ClassicMac the model changes the open verdict (`fork.mac-rejects`, `fork.mac-misreads`), `fork.mac-hangs` (Mac OS 9
only), and decompression (§12). The decompressors themselves are the same 68k code under both.

---

## 18. Diagnostics

Fork diagnostics are reported by `ResourceFork.Read` (with the fork offset of the problem, where there is one);
decompression diagnostics by `ResourceDecompression.GetData`, their message prefixed with the resource (`'TYPE' id`).
`fork.unreadable` is reported by `ClassicMac.Files` (`MacFileResources`) and the app when `ResourceFork.Read` throws.

| Code | Severity | Meaning | What the Mac does |
| --- | --- | --- | --- |
| `fork.mac-rejects` | Warning | The modelled Resource Manager would refuse to open the fork; the message names the error and the failed check. The fork is read anyway | OpenResFile fails with `eofErr`, `posErr`, `paramErr` or `mapReadErr` (§8) |
| `fork.mac-misreads` | Warning | (68k ROM model) the ROM would open the fork but read memory past its map | opens; later calls see garbage (§8.2) |
| `fork.mac-hangs` | Warning | (Mac OS 9 model) a type's count − 1 is `$7FFF` or more | at `$FFFF` the fork opens and Preload loops forever; other such counts fail check 11 (§8.1) |
| `fork.map-length` | Warning | `mL` is under 28 or runs past the fork; the map is taken to run to the end | Mac OS 9: `mapReadErr`; 68k ROM: `eofErr`, or a misread map |
| `fork.data-length` | Warning | the data area runs past the fork; it is cut at the end | Mac OS 9: `mapReadErr`; 68k ROM: opens, loads fail at `EOF` |
| `fork.header-mismatch` | Info | the map's header copy is neither zero nor equal to the header | ignored |
| `fork.type-list-truncated` | Error | the type count promises more types than the map holds; the rest are dropped | Mac OS 9: `mapReadErr`; 68k ROM: reads past the map |
| `fork.duplicate-type` | Warning | a type appears twice in the type list; its lists are merged | counting and indexing see the first list; GetResource searches all (Mac OS 9) or the first (68k ROM) (§8.4) |
| `fork.ref-lists-out-of-order` | Warning | the reference lists are not contiguous in type order (reported once) | opens; wrong IDs, `resNotFound` and mixed-up names for later types (§5.4) |
| `fork.ref-list-out-of-range` | Error | a reference list runs past the map; the rest of it is dropped | Mac OS 9: `mapReadErr`; 68k ROM: `mapReadErr` or reads past the map |
| `fork.unreadable` | Error | `ResourceFork.Read` threw (no header or map, over 32 MiB, or too damaged); the file is shown without resources | — |
| `resource.duplicate` | Warning | a second resource with the same type and ID; the first is kept | Get1Resource returns the first; the Ind calls list both (§8.4) |
| `resource.data-out-of-range` | Error | the item's length word lies outside the data area; the resource is skipped | Mac OS 9: `mapReadErr` when the offset is past `dL`; otherwise loading fails |
| `resource.too-large` | Error | a stored length, or a decompressed size, over `ReadOptions.MaxResourceSize`; skipped, or left compressed | loads it if memory allows, else `memFullErr` |
| `resource.data-truncated` | Error | the item runs past the data area; the bytes that are there are kept | opens; loading fails with `eofErr` (§8.3) |
| `resource.name-out-of-range` | Warning | the name lies outside the map; the resource is kept unnamed | Mac OS 9: `mapReadErr` when the offset is past the name list; else a name read from beyond the map |
| `resource.data-overlap` | Info | two items overlap or are shared; they are read, and written separately unless exactly shared | both load their bytes (§4) |
| `resource.not-compressed` | Info | marked compressed but without the signature; used as stored | loads as stored (§12.2) |
| `resource.extended-uncompressed` | Warning | (Mac OS 9 model) signature present, header bit 0 clear: the header kept, the last 12 bytes dropped | as described (§12.2); the 68k ROM model strips 12 bytes silently |
| `resource.dcmp-header` | Error | the extended header is shorter than 18 bytes, or a version-8 reserved word is not zero; stored bytes returned | `CantDecompress` for the reserved word; a short header is not traced |
| `resource.dcmp-version` | Info (Mac OS 9) / Error (68k ROM) | the header version is not 8 or 9 | Mac OS 9 decodes as version 9; the ROM fails with `CantDecompress` |
| `resource.dcmp-unknown` | Warning | no decompressor with the header's ID; kept compressed | Mac OS 9: the compressed bytes in a handle, no error; ROM: `CantDecompress` (§12.6) |
| `resource.dcmp-overridden` | Info | the fork carries its own `'dcmp'` of that ID, which the Mac would run; the built-in one was used | runs the file's `'dcmp'` (§12.5) |
| `resource.dcmp-form` | Error | the header version does not match the decompressor's entry form; kept compressed | jumps into the wrong entry point |
| `resource.dcmp-overrun` | Error | the compressed data do not fit the block, or decoding read or wrote outside the block and the 2 KiB after it, or referred to memory before the output; kept compressed | reads or overwrites neighbouring memory |
| `resource.dcmp-failed` | Error | an application-supplied decompressor threw; kept compressed | — |
| `resource.dcmp-read-past-input` | Warning | the decompressor read past its input; zeros were read | reads whatever memory follows |
| `resource.dcmp-wrote-past-block` | Info | the last command overshot the block, as `'dcmp'` 3 may; the result is cut to size | writes into the memory after the block |
| `resource.dcmp-size` | Warning | fewer bytes written than declared; the rest is the block's leftover contents | the same: SetHandleSize to the declared size (§12.3) |
| `resource.dcmp-undefined-slot` | Warning | (`'dcmp'` 0/1) a memo slot was recalled before it was filled; zeros used (reported once) | copies whatever the working buffer held |
| `resource.dcmp2-stale-table` | Info | (`'dcmp'` 2) a custom-table entry above param1 was used; zero used (reported once) | uses a word left by an earlier resource |

---

## 19. Not covered

- The Ind-call size cache for duplicate IDs (§8.4) was read in code only.
- The 68k ROM's behaviour was traced in code only; it was never run (SheepShaver runs the native Resource Manager).
- The size limits of §7 were traced in code but not yet run: reopening the 63 MB fork, forks ending at `$FFFFFE`
  (open) and `$FFFFFF` (`mapReadErr`), and AddResource's `eofErr` near `$1000000`.
- `'dcmp'` 0–3 of System versions other than 9.0, and the native `'ncmp'` decompressors, which Mac OS 9.0 never uses
  (they differ from `'dcmp'` 0 and 2 only on bad input).
- The trap patches that extensions install on the Resource Manager (Multiple Users, Apple Menu Options, language
  packs, the Process Manager): they change what running applications see, not what a file contains.
- Compressing resources: ClassicMac writes compressed resources only as they were read.
