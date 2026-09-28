# Mac files on other file systems — an implementer's specification

A classic Mac OS file has a name of up to 31 (HFS) or 255 bytes in Mac OS Roman, 32 bytes of Finder information,
dates in local time and two forks. Other file systems keep one stream of bytes and a name in their own character set,
so every tool that put Mac files on them kept the rest somewhere beside the file. This document describes those
layouts completely enough to read them, and to write the ones ClassicMac writes, without reading ClassicMac's code:
Basilisk II / SheepShaver shared folders, AppleDouble companion files, macOS named forks, and the `RESOURCE.FRK` /
`FINDER.DAT` data PC Exchange and File Exchange kept on DOS disks. It then specifies how ClassicMac turns Mac names
into host names and back, and what `unpack` writes.

The FAT volume itself (directory entries, VFAT long names, clusters) is in [FAT.md](FAT.md); the AppleDouble header
format in full is in [CONTAINERS.md](CONTAINERS.md). This document covers what lies on top of them.

References:

- *Inside Macintosh: Files* (`FInfo`, `FXInfo`, `DInfo`, `DXInfo`, file names) and *Inside Macintosh: Macintosh
  Toolbox Essentials*, the Finder Interface chapter (Finder flags).
- Apple's *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note (version 2; RFC 1740).
- Microsoft's FAT specification (fatgen103) for DOS dates and 8.3 names, and Microsoft's *Naming Files, Paths, and
  Namespaces* for what Windows names may hold.
- PC Exchange 1.0.4 and File Exchange 3.0.2 (Mac OS 9.0), traced in disassembly; File Exchange checked on FAT12
  disks in SheepShaver, Mac OS 9.0.
- Basilisk II and SheepShaver (GPL): their shared-folder layout as the emulator behaves, verified in SheepShaver's
  Windows build with Mac OS 9.0. Their source is a behavioural reference only.
- The Unarchiver (`unar -k visible`), for the `name.rsrc` form of AppleDouble: behaviour only.
- Unicode's `VENDORS/APPLE/ROMAN.TXT` for Mac OS Roman; Windows code page 1252.

Contents

