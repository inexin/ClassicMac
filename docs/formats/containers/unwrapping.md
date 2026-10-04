# Containers: shared structures and unwrapping

The single-file containers carry one Macintosh file (name, Finder information, dates, data fork and resource fork)
through systems that know only flat byte streams: [MacBinary](macbinary.md) I, II and III, [BinHex 4.0](binhex.md),
[AppleSingle and AppleDouble](applesingle-appledouble.md), and [uuencode](uuencode.md), which carries a file's bytes
alone (often one of the others). This file holds the structures they share (Finder information, dates, the CRC-16)
and how ClassicMac unwraps containers nested in one another (a MacBinary file inside a BinHex file inside an
AppleSingle file) into a tree of Mac files. Mac files kept on host file systems are in [host-folders.md](host-folders.md);
saving edited resources back into a container is in [writing.md](writing.md).

| | |
| --- | --- |
| Identified by | Each format by its own test, in the order of §2.1 |
| ClassicMac | Reads; `ClassicMac.Files` (`ContainerUnwrapper`, `ContainerReadOptions`, `IContainerReader`), the readers in `ClassicMac.Files.Containers` |
| Verified against | Nothing yet: no Apple code reads these containers, so there is nothing to check in an emulator |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials* and *Files* (Finder information); the MacBinary specifications and the BinHex 4.0 description (CRC-16); the AppleSingle/AppleDouble Developer Note (dates) |

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

In all of these formats a **fork** is a byte sequence of known length, and a missing fork is the same as an empty one.

### 1.1 Finder information

Every container and host layout that keeps Finder information keeps parts of the same 32 bytes: `FInfo` followed by
`FXInfo` for a file, `DInfo` followed by `DXInfo` for a folder [Doc: *Inside Macintosh: Files*]. AppleSingle stores
all 32 bytes as one entry [Doc]; MacBinary, BinHex and the host layouts store the parts their formats name.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `fdType` / `frRect` | file type (`OSType`); for a folder, `frRect` (a `Rect`, 8 bytes: +$00–$07) [Doc] |
| +$04 | 4 | `fdCreator` / (`frRect`) | creator (`OSType`); for a folder, the rest of `frRect` [Doc] |
| +$08 | 2 | `fdFlags` / `frFlags` | Finder flags, `u16` (below) [Doc] |
| +$0A | 4 | `fdLocation` / `frLocation` | the icon's position in its window, a `Point`: v then h [Doc] |
| +$0E | 2 | `fdFldr` / `frView` | `i16`: the window (folder) the icon is in; for a folder, its view [Doc] |
| +$10 | 2 | `fdIconID` / `frScroll` | `i16`; for a folder, `frScroll` (a `Point`, 4 bytes: +$10–$13) [Doc] |
| +$12 | 6 | `fdReserved` / `frOpenChain` | 3 × `i16`, reserved; for a folder, +$12 is the rest of `frScroll` and +$14 `frOpenChain` (`i32`) [Doc] |
| +$18 | 1 | `fdScript` / `frScript` | `i8`: the script code of the name [Doc] |
| +$19 | 1 | `fdXFlags` / `frXFlags` | `i8`: extended flags [Doc] |
| +$1A | 2 | `fdComment` / `frComment` | `i16`: Finder comment ID [Doc] |
| +$1C | 4 | `fdPutAway` / `frPutAway` | `i32`: home directory ID [Doc] |

Bytes +$00–$0F are `FInfo` (`DInfo`), +$10–$1F are `FXInfo` (`DXInfo`).

Finder flag bits (`fdFlags`) [Doc: *Macintosh Toolbox Essentials*, Finder Interface]:

| Bit | Mask | Meaning |
| --- | --- | --- |
| 0 | `$0001` | `isOnDesk`: on the desktop (System 6 and earlier) |
| 1–3 | `$000E` | colour label |
| 6 | `$0040` | `isShared`: an application that can run more than once from a shared volume |
| 7 | `$0080` | `hasNoINITs`: no `INIT` resources |
| 8 | `$0100` | `hasBeenInited`: the Finder has recorded the bundle |
| 10 | `$0400` | `hasCustomIcon` (resource ID −16455) |
| 11 | `$0800` | `isStationery` |
| 12 | `$1000` | `nameLocked` |
| 13 | `$2000` | `hasBundle`: the file has a `BNDL` resource |
| 14 | `$4000` | `isInvisible` |
| 15 | `$8000` | `isAlias` |

