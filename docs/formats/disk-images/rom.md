# Macintosh ROM images — an implementer's specification

This document describes the resources built into a Macintosh ROM and how to list them from a ROM image: the table the
ROM keeps, the entries, where each resource's data and size are, and which entries a given machine uses. It also
describes the "Mac OS ROM" file of NewWorld machines, which carries a compressed ROM
image. It is complete enough to write a reader without reading ClassicMac's code. The code is in
`ClassicMac.Files.Rom` (`RomResourceTable`, `MacRomReader`, `NewWorldRomReader`) and `ClassicMac.Files.Compression`
(`Lzss`).

ClassicMac reads a ROM image as a container: it yields one file with no data fork whose **resource fork is
synthesised** from the ROM's resources, so listing, extracting and viewing work as for any other file.

References:

- *Inside Macintosh* Volume IV, the Resource Manager chapter (ROM resources, from the 128K ROM on) and Volume V.
- *Inside Macintosh: Memory*, the Memory Manager chapter (the 32-bit block header).
- Haruhiko Okumura, `lzss.c` (1989), the LZSS coder the "Mac OS ROM" file uses ([Author] in §6).
- Shared conventions and source tags: [README.md](../README.md). Resource forks and attributes:
  [resource-fork.md](../resources/resource-fork.md). How the unwrapper chooses readers: [unwrapping.md §3](../containers/unwrapping.md#3-unwrapping-nested-containers).

Contents

1. [Conventions](#1-conventions)
2. [The ROM image](#2-the-rom-image)
3. [The resource table](#3-the-resource-table)
4. [Combinations: which entries a machine uses](#4-combinations-which-entries-a-machine-uses)
5. [What ClassicMac yields](#5-what-classicmac-yields)
6. [The NewWorld "Mac OS ROM" file](#6-the-newworld-mac-os-rom-file)
7. [Diagnostics](#7-diagnostics)
8. [Not covered](#8-not-covered)

---

## 1. Conventions

The conventions of [README.md](../README.md) hold. In this document:

- **[Code]** is the 68k code of the ROM with version `$077D` (the PowerPC ROM, as the "Mac OS ROM" file of Mac OS
  9.x expands it), traced in disassembly. The layout was also checked against that image's own data: every entry
  walks, and the sizes come out exactly (§3.3).
- "ROMBase" is the address the ROM is mapped at; every offset in the table is from ROMBase, which is offset 0 of the
  image.
- Older ROMs (the 128K ROM of the Mac Plus through the 68040 machines) also carry ROM resources [Doc: IM IV], but
  whether their table has this layout is unverified. A reader that follows §3's checks simply does not
  recognise an image whose table is not plausible.

## 2. The ROM image

A ROM image is the ROM's bytes, with no header. ClassicMac recognises images of 64 KiB to 4 MiB whose length is a power
of two [ClassicMac].

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$08` | 2 | u16 | ROM version (`$077D` in the image checked) |
| `+$1A` | 4 | u32 | offset of the resource table header [Code] |

## 3. The resource table

### 3.1 Header

At the offset ROMBase+`$1A` gives [Code]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+0` | 4 | u32 | offset of the first entry |
| `+4` | 1 | u8 | highest combination index (4 on `$077D`) |
| `+5` | 1 | u8 | length of each entry's combination field in bytes (8 on `$077D`) |
| `+6` | 2 | u16 | version of the combination field: 1 |
| `+8` | 2 | u16 | length of the Memory Manager block header in front of each resource's data: 12 |

The widths are as the `$077D` image stores them: `+6` holds `$0001` and `+8` holds `$000C`.

### 3.2 Entries

The entries form a linked list; an entry's "next" offset of 0 ends it. Each entry [Code]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+0` | *n* | bytes | combination field (*n* from the header's `+5`; §4) |
| `+n` | 4 | u32 | offset of the next entry, 0 for the last |
| `+n+4` | 4 | u32 | offset of the resource's data |
| `+n+8` | 4 | `OSType` | resource type |
| `+n+12` | 2 | i16 | resource ID |
| `+n+14` | 1 | u8 | attributes |
| `+n+15` | 1+ | Str255 | name (length 0: no name) |

The attribute byte is not used by the Resource Manager for ROM resources; on `$077D` every entry has `$58`
(system heap, locked, protected) [Code]. ClassicMac keeps it as read.

On `$077D` the list starts at the highest entry and runs down through the image; each entry sits just above its data.
Readers should not rely on the order of addresses.

### 3.3 Data and size

The data offset points at the resource's first byte. In front of it is a 32-bit Memory Manager block header, 12 bytes
[Doc: IM Memory]:

| Offset from data | Size | Type | Meaning |
| --- | --- | --- | --- |
| `−12` | 1 | u8 | tag byte (`$C0` on `$077D`) |
| `−11` | 1 | u8 | master-pointer flags (`$A0` on `$077D`) |
| `−10` | 1 | u8 | reserved |
| `−9` | 1 | u8 | size correction |
| `−8` | 4 | u32 | physical size of the block, header included |
| `−4` | 4 | u32 | relative handle |

The resource's size is **physical size − 12 − size correction** [Code]. Resources are stored uncompressed. On `$077D`
the size correction is 0 in every header; the 51 PEF containers (`'nlib'`, `'ndrv'`, `'ncod'` …) all end within the
size this gives, 45 of them exactly, the other 6 padded to a multiple of 16 bytes.

### 3.4 Recognising a table

A reader can accept a table when [ClassicMac]:

- the pointer at `$1A` and the header lie inside the image;
- the version is 1, the field is 1–8 bytes, the highest index is 1 or more and below 8 × the field length, the block
  header length is 12 and the first-entry offset is not 0;
- the list ends inside the image, with at most one entry per 24 bytes of image (so a loop ends the walk), every entry
  inside the image and every type made of bytes `$20` or above, other than `$7F`.

An entry whose data offset or size falls outside the image is kept with no data and reported (`rom.bad-entry`).

## 4. Combinations: which entries a machine uses

The ROM serves several machine configurations ("combinations"), numbered 1 to the header's highest index. At startup
the machine's combination index selects the entries it uses: an entry is included when the bit for that index is set
in its combination field [Code]. Bit *i* is counted from the most significant bit of the first byte: bit *i* is
`(field[i / 8] >> (7 − i % 8)) & 1`, so for an 8-byte field it is bit 63 − *i* of the big-endian u64.

On `$077D`, 140 of the 142 entries have `$7800000000000000` (combinations 1–4); `'PACK'` 4 and `'PACK'` 5 have
`$0800000000000000` (combination 4 only).

Nothing in the format stops a type and ID appearing more than once, for combinations that do not overlap: the
Resource Manager takes only the entries of its own combination [Code], so it never sees two at once. (`$077D` has no
such pair.)

## 5. What ClassicMac yields

ClassicMac lists **every** entry, whatever its combinations [ClassicMac]:

- One file, named after the input (its host or container name), or `ROM $vvvv` from the version word when it has none.
  Its data fork is empty, so it is not unwrapped again; its resource fork is written from the entries
  (`ResourceFork`), in list order, with each entry's type, ID, name, attribute byte and data.
- An entry not in every combination 1 to the highest index is reported as `rom.combinations` (Info), with its
  combination numbers and the field in hex. `RomResourceTable` gives each entry's field (`ComboMask`,
  `IsInCombination`) to callers.
- When a type and ID appear again, the first entry in list order is kept and each later one is left out and reported
  as `rom.duplicate` (Warning), with both entries' combinations. (A resource fork holds one resource per type and ID;
  renaming the others would invent IDs the ROM never had.)

The unwrapper tries the ROM readers after the archives and before the disk images ([unwrapping.md §3.1](../containers/unwrapping.md#31-readers-and-order)): their
tests are strict, and some disk-image tests are weak enough that ROM code could pass them.

## 6. The NewWorld "Mac OS ROM" file

The "Mac OS ROM" file in the System Folder of NewWorld machines (type `'tbxi'`) is an Open Firmware
boot file. Its data fork starts with `<CHRP-BOOT>` and an XML-like header (`<COMPATIBLE>`, `<DESCRIPTION>`, `<ICON>`,
`<BOOT-SCRIPT>`), then holds an ELF image (the boot loader) and the ROM image compressed with LZSS. The Forth boot
script names the parts with constants [Code: the file's boot script]:

```
h# 004000 constant elf-offset
h# 00CDB0 constant elf-size
h# 010DB0 constant lzss-offset
h# 1CA2E2 constant lzss-size
```

ClassicMac reads `lzss-offset` and `lzss-size` from the script (in the first 64 KiB, before `</CHRP-BOOT>`), rather
than assuming fixed offsets, and recognises the file only when both are present and the compressed image lies inside
the file [ClassicMac].

The compression is Okumura's LZSS [Author], checked on a "Mac OS ROM" file whose image is `$077D`: it expands to
exactly the 4 MiB ROM image.

- A 4096-byte ring window, filled with spaces before decoding; writing starts at position 4078 (4096 − 18).
- A flag byte, read least significant bit first, governs the next eight items: bit 1 is a literal byte; bit 0 is a
  two-byte match `pppppppp PPPPLLLL`, window position `PPPP:pppppppp` (12 bits) and length `LLLL` + 3 (3–18). Each byte
  copied is also written to the window, so a match may overlap what it writes.
- The stream has no length; it ends with its input. A match cut short by the end is dropped.

The `$077D` file never reads a window byte before writing it, so the initial fill (spaces in Okumura's coder) does
not change its output.

ClassicMac yields one file named `ROM $vvvv` (from the expanded image's version word) whose data fork is the expanded
image; the ROM image reader (§5) then opens it. Expansion stops with an error past `MaxExpandedBytesPerInput`.

## 7. Diagnostics

| Code | Severity | Meaning | ClassicMac |
| --- | --- | --- | --- |
| `rom.combinations` | Info | an entry is not in every combination | lists it; the message gives its combinations and field |
| `rom.duplicate` | Warning | a type and ID listed again (for other combinations) | keeps the first entry in list order |
| `rom.bad-entry` | Error | an entry's data offset or block-header size falls outside the image | lists the resource with no data |

An image whose table is not plausible (§3.4) is not recognised and stays a plain file.

## 8. Not covered

- ROMs whose table does not follow §3 (possibly the 68k ROMs before the PowerPC ROM; unverified): not
  recognised.
- 24-bit Memory Manager block headers (header length 8): not recognised.
- The later "Mac OS ROM" files that carry "parcels" (`parcels-offset` instead of `lzss-offset` in the boot script):
  not recognised.
- The ROM's code, its ELF boot loader and other non-resource contents: not extracted, except the expanded image as the
  intermediate file.
- Writing ROM images.