1. [Conventions](#1-conventions)
2. [Overview](#2-overview)
3. [Finder info](#3-finder-info)
4. [Basilisk II and SheepShaver shared folders](#4-basilisk-ii-and-sheepshaver-shared-folders)
5. [AppleDouble companion files](#5-appledouble-companion-files)
6. [macOS named forks](#6-macos-named-forks)
7. [PC Exchange and File Exchange folders](#7-pc-exchange-and-file-exchange-folders)
8. [File Exchange names](#8-file-exchange-names)
9. [The extension map](#9-the-extension-map)
10. [Host names](#10-host-names)
11. [Reading a host file](#11-reading-a-host-file)
12. [Writing a Mac file to the host](#12-writing-a-mac-file-to-the-host)
13. [What unpack writes](#13-what-unpack-writes)
14. [Diagnostics](#14-diagnostics)
15. [Not covered](#15-not-covered)

---

## 1. Conventions

The conventions and source tags of [README.md](README.md) apply: big-endian values, offsets in hex, Mac dates as
`u32` seconds since 1904 in local time, and **[Doc]**, **[Code]**, **[Verified]**, **[Author]** and **[Fitted]** on
every rule. In this document:

- **[Author]** also covers the behaviour of the tool that defined a layout Apple never documented: Basilisk II /
  SheepShaver for shared folders, The Unarchiver for `name.rsrc`. Behaviour only; their code is GPL or LGPL.
- **[Verified]** without further detail means verified in SheepShaver's Windows build, Mac OS 9.0.
- **[ClassicMac]** marks a rule that is ClassicMac's own choice: how it picks among layouts, the names it makes and
  what `unpack` writes. It claims nothing about what a Mac did; it defines output that only ClassicMac specifies.
- **[Fitted?]** marks a rule whose source is uncertain.
- DOS dates and times (section 7.6) are **little-endian**, as everything on a FAT volume is. Everything else is
  big-endian.
- "Host" is the computer ClassicMac runs on; a "host name" is a file name on its disk (UTF-16 on Windows and .NET).

---

## 2. Overview

| Layout | Data fork | Resource fork | Finder info and dates | Written by |
| --- | --- | --- | --- | --- |
| Plain | the file | none | none | anything |
| Basilisk II folder | `name` | `.rsrc/name` | `.finf/name`; dates are the data file's host times | the emulators; ClassicMac |
| AppleDouble pair | `name` | in `._name` | in `._name` | macOS, netatalk, ClassicMac |
| AppleDouble, visible | `name` | in `name.rsrc` | in `name.rsrc` | The Unarchiver |
| macOS named fork | `name` | `name/..namedfork/rsrc` | extended attribute | macOS |
| PC Exchange folder | `NAME.EXT` | `RESOURCE.FRK/NAME.EXT` | a record in `FINDER.DAT`; DOS dates | PC Exchange, File Exchange |

The resource fork is always stored raw, with no header, except inside an AppleDouble header file, where it is one
entry [Code] [Author] [Doc].

---

## 3. Finder info

Every layout that keeps Finder info keeps the same 32 bytes: `FInfo` followed by `FXInfo` for a file, `DInfo`
followed by `DXInfo` for a folder [Doc] (*Inside Macintosh: Files*).

| Offset | Size | Type | File (`FInfo`, `FXInfo`) | Folder (`DInfo`, `DXInfo`) |
| --- | --- | --- | --- | --- |
| +$00 | 4 | `OSType` / `Rect` | `fdType` | `frRect` (8 bytes: +$00–$07) |
| +$04 | 4 | `OSType` | `fdCreator` | (`frRect`, continued) |
| +$08 | 2 | `u16` | `fdFlags`, Finder flags | `frFlags` |
| +$0A | 4 | `Point` | `fdLocation`, icon position in its window | `frLocation` |
| +$0E | 2 | `i16` | `fdFldr`, window the icon is in | `frView` |
| +$10 | 2 | `i16` | `fdIconID` | `frScroll` (4 bytes: +$10–$13) |
| +$12 | 6 | reserved | `fdReserved` (3 × `i16`) | +$12: rest of `frScroll`; +$14: `frOpenChain` (`i32`) |
| +$18 | 1 | `i8` | `fdScript` | `frScript` |
| +$19 | 1 | `i8` | `fdXFlags` | `frXFlags` |
| +$1A | 2 | `i16` | `fdComment` | `frComment` |
| +$1C | 4 | `i32` | `fdPutAway` | `frPutAway` |

The Finder flags this document uses: `$4000` is invisible, `$0400` has a custom icon, `$8000` is an alias [Doc]
(*Macintosh Toolbox Essentials*).

ClassicMac interprets the first 16 bytes and keeps bytes +$10–$1F raw, so they round-trip unchanged. A shorter
record is padded with zeros, since some writers keep only `FInfo`; bytes past 32 are ignored [ClassicMac].

---

## 4. Basilisk II and SheepShaver shared folders

Basilisk II and SheepShaver show a folder of the host as a Mac volume. The data fork is the host file itself; the
rest lives in two hidden folders beside it.

### 4.1 Layout

For a file `name` in host folder `F` [Author] [Verified]:

| Path | Content |
| --- | --- |
| `F/name` | the data fork, raw. A file with an empty data fork is an empty host file. |
| `F/.rsrc/name` | the resource fork, raw, no header. Missing means an empty fork. |
| `F/.finf/name` | 32 bytes of Finder info (section 3). |

- The companions carry exactly the data file's host name [Verified].
- A folder's `DInfo` and `DXInfo` (32 bytes) are in `.finf/<folder name>` in the **parent** folder, not inside the
  folder itself. SheepShaver writes it once the Finder has opened or moved the folder; folders the Finder never
  touched have none [Verified].
- A file with no `.finf` gets a type guessed by the emulator from its name ending: a `.txt` file shows as
  `TEXT`/`ttxt` [Verified]. ClassicMac leaves type and creator zero in that case [ClassicMac].
- A missing `.rsrc` file reads as an empty resource fork in the emulator [Verified]; ClassicMac writes none for an
  empty fork (section 12) [ClassicMac].
- Resource forks written this way open normally through the Resource Manager in the emulator [Verified].

### 4.2 Dates

The layout has no place for dates: the emulator takes the file's creation and modification dates from the data
file's host times [Verified].

- SheepShaver (Windows) converts host times with the UTC offset **in force now**, not the one on the date itself:
  a date in the other half of the year from today comes back an hour off [Verified].
- A creation date before 1970 does not survive: 1904-01-02 read back as 2036-02-07 06:28:16 (a negative `time_t`
  taken as unsigned) [Verified].
- ClassicMac writes the data file's creation and modification times from the Mac dates, converted with the rules
  for that date (section 12) [ClassicMac]. It does not read the host times back as Mac dates for this layout: a file
  read from a Basilisk II folder has no dates [ClassicMac].

### 4.3 Names: SheepShaver on Windows

The Mac name and the host name are the same bytes [Verified]:

- Each Mac OS Roman byte becomes the **Windows-1252 character with the same byte value**. No Unicode conversion is
  done: Mac `é` ($8E) is host `Ž` (Windows-1252 $8E, U+017D), and a host `é` (U+00E9, byte $E9) shows on the Mac
  as `È` [Verified].
- Exactly seven characters are escaped when the Mac creates a name, as `%` and two hex digits of the byte:
  `%` → `%25`, `?` → `%3F`, `*` → `%2A`, `"` → `%22`, `<` → `%3C`, `>` → `%3E`, `|` → `%7C`. A `%` is always
  escaped, even where it already looks like an escape (`x%41y` → `x%2541y`) [Verified].
- When reading, **any** `%XX` in a host name is decoded to the byte `$XX`, whether or not the emulator would have
  escaped it (`Icon%0D` is the Mac name `Icon` + CR) [Verified].
- The emulator does not escape `/`, `\`, a trailing dot or space, or Windows device names. What happens when the
  Mac creates such a name [Verified]:

  | Mac name | Host result | Mac sees |
  | --- | --- | --- |
  | `trail.`, `space `, `dot..` | `trail`, `space`, `dot` (Windows drops the ending) | the renamed file |
  | `a/b` | an empty file `a`; the create returns −43 | `a` |
  | `back\slash` | a folder `back` holding `slash` | a folder `back` |
  | `con.txt`, `COM1`, `nul.txt`, `aux`, `lpt1.txt` | nothing; the create returns −48 | nothing |
  | `CLOCK$` | `CLOCK$` | `CLOCK$` |

- Host names written with `%2E`, `%20` or a device name escaped as `%XX` decode back to the forbidden name and the
  file is then missing (`trail%2E`, `space%20`) or reads as an empty device (`co%6E.txt`, `COM%31`: both forks
  0 bytes, location −1,−1, date 0, type `TEXT`/`ttxt` or `????`/`????`) [Verified]. A name the emulator cannot hold
  therefore has no escape; it must be changed (section 10.2).
- Bytes Windows-1252 leaves undefined ($81, $8D, $8F, $90, $9D) were not tested in the emulator. ClassicMac
  escapes them as `%XX`, expecting the emulator to decode them like any other escape [Fitted?].
- Other builds (Linux, macOS hosts) may convert names differently, for example to UTF-8; only the Windows build
  was checked [Fitted?].

---

## 5. AppleDouble companion files

An AppleDouble header file holds everything of a Mac file except its data fork, which is the plain file beside it.
The header format (magic `$00051607`, versions 1 and 2, entry table) is Apple's [Doc] and is specified in
[CONTAINERS.md](CONTAINERS.md). No Mac OS 7.1–9 file-system code reads or writes AppleSingle or AppleDouble; on
Mac OS 9 only mail clients and the StuffIt Engine handle them [Code].

### 5.1 Where the header file is

| Form | Header file | Source |
| --- | --- | --- |
| Hidden | `._name` beside `name` | macOS's convention on volumes without forks [Fitted?] |
| Visible | `name.rsrc` beside `name` | [Author] (The Unarchiver with `-k visible`) |

- ClassicMac takes `._name` when it starts with the AppleDouble magic and version 1 or 2. Anything else is ignored
  with `host.appledouble-invalid`, and the next layout is tried [ClassicMac].
- `name.rsrc` is taken only when it is a valid AppleDouble header, silently otherwise, since plenty of `.rsrc` files
  are raw forks or other data [ClassicMac]. `._name` wins when both exist [ClassicMac].
- The Mac name is the header's Real Name entry (ID 3); without one, the host name read as Mac OS Roman
  (section 11.2) [ClassicMac].
- The data fork is always the host file. A Data Fork entry in the header (which AppleDouble should not have) is
  reported by the AppleDouble reader and then replaced by the host file [ClassicMac].
- Version 2 File Dates Info (ID 8) holds signed seconds from 2000-01-01 00:00 **UTC**, `$80000000` for unknown
  [Doc]. Mac dates are local, so ClassicMac converts with the time zone in its read options (default: the
  machine's) [ClassicMac].
- macOS's own `._` files may carry more than 32 bytes in the Finder Info entry (extended attributes follow); only
  the first 32 are Finder info [Fitted?].

### 5.2 The header file ClassicMac writes

AppleDouble version 2 with four entries, the resource fork last so it can be streamed [Doc] (layout) [ClassicMac]
(choice and order). `n` is the length of the Mac name.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `u32` | magic `$00051607` [Doc] |
| +$04 | 4 | `u32` | version `$00020000` [Doc] |
| +$08 | 16 | bytes | filler, zero [Doc] |
| +$18 | 2 | `u16` | entry count: 4 |
| +$1A | 12 | entry | ID 3 (Real Name), offset $4A, length `n` |
| +$26 | 12 | entry | ID 8 (File Dates Info), offset $4A + `n`, length 16 |
| +$32 | 12 | entry | ID 9 (Finder Info), offset $5A + `n`, length 32 |
| +$3E | 12 | entry | ID 2 (Resource Fork), offset $7A + `n`, length of the fork (0 allowed) |
| +$4A | `n` | bytes | the Mac name, Mac OS Roman, no length byte, no padding |
| +$4A+`n` | 4 | `i32` | creation date: seconds from 2000-01-01 00:00 UTC, `$80000000` if unknown |
| +$4E+`n` | 4 | `i32` | modification date, the same way |
| +$52+`n` | 4 | `i32` | backup date: always `$80000000` |
| +$56+`n` | 4 | `i32` | access date: always `$80000000` |
| +$5A+`n` | 32 | bytes | Finder info (section 3) |
| +$7A+`n` | … | bytes | the resource fork |

An entry is 12 bytes: `u32` ID, `u32` offset from the start of the file, `u32` length [Doc].

Date conversion [ClassicMac]: the Mac date is local time in the zone of the write options (default: the machine's).
A local time that falls in a daylight-saving gap takes the offset in force an hour earlier. The result is clamped to
`$80000001`–`$7FFFFFFF` so that a real date never reads as unknown. A fork that would push an offset past `u32` is
refused.

---

## 6. macOS named forks

On macOS the resource fork of `path` can be read as the file `path/..namedfork/rsrc` [Fitted?]. ClassicMac reads it
only when running on macOS and only when it is non-empty [ClassicMac].

macOS also keeps the 32 bytes of Finder info as the extended attribute `com.apple.FinderInfo` (and the resource fork
as `com.apple.ResourceFork`) [Fitted?]. ClassicMac does not read that attribute, so a file read this way has the
resource fork but no type, creator or flags, and no dates [ClassicMac]. It never writes this layout.

---

## 7. PC Exchange and File Exchange folders

PC Exchange (System 7.1–Mac OS 8) and File Exchange (Mac OS 9) let the Mac use DOS disks. They keep what FAT cannot
hold in two hidden items in **every directory**. The format is the same in PC Exchange 1.0.4 and File Exchange
3.0.2 [Code]; PC Exchange 2.x was not examined, and the same record code at both ends of the range suggests it is the
same [Fitted?]. No header or version number exists anywhere [Code].

### 7.1 `RESOURCE.FRK`

- A subdirectory named `RESOURCE.FRK` with the attributes hidden + directory ($12) [Code] [Verified].
- It holds one file per data file that has a resource fork, under **the data file's 8.3 name**, containing the raw
  fork with no header [Code] [Verified]. Its attributes are the data file's with archive ($20) added; its dates are
  when it was written [Code].
- It is created at the first resource-fork write; deleting the data file deletes the fork file; move and rename carry
  it along; `RESOURCE.FRK` is removed when nothing else is left in its directory [Code].
- A missing fork file means an empty resource fork [Code]. A fork file made by hand opens normally [Verified].

### 7.2 `FINDER.DAT`

A hidden file ($02; the archive bit, $22, appears after the Mac writes it) of 92-byte records, one per item of the
directory that has one [Code] [Verified]. An item's record is in its **parent's** `FINDER.DAT` [Code].

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | `u8` | length of the Mac name, 0–31; **0 marks a free record** [Code] |
| +$01 | 31 | bytes | the Mac name, Mac OS Roman. Bytes after the name are garbage [Code] [Verified] |
| +$20 | 16 | `FInfo` | Finder info (`DInfo` for a folder), section 3 [Code] |
| +$30 | 16 | `FXInfo` | extended Finder info (`DXInfo`) [Code] |
| +$40 | 4 | `u32` | creation date, Mac seconds, local time; 0 when unset [Code] |
| +$44 | 4 | `u32` | modification date, full precision (odd seconds kept) [Code] [Verified] |
| +$48 | 4 | `u32` | backup date; written only when non-zero [Code] |
| +$4C | 4 | `u32` | file number (catalog node ID): the volume's counter starts at `$7FFFFFFF` and counts **down** [Code] [Verified] |
| +$50 | 11 | chars | the item's DOS 8.3 name, upper case, space-padded, no dot (`FANTAS~1EML`): **the lookup key** [Code] |
| +$5B | 1 | — | unused; garbage [Code] |

Other files [Code]:

- The root directory's `FINDER.DAT` holds a record keyed by the volume label that keeps the volume's next file number
  [Code] [Verified].
- `FILEID.DAT`, in the root only and hidden, holds 64-byte records of file ID, parent directory ID and a Pascal name,
  record 0 being a header (`$00010000`). Only `PBCreateFileIDRef` creates it [Code]. ClassicMac does not read it.
- The Mac hides `FINDER.DAT`, `FILEID.DAT` and `RESOURCE.FRK` from listings **by name** as well as by the hidden
  attribute [Code]. "Desktop" is a synthetic root entry [Code].
- Mounting a FAT disk writable makes the Finder add `TheVolumeSettingsFolder` (with `DesktopPrinters DB`),
  `Desktop Folder`, `Trash` and a hidden `DESKTOP` file with a resource fork [Verified].

### 7.3 Packing and free records

- Records are packed **floor(cluster size / 92) per cluster and never straddle a cluster**. The bytes between the
  last record of a cluster and the cluster's end are undefined [Code] [Verified] (with 512-byte clusters, records
  sit at 0, 92, …, 368, then 512).
- A record is free when its name length is 0. Deleting an item clears the name and the DOS name; the file never
  shrinks [Code].
- A new record takes the first free slot, but **never offset 0** (a quirk), or is appended [Code].
- Stray records with a garbage name and an empty DOS name appear; treat a record whose first DOS-name byte is 0 as
  free [Verified].
- Nothing is validated. A corrupt record is used as it stands [Code].
- Parse only whole 92-byte slots before the end of the file. Cluster slack in `FINDER.DAT`, and in data files, can
  hold stale copies of other records [Verified].

**Cluster size.** The file does not record it. On a FAT volume the reader knows it (see [FAT.md](FAT.md)). For a
`FINDER.DAT` found on the host, ClassicMac tries tight packing (every 92 bytes) and cluster sizes of 512 to 65,536
bytes (powers of two), counts the used records under each, and keeps the size under which every used record is
plausible — name length 1–31 and all eleven DOS-name bytes in $20–$7E — with the most used records; the smaller size
wins a tie. If no size gives only plausible records, no records are read [Fitted].

### 7.4 When records are made

- Getting catalog information for an item with no file number creates a record, so **browsing a writable disk
  creates records** [Code] [Verified]. Records are also made when the Mac name differs from the 8.3 name and
  whenever Finder info is set [Code], but only while the volume's "save info" flag is set [Code].
- A new record has type `TEXT`, creator `'dosa'`, flags 0 and `fdPutAway` 2 [Code] [Verified]. Its dates are 0 in
  File Exchange 3.0.2 [Verified]; the code of both versions leaves them from uninitialised memory [Code].
- A record File Exchange created for a file that had a resource fork but no record got a **garbage Mac name**
  [Verified]. File Exchange shows such names as they are; see [FAT.md](FAT.md) for the warning ClassicMac gives.
- File Exchange creates a record and a VFAT long name for every file the Mac creates, even one whose name fits 8.3
  [Verified].

### 7.5 Finding an item's record

- The Mac looks items up by the record's Mac name, then by the VFAT long name (whose checksum must match), then by
  the 8.3 name [Code] [Verified]. Records are matched to directory entries by the 8.3 key at +$50; with duplicate
  keys the first in file order wins [Code].
- On the host, the 8.3 name may not be visible. ClassicMac forms the key from the host name when it is a valid 8.3
  name — a stem of 1–8 and an extension of 0–3 characters, all in $21–$7E, one dot at most — upper-cased and
  space-padded to 8 + 3 (`FANTAS~1.EML` → `FANTAS~1EML`, `readme` → `README     `). Otherwise (a long host name)
  it matches the record's Mac name against the host name, ignoring case [Fitted].
- `RESOURCE.FRK` and `FINDER.DAT` are found whatever their case, as on FAT [ClassicMac]. The fork file is looked up
  under the host name [ClassicMac].

Advice from the traces [Verified]: trust a record's dates and file number more than its type, which can revert to
an earlier value around a close; ignore records whose DOS key matches no directory entry.

### 7.6 Dates

DOS directory entries hold local time [Code], in two little-endian `u16` fields [Doc] (fatgen103):

| Field | Bits | Meaning |
| --- | --- | --- |
| date | 15–9 | year − 1980 (0–127) |
| date | 8–5 | month, 1–12 |
| date | 4–0 | day, 1–31 |
| time | 15–11 | hours, 0–23 |
| time | 10–5 | minutes, 0–59 |
| time | 4–0 | seconds / 2, 0–29 |

- DOS keeps even seconds only: writing stores the exact date in the record and the 2-second-truncated date in the
  directory entry ($B0000001 reads back as $B0000000) [Verified].
- **Year wrap (File Exchange):** DOS years 2032–2107 read as 128 years earlier, 1904–1979, and Mac years 1904–1979
  are written that way (Mac 1950 → DOS 2078; DOS 2040 reads as 1912) [Code] [Verified]. PC Exchange 1.0.4 has no
  wrap; pre-1980 dates come out as garbage [Code].
- A date field of 0, or an impossible date or time, is no date [ClassicMac].

Which date the Mac shows:

| Date | File Exchange 3.0.2 | PC Exchange 1.0.4 |
| --- | --- | --- |
| Created | the DOS entry's creation fields ("now" if zero); the record's +$40 is written but never read [Code] [Verified] | the record's +$40, or 0 without a record; DOS entries carry no creation date for it [Code] |
| Modified | the later of the DOS modification time and the record's +$44 [Code] [Verified] | the same [Code] |
| Backup | the record's +$48 [Code] [Verified] | the record's +$48 [Code] |

ClassicMac follows File Exchange: created from the DOS entry (the record's +$40 only when the entry has none),
modified the later of the two; it keeps no backup date [ClassicMac]. For a `FINDER.DAT` folder on the host, the
host file's creation and modification times stand for the DOS entry's: truncated to even seconds, and years from
2032 moved back 128 [ClassicMac].

### 7.7 Flags from DOS attributes

- DOS hidden or system → the file is invisible (`$4000`) [Code]. ClassicMac adds the invisible flag the same way
  [ClassicMac].
- DOS read-only or system → the file is locked [Code]. ClassicMac does not carry a locked attribute [ClassicMac].
- Type `'scut'` makes the file an alias (File Exchange only) [Code].

### 7.8 Without a record

File Exchange shows a file with no record as `TEXT`/`'dosa'`, then applies the extension map (section 9) [Code]
[Verified]. On a FAT volume ClassicMac does the same (placeholder type, DOS dates) [ClassicMac]. For a host folder, a
file with a `RESOURCE.FRK` fork but no record is read with the fork only: empty Finder info and no dates
[ClassicMac].

---

## 8. File Exchange names

ClassicMac does not write FAT volumes; the Mac-to-FAT rules are given for completeness and for writers.

### 8.1 Creating a file: Mac name to FAT

File Exchange 3.0.2, in order [Code] [Verified: 14 names, every 8.3 name exact]:

1. A record in the directory with the same Mac name, ignoring case, refuses the create with −48 (`dupFNErr`).
2. **Sanitise:** control characters, `" * / : < > ? \ |` and $7F become `_`; trailing dots and spaces are removed;
   leading spaces stay. **The sanitised name replaces the Mac name** (`a/b` becomes `a_b`, `trail.` becomes `trail`).
3. **Short name:** characters DOS does not allow, including every byte ≥ $80, become `_`; spaces are dropped;
   letters are upper-cased. A name that does not fit 8.3 exactly is cut to 6 characters and `~1`, with the extension
   cut to 3. On a collision the number grows and the `~` moves left. Device names are not checked.
4. A `FINDER.DAT` record and a VFAT long name are always written. The long name is the Mac name converted from Mac OS
   Roman to UTF-16, precomposed.

| Mac name | 8.3 name | Mac name | 8.3 name |
| --- | --- | --- | --- |
| `Hello World` | `HELLOW~1` | `trail.` | `TRAIL` |
| `a/b` | `A_B` | `12345678.1234` | `123456~1.123` |
| `Résumé` | `R_SUM_~1` | `.profile` | `~1.PRO` |
| `readme.txt` | `README.TXT` | `COM1` | `COM1` |
| `a.b.c` | `AB~1.C` | `CON.txt` | `CON.TXT` |
| `   lead` | `LEAD~1` | `ÄÖÜ ß` | `____~1` |
| `™®©` | `___~1` | a 31-character name | `ABCDEF~1` |

### 8.2 Reading a file with no record: long name to Mac name

[Code] [Verified]:

- The long name is taken in precomposed form (NFC).
- If **every** character has a Mac OS Roman byte, the Mac OS Roman bytes are the name, **`:` included**
  (`a:b c.txt` shows with its colon).
- If even one character has none, the whole name takes another path: each UTF-16 unit's **low byte**, with `:`
  becoming `_` (`漢字 kanji.txt` shows as `"W kanji.txt`). File Exchange's own `_` fallback never takes effect.
- A result over 31 bytes is shortened to exactly 31: the start of the name, `#`, three upper-case hex digits, and the
  extension (`This is a very long Win#7C7.txt`).
  - The extension is from the last `.` among the final six characters of the name; among the final `length − 2` for
    names of 3–6 characters; none for shorter names.
  - The start keeps `27 − extension length` characters (the extension counting its dot).
  - The digits are the low 12 bits of a CRC-16 over the whole long name as big-endian UTF-16: polynomial `$1021`,
    initial value 0, no reflection, no final XOR (the UDF unique-name checksum).
  - Both parts are converted the same way as the whole name would have been (Mac OS Roman, or low bytes).
- With no long name, the 8.3 name is the Mac name.

---

## 9. The extension map

What File Exchange 3.0.2 does [Code] [Verified]:

- Its per-volume extension table is **always empty**: at startup it moves the old PC Exchange `'dMap'` −4040 entries
  into Internet Config and builds its table with a count of 0 [Code].
- The only mapping is therefore Internet Config's map of name endings, applied **only when type and creator are
  exactly `TEXT`/`'dosa'`** [Code] [Verified]. A stored type is never overridden (`APPTEST.TXT` stored as
  `APPL`/`abcd` stays so) [Verified].
- The ending is matched against the end of the Mac long name, ignoring case; the longest ending wins, the earlier
  entry on a tie; the entries' flags are ignored [Code].
- It needs the "map extensions" preference (Gestalt `'pcxg'` bit 3), on by default [Code].
- Examples from the default map: `.txt` → `TEXT`/`ttxt`, `.bin` → `BINA`/`SITx`, `.jpg` → `JPEG`/`ogle`, `.gif` →
  `GIFf`/`ogle`, `.doc` → `WDBN`/`MSWD` [Verified]; `.pdf` → `'PDF '`/`CARO`, `.sit` → `SITD`/`SITx` [Code].
- **Only shown, rarely stored:** when a data fork is closed or flushed, File Exchange reads the Finder info (mapping
  applied) and writes it back, so files that were opened keep the mapped type in `FINDER.DAT`; records created only by
  browsing keep `TEXT`/`'dosa'` on disk [Code] [Verified].

PC Exchange 1.0.4 uses only its own table, matched on the 3-character DOS extension, with the default `.TXT` →
`TEXT`/`ttxt`. Its code applies the table only when a record exists and lets it override the stored type; this was
not checked on a running system [Code].

ClassicMac shows stored types. It applies a map only when the application supplies one (`ExtensionMap` in the read
options), with File Exchange's matching rules; none ships with ClassicMac [ClassicMac]. The FAT reader matches the Mac
name; the host-folder reader matches the host name [ClassicMac].

---

## 10. Host names

ClassicMac makes host names from Mac names in two ways: a **portable** name valid on Windows, macOS and Linux, used
for AppleDouble output and by `extract`, and **SheepShaver's** naming for Basilisk II folders. Both work on the Mac
name's bytes.

### 10.1 Portable names

[ClassicMac], with the forbidden characters and device names from Microsoft's naming rules [Doc]:

1. Each Mac OS Roman byte becomes its Unicode character (`ROMAN.TXT`; $DB is `€`, $F0 the Apple logo U+F8FF).
2. These become `%` and two upper-case hex digits of the **Mac OS Roman byte**: bytes below $20, $7F,
   `% / \ : * ? " < > |`, and a final space or dot.
3. A Windows device name — `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, ignoring case, as the part
   before the first dot with trailing spaces ignored — gets the last character of that part escaped.
4. An empty name is `%00`.
5. The name is fitted to the length limit (section 10.3).

| Mac name | Host name | Mac name | Host name |
| --- | --- | --- | --- |
| `a/b` | `a%2Fb` | `COM1` | `COM%31` |
| `100% done?` | `100%25 done%3F` | `con.txt` | `co%6E.txt` |
| `Icon` + CR | `Icon%0D` | `Résumé ƒ` | `Résumé ƒ` |
| `trail.` | `trail%2E` | `space ` | `space%20` |

The escapes are one-way: a reader does not decode `%XX` in a portable name (section 11.2). AppleDouble output keeps
the exact Mac name in its Real Name entry.

### 10.2 SheepShaver names

For Basilisk II folders ClassicMac writes what SheepShaver on Windows would write, so the emulator reads the Mac
name back, and changes only what it cannot hold [Verified] (section 4.3) [ClassicMac] (the replacements):

1. Each byte becomes the Windows-1252 character with the same value (bytes $00–$7F and $A0–$FF are the same code
   point; $80–$9F are Windows-1252's `€ ‚ ƒ „ … † ‡ ˆ ‰ Š ‹ Œ Ž ‘ ’ “ ” • – — ˜ ™ š › œ ž Ÿ`).
2. These become `%XX` (the byte): `% ? * " < > |`, bytes below $20, $7F, and the bytes Windows-1252 leaves undefined
   ($81, $8D, $8F, $90, $9D).
3. `/`, `\` and `:` become `_`. No escape can be used: the emulator would decode it back to the character [Verified].
4. Every trailing space or dot becomes `_` (`dots..` → `dots__`).
5. A Windows device name (as in 10.1, checked after step 4) gets `_` appended to the part before the first dot
   (`COM1` → `COM1_`, `con.txt` → `con_.txt`). `CLOCK$` is not a device name for this purpose [Verified].
6. An empty name is `_`.
7. The name is fitted to the length limit (section 10.3).

Steps 3–6 change the name the Mac will see; `unpack` warns when they do (section 13.3).

| Mac name | Host name | Same name on the Mac |
| --- | --- | --- |
| `Résumé` | `RŽsumŽ` | yes |
| `50% done?` | `50%25 done%3F` | yes |
| `star* <q"> pipe|` | `star%2A %3Cq%22%3E pipe%7C` | yes |
| `Icon` + CR | `Icon%0D` | yes |
| bytes $41 $81 $8D $42 | `A%81%8DB` | yes |
| `a/b`, `back\slash` | `a_b`, `back_slash` | no |
| `trail.`, `space ` | `trail_`, `space_` | no |
| `COM1`, `con.txt` | `COM1_`, `con_.txt` | no |

### 10.3 Length

[ClassicMac]: a name is limited to a number of UTF-16 characters (255 unless the caller gives less; never less
than 8). When it is longer:

- The name is treated as a list of parts: a character, or a whole `%XX` escape. A part is never split.
- The extension is the last `.` part and what follows it, if that is at most 5 parts and the dot is not first.
- Parts of the stem are kept from the start while they fit in the limit less the extension's length; trailing spaces
  and dots of the kept stem are removed; the extension is appended.

For example `aaaa…a?.sit` (40 `a`) with a limit of 20 is `aaaaaaaaaaaaaaaa.sit`.

### 10.4 Distinct names

[ClassicMac]: within one output folder, names are kept distinct **ignoring case**, since Windows and macOS disks
ignore it. A name already taken gets ` ~2`, ` ~3`, … before its extension (the text from the last dot, unless the dot
is first): `DUP.TXT`, `DUP ~2.TXT`, `dup ~3.txt`. The numbered name is not fitted to the length limit again.

---

## 11. Reading a host file

### 11.1 Choosing the layout

ClassicMac reads a host file `F/name` with the first layout that applies [ClassicMac]:

1. **PC Exchange:** `F` holds a `RESOURCE.FRK` folder or a `FINDER.DAT` file (any case), `name` is not `FINDER.DAT`,
   and either `RESOURCE.FRK/name` exists or `FINDER.DAT` has a record for `name` (section 7.5). The record gives the
   Mac name, Finder info and dates (section 7.6); the map applies if supplied (section 9); DOS hidden or system
   makes it invisible.
2. **Basilisk II:** `F/.rsrc/name` or `F/.finf/name` exists. A `.finf` that is not 32 bytes is reported
   (`host.finf-length`) and read as far as it goes.
3. **AppleDouble:** `F/._name` is a valid AppleDouble header (otherwise `host.appledouble-invalid`).
4. **AppleDouble, visible:** `F/name.rsrc` is a valid AppleDouble header.
5. **macOS named fork:** on macOS only, `name/..namedfork/rsrc` is non-empty.
6. **Plain:** the data fork only.

When a format is split across several files, the other files of the folder are offered to it, each read the same
way; `._` files are companions and are left out [ClassicMac].

### 11.2 Mac names from host names

[ClassicMac]:

- **Plain, AppleDouble without a Real Name, PC Exchange without a record:** each character becomes its Mac OS Roman
  byte (also accepting `¤` for $DB and the ohm sign U+2126 for $BD, from older mappings). `%XX` is **not** decoded.
- **Basilisk II:** `%` followed by two hex digits (either case) is that byte [Verified]; any other character up to
  U+00FF is the byte of the same value, and Windows-1252's characters for $80–$9F are those bytes (section 10.2).
  A `%` not followed by two hex digits stays `%`.
- A character with no byte becomes `?` (`host.name-unmappable`). A name over 255 bytes is cut to 255
  (`host.name-too-long`).

---

## 12. Writing a Mac file to the host

`HostFiles.Write` puts one Mac file into a host folder in one of two layouts [ClassicMac]. The options are the layout
(AppleDouble, the default, or Basilisk II), the longest path (200 characters), overwrite (off) and the time zone for
AppleDouble dates (the machine's).

| Layout | Files written, in order |
| --- | --- |
| AppleDouble | `name` (the data fork), `._name` (section 5.2) |
| Basilisk II | `name` (the data fork), `.rsrc/name` (only when the resource fork is non-empty), `.finf/name` (always: the 32 bytes of section 3) |

- The host name is the caller's, or made from the Mac name: portable (section 10.1) for AppleDouble, SheepShaver's
  (section 10.2) for Basilisk II.
- Before writing anything, every path about to be written is checked; if one exists and overwrite is off, nothing is
  written and the write fails.
- The target folder, and `.rsrc` and `.finf` as needed, are created.
- The data file's creation and modification times are set from the Mac dates, taken as the machine's local time
  (the historical offset for that date applies). Companion files keep the time they were written.
- Only files are written. No folder Finder info (`.finf/<folder>`) is written, so the emulator shows those folders
  without window information, dated when the host folder was made [Verified].

---

## 13. What unpack writes

`unpack` (and the app's Unpack commands) writes every Mac file inside an input — through containers, disk images and
folders — to a host folder, with both forks and Finder info, in the AppleDouble or Basilisk II layout. All of this
section is ClassicMac's own [ClassicMac], except where it follows the emulator [Verified].

### 13.1 The output folder

- CLI: `-o <folder>`, or by default `<input name without extension> unpacked` beside the input. The folder may
  already exist; files already there are not replaced unless `--overwrite` is given, and each one that is in the way
  is a failure (exit code for an I/O error).
- App: a new folder `<item's portable name> unpacked` in the chosen folder. If that name exists (file or folder),
  `<name> 2`, `<name> 3`, … is used instead, the first free one, so nothing is ever overwritten. `extract` makes its
  folders the same way, with ` resources`.

### 13.2 Placement

The input is a tree: each container node holds the files it read, and each file may be a container again. The output
folder stands for the input itself. Below it:

- A file of a volume goes into host folders for its Mac folders (its path inside the volume), each named with the
  layout's host naming. A Mac folder met again keeps the host name it was first given. Empty Mac folders are not
  written.
- A leaf (a file that is not a container) is written.
- A container whose tree ends in **one file and no folders** (MacBinary, BinHex, AppleSingle, a single-file archive)
  is replaced by that file: the file is written where the container would have been.
- A container that read **several files** (a disk image, an archive) becomes a folder named after the container
  file, and its files are placed inside it by these same rules.
- **Wrapper chains:** a container that read exactly one file, whose tree below holds several files or any folder (a
  disk image file → its NDIF image → its HFS volume), is passed through. The first container below it that read
  several files becomes the folder, named after the **outermost** container of the chain. So `Games/Inner.img`, an
  NDIF image of an HFS volume, becomes the folder `Games/Inner.img/` holding the volume's files. If the chain instead
  ends in a single file inside Mac folders, only those folders are made; nothing is named after the chain.
- An input with nothing inside is written as itself, at the top.

### 13.3 Names

For each file, in tree order:

1. The host name is made from the Mac name with the layout's naming (section 10), fitted to
   `max(8, longest path − length of the folder path below the output folder − 7)` characters. The folder path counts
   its separators; 7 leaves room for a companion prefix (`.rsrc/`, `._`). The output folder's own path is not
   counted. Folder names are not shortened.
2. **Basilisk II only:** if reading the host name back as SheepShaver would (section 11.2) does not give the Mac name
   — a replaced character, a trailing dot or space, a device name, or a shortened name — `unpack.name-changed` is
   reported. The file is still written under the changed name.
3. The name is made distinct within its folder (section 10.4). Folder names are taken first, so a file named like a
   sibling folder is the one numbered. A numbered name is reported as `unpack.name-collision`.

Names are kept distinct only among the files `unpack` writes; a name already on disk is a failure unless overwriting.

### 13.4 Results

Each file is written with section 12. A file that cannot be written (it exists, access is denied, a fork cannot be
read) is recorded as a failure with its path and the reason, and the others continue. The result counts the files
written and their data and resource fork bytes. The CLI prints `<files> files, <bytes> bytes, to <folder>` and exits
with the I/O error code when anything failed; the app lists failures as `export.failed`.

---

## 14. Diagnostics

Reading a host file, and unpacking. The AppleDouble reader's own codes (`applesingle.*`) are in
[CONTAINERS.md](CONTAINERS.md); the FAT reader's (`fat.*`) in [FAT.md](FAT.md).

| Code | Severity | Meaning | What the Mac or emulator does |
| --- | --- | --- | --- |
| `host.finf-length` | Warning | A `.finf` file is not 32 bytes. A short one is padded with zeros, a long one read for its first 32 bytes. | Not tested. |
| `host.appledouble-invalid` | Warning | A `._name` file is not an AppleDouble header (wrong magic or version, or under 26 bytes); it is ignored and the next layout tried. | No Mac OS code reads AppleDouble [Code]. |
| `host.name-unmappable` | Info | A host name has characters with no byte in the name's encoding (Mac OS Roman, or Windows-1252 for Basilisk II); they became `?`. | SheepShaver hands the Mac the Windows-1252 bytes; a character outside that code page was not tested. |
| `host.name-too-long` | Warning | A host name is over 255 bytes as a Mac name; cut to 255. | Not tested. |
| `unpack.name-changed` | Warning | Basilisk II layout: the host name will not read back as the Mac name in SheepShaver (a character or ending it cannot hold, a device name, or a shortened name). | SheepShaver cannot create such names itself: −43 for `/`, −48 for device names, trailing dots and spaces dropped [Verified]. |
| `unpack.name-collision` | Warning | The host name is taken in its folder (ignoring case); the file is written with ` ~N`. | The Mac refuses a duplicate name in a folder with −48 (`dupFNErr`) [Doc]; File Exchange does too [Verified]. |
| `export.failed` | Error | App only: a file or folder could not be written; the message holds the path and the reason. | — |

---

## 15. Not covered

- Writing PC Exchange / File Exchange data, `FILEID.DAT`, or FAT volumes.
- Folder Finder info in Basilisk II folders: read neither for folders on the host nor written by `unpack`.
- Dates of files in Basilisk II folders (the data file's host times) and of macOS named-fork files, which ClassicMac
  does not read; `com.apple.FinderInfo`.
- The locked flag from DOS read-only, and the backup date.
- `__MACOSX/` folders in zip archives, and netatalk's `.AppleDouble/` folders.
- Basilisk II and SheepShaver builds for other hosts, whose name conversion may differ from the Windows build.
- PC Exchange 2.x, which was not examined.
