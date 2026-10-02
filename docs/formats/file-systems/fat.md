# FAT volumes

This document describes FAT volumes as classic Mac OS reads them, completely enough to write a reader without
ClassicMac's code: the DOS partition table that may hold them, the FAT12, FAT16 and FAT32 on-disk format with VFAT
long names, and the private data PC Exchange (Mac OS 7.1–8) and File Exchange (Mac OS 9) keep on a FAT volume so that
each file has a Mac name, Finder info, dates and a resource fork. The goal is a listing identical to the one Mac OS 9
shows for the same disk. It is the behaviour of `ClassicMac.Files.Fat` (`FatReader`, `MbrReader`) and
`ClassicMac.Files.Containers` (`PcExchange`, `ExtensionMap`).

References:

- **Microsoft**, *Microsoft Extensible Firmware Initiative FAT32 File System Specification — FAT: General Overview of
  On-Disk Format*, version 1.03 (December 2000). Called "the FAT specification" below. It defines the boot sector, the
  type decision, the allocation table, directory entries and long names.
- **UEFI Forum**, *Unified Extensible Firmware Interface Specification*, section 5.2.1 "Legacy Master Boot Record",
  for the partition table layout; **Microsoft**'s partition-type constants (`PARTITION_FAT_12` and the rest, Windows
  `PARTITION_INFORMATION`) for the type values.
- **Apple**, File Exchange 3.0.2 as shipped with Mac OS 9.0, and PC Exchange 1.0.4, as traced in disassembly: the
  `FINDER.DAT` and `RESOURCE.FRK` format, dates, name conversion and type mapping. Apple never documented them.
- Floppies written by File Exchange in SheepShaver, Mac OS 9.0, with the Mac's own listing of every file.

The FAT format is Microsoft's, not Apple's, so no Apple code decides it; Apple's code decides only how the Mac presents
what it finds. Other FAT implementations (mtools, the Linux `msdos`/`vfat` drivers, DiscUtils) are behavioural
references only.

Contents

