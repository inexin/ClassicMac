# FAT volumes

FAT12, FAT16 and FAT32 are the PC's file systems, on floppies, hard disks and removable media, with VFAT long names.
Classic Mac OS read and wrote them through PC Exchange (Mac OS 7.1–8) and File Exchange (Mac OS 9), which keep each
file's Mac name, Finder information, dates and resource fork in private files on the volume ([pc-exchange.md](pc-exchange.md)).
The FAT format is Microsoft's, so no Apple code decides it; Apple's code decides only how the Mac presents what it
finds. ClassicMac lists a FAT volume as Mac OS 9 lists it.

| | |
| --- | --- |
| Identified by | A boot sector at offset 0 with `55 AA` at byte 510 and a valid BPB (§2.2); or a DOS partition table listing FAT partitions ([mbr.md](mbr.md)) |
| ClassicMac | Reads: `ClassicMac.Files.Fat` (`FatReader`) |
| Verified against | Two FAT12 floppies File Exchange 3.0.2 wrote in SheepShaver, Mac OS 9.0, with the Mac's own listing of every file |
| Sources | Microsoft's FAT specification 1.03; File Exchange 3.0.2 and PC Exchange 1.0.4 (disassembly); `Date2Secs` of Mac OS 9.0; mtools, the Linux `msdos`/`vfat` drivers and DiscUtils (behaviour only) |

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

