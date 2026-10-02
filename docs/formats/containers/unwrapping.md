# Containers: shared structures and unwrapping

This document describes the wrappers that carry one Macintosh file (name, Finder information, dates, data fork and
resource fork) through systems that know only flat byte streams: MacBinary I, II and III, BinHex 4.0, AppleSingle and
AppleDouble; and uuencode, which carries a file's bytes alone (often one of the others). It is complete enough to write a reader for each of them, and the AppleDouble writer, without reading
ClassicMac's code. It also describes how ClassicMac unwraps containers nested in one another (a MacBinary file inside a
BinHex file inside an AppleSingle file). The code is in `ClassicMac.Files.Containers` (the readers and the writer) and
`ClassicMac.Files` (`ContainerUnwrapper`, `ContainerReadOptions`).

None of these formats is read by Mac OS itself. MacBinary and BinHex were defined by their authors for modem
transfers and have no Apple specification. On a Mac OS 9.0 disk no Apple code handles MacBinary, and the only Apple
BinHex code is an encoder in the Web Sharing Extension ([binhex.md §7](binhex.md#7-apples-encoder)). AppleSingle and AppleDouble were defined by
Apple for A/UX and foreign file systems, but no Apple code in Mac OS 7.1–9 handles them ([applesingle-appledouble.md](applesingle-appledouble.md)). So the authors'
and Apple's published descriptions are the whole reference, and nothing here could be checked in an emulator.

References:

- Apple Computer, *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note, versions 1 and 2; version 2 is
  also the basis of RFC 1740 (MIME encapsulation of Macintosh files).
- The MacBinary I specification (1985), the MacBinary II specification (1987) and the MacBinary III specification
  (1996).
- Yves Lempereur's BinHex 4.0 format, as described by its author and in Peter N. Lewis's *BinHex 4.0 Definition*
  (1991); RFC 1741 (MIME content type for BinHex encoded files).
- The Open Group, *The Single UNIX Specification* (POSIX.1-2017), `uuencode` (the [Doc] source of [uuencode.md](uuencode.md)).
- *Inside Macintosh: Macintosh Toolbox Essentials*, the Finder Interface chapter (`FInfo`, `FXInfo`, Finder flags).
- Shared conventions and source tags: [README.md](../README.md). Host-folder layouts that use AppleDouble (`._` files,
  `name.rsrc`) and what `unpack` writes: [host-folders.md](host-folders.md).

Contents

1. [Conventions](#1-conventions)
2. [Shared structures](#2-shared-structures)
3. [Unwrapping nested containers](#3-unwrapping-nested-containers)
4. [Diagnostics](#4-diagnostics)
5. [Not covered](#5-not-covered)

---

## 1. Conventions

The conventions of [README.md](../README.md) hold: big-endian values, `u8`/`u16`/`u32` and `i16`/`i32`, `OSType` codes in
quotes, Mac dates as local-time seconds since 1904, structures as offset/size/type/meaning tables, and the source tags
**[Doc]**, **[Code]**, **[Verified]**, **[Author]** and **[Fitted]**. In this document:

- **[Doc]** is the *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note (version 1 or 2, as stated) or
  *Inside Macintosh*; in [uuencode.md](uuencode.md) (uuencode) it is the Single UNIX Specification. **[Author]** is one of the
  MacBinary specifications or the BinHex 4.0 description.
- **[Code]** appears only for the findings that Mac OS does not handle these formats, and for Apple's one BinHex
  encoder ([binhex.md §7](binhex.md#7-apples-encoder)). **[Verified]** does not appear: there is no Apple reader to check against.
- A tag at the end of a table row or sentence covers that row or sentence.
- Sentences that begin "ClassicMac …" describe the reader's own choices where the sources leave room: limits, how
  damage is recovered from, diagnostic severities. They are not claims about the format and carry no tag.
- MacBinary offsets are given in hex with the decimal offset the specifications use in parentheses: `+$53` (83).
- "The reader" means a reader that follows this document; "ClassicMac" means ClassicMac's implementation of it.
- A **fork** is a byte sequence of known length. A missing fork is the same as an empty one.

---

## 2. Shared structures

### 2.1 Finder information

All three families carry parts of the Finder's `FInfo` record, and AppleSingle and MacBinary III parts of `FXInfo`.
ClassicMac keeps both as one 32-byte block, `FInfo` then `FXInfo`, the way AppleSingle stores them. [Doc]

`FInfo` (16 bytes):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `OSType` | `fdType`: file type [Doc] |
| +$04 | 4 | `OSType` | `fdCreator`: creator [Doc] |
| +$08 | 2 | `u16` | `fdFlags`: Finder flags (below) [Doc] |
| +$0A | 4 | `Point` | `fdLocation`: the icon's position in its window, v then h [Doc] |
| +$0E | 2 | `i16` | `fdFldr`: the window (folder) the icon is in [Doc] |

`FXInfo` (16 bytes):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `i16` | `fdIconID` [Doc] |
| +$02 | 6 | `i16`[3] | reserved [Doc] |
| +$08 | 1 | `i8` | `fdScript`: script code of the name [Doc] |
| +$09 | 1 | `i8` | `fdXFlags`: extended flags [Doc] |
| +$0A | 2 | `i16` | `fdComment`: Finder comment ID [Doc] |
| +$0C | 4 | `i32` | `fdPutAway`: home directory ID [Doc] |

ClassicMac keeps `FXInfo` as 16 uninterpreted bytes, so containers round-trip it.

Finder flag bits (`fdFlags`):

| Bit | Mask | Meaning |
| --- | --- | --- |
| 0 | `$0001` | `isOnDesk`: on the desktop (System 6 and earlier) [Doc] |
| 1–3 | `$000E` | colour label [Doc] |
| 6 | `$0040` | `isShared`: an application that can run more than once from a shared volume [Doc] |
| 7 | `$0080` | `hasNoINITs`: no `INIT` resources [Doc] |
| 8 | `$0100` | `hasBeenInited`: the Finder has recorded the bundle [Doc] |
| 10 | `$0400` | `hasCustomIcon` (resource ID −16455) [Doc] |
| 11 | `$0800` | `isStationery` [Doc] |
| 12 | `$1000` | `nameLocked` [Doc] |
| 13 | `$2000` | `hasBundle`: the file has a `BNDL` resource [Doc] |
| 14 | `$4000` | `isInvisible` [Doc] |
| 15 | `$8000` | `isAlias` [Doc] |

### 2.2 Dates

MacBinary and AppleSingle version 1 store Mac dates: `u32` seconds since 1 January 1904 in the writing Mac's local
time, covering 1904 to 6 February 2040 [Doc]. AppleSingle version 2 stores signed seconds from 1 January 2000,
00:00 UTC ([applesingle-appledouble.md §4](applesingle-appledouble.md#4-entry-formats)) [Doc]. BinHex stores no dates [Author].

ClassicMac treats a MacBinary date of 0 as "not recorded".

### 2.3 CRC-16

MacBinary II/III and BinHex 4.0 use the same 16-bit CRC: polynomial `$1021`, initial value 0, bits processed most
significant first, no reflection and no final XOR [Author]. This is the CRC of the XMODEM protocol (the "CCITT" CRC
of the specifications); the check value over the nine ASCII bytes `123456789` is `$31C3`. A table-driven form, per
byte `b`:

```
crc = (crc << 8) XOR table[((crc >> 8) XOR b) AND $FF]      (16-bit crc)
table[i] = i << 8, then 8 times: if the top bit is set, shift left and XOR $1021, else shift left
```

Some descriptions compute the CRC bit by bit and then feed two zero bytes after the data; that "augmented" form gives
the same value as the direct form above. [Author] The CRC of zero bytes is 0.

---

## 3. Unwrapping nested containers

`ContainerUnwrapper` turns an input into a tree: each node is a file and the name of the format it was found in, and
its children are the files its data fork holds when that data fork is itself a container. The leaves are the files
that are not containers. For example, an AppleSingle file whose data fork is a BinHex file whose data is a MacBinary II
file unwraps to four levels: the host file, `AppleSingle`, `BinHex 4.0`, `MacBinary II`.

### 3.1 Readers and order

For each file, the unwrapper asks every reader in turn whether the file is in its format, and uses the first that says
yes. The order:

1. readers the application registers, first so they can override the built-in ones;
2. AppleSingle, AppleDouble;
3. MacBinary III, MacBinary II, MacBinary I;
4. BinHex 4.0, uuencode;
5. archives: DiskDoubler and its split files, PackIt, StuffIt split files and archives, Compact Pro, LHA, zip, gzip, tar (see
   [archives/](../README.md#archives));
6. NewWorld "Mac OS ROM" files and Mac ROM images (see [rom.md](../disk-images/rom.md));
7. UDIF, Apple partition maps, Disk Copy 4.2, NDIF, DART, HFS, MFS, DOS partition tables (MBR), FAT, raw CD images, cue
   sheets, ISO 9660 / High Sierra (see [disk-images/](../README.md#disk-images) and
   [file-systems/](../README.md#file-systems)).

The single-file containers come first because their tests are cheap and read only the start of the data fork, and
because AppleSingle and MacBinary II/III have strong signatures. MacBinary I, the weakest test, comes after the
stronger MacBinary versions. Most tests look at the data fork only; a reader may also look at the resource fork or the
Finder info (NDIF images need both).

- A file with an empty data fork is a leaf; no reader is asked.
- If the chosen reader then fails (the input turns out to be unusable), the file becomes a leaf and the failure is
  reported as `container.unreadable` (Error). The remaining readers are not tried.
- Readers report damage as diagnostics and keep going; only an input they cannot read at all fails.

### 3.2 Nesting depth

The file the unwrapper starts with is at depth 0, the files a container holds are one deeper than the container. A
container found at depth `MaxNestingDepth` or deeper is not opened: it becomes a leaf with `container.too-deep`
(Warning). So at most `MaxNestingDepth` containers are opened along any path; the default is 8. A BinHex file holding
a disk image needs a depth of at least 2 for the disk's files to appear.

### 3.3 Expanded-bytes limit

The unwrapper adds up the data and resource fork lengths of every file any container yields during one unwrap, across
the whole tree. When the total passes `MaxExpandedBytesPerInput` (default 1 GiB), the file that passed it and the rest
of that container's files are dropped, with `container.too-large` (Error). The count is not reset, so each container
still to be read reports the same error for its first file. This guards against inputs that expand without bound, such
as nested disk images or a BinHex file of long runs.

The same limit caps what a single reader holds in memory: BinHex reads its whole input and expands it under this limit
([binhex.md §6](binhex.md#6-reading)).

### 3.4 Names and sibling files

Each reader gets, besides the data:

- **the host name**: the name of the file being read (the host file's name at the top, the container's own name
  inside). AppleSingle and AppleDouble use it when the container has no Real Name entry. MacBinary and BinHex always
  carry their own name.
- **the sibling files**: the other files in the same folder as the input, read on demand, for formats split across
  files (segmented disk images). At the top these are the other files in the host folder; inside a container, the
  other files the same container yielded in the same folder. The single-file containers do not use them.
- **the options** (`ContainerReadOptions`): nesting depth (8), expanded bytes (1 GiB), the time zone for UTC dates
  (the machine's), and settings for volumes that do not concern these containers.

When unwrapping starts from a host path, the host-folder layer first joins the file with its companions (an
AppleDouble `._` file, Basilisk II `.rsrc`/`.finf` folders, PC Exchange records), and the root node's format is the
host layout's name (`AppleDouble pair`, `Basilisk II folder`, …).

---

## 4. Diagnostics

Codes these readers and the unwrapper emit. Offsets, where given, are in the container's input. None of these formats
is handled by Mac OS ([applesingle-appledouble.md](applesingle-appledouble.md)), so the "original tools" column describes the format authors' intent where it is
stated; the behaviour of MacBinary, BinHex and StuffIt programs was not traced.

| Code | Severity | Meaning | ClassicMac | Original tools |
| --- | --- | --- | --- | --- |
| `container.unreadable` | Error | a reader matched the file but could not read it | the file becomes a leaf | — |
| `container.too-deep` | Warning | a container nested past `MaxNestingDepth` | the file becomes a leaf | — |
| `container.too-large` | Error | unwrapping produced more than `MaxExpandedBytesPerInput` bytes | drops the rest of that container | — |

Inputs a reader cannot read at all raise `container.unreadable` in the unwrapper. For these containers they are: a
MacBinary header that does not pass its version's test; no BinHex marker and data; a BinHex input or expansion larger
than `MaxExpandedBytesPerInput`; a BinHex header with no valid name or too short to hold its fields; an AppleSingle or
AppleDouble header with the wrong magic number or version; a uuencode input with no `begin` line or larger than
`MaxExpandedBytesPerInput`.

---

## 5. Not covered

- MIME encapsulation (`application/applefile`, `multipart/appledouble`, `application/mac-binhex40`): the reader
  expects the container's bytes, not a mail message's MIME parts (a BinHex file in a mail body is found by its marker
  line).