1. [Conventions](#1-conventions)
2. [Finding a FAT volume](#2-finding-a-fat-volume)
3. [The boot sector](#3-the-boot-sector)
4. [Layout and FAT type](#4-layout-and-fat-type)
5. [The allocation table and cluster chains](#5-the-allocation-table-and-cluster-chains)
6. [Directories](#6-directories)
7. [Long names (VFAT)](#7-long-names-vfat)
8. [DOS dates and times](#8-dos-dates-and-times)
9. [The listing, compared with Mac OS 9's](#9-the-listing-compared-with-mac-os-9s)
10. [Diagnostics](#10-diagnostics)
11. [Not covered](#11-not-covered)

---

## 1. Conventions

The shared conventions of [README.md](../README.md) apply, with these exceptions and additions.

- **FAT values are little-endian.** Every multi-byte field of the partition table, boot sector, allocation table and
  directory entries is little-endian, as on the PC. This is the exception to README.md's big-endian rule. The Mac
  data inside `FINDER.DAT` ([pc-exchange.md §1](pc-exchange.md#1-the-mac-data-resourcefrk-and-finderdat)) is big-endian, like every Mac structure.
- Offsets are hex (`$0B`), sizes decimal. A **sector** is the boot sector's bytes-per-sector value (normally 512); a
  partition table's sector numbers are always in 512-byte sectors.
- A **cluster** is the allocation unit: sectors per cluster × bytes per sector. Clusters are numbered from 2.
- An **8.3 name** is the 11-byte short name of a directory entry: 8 bytes of stem and 3 of extension, each padded
  with spaces, no dot (`FANTAS~1EML`). Shown as a file name, it is written with a dot (`FANTAS~1.EML`).
- A **long name** is a VFAT name: UTF-16 code units, up to 255.
- **File Exchange** means File Exchange 3.0.2 as shipped with Mac OS 9.0; **PC Exchange** means PC Exchange 1.0.4.
  Where they agree, "the Mac" means both. [Code] without a version names File Exchange 3.0.2.
- [Doc] on a FAT rule means the FAT specification; on a partition-table rule, the UEFI specification or Microsoft's
  partition-type constants.
- What ClassicMac does with damage (loops, missing clusters, limits) is ClassicMac's own policy, not a rule of the
  format; it is described in §10 and carries a tag only where the Mac's behaviour is known.

---

## 2. Finding a FAT volume

A FAT volume comes either **bare** (a floppy image, a volume dump: the boot sector is at offset 0) or **inside a DOS
partition table** (a hard-disk or removable-media image). The two are told apart by sector 0:

1. If sector 0 passes the boot-sector test of §3.3, the image is a bare FAT volume.
2. Otherwise, if sector 0 passes the partition-table test of [mbr.md §2](mbr.md#2-recognising-a-table), each FAT partition it lists is a FAT volume,
   read as a bare one from its first sector.

A partitioned disk's sector 0 rarely passes the boot-sector test, and a boot sector's bytes at $1BE rarely pass the
table test, so the order matters only for odd images; testing for a boot sector first is ClassicMac's choice.

ClassicMac tries Apple's formats (Apple partition map, HFS, MFS) before the DOS table and FAT, so a disk that is both
(an HFS volume with a stray `55 AA`) reads as the Apple format.

---

## 3. The boot sector

### 3.1 Common fields (the BPB)

Sector 0 of the volume. All FAT types share the first 36 bytes [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $00 | 3 | bytes | `BS_jmpBoot`: a jump, `EB xx 90` or `E9 xx xx` |
| $03 | 8 | chars | `BS_OEMName`; ignored (File Exchange writes `PCX_3.0 ` [Code]) |
| $0B | 2 | u16 | `BPB_BytsPerSec`: 512, 1024, 2048 or 4096 |
| $0D | 1 | u8 | `BPB_SecPerClus`: a power of two, 1–128 |
| $0E | 2 | u16 | `BPB_RsvdSecCnt`: reserved sectors before the first FAT, including this one; not 0 |
| $10 | 1 | u8 | `BPB_NumFATs`: copies of the allocation table, normally 2 |
| $11 | 2 | u16 | `BPB_RootEntCnt`: 32-byte entries in the fixed root directory; 0 on FAT32 |
| $13 | 2 | u16 | `BPB_TotSec16`: total sectors, or 0 when `BPB_TotSec32` holds it |
| $15 | 1 | u8 | `BPB_Media`: media descriptor (`$F0` floppy, `$F8` fixed); ignored |
| $16 | 2 | u16 | `BPB_FATSz16`: sectors per FAT, or 0 on FAT32 |
| $18 | 2 | u16 | `BPB_SecPerTrk`; ignored |
| $1A | 2 | u16 | `BPB_NumHeads`; ignored |
| $1C | 4 | u32 | `BPB_HiddSec`: sectors before the volume on its disk; ignored |
| $20 | 4 | u32 | `BPB_TotSec32`: total sectors when `BPB_TotSec16` is 0 |
| $1FE | 2 | bytes | Signature `55 AA` |

### 3.2 Extended fields

FAT12 and FAT16 [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $24 | 1 | u8 | `BS_DrvNum`; ignored |
| $25 | 1 | u8 | `BS_Reserved1` |
| $26 | 1 | u8 | `BS_BootSig`: `$29` when the next three fields are present |
| $27 | 4 | u32 | `BS_VolID`: serial number; ignored (File Exchange writes the tick count [Code]) |
| $2B | 11 | chars | `BS_VolLab`: volume label; ignored (the root directory's label entry is authoritative) |
| $36 | 8 | chars | `BS_FilSysType`: `"FAT12   "` or similar; informational only, never used to decide the type |

FAT32 [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $24 | 4 | u32 | `BPB_FATSz32`: sectors per FAT |
| $28 | 2 | u16 | `BPB_ExtFlags`: bit 7 set = only FAT number (bits 0–3) is active; clear = all FATs mirrored |
| $2A | 2 | u16 | `BPB_FSVer`: 0 |
| $2C | 4 | u32 | `BPB_RootClus`: first cluster of the root directory (top 4 bits reserved) |
| $30 | 2 | u16 | `BPB_FSInfo`: sector of the FSInfo structure; ignored |
| $32 | 2 | u16 | `BPB_BkBootSec`: sector of the backup boot sector; ignored |
| $34 | 12 | bytes | `BPB_Reserved` |
| $40 | 1 | u8 | `BS_DrvNum`; ignored |
| $41 | 1 | u8 | `BS_Reserved1` |
| $42 | 1 | u8 | `BS_BootSig` |
| $43 | 4 | u32 | `BS_VolID`; ignored |
| $47 | 11 | chars | `BS_VolLab`; ignored |
| $52 | 8 | chars | `BS_FilSysType`: `"FAT32   "`; informational only |

Two fields have 16- and 32-bit forms: total sectors is `BPB_TotSec16`, or `BPB_TotSec32` when that is 0; sectors per
FAT is `BPB_FATSz16`, or `BPB_FATSz32` when that is 0 [Doc].

### 3.3 Recognising a boot sector

ClassicMac accepts sector 0 as a FAT boot sector when all of these hold; each is a requirement of the FAT
specification [Doc], but the selection is ClassicMac's (the Mac's own mount test is not traced):

- the signature `55 AA` is at $1FE;
- the first byte is `$EB` or `$E9`;
- bytes per sector is 512, 1024, 2048 or 4096;
- sectors per cluster is a non-zero power of two;
- reserved sectors, total sectors and sectors per FAT are non-zero, and there are 1 or 2 FATs;
- the first data sector (§4.1) lies before the end of the volume;
- a volume found to be FAT32 (§4.2) has `BPB_RootEntCnt` = 0;
- the image holds at least the reserved sectors and the first FAT.

DOS 1.x floppies without a BPB are not recognised.

---

## 4. Layout and FAT type

### 4.1 Regions

In sectors from the start of the volume [Doc]:

```
RootDirSectors = ceil(BPB_RootEntCnt × 32 / BPB_BytsPerSec)      (0 on FAT32)
FirstFAT       = BPB_RsvdSecCnt
RootDir        = BPB_RsvdSecCnt + BPB_NumFATs × FATSz              (FAT12 and FAT16 only)
FirstDataSec   = BPB_RsvdSecCnt + BPB_NumFATs × FATSz + RootDirSectors
DataSec        = TotSec − FirstDataSec
CountOfClusters = floor(DataSec / BPB_SecPerClus)
```

Cluster n (n ≥ 2) starts at sector `FirstDataSec + (n − 2) × BPB_SecPerClus` [Doc].

### 4.2 The FAT type

The type is decided by the cluster count **alone** [Doc]:

| CountOfClusters | Type |
| --- | --- |
| below 4085 | FAT12 |
| 4085 to 65524 | FAT16 |
| 65525 and over | FAT32 |

The file-system-type string, the partition type and the size of the FAT never decide it. The boundaries are exact: a
reader that is off by one misreads volumes a formatter deliberately placed near them [Doc].

PC Exchange reads FAT12 and FAT16 only; FAT32 needs File Exchange [Code].

### 4.3 The root directory

- **FAT12 and FAT16:** the root directory is a fixed region of `BPB_RootEntCnt` entries starting at `RootDir`,
  outside the cluster area [Doc].
- **FAT32:** the root directory is an ordinary cluster chain starting at `BPB_RootClus & $0FFFFFFF` [Doc].

---

## 5. The allocation table and cluster chains

### 5.1 Entries

The allocation table (FAT) has one entry per cluster, indexed by cluster number; entries 0 and 1 are reserved [Doc].

| Type | Entry | Entry n is at byte | Value |
| --- | --- | --- | --- |
| FAT12 | 12 bits | `n × 3 / 2` (integer division) | the u16 there: for even n its low 12 bits, for odd n its top 12 bits (`>> 4`) |
| FAT16 | 16 bits | `n × 2` | u16 |
| FAT32 | 32 bits | `n × 4` | u32 `& $0FFFFFFF` (the top 4 bits are reserved and ignored) |

Values [Doc]:

| FAT12 | FAT16 | FAT32 | Meaning |
| --- | --- | --- | --- |
| `$000` | `$0000` | `$0000000` | Free |
| `$002`–`$FF6` | `$0002`–`$FFF6` | `$0000002`–`$FFFFFF6` | Next cluster of the chain |
| `$FF7` | `$FFF7` | `$FFFFFF7` | Bad cluster |
| `$FF8`–`$FFF` | `$FFF8`–`$FFFF` | `$FFFFFF8`–`$FFFFFFF` | End of chain |

### 5.2 Following a chain

A file or directory starts at the cluster in its directory entry (§6.1) and continues through the table until an
end-of-chain value [Doc]. A file's data is the chain's clusters in order, cut to the entry's file size [Doc]; a reader
may stop once it has the file size in clusters, without reaching the end marker. A directory's size is its whole
chain.

ClassicMac reads the **first** FAT copy only. On FAT32 it does not consult `BPB_ExtFlags`, so a volume whose mirroring
is off and whose active FAT is not the first is misread.

ClassicMac ends a chain, with `fat.bad-chain`, at a free cluster, a cluster number past `CountOfClusters + 1` (which
includes the bad-cluster value on every volume) and at a cluster it has already visited. A chain that ends early, or
runs past the end of the image, gives a shorter fork, reported as `fat.short`.

A file of size 0 has no clusters; its first-cluster field is 0 [Doc].

---

## 6. Directories

### 6.1 Directory entries

A directory is an array of 32-byte entries [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $00 | 11 | bytes | `DIR_Name`: the 8.3 name, stem then extension, space-padded |
| $0B | 1 | u8 | `DIR_Attr`: attributes (§6.2) |
| $0C | 1 | u8 | `DIR_NTRes`: reserved for Windows NT (lower-case flags); ignored, by File Exchange too [Code] |
| $0D | 1 | u8 | `DIR_CrtTimeTenth`: creation time, hundredths of a second 0–199; ignored |
| $0E | 2 | u16 | `DIR_CrtTime`: creation time (§8) |
| $10 | 2 | u16 | `DIR_CrtDate`: creation date |
| $12 | 2 | u16 | `DIR_LstAccDate`: last access date; ignored |
| $14 | 2 | u16 | `DIR_FstClusHI`: high 16 bits of the first cluster (FAT32 only; 0 on FAT12/16) |
| $16 | 2 | u16 | `DIR_WrtTime`: modification time |
| $18 | 2 | u16 | `DIR_WrtDate`: modification date |
| $1A | 2 | u16 | `DIR_FstClusLO`: low 16 bits of the first cluster |
| $1C | 4 | u32 | `DIR_FileSize`: size in bytes; 0 for directories |

The first cluster is `DIR_FstClusLO`, with `DIR_FstClusHI << 16` added on FAT32 only [Doc]. ClassicMac ignores
`DIR_FstClusHI` on FAT12 and FAT16.

### 6.2 Attributes

| Bit | Name | Meaning |
| --- | --- | --- |
| `$01` | `ATTR_READ_ONLY` | Read-only [Doc] |
| `$02` | `ATTR_HIDDEN` | Hidden [Doc] |
| `$04` | `ATTR_SYSTEM` | System [Doc] |
| `$08` | `ATTR_VOLUME_ID` | The volume label; only in the root directory [Doc] |
| `$10` | `ATTR_DIRECTORY` | A directory [Doc] |
| `$20` | `ATTR_ARCHIVE` | Changed since backup [Doc] |
| `$0F` | `ATTR_LONG_NAME` | All four low bits: a long-name entry (§7) [Doc] |

### 6.3 Reading a directory

Entries are read in order [Doc]:

- A first name byte of `$00` ends the directory: this entry and every later one are free.
- A first name byte of `$E5` marks a deleted entry: skip it. It also discards any long-name parts gathered so far.
- A first name byte of `$05` stands for a real `$E5` (a Kanji lead byte): show it as `$E5` in the name, but keep the
  stored bytes for `FINDER.DAT` and `RESOURCE.FRK` lookups.
- An entry with attributes `$0F` is a long-name part (§7). ClassicMac tests the attribute byte for exactly
  `$0F`; the FAT specification masks it with `$3F` first, which differs only for entries with reserved bits set.
- An entry with `ATTR_VOLUME_ID` and without `ATTR_DIRECTORY` is the volume label: not a file, skip it.
- The entries `.` and `..` at the start of every subdirectory point at the directory itself and its parent: skip them.
  ClassicMac skips every entry whose name starts with `.`, which no valid 8.3 name does.

The rest are files and subdirectories. A subdirectory's contents are read from its chain like the root's.

A subdirectory entry whose first cluster is 0, or whose chain starts at a cluster already read as a directory, would
make the walk loop; ClassicMac skips it (`fat.folder-loop`).

---

## 7. Long names (VFAT)

### 7.1 Long-name entries

A long name is stored in one or more 32-byte entries directly **before** the 8.3 entry it belongs to, last part first
[Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $00 | 1 | u8 | `LDIR_Ord`: part number 1–20 in bits 0–4; `$40` set on the last part (the first entry stored) |
| $01 | 10 | u16 × 5 | Characters 1–5 of this part |
| $0B | 1 | u8 | `LDIR_Attr`: `$0F` |
| $0C | 1 | u8 | `LDIR_Type`: 0 |
| $0D | 1 | u8 | `LDIR_Chksum`: checksum of the 8.3 name (§7.2) |
| $0E | 12 | u16 × 6 | Characters 6–11 |
| $1A | 2 | u16 | `LDIR_FstClusLO`: 0 |
| $1C | 4 | u16 × 2 | Characters 12–13 |

Each part holds 13 UTF-16 code units, little-endian; part n holds characters `(n − 1) × 13 + 1` to `n × 13` [Doc]. A
name that does not fill its last part is ended by `$0000` and padded with `$FFFF` [Doc].

### 7.2 The checksum

The 8.3 entry's 11 name bytes, as stored (a leading `$05` included) [Doc]:

```
sum = 0
for each of the 11 bytes b:
    sum = ((sum & 1) << 7) + (sum >> 1) + b     (all in 8 bits)
```

### 7.3 Assembling a name

As ClassicMac reads them:

1. A part with `$40` set starts a new name: discard any parts gathered before it.
2. Gather each part's characters up to the first `$0000` or `$FFFF`, with its part number, and remember the checksum
   of the latest part.
3. At the next 8.3 entry, if parts were gathered and the remembered checksum equals the 8.3 name's checksum, the long
   name is the parts in part-number order. Otherwise the entry has no long name. Either way, the parts are discarded.

A checksum mismatch means the 8.3 entry was changed by a system that does not know long names; the long name is then
stale and ignored [Doc]. File Exchange also uses a long name only when its checksum matches [Code]. ClassicMac does not
check that the part numbers run without gaps, nor that every part carries the same checksum.

Long names exist on volumes File Exchange reads; PC Exchange ignores them [Code].

---

## 8. DOS dates and times

### 8.1 The fields

[Doc]:

| Field | Bits 15–9 | Bits 8–5 | Bits 4–0 |
| --- | --- | --- | --- |
| Date | year − 1980 (0–127) | month (1–12) | day (1–31) |

| Field | Bits 15–11 | Bits 10–5 | Bits 4–0 |
| --- | --- | --- | --- |
| Time | hours (0–23) | minutes (0–59) | seconds ÷ 2 (0–29) |

A date of 0 means none [Doc]. DOS times have no time zone; they are the local time of whoever wrote them [Doc].

### 8.2 How the Mac reads them

- A DOS date and time is read as a Mac local date: no time zone conversion, since Mac dates are local time too
  [Code].
- Seconds are even; the hundredths byte `DIR_CrtTimeTenth` is not used [Code] [Verified: a creation date written as
  `$B0000001` reads back as `$B0000000`].
- **Years from 2032 on read 128 years earlier** [Code] [Verified]. File Exchange writes Mac years 1904–1979, which DOS
  cannot hold, as DOS years 2032–2107, and reads them back the same way: DOS 2040 reads as 1912, and Mac 1950 is
  written as DOS 2078 and reads back as 1950. The cost is that genuine DOS dates from 2032 on read wrong. PC Exchange
  has no wrap; dates before 1980 come out as garbage [Code].
- **Nothing is checked, and nothing reads as "no date"** [Code] [Verified]. File Exchange unpacks the fields as they
  are (year, month, day, hours, minutes, seconds × 2) and hands them to Mac OS 9's `Date2Secs`, which works in 16-bit
  day arithmetic with truncating division and clamps nothing [Code]. So impossible values roll over [Code]
  [Verified: all 14 dates tried, which also confirms that this `Date2Secs` is the one Mac OS 9 installs]:

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

  The one exception is the creation date: a creation date word of 0 shows as **now** (only the date word is tested,
  not the time) [Code] [Verified]. A modification date of 0 is not special and shows as 1979-12-01 [Code] [Verified].
- ClassicMac converts every DOS date the same way, rollover included. The one difference is a creation date word of
  0: ClassicMac has no "now" to match and uses the record's creation date instead ([pc-exchange.md §2.3](pc-exchange.md#23-dates)).

---

## 9. The listing, compared with Mac OS 9's

### 9.1 What ClassicMac lists

Walking from the root directory (§4.3), for each directory:

1. Read the entries (§6.3), skipping free and deleted entries, the volume label, `.` and `..`, and assembling long names.
2. Read the directory's `FINDER.DAT` records ([pc-exchange.md §§1.2–1.4](pc-exchange.md#12-finderdat)) and its `RESOURCE.FRK` entries ([pc-exchange.md §1.1](pc-exchange.md#11-resourcefrk)), if present.
3. For each entry other than the hidden items ([pc-exchange.md §2.5](pc-exchange.md#25-hidden-items)): a subdirectory is walked with its Mac name ([pc-exchange.md §2.1](pc-exchange.md#21-name)) added to the
   path; a file is listed with its name, Finder info, dates and forks ([pc-exchange.md §2](pc-exchange.md#2-the-mac-view-of-a-file)), then the extension map if one was
   supplied ([pc-exchange.md §4.4](pc-exchange.md#44-classicmac-opt-in)), then the invisible flag from DOS attributes ([pc-exchange.md §2.2](pc-exchange.md#22-finder-info)).

Items the Finder creates when it mounts a disk writable — `TheVolumeSettingsFolder`, `Desktop Folder`, `Trash` and a
hidden `Desktop` file with a resource fork — are ordinary FAT entries, and are listed like any other [Verified].

### 9.2 Verification

On two FAT12 floppies written by File Exchange in SheepShaver, Mac OS 9.0, ClassicMac's listing, with the
extension-map entries the Mac showed, matches Mac OS 9's own for every file the Mac listed: folder, name, data and
resource fork lengths, type, creator, Finder flags, creation and modification dates [Verified]. The only exception is
the Finder's `Desktop` file, which the Finder rewrote after the listing was taken. The only diagnostics are
`fat.suspect-name` for the garbage names of [pc-exchange.md §1.5](pc-exchange.md#15-when-records-exist), which match the Mac's listing byte for byte.

---

## 10. Diagnostics

ClassicMac reports each problem and reads on; it throws only when the input is not a FAT volume or partition table at
all, or when one fork or directory would exceed the per-input size limit
(`ContainerReadOptions.MaxExpandedBytesPerInput`, 1 GiB), which the container unwrapper reports as
`container.unreadable`.

| Code | Severity | Meaning | ClassicMac does | The Mac |
| --- | --- | --- | --- | --- |
| `fat.bad-chain` | Error | A cluster chain reaches a free cluster, a cluster number outside the volume (including the bad-cluster mark) or a cluster it already passed | Ends the chain there; the fork is short (`fat.short` follows when a size was expected) | Not traced |
| `fat.short` | Error | A file, fork, `FINDER.DAT` or directory has fewer bytes in the image than its size or chain calls for | Returns the bytes that are there | Not traced |
| `fat.folder-loop` | Error | A subdirectory's first cluster is 0 or was already read as a directory | Skips the subdirectory and its contents | Not traced |
| `fat.too-many-entries` | Error | The volume holds more files and folders than `ContainerReadOptions.MaxVolumeEntries` (default 1,000,000) | Stops the walk; files read so far are kept | No such limit |
| `fat.suspect-name` | Warning | A `FINDER.DAT` record's Mac name contains bytes `$00`–`$1F` | Shows the name as it is | Shows it as it is [Verified] |

---

## 11. Not covered

- Writing FAT volumes ([pc-exchange.md §5](pc-exchange.md#5-what-file-exchange-writes) describes what File Exchange writes, for reference).
- FAT32 FSInfo, the backup boot sector, and active-FAT selection by `BPB_ExtFlags` (§5.2).
- ProDOS volumes, which File Exchange also mounts with ProDOS's own extended files, and ISO 9660
  ([iso9660.md](iso9660.md)).
