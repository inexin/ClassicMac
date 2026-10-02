# Mac files on other file systems

A classic Mac OS file has a name of up to 31 (HFS) or 255 bytes in Mac OS Roman, 32 bytes of Finder information,
dates in local time and two forks. Other file systems keep one stream of bytes and a name in their own character set,
so every tool that put Mac files on them kept the rest somewhere beside the file: Basilisk II and SheepShaver shared
folders, AppleDouble companion files, macOS named forks, and the `RESOURCE.FRK` / `FINDER.DAT` data PC Exchange and
File Exchange kept on DOS disks. ClassicMac reads all of these from the host's disk, writes the AppleDouble and
Basilisk II layouts, and uses them for what `unpack` writes. This file also gives how ClassicMac turns Mac names into
host names and back.

| | |
| --- | --- |
| Identified by | Companion files beside the host file: `.rsrc/name` and `.finf/name`; `._name` or `name.rsrc` holding an AppleDouble header; `RESOURCE.FRK` and `FINDER.DAT`; on macOS, `name/..namedfork/rsrc` |
| ClassicMac | Reads all layouts, writes AppleDouble and Basilisk II; `ClassicMac.Files` (`HostFiles`, `HostWriteOptions`), `ClassicMac.Core` (`HostNames`), `ClassicMac.Files.Export` (`Unpacker`, `OutputLayout`) |
| Verified against | SheepShaver's Windows build, Mac OS 9.0 (shared folders and their names) |
| Sources | *Inside Macintosh: Files*; the AppleSingle/AppleDouble Developer Note; Microsoft's naming rules; Basilisk II and SheepShaver, The Unarchiver and macOS (behaviour only) |

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