### 1.2 Dates

| Format | Field | Notes |
| --- | --- | --- |
| MacBinary, AppleSingle version 1 (`Macintosh` home file system) | `u32` | Mac dates: seconds since 1 January 1904 in the writing Mac's local time, covering 1904 to 6 February 2040 [Doc] [Author] |
| AppleSingle and AppleDouble version 2 | `i32` | signed seconds from 1 January 2000, 00:00 UTC; `$80000000` is unknown ([applesingle-appledouble.md §1.4](applesingle-appledouble.md#14-entry-formats)) [Doc] |
| BinHex 4.0, uuencode | none | no dates [Author] [Doc] |

### 1.3 CRC-16

MacBinary II/III and BinHex 4.0 use the same 16-bit CRC [Author]:

| Parameter | Value |
| --- | --- |
| Polynomial | `$1021` |
| Initial value | 0 |
| Bit order | most significant first, no reflection |
| Final XOR | none |
| Check value | `$31C3` over the nine ASCII bytes `123456789` |

This is the CRC of the XMODEM protocol, the "CCITT" CRC of the specifications. The CRC of zero bytes is 0. [Author]

A table-driven form, per byte `b`, with a 16-bit `crc`:

1. `crc = (crc << 8) XOR table[((crc >> 8) XOR b) AND $FF]`.
2. The table: `table[i]` starts as `i << 8`; then 8 times, if the top bit is set, shift left and XOR `$1021`, else
   shift left.

Some descriptions compute the CRC bit by bit and then feed two zero bytes after the data; that "augmented" form gives
the same value as the direct form above. [Author]

## 2. Reading

Unwrapping is ClassicMac's own design: Mac OS has no counterpart [ClassicMac]. The result is a tree: each node is a
file and the name of the format it was found in, and its children are the files its data fork holds when that data
fork is itself a container. The leaves are the files that are not containers. An AppleSingle file whose data fork is
a BinHex file whose data is a MacBinary II file unwraps to four levels: the host file, `AppleSingle`, `BinHex 4.0`,
`MacBinary II`.

### 2.1 Reader order

Every reader is asked in turn whether the file is in its format, and the first that says yes is used [ClassicMac]:

1. readers the application registers, first so they can override the built-in ones;
2. AppleSingle, AppleDouble;
3. MacBinary III, MacBinary II, MacBinary I;
4. BinHex 4.0, uuencode;
5. archives: DiskDoubler and its split files, PackIt, StuffIt split files, StuffIt, Compact Pro, LHA, zip, gzip, tar
   (see [archives](../README.md#archives));
6. NewWorld "Mac OS ROM" files and Mac ROM images ([rom.md](../disk-images/rom.md));
7. UDIF, Apple partition maps, Disk Copy 4.2, NDIF, DART, HFS, MFS, DOS partition tables (MBR), FAT, raw CD images,
   cue sheets, ISO 9660 / High Sierra (see [disk images](../README.md#disk-images) and
   [file systems](../README.md#file-systems)).

The single-file containers come first because their tests are cheap and read only the start of the data fork, and
because AppleSingle and MacBinary II/III have strong signatures. MacBinary I, the weakest test, comes after the
stronger MacBinary versions. BinHex and uuencode search the first 64 KiB for their text, so they come before the
formats with a signature at a fixed place (an HFS master directory block at 1024, `CD001` at 32769), which text could
match by chance; they accept only an input holding text before their marker, so a disk image or volume with a `.hqx`
or `.uu` file near its start falls through to its own reader ([binhex.md §5](binhex.md#5-classicmac)). Most tests look at the data fork only; a reader may also look at the resource fork or the
Finder info (NDIF images need both).

### 2.2 Unwrapping a file

For each file, starting with the input at depth 0 [ClassicMac]:

1. A file with an empty data fork is a leaf; no reader is asked.
2. Find the first reader that accepts the file (§2.1). If none does, the file is a leaf.
3. If the file's depth is at the nesting limit (§5) or deeper, it is not opened: it becomes a leaf, with
   `container.too-deep` (Warning). So at most that many containers are opened along any path. A BinHex file holding
   a disk image needs a limit of at least 2 for the disk's files to appear.
4. Read the container. If the reader finds the input unusable, the file becomes a leaf, with `container.unreadable`
   (Error); the remaining readers are not tried. Each format's file says when its input is unusable. Readers report
   damage as diagnostics and keep going; only an input they cannot read at all fails, by finding its structures wrong
   or its data shorter than they say (a truncated image).
5. For each file the container yielded, in order: add its data and resource fork lengths to a running total kept for
   the whole tree. When the total passes the expanded-bytes limit (§5), that file and the rest of this container's
   files are dropped, with `container.too-large` (Error). The total is not reset, so each container still to be read
   reports the same error for its first file. This guards against inputs that expand without bound, such as nested
   disk images or a BinHex file of long runs.
6. Otherwise unwrap the yielded file at depth + 1, from step 1.

### 2.3 What a reader is given

Besides the data, each reader gets [ClassicMac]:

- **The host name**: the name of the file being read (the host file's name at the top, the container's own name
  inside). AppleSingle and AppleDouble use it when the container has no Real Name entry. MacBinary and BinHex always
  carry their own name.
- **The sibling files**: the other files in the same folder as the input, read on demand, for formats split across
  files (segmented disk images). At the top these are the other files in the host folder; inside a container, the
  other files the same container yielded in the same folder. The single-file containers do not use them.
- **The options** (§5).

When unwrapping starts from a host path, the host-folder layer first joins the file with its companions (an
AppleDouble `._` file, Basilisk II `.rsrc`/`.finf` folders, PC Exchange records;
[host-folders.md §2](host-folders.md#2-reading)), and the root node's format is the host layout's name.

## 3. Writing

None.

## 4. Variants

Which shared structures each container carries:

| Format | Finder information | Dates | CRC-16 |
| --- | --- | --- | --- |
| MacBinary I | type, creator, flags high byte, location, folder | Mac dates | none |
| MacBinary II | as I, plus the flags low byte | Mac dates | header |
| MacBinary III | as II, plus `fdScript` and `fdXFlags` | Mac dates | header |
| BinHex 4.0 | type, creator, flags | none | header and each fork |
| AppleSingle, AppleDouble | all 32 bytes | version 1: Mac dates; version 2: UTC since 2000 | none |
| uuencode | none | none | none |

None of these formats is read by Mac OS itself. MacBinary and BinHex were defined by their authors for modem transfers
and have no Apple specification. On a Mac OS 9.0 disk no Apple code handles MacBinary, and the only Apple BinHex code
is an encoder in the Web Sharing Extension ([binhex.md §4.1](binhex.md#41-apples-encoder)) [Code: Mac OS 9.0 disk].
AppleSingle and AppleDouble were defined by Apple for A/UX and foreign file systems, but no Apple code in Mac OS
7.1–9 handles them ([applesingle-appledouble.md §4](applesingle-appledouble.md#4-variants)) [Code]. So the authors'
and Apple's published descriptions are the whole reference.

## 5. ClassicMac

- `ContainerUnwrapper` builds the tree of §2 as `ContainerNode` records (format name, Mac file, children);
  `ContainerUnwrapper.Default` has the built-in readers, and an application passes its own `IContainerReader`s to the
  constructor to have them tried first. `Unwrap(path)` reads a host file with its companions
  ([host-folders.md](host-folders.md)) and unwraps it. [ClassicMac]
- `Unwrap(file, format, context, levels)` and `Expand(node, context, levels)` read at most `levels` levels of
  containers; the containers below are leaves with `UnreadFormat` set, read later by `Expand`. A container holding one
  file (a wrapper, a disk image's disk) does not count as a level, so a wrapped volume is reached at level 1; a volume
  always counts, even with one file on it, so its files are never opened by a level 1 read. [ClassicMac]
- Files read side by side are probed side by side (a volume holds thousands); files left at the level limit are probed
  only when their `UnreadFormat` is first asked for (listing one folder probes that folder's files), and `Expand`
  probes those still unprobed side by side. A probe that fails is thrown in the files' order. Probes read little: each
  fork's head and tail once, shared by every reader; NDIF looks for `'bcem'` in the resource map's type list before
  parsing the fork, and Compact Pro checks its first byte before opening a stream. [ClassicMac]
- `ContainerReadOptions` holds every limit [ClassicMac]:

  | Option | Default | Use |
  | --- | --- | --- |
  | `MaxNestingDepth` | 8 | the nesting limit of §2.2 step 3 |
  | `MaxExpandedBytesPerInput` | 1 GiB | the expanded-bytes limit of §2.2 step 5; also caps what one reader holds in memory (BinHex, uuencode) |
  | `TimeZone` | the machine's | the zone of the Mac that reads the files, for UTC dates (AppleSingle and AppleDouble version 2) |
  | `MaxVolumeEntries`, `ExtensionMap`, `VerifyChecksums`, `ArchivePassword` | | settings for volumes and archives, not for these containers |

- The Finder information is kept as one 32-byte block (`FinderInfo`): `FInfo` interpreted (type, creator, flags,
  location, folder), `FXInfo` as 16 uninterpreted bytes, so containers round-trip it. A shorter record is padded with
  zeros, since some writers store only `FInfo`; bytes past 32 are ignored. [ClassicMac]
- The CRC-16 of §1.3 is one internal routine (`Crc16`) shared by the MacBinary and BinHex readers and writers.

## 6. Diagnostics

The single-file containers' own codes are in their files. Offsets, where given, are in the container's input.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `container.too-deep` | Warning | a container nested at or past the nesting limit | the file becomes a leaf | no Mac counterpart |
| `container.too-large` | Error | unwrapping produced more than the expanded-bytes limit | drops the rest of that container's files | no Mac counterpart |
| `container.unreadable` | Error | a reader accepted the file but cannot read it, its structures wrong or its data short (each format's §2 says when) | the file becomes a leaf; other readers are not tried | no Mac counterpart |
| `container.reader-fault` | Error | a reader failed on the file's data with an arithmetic or index exception (a ClassicMac bug, found by mutation testing) | the file becomes a leaf; the unwrap goes on | no Mac counterpart |

## 7. Verification

Synthetic inputs, built byte by byte by `tests/ClassicMac.Files.Tests/Fixtures.cs`:

- `tests/ClassicMac.Files.Tests/UnwrapTests.cs`: `Nested_containers_unwrap_to_a_tree` (AppleSingle → BinHex →
  MacBinary II, §2), `Plain_files_are_their_own_leaf`, `Nesting_stops_at_the_limit` (§2.2 step 3),
  `Expansion_stops_at_the_limit` (step 5), `Readers_find_sibling_files_on_the_host_and_in_containers` (§2.3).
  `Corpus_host_files_unwrap` reads every file of the real-file corpus named by `CLASSICMAC_CORPUS` (outside the repo)
  and requires no Error diagnostic; it is skipped without it.
- `tests/ClassicMac.Files.Tests/MacBinaryTests.cs`: `Crc16_is_XMODEM` (the check value of §1.3).
- `tests/ClassicMac.Files.Tests/AppleSingleTests.cs`: `Short_Finder_info_is_padded` (§5).

## 8. Not covered

- MIME encapsulation (`application/applefile`, `multipart/appledouble`, `application/mac-binhex40`): the readers
  expect the container's bytes, not a mail message's MIME parts (a BinHex file in a mail body is found by its marker
  line).

## 9. References

1. Apple Computer, *Inside Macintosh: Macintosh Toolbox Essentials*, the Finder Interface chapter (`FInfo`, `FXInfo`,
   Finder flags), and *Inside Macintosh: Files* (`DInfo`, `DXInfo`).
2. Apple Computer, *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note, versions 1 and 2; version 2 is
   also the basis of RFC 1740 (MIME encapsulation of Macintosh files).
3. The MacBinary I specification (1985), the MacBinary II specification (1987) and the MacBinary III specification
   (1996).
4. Yves Lempereur's BinHex 4.0 format, as described by its author and in Peter N. Lewis's *BinHex 4.0 Definition*
   (1991); RFC 1741 (MIME content type for BinHex encoded files).
5. The Open Group, *The Single UNIX Specification* (POSIX.1-2017), `uuencode`.
6. Shared conventions and source tags: [README.md](../README.md).
