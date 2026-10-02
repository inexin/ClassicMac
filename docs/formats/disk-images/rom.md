# Macintosh ROM images

A Macintosh ROM carries resources of its own, which the Resource Manager adds to the System's map: a table in the ROM
lists them, each with the machine configurations ("combinations") it serves. A ROM image is the ROM's bytes with no
header; NewWorld machines instead load the ROM from the "Mac OS ROM" file in the System Folder, which holds it
compressed with LZSS. ClassicMac reads a ROM image as one file with no data fork whose resource fork is synthesised
from the ROM's resources, so listing, extracting and viewing work as for any other file; it expands a "Mac OS ROM"
file to the image first.

| | |
| --- | --- |
| Identified by | ROM image: a power-of-two length, the table pointer at `+$1A` and a plausible table. "Mac OS ROM": type `'tbxi'`, data fork starting `<CHRP-BOOT>` |
| ClassicMac | Reads; `ClassicMac.Files.Rom` (`MacRomReader`, `RomResourceTable`, `NewWorldRomReader`) and `ClassicMac.Files.Compression.Lzss` |
| Verified against | The ROM with version `$077D` (the PowerPC ROM, as Mac OS 9.x's "Mac OS ROM" file expands it): every entry walks and the sizes come out exactly |
| Sources | *Inside Macintosh* IV (ROM resources), *Inside Macintosh: Memory* (block headers); the 68k code of ROM `$077D` (disassembly); Okumura's `lzss.c` |

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

---

## 1. Layout

[Code] in this document is the 68k code of the ROM with version `$077D`, traced in disassembly. "ROMBase" is the
address the ROM is mapped at; every offset in the table is from ROMBase, which is offset 0 of the image.

### 1.1 The ROM image

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$08` | 2 | ROM version | `u16` (`$077D` in the image checked) |
| `+$1A` | 4 | Table pointer | `u32`: offset of the resource table header [Code] |

### 1.2 The resource table header

At the offset `+$1A` gives [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+0` | 4 | First entry | `u32`: offset of the first entry |
| `+4` | 1 | Highest combination | `u8`: the highest combination index (4 on `$077D`) |
| `+5` | 1 | Field length | `u8`: length of each entry's combination field in bytes (8 on `$077D`) |
| `+6` | 2 | Version | `u16`: version of the combination field, 1 (`$0001` on `$077D`) |
| `+8` | 2 | Block header length | `u16`: length of the Memory Manager block header in front of each resource's data, 12 (`$000C` on `$077D`) |

### 1.3 Entries

The entries form a linked list; an entry's "next" offset of 0 ends it. Each entry [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+0` | *n* | Combination field | *n* from the header's `+5` ([§1.5](#15-combinations)) |
| `+n` | 4 | Next | `u32`: offset of the next entry, 0 for the last |
| `+n+4` | 4 | Data | `u32`: offset of the resource's data |
| `+n+8` | 4 | Type | `OSType` |
| `+n+12` | 2 | ID | `i16` |
| `+n+14` | 1 | Attributes | `u8`. Not used by the Resource Manager for ROM resources; `$58` (system heap, locked, protected) in every `$077D` entry [Code] |
| `+n+15` | 1+ | Name | `Str255`; length 0 = no name |

### 1.4 Data and size

The data offset points at the resource's first byte. In front of it is a 32-bit Memory Manager block header, 12 bytes
[Doc: IM Memory]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `−12` | 1 | Tag byte | `$C0` on `$077D` |
| `−11` | 1 | Master-pointer flags | `$A0` on `$077D` |
| `−10` | 1 | Reserved | |
| `−9` | 1 | Size correction | 0 in every `$077D` header |
| `−8` | 4 | Physical size | `u32`: the block's size, header included |
| `−4` | 4 | Relative handle | `u32` |

The resource's size is **physical size − 12 − size correction** [Code]. Resources are stored uncompressed.

### 1.5 Combinations

The ROM serves several machine configurations, numbered 1 to the header's highest index. At startup the machine's
combination index selects the entries it uses: an entry is included when the bit for that index is set in its
combination field [Code]. Bit *i* is counted from the most significant bit of the first byte:
`(field[i / 8] >> (7 − i % 8)) & 1`, so for an 8-byte field it is bit 63 − *i* of the big-endian `u64`.

Nothing in the format stops a type and ID appearing more than once, for combinations that do not overlap: the Resource
Manager takes only the entries of its own combination [Code], so it never sees two at once.

### 1.6 The NewWorld "Mac OS ROM" file

The file (type `'tbxi'`) is an Open Firmware boot file. Its data fork starts with `<CHRP-BOOT>` and an XML-like header
(`<COMPATIBLE>`, `<DESCRIPTION>`, `<ICON>`, `<BOOT-SCRIPT>`), then holds an ELF image (the boot loader) and the ROM
image compressed with LZSS ([§1.7](#17-lzss)). The Forth boot script names the parts with constants [Code: the file's
boot script]:

```
h# 004000 constant elf-offset
h# 00CDB0 constant elf-size
h# 010DB0 constant lzss-offset
h# 1CA2E2 constant lzss-size
```

### 1.7 LZSS

Okumura's LZSS [Author: `lzss.c`], checked on a "Mac OS ROM" file whose image is `$077D`: it expands to exactly the
4 MiB ROM image.

1. Fill a 4096-byte ring window with spaces; writing starts at position 4078 (4096 − 18).
2. Read a flag byte; its bits, least significant first, govern the next eight items.
3. Bit 1: copy one literal byte.
4. Bit 0: read a two-byte match `pppppppp PPPPLLLL`: window position `PPPP:pppppppp` (12 bits) and length `LLLL` + 3
   (3–18). Copy the bytes from the window one at a time, so a match may overlap what it writes.
5. Every byte output is also written to the window at the write position, which then advances.
6. The stream has no length: it ends with its input. A match cut short by the end is dropped.

The `$077D` file never reads a window byte before writing it, so the initial fill does not change its output.

## 2. Reading

1. Read the table pointer at `+$1A` and the header there ([§1.2](#12-the-resource-table-header)).
2. Walk the list from the first entry ([§1.3](#13-entries)) until a "next" of 0. On `$077D` the list starts at the
   highest entry and runs down through the image, each entry just above its data; a reader should not rely on the
   order of addresses.
3. For each entry, find the data and its size from the block header ([§1.4](#14-data-and-size)).
4. To see the resources one machine sees, keep the entries whose combination field has that machine's bit
   ([§1.5](#15-combinations)).

For a "Mac OS ROM" file, find `lzss-offset` and `lzss-size` in the boot script ([§1.6](#16-the-newworld-mac-os-rom-file)),
expand those bytes ([§1.7](#17-lzss)), and read the image as above.

## 3. Writing

None.

## 4. Variants

- **`$077D`** (the PowerPC ROM): 142 entries; 140 have the combination field `$7800000000000000` (combinations 1–4),
  and `'PACK'` 4 and `'PACK'` 5 have `$0800000000000000` (combination 4 only). No type and ID appears twice. Every size
  correction is 0, and the 51 PEF containers (`'nlib'`, `'ndrv'`, `'ncod'` …) all end within the size [§1.4](#14-data-and-size)
  gives, 45 of them exactly, the other 6 padded to a multiple of 16 bytes [Code], [Verified: the `$077D` image's data].
- **Older ROMs** (the 128K ROM of the Mac Plus through the 68040 machines) also carry ROM resources [Doc: IM IV], but
  whether their table has this layout is unverified.
- **Later "Mac OS ROM" files** carry "parcels" (`parcels-offset` instead of `lzss-offset` in the boot script).

## 5. ClassicMac

- **Recognising a ROM image** [ClassicMac]: a length of 64 KiB to 4 MiB that is a power of two, and a table that passes
  these checks (an image that fails stays a plain file):
  - the pointer at `$1A` and the 10-byte header lie inside the image;
  - the version is 1, the field is 1–8 bytes, the highest index is 1 or more and below 8 × the field length, the block
    header length is 12 and the first-entry offset is not 0;
  - the list ends inside the image, with at most one entry per (field length + 16) bytes of image (24 for an 8-byte
    field), so a loop ends the walk; every entry, its name included, lies inside the image, and every type is made of
    bytes `$20` or above, other than `$7F`.
- **Bad entries**: an entry whose data offset is below 12 or past the image, or whose block header gives a size past
  the end, is kept with no data and reported (`rom.bad-entry`). The size correction is subtracted as read.
- **Output**: one file, named after the input (its host or container name), or `ROM $vvvv` from the version word when
  it has none. Its data fork is empty, so it is not unwrapped again; its resource fork is written from the entries, in
  list order, with each entry's type, ID, name, attribute byte (kept as read) and data.
- **Every entry is listed**, whatever its combinations. An entry not in every combination 1 to the highest index is
  reported as `rom.combinations` (Info), with its combination numbers and the field in hex. `RomResourceTable` gives
  each entry's field (`ComboMask`, `IsInCombination`) to callers.
- **Duplicates**: when a type and ID appear again, the first entry in list order is kept and each later one is left out
  and reported as `rom.duplicate` (Warning), with both entries' combinations. A resource fork holds one resource per
  type and ID; renaming the others would invent IDs the ROM never had.
- **Order**: the ROM readers are tried after the archives and before the disk images
  ([unwrapping.md §3.1](../containers/unwrapping.md#31-readers-and-order)): their tests are strict, and some disk-image
  tests are weak enough that ROM code could pass them.
- **"Mac OS ROM" files** [ClassicMac]: recognised only when the data fork starts with `<CHRP-BOOT>` and the script (in
  the first 64 KiB, before `</CHRP-BOOT>`) has both `h# <1–8 hex digits> constant lzss-offset` and
  `… lzss-size` with the compressed image lying inside the file; fixed offsets are not assumed. The output is one file
  named `ROM $vvvv` (from the expanded image's version word; "ROM image" for an image under 10 bytes) whose data fork
  is the expanded image, which the ROM image reader then opens. Expansion stops with an error past the expanded-bytes
  limit ([unwrapping.md §3.3](../containers/unwrapping.md#33-expanded-bytes-limit)).

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `rom.bad-entry` | Error | An entry's data offset or block-header size falls outside the image | Lists the resource with no data | Not traced |
| `rom.combinations` | Info | An entry is not in every combination | Lists it; the message gives its combinations and field | Uses it only on machines of those combinations |
| `rom.duplicate` | Warning | A type and ID listed again (for other combinations) | Keeps the first entry in list order | Sees only the entry of its own combination |

## 7. Verification

- `tests/ClassicMac.Files.Tests/RomTests.cs`, on ROM images built as §1 describes (version word, table pointer, table
  header, entries linked last first, 12-byte block headers): every entry listed, the file's name, the header and
  combination fields, duplicates, data outside the image, and the recognition checks
  (`Rejects_an_implausible_image`: length, pointer, version, field length, a loop, a bad type, an empty table).
- `Lzss_matches_copy_from_the_window_and_may_overlap`, `Lzss_stops_at_the_limit`: the LZSS rules of §1.7 and the
  expanded-bytes limit. `NewWorld_file_unwraps_to_the_image_and_its_resources`,
  `NewWorld_file_without_the_constants_or_out_of_range_is_not_recognised`: the boot-script rules.
- `Rom_077D_corpus`: with `CLASSICMAC_ROM_IMAGE` set to a `$077D` ROM image or a "Mac OS ROM" file (never committed),
  the image unwraps with no warnings to 142 resources; the highest combination is 4, `'PACK'` 4 and 5 are reported as
  combination 4 only, and the 51 PEF containers end within their sizes (45 exactly).

## 8. Not covered

- ROMs whose table does not follow §1 (possibly the 68k ROMs before the PowerPC ROM; unverified): not recognised.
- 24-bit Memory Manager block headers (header length 8): not recognised.
- "Mac OS ROM" files with parcels: not recognised.
- The ROM's code, its ELF boot loader and other non-resource contents: not extracted, except the expanded image as the
  intermediate file.
- Writing ROM images.

## 9. References

1. *Inside Macintosh* Volume IV, the Resource Manager chapter (ROM resources, from the 128K ROM on), and Volume V.
2. *Inside Macintosh: Memory*, the Memory Manager chapter (the 32-bit block header).
3. Haruhiko Okumura, `lzss.c` (1989), public domain: the LZSS coder the "Mac OS ROM" file uses.
4. Resource forks and attributes: [resource-fork.md](../resources/resource-fork.md).