"Host" is the computer ClassicMac runs on; a "host name" is a file name on its disk (UTF-16 on Windows and .NET).
Every layout that keeps Finder information keeps the 32 bytes of
[unwrapping.md §1.1](unwrapping.md#11-finder-information): `FInfo` and `FXInfo` for a file, `DInfo` and `DXInfo` for a
folder. [Doc]

### 1.1 Overview

| Layout | Data fork | Resource fork | Finder info and dates | Written by |
| --- | --- | --- | --- | --- |
| Plain | the file | none | none | anything |
| Basilisk II folder | `name` | `.rsrc/name` | `.finf/name`; dates are the data file's host times | the emulators; ClassicMac |
| AppleDouble pair | `name` | in `._name` | in `._name` | macOS, netatalk, ClassicMac |
| AppleDouble, visible | `name` | in `name.rsrc` | in `name.rsrc` | The Unarchiver |
| macOS named fork | `name` | `name/..namedfork/rsrc` | extended attribute | macOS |
| PC Exchange folder | `NAME.EXT` | `RESOURCE.FRK/NAME.EXT` | a record in `FINDER.DAT`; DOS dates | PC Exchange, File Exchange |

The resource fork is always stored raw, with no header, except inside an AppleDouble header file, where it is one
entry [Code] [Verified] [Doc]. The PC Exchange layout, its records and its little-endian DOS dates are in
[pc-exchange.md](../file-systems/pc-exchange.md); the FAT volume itself in [fat.md](../file-systems/fat.md).

### 1.2 Basilisk II and SheepShaver shared folders

Basilisk II and SheepShaver show a folder of the host as a Mac volume. The data fork is the host file itself; the
rest lives in two hidden folders beside it. For a file `name` in host folder `F` [Verified]:

| Path | Size | Content | Notes |
| --- | --- | --- | --- |
| `F/name` | any | the data fork, raw | a file with an empty data fork is an empty host file [Verified] |
| `F/.rsrc/name` | any | the resource fork, raw, no header | missing means an empty fork: the emulator reads it so [Verified] |
| `F/.finf/name` | 32 | the Finder information ([unwrapping.md §1.1](unwrapping.md#11-finder-information)) | [Verified] |

- The companions carry exactly the data file's host name. [Verified]
- A folder's `DInfo` and `DXInfo` (32 bytes) are in `.finf/<folder name>` in the **parent** folder, not inside the
  folder itself. SheepShaver writes it once the Finder has opened or moved the folder; folders the Finder never
  touched have none. [Verified] A folder without it shows without window information, dated when the host folder was
  made. [Verified]
- A file with no `.finf` gets a type guessed by the emulator from its name ending: a `.txt` file shows as
  `TEXT`/`ttxt`. [Verified]
- Resource forks written this way open normally through the Resource Manager in the emulator. [Verified]

### 1.3 Basilisk II dates

The layout has no place for dates: the emulator takes the file's creation and modification dates from the data
file's host times. [Verified]

- SheepShaver (Windows) converts host times with the UTC offset **in force when it reads them**, not the one on the date itself:
  a date in the other half of the year from today comes back an hour off. [Verified]
- A creation date before 1970 does not survive: 1904-01-02 read back as 2036-02-07 06:28:16 (a negative `time_t`
  taken as unsigned). [Verified]

### 1.4 Basilisk II names

In SheepShaver's Windows build the Mac name and the host name are the same bytes [Verified]:

- Each Mac OS Roman byte becomes the **Windows-1252 character with the same byte value**. No Unicode conversion is
  done: Mac `é` ($8E) is host `Ž` (Windows-1252 $8E, U+017D), and a host `é` (U+00E9, byte $E9) shows on the Mac as
  `È`.
- Exactly seven characters are escaped when the Mac creates a name, as `%` and two hex digits of the byte:
  `%` → `%25`, `?` → `%3F`, `*` → `%2A`, `"` → `%22`, `<` → `%3C`, `>` → `%3E`, `|` → `%7C`. A `%` is always
  escaped, even where it already looks like an escape (`x%41y` → `x%2541y`).
- When reading, **any** `%XX` in a host name is decoded to the byte `$XX`, whether or not the emulator would have
  escaped it (`Icon%0D` is the Mac name `Icon` + CR).
- The emulator does not escape `/`, `\`, a trailing dot or space, or Windows device names. When the Mac creates such
  a name:

  | Mac name | Host result | Mac sees |
  | --- | --- | --- |
  | `trail.`, `space `, `dot..` | `trail`, `space`, `dot` (Windows drops the ending) | the renamed file |
  | `a/b` | an empty file `a`; the create returns −43 | `a` |
  | `back\slash` | a folder `back` holding `slash` | a folder `back` |
  | `con.txt`, `COM1`, `nul.txt`, `aux`, `lpt1.txt` | nothing; the create returns −48 | nothing |
  | `CLOCK$` | `CLOCK$` | `CLOCK$` |

- Host names written with `%2E`, `%20` or a device name escaped as `%XX` decode back to the forbidden name, and the
  file is then missing (`trail%2E`, `space%20`) or reads as an empty device (`co%6E.txt`, `COM%31`: both forks
  0 bytes, location −1,−1, date 0, type `TEXT`/`ttxt` or `????`/`????`). A name the emulator cannot hold therefore
  has no escape; it must be changed (§3.2).
- Bytes Windows-1252 leaves undefined ($81, $8D, $8F, $90, $9D) were not tested in the emulator (§8).

### 1.5 AppleDouble companion files

An AppleDouble header file holds everything of a Mac file except its data fork, which is the plain file beside it.
The header format (magic `$00051607`, versions 1 and 2, entry table) is Apple's [Doc] and is in
[applesingle-appledouble.md](applesingle-appledouble.md). No Mac OS 7.1–9 file-system code reads or writes it
([applesingle-appledouble.md §4](applesingle-appledouble.md#4-variants)) [Code].

| Form | Header file | Source |
| --- | --- | --- |
| Hidden | `._name` beside `name` | macOS's convention on volumes without forks [Fitted] |
| Visible | `name.rsrc` beside `name` | The Unarchiver with `unar -k visible` [Reference: The Unarchiver] |

- The data fork is the host file. [Doc]
- Version 2 File Dates Info holds UTC dates; Mac dates are local
  ([applesingle-appledouble.md §2](applesingle-appledouble.md#2-reading), step 6). [Doc]
- macOS's own `._` files may carry more than 32 bytes in the Finder Info entry (extended attributes follow); only the
  first 32 are Finder information. [Fitted]

### 1.6 macOS named forks

On macOS the resource fork of `path` can be read as the file `path/..namedfork/rsrc`. macOS also keeps the 32 bytes
of Finder information as the extended attribute `com.apple.FinderInfo` (and the resource fork as
`com.apple.ResourceFork`). [Fitted]

## 2. Reading

### 2.1 Choosing the layout

A host file `F/name` is read with the first layout that applies [ClassicMac]:

1. **PC Exchange:** `F` holds a `RESOURCE.FRK` folder or a `FINDER.DAT` file (any case), `name` is not `FINDER.DAT`,
   and either `RESOURCE.FRK/name` exists or `FINDER.DAT` has a record for `name`
   ([pc-exchange.md §6.5](../file-systems/pc-exchange.md#65-finding-an-items-record)). The record gives the Mac name,
   Finder information and dates ([pc-exchange.md §6.6](../file-systems/pc-exchange.md#66-dates)); the extension map
   applies if supplied ([pc-exchange.md §8](../file-systems/pc-exchange.md#8-the-extension-map)); DOS hidden or system
   makes the file invisible.
2. **Basilisk II:** `F/.rsrc/name` or `F/.finf/name` exists. An empty `.rsrc` file is an empty fork. A `.finf` that is
   not 32 bytes is reported (`host.finf-length`, Warning) and read as far as it goes: a short one padded with zeros, a
   long one read for its first 32 bytes.
3. **AppleDouble:** `F/._name` starts with the AppleDouble magic and version 1 or 2 (and has the 26-byte header).
   Anything else is ignored with `host.appledouble-invalid` (Warning), and the next layout is tried.
4. **AppleDouble, visible:** `F/name.rsrc` is a valid AppleDouble header. Otherwise it is ignored silently, since
   plenty of `.rsrc` files are raw forks or other data. `._name` wins when both exist.
5. **macOS named fork:** on macOS only, `name/..namedfork/rsrc` is non-empty.
6. **Plain:** the data fork only.

For the AppleDouble layouts, the header is read by
[applesingle-appledouble.md §2](applesingle-appledouble.md#2-reading), with the host name (§2.2) as the name when it
has no Real Name entry. The data fork is always the host file: a Data Fork entry in the header is reported by that
reader and then replaced by the host file.

When a format is split across several files, the other files of the folder are offered to it
([unwrapping.md §2.3](unwrapping.md#23-what-a-reader-is-given)), each read the same way; `._` files are companions
and are left out. [ClassicMac]

### 2.2 Mac names from host names

[ClassicMac]:

- **Plain, AppleDouble without a Real Name, PC Exchange without a record:** each character becomes its Mac OS Roman
  byte (also accepting `¤` for $DB and the ohm sign U+2126 for $BD, from older mappings). `%XX` is **not** decoded.
- **Basilisk II:** `%` followed by two hex digits (either case) is that byte, as the emulator reads it (§1.4)
  [Verified]; any other character up to U+00FF is the byte of the same value, and Windows-1252's characters for
  $80–$9F are those bytes (§3.2). A `%` not followed by two hex digits stays `%`.
- A character with no byte becomes `?` (`host.name-unmappable`, Info). A name over 255 bytes is cut to 255
  (`host.name-too-long`, Warning).

## 3. Writing

ClassicMac writes two layouts, AppleDouble and Basilisk II, and makes host names from Mac names in two ways: a
**portable** name valid on Windows, macOS and Linux, used for AppleDouble output and by `extract`, and
**SheepShaver's** naming for Basilisk II folders. Both work on the Mac name's bytes. All of this section is
ClassicMac's own [ClassicMac], except where it follows the emulator [Verified].

### 3.1 Portable names

With the forbidden characters and device names from Microsoft's naming rules [Doc]:

1. Each Mac OS Roman byte becomes its Unicode character (`ROMAN.TXT`; $DB is `€`, $F0 the Apple logo U+F8FF).
2. These become `%` and two upper-case hex digits of the **Mac OS Roman byte**: bytes below $20, $7F,
   `% / \ : * ? " < > |`, and a final space or dot.
3. A Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, ignoring case, as the part before
   the first dot with trailing spaces ignored) gets the last character of that part escaped.
4. An empty name is `%00`.
5. The name is fitted to the length limit (§3.3).

| Mac name | Host name | Mac name | Host name |
| --- | --- | --- | --- |
| `a/b` | `a%2Fb` | `COM1` | `COM%31` |
| `100% done?` | `100%25 done%3F` | `con.txt` | `co%6E.txt` |
| `Icon` + CR | `Icon%0D` | `Résumé ƒ` | `Résumé ƒ` |
| `trail.` | `trail%2E` | `space ` | `space%20` |
| `Scenario:Data` | `Scenario%3AData` | `Read Me` | `Read Me` |

The escapes are one-way: a reader does not decode `%XX` in a portable name (§2.2). AppleDouble output keeps the exact
Mac name in its Real Name entry.

### 3.2 SheepShaver names

For Basilisk II folders, the host name is what SheepShaver on Windows would write, so the emulator reads the Mac name
back (§1.4) [Verified], changing only what it cannot hold:

1. Each byte becomes the Windows-1252 character with the same value (bytes $00–$7F and $A0–$FF are the same code
   point; $80–$9F are Windows-1252's `€ ‚ ƒ „ … † ‡ ˆ ‰ Š ‹ Œ Ž ‘ ’ “ ” • – — ˜ ™ š › œ ž Ÿ`).
2. These become `%XX` (the byte): `% ? * " < > |`, bytes below $20, $7F, and the bytes Windows-1252 leaves undefined
   ($81, $8D, $8F, $90, $9D), expecting the emulator to decode them like any other escape (§8).
3. `/`, `\` and `:` become `_`. No escape can be used: the emulator would decode it back to the character. [Verified]
4. Every trailing space or dot becomes `_` (`dots..` → `dots__`).
5. A Windows device name (as in §3.1, checked after step 4) gets `_` appended to the part before the first dot
   (`COM1` → `COM1_`, `con.txt` → `con_.txt`). `CLOCK$` is not a device name for this purpose. [Verified]
6. An empty name is `_`.
7. The name is fitted to the length limit (§3.3).

Steps 3–6 change the name the Mac will see; `unpack` warns when they do (§3.8).

| Mac name | Host name | Same name on the Mac |
| --- | --- | --- |
| `Résumé` | `RŽsumŽ` | yes |
| `50% done?` | `50%25 done%3F` | yes |
| `star* <q"> pipe|` | `star%2A %3Cq%22%3E pipe%7C` | yes |
| `Icon` + CR | `Icon%0D` | yes |
| `CLOCK$` | `CLOCK$` | yes |
| bytes $41 $81 $8D $42 | `A%81%8DB` | yes |
| `a/b`, `back\slash` | `a_b`, `back_slash` | no |
| `trail.`, `dots..`, `space ` | `trail_`, `dots__`, `space_` | no |
| `COM1`, `con.txt` | `COM1_`, `con_.txt` | no |

### 3.3 Length

A name is limited to a number of UTF-16 characters (§5). When it is longer:

1. The name is treated as a list of parts: a character, or a whole `%XX` escape. A part is never split.
2. The extension is the last `.` part and what follows it, if that is at most 5 parts and the dot is not first.
3. Parts of the stem are kept from the start while they fit in the limit less the extension's length.
4. Trailing spaces and dots of the kept stem are removed, since Windows drops them; the extension is appended.

`aaaa…a?.sit` (40 `a`) with a limit of 20 is `aaaaaaaaaaaaaaaa.sit`; 30 `a` with a limit of 20 is 20 `a`.

### 3.4 Distinct names

Within one output folder, names are kept distinct **ignoring case**, since Windows and macOS disks ignore it. A name
already taken gets ` ~2`, ` ~3`, … before its extension (the text from the last dot, unless the dot is first):
`DUP.TXT`, `DUP ~2.TXT`, `dup ~3.txt`. The numbered name is not fitted to the length limit again.

### 3.5 Writing a Mac file

One Mac file goes into a host folder in one of two layouts:

| Layout | Files written, in order |
| --- | --- |
| AppleDouble | `name` (the data fork), `._name` ([applesingle-appledouble.md §3.1](applesingle-appledouble.md#31-appledouble-header-file)) |
| Basilisk II | `name` (the data fork), `.rsrc/name` (only when the resource fork is non-empty), `.finf/name` (always: the 32 bytes of Finder information) |

1. The host name is the caller's, or made from the Mac name: portable (§3.1) for AppleDouble, SheepShaver's (§3.2)
   for Basilisk II.
2. Before writing anything, every path about to be written is checked; if one exists and overwriting is off, nothing
   is written and the write fails.
3. The target folder, and `.rsrc` and `.finf` as needed, are created.
4. The data file's creation and modification times are set from the Mac dates, taken as the machine's local time (the
   historical offset for that date applies). Companion files keep the time they were written.
5. Only files are written. No folder Finder information (`.finf/<folder>`) is written, so the emulator shows those
   folders without window information, dated when the host folder was made. [Verified]

### 3.6 Unpack: the output folder

`unpack` (and the app's Unpack commands) writes every Mac file inside an input (through containers, disk images and
folders) to a host folder, with both forks and Finder information, in the AppleDouble or Basilisk II layout.

- CLI: `-o <folder>`, or by default `<input name without extension> unpacked` beside the input. The folder may
  already exist; files already there are not replaced unless `--overwrite` is given, and each one in the way is a
  failure (the exit code for an I/O error).
- App: a new folder `<item's portable name> unpacked` in the chosen folder. If that name exists (file or folder),
  `<name> 2`, `<name> 3`, … is used instead, the first free one, so nothing is ever overwritten. `extract` makes its
  folders the same way, with ` resources`.

### 3.7 Unpack: placement

The input is a tree ([unwrapping.md §2](unwrapping.md#2-reading)): each container node holds the files it read, and
each file may be a container again. The output folder stands for the input itself. Below it:

- A file of a volume goes into host folders for its Mac folders (its path inside the volume), each named with the
  layout's host naming. A Mac folder met again keeps the host name it was first given. Empty Mac folders are not
  written.
- A leaf (a file that is not a container) is written.
- A container whose tree ends in **one file and no folders** (MacBinary, BinHex, AppleSingle, a single-file archive)
  is replaced by that file: the file is written where the container would have been.
- A container that read **several files** (a disk image, an archive) becomes a folder named after the container file,
  and its files are placed inside it by these same rules.
- **Wrapper chains:** a container that read exactly one file, whose tree below holds several files or any folder (a
  disk image file → its NDIF image → its HFS volume), is passed through. The first container below it that read
  several files becomes the folder, named after the **outermost** container of the chain. So `Games/Inner.img`, an
  NDIF image of an HFS volume, becomes the folder `Games/Inner.img/` holding the volume's files. If the chain instead
  ends in a single file inside Mac folders, only those folders are made; nothing is named after the chain.
- An input with nothing inside is written as itself, at the top.

### 3.8 Unpack: names

For each file, in tree order:

1. The host name is made from the Mac name with the layout's naming (§3.1, §3.2), fitted to
   `max(8, longest path − length of the folder path below the output folder − 7)` characters. The folder path counts
   its separators; 7 leaves room for a companion prefix (`.rsrc/`, `._`). The output folder's own path is not
   counted. Folder names are not shortened.
2. **Basilisk II only:** if reading the host name back as SheepShaver would (§2.2) does not give the Mac name (a
   replaced character, a trailing dot or space, a device name, or a shortened name), `unpack.name-changed` (Warning)
   is reported. The file is still written under the changed name.
3. The name is made distinct within its folder (§3.4). Folder names are taken first, so a file named like a sibling
   folder is the one numbered. A numbered name is reported as `unpack.name-collision` (Warning).

Names are kept distinct only among the files `unpack` writes; a name already on disk is a failure unless overwriting.

### 3.9 Unpack: results

Each file is written by §3.5. A file that cannot be written (it exists, access is denied, a fork cannot be read) is
recorded as a failure with its path and the reason, and the others continue. The result counts the files written and
their data and resource fork bytes. The CLI prints `<files> files, <bytes> bytes, to <folder>` and exits with the I/O
error code when anything failed; the app lists failures as `export.failed` (Error).

## 4. Variants

- **Emulator builds.** Only SheepShaver's Windows build was checked. Builds for other hosts (Linux, macOS) may convert
  names differently, for example to UTF-8 (§8).
- **AppleDouble forms.** Hidden (`._name`) and visible (`name.rsrc`) hold the same header (§1.5).

## 5. ClassicMac

- `HostFiles.Read` reads a host file by §2 into a `HostFile`: the Mac file, its `HostLayout` and the companion paths.
  `HostFiles.Siblings` gives the other files of its folder (§2.1). The layouts' names in a container chain:
  `Basilisk II folder`, `AppleDouble pair`, `macOS named fork`, `PC Exchange folder`, `host file`. [ClassicMac]
- A file read from a Basilisk II folder has no dates: the data file's host times are not read back as Mac dates. A
  file with no `.finf` keeps type and creator zero, where the emulator would guess them (§1.2). [ClassicMac]
- macOS named forks are read only on macOS and only when non-empty. The `com.apple.FinderInfo` attribute is not read,
  so such a file has the resource fork but no type, creator, flags or dates; the layout is never written.
  [ClassicMac]
- An AppleDouble header's dates are read with `ContainerReadOptions.TimeZone`
  ([unwrapping.md §5](unwrapping.md#5-classicmac)). [ClassicMac]
- `HostFiles.Write` writes §3.5 with `HostWriteOptions` [ClassicMac]:

  | Option | Default | Use |
  | --- | --- | --- |
  | `Layout` | `AppleDouble` | `AppleDouble` or `BasiliskII`; any other layout is refused |
  | `MaxPathLength` | 200 | the longest path, counted from the folder written into (§3.8) |
  | `Overwrite` | false | whether existing files may be replaced (§3.5 step 2) |
  | `TimeZone` | the machine's | the zone Mac dates are taken to be in for the AppleDouble header's UTC dates |

- `HostNames.ToHostName` makes portable names (§3.1), `HostNames.ToBasiliskName` SheepShaver's (§3.2), both with a
  length limit of 255 unless the caller gives less; a limit under 8 is refused. `HostNames.MakeUnique` keeps names
  distinct (§3.4). [ClassicMac]
- `Unpacker.Unpack` writes §3.6–§3.9, placing files with `OutputLayout`; `ExportFolders.CreateNew` makes the app's
  numbered folders. The CLI's `unpack` takes `--layout appledouble|basilisk`, `-o` and `--overwrite`. [ClassicMac]

## 6. Diagnostics

Reading a host file, and unpacking. The AppleDouble reader's own codes (`applesingle.*`) are in
[applesingle-appledouble.md §6](applesingle-appledouble.md#6-diagnostics); the FAT and PC Exchange codes in
[fat.md](../file-systems/fat.md) and [pc-exchange.md](../file-systems/pc-exchange.md).

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `export.failed` | Error | app only: a file or folder could not be written | lists the path and the reason; the other files are written | — |
| `host.appledouble-invalid` | Warning | a `._name` file is not an AppleDouble header (wrong magic or version, or under 26 bytes) | ignores it and tries the next layout | no Mac OS code reads AppleDouble [Code] |
| `host.finf-length` | Warning | a `.finf` file is not 32 bytes | pads a short one with zeros, reads a long one's first 32 bytes | not tested |
| `host.name-too-long` | Warning | a host name is over 255 bytes as a Mac name | cuts it to 255 | not tested |
| `host.name-unmappable` | Info | a host name has characters with no byte in the name's encoding (Mac OS Roman, or Windows-1252 for Basilisk II) | they become `?` | SheepShaver hands the Mac the Windows-1252 bytes; a character outside that code page was not tested |
| `unpack.name-changed` | Warning | Basilisk II layout: the host name will not read back as the Mac name in SheepShaver (a character or ending it cannot hold, a device name, or a shortened name) | writes the file under the changed name | SheepShaver cannot create such names itself: −43 for `/`, −48 for device names, trailing dots and spaces dropped [Verified] |
| `unpack.name-collision` | Warning | the host name is taken in its folder (ignoring case) | writes the file with ` ~N` | the Mac refuses a duplicate name in a folder with −48 (`dupFNErr`) [Doc]; File Exchange does too [Verified] |

## 7. Verification

Every [Verified] rule of §1.2–§1.4, §3.2 and §3.5 was checked in SheepShaver's Windows build running Mac OS 9.0:
files and folders created on the Mac and read on the host, and host files read on the Mac.

- `tests/ClassicMac.Files.Tests/UnwrapTests.cs`: `Basilisk_II_folders_give_Finder_info_and_the_resource_fork`,
  `Basilisk_II_names_map_through_Windows_1252`, `AppleDouble_pairs_join_the_header_file`,
  `AppleDouble_files_named_dot_rsrc_join_too`, `Plain_files_have_only_a_data_fork`,
  `Unmappable_and_long_names_are_reported` (§2). `Corpus_host_files_unwrap` reads the real-file corpus named by
  `CLASSICMAC_CORPUS` (outside the repo), counting Basilisk II folders; it is skipped without it.
- `tests/ClassicMac.Files.Tests/HostWriteTests.cs`: `Finder_info_round_trips`, `Written_files_read_back_the_same`
  (both layouts, dates), `Basilisk_names_follow_SheepShaver` (the table of §3.2, read back by §2.2),
  `Bytes_Windows_1252_lacks_are_escaped_for_Basilisk`, `Basilisk_folders_get_no_resource_file_for_an_empty_fork`,
  `Existing_files_are_kept_unless_overwriting`, `AppleDouble_headers_read_back`, `Export_folders_are_new_and_numbered`.
- `tests/ClassicMac.Core.Tests/HostNamesTests.cs`: `Mac_names_become_safe_host_names` (§3.1),
  `Long_names_are_cut_before_the_extension` (§3.3), `Colliding_names_get_numbers` (§3.4).
- `tests/ClassicMac.Resources.Cli.Tests/UnpackTests.cs`: `Disks_unpack_to_folders_that_read_back` (an HFS disk with
  an NDIF image in a folder: placement, names and `unpack.name-changed`, in both layouts),
  `Existing_files_are_kept_unless_overwriting`, and `Corpus_images_unpack_and_read_back` (the corpus's disk images,
  skipped without `CLASSICMAC_CORPUS`).
- `tests/ClassicMac.Files.Tests/PcExchangeTests.cs`: the PC Exchange layout (§2.1 step 1).
- `tests/ClassicMac.App.Tests/ExportTests.cs`: `A_folder_unpacks_only_its_files`, `A_file_unpacks_alone_as_AppleDouble`;
  `tests/ClassicMac.App.Tests/DragOutTests.cs`: `A_file_drags_out_as_its_data_fork_and_an_AppleDouble_header`.

## 8. Not covered

- Folder Finder information in Basilisk II folders: neither read for folders on the host nor written by `unpack`.
- Dates of files in Basilisk II folders (the data file's host times) and of macOS named-fork files, which ClassicMac
  does not read; `com.apple.FinderInfo`.
- `__MACOSX/` folders in zip archives ([zip.md](../archives/zip.md) reads them inside archives), and netatalk's
  `.AppleDouble/` folders.
- Basilisk II and SheepShaver builds for other hosts, whose name conversion may differ from the Windows build.
- Open question: whether the emulator decodes `%XX` escapes of the bytes Windows-1252 leaves undefined ($81, $8D, $8F,
  $90, $9D) like any other escape; it was not tested.

## 9. References

1. Apple Computer, *Inside Macintosh: Files* (`FInfo`, `FXInfo`, `DInfo`, `DXInfo`, file names), and *Inside
   Macintosh: Macintosh Toolbox Essentials*, the Finder Interface chapter (Finder flags).
2. Apple Computer, *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note, version 2 (RFC 1740).
3. Microsoft, the FAT specification (fatgen103), for DOS dates and 8.3 names; *Naming Files, Paths, and Namespaces*,
   for what Windows names may hold.
4. Basilisk II and SheepShaver (GPL): their shared-folder layout, as the emulator behaves. Behaviour only.
5. The Unarchiver (`unar -k visible`, LGPL), for the `name.rsrc` form of AppleDouble. Behaviour only.
6. netatalk (GPL), which writes AppleDouble files. Behaviour only.
7. Unicode, `VENDORS/APPLE/ROMAN.TXT`, for Mac OS Roman; Microsoft, Windows code page 1252.
8. PC Exchange and File Exchange: [pc-exchange.md](../file-systems/pc-exchange.md).
