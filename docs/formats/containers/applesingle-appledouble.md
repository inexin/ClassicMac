# AppleSingle and AppleDouble

AppleSingle stores a whole file (both forks and its metadata) in one file of entries. AppleDouble splits it: the data
fork goes into a plain *data file*, everything else into a *header file* in AppleSingle's layout with its own magic
number. [Doc] The two formats share every structure below.

No Apple code in Mac OS 7.1 to 9 reads or writes either format: File Exchange 3.0.2 (Mac OS 9.0), PC Exchange 1.0.4
and Foreign File Access contain neither the magic numbers nor code that builds these files, and on a Mac OS 9.0 disk
only mail clients (Netscape Communicator, Outlook Express 4.5, decoding `application/applefile` and
`multipart/appledouble` attachments) and the StuffIt Engine handle them. [Code] PC Exchange and File Exchange use
their own private format (see [pc-exchange.md](../file-systems/pc-exchange.md)).

Contents

1. [Header](#1-header)
2. [Entry table](#2-entry-table)
3. [Entry IDs](#3-entry-ids)
4. [Entry formats](#4-entry-formats)
5. [Versions 1 and 2](#5-versions-1-and-2)
6. [Reading](#6-reading)
7. [Writing AppleDouble](#7-writing-appledouble)
8. [Writing AppleSingle](#8-writing-applesingle)
9. [Diagnostics](#9-diagnostics)
10. [Not covered](#10-not-covered)

---

## 1. Header

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `u32` | magic number: `$00051600` AppleSingle, `$00051607` AppleDouble header file [Doc] |
| +$04 | 4 | `u32` | version: `$00010000` (version 1) or `$00020000` (version 2) [Doc] |
| +$08 | 16 | bytes | version 1: the home file system's name, ASCII, padded with spaces; version 2: filler, all zero [Doc] |
| +$18 | 2 | `u16` | number of entries, may be 0 [Doc] |
| +$1A | 12 × count | | the entry table (§2) [Doc] |

The version 1 home file system names are `Macintosh`, `ProDOS`, `MS-DOS`, `Unix` and `VAX VMS`. [Doc]

A reader recognises a file by the magic number and a version of 1 or 2. ClassicMac requires both and the full 26-byte
header; it rejects other versions (`$00030000`, for instance) rather than guess at them.

---

## 2. Entry table

Each entry descriptor is 12 bytes [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `u32` | entry ID (§3) [Doc] |
| +$04 | 4 | `u32` | offset of the entry's data from the start of the file [Doc] |
| +$08 | 4 | `u32` | length of the entry's data; may be 0 [Doc] |

Entries may be stored in any order, and their data need not follow the table in the same order. [Doc] Readers skip
entries whose IDs they do not know. [Doc]

ClassicMac:

- reports a table cut off by the end of the file as `applesingle.entries-truncated` (Error) and reads the complete
  descriptors;
- skips an entry whose offset plus length runs past the end of the file, reporting `applesingle.entry-out-of-range`
  (Error);
- lets a later entry with the same ID replace an earlier one;
- slices forks out of the input without copying.

---

## 3. Entry IDs

| ID | Name | Versions | ClassicMac |
| --- | --- | --- | --- |
| 1 | Data Fork | 1, 2 [Doc] | data fork |
| 2 | Resource Fork | 1, 2 [Doc] | resource fork |
| 3 | Real Name | 1, 2 [Doc] | name |
| 4 | Comment | 1, 2 [Doc] | ignored |
| 5 | Icon, black and white | 1, 2 [Doc] | ignored |
| 6 | Icon, colour | 1, 2 [Doc] | ignored |
| 7 | File Info | 1 only [Doc] | creation and modification dates when the home file system is `Macintosh`; else ignored |
| 8 | File Dates Info | 2 [Doc] | creation and modification dates |
| 9 | Finder Info | 1, 2 [Doc] | Finder info |
| 10 | Macintosh File Info | 2 [Doc] | ignored |
| 11 | ProDOS File Info | 2 [Doc] | ignored |
| 12 | MS-DOS File Info | 2 [Doc] | ignored |
| 13 | AFP Short Name | 2 [Doc] | ignored |
| 14 | AFP File Info | 2 [Doc] | ignored |
| 15 | AFP Directory ID | 2 [Doc] | ignored |

ClassicMac reports any other ID as `applesingle.unknown-entry` (Info) and skips it. It does not check which version
defines an ID: entry 8 is read in a version 1 file too (with the version 2 layout), and entry 7 is read only in
version 1 files whose home file system is exactly `Macintosh`.

---

## 4. Entry formats

**Data Fork (1), Resource Fork (2).** The fork's bytes. [Doc] An AppleDouble header file holds no data fork: the data
file is the data fork. [Doc] ClassicMac reads a data fork found in an AppleDouble header anyway, and reports
`applesingle.double-data-fork` (Warning).

**Real Name (3).** The file's name on its home file system, as bytes with no length byte or terminator. [Doc]
ClassicMac keeps the bytes as a Mac name (Mac OS Roman), cut to 255 bytes with `applesingle.name-too-long` (Warning).
Without a Real Name entry, ClassicMac uses the name the input had on the host or in its outer container; with neither,
it reports `applesingle.no-name` (Info) and the name is empty.

**Comment (4), Icons (5, 6).** The Finder comment and icon images. [Doc] Not read by ClassicMac.

**File Info (7), version 1.** Its layout depends on the home file system. [Doc] For `Macintosh` [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `u32` | creation date, Mac date (local, since 1904) [Doc] |
| +$04 | 4 | `u32` | modification date, Mac date [Doc] |
| +$08 | 4 | `u32` | backup date, Mac date [Doc] |
| +$0C | 4 | `u32` | attributes: bit 0 locked, bit 1 protected [Doc] |

ClassicMac reads the first two dates as they are. Fewer than 8 bytes: `applesingle.dates-truncated` (Warning), no
dates.

**File Dates Info (8), version 2** [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `i32` | creation date [Doc] |
| +$04 | 4 | `i32` | modification date [Doc] |
| +$08 | 4 | `i32` | backup date [Doc] |
| +$0C | 4 | `i32` | last access date [Doc] |

Each date is signed seconds from 1 January 2000, 00:00 UTC; `$80000000` means unknown. [Doc] Seconds 0 is Mac date
3,029,529,600 in UTC.

Mac dates are local, so a reader converts the UTC value into the local time of the Mac that will use the file.
ClassicMac uses `ContainerReadOptions.TimeZone` (default: the machine's zone): in a UTC+2 zone, 3600 becomes
1 January 2000, 03:00. It reads creation and modification only; unknown dates stay unrecorded. A date outside
1904–2040 is `applesingle.date-out-of-range` (Warning) and dropped. An entry shorter than 8 bytes is
`applesingle.dates-truncated` (Warning).

**Finder Info (9).** 32 bytes: `FInfo` then `FXInfo` ([unwrapping.md §2.1](unwrapping.md#21-finder-information)). [Doc] Some writers store only the 16 bytes of
`FInfo`; ClassicMac pads a short entry with zeros [Fitted] and ignores bytes after the first 32.

**Macintosh File Info (10), version 2** [Doc]: a `u32` of attributes, bit 0 locked and bit 1 protected. Not read by
ClassicMac.

**ProDOS File Info (11), version 2** [Doc]: access (`u16`), file type (`u16`), auxiliary type (`u32`).

**MS-DOS File Info (12), version 2** [Doc]: the MS-DOS attributes (`u16`).

**AFP Short Name (13), AFP File Info (14), AFP Directory ID (15), version 2** [Doc]: the file's AFP short name, AFP
attributes and the ID of its parent directory on an AFP server. Not read by ClassicMac.

---

## 5. Versions 1 and 2

| | Version 1 | Version 2 |
| --- | --- | --- |
| Bytes 8–23 | home file system name, space-padded [Doc] | zero [Doc] |
| Dates | entry 7, whose layout depends on the home file system; for `Macintosh`, local Mac dates since 1904 [Doc] | entry 8, UTC seconds since 2000, signed [Doc] |
| System-specific info | inside entry 7 [Doc] | entries 10–15 [Doc] |

ClassicMac reads the home file system name by trimming trailing spaces and NUL bytes from bytes 8–23, and uses it
only in version 1.

---

## 6. Reading

An AppleSingle file gives one file with both forks, name, Finder info and dates. An AppleDouble header file read on its
own gives the same with an empty data fork; joining it with its data file is the job of the host-folder layer
([host-folders.md](host-folders.md)), which pairs `._name` and `name.rsrc` header files with `name`.

---

## 7. Writing AppleDouble

ClassicMac writes AppleDouble version 2 header files (`AppleDoubleWriter`), which `unpack` puts beside each data file
as `._name` ([host-folders.md](host-folders.md)). The layout follows the version 2 Developer Note [Doc]; the choice of
entries and their order is ClassicMac's.

The file:

| Offset | Size | Contents |
| --- | --- | --- |
| +$00 | 4 | `$00051607` |
| +$04 | 4 | `$00020000` |
| +$08 | 16 | zero |
| +$18 | 2 | 4 (entries) |
| +$1A | 12 | entry 3 (Real Name): offset 74, length *n* |
| +$26 | 12 | entry 8 (File Dates Info): offset 74+*n*, length 16 |
| +$32 | 12 | entry 9 (Finder Info): offset 90+*n*, length 32 |
| +$3E | 12 | entry 2 (Resource Fork): offset 122+*n*, length *r* |
| +$4A (74) | *n* | the name's bytes (Mac OS Roman), no length byte |
| 74+*n* | 16 | creation, modification, backup, access dates (`i32` each) |
| 90+*n* | 32 | `FInfo` and `FXInfo` |
| 122+*n* | *r* | the resource fork |

- There is no padding or alignment, and no Data Fork entry.
- The Resource Fork entry is always written, with length 0 for an empty fork. It comes last so the fork can be
  streamed straight from its source.
- **Dates**: creation and modification are converted from the Mac's local time to UTC in the writer's time zone
  (`HostWriteOptions.TimeZone`, default the machine's zone), then to seconds since 2000 UTC, clamped to
  `-$7FFFFFFF`…`$7FFFFFFF` so no real date becomes the "unknown" value. A missing date, and the backup and access
  dates, are written as `$80000000`. A local time that does not exist (skipped by a daylight-saving change) is
  converted with the UTC offset in force one hour earlier; an ambiguous one (repeated when clocks go back) is taken
  as standard time.
- **Finder info**: `FInfo` from the file's type, creator, flags, location and folder; `FXInfo` as held (zero when the
  source container had none).
- A resource fork too large for its end offset to fit in a `u32` is refused (an argument error, nothing is written).
- The comment, the locked and protected attributes and `FXInfo` fields ClassicMac does not hold are not written.

Reading the result with the reader of this document in the same time zone gives back the name, Finder info, dates and
resource fork unchanged.

Example: a file named `Read Me` (7 bytes) with a 300-byte resource fork gives entries at 74 (7 bytes), 81 (16),
97 (32) and 129 (300): a 429-byte header file.

---

## 8. Writing AppleSingle

`AppleDoubleWriter.WriteAppleSingle` writes version 2 AppleSingle files [ClassicMac]: the AppleDouble layout of §7
with the magic number `$00051600`, five entries, and a Data Fork entry (1) before the Resource Fork entry; both forks
follow the header, the data fork first. Dates, Finder info and name as in 6.1.

---

## 9. Diagnostics

Codes these readers and the unwrapper emit. Offsets, where given, are in the container's input. None of these formats
is handled by Mac OS, so the "original tools" column describes the format authors' intent where it is
stated; the behaviour of MacBinary, BinHex and StuffIt programs was not traced.

| Code | Severity | Meaning | ClassicMac | Original tools |
| --- | --- | --- | --- | --- |
| `applesingle.entries-truncated` | Error | the file ends inside the entry table | reads the complete descriptors | Mac OS: not read [Code] |
| `applesingle.entry-out-of-range` | Error | an entry's offset plus length runs past the end of the file | skips the entry | Mac OS: not read [Code] |
| `applesingle.double-data-fork` | Warning | an AppleDouble header file has a Data Fork entry | uses it | the Developer Note puts the data fork in the data file [Doc] |
| `applesingle.name-too-long` | Warning | the Real Name is longer than 255 bytes | cuts it to 255 | Mac OS: not read [Code] |
| `applesingle.dates-truncated` | Warning | a File Dates Info or version 1 File Info entry is shorter than 8 bytes | ignores its dates | Mac OS: not read [Code] |
| `applesingle.date-out-of-range` | Warning | a version 2 date falls outside 1904–2040 in the reading zone | drops that date | Mac OS: not read [Code] |
| `applesingle.unknown-entry` | Info | an entry ID other than 1–15 | skips it | readers skip unknown entries [Doc] |
| `applesingle.no-name` | Info | no Real Name entry and no host name | leaves the name empty | Mac OS: not read [Code] |

---

## 10. Not covered

- Writing AppleDouble or AppleSingle version 1, MacBinary I or II, and MacBinary's secondary header and comment.
- The AppleSingle Comment, icon, Macintosh, ProDOS, MS-DOS and AFP entries: not read. Version 1 File Info for home
  file systems other than `Macintosh`: not read.
- AppleSingle and AppleDouble files describing folders (Finder Info holding `DInfo`/`DXInfo`).
