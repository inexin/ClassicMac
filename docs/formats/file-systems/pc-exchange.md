# PC Exchange and File Exchange

PC Exchange (System 7.1 to Mac OS 8) and File Exchange (Mac OS 9) let the Mac use DOS disks. They keep what FAT cannot
hold, a file's Mac name, Finder information, dates and resource fork, in two hidden items in every directory: a
`RESOURCE.FRK` folder of raw resource forks and a `FINDER.DAT` file of 92-byte records. Apple never documented the
format; it is the same in PC Exchange 1.0.4 and File Exchange 3.0.2 [Code], and no Apple file-system code in Mac OS
7.1–9 reads or writes AppleSingle or AppleDouble [Code]: this is what a Mac wrote to DOS disks. ClassicMac reads it on
FAT volumes ([fat.md](fat.md)) and in folders copied off such disks onto another file system
([host-folders.md](../containers/host-folders.md)), and shows each file as Mac OS 9 shows it.

| | |
| --- | --- |
| Identified by | A hidden `FINDER.DAT` file or `RESOURCE.FRK` folder in a directory of a FAT volume or host folder |
| ClassicMac | Reads: `ClassicMac.Files.Containers` (`PcExchange`, `ExtensionMap`), used by `ClassicMac.Files.Fat` (`FatReader`) and `ClassicMac.Files` (`HostFiles`) |
| Verified against | Two FAT12 floppies File Exchange 3.0.2 wrote in SheepShaver, Mac OS 9.0, with the Mac's own listing of every file |
| Sources | PC Exchange 1.0.4 and File Exchange 3.0.2 (disassembly); Internet Config's default map in Mac OS 9.0 |

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

