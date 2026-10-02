# PC Exchange and File Exchange

Contents

1. [The Mac data: `RESOURCE.FRK` and `FINDER.DAT`](#1-the-mac-data-resourcefrk-and-finderdat)
2. [The Mac view of a file](#2-the-mac-view-of-a-file)
3. [Long names as File Exchange converts them](#3-long-names-as-file-exchange-converts-them)
4. [Types by name ending](#4-types-by-name-ending)
5. [What File Exchange writes](#5-what-file-exchange-writes)
6. [PC Exchange and File Exchange folders](#6-pc-exchange-and-file-exchange-folders)
7. [File Exchange names](#7-file-exchange-names)
8. [The extension map](#8-the-extension-map)
9. [Diagnostics](#9-diagnostics)
10. [Not covered](#10-not-covered)

---

## 1. The Mac data: `RESOURCE.FRK` and `FINDER.DAT`

PC Exchange and File Exchange keep everything a FAT file lacks in two hidden items **in every directory** [Code]. The
format is the same in PC Exchange 1.0.4 and File Exchange 3.0.2 [Code]; PC Exchange 2.x (Mac OS 7.5–8.x) was not
available for tracing, and with the same code at both ends of the range its format is taken to be the same. No Apple
file-system code in Mac OS 7.1–9 reads or writes AppleSingle or AppleDouble [Code]: this is what a Mac wrote to DOS
disks.

### 1.1 `RESOURCE.FRK`

- A subdirectory named `RESOURCE.FRK`, attributes `$12` (hidden, directory) [Code] [Verified].
- It holds one file per data file that has a resource fork, under **the same 8.3 name** as the data file [Code]
  [Verified]. Only the 8.3 name matters; long names on these files are not used.
- The file's content is the **raw resource fork, with no header** [Code] [Verified].
- Its attributes are the data file's with `$20` added, and its dates are the time it was written [Code].
- It is created on the first write to the resource fork, deleted with the data file, and moved and renamed with it;
  `RESOURCE.FRK` itself is removed when it becomes empty [Code].
- A file with no entry in `RESOURCE.FRK`, or an empty one, has an empty resource fork [Code].

### 1.2 `FINDER.DAT`

A hidden file named `FINDER.DAT` (attributes `$02` when created; `$22` once written, with the archive bit [Verified])
holding **92-byte records**, big-endian, with no header and no version [Code]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $00 | 32 | Str31 | Mac name: a length byte (0–31) and the name in Mac OS Roman; garbage after the name |
| $20 | 16 | FInfo | Finder info: type, creator, flags, location, folder (DInfo for a folder) |
| $30 | 16 | FXInfo | Extended Finder info (DXInfo for a folder) |
| $40 | 4 | u32 | Creation date, Mac seconds, local time |
| $44 | 4 | u32 | Modification date, Mac seconds, local time, full precision |
| $48 | 4 | u32 | Backup date; written only when non-zero |
| $4C | 4 | u32 | File number (catalog node ID) |
| $50 | 11 | bytes | The item's 8.3 name as stored in its directory entry, space-padded, no dot: **the lookup key** |
| $5B | 1 | u8 | Unused; garbage |

- An item's record is in its **parent directory's** `FINDER.DAT` [Code]. Folders have records like files, in the
  directory that contains them.
- A record is found by comparing its 11 bytes at $50 with the directory entry's 11 name bytes [Code] [Verified]. The
  comparison is **byte for byte**, with no case folding, on the name bytes as stored in the directory, a leading
  `$05` included [Code]. ClassicMac compares the same way.
- A record can also be found by its Mac name, as when a file is created: that comparison ignores case and respects
  diacritical marks (`EqualString`) [Code].
- File numbers come from a per-volume counter that starts at `$7FFFFFFF` and counts **down** [Code] [Verified]. The
  counter is kept in a **root record**, keyed by the volume label [Code] [Verified]; it matches no file.
- **Nothing is validated.** The Mac uses a corrupt record as it is [Code].

### 1.3 Record packing

- Records are packed `floor(clusterSize / 92)` to a cluster and **never straddle a cluster** [Code] [Verified]: with
  512-byte clusters, 5 records fill bytes 0–459, the 52 bytes to 512 are left as they were, and the 6th record starts
  at 512 (so a file of 6 records is 604 bytes, not 552). The gap holds garbage: File Exchange writes into a new cluster
  without clearing it [Code] [Verified].
- Read only whole 92-byte slots that lie within the file's size. Bytes past the end of the file, and cluster slack,
  may hold copies of other records [Code] [Verified].
- The file does not record the cluster size; on a FAT volume the reader takes it from the boot sector. (For a
  `FINDER.DAT` copied off its volume, see §6.3, where the cluster size is guessed.)

### 1.4 Free records

- A record whose name length (byte $00) is 0 is free [Code]. Deleting an item clears its name length and 8.3 name;
  the file never shrinks [Code].
- ClassicMac also treats a record whose first 8.3-name byte ($50) is 0 as free [Fitted]: File Exchange leaves stray
  records with a garbage name and an empty 8.3 name [Verified], which match no directory entry anyway.
- Records are searched in file order and **the first match wins**, for reading and for writing, so a later record
  with the same 8.3 name is dead; free slots are skipped even when their 8.3 name matches [Code]. (A 32-record cache
  is checked first, but it is filled only from the same search, so it gives the same record [Code].) ClassicMac uses
  the first used record in file order too.

### 1.5 When records exist

A reader cannot assume that a file has a record, nor that a record's contents were set by an application:

- Records are created **lazily**. Merely listing a writable FAT disk on the Mac creates a record for every item that
  has none, because the Mac asks for a file number [Code] [Verified]. Such a record has the file's Mac name, type
  `TEXT`, creator `dosa`, Finder flags 0, put-away folder 2, **zero dates** and a file number `$7FFFFFxx` from the
  counter [Verified], even for a file whose name ending the Mac shows mapped (§4.3): the mapped type is not written
  back [Verified]. (The code leaves the dates uninitialised [Code]; they were zero in every record seen.)
- A record is also created when a file's Mac name differs from its 8.3 name, and whenever Finder info is set [Code].
- Records are written only while the volume's "save info" setting is on [Code].
- A new record takes the first free slot, but never offset 0 (a quirk), else it is appended [Code].
- A record made for a file that has a resource fork but no record can get a **garbage Mac name** (seen as `P"P`,
  with control characters) [Verified]; the Mac then shows that name (§2.1). The name routine gives up without filling
  its buffer when its search of the directory misses, and the new record takes its name from that uninitialised
  buffer [Code]; why the search misses is not traced.
- A record's type can revert to an earlier value around a close [Verified]; see 13.3.

### 1.6 `FILEID.DAT`

A hidden file in the root directory only, holding 64-byte records {file ID, parent directory ID, Pascal name}, record 0
being a header (`$00010000`) [Code]. It is created only when an application asks for a file ID reference, carries
nothing a reader needs, and ClassicMac does not read it.

---

## 2. The Mac view of a file

For each directory entry other than the hidden items (§2.5), the Mac shows the following. ClassicMac shows the same.

### 2.1 Name

In this order [Code] [Verified]:

1. **The record's Mac name**, when the item has a record (§1.2) — even a garbage one, which the Mac shows as it is
   [Verified]. ClassicMac shows it too, with `fat.suspect-name` when it contains bytes `$00`–`$1F`.
2. **The long name**, converted as in §3, when the entry has one whose checksum matches ([fat.md §7.3](fat.md#73-assembling-a-name)).
3. **The 8.3 name**, byte for byte [Code]:
   - The bytes are shown **as stored**: no case change (the `DIR_NTRes` lower-case flags are ignored, so a Windows NT
     `readme.txt` stored as an 8.3 entry shows as `README.TXT`), and no code page conversion: each byte is shown as
     the Mac byte of the same value (in the system script, Mac OS Roman on a Roman system), with no `?`. CP437 `$82`
     (`é`) shows as Mac OS Roman `$82` (`Ç`). A leading `$05` shows as `$E5` (`Â`).
   - The stem stops at its **first byte of `$20` or less**, so `A B     TXT` shows as `A.TXT`.
   - A `.` is added when any extension byte is above `$20`; the extension bytes are copied while they are `$20` or
     more, and trailing spaces are then trimmed.

   ClassicMac shows 8.3 names the same way: the stored bytes as Mac OS Roman, case kept, nothing replaced, the stem and
   extension cut as above (`NAME    A B` shows as `NAME.A B`, `NAME     AB` as `NAME. AB`). `FINDER.DAT` and
   `RESOURCE.FRK` keys are compared with the stored bytes, a leading `$05` included.

A folder's name follows the same order, using the folder's record in its parent's `FINDER.DAT`.

### 2.2 Finder info

- With a record: the record's FInfo and FXInfo, as stored [Code] [Verified].
- Without a record: the blank Finder info, type `TEXT`, creator `dosa`, flags 0, location 0, and in the FXInfo
  everything 0 but the put-away folder, 2 (the root directory's ID; File Exchange never reads it) [Code] [Verified].
  The record the Mac creates on first listing the file (§1.5) starts from the same blank. ClassicMac gives the same
  placeholder.
- Then, for a `TEXT`/`dosa` file with a record only, the Mac may show a mapped type (§4) [Code]. A file with
  no record never gets one; on a writable volume with "save info" on, though, the Mac makes the record before it
  reads the Finder info, so the mapped type shows there [Code] [Verified: `.TXT` → `TEXT`/`ttxt`, `.BIN` →
  `BINA`/`SITx`, `.JPG` → `JPEG`/`ogle`]. On a locked or read-only volume, or with "save info" off, the file shows
  plain `TEXT`/`dosa` [Code]; File Exchange has only one mapping path, and it needs a record [Code]. This is not yet
  checked on a volume the Mac sees as locked. An image that was read-only only on the host is no such test: the Mac
  saw it writable, File Exchange made `FINDER.DAT` and records in its cache, the writes failed (the Finder reported
  a problem with the disk), and a `.JPG` file with no record on disk showed `JPEG`/`ogle` [Verified], mapped through
  a stale cached record [Code], while a file with a `RESOURCE.FRK` fork and no record showed `TEXT`/`dosa`, flags 0,
  put-away folder 2 [Verified]. The run needs repeating with the image write-protected in the emulator. "Save info"
  off was not tested.
- DOS **hidden** or **system** makes the file invisible: the Finder flag `isInvisible` (`$4000`) is ORed onto the
  stored flags and never cleared, so a record that stores `$4000` keeps the file invisible after the PC clears the
  hidden attribute [Code]. (Making a file invisible on the Mac sets DOS hidden and stores the flags in the record
  [Code].) The archive attribute is ignored [Code]. ClassicMac sets the invisible flag the same way.
- DOS **read-only** or **system** makes the file locked [Code]. ClassicMac has no locked attribute yet and does not
  report it.
- File Exchange sets the alias flag (`$8000`) on a file whose type and creator, after the record and the mapping, are
  exactly `'scut'`/`'dosa'` [Code]. Nothing ties this to the `.lnk` ending: Mac OS 9's default Internet Config map has
  no `.lnk` entry, so a Windows shortcut shows as a plain `TEXT`/`dosa` document, and its contents are never
  translated into an alias [Code]. ClassicMac does not set the alias flag.

### 2.3 Dates

File Exchange [Code] [Verified]:

- **Creation:** from the DOS entry's creation fields ([fat.md §8](fat.md#8-dos-dates-and-times)). The record's creation date ($40) is written but
  never read. When the DOS creation date word is 0, whatever the time word, File Exchange shows the current time
  [Code] [Verified]; ClassicMac, which has no "now" to match, uses the record's creation date instead (PC
  Exchange's rule), or none.
- **Modification:** the later of the DOS entry's modification date and the record's ($44), compared as unsigned
  Mac dates [Code]. A record date of 0 counts as earlier than any DOS date. A DOS date of 0 is not special: it reads
  as 1979-12-01 ([fat.md §8.2](fat.md#82-how-the-mac-reads-them)) [Verified], on the Mac and in ClassicMac.
- **Backup:** the record's ($48) [Code]. ClassicMac does not carry backup dates.

PC Exchange [Code]: creation from the record's $40 (0 without a record); modification the later of the DOS date and
$44. DOS entries written by PC Exchange carry the modification date only.

Because DOS keeps only even seconds and the record keeps full precision, a file modified on the Mac shows the record's
exact modification time [Verified].

### 2.4 Forks

- **Data fork:** the entry's cluster chain, `DIR_FileSize` bytes [Doc].
- **Resource fork:** the file in this directory's `RESOURCE.FRK` whose 8.3 name equals the entry's (§1.1), its full
  size [Code] [Verified].

### 2.5 Hidden items

The Mac hides these entries **by name**, whatever their attributes, in every directory [Code]:

- `FINDER.DAT` (8.3 key `FINDER  DAT`)
- `FILEID.DAT` (`FILEID  DAT`)
- `RESOURCE.FRK` (`RESOURCEFRK`)

Other hidden or system entries are listed, invisible (§2.2).

### 2.6 Folders

A folder contributes its name (§2.1) to the path of everything inside it. Its record's DInfo and DXInfo and its dates
are not used by ClassicMac, whose output is files; a folder with no files in it produces nothing.

---

## 3. Long names as File Exchange converts them

A file with no record but a long name gets a Mac name from the long name, converted as File Exchange converts it
[Code] [Verified]. PC Exchange does not read long names [Code].

### 3.1 Mac Roman or low bytes

1. File Exchange does **no Unicode normalization**: the UTF-16 from the long-name entries goes to the Text Encoding
   Converter unchanged, from Unicode 2.1 to the **system script's** encoding (Mac OS Roman on a Roman system;
   MacIcelandic, MacJapanese and so on elsewhere) [Code]. The converter does **not** compose a decomposed sequence
   (`e` + U+0301 into `é`): the combining mark has no byte, so step 3 applies [Verified: `Cafe`+U+0301`.txt` shows
   as `Cafe`, `$01`, `.txt` (the low byte of U+0301), while the precomposed `Café.txt` stays `Café.txt`]. The
   low bytes can include `$00`: a Mac name can hold a NUL byte (§3.2). ClassicMac converts to Mac OS Roman, as on
   a Roman system, and takes the name as stored, with no normalization, the same way.
2. If **every** character has a Mac OS Roman equivalent, the name is its Mac Roman bytes [Code] [Verified]. A `:` is
   kept as it is, even though it is the Mac's path separator [Verified: `a:b c.txt`].
3. If **any** character has none, the converter reports that it used fallbacks, and the **whole** name takes the other
   path: each UTF-16 code unit becomes its low byte, and `:` becomes `_` [Code] [Verified]. `漢字 kanji.txt` becomes
   `"W kanji.txt` (U+6F22 → `$22`, U+5B57 → `$57`); `漢:.txt` becomes `"_.txt`. File Exchange's own `_` fallback
   character never takes effect [Code].

### 3.2 Names over 31 bytes

If the result of §3.1 is at most 31 bytes, it is the name. Otherwise it is shortened to exactly 31 bytes [Code]
[Verified]:

```
extension = from the last '.' among the name's final six characters to the end; none if there is no '.' there
name      = first (27 − length(extension)) characters, '#', three upper-case hex digits of the CRC, extension
```

- The characters and the extension are converted by the same path as the whole name (Mac Roman or low bytes).
- The **CRC** is CRC-16 with polynomial `$1021`, initial value 0, no reflection and no final XOR, computed over the
  long name's own UTF-16 units as stored, **not normalized**, all of them, as **big-endian** UTF-16 [Code]
  [Verified]; the three digits are its low 12 bits. ClassicMac computes it the same way, so a decomposed and a
  precomposed spelling of one name get different digits, as on the Mac. This is the unique-name checksum of the OSTA
  UDF specification, which File Exchange borrowed [Code].
- `This is a very long Windows file name.txt` becomes `This is a very long Win#7C7.txt` [Verified].
- `Crème brûlée with a very long name.txt`, 38 characters precomposed, becomes `Crème brûlée with a ver#F28.txt`
  (Mac Roman); the same name decomposed, 41 characters, takes the low-byte path and becomes `Cre`, `$00`,
  `me bru`, `$02`, `le`, `$01`, `e with a #366.txt` (U+0300, U+0302 and U+0301 give `$00`, `$02` and `$01`)
  [Verified].
- `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef.jpeg` becomes `ABCDEFGHIJKLMNOPQRSTUV#` + 3 digits + `.jpeg` (22 + 4 + 5).
- In `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh.eleven` the last `.` is seven characters from the end, so there is no
  extension: the first 27 characters, `#` and the digits.

The extension search follows File Exchange's `FindExtension` [Code]. For names shorter than seven characters its window
is smaller, but such names are never shortened.

---

## 4. Types by name ending

### 4.1 The File Exchange table is empty

File Exchange keeps a per-volume table of extension → type and creator, but in Mac OS 9.0 it is **always empty**: at
startup its INIT moves any old PC Exchange mappings (the `'dMap'` resource, ID −4040) into Internet Config and then
builds the table with a count of 0, and nothing fills it later [Code].

PC Exchange 1.0.4 used only its own table, matched on the 3-character DOS extension, whose default entry is `.TXT` →
`TEXT`/`ttxt` [Code].

### 4.2 Internet Config's map

The only mapping File Exchange applies is Internet Config's map of file-name endings [Code] [Verified]:

- It applies **only** when the file's type and creator are exactly `TEXT`/`dosa` [Code] [Verified]. A stored type is
  never overridden: a `.TXT` file whose record says `APPL`/`abcd` stays `APPL`/`abcd` [Verified].
- It applies only to a file that has a record; a file with none keeps the placeholder, unless the Mac makes its
  record first, as it does on a writable volume (§2.2) [Code].
- It matches the end of the file's Mac name (§2.1), ignoring case; the **longest** matching ending wins, and among
  endings of the same length the earlier entry [Code]. The entries' flags are ignored [Code].
- It applies only while File Exchange's "map extensions" preference is on (Gestalt `'pcxg'` bit 3), which is the
  default [Code].
- Examples from Mac OS 9's default map: `.txt` → `TEXT`/`ttxt`, `.bin` → `BINA`/`SITx`, `.jpg` → `JPEG`/`ogle`,
  `.gif` → `GIFf`/`ogle`, `.doc` → `WDBN`/`MSWD` [Verified]; `.pdf` → `'PDF '`/`CARO`, `.sit` → `SITD`/`SITx`
  [Code].
  The default map has 313 entries, and a user can change it.

### 4.3 What is on the disk

The mapped type is shown, not stored, until the file's data fork is closed or flushed: File Exchange then reads the
Finder info (mapping applied) and writes it back to the record [Code]. So:

- a file only listed on the Mac keeps `TEXT`/`dosa` in its record [Verified];
- a file opened on the Mac usually has its mapped type stored, indistinguishable from a type an application set;
- that close-time round trip can also write back a stale copy of the record, reverting a type set just before
  [Verified; the exact trigger is not traced]. A record's dates and file number are more trustworthy than its type.

### 4.4 ClassicMac: opt-in

The map belongs to the Mac that reads the disk, not to the disk, so ClassicMac shows the **stored** type by default.
An application may supply a map (`ContainerReadOptions.ExtensionMap`); ClassicMac then applies it as in 13.2, after
the record and before the DOS attributes, to every `TEXT`/`dosa` file, with a record or not, as a writable volume
shows them. No map derived from Apple's ships with ClassicMac.

---

## 5. What File Exchange writes

ClassicMac does not write FAT volumes. For a writer, and for reading what File Exchange wrote, its rules are
[Code] [Verified] unless marked; §7 covers the same names on host folders.

### 5.1 Mac name to 8.3 name, on creating a file

1. A record with the same Mac name, compared without regard to case, already in the directory: the create fails with
   `dupFNErr` (−48) [Verified: `readme.txt` then `README.TXT`].
2. The Mac name is sanitized: control characters, `" * / : < > ? \ |` and `$7F` become `_`; trailing dots and spaces
   are removed; leading spaces are kept. **The sanitized name replaces the Mac name** [Verified: `a/b` → `a_b`,
   `trail.` → `trail`].
3. The 8.3 name: characters DOS forbids, and every byte from `$80` up, become `_`; spaces are dropped; letters are
   upper-cased. A name that does not fit 8.3 exactly is cut to 6 characters plus `~1`, the extension to 3; on a
   collision the number grows and the `~` moves left. Device names (`CON`, `COM1`) are not checked.
4. A record and a long name (the Mac name as precomposed UTF-16) are always written, even when the name fits 8.3.

| Mac name | 8.3 name |
| --- | --- |
| `Hello World` | `HELLOW~1` |
| `a/b` | `A_B` |
| `Résumé` | `R_SUM_~1` |
| `readme.txt` | `README.TXT` |
| `a.b.c` | `AB~1.C` |
| `   lead` | `LEAD~1` |
| `trail.` | `TRAIL` |
| `12345678.1234` | `123456~1.123` |
| `.profile` | `~1.PRO` |
| `COM1` | `COM1` |
| `CON.txt` | `CON.TXT` |
| `ÄÖÜ ß` | `____~1` |
| `™®©` | `___~1` |
| a 31-character name starting `ABCDEF` | `ABCDEF~1` |

Dates are written exactly into the record and truncated to even seconds in the DOS entry, with Mac years 1904–1979
stored as DOS 2032–2107 ([fat.md §8.2](fat.md#82-how-the-mac-reads-them)).

### 5.2 Formatting

From the code only [Code, not verified]:

- OEM name `PCX_3.0 `; FAT12 below 16 MB, FAT16 below 1 GB, FAT32 above.
- A 1.44 MB floppy: 1 sector per cluster, 224 root entries, media `$F0`, 9 sectors per FAT, 18 sectors per track,
  2 heads.
- Serial number: the tick count at format time. Label: upper-cased, at $2B.
- Root directory: entries 0 and 1 marked deleted (`$E5`), then the label entry with attributes `$28`. No `FINDER.DAT`
  or `RESOURCE.FRK` is written at format time.
- Floppies and file-backed devices get no partition table. Other disks get one partition at sector 32, with a geometry
  of 64 heads × 32 sectors per track, of type `$01`, `$04`, `$06`, `$0B` or `$0C` by size.

---

## 6. PC Exchange and File Exchange folders

PC Exchange (System 7.1–Mac OS 8) and File Exchange (Mac OS 9) let the Mac use DOS disks. They keep what FAT cannot
hold in two hidden items in **every directory**. The format is the same in PC Exchange 1.0.4 and File Exchange
3.0.2 [Code]; PC Exchange 2.x was not examined, and the same record code at both ends of the range suggests it is the
same [Fitted?]. No header or version number exists anywhere [Code].

### 6.1 `RESOURCE.FRK`

- A subdirectory named `RESOURCE.FRK` with the attributes hidden + directory ($12) [Code] [Verified].
- It holds one file per data file that has a resource fork, under **the data file's 8.3 name**, containing the raw
  fork with no header [Code] [Verified]. Its attributes are the data file's with archive ($20) added; its dates are
  when it was written [Code].
- It is created at the first resource-fork write; deleting the data file deletes the fork file; move and rename carry
  it along; `RESOURCE.FRK` is removed when nothing else is left in its directory [Code].
- A missing fork file means an empty resource fork [Code]. A fork file made by hand opens normally [Verified].

### 6.2 `FINDER.DAT`

A hidden file ($02; the archive bit, $22, appears after the Mac writes it) of 92-byte records, one per item of the
directory that has one [Code] [Verified]. An item's record is in its **parent's** `FINDER.DAT` [Code].

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | `u8` | length of the Mac name, 0–31; **0 marks a free record** [Code] |
| +$01 | 31 | bytes | the Mac name, Mac OS Roman. Bytes after the name are garbage [Code] [Verified] |
| +$20 | 16 | `FInfo` | Finder info (`DInfo` for a folder), [host-folders.md §3](../containers/host-folders.md#3-finder-info) [Code] |
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

### 6.3 Packing and free records

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

**Cluster size.** The file does not record it. On a FAT volume the reader knows it (see [fat.md](fat.md)). For a
`FINDER.DAT` found on the host, ClassicMac tries tight packing (every 92 bytes) and cluster sizes of 512 to 65,536
bytes (powers of two), counts the used records under each, and keeps the size under which every used record is
plausible — name length 1–31 and all eleven DOS-name bytes in $20–$7E — with the most used records; the smaller size
wins a tie. If no size gives only plausible records, no records are read [Fitted].

### 6.4 When records are made

- Getting catalog information for an item with no file number creates a record, so **browsing a writable disk
  creates records** [Code] [Verified]. Records are also made when the Mac name differs from the 8.3 name and
  whenever Finder info is set [Code], but only while the volume's "save info" flag is set [Code].
- A new record has type `TEXT`, creator `'dosa'`, flags 0 and `fdPutAway` 2 [Code] [Verified], and a file number
  `$7FFFFFxx` from the counter, which counts down [Verified]. The mapped type (§8) is not written back to it
  [Verified]. Its dates are 0 in File Exchange 3.0.2 [Verified]; the code of both versions leaves them from
  uninitialised memory [Code].
- A record File Exchange created for a file that had a resource fork but no record got a **garbage Mac name**
  [Verified]: its name routine gives up without filling its buffer when its directory search misses, and the record
  takes its name from that uninitialised buffer [Code]. File Exchange shows such names as they are; see
  [fat.md](fat.md) for the warning ClassicMac gives.
- File Exchange creates a record and a VFAT long name for every file the Mac creates, even one whose name fits 8.3
  [Verified].

### 6.5 Finding an item's record

- The Mac looks items up by the record's Mac name, then by the VFAT long name (whose checksum must match), then by
  the 8.3 name [Code] [Verified]. Records are matched to directory entries by the 8.3 key at +$50, **byte for byte**
  with no case folding; with duplicate keys the first in file order wins, for reading and writing, and free records
  are skipped [Code]. Looked up by Mac name, a record matches ignoring case but not diacritical marks [Code].
- On the host, the 8.3 name may not be visible. ClassicMac forms the key from the host name when it is a valid 8.3
  name — a stem of 1–8 and an extension of 0–3 characters, all in $21–$7E, one dot at most — upper-cased and
  space-padded to 8 + 3 (`FANTAS~1.EML` → `FANTAS~1EML`, `readme` → `README     `). Otherwise (a long host name)
  it matches the record's Mac name against the host name, ignoring case [Fitted].
- `RESOURCE.FRK` and `FINDER.DAT` are found whatever their case, as on FAT [ClassicMac]. The fork file is looked up
  under the host name [ClassicMac].

Advice from the traces [Verified]: trust a record's dates and file number more than its type, which can revert to
an earlier value around a close; ignore records whose DOS key matches no directory entry.

### 6.6 Dates

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
- File Exchange checks nothing: impossible fields roll over through `Date2Secs` arithmetic (month 13 is January of
  the next year, day 0 the day before the 1st), a zero creation date shows as now and a zero modification date as
  1979-12-01 [Code] [Verified]; see [fat.md §8.2](fat.md#82-how-the-mac-reads-them). ClassicMac's FAT reader does the same, except that
  a zero creation date takes the record's +$40 [ClassicMac].

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

### 6.7 Flags from DOS attributes

- DOS hidden or system → the file is invisible (`$4000`) [Code]. The flag is ORed onto the stored Finder flags and
  never cleared, so a record storing `$4000` keeps the file invisible after the PC clears hidden [Code]. ClassicMac
  adds the invisible flag the same way [ClassicMac].
- DOS read-only or system → the file is locked [Code]. ClassicMac does not carry a locked attribute [ClassicMac].
- The archive attribute is ignored [Code].
- Type and creator `'scut'`/`'dosa'` make the file an alias (File Exchange only); a `.lnk` file is not mapped to them
  by the default map, and its contents are never translated [Code]. ClassicMac does not set the alias flag.

### 6.8 Without a record

File Exchange gives a file with no record the blank Finder info: `TEXT`/`'dosa'`, flags 0, and a put-away folder of
2 (the root directory's ID, never read) [Code]. A `RESOURCE.FRK` fork plays no part in it: it only gives the fork's
length [Code]. The extension map (§8) applies only when a record exists; on a writable volume with "save
info" on, the Mac creates the record before reading the Finder info, so the mapped type shows there [Code]
[Verified], while on a locked or read-only volume, or with "save info" off, the file stays `TEXT`/`'dosa'`: the
only mapping path needs a record [Code]. A volume the Mac sees as locked is not yet checked; an image read-only only
on the host is not one (the Mac mounts it writable, makes records in its cache and maps through them), and "save
info" off was not tested. See §2.2.

ClassicMac gives the same placeholder with the DOS dates, on a FAT volume [ClassicMac] and in a host folder, where a
file with a `RESOURCE.FRK` fork but no record gets the placeholder and the host file's times as its dates (§6.6). The extension map applies to such a file, as on a writable volume, on FAT volumes and in host folders alike
[ClassicMac].

---

## 7. File Exchange names

ClassicMac does not write FAT volumes; the Mac-to-FAT rules are given for completeness and for writers.

### 7.1 Creating a file: Mac name to FAT

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

### 7.2 Reading a file with no record: long name to Mac name

[Code] [Verified]:

- The long name is converted as stored, with no Unicode normalization, to the system script's encoding (Mac OS
  Roman on a Roman system) [Code]. The converter does not compose a decomposed name: a combining mark has no byte,
  so the name takes the low-byte path below, and can hold a `$00` [Verified: `Cafe`+U+0301`.txt` shows as `Cafe`,
  `$01`, `.txt`]. ClassicMac converts to Mac OS Roman, as stored, the same way [ClassicMac].
- If **every** character has a Mac OS Roman byte, the Mac OS Roman bytes are the name, **`:` included**
  (`a:b c.txt` shows with its colon).
- If even one character has none, the whole name takes another path: each UTF-16 unit's **low byte**, with `:`
  becoming `_` (`漢字 kanji.txt` shows as `"W kanji.txt`). File Exchange's own `_` fallback never takes effect.
- A result over 31 bytes is shortened to exactly 31: the start of the name, `#`, three upper-case hex digits, and the
  extension (`This is a very long Win#7C7.txt`).
  - The extension is from the last `.` among the final six characters of the name; among the final `length − 2` for
    names of 3–6 characters; none for shorter names.
  - The start keeps `27 − extension length` characters (the extension counting its dot).
  - The digits are the low 12 bits of a CRC-16 over the whole long name as stored (not normalized), as big-endian
    UTF-16: polynomial `$1021`, initial value 0, no reflection, no final XOR (the UDF unique-name checksum).
  - Both parts are converted the same way as the whole name would have been (Mac OS Roman, or low bytes).
- With no long name, the 8.3 name is the Mac name, byte for byte: no case change and no code page conversion, the
  stem cut at its first byte of `$20` or less (see §2.1).

---

## 8. The extension map

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

## 9. Diagnostics

Corrupt `FINDER.DAT` records are not diagnosed: the Mac uses them as they are [Code], and so does ClassicMac, except
for the free-record rules of §1.4.

---

## 10. Not covered

- The locked state from DOS attributes and the `'scut'`/`'dosa'` alias (§2.2); backup dates (§2.3); folder Finder
  info (§2.6).
- Open on the Mac side, to be checked on a running Mac OS 9.0: a file with no record on a volume the Mac sees as
  locked (an image write-protected in the emulator), and with "save info" off (§2.2).
- PC Exchange 2.x (Mac OS 7.5–8.x), not available for tracing; its creation-date rule is unknown.
- Writing PC Exchange / File Exchange data, `FILEID.DAT`, or FAT volumes.
- The locked flag from DOS read-only, and the backup date.
- PC Exchange 2.x, which was not examined.
