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

§§2–7 describe the format, §8 what each Resource Manager accepts when it opens a fork, §§9–11
reading and writing, [compressed-resources.md](compressed-resources.md) compressed resources. §12 lists every point where the two Resource Managers
differ, and §13 every diagnostic ClassicMac reports.

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
12. [Mac OS 9 and 68k ROM differences](#12-mac-os-9-and-68k-rom-differences)
13. [Diagnostics](#13-diagnostics)
14. [Not covered](#14-not-covered)

---

## 1. Conventions

The conventions and source tags of [README.md](../README.md) hold. In addition:

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
| 0 | $01 | `decompressionPasswordBit` | The 68k ROM searches a map for `'dcmp'` resources only when this bit is set ([compressed-resources.md §1.5](compressed-resources.md#15-finding-the-decompressor)) |

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
| 0 | $01 | `resCompressed` | the data is compressed ([compressed-resources.md §1](compressed-resources.md#1-compressed-resources)) [Code: 68k ROM; Mac OS 9.0] |

- Bit 0 is `resExtended` in Apple's private equates of the System 7 era and was later published as `resCompressed`
  [Code: 68k ROM].
- The Resource Manager writes every attribute byte with `resChanged` cleared [Code: Mac OS 9.0, 68k ROM; Verified].

---

## 7. Size limits

| Limit | Value | Why | Source |
| --- | --- | --- | --- |
| Offset of a data item | ≤ `$FFFFFF` from `dO` | 24-bit data offsets | [Doc] |
| End of the data area and of the map | ≤ `$FFFFFE` (Mac OS 9), < `$FFFFFF` (68k ROM) | checked at open (§8) | [Code: Mac OS 9.0; 68k ROM] [Verified: Mac OS 9.0] |
| Map header, type list and reference lists | within the first 64 KiB of the map | `mN` is a 16-bit offset; reference-list offsets are 16-bit | [Doc] |
| Name list | a name must start within 64 KiB of `mN` | 16-bit name offsets | [Doc] |
| Types; resources per type | 65536 each | 16-bit counts minus one | [Doc] |
| Resources in practice | about 5,400 in total | 12 bytes each in the 64 KiB before the name list | [Doc] |
| Resource count `$FFFF` (65536) in one type | hangs Mac OS 9 | Preload loops forever after the fork opens | [Verified] |

- **A fork is effectively capped at 16 MiB.** Mac OS 9 will not open a fork whose data area or map ends past
  `$FFFFFE` [Code: Mac OS 9.0] [Verified: a fork ending at `$FFFFFE` opens, one ending at `$FFFFFF` gives −199];
  the 68k ROM rejects `$FFFFFF` and beyond [Code: 68k ROM].
- **The Resource Manager does not grow a fork that far.** AddResource and ChangedResource return `eofErr` (−39)
  when the fork's end + the resource's size + `$118` reaches `$1000000` [Code: Mac OS 9.0] [Verified: to a fork
  ending at `$FFFE00`, adding 512 bytes gives −39 and adding 3 bytes succeeds, and the fork is then laid out as the
  code predicts]. SetResourceSize returns −195 for a size of `$FFFFFF` or more, and −39 when moving the data would
  pass that limit [Code: Mac OS 9.0]. Where an item offset
  does pass 24 bits, every store keeps `offset & $FFFFFF` and UpdateResFile reports nothing [Code: Mac OS 9.0], but
  normal calls on a fork that opens cannot get there.
- **The one larger fork seen is corruption.** Mac OS 9 corrupts a fork whose map comes before its data when it
  compacts it: its item offsets are rebased but `dO` is not, and a length read past the end from an uncleared buffer
  is copied. Adding one 3-byte resource to such a fork produced a 63 MB fork with a garbage data offset
  [Code: Mac OS 9.0; Verified], which Mac OS 9 then refuses to open (`mapReadErr`, check 5 of §8.1)
  [Code: Mac OS 9.0] [Verified: reopening it gives −199]. The 68k ROM rebases the offsets correctly [Code: 68k ROM].

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
| 5 | max(`dO` + `dL`, `mO` + `mL`) ≤ `$FFFFFE` | −199 [Verified: a fork ending at `$FFFFFE` opens, at `$FFFFFF` gives −199] |
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

The reader is tolerant: it reads what it can, reports every problem as a diagnostic (§13), and throws
`InvalidDataException` only when the header, the map header or the type list cannot be found.

1. **Empty input** gives an empty fork with no diagnostics.
2. **Under 16 bytes** throws.
3. **The verdict.** The checks of §8.1 (or §8.2 with `Rom68k`) run on the whole fork. A refusal adds
   `fork.mac-rejects` ("Mac OS 9 would not open this fork (mapReadErr −199): …. Read anyway."); a ROM case that opens
   but reads past the map adds `fork.mac-misreads`. Reading continues either way, and the verdict is appended to any
   exception thrown later.
4. **Map header.** `mO` + 28 > `EOF` first tries the recovery ResEdit applies [ClassicMac]: if a map with a type-list
   offset of 28 or more, inside the fork, sits at `dO` + `dL`, that is taken as the map (to the end of the fork) and
   `fork.map-recovered` is reported; otherwise it throws. If `mL` < 28 or `mO` + `mL` > `EOF`, `fork.map-length` is reported and the
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
writer does not compress: a compressed resource's stored bytes ([compressed-resources.md §1](compressed-resources.md#1-compressed-resources)) are written as they are.

---

## 12. Mac OS 9 and 68k ROM differences

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
| Header version not 8 or 9 | read as version 9 | `CantDecompress` | [compressed-resources.md §1.1](compressed-resources.md#11-the-extended-header) |
| Extended, uncompressed | header kept, last 12 bytes lost | first 12 bytes stripped | [compressed-resources.md §1.2](compressed-resources.md#12-when-data-is-decompressed) |
| GetResourceSizeOnDisk (compressed) | decompressed size + expansion | decompressed size | [compressed-resources.md §1.2](compressed-resources.md#12-when-data-is-decompressed) |
| Working buffer | n + 2, arithmetic shift | n + 4, logical shift | [compressed-resources.md §1.4](compressed-resources.md#14-the-working-buffer-version-8) |
| `'dcmp'` search | whole chain from the current map; password bit ignored | own file downward; password-bit maps only | [compressed-resources.md §1.5](compressed-resources.md#15-finding-the-decompressor) |
| Missing `'dcmp'` | the compressed input in a decompressed-size handle, no error | nil, `CantDecompress` | [compressed-resources.md §1.6](compressed-resources.md#16-failures) |

In ClassicMac the model changes the open verdict (`fork.mac-rejects`, `fork.mac-misreads`), `fork.mac-hangs` (Mac OS 9
only), and decompression ([compressed-resources.md §1](compressed-resources.md#1-compressed-resources)). The decompressors themselves are the same 68k code under both.

---

## 13. Diagnostics

Fork diagnostics are reported by `ResourceFork.Read` (with the fork offset of the problem, where there is one);
decompression diagnostics by `ResourceDecompression.GetData`, their message prefixed with the resource (`'TYPE' id`).
`fork.unreadable` is reported by `ClassicMac.Files` (`MacFileResources`) and the app when `ResourceFork.Read` throws.

| Code | Severity | Meaning | What the Mac does |
| --- | --- | --- | --- |
| `fork.mac-rejects` | Warning | The modelled Resource Manager would refuse to open the fork; the message names the error and the failed check. The fork is read anyway | OpenResFile fails with `eofErr`, `posErr`, `paramErr` or `mapReadErr` (§8) |
| `fork.mac-misreads` | Warning | (68k ROM model) the ROM would open the fork but read memory past its map | opens; later calls see garbage (§8.2) |
| `fork.mac-hangs` | Warning | (Mac OS 9 model) a type's count − 1 is `$7FFF` or more | at `$FFFF` the fork opens and Preload loops forever; other such counts fail check 11 (§8.1) |
| `fork.map-length` | Warning | `mL` is under 28 or runs past the fork; the map is taken to run to the end | Mac OS 9: `mapReadErr`; 68k ROM: `eofErr`, or a misread map |
| `fork.map-recovered` | Warning | the map offset lies outside the fork and a map was found right after the data area; that one is used [ClassicMac] | Mac OS 9: `mapReadErr`; 68k ROM: `eofErr` |
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

---

## 14. Not covered

- The Ind-call size cache for duplicate IDs (§8.4) was read in code only.
- The 68k ROM's behaviour was traced in code only; it was never run (SheepShaver runs the native Resource Manager).
- The trap patches that extensions install on the Resource Manager (Multiple Users, Apple Menu Options, language
  packs, the Process Manager): they change what running applications see, not what a file contains.