"File Exchange" means File Exchange 3.0.2 as shipped with Mac OS 9.0 and "PC Exchange" PC Exchange 1.0.4; where they
agree, "the Mac" means both. [Code] without a version names File Exchange 3.0.2; [Verified] means checked on the
floppies of §7 in SheepShaver, Mac OS 9.0. FAT names, attributes and dates are as in [fat.md §1](fat.md#1-layout).

### 1.1 `RESOURCE.FRK`

- A subdirectory named `RESOURCE.FRK`, attributes `$12` (hidden, directory) [Code] [Verified].
- It holds one file per data file that has a resource fork, under **the data file's 8.3 name** [Code] [Verified]. Only
  the 8.3 name matters; long names on these files are not used.
- The file's content is the **raw resource fork, with no header** [Code] [Verified].
- Its attributes are the data file's with archive (`$20`) added; its dates are the time it was written [Code].
- A file with no entry in `RESOURCE.FRK`, or an empty one, has an empty resource fork [Code]. A fork file made by hand
  opens normally [Verified].

### 1.2 `FINDER.DAT`

A hidden file named `FINDER.DAT` (attributes `$02` when created; `$22`, with the archive bit, once written
[Verified]) holding **92-byte records**, big-endian, with no header and no version number anywhere [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | name length | Length of the Mac name, 0–31; **0 marks a free record** (§1.4) |
| +$01 | 31 | Mac name | Mac OS Roman. Bytes after the name are garbage [Code] [Verified] |
| +$20 | 16 | `FInfo` | Finder info: type, creator, flags, location, folder; `DInfo` for a folder ([unwrapping.md §1.1](../containers/unwrapping.md#11-finder-information)) |
| +$30 | 16 | `FXInfo` | Extended Finder info; `DXInfo` for a folder |
| +$40 | 4 | creation date | Mac seconds, local time; 0 when unset |
| +$44 | 4 | modification date | Mac seconds, local time, full precision: odd seconds kept [Code] [Verified] |
| +$48 | 4 | backup date | Written only when non-zero |
| +$4C | 4 | file number | Catalog node ID, from the volume's counter (§1.5) |
| +$50 | 11 | 8.3 name | The item's 8.3 name exactly as stored in its directory entry, space-padded, no dot (`FANTAS~1EML`): **the lookup key** |
| +$5B | 1 | unused | Garbage |

All of it [Code]. An item's record is in its **parent directory's** `FINDER.DAT`; folders have records like files, in
the directory that contains them [Code]. **Nothing is validated**: the Mac uses a corrupt record as it is [Code].

### 1.3 Record packing

- Records are packed `floor(cluster size / 92)` to a cluster and **never straddle a cluster** [Code] [Verified]. With
  512-byte clusters, 5 records fill bytes 0–459 (at 0, 92, …, 368), the 52 bytes to 512 are left as they were, and the
  6th record starts at 512, so a file of 6 records is 604 bytes, not 552.
- The gap holds garbage: File Exchange writes into a new cluster without clearing it [Code] [Verified]. Bytes past the
  end of the file, and cluster slack, may hold copies of other records [Code] [Verified]. Only whole 92-byte slots
  that lie within the file's size are records.
- The file does not record the cluster size. On a FAT volume it is the volume's (§5.2 for a `FINDER.DAT` off its
  volume).

### 1.4 Free records

- A record whose name length (+$00) is 0 is free [Code]. Deleting an item clears its name length and its 8.3 name;
  the file never shrinks [Code].
- A record whose first 8.3-name byte (+$50) is 0 is free too [Fitted]: File Exchange leaves stray records with a
  garbage name and an empty 8.3 name [Verified], which match no directory entry anyway.

### 1.5 The root record

File numbers come from a per-volume counter that starts at `$7FFFFFFF` and counts **down** [Code] [Verified]. The
counter is kept in a record of the root directory's `FINDER.DAT` keyed by the volume label [Code] [Verified]; it matches
no file.

### 1.6 `FILEID.DAT`

A hidden file in the root directory only, holding 64-byte records {file ID, parent directory ID, Pascal name}, record
0 being a header (`$00010000`) [Code]. Only `PBCreateFileIDRef` creates it, when an application asks for a file ID
reference [Code]; it carries nothing a reader needs.

## 2. Reading

For each directory entry other than the hidden items (§2.7), the Mac shows the name, Finder information, dates and
forks of §2.2–2.8. The steps for one file:

1. Find its record (§2.1).
2. Its name (§2.2, §2.3).
3. Its Finder information (§2.4): the record's or the placeholder.
4. The type mapped by name ending (§2.5).
5. The invisible and locked flags from DOS attributes (§2.4).
6. Its dates (§2.6) and forks (§2.8).

### 2.1 Finding an item's record

- The Mac looks an item up by the record's Mac name, then by the VFAT long name (whose checksum must match), then by
  the 8.3 name [Code] [Verified].
- A directory entry's record is the one whose 11 bytes at +$50 equal the entry's 11 name bytes [Code] [Verified]: byte
  for byte, with no case folding, on the name bytes as stored in the directory, a leading `$05` included [Code].
- A record found by its Mac name, as when a file is created, matches ignoring case but not diacritical marks
  (`EqualString`) [Code].
- Records are searched in file order and **the first match wins**, for reading and for writing, so a later record with
  the same 8.3 name is dead; free slots are skipped even when their 8.3 name matches [Code]. A 32-record cache is
  checked first, but it is filled only from the same search, so it gives the same record [Code].

### 2.2 The Mac name

In this order [Code] [Verified]:

1. **The record's Mac name**, when the item has a record, even a garbage one, which the Mac shows as it is [Verified].
2. **The long name**, converted as in §2.3, when the entry has one whose checksum matches
   ([fat.md §2.5](fat.md#25-assembling-a-long-name)).
3. **The 8.3 name**, byte for byte [Code]:
   - The bytes are shown **as stored**: no case change (the `DIR_NTRes` lower-case flags are ignored, so a Windows NT
     `readme.txt` stored as an 8.3 entry shows as `README.TXT`), and no code page conversion: each byte is shown as
     the Mac byte of the same value (in the system script, Mac OS Roman on a Roman system), with no `?`. CP437 `$82`
     (`é`) shows as Mac OS Roman `$82` (`Ç`). A leading `$05` shows as `$E5` (`Â`).
   - The stem stops at its **first byte of `$20` or less**, so `A B     TXT` shows as `A.TXT`.
   - A `.` is added when any extension byte is above `$20`; the extension bytes are copied while they are `$20` or
     more, and trailing spaces are then trimmed: `NAME    A B` shows as `NAME.A B`, `NAME     AB` as `NAME. AB`.

A folder's name follows the same order, using the folder's record in its parent's `FINDER.DAT`.

### 2.3 Long names to Mac names

A file with no record but a long name gets its Mac name from the long name, as File Exchange converts it [Code]
[Verified].

1. The UTF-16 from the long-name entries goes to the Text Encoding Converter **unchanged, with no Unicode
   normalization**, from Unicode 2.1 to the **system script's** encoding (Mac OS Roman on a Roman system; MacIcelandic,
   MacJapanese and so on elsewhere) [Code]. The converter does **not** compose a decomposed sequence (`e` + U+0301 into
   `é`): the combining mark has no byte, so step 3 applies [Verified: `Cafe`+U+0301`.txt` shows as `Cafe`, `$01`,
   `.txt` (the low byte of U+0301), while the precomposed `Café.txt` stays `Café.txt`].
2. If **every** character has a Mac OS Roman equivalent, the name is its Mac Roman bytes [Code] [Verified]. A `:` is
   kept, though it is the Mac's path separator [Verified: `a:b c.txt`].
3. If **any** character has none, the converter reports that it used fallbacks and the **whole** name takes the other
   path: each UTF-16 code unit becomes its low byte, and `:` becomes `_` [Code] [Verified]. `漢字 kanji.txt` becomes
   `"W kanji.txt` (U+6F22 → `$22`, U+5B57 → `$57`); `漢:.txt` becomes `"_.txt`. File Exchange's own `_` fallback
   character never takes effect [Code]. The low bytes can include `$00`: a Mac name can hold a NUL byte.
4. If the result is at most 31 bytes, it is the name. Otherwise it is shortened to exactly 31 bytes [Code] [Verified]:

   ```
   extension = from the last '.' among the name's final six characters to the end; none if there is no '.' there
   name      = first (27 − length(extension)) characters, '#', three upper-case hex digits of the CRC, extension
   ```

   - The extension search follows File Exchange's `FindExtension` [Code]: among the final six characters; among the
     final `length − 2` for names of 3–6 characters; none for shorter names. Such short names are never shortened.
   - The extension counts its dot. The kept characters and the extension are converted by the same path as the whole
     name (Mac Roman or low bytes).
   - The **CRC** is CRC-16 with polynomial `$1021`, initial value 0, no reflection and no final XOR, computed over all
     of the long name's UTF-16 units as stored, **not normalized**, as **big-endian** UTF-16 [Code] [Verified]; the
     three digits are its low 12 bits. A decomposed and a precomposed spelling of one name get different digits. This
     is the unique-name checksum of the OSTA UDF specification, which File Exchange borrowed [Code].

Examples [Verified] unless marked:

- `This is a very long Windows file name.txt` becomes `This is a very long Win#7C7.txt`.
- `Crème brûlée with a very long name.txt`, 38 characters precomposed, becomes `Crème brûlée with a ver#F28.txt` (Mac
  Roman); the same name decomposed, 41 characters, takes the low-byte path and becomes `Cre`, `$00`, `me bru`, `$02`,
  `le`, `$01`, `e with a #366.txt` (U+0300, U+0302 and U+0301 give `$00`, `$02` and `$01`).
- `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef.jpeg` becomes `ABCDEFGHIJKLMNOPQRSTUV#` + 3 digits + `.jpeg` (22 + 4 + 5)
  [Code].
- In `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh.eleven` the last `.` is seven characters from the end, so there is no
  extension: the first 27 characters, `#` and the digits [Code].

### 2.4 Finder info

- **With a record:** the record's `FInfo` and `FXInfo`, as stored [Code] [Verified].
- **Without a record:** the blank Finder info, the **placeholder**: type `TEXT`, creator `dosa`, flags 0, location 0,
  and in the `FXInfo` everything 0 but the put-away folder, 2 (the root directory's ID; File Exchange never reads it)
  [Code] [Verified]. A `RESOURCE.FRK` fork plays no part in it: it only gives the fork's length [Code].
- **DOS hidden or system** makes the file invisible: the Finder flag `isInvisible` (`$4000`) is ORed onto the stored
  flags and never cleared, so a record that stores `$4000` keeps the file invisible after the PC clears the hidden
  attribute [Code]. Making a file invisible on the Mac sets DOS hidden and stores the flags in the record [Code]. The
  archive attribute is ignored [Code].
- **DOS read-only or system** makes the file locked [Code].
- **Alias:** File Exchange sets the alias flag (`$8000`) on a file whose type and creator, after the record and the
  mapping, are exactly `'scut'`/`'dosa'` [Code]. Nothing ties this to the `.lnk` ending: Mac OS 9's default Internet
  Config map has no `.lnk` entry, so a Windows shortcut shows as a plain `TEXT`/`dosa` document, and its contents are
  never translated into an alias [Code].

### 2.5 Types by name ending

File Exchange keeps a per-volume table of extension → type and creator, but in Mac OS 9.0 it is **always empty**: at
startup its INIT moves any old PC Exchange mappings (the `'dMap'` resource, ID −4040) into Internet Config and builds
the table with a count of 0, and nothing fills it later [Code]. The only mapping File Exchange applies is therefore
Internet Config's map of file-name endings [Code] [Verified]:

1. It applies **only** when the file's type and creator are exactly `TEXT`/`dosa` [Code] [Verified]. A stored type is
   never overridden: a `.TXT` file whose record says `APPL`/`abcd` stays `APPL`/`abcd` [Verified].
2. It applies only to a file that has a record; File Exchange has only one mapping path, and it needs a record [Code].
   On a writable volume with "save info" on, though, the Mac makes the record (§3.3) before it reads the Finder info,
   so the mapped type shows there [Code] [Verified: `.TXT` → `TEXT`/`ttxt`, `.BIN` → `BINA`/`SITx`, `.JPG` →
   `JPEG`/`ogle`]. On a locked or read-only volume, or with "save info" off, a file without a record shows plain
   `TEXT`/`dosa` [Code].
3. It matches the end of the file's Mac name (§2.2), ignoring case; the **longest** matching ending wins, and among
   endings of the same length the earlier entry [Code]. The entries' flags are ignored [Code].
4. It applies only while File Exchange's "map extensions" preference is on (Gestalt `'pcxg'` bit 3), which is the
   default [Code].

Examples from Mac OS 9's default map, which has 313 entries and which a user can change: `.txt` → `TEXT`/`ttxt`,
`.bin` → `BINA`/`SITx`, `.jpg` → `JPEG`/`ogle`, `.gif` → `GIFf`/`ogle`, `.doc` → `WDBN`/`MSWD` [Verified]; `.pdf` →
`'PDF '`/`CARO`, `.sit` → `SITD`/`SITx` [Code].

The map belongs to the Mac that reads the disk, not to the disk. The mapped type is shown, not stored, until the file's
data fork is closed (§3.4).

A read-only image is not a locked volume. In a run where the image was read-only only on the host, the Mac saw the
volume writable, File Exchange made `FINDER.DAT` and records in its cache, the writes failed (the Finder reported a
problem with the disk), and a `.JPG` file with no record on disk showed `JPEG`/`ogle` [Verified], mapped through a
stale cached record [Code], while a file with a `RESOURCE.FRK` fork and no record showed `TEXT`/`dosa`, flags 0,
put-away folder 2 [Verified].

### 2.6 Dates

| Date | File Exchange 3.0.2 |
| --- | --- |
| Created | The DOS entry's creation fields ([fat.md §2.6](fat.md#26-how-the-mac-reads-dos-dates)); "now" when the creation date word is 0, whatever the time word. The record's +$40 is written but never read [Code] [Verified] |
| Modified | The later of the DOS entry's modification date and the record's +$44, compared as unsigned Mac dates; a record date of 0 counts as earlier than any DOS date [Code] [Verified]. A DOS date of 0 is not special: it reads as 1979-12-01 [Verified] |
| Backup | The record's +$48 [Code] [Verified] |

Because DOS keeps only even seconds and the record keeps full precision, a file modified on the Mac shows the record's
exact modification time [Verified]. PC Exchange's rules are in §4.

### 2.7 Hidden items

The Mac hides these entries **by name**, whatever their attributes, in every directory [Code]:

- `FINDER.DAT` (8.3 key `FINDER  DAT`)
- `FILEID.DAT` (`FILEID  DAT`)
- `RESOURCE.FRK` (`RESOURCEFRK`)

Other hidden or system entries are listed, invisible (§2.4). "Desktop" is a synthetic root entry [Code]. Mounting a
FAT disk writable makes the Finder add `TheVolumeSettingsFolder` (with `DesktopPrinters DB`), `Desktop Folder`,
`Trash` and a hidden `DESKTOP` file with a resource fork, ordinary FAT entries listed like any other [Verified].

### 2.8 Forks

- **Data fork:** the entry's cluster chain, `DIR_FileSize` bytes [Doc: Microsoft FAT specification].
- **Resource fork:** the file in this directory's `RESOURCE.FRK` whose 8.3 name equals the entry's (§1.1), its full
  size [Code] [Verified].

### 2.9 Folders

A folder contributes its name (§2.2) to the path of everything inside it. Its record holds `DInfo`, `DXInfo` and dates
(§1.2).

## 3. Writing

ClassicMac does not write this format. File Exchange writes it as follows [Code] [Verified] unless marked; a reader must
expect what it leaves. What File Exchange's formatter writes is in [fat.md §3](fat.md#3-writing).

### 3.1 Creating a file: Mac name to FAT

In order [Code] [Verified: 14 names, every 8.3 name exact]:

1. A record in the directory with the same Mac name, ignoring case, refuses the create with `dupFNErr` (−48)
   [Verified: `readme.txt` then `README.TXT`].
2. **Sanitise:** control characters, `" * / : < > ? \ |` and `$7F` become `_`; trailing dots and spaces are removed;
   leading spaces stay. **The sanitised name replaces the Mac name** [Verified: `a/b` → `a_b`, `trail.` → `trail`].
3. **Short name:** characters DOS does not allow, and every byte from `$80` up, become `_`; spaces are dropped;
   letters are upper-cased. A name that does not fit 8.3 exactly is cut to 6 characters plus `~1`, the extension to 3;
   on a collision the number grows and the `~` moves left. Device names (`CON`, `COM1`) are not checked.
4. A `FINDER.DAT` record and a VFAT long name are always written, even when the name fits 8.3 [Verified]. The long
   name is the Mac name converted from Mac OS Roman to UTF-16, precomposed.

| Mac name | 8.3 name | Mac name | 8.3 name |
| --- | --- | --- | --- |
| `Hello World` | `HELLOW~1` | `trail.` | `TRAIL` |
| `a/b` | `A_B` | `12345678.1234` | `123456~1.123` |
| `Résumé` | `R_SUM_~1` | `.profile` | `~1.PRO` |
| `readme.txt` | `README.TXT` | `COM1` | `COM1` |
| `a.b.c` | `AB~1.C` | `CON.txt` | `CON.TXT` |
| `   lead` | `LEAD~1` | `ÄÖÜ ß` | `____~1` |
| `™®©` | `___~1` | a 31-character name starting `ABCDEF` | `ABCDEF~1` |

### 3.2 Dates

Dates are written exactly into the record and truncated to even seconds in the DOS entry (`$B0000001` reads back as
`$B0000000`), with Mac years 1904–1979 stored as DOS 2032–2107 ([fat.md §2.6](fat.md#26-how-the-mac-reads-dos-dates))
[Verified].

### 3.3 When records are made

A reader cannot assume that a file has a record, nor that a record's contents were set by an application:

- Records are created **lazily**. Getting catalog information for an item with no file number creates a record, so
  merely listing a writable FAT disk on the Mac creates a record for every item that has none [Code] [Verified].
- Such a record has the file's Mac name, type `TEXT`, creator `dosa`, Finder flags 0, put-away folder 2, **zero dates**
  and a file number `$7FFFFFxx` from the counter [Verified], even for a file whose name ending the Mac shows mapped:
  the mapped type is not written back [Verified]. The code of both versions leaves the dates from uninitialised memory
  [Code]; they were zero in every record seen.
- A record is also created when a file's Mac name differs from its 8.3 name, whenever Finder info is set, and for
  every file the Mac creates (§3.1) [Code].
- Records are written only while the volume's "save info" setting is on [Code].
- A new record takes the first free slot, but **never offset 0** (a quirk), else it is appended [Code].
- A record made for a file that has a resource fork but no record can get a **garbage Mac name** (seen as `P"P`, with
  control characters) [Verified]; the Mac then shows that name (§2.2). The name routine gives up without filling its
  buffer when its search of the directory misses, and the new record takes its name from that uninitialised buffer
  [Code]; why the search misses is not traced.
- A `RESOURCE.FRK` file is created on the first write to the resource fork, deleted with the data file, and moved and
  renamed with it; `RESOURCE.FRK` itself is removed when nothing else is left in it [Code].

### 3.4 What a close stores

The mapped type (§2.5) is shown, not stored, until the file's data fork is closed or flushed: File Exchange then reads
the Finder info, mapping applied, and writes it back to the record [Code]. So:

- a file only listed on the Mac keeps `TEXT`/`dosa` in its record [Verified];
- a file opened on the Mac usually has its mapped type stored, indistinguishable from a type an application set;
- that close-time round trip can also write back a stale copy of the record, reverting a type set just before
  [Verified]; the exact trigger is not traced.

So a record's dates and file number are more trustworthy than its type, and a record whose 8.3 key matches no
directory entry is best ignored [Verified].

## 4. Variants

| | File Exchange 3.0.2 (Mac OS 9) | PC Exchange 1.0.4 (System 7.1) |
| --- | --- | --- |
| FAT types | FAT12, FAT16, FAT32 | FAT12 and FAT16 only [Code] ([fat.md §4](fat.md#4-variants)) |
| Long names | Read (§2.3) and written (§3.1) | Ignored [Code] |
| Creation date | The DOS entry's (§2.6) | The record's +$40, or 0 without a record; DOS entries written by PC Exchange carry the modification date only [Code] |
| Modification date | The later of the DOS date and +$44 | The same [Code] |
| Backup date | The record's +$48 | The same [Code] |
| DOS years 2032–2107 | Read as 1904–1979 ([fat.md §2.6](fat.md#26-how-the-mac-reads-dos-dates)) | No wrap; dates before 1980 come out as garbage [Code] |
| Type by ending | Internet Config's map, `TEXT`/`dosa` only (§2.5) | Its own table, matched on the 3-character DOS extension, default `.TXT` → `TEXT`/`ttxt`. Its code applies the table only when a record exists and lets it override the stored type; not checked on a running system [Code: PC Exchange 1.0.4] |

PC Exchange 2.x (Mac OS 7.5–8.x) was not available for tracing. With the same record code at both ends of the range,
its format is assumed to be the same; its date rules are unknown (§8).

## 5. ClassicMac

### 5.1 On FAT volumes

- The record is the first used record, in file order, whose 8.3 key equals the entry's stored name bytes (§2.1);
  records are free under both rules of §1.4.
- The name is chosen as §2.2, garbage record names included; a record name containing `$00`–`$1F` is reported as
  `fat.suspect-name` ([fat.md §6](fat.md#6-diagnostics)).
- Finder info is the record's, or the placeholder (§2.4).
- Dates follow File Exchange (§2.6), except that a DOS creation date word of 0 takes the record's creation date (+$40,
  PC Exchange's rule), or none: ClassicMac has no "now" to match. No backup date is kept.
- ClassicMac shows the **stored** type by default. An application may supply a map (`ContainerReadOptions.ExtensionMap`,
  a list of endings with type and creator in Internet Config's order); ClassicMac then applies it with File Exchange's
  rules (§2.5), after the record and before the DOS attributes, to every `TEXT`/`dosa` file, with a record or not, as a
  writable volume shows them. It matches the Mac name. No map derived from Apple's ships with ClassicMac.
- DOS hidden or system sets the invisible flag as §2.4. ClassicMac has no locked attribute and does not report DOS
  read-only, and does not set the alias flag.
- `FILEID.DAT` is not read. Folder records' `DInfo`, `DXInfo` and dates are not used: the output is files, and a folder
  with no files in it produces nothing.
- Corrupt records are used as they are, as on the Mac.

### 5.2 In host folders

A folder copied off a DOS disk keeps `RESOURCE.FRK` and `FINDER.DAT` beside the files; when the PC Exchange layout
applies is in [host-folders.md](../containers/host-folders.md). There:

- `RESOURCE.FRK` and `FINDER.DAT` are found whatever their case, as on FAT. The fork file is looked up under the host
  name, whatever its case; an empty fork file gives no resource fork.
- The 8.3 name may not be visible. The record key is formed from the host name when it is a valid 8.3 name: a stem of
  1–8 and an extension of 0–3 characters, all in `$21`–`$7E`, one dot at most, upper-cased and space-padded to 8 + 3
  (`FANTAS~1.EML` → `FANTAS~1EML`, `readme` → `README     `). Otherwise (a long host name) the record whose Mac name
  equals the host name, ignoring case, is used. The first match in file order wins.
- The cluster size is not known. ClassicMac tries tight packing (a record every 92 bytes) and cluster sizes of 512 to
  65,536 bytes (powers of two), counts the used records under each, and keeps the packing under which every used
  record is plausible (name length 1–31 and all eleven 8.3-name bytes in `$20`–`$7E`) with the most used records; the
  smaller packing wins a tie. If none gives only plausible records, no records are read.
- The host file's creation and modification times stand for the DOS entry's: truncated to even seconds, and years from
  2032 moved back 128.
- A file with a `RESOURCE.FRK` fork but no record gets the placeholder (§2.4) and the host times. The extension map,
  when supplied, applies to files with and without a record, matched against the host name.
- The host file's hidden or system attribute sets the invisible flag.

## 6. Diagnostics

The format has no codes of its own: corrupt `FINDER.DAT` records are not diagnosed, because the Mac uses them as they
are [Code], and so does ClassicMac, apart from the free-record rules of §1.4. A record's garbage Mac name is
`fat.suspect-name` ([fat.md §6](fat.md#6-diagnostics)); the host-folder reader's codes are in
[host-folders.md](../containers/host-folders.md).

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |

## 7. Verification

- `tests/ClassicMac.Files.Tests/PcExchangeTests.cs`: records decode; records never straddle a cluster; 8.3 keys from
  host names; a host folder from a DOS disk joining its companions; DOS times from host times; long host names matched
  against Mac names; a fork without a record getting the placeholder; files with neither staying plain.
- `tests/ClassicMac.Files.Tests/FatTests.cs`: File Exchange data giving Mac names, Finder info, dates and forks;
  decomposed long names not composed; shortened names with their CRC over the stored UTF-16; long names as File
  Exchange shows them; the extension map applied to placeholders only and only when given; the longest ending, then the
  earlier entry, winning.
- Two FAT12 floppies File Exchange 3.0.2 wrote in SheepShaver, Mac OS 9.0, with the Mac's own listing
  ([fat.md §7](fat.md#7-verification)). Every [Verified] rule in this document was checked on them or on the same
  emulator: the record layout and packing, record creation on listing, garbage names, the 14 names of §3.1, the long
  names of §2.3, the mapped types of §2.5, the dates of §2.6.

## 8. Not covered

- The locked state from DOS read-only or system, the `'scut'`/`'dosa'` alias flag (§2.4), backup dates (§2.6) and
  folder Finder info (§2.9).
- Writing PC Exchange or File Exchange data, `FILEID.DAT`, or FAT volumes.
- Open, to be checked on a running Mac OS 9.0: a file with no record on a volume the Mac sees as locked (an image
  write-protected in the emulator), and with "save info" off (§2.5).
- PC Exchange 2.x (Mac OS 7.5–8.x), not available for tracing; its creation-date rule is unknown.
- Why File Exchange's directory search misses when it names a new record (§3.3), and what triggers a stale record at a
  close (§3.4).

## 9. References

1. Apple, PC Exchange 1.0.4 and File Exchange 3.0.2 (as shipped with Mac OS 9.0), traced in disassembly: the
   `FINDER.DAT` and `RESOURCE.FRK` format, dates, name conversion (`Win95NameToMac`, `FindExtension`, `MakeCRC`,
   `UDFChecksum`) and type mapping.
2. Apple, Internet Config's default extension map as installed by Mac OS 9.0.
3. Optical Storage Technology Association, *Universal Disk Format Specification*: the unique-name CRC.
4. Microsoft, *FAT32 File System Specification*, version 1.03: directory entries and long names.