**FAT values are little-endian.** Every multi-byte field of the partition table, boot sector, allocation table and
directory entries is little-endian, as on the PC: the exception to the README's big-endian rule. The Mac data in
`FINDER.DAT` is big-endian, like every Mac structure ([pc-exchange.md §1.2](pc-exchange.md#12-finderdat)).

- A **sector** is the boot sector's bytes-per-sector value (normally 512); a partition table's sector numbers are
  always in 512-byte sectors.
- A **cluster** is the allocation unit, sectors per cluster × bytes per sector. Clusters are numbered from 2.
- An **8.3 name** is the 11-byte short name of a directory entry: 8 bytes of stem and 3 of extension, each padded with
  spaces, no dot (`FANTAS~1EML`). Shown as a file name it has a dot (`FANTAS~1.EML`).
- A **long name** is a VFAT name: UTF-16 code units, up to 255.

[Doc] in this document means Microsoft's FAT specification, version 1.03. "File Exchange" means File Exchange 3.0.2
as shipped with Mac OS 9.0 and "PC Exchange" PC Exchange 1.0.4; where they agree, "the Mac" means both. [Code] without
a version names File Exchange 3.0.2; [Verified] means checked on floppies File Exchange wrote in SheepShaver, Mac OS
9.0 (§7).

### 1.1 The boot sector

Sector 0 of the volume. All FAT types share the first 36 bytes (the BPB) [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 3 | `BS_jmpBoot` | A jump, `EB xx 90` or `E9 xx xx` |
| +$03 | 8 | `BS_OEMName` | Not used (File Exchange writes `PCX_3.0 `, §3) |
| +$0B | 2 | `BPB_BytsPerSec` | 512, 1024, 2048 or 4096 |
| +$0D | 1 | `BPB_SecPerClus` | A power of two, 1–128 |
| +$0E | 2 | `BPB_RsvdSecCnt` | Reserved sectors before the first FAT, this one included; not 0 |
| +$10 | 1 | `BPB_NumFATs` | Copies of the allocation table, normally 2 |
| +$11 | 2 | `BPB_RootEntCnt` | 32-byte entries in the fixed root directory; 0 on FAT32 |
| +$13 | 2 | `BPB_TotSec16` | Total sectors, or 0 when `BPB_TotSec32` holds it |
| +$15 | 1 | `BPB_Media` | Media descriptor (`$F0` floppy, `$F8` fixed); not used |
| +$16 | 2 | `BPB_FATSz16` | Sectors per FAT, or 0 on FAT32 |
| +$18 | 2 | `BPB_SecPerTrk` | Not used |
| +$1A | 2 | `BPB_NumHeads` | Not used |
| +$1C | 4 | `BPB_HiddSec` | Sectors before the volume on its disk; not used |
| +$20 | 4 | `BPB_TotSec32` | Total sectors when `BPB_TotSec16` is 0 |
| +$1FE | 2 | signature | `55 AA` |

Total sectors is `BPB_TotSec16`, or `BPB_TotSec32` when that is 0; sectors per FAT is `BPB_FATSz16`, or `BPB_FATSz32`
when that is 0 [Doc].

### 1.2 Extended fields

FAT12 and FAT16 [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$24 | 1 | `BS_DrvNum` | Not used |
| +$25 | 1 | `BS_Reserved1` | Reserved |
| +$26 | 1 | `BS_BootSig` | `$29` when the next three fields are present |
| +$27 | 4 | `BS_VolID` | Serial number; not used |
| +$2B | 11 | `BS_VolLab` | Volume label; not used (the root directory's label entry is authoritative) |
| +$36 | 8 | `BS_FilSysType` | `"FAT12   "` or similar; informational, never used to decide the type |

FAT32 [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$24 | 4 | `BPB_FATSz32` | Sectors per FAT |
| +$28 | 2 | `BPB_ExtFlags` | Bit 7 set: only FAT number (bits 0–3) is active; clear: all FATs mirrored |
| +$2A | 2 | `BPB_FSVer` | 0 |
| +$2C | 4 | `BPB_RootClus` | First cluster of the root directory; top 4 bits reserved |
| +$30 | 2 | `BPB_FSInfo` | Sector of the FSInfo structure; not used |
| +$32 | 2 | `BPB_BkBootSec` | Sector of the backup boot sector; not used |
| +$34 | 12 | `BPB_Reserved` | Reserved |
| +$40 | 1 | `BS_DrvNum` | Not used |
| +$41 | 1 | `BS_Reserved1` | Reserved |
| +$42 | 1 | `BS_BootSig` | |
| +$43 | 4 | `BS_VolID` | Not used |
| +$47 | 11 | `BS_VolLab` | Not used |
| +$52 | 8 | `BS_FilSysType` | `"FAT32   "`; informational |

### 1.3 Regions and the FAT type

In sectors from the start of the volume [Doc]:

```
RootDirSectors  = ceil(BPB_RootEntCnt × 32 / BPB_BytsPerSec)      (0 on FAT32)
FirstFAT        = BPB_RsvdSecCnt
RootDir         = BPB_RsvdSecCnt + BPB_NumFATs × FATSz             (FAT12 and FAT16 only)
FirstDataSec    = BPB_RsvdSecCnt + BPB_NumFATs × FATSz + RootDirSectors
DataSec         = TotSec − FirstDataSec
CountOfClusters = floor(DataSec / BPB_SecPerClus)
```

Cluster n (n ≥ 2) starts at sector `FirstDataSec + (n − 2) × BPB_SecPerClus`.

The type is decided by the cluster count **alone** [Doc]:

| CountOfClusters | Type |
| --- | --- |
| below 4085 | FAT12 |
| 4085 to 65524 | FAT16 |
| 65525 and over | FAT32 |

The file-system-type string, the partition type and the size of the FAT never decide it. The boundaries are exact: a
reader that is off by one misreads volumes a formatter deliberately placed near them [Doc].

The root directory [Doc]:

- FAT12 and FAT16: a fixed region of `BPB_RootEntCnt` entries starting at `RootDir`, outside the cluster area.
- FAT32: an ordinary cluster chain starting at `BPB_RootClus & $0FFFFFFF`.

### 1.4 The allocation table

The allocation table (FAT) has one entry per cluster, indexed by cluster number; entries 0 and 1 are reserved [Doc].

| Type | Entry | Entry n is at byte | Value |
| --- | --- | --- | --- |
| FAT12 | 12 bits | `n × 3 / 2` (integer division) | The u16 there: for even n its low 12 bits, for odd n its top 12 bits (`>> 4`) |
| FAT16 | 16 bits | `n × 2` | u16 |
| FAT32 | 32 bits | `n × 4` | u32 `& $0FFFFFFF`; the top 4 bits are reserved and ignored |

Values [Doc]:

| FAT12 | FAT16 | FAT32 | Meaning |
| --- | --- | --- | --- |
| `$000` | `$0000` | `$0000000` | Free |
| `$002`–`$FF6` | `$0002`–`$FFF6` | `$0000002`–`$FFFFFF6` | Next cluster of the chain |
| `$FF7` | `$FFF7` | `$FFFFFF7` | Bad cluster |
| `$FF8`–`$FFF` | `$FFF8`–`$FFFF` | `$FFFFFF8`–`$FFFFFFF` | End of chain |

### 1.5 Directory entries

A directory is an array of 32-byte entries [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 11 | `DIR_Name` | The 8.3 name, stem then extension, space-padded |
| +$0B | 1 | `DIR_Attr` | Attributes (below) |
| +$0C | 1 | `DIR_NTRes` | Reserved for Windows NT (lower-case flags); File Exchange ignores it [Code] |
| +$0D | 1 | `DIR_CrtTimeTenth` | Creation time, hundredths of a second 0–199; not used |
| +$0E | 2 | `DIR_CrtTime` | Creation time (§1.7) |
| +$10 | 2 | `DIR_CrtDate` | Creation date |
| +$12 | 2 | `DIR_LstAccDate` | Last access date; not used |
| +$14 | 2 | `DIR_FstClusHI` | High 16 bits of the first cluster; FAT32 only, 0 on FAT12/16 |
| +$16 | 2 | `DIR_WrtTime` | Modification time |
| +$18 | 2 | `DIR_WrtDate` | Modification date |
| +$1A | 2 | `DIR_FstClusLO` | Low 16 bits of the first cluster |
| +$1C | 4 | `DIR_FileSize` | Size in bytes; 0 for directories |

The first cluster is `DIR_FstClusLO`, with `DIR_FstClusHI << 16` added on FAT32 only. A file of size 0 has no
clusters; its first-cluster field is 0 [Doc].

The first name byte [Doc]: `$00` ends the directory (this entry and every later one are free); `$E5` marks a deleted
entry; `$05` stands for a real `$E5` (a Kanji lead byte).

Attributes [Doc]:

| Bit | Name | Meaning |
| --- | --- | --- |
| `$01` | `ATTR_READ_ONLY` | Read-only |
| `$02` | `ATTR_HIDDEN` | Hidden |
| `$04` | `ATTR_SYSTEM` | System |
| `$08` | `ATTR_VOLUME_ID` | The volume label; only in the root directory |
| `$10` | `ATTR_DIRECTORY` | A directory |
| `$20` | `ATTR_ARCHIVE` | Changed since backup |
| `$0F` | `ATTR_LONG_NAME` | All four low bits: a long-name entry (§1.6). The specification masks the byte with `$3F` before comparing |

The entries `.` and `..` at the start of every subdirectory point at the directory itself and its parent [Doc].

### 1.6 Long-name entries

A long name is stored in one or more 32-byte entries directly **before** the 8.3 entry it belongs to, last part first
[Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | `LDIR_Ord` | Part number 1–20 in bits 0–4; `$40` set on the last part (the first entry stored) |
| +$01 | 10 | `LDIR_Name1` | Characters 1–5 of this part, 5 × u16 |
| +$0B | 1 | `LDIR_Attr` | `$0F` |
| +$0C | 1 | `LDIR_Type` | 0 |
| +$0D | 1 | `LDIR_Chksum` | Checksum of the 8.3 name (below) |
| +$0E | 12 | `LDIR_Name2` | Characters 6–11, 6 × u16 |
| +$1A | 2 | `LDIR_FstClusLO` | 0 |
| +$1C | 4 | `LDIR_Name3` | Characters 12–13, 2 × u16 |

Each part holds 13 UTF-16 code units, little-endian; part n holds characters `(n − 1) × 13 + 1` to `n × 13`. A name
that does not fill its last part is ended by `$0000` and padded with `$FFFF` [Doc].

The checksum is over the 8.3 entry's 11 name bytes as stored, a leading `$05` included [Doc]:

```
sum = 0
for each of the 11 bytes b:
    sum = ((sum & 1) << 7) + (sum >> 1) + b     (all in 8 bits)
```

### 1.7 DOS dates and times

[Doc]:

| Field | Bits 15–9 | Bits 8–5 | Bits 4–0 |
| --- | --- | --- | --- |
| Date | year − 1980 (0–127) | month (1–12) | day (1–31) |

| Field | Bits 15–11 | Bits 10–5 | Bits 4–0 |
| --- | --- | --- | --- |
| Time | hours (0–23) | minutes (0–59) | seconds ÷ 2 (0–29) |

A date of 0 means none. DOS times have no time zone: they are the local time of whoever wrote them [Doc].

## 2. Reading

### 2.1 Finding a FAT volume

A FAT volume comes either bare (a floppy image, a volume dump: the boot sector at offset 0) or inside a DOS partition
table (a hard-disk or removable-media image). Sector 0 tells them apart:

1. If sector 0 passes the boot-sector test of §2.2, the image is a bare FAT volume.
2. Otherwise, if it passes the partition-table test of [mbr.md §2.1](mbr.md#21-recognising-a-table), each FAT
   partition it lists is a FAT volume, read as a bare one from its first sector.

A partitioned disk's sector 0 rarely passes the boot-sector test, and a boot sector's bytes at $1BE rarely pass the
table test, so the order matters only for odd images.

### 2.2 Recognising a boot sector

Each of these is a requirement of the FAT specification [Doc]; testing all of them is [ClassicMac] (the Mac's own mount
test is not traced):

1. The signature `55 AA` is at $1FE.
2. The first byte is `$EB` or `$E9`.
3. Bytes per sector is 512, 1024, 2048 or 4096.
4. Sectors per cluster is a non-zero power of two.
5. Reserved sectors, total sectors and sectors per FAT are non-zero, and there are 1 or 2 FATs.
6. The first data sector (§1.3) lies before the end of the volume.
7. A volume found to be FAT32 (§1.3) has `BPB_RootEntCnt` = 0.
8. The image holds at least the reserved sectors and the first FAT.

DOS 1.x floppies without a BPB do not pass.

### 2.3 Following a chain

A file or directory starts at the cluster in its directory entry and continues through the table until an
end-of-chain value [Doc]. A file's data is the chain's clusters in order, cut to the entry's file size; a reader may
stop once it has the file size in clusters, without reaching the end marker. A directory's size is its whole chain
[Doc].

### 2.4 Reading a directory

Entries are read in order [Doc]:

1. A first name byte of `$00` ends the directory.
2. A first name byte of `$E5` is a deleted entry: skip it, and discard any long-name parts gathered so far.
3. An entry with attributes `$0F` is a long-name part: gather it (§2.5).
4. An entry with `ATTR_VOLUME_ID` and without `ATTR_DIRECTORY` is the volume label: not a file, skip it.
5. `.` and `..` are skipped.
6. The rest are files and subdirectories. A leading `$05` is shown as `$E5`, but the stored bytes are kept for the
   `FINDER.DAT` and `RESOURCE.FRK` lookups ([pc-exchange.md §2.1](pc-exchange.md#21-finding-an-items-record)). A
   subdirectory's contents are read from its chain like the root's.

### 2.5 Assembling a long name

1. A part with `$40` set starts a new name: discard any parts gathered before it.
2. Gather each part's characters up to the first `$0000` or `$FFFF`, with its part number, and remember the checksum
   of the latest part.
3. At the next 8.3 entry, if parts were gathered and the remembered checksum equals the 8.3 name's checksum (§1.6),
   the long name is the parts in part-number order. Otherwise the entry has no long name. Either way, the parts are
   discarded.

A checksum mismatch means the 8.3 entry was changed by a system that does not know long names; the long name is then
stale and ignored [Doc]. File Exchange too uses a long name only when its checksum matches [Code].

### 2.6 How the Mac reads DOS dates

File Exchange [Code] [Verified]:

- A DOS date and time is read as a Mac local date: no time zone conversion, since Mac dates are local time too
  [Code].
- Seconds are even; `DIR_CrtTimeTenth` is not used [Code] [Verified: a creation date written as `$B0000001` reads
  back as `$B0000000`].
- **Years from 2032 on read 128 years earlier** [Code] [Verified]. File Exchange writes Mac years 1904–1979, which DOS
  cannot hold, as DOS years 2032–2107, and reads them back the same way: DOS 2040 reads as 1912, and Mac 1950 is
  written as DOS 2078 and reads back as 1950. Genuine DOS dates from 2032 on therefore read wrong.
- **Nothing is checked, and nothing reads as "no date"** [Code] [Verified]. File Exchange unpacks the fields as they
  are (year, month, day, hours, minutes, seconds × 2) and hands them to Mac OS 9's `Date2Secs`, which works in 16-bit
  day arithmetic with truncating division and clamps nothing [Code]. Impossible values roll over [Code] [Verified: all
  14 dates tried, which also confirms that this `Date2Secs` is the one Mac OS 9 installs]:

  | DOS date or time | Shown as |
  | --- | --- |
  | 1999, month 0, day 1 | 1998-12-02 (month 0 is 30 days back, not 31) |
  | 1999, month 0, day 0 | 1998-12-01 |
  | 1999-01, day 0 | 1998-12-31 (day 0 is the day before the 1st) |
  | 1999-03, day 0 | 1999-02-28 |
  | 1999-02-29 | 1999-03-01 |
  | 1999-04-31 | 1999-05-01 |
  | 1999, month 13, day 1 | 2000-01-01 |
  | 1999, month 14, day 1 | 2000-02-01 |
  | 1999, month 15, day 1 | 2000-03-02 (the month interpolation is a day off this far out) |
  | 1999, month 15, day 31 | 2000-04-01 |
  | date and time `$0000` | 1979-12-01 00:00 |
  | 1999-01-01 at 31:63:62 | 1999-01-02 08:04:02 (hours, minutes and seconds simply add) |
  | 2032-01, day 0 | 1947-04-29 17:31:44 (year 1904, day −1: 65,535 days, which overflow the `u32` seconds) |

- The one exception is the creation date: a creation date word of 0 shows as **now**; only the date word is tested,
  not the time [Code] [Verified]. A modification date of 0 is not special and shows as 1979-12-01 [Code] [Verified].

PC Exchange's dates differ ([pc-exchange.md §4](pc-exchange.md#4-variants)).

### 2.7 Listing a volume

Walking from the root directory (§1.3), for each directory:

1. Read the entries (§2.4), assembling long names (§2.5).
2. Read the directory's `FINDER.DAT` records and its `RESOURCE.FRK` entries, if present ([pc-exchange.md §1](pc-exchange.md#1-layout)).
3. For each entry other than the hidden items ([pc-exchange.md §2.7](pc-exchange.md#27-hidden-items)): a subdirectory is
   walked with its Mac name ([pc-exchange.md §2.2](pc-exchange.md#22-the-mac-name)) added to the path; a file is listed
   with its Mac name, Finder information, dates and forks ([pc-exchange.md §2](pc-exchange.md#2-reading)).

## 3. Writing

ClassicMac does not write FAT volumes. File Exchange's formatter writes [Code: File Exchange 3.0.2], read from the
code only and not checked on a running Mac:

- OEM name `PCX_3.0 `; FAT12 below 16 MB, FAT16 below 1 GB, FAT32 above.
- A 1.44 MB floppy: 1 sector per cluster, 224 root entries, media `$F0`, 9 sectors per FAT, 18 sectors per track, 2
  heads.
- Serial number: the tick count at format time. Label: upper-cased, at $2B.
- Root directory: entries 0 and 1 marked deleted (`$E5`), then the label entry with attributes `$28`. No `FINDER.DAT`
  or `RESOURCE.FRK` is written at format time.
- The partition table, when there is one, is in [mbr.md §3](mbr.md#3-writing).

What File Exchange writes for each file and its dates is in [pc-exchange.md §3](pc-exchange.md#3-writing).

## 4. Variants

- **FAT12, FAT16 and FAT32** differ in the entry size (§1.4), the root directory (§1.3) and `DIR_FstClusHI` (§1.5);
  the cluster count alone decides which (§1.3).
- **PC Exchange** reads FAT12 and FAT16 only; FAT32 needs File Exchange [Code]. PC Exchange ignores long names [Code].

## 5. ClassicMac

- Recognition is as in §2.1 and §2.2; testing for a boot sector before a partition table is ClassicMac's choice.
  Apple's formats (partition map, HFS, MFS) are tried before the DOS table and FAT ([hfs.md §5.1](hfs.md#51-recognising-a-volume)),
  so a disk that is both (an HFS volume with a stray `55 AA`) reads as the Apple format.
- Only the **first** FAT copy is read. `BPB_ExtFlags` is not consulted, so a FAT32 volume whose mirroring is off and
  whose active FAT is not the first is misread.
- A chain ends at a free cluster, at a cluster number below 2 or past `CountOfClusters + 1` (which includes the
  bad-cluster value on every volume), and at a cluster already visited. A chain that ends early, or runs past the end of
  the image, gives a shorter fork.
- `DIR_FstClusHI` is ignored on FAT12 and FAT16.
- The attribute byte is tested for exactly `$0F`, not masked with `$3F` first; the two differ only for entries with
  reserved bits set.
- Every entry whose name starts with `.` is skipped, which covers `.` and `..`; no valid 8.3 name starts with a dot.
- A subdirectory entry whose first cluster is 0, or whose chain starts at a cluster already read as a directory, is
  skipped, as it would make the walk loop.
- Long names: the part numbers are not checked for gaps, nor every part for the same checksum.
- DOS dates are converted as §2.6, rollover included, except a creation date word of 0: ClassicMac has no "now" to
  match and uses the `FINDER.DAT` record's creation date instead, or none ([pc-exchange.md §5](pc-exchange.md#5-classicmac)).
- The Finder's items on a disk mounted writable (`TheVolumeSettingsFolder`, `Desktop Folder`, `Trash`, `Desktop`) are
  ordinary entries and are listed like any other.
- The walk stops after `ContainerReadOptions.MaxVolumeEntries` files and folders (1,000,000 by default). The reader
  throws only when the input is not a FAT volume, or when one fork or directory would exceed
  `ContainerReadOptions.MaxExpandedBytesPerInput` (1 GiB by default); the unwrapper then reports `container.unreadable`.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `fat.bad-chain` | Error | A cluster chain reaches a free cluster, a cluster number outside the volume (the bad-cluster mark included) or a cluster it already passed | Ends the chain there; `fat.short` follows when a size was expected | Not traced |
| `fat.folder-loop` | Error | A subdirectory's first cluster is 0 or was already read as a directory | Skips the subdirectory and its contents | Not traced |
| `fat.short` | Error | A file, fork, `FINDER.DAT` or directory has fewer bytes in the image than its size or chain calls for | Returns the bytes that are there | Not traced |
| `fat.suspect-name` | Warning | A `FINDER.DAT` record's Mac name contains bytes `$00`–`$1F` ([pc-exchange.md §2.2](pc-exchange.md#22-the-mac-name)) | Shows the name as it is | Shows it as it is [Verified] |
| `fat.too-many-entries` | Error | More than `MaxVolumeEntries` files and folders | Stops the walk; keeps the files read | No such limit |

## 7. Verification

- `tests/ClassicMac.Files.Tests/FatTests.cs` builds volumes with `FatBuilder.cs`: files, folders and long names on
  FAT12, FAT16 and FAT32; File Exchange data; 8.3 names shown byte for byte; a looping chain, a cluster outside the
  volume, a folder pointing at its parent and the entry limit; non-FAT boot sectors refused; nesting through the
  unwrapper; and the DOS dates of §2.6 through `Date2Secs`.
- `FatTests.Floppies_written_by_File_Exchange_read_as_OS_9_listed_them` reads two FAT12 floppies File Exchange 3.0.2
  wrote in SheepShaver, Mac OS 9.0, with the Mac's own listing beside each (under `CLASSICMAC_CORPUS`, not in the
  repository). With the extension-map entries the Mac showed, ClassicMac's listing matches the Mac's for every file it
  listed: folder, name, data and resource fork lengths, type, creator, Finder flags, creation and modification dates
  [Verified]. The one exception is the Finder's `Desktop` file, which the Finder rewrote after the listing. The only
  diagnostics are `fat.suspect-name` for the garbage names of [pc-exchange.md §3.3](pc-exchange.md#33-when-records-are-made),
  which match the Mac's listing byte for byte.
- The Finder items of §5 were seen on those floppies [Verified].

## 8. Not covered

- Writing FAT volumes (§3 gives what File Exchange writes, for reference).
- FAT32 FSInfo, the backup boot sector, and active-FAT selection by `BPB_ExtFlags`.
- ProDOS volumes, which File Exchange also mounts with ProDOS's own extended files; ISO 9660 is in
  [iso9660.md](iso9660.md).

## 9. References

1. Microsoft, *Microsoft Extensible Firmware Initiative FAT32 File System Specification — FAT: General Overview of
   On-Disk Format*, version 1.03 (December 2000): the boot sector, the type decision, the allocation table, directory
   entries and long names.
2. File Exchange 3.0.2 as shipped with Mac OS 9.0, and PC Exchange 1.0.4, traced in disassembly; Mac OS 9.0's
   `Date2Secs`.
3. mtools; GPL-3.0. Behaviour only.
4. The Linux `msdos` and `vfat` drivers; GPL-2.0. Behaviour only.
5. DiscUtils; MIT. Behaviour only.
