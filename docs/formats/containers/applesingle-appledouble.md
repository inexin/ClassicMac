# AppleSingle and AppleDouble

AppleSingle stores a whole Mac file (both forks and its metadata) in one file of entries. AppleDouble splits it: the
data fork goes into a plain *data file*, everything else into a *header file* in AppleSingle's layout with its own
magic number. Apple defined both for A/UX and foreign file systems, and they share every structure below. [Doc] No
Mac OS 7.1–9 code reads or writes them; on a Mac OS 9.0 disk only mail clients and the StuffIt Engine do (§4).
ClassicMac reads both, versions 1 and 2, and writes version 2 of both.

| | |
| --- | --- |
| Identified by | Magic `$00051600` (AppleSingle) or `$00051607` (AppleDouble header) at +$00 and version `$00010000` or `$00020000` at +$04; AppleDouble header files are named `._name` or `name.rsrc` ([host-folders.md §1.5](host-folders.md#15-appledouble-companion-files)) |
| ClassicMac | Reads and writes (version 2); `ClassicMac.Files.Containers` (`AppleSingleReader`, `AppleDoubleWriter`) |
| Verified against | Nothing yet |
| Sources | Apple's *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note, versions 1 and 2 (RFC 1740); File Exchange 3.0.2, PC Exchange 1.0.4 and Foreign File Access (traced: no AppleSingle code) |

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

### 1.1 Header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | magic number | `u32`: `$00051600` AppleSingle, `$00051607` AppleDouble header file [Doc] |
| +$04 | 4 | version | `u32`: `$00010000` (version 1) or `$00020000` (version 2) [Doc] |
| +$08 | 16 | home file system / filler | version 1: the home file system's name, ASCII, padded with spaces; version 2: filler, all zero [Doc] |
| +$18 | 2 | number of entries | `u16`, may be 0 [Doc] |
| +$1A | 12 × count | entry table | §1.2 [Doc] |

The version 1 home file system names are `Macintosh`, `ProDOS`, `MS-DOS`, `Unix` and `VAX VMS`. [Doc]

### 1.2 Entry descriptor

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | entry ID | `u32` (§1.3) [Doc] |
| +$04 | 4 | offset | `u32`: of the entry's data, from the start of the file [Doc] |
| +$08 | 4 | length | `u32`: of the entry's data; may be 0 [Doc] |

Entries may be stored in any order, and their data need not follow the table in the same order. Readers skip entries
whose IDs they do not know. [Doc]

### 1.3 Entry IDs

| ID | Name | Versions |
| --- | --- | --- |
| 1 | Data Fork | 1, 2 [Doc] |
| 2 | Resource Fork | 1, 2 [Doc] |
| 3 | Real Name | 1, 2 [Doc] |
| 4 | Comment | 1, 2 [Doc] |
| 5 | Icon, black and white | 1, 2 [Doc] |
| 6 | Icon, colour | 1, 2 [Doc] |
| 7 | File Info | 1 only [Doc] |
| 8 | File Dates Info | 2 [Doc] |
| 9 | Finder Info | 1, 2 [Doc] |
| 10 | Macintosh File Info | 2 [Doc] |
| 11 | ProDOS File Info | 2 [Doc] |
| 12 | MS-DOS File Info | 2 [Doc] |
| 13 | AFP Short Name | 2 [Doc] |
| 14 | AFP File Info | 2 [Doc] |
| 15 | AFP Directory ID | 2 [Doc] |

### 1.4 Entry formats

**Data Fork (1), Resource Fork (2).** The fork's bytes. An AppleDouble header file holds no data fork: the data file
is the data fork. [Doc]

**Real Name (3).** The file's name on its home file system, as bytes with no length byte or terminator. [Doc]

**Comment (4), Icons (5, 6).** The Finder comment and the icon images. [Doc]

**File Info (7), version 1.** Its layout depends on the home file system. [Doc] For `Macintosh`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | creation date | `u32`, Mac date (local, since 1904) [Doc] |
| +$04 | 4 | modification date | `u32`, Mac date [Doc] |
| +$08 | 4 | backup date | `u32`, Mac date [Doc] |
| +$0C | 4 | attributes | `u32`: bit 0 locked, bit 1 protected [Doc] |

**File Dates Info (8), version 2:**

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | creation date | `i32` [Doc] |
| +$04 | 4 | modification date | `i32` [Doc] |
| +$08 | 4 | backup date | `i32` [Doc] |
| +$0C | 4 | last access date | `i32` [Doc] |

Each date is signed seconds from 1 January 2000, 00:00 UTC; `$80000000` means unknown. [Doc] Seconds 0 is Mac date
3,029,529,600 in UTC.

**Finder Info (9).** 32 bytes: `FInfo` then `FXInfo` ([unwrapping.md §1.1](unwrapping.md#11-finder-information)).
[Doc] Some writers store only the 16 bytes of `FInfo`. [Fitted]

**Macintosh File Info (10), version 2:** a `u32` of attributes, bit 0 locked and bit 1 protected. [Doc]

**ProDOS File Info (11), version 2:** access (`u16`), file type (`u16`), auxiliary type (`u32`). [Doc]

**MS-DOS File Info (12), version 2:** the MS-DOS attributes (`u16`). [Doc]

**AFP Short Name (13), AFP File Info (14), AFP Directory ID (15), version 2:** the file's AFP short name, AFP
attributes and the ID of its parent directory on an AFP server. [Doc]

## 2. Reading

1. Require the full 26-byte header, the magic number of the format read and a version of 1 or 2. Anything else is not
   this format, and an input that fails this when read is unusable (`container.unreadable`,
   [unwrapping.md §2.2](unwrapping.md#22-unwrapping-a-file)).
2. Read the home file system name: bytes 8–23 with trailing spaces and NUL bytes trimmed. It is used only in version 1.
3. Read the entry table. A table cut off by the end of the file is `applesingle.entries-truncated` (Error); the
   complete descriptors are read.
4. For each descriptor in table order: an entry whose offset plus length runs past the end of the file is skipped with
   `applesingle.entry-out-of-range` (Error). Otherwise read it by ID; a later entry with the same ID replaces an
   earlier one.
   - **1, Data Fork**: the data fork. In an AppleDouble header file it is read anyway, with
     `applesingle.double-data-fork` (Warning).
   - **2, Resource Fork**: the resource fork.
   - **3, Real Name**: the name, as Mac OS Roman bytes; over 255 bytes it is cut to 255, with
     `applesingle.name-too-long` (Warning).
   - **7, File Info**: in a version 1 file whose home file system is exactly `Macintosh`, the creation and
     modification dates as they are; under 8 bytes, `applesingle.dates-truncated` (Warning) and no dates. In any other
     file, skipped.
   - **8, File Dates Info**: the creation and modification dates (step 6), in either version (with the version 2
     layout); under 8 bytes, `applesingle.dates-truncated` (Warning) and no dates.
   - **9, Finder Info**: the first 32 bytes; a shorter entry is padded with zeros.
   - **4–6 and 10–15**: skipped.
   - **Any other ID**: skipped with `applesingle.unknown-entry` (Info).
5. Without a Real Name entry, the name is the host name: the name the input had on the host or in its outer container
   ([unwrapping.md §2.3](unwrapping.md#23-what-a-reader-is-given)). With neither, the name is empty, with
   `applesingle.no-name` (Info).
6. A version 2 date: `$80000000` stays unrecorded. Otherwise the UTC time is converted into the local time of the Mac
   that will use the file (Mac dates are local; the zone is in §5): in a UTC+2 zone, 3600 becomes 1 January 2000,
   03:00. A date outside 1904–2040 is dropped with `applesingle.date-out-of-range` (Warning).

An AppleSingle file gives one file with both forks, name, Finder information and dates. An AppleDouble header file
read on its own gives the same with an empty data fork; joining it with its data file is the host-folder layer's job
([host-folders.md §2.1](host-folders.md#21-choosing-the-layout)).

## 3. Writing

### 3.1 AppleDouble header file

A version 2 header file with four entries in a fixed order. The layout follows the version 2 Developer Note [Doc];
the choice of entries and their order is ClassicMac's [ClassicMac]. *n* is the length of the name, *r* of the
resource fork.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | magic number | `$00051607` |
| +$04 | 4 | version | `$00020000` |
| +$08 | 16 | filler | zero |
| +$18 | 2 | number of entries | 4 |
| +$1A | 12 | entry 3, Real Name | offset $4A (74), length *n* |
| +$26 | 12 | entry 8, File Dates Info | offset $4A+*n*, length 16 |
| +$32 | 12 | entry 9, Finder Info | offset $5A+*n*, length 32 |
| +$3E | 12 | entry 2, Resource Fork | offset $7A+*n*, length *r* |
| +$4A | *n* | name | Mac OS Roman, no length byte |
| +$4A+*n* | 4 | creation date | `i32`, seconds from 2000 UTC, `$80000000` if unknown |
| +$4E+*n* | 4 | modification date | `i32`, the same way |
| +$52+*n* | 4 | backup date | always `$80000000` |
| +$56+*n* | 4 | access date | always `$80000000` |
| +$5A+*n* | 32 | Finder Info | `FInfo` from the type, creator, flags, location and folder; `FXInfo` as held (zero when the source had none) |
| +$7A+*n* | *r* | resource fork | |

1. There is no padding or alignment, and no Data Fork entry.
2. The Resource Fork entry is always written, with length 0 for an empty fork. It comes last so the fork can be
   streamed straight from its source.
3. Creation and modification are converted from the Mac's local time to UTC, then to seconds since 2000 UTC, clamped
   to `-$7FFFFFFF`…`$7FFFFFFF` (`$80000001`–`$7FFFFFFF`) so that no real date becomes the "unknown" value. A missing
   date is `$80000000`.
4. The comment, the locked and protected attributes, and the other entries are not written.

Reading the result with §2 in the same time zone gives back the name, Finder information, dates and resource fork
unchanged.

Example: a file named `Read Me` (7 bytes) with a 300-byte resource fork gives entries at 74 (7 bytes), 81 (16),
97 (32) and 129 (300): a 429-byte header file.

### 3.2 AppleSingle file

A version 2 AppleSingle file is the layout of §3.1 with the magic number `$00051600` and five entries: Real Name, File
Dates Info, Finder Info, then a Data Fork entry (1) before the Resource Fork entry (2). The entry table ends at $56
(86), where the name starts; both forks follow the Finder Info, the data fork first. Name, dates and Finder
information are written as in §3.1. [ClassicMac]

## 4. Variants

| | Version 1 | Version 2 |
| --- | --- | --- |
| Bytes 8–23 | home file system name, space-padded [Doc] | zero [Doc] |
| Dates | entry 7, whose layout depends on the home file system; for `Macintosh`, local Mac dates since 1904 [Doc] | entry 8, UTC seconds since 2000, signed [Doc] |
| System-specific info | inside entry 7 [Doc] | entries 10–15 [Doc] |

No Apple code in Mac OS 7.1 to 9 reads or writes either format: File Exchange 3.0.2 (Mac OS 9.0), PC Exchange 1.0.4
and Foreign File Access contain neither the magic numbers nor code that builds these files, and on a Mac OS 9.0 disk
only mail clients (Netscape Communicator, Outlook Express 4.5, decoding `application/applefile` and
`multipart/appledouble` attachments) and the StuffIt Engine handle them. [Code] PC Exchange and File Exchange use
their own private format ([pc-exchange.md](../file-systems/pc-exchange.md)).

## 5. ClassicMac

- `AppleSingleReader.AppleSingle` and `AppleSingleReader.AppleDouble` (format names `AppleSingle`, `AppleDouble`)
  read §2. They reject versions other than 1 and 2 (`$00030000`, for instance) rather than guess at them.
  [ClassicMac]
- The reader does not check which version defines an ID (§2 step 4). [ClassicMac]
- Forks are slices of the input, not copies. [ClassicMac]
- Version 2 dates are converted with `ContainerReadOptions.TimeZone` (default: the machine's zone). [ClassicMac]
- `AppleDoubleWriter.Write` writes §3.1, which `unpack` puts beside each data file as `._name`
  ([host-folders.md §3.5](host-folders.md#35-writing-a-mac-file)); `AppleDoubleWriter.WriteAppleSingle` writes §3.2.
  [ClassicMac]
- The writer converts dates with its time zone argument (`HostWriteOptions.TimeZone` when writing to the host; default
  the machine's zone). A local time that does not exist (skipped by a daylight-saving change) is converted with the
  UTC offset in force one hour earlier; an ambiguous one (repeated when clocks go back) is taken as standard time.
  [ClassicMac]
- Forks too large for an entry's end offset to fit in a `u32` are refused (an argument error, nothing is written).
  [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `applesingle.date-out-of-range` | Warning | a version 2 date falls outside 1904–2040 in the reading zone | drops that date | not read by Mac OS [Code] |
| `applesingle.dates-truncated` | Warning | a File Dates Info or version 1 File Info entry is shorter than 8 bytes | ignores its dates | not read by Mac OS [Code] |
| `applesingle.double-data-fork` | Warning | an AppleDouble header file has a Data Fork entry | uses it | not read by Mac OS; the Developer Note puts the data fork in the data file [Doc] |
| `applesingle.entries-truncated` | Error | the file ends inside the entry table | reads the complete descriptors | not read by Mac OS [Code] |
| `applesingle.entry-out-of-range` | Error | an entry's offset plus length runs past the end of the file | skips the entry | not read by Mac OS [Code] |
| `applesingle.name-too-long` | Warning | the Real Name is longer than 255 bytes | cuts it to 255 | not read by Mac OS [Code] |
| `applesingle.no-name` | Info | no Real Name entry and no host name | leaves the name empty | not read by Mac OS [Code] |
| `applesingle.unknown-entry` | Info | an entry ID other than 1–15 | skips it | not read by Mac OS; readers skip unknown entries [Doc] |

## 7. Verification

No file made by another AppleSingle or AppleDouble writer is in the tests; the inputs are built by
`tests/ClassicMac.Files.Tests/Fixtures.cs` (`AppleSingle`, `FinderInfo`).

- `tests/ClassicMac.Files.Tests/AppleSingleTests.cs`: `Version_2_AppleSingle_carries_a_whole_file` (§2),
  `Version_2_dates_are_converted_to_the_readers_zone` (§2 step 6), `Unknown_dates_are_left_out`,
  `Version_1_Macintosh_file_info_holds_Mac_dates`, `AppleDouble_header_files_have_no_data_fork`,
  `Short_Finder_info_is_padded`, `Damaged_and_unknown_entries_are_reported`, `A_truncated_entry_table_is_an_error`,
  `Other_data_is_not_AppleSingle`.
- `tests/ClassicMac.Files.Tests/ContainerWriterTests.cs`: `AppleSingle_reads_back` (§3.2 read back by §2).
- `tests/ClassicMac.Files.Tests/HostWriteTests.cs`: `AppleDouble_headers_read_back`,
  `Written_files_read_back_the_same` (§3.1 dates round-trip).
- `tests/ClassicMac.Files.Tests/UnwrapTests.cs`: `AppleDouble_pairs_join_the_header_file`,
  `AppleDouble_files_named_dot_rsrc_join_too`.

## 8. Not covered

- Writing version 1 files.
- The Comment, icon, Macintosh, ProDOS, MS-DOS and AFP entries: not read. Version 1 File Info for home file systems
  other than `Macintosh`: not read.
- AppleSingle and AppleDouble files describing folders (Finder Info holding `DInfo`/`DXInfo`).
- MIME encapsulation ([unwrapping.md §8](unwrapping.md#8-not-covered)).

## 9. References

1. Apple Computer, *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note, version 1 (1990) and version 2.
2. RFC 1740, MIME encapsulation of Macintosh files (MacMIME), based on version 2.
3. Shared structures (Finder information, dates): [unwrapping.md](unwrapping.md).
