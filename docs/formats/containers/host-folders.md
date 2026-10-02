# Mac files on other file systems — an implementer's specification

A classic Mac OS file has a name of up to 31 (HFS) or 255 bytes in Mac OS Roman, 32 bytes of Finder information,
dates in local time and two forks. Other file systems keep one stream of bytes and a name in their own character set,
so every tool that put Mac files on them kept the rest somewhere beside the file. This document describes those
layouts completely enough to read them, and to write the ones ClassicMac writes, without reading ClassicMac's code:
Basilisk II / SheepShaver shared folders, AppleDouble companion files, macOS named forks, and the `RESOURCE.FRK` /
`FINDER.DAT` data PC Exchange and File Exchange kept on DOS disks. It then specifies how ClassicMac turns Mac names
into host names and back, and what `unpack` writes.

The FAT volume itself (directory entries, VFAT long names, clusters) is in [fat.md](../file-systems/fat.md); the AppleDouble header
format in full is in [applesingle-appledouble.md](applesingle-appledouble.md). This document covers what lies on top of them.

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
7. [Host names](#7-host-names)
8. [Reading a host file](#8-reading-a-host-file)
9. [Writing a Mac file to the host](#9-writing-a-mac-file-to-the-host)
10. [What unpack writes](#10-what-unpack-writes)
11. [Diagnostics](#11-diagnostics)
12. [Not covered](#12-not-covered)

---

## 1. Conventions

The conventions and source tags of [README.md](../README.md) apply: big-endian values, offsets in hex, Mac dates as
`u32` seconds since 1904 in local time, and **[Doc]**, **[Code]**, **[Verified]**, **[Author]** and **[Fitted]** on
every rule. In this document:

- **[Author]** also covers the behaviour of the tool that defined a layout Apple never documented: Basilisk II /
  SheepShaver for shared folders, The Unarchiver for `name.rsrc`. Behaviour only; their code is GPL or LGPL.
- **[Verified]** without further detail means verified in SheepShaver's Windows build, Mac OS 9.0.
- **[ClassicMac]** marks a rule that is ClassicMac's own choice: how it picks among layouts, the names it makes and
  what `unpack` writes. It claims nothing about what a Mac did; it defines output that only ClassicMac specifies.
- **[Fitted?]** marks a rule whose source is uncertain.
- DOS dates and times ([pc-exchange.md §6.6](../file-systems/pc-exchange.md#66-dates)) are **little-endian**, as everything on a FAT volume is. Everything else is
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
| `F/.finf/name` | 32 bytes of Finder info (§3). |

- The companions carry exactly the data file's host name [Verified].
- A folder's `DInfo` and `DXInfo` (32 bytes) are in `.finf/<folder name>` in the **parent** folder, not inside the
  folder itself. SheepShaver writes it once the Finder has opened or moved the folder; folders the Finder never
  touched have none [Verified].
- A file with no `.finf` gets a type guessed by the emulator from its name ending: a `.txt` file shows as
  `TEXT`/`ttxt` [Verified]. ClassicMac leaves type and creator zero in that case [ClassicMac].
- A missing `.rsrc` file reads as an empty resource fork in the emulator [Verified]; ClassicMac writes none for an
  empty fork (§9) [ClassicMac].
- Resource forks written this way open normally through the Resource Manager in the emulator [Verified].

### 4.2 Dates

The layout has no place for dates: the emulator takes the file's creation and modification dates from the data
file's host times [Verified].

- SheepShaver (Windows) converts host times with the UTC offset **in force now**, not the one on the date itself:
  a date in the other half of the year from today comes back an hour off [Verified].
- A creation date before 1970 does not survive: 1904-01-02 read back as 2036-02-07 06:28:16 (a negative `time_t`
  taken as unsigned) [Verified].
- ClassicMac writes the data file's creation and modification times from the Mac dates, converted with the rules
  for that date (§9) [ClassicMac]. It does not read the host times back as Mac dates for this layout: a file
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
  therefore has no escape; it must be changed (§7.2).
- Bytes Windows-1252 leaves undefined ($81, $8D, $8F, $90, $9D) were not tested in the emulator. ClassicMac
  escapes them as `%XX`, expecting the emulator to decode them like any other escape [Fitted?].
- Other builds (Linux, macOS hosts) may convert names differently, for example to UTF-8; only the Windows build
  was checked [Fitted?].

---

## 5. AppleDouble companion files

An AppleDouble header file holds everything of a Mac file except its data fork, which is the plain file beside it.
The header format (magic `$00051607`, versions 1 and 2, entry table) is Apple's [Doc] and is specified in
[applesingle-appledouble.md](applesingle-appledouble.md). No Mac OS 7.1–9 file-system code reads or writes AppleSingle or AppleDouble; on
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
  (§8.2) [ClassicMac].
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
| +$5A+`n` | 32 | bytes | Finder info (§3) |
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

## 7. Host names

ClassicMac makes host names from Mac names in two ways: a **portable** name valid on Windows, macOS and Linux, used
for AppleDouble output and by `extract`, and **SheepShaver's** naming for Basilisk II folders. Both work on the Mac
name's bytes.

### 7.1 Portable names

[ClassicMac], with the forbidden characters and device names from Microsoft's naming rules [Doc]:

1. Each Mac OS Roman byte becomes its Unicode character (`ROMAN.TXT`; $DB is `€`, $F0 the Apple logo U+F8FF).
2. These become `%` and two upper-case hex digits of the **Mac OS Roman byte**: bytes below $20, $7F,
   `% / \ : * ? " < > |`, and a final space or dot.
3. A Windows device name — `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, ignoring case, as the part
   before the first dot with trailing spaces ignored — gets the last character of that part escaped.
4. An empty name is `%00`.
5. The name is fitted to the length limit (§7.3).

| Mac name | Host name | Mac name | Host name |
| --- | --- | --- | --- |
| `a/b` | `a%2Fb` | `COM1` | `COM%31` |
| `100% done?` | `100%25 done%3F` | `con.txt` | `co%6E.txt` |
| `Icon` + CR | `Icon%0D` | `Résumé ƒ` | `Résumé ƒ` |
| `trail.` | `trail%2E` | `space ` | `space%20` |

The escapes are one-way: a reader does not decode `%XX` in a portable name (§8.2). AppleDouble output keeps
the exact Mac name in its Real Name entry.

### 7.2 SheepShaver names

For Basilisk II folders ClassicMac writes what SheepShaver on Windows would write, so the emulator reads the Mac
name back, and changes only what it cannot hold [Verified] (§4.3) [ClassicMac] (the replacements):

1. Each byte becomes the Windows-1252 character with the same value (bytes $00–$7F and $A0–$FF are the same code
   point; $80–$9F are Windows-1252's `€ ‚ ƒ „ … † ‡ ˆ ‰ Š ‹ Œ Ž ‘ ’ “ ” • – — ˜ ™ š › œ ž Ÿ`).
2. These become `%XX` (the byte): `% ? * " < > |`, bytes below $20, $7F, and the bytes Windows-1252 leaves undefined
   ($81, $8D, $8F, $90, $9D).
3. `/`, `\` and `:` become `_`. No escape can be used: the emulator would decode it back to the character [Verified].
4. Every trailing space or dot becomes `_` (`dots..` → `dots__`).
5. A Windows device name (as in §7.1, checked after step 4) gets `_` appended to the part before the first dot
   (`COM1` → `COM1_`, `con.txt` → `con_.txt`). `CLOCK$` is not a device name for this purpose [Verified].
6. An empty name is `_`.
7. The name is fitted to the length limit (§7.3).

Steps 3–6 change the name the Mac will see; `unpack` warns when they do (§10.3).

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

### 7.3 Length

[ClassicMac]: a name is limited to a number of UTF-16 characters (255 unless the caller gives less; never less
than 8). When it is longer:

- The name is treated as a list of parts: a character, or a whole `%XX` escape. A part is never split.
- The extension is the last `.` part and what follows it, if that is at most 5 parts and the dot is not first.
- Parts of the stem are kept from the start while they fit in the limit less the extension's length; trailing spaces
  and dots of the kept stem are removed; the extension is appended.

For example `aaaa…a?.sit` (40 `a`) with a limit of 20 is `aaaaaaaaaaaaaaaa.sit`.

### 7.4 Distinct names

[ClassicMac]: within one output folder, names are kept distinct **ignoring case**, since Windows and macOS disks
ignore it. A name already taken gets ` ~2`, ` ~3`, … before its extension (the text from the last dot, unless the dot
is first): `DUP.TXT`, `DUP ~2.TXT`, `dup ~3.txt`. The numbered name is not fitted to the length limit again.

---

## 8. Reading a host file

### 8.1 Choosing the layout

ClassicMac reads a host file `F/name` with the first layout that applies [ClassicMac]:

1. **PC Exchange:** `F` holds a `RESOURCE.FRK` folder or a `FINDER.DAT` file (any case), `name` is not `FINDER.DAT`,
   and either `RESOURCE.FRK/name` exists or `FINDER.DAT` has a record for `name` ([pc-exchange.md §6.5](../file-systems/pc-exchange.md#65-finding-an-items-record)). The record gives the
   Mac name, Finder info and dates ([pc-exchange.md §6.6](../file-systems/pc-exchange.md#66-dates)); the map applies if supplied ([pc-exchange.md §8](../file-systems/pc-exchange.md#8-the-extension-map)); DOS hidden or system
   makes it invisible.
2. **Basilisk II:** `F/.rsrc/name` or `F/.finf/name` exists. A `.finf` that is not 32 bytes is reported
   (`host.finf-length`) and read as far as it goes.
3. **AppleDouble:** `F/._name` is a valid AppleDouble header (otherwise `host.appledouble-invalid`).
4. **AppleDouble, visible:** `F/name.rsrc` is a valid AppleDouble header.
5. **macOS named fork:** on macOS only, `name/..namedfork/rsrc` is non-empty.
6. **Plain:** the data fork only.

When a format is split across several files, the other files of the folder are offered to it, each read the same
way; `._` files are companions and are left out [ClassicMac].

### 8.2 Mac names from host names

[ClassicMac]:

- **Plain, AppleDouble without a Real Name, PC Exchange without a record:** each character becomes its Mac OS Roman
  byte (also accepting `¤` for $DB and the ohm sign U+2126 for $BD, from older mappings). `%XX` is **not** decoded.
- **Basilisk II:** `%` followed by two hex digits (either case) is that byte [Verified]; any other character up to
  U+00FF is the byte of the same value, and Windows-1252's characters for $80–$9F are those bytes (§7.2).
  A `%` not followed by two hex digits stays `%`.
- A character with no byte becomes `?` (`host.name-unmappable`). A name over 255 bytes is cut to 255
  (`host.name-too-long`).

---

## 9. Writing a Mac file to the host

`HostFiles.Write` puts one Mac file into a host folder in one of two layouts [ClassicMac]. The options are the layout
(AppleDouble, the default, or Basilisk II), the longest path (200 characters), overwrite (off) and the time zone for
AppleDouble dates (the machine's).

| Layout | Files written, in order |
| --- | --- |
| AppleDouble | `name` (the data fork), `._name` (§5.2) |
| Basilisk II | `name` (the data fork), `.rsrc/name` (only when the resource fork is non-empty), `.finf/name` (always: the 32 bytes of §3) |

- The host name is the caller's, or made from the Mac name: portable (§7.1) for AppleDouble, SheepShaver's
  (§7.2) for Basilisk II.
- Before writing anything, every path about to be written is checked; if one exists and overwrite is off, nothing is
  written and the write fails.
- The target folder, and `.rsrc` and `.finf` as needed, are created.
- The data file's creation and modification times are set from the Mac dates, taken as the machine's local time
  (the historical offset for that date applies). Companion files keep the time they were written.
- Only files are written. No folder Finder info (`.finf/<folder>`) is written, so the emulator shows those folders
  without window information, dated when the host folder was made [Verified].

---

## 10. What unpack writes

`unpack` (and the app's Unpack commands) writes every Mac file inside an input — through containers, disk images and
folders — to a host folder, with both forks and Finder info, in the AppleDouble or Basilisk II layout. All of this
section is ClassicMac's own [ClassicMac], except where it follows the emulator [Verified].

### 10.1 The output folder

- CLI: `-o <folder>`, or by default `<input name without extension> unpacked` beside the input. The folder may
  already exist; files already there are not replaced unless `--overwrite` is given, and each one that is in the way
  is a failure (exit code for an I/O error).
- App: a new folder `<item's portable name> unpacked` in the chosen folder. If that name exists (file or folder),
  `<name> 2`, `<name> 3`, … is used instead, the first free one, so nothing is ever overwritten. `extract` makes its
  folders the same way, with ` resources`.

### 10.2 Placement

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

### 10.3 Names

For each file, in tree order:

1. The host name is made from the Mac name with the layout's naming (§7), fitted to
   `max(8, longest path − length of the folder path below the output folder − 7)` characters. The folder path counts
   its separators; 7 leaves room for a companion prefix (`.rsrc/`, `._`). The output folder's own path is not
   counted. Folder names are not shortened.
2. **Basilisk II only:** if reading the host name back as SheepShaver would (§8.2) does not give the Mac name
   — a replaced character, a trailing dot or space, a device name, or a shortened name — `unpack.name-changed` is
   reported. The file is still written under the changed name.
3. The name is made distinct within its folder (§7.4). Folder names are taken first, so a file named like a
   sibling folder is the one numbered. A numbered name is reported as `unpack.name-collision`.

Names are kept distinct only among the files `unpack` writes; a name already on disk is a failure unless overwriting.

### 10.4 Results

Each file is written with §9. A file that cannot be written (it exists, access is denied, a fork cannot be
read) is recorded as a failure with its path and the reason, and the others continue. The result counts the files
written and their data and resource fork bytes. The CLI prints `<files> files, <bytes> bytes, to <folder>` and exits
with the I/O error code when anything failed; the app lists failures as `export.failed`.

---

## 11. Diagnostics

Reading a host file, and unpacking. The AppleDouble reader's own codes (`applesingle.*`) are in
[applesingle-appledouble.md](applesingle-appledouble.md); the FAT reader's (`fat.*`) in [fat.md](../file-systems/fat.md).

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

## 12. Not covered

- Folder Finder info in Basilisk II folders: read neither for folders on the host nor written by `unpack`.
- Dates of files in Basilisk II folders (the data file's host times) and of macOS named-fork files, which ClassicMac
  does not read; `com.apple.FinderInfo`.
- `__MACOSX/` folders in zip archives, and netatalk's `.AppleDouble/` folders.
- Basilisk II and SheepShaver builds for other hosts, whose name conversion may differ from the Windows build.
