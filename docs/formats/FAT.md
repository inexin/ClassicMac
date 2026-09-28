# FAT volumes and their Mac data — an implementer's specification

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
3. [DOS partition tables](#3-dos-partition-tables)
4. [The boot sector](#4-the-boot-sector)
5. [Layout and FAT type](#5-layout-and-fat-type)
6. [The allocation table and cluster chains](#6-the-allocation-table-and-cluster-chains)
7. [Directories](#7-directories)
8. [Long names (VFAT)](#8-long-names-vfat)
9. [DOS dates and times](#9-dos-dates-and-times)
10. [The Mac data: `RESOURCE.FRK` and `FINDER.DAT`](#10-the-mac-data-resourcefrk-and-finderdat)
11. [The Mac view of a file](#11-the-mac-view-of-a-file)
12. [Long names as File Exchange converts them](#12-long-names-as-file-exchange-converts-them)
13. [Types by name ending](#13-types-by-name-ending)
14. [The listing, compared with Mac OS 9's](#14-the-listing-compared-with-mac-os-9s)
15. [What File Exchange writes](#15-what-file-exchange-writes)
16. [Diagnostics](#16-diagnostics)
17. [Not covered](#17-not-covered)

---

## 1. Conventions

The shared conventions of [README.md](README.md) apply, with these exceptions and additions.

- **FAT values are little-endian.** Every multi-byte field of the partition table, boot sector, allocation table and
  directory entries is little-endian, as on the PC. This is the exception to README.md's big-endian rule. The Mac
  data inside `FINDER.DAT` (section 10) is big-endian, like every Mac structure.
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
  format; it is described in section 16 and carries a tag only where the Mac's behaviour is known.

---

## 2. Finding a FAT volume

A FAT volume comes either **bare** (a floppy image, a volume dump: the boot sector is at offset 0) or **inside a DOS
partition table** (a hard-disk or removable-media image). The two are told apart by sector 0:

1. If sector 0 passes the boot-sector test of section 4.3, the image is a bare FAT volume.
2. Otherwise, if sector 0 passes the partition-table test of section 3.2, each FAT partition it lists is a FAT volume,
   read as a bare one from its first sector.

A partitioned disk's sector 0 rarely passes the boot-sector test, and a boot sector's bytes at $1BE rarely pass the
table test, so the order matters only for odd images; testing for a boot sector first is ClassicMac's choice.

ClassicMac tries Apple's formats (Apple partition map, HFS, MFS) before the DOS table and FAT, so a disk that is both
(an HFS volume with a stray `55 AA`) reads as the Apple format.

---

## 3. DOS partition tables

### 3.1 Layout

Sector 0 (512 bytes) of a partitioned disk holds boot code, four partition entries and a signature [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $000 | 446 | bytes | Boot code; ignored |
| $1BE | 16 | entry | Partition 1 |
| $1CE | 16 | entry | Partition 2 |
| $1DE | 16 | entry | Partition 3 |
| $1EE | 16 | entry | Partition 4 |
| $1FE | 2 | bytes | Signature `55 AA` |

Each entry [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $0 | 1 | u8 | Status: `$80` bootable, `$00` not |
| $1 | 3 | bytes | CHS address of the first sector; ignored |
| $4 | 1 | u8 | Partition type; `$00` for an unused entry |
| $5 | 3 | bytes | CHS address of the last sector; ignored |
| $8 | 4 | u32 | First sector (LBA), in 512-byte sectors from the start of the disk |
| $C | 4 | u32 | Number of sectors, in 512-byte sectors |

UEFI firmware ignores the CHS fields and uses the LBA ones [Doc]; so does ClassicMac.

### 3.2 Recognising a table

ClassicMac takes sector 0 as a DOS partition table when all of these hold:

- it ends in `55 AA` [Doc];
- it is not a FAT boot sector (section 4.3);
- every entry's status byte is `$00` or `$80` [Doc];
- at least one entry has a FAT type (3.3) and a non-zero first sector.

The last two conditions are ClassicMac's heuristic; the Mac's own partition recognition for DOS disks is not traced.

### 3.3 Partition types

| Type | Meaning | Read |
| --- | --- | --- |
| `$01` | FAT12 | yes |
| `$04` | FAT16, under 32 MB | yes |
| `$06` | FAT16, 32 MB and over ("huge") | yes |
| `$0B` | FAT32 | yes |
| `$0C` | FAT32, LBA addressing | yes |
| `$0E` | FAT16, LBA addressing | yes |
| `$05`, `$0F` | Extended partition | no |
| anything else | Not FAT (`$07` NTFS, `$83` Linux, `$EE` GPT protective, …) | no |

The values are Microsoft's [Doc]. File Exchange's formatter writes `$01`, `$04`, `$06`, `$0B` or `$0C` by size
[Code, not verified]. The partition type is only a hint: the volume's own boot sector decides the FAT type (5.2) [Doc].

### 3.4 Reading

- An entry of type `$00` is unused and skipped silently.
- An entry of another non-FAT type is skipped (`mbr.skipped`).
- A FAT entry whose first sector is 0 or lies past the end of the image is skipped (`mbr.outside`).
- A FAT entry running past the end of the image is cut to the image (`mbr.truncated`) and read.
- Each remaining entry is a FAT volume from byte `first × 512`, `count × 512` bytes long. ClassicMac names it
  `Partition n`, where n is the entry's slot, 1–4.

Extended partitions and the logical partitions inside them are not read; neither is a GUID partition table.

---

## 4. The boot sector

### 4.1 Common fields (the BPB)

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

### 4.2 Extended fields

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

### 4.3 Recognising a boot sector

ClassicMac accepts sector 0 as a FAT boot sector when all of these hold; each is a requirement of the FAT
specification [Doc], but the selection is ClassicMac's (the Mac's own mount test is not traced):

- the signature `55 AA` is at $1FE;
- the first byte is `$EB` or `$E9`;
- bytes per sector is 512, 1024, 2048 or 4096;
- sectors per cluster is a non-zero power of two;
- reserved sectors, total sectors and sectors per FAT are non-zero, and there are 1 or 2 FATs;
- the first data sector (5.1) lies before the end of the volume;
- a volume found to be FAT32 (5.2) has `BPB_RootEntCnt` = 0;
- the image holds at least the reserved sectors and the first FAT.

DOS 1.x floppies without a BPB are not recognised.

---

## 5. Layout and FAT type

### 5.1 Regions

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

### 5.2 The FAT type

The type is decided by the cluster count **alone** [Doc]:

| CountOfClusters | Type |
| --- | --- |
| below 4085 | FAT12 |
| 4085 to 65524 | FAT16 |
| 65525 and over | FAT32 |

The file-system-type string, the partition type and the size of the FAT never decide it. The boundaries are exact: a
reader that is off by one misreads volumes a formatter deliberately placed near them [Doc].

PC Exchange reads FAT12 and FAT16 only; FAT32 needs File Exchange [Code].

### 5.3 The root directory

- **FAT12 and FAT16:** the root directory is a fixed region of `BPB_RootEntCnt` entries starting at `RootDir`,
  outside the cluster area [Doc].
- **FAT32:** the root directory is an ordinary cluster chain starting at `BPB_RootClus & $0FFFFFFF` [Doc].

---

## 6. The allocation table and cluster chains

### 6.1 Entries

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

### 6.2 Following a chain

A file or directory starts at the cluster in its directory entry (7.1) and continues through the table until an
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

## 7. Directories

### 7.1 Directory entries

A directory is an array of 32-byte entries [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $00 | 11 | bytes | `DIR_Name`: the 8.3 name, stem then extension, space-padded |
| $0B | 1 | u8 | `DIR_Attr`: attributes (7.2) |
| $0C | 1 | u8 | `DIR_NTRes`: reserved for Windows NT (lower-case flags); ignored, by File Exchange too [Code] |
| $0D | 1 | u8 | `DIR_CrtTimeTenth`: creation time, hundredths of a second 0–199; ignored |
| $0E | 2 | u16 | `DIR_CrtTime`: creation time (section 9) |
| $10 | 2 | u16 | `DIR_CrtDate`: creation date |
| $12 | 2 | u16 | `DIR_LstAccDate`: last access date; ignored |
| $14 | 2 | u16 | `DIR_FstClusHI`: high 16 bits of the first cluster (FAT32 only; 0 on FAT12/16) |
| $16 | 2 | u16 | `DIR_WrtTime`: modification time |
| $18 | 2 | u16 | `DIR_WrtDate`: modification date |
| $1A | 2 | u16 | `DIR_FstClusLO`: low 16 bits of the first cluster |
| $1C | 4 | u32 | `DIR_FileSize`: size in bytes; 0 for directories |

The first cluster is `DIR_FstClusLO`, with `DIR_FstClusHI << 16` added on FAT32 only [Doc]. ClassicMac ignores
`DIR_FstClusHI` on FAT12 and FAT16.

### 7.2 Attributes

| Bit | Name | Meaning |
| --- | --- | --- |
| `$01` | `ATTR_READ_ONLY` | Read-only [Doc] |
| `$02` | `ATTR_HIDDEN` | Hidden [Doc] |
| `$04` | `ATTR_SYSTEM` | System [Doc] |
| `$08` | `ATTR_VOLUME_ID` | The volume label; only in the root directory [Doc] |
| `$10` | `ATTR_DIRECTORY` | A directory [Doc] |
| `$20` | `ATTR_ARCHIVE` | Changed since backup [Doc] |
| `$0F` | `ATTR_LONG_NAME` | All four low bits: a long-name entry (section 8) [Doc] |

### 7.3 Reading a directory

Entries are read in order [Doc]:

- A first name byte of `$00` ends the directory: this entry and every later one are free.
- A first name byte of `$E5` marks a deleted entry: skip it. It also discards any long-name parts gathered so far.
- A first name byte of `$05` stands for a real `$E5` (a Kanji lead byte): show it as `$E5` in the name, but keep the
  stored bytes for `FINDER.DAT` and `RESOURCE.FRK` lookups.
- An entry with attributes `$0F` is a long-name part (section 8). ClassicMac tests the attribute byte for exactly
  `$0F`; the FAT specification masks it with `$3F` first, which differs only for entries with reserved bits set.
- An entry with `ATTR_VOLUME_ID` and without `ATTR_DIRECTORY` is the volume label: not a file, skip it.
- The entries `.` and `..` at the start of every subdirectory point at the directory itself and its parent: skip them.
  ClassicMac skips every entry whose name starts with `.`, which no valid 8.3 name does.

The rest are files and subdirectories. A subdirectory's contents are read from its chain like the root's.

A subdirectory entry whose first cluster is 0, or whose chain starts at a cluster already read as a directory, would
make the walk loop; ClassicMac skips it (`fat.folder-loop`).

---

## 8. Long names (VFAT)

### 8.1 Long-name entries

A long name is stored in one or more 32-byte entries directly **before** the 8.3 entry it belongs to, last part first
[Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| $00 | 1 | u8 | `LDIR_Ord`: part number 1–20 in bits 0–4; `$40` set on the last part (the first entry stored) |
| $01 | 10 | u16 × 5 | Characters 1–5 of this part |
| $0B | 1 | u8 | `LDIR_Attr`: `$0F` |
| $0C | 1 | u8 | `LDIR_Type`: 0 |
| $0D | 1 | u8 | `LDIR_Chksum`: checksum of the 8.3 name (8.2) |
| $0E | 12 | u16 × 6 | Characters 6–11 |
| $1A | 2 | u16 | `LDIR_FstClusLO`: 0 |
| $1C | 4 | u16 × 2 | Characters 12–13 |

Each part holds 13 UTF-16 code units, little-endian; part n holds characters `(n − 1) × 13 + 1` to `n × 13` [Doc]. A
name that does not fill its last part is ended by `$0000` and padded with `$FFFF` [Doc].

### 8.2 The checksum

The 8.3 entry's 11 name bytes, as stored (a leading `$05` included) [Doc]:

```
sum = 0
for each of the 11 bytes b:
    sum = ((sum & 1) << 7) + (sum >> 1) + b     (all in 8 bits)
```

### 8.3 Assembling a name

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

## 9. DOS dates and times

### 9.1 The fields

[Doc]:

| Field | Bits 15–9 | Bits 8–5 | Bits 4–0 |
| --- | --- | --- | --- |
| Date | year − 1980 (0–127) | month (1–12) | day (1–31) |

| Field | Bits 15–11 | Bits 10–5 | Bits 4–0 |
| --- | --- | --- | --- |
| Time | hours (0–23) | minutes (0–59) | seconds ÷ 2 (0–29) |

A date of 0 means none [Doc]. DOS times have no time zone; they are the local time of whoever wrote them [Doc].

### 9.2 How the Mac reads them

- A DOS date and time is read as a Mac local date: no time zone conversion, since Mac dates are local time too
  [Code].
- Seconds are even; the hundredths byte `DIR_CrtTimeTenth` is not used [Code] [Verified: a creation date written as
  `$B0000001` reads back as `$B0000000`].
- **Years from 2032 on read 128 years earlier** [Code] [Verified]. File Exchange writes Mac years 1904–1979, which DOS
  cannot hold, as DOS years 2032–2107, and reads them back the same way: DOS 2040 reads as 1912, and Mac 1950 is
  written as DOS 2078 and reads back as 1950. The cost is that genuine DOS dates from 2032 on read wrong. PC Exchange
  has no wrap; dates before 1980 come out as garbage [Code].
- **Nothing is checked, and nothing reads as "no date"** [Code]. File Exchange unpacks the fields as they are (year,
  month, day, hours, minutes, seconds × 2) and hands them to Mac OS 9's `Date2Secs`, which works in 16-bit day
  arithmetic with truncating division and clamps nothing [Code]. So impossible values roll over:

  | DOS date or time | Shown as |
  | --- | --- |
  | 1999, month 0, day 1 | 1998-12-02 (month 0 is 30 days back, not 31) |
  | 1999-01, day 0 | 1998-12-31 (day 0 is the day before the 1st) |
  | 1999-02-29 | 1999-03-01 |
  | 1999-04-31 | 1999-05-01 |
  | 1999, month 13, day 1 | 2000-01-01 |
  | 1999, month 15, day 1 | 2000-03-02 (the month interpolation is a day off this far out) |
  | date and time `$0000` | 1979-12-01 00:00 |
  | 1999-01-01 at 31:63:62 | 1999-01-02 08:04:02 (hours, minutes and seconds simply add) |

  The one exception is the creation date: a creation date word of 0 shows as **now** (only the date word is tested,
  not the time). A modification date of 0 is not special and shows as 1979-12-01 [Code]. That `Date2Secs` routine is
  the one Mac OS 9 installs is inferred from its constants and structure, not yet checked on a running system.
- ClassicMac reads a zero date, and a date with an impossible month, day, hour, minute or second, as no date. It
  does not reproduce the rollover until the rule above is checked on a running Mac; a zero creation date is 11.3.

---

## 10. The Mac data: `RESOURCE.FRK` and `FINDER.DAT`

PC Exchange and File Exchange keep everything a FAT file lacks in two hidden items **in every directory** [Code]. The
format is the same in PC Exchange 1.0.4 and File Exchange 3.0.2 [Code]; PC Exchange 2.x (Mac OS 7.5–8.x) was not
available for tracing, and with the same code at both ends of the range its format is taken to be the same. No Apple
file-system code in Mac OS 7.1–9 reads or writes AppleSingle or AppleDouble [Code]: this is what a Mac wrote to DOS
disks.

### 10.1 `RESOURCE.FRK`

- A subdirectory named `RESOURCE.FRK`, attributes `$12` (hidden, directory) [Code] [Verified].
- It holds one file per data file that has a resource fork, under **the same 8.3 name** as the data file [Code]
  [Verified]. Only the 8.3 name matters; long names on these files are not used.
- The file's content is the **raw resource fork, with no header** [Code] [Verified].
- Its attributes are the data file's with `$20` added, and its dates are the time it was written [Code].
- It is created on the first write to the resource fork, deleted with the data file, and moved and renamed with it;
  `RESOURCE.FRK` itself is removed when it becomes empty [Code].
- A file with no entry in `RESOURCE.FRK`, or an empty one, has an empty resource fork [Code].

### 10.2 `FINDER.DAT`

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

### 10.3 Record packing

- Records are packed `floor(clusterSize / 92)` to a cluster and **never straddle a cluster** [Code] [Verified]: with
  512-byte clusters, 5 records fill bytes 0–459, the 52 bytes to 512 are left as they were, and the 6th record starts
  at 512 (so a file of 6 records is 604 bytes, not 552). The gap holds garbage: File Exchange writes into a new cluster
  without clearing it [Code] [Verified].
- Read only whole 92-byte slots that lie within the file's size. Bytes past the end of the file, and cluster slack,
  may hold copies of other records [Code] [Verified].
- The file does not record the cluster size; on a FAT volume the reader takes it from the boot sector. (For a
  `FINDER.DAT` copied off its volume, see [HOST-FOLDERS.md](HOST-FOLDERS.md), where the cluster size is guessed.)

### 10.4 Free records

- A record whose name length (byte $00) is 0 is free [Code]. Deleting an item clears its name length and 8.3 name;
  the file never shrinks [Code].
- ClassicMac also treats a record whose first 8.3-name byte ($50) is 0 as free [Fitted]: File Exchange leaves stray
  records with a garbage name and an empty 8.3 name [Verified], which match no directory entry anyway.
- Records are searched in file order and **the first match wins**, for reading and for writing, so a later record
  with the same 8.3 name is dead; free slots are skipped even when their 8.3 name matches [Code]. (A 32-record cache
  is checked first, but it is filled only from the same search, so it gives the same record [Code].) ClassicMac uses
  the first used record in file order too.

### 10.5 When records exist

A reader cannot assume that a file has a record, nor that a record's contents were set by an application:

- Records are created **lazily**. Merely listing a writable FAT disk on the Mac creates a record for every item that
  has none, because the Mac asks for a file number [Code] [Verified]. Such a record has type `TEXT`, creator `dosa`,
  Finder flags 0, put-away folder 2 and **zero dates** [Verified]. (The code leaves the dates uninitialised [Code];
  they were zero in every record seen.)
- A record is also created when a file's Mac name differs from its 8.3 name, and whenever Finder info is set [Code].
- Records are written only while the volume's "save info" setting is on [Code].
- A new record takes the first free slot, but never offset 0 (a quirk), else it is appended [Code].
- A record made for a file that has a resource fork but no record can get a **garbage Mac name** (seen as `P"P`,
  with control characters) [Verified]; the Mac then shows that name (11.1). The name routine gives up without filling
  its buffer when its search of the directory misses, and the new record takes its name from that uninitialised
  buffer [Code]; why the search misses is not traced.
- A record's type can revert to an earlier value around a close [Verified]; see 13.3.

### 10.6 `FILEID.DAT`

A hidden file in the root directory only, holding 64-byte records {file ID, parent directory ID, Pascal name}, record 0
being a header (`$00010000`) [Code]. It is created only when an application asks for a file ID reference, carries
nothing a reader needs, and ClassicMac does not read it.

---

## 11. The Mac view of a file

For each directory entry other than the hidden items (11.5), the Mac shows the following. ClassicMac shows the same.

### 11.1 Name

In this order [Code] [Verified]:

1. **The record's Mac name**, when the item has a record (10.2) — even a garbage one, which the Mac shows as it is
   [Verified]. ClassicMac shows it too, with `fat.suspect-name` when it contains bytes `$00`–`$1F`.
2. **The long name**, converted as in section 12, when the entry has one whose checksum matches (8.3).
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

### 11.2 Finder info

- With a record: the record's FInfo and FXInfo, as stored [Code] [Verified].
- Without a record: the blank Finder info, type `TEXT`, creator `dosa`, flags 0, location 0, and in the FXInfo
  everything 0 but the put-away folder, 2 (the root directory's ID; File Exchange never reads it) [Code] [Verified].
  The record the Mac creates on first listing the file (10.5) starts from the same blank. ClassicMac gives the same
  placeholder.
- Then, for a `TEXT`/`dosa` file with a record only, the Mac may show a mapped type (section 13) [Code]. A file with
  no record never gets one; on a writable volume with "save info" on, though, the Mac makes the record before it
  reads the Finder info, so the mapped type shows there [Code] [Verified]. On a locked or read-only volume, or with
  "save info" off, the file shows plain `TEXT`/`dosa` [Code] (not yet checked on a running system).
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

### 11.3 Dates

File Exchange [Code] [Verified]:

- **Creation:** from the DOS entry's creation fields (section 9). The record's creation date ($40) is written but
  never read. When the DOS creation date word is 0, whatever the time word, File Exchange shows the current time
  [Code]; ClassicMac, which has no "now" to match, uses the record's creation date instead (PC Exchange's rule), or
  none.
- **Modification:** the later of the DOS entry's modification date and the record's ($44), compared as unsigned
  Mac dates [Code]. A record date of 0 counts as earlier than any DOS date. A DOS date of 0 is not special on the Mac
  (it reads as 1979-12-01, 9.2); ClassicMac reads it as no date, so with neither date it has none.
- **Backup:** the record's ($48) [Code]. ClassicMac does not carry backup dates.

PC Exchange [Code]: creation from the record's $40 (0 without a record); modification the later of the DOS date and
$44. DOS entries written by PC Exchange carry the modification date only.

Because DOS keeps only even seconds and the record keeps full precision, a file modified on the Mac shows the record's
exact modification time [Verified].

### 11.4 Forks

- **Data fork:** the entry's cluster chain, `DIR_FileSize` bytes [Doc].
- **Resource fork:** the file in this directory's `RESOURCE.FRK` whose 8.3 name equals the entry's (10.1), its full
  size [Code] [Verified].

### 11.5 Hidden items

The Mac hides these entries **by name**, whatever their attributes, in every directory [Code]:

- `FINDER.DAT` (8.3 key `FINDER  DAT`)
- `FILEID.DAT` (`FILEID  DAT`)
- `RESOURCE.FRK` (`RESOURCEFRK`)

Other hidden or system entries are listed, invisible (11.2).

### 11.6 Folders

A folder contributes its name (11.1) to the path of everything inside it. Its record's DInfo and DXInfo and its dates
are not used by ClassicMac, whose output is files; a folder with no files in it produces nothing.

---

## 12. Long names as File Exchange converts them

A file with no record but a long name gets a Mac name from the long name, converted as File Exchange converts it
[Code] [Verified]. PC Exchange does not read long names [Code].

### 12.1 Mac Roman or low bytes

1. File Exchange does **no Unicode normalization**: the UTF-16 from the long-name entries goes to the Text Encoding
   Converter unchanged, from Unicode 2.1 to the **system script's** encoding (Mac OS Roman on a Roman system;
   MacIcelandic, MacJapanese and so on elsewhere) [Code]. Whether the converter composes a decomposed sequence
   (`e` + U+0301 into `é`) is not known; if it does not, the combining mark has no byte and step 3 applies.
   ClassicMac converts to Mac OS Roman, as on a Roman system, and puts the name in precomposed form (normalization
   form C) first, which assumes the converter composes.
2. If **every** character has a Mac OS Roman equivalent, the name is its Mac Roman bytes [Code] [Verified]. A `:` is
   kept as it is, even though it is the Mac's path separator [Verified: `a:b c.txt`].
3. If **any** character has none, the converter reports that it used fallbacks, and the **whole** name takes the other
   path: each UTF-16 code unit becomes its low byte, and `:` becomes `_` [Code] [Verified]. `漢字 kanji.txt` becomes
   `"W kanji.txt` (U+6F22 → `$22`, U+5B57 → `$57`); `漢:.txt` becomes `"_.txt`. File Exchange's own `_` fallback
   character never takes effect [Code].

### 12.2 Names over 31 bytes

If the result of 12.1 is at most 31 bytes, it is the name. Otherwise it is shortened to exactly 31 bytes [Code]
[Verified]:

```
extension = from the last '.' among the name's final six characters to the end; none if there is no '.' there
name      = first (27 − length(extension)) characters, '#', three upper-case hex digits of the CRC, extension
```

- The characters and the extension are converted by the same path as the whole name (Mac Roman or low bytes).
- The **CRC** is CRC-16 with polynomial `$1021`, initial value 0, no reflection and no final XOR, computed over the
  long name's own UTF-16 units as stored, **not normalized**, all of them, as **big-endian** UTF-16 [Code]; the three
  digits are its low 12 bits. ClassicMac computes it the same way, so a decomposed and a precomposed spelling of one
  name get different digits, as on the Mac. This is the unique-name
  checksum of the OSTA UDF specification, which File Exchange borrowed [Code].
- `This is a very long Windows file name.txt` becomes `This is a very long Win#7C7.txt` [Verified].
- `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef.jpeg` becomes `ABCDEFGHIJKLMNOPQRSTUV#` + 3 digits + `.jpeg` (22 + 4 + 5).
- In `ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh.eleven` the last `.` is seven characters from the end, so there is no
  extension: the first 27 characters, `#` and the digits.

The extension search follows File Exchange's `FindExtension` [Code]. For names shorter than seven characters its window
is smaller, but such names are never shortened.

For a long name stored decomposed, ClassicMac's result is therefore a guess until the converter's handling is checked
on a running Mac: it composes the name before the Mac Roman conversion and before taking low bytes, where File
Exchange takes the low bytes of the name as stored [Code].

---

## 13. Types by name ending

### 13.1 The File Exchange table is empty

File Exchange keeps a per-volume table of extension → type and creator, but in Mac OS 9.0 it is **always empty**: at
startup its INIT moves any old PC Exchange mappings (the `'dMap'` resource, ID −4040) into Internet Config and then
builds the table with a count of 0, and nothing fills it later [Code].

PC Exchange 1.0.4 used only its own table, matched on the 3-character DOS extension, whose default entry is `.TXT` →
`TEXT`/`ttxt` [Code].

### 13.2 Internet Config's map

The only mapping File Exchange applies is Internet Config's map of file-name endings [Code] [Verified]:

- It applies **only** when the file's type and creator are exactly `TEXT`/`dosa` [Code] [Verified]. A stored type is
  never overridden: a `.TXT` file whose record says `APPL`/`abcd` stays `APPL`/`abcd` [Verified].
- It applies only to a file that has a record; a file with none keeps the placeholder, unless the Mac makes its
  record first, as it does on a writable volume (11.2) [Code].
- It matches the end of the file's Mac name (11.1), ignoring case; the **longest** matching ending wins, and among
  endings of the same length the earlier entry [Code]. The entries' flags are ignored [Code].
- It applies only while File Exchange's "map extensions" preference is on (Gestalt `'pcxg'` bit 3), which is the
  default [Code].
- Examples from Mac OS 9's default map: `.txt` → `TEXT`/`ttxt`, `.bin` → `BINA`/`SITx`, `.jpg` → `JPEG`/`ogle`,
  `.gif` → `GIFf`/`ogle`, `.doc` → `WDBN`/`MSWD` [Verified]; `.pdf` → `'PDF '`/`CARO`, `.sit` → `SITD`/`SITx`
  [Code].
  The default map has 313 entries, and a user can change it.

### 13.3 What is on the disk

The mapped type is shown, not stored, until the file's data fork is closed or flushed: File Exchange then reads the
Finder info (mapping applied) and writes it back to the record [Code]. So:

- a file only listed on the Mac keeps `TEXT`/`dosa` in its record [Verified];
- a file opened on the Mac usually has its mapped type stored, indistinguishable from a type an application set;
- that close-time round trip can also write back a stale copy of the record, reverting a type set just before
  [Verified; the exact trigger is not traced]. A record's dates and file number are more trustworthy than its type.

### 13.4 ClassicMac: opt-in

The map belongs to the Mac that reads the disk, not to the disk, so ClassicMac shows the **stored** type by default.
An application may supply a map (`ContainerReadOptions.ExtensionMap`); ClassicMac then applies it as in 13.2, after
the record and before the DOS attributes, to every `TEXT`/`dosa` file, with a record or not, as a writable volume
shows them. No map derived from Apple's ships with ClassicMac.

---

## 14. The listing, compared with Mac OS 9's

### 14.1 What ClassicMac lists

Walking from the root directory (5.3), for each directory:

1. Read the entries (7.3), skipping free and deleted entries, the volume label, `.` and `..`, and assembling long names.
2. Read the directory's `FINDER.DAT` records (10.2–10.4) and its `RESOURCE.FRK` entries (10.1), if present.
3. For each entry other than the hidden items (11.5): a subdirectory is walked with its Mac name (11.1) added to the
   path; a file is listed with its name, Finder info, dates and forks (section 11), then the extension map if one was
   supplied (13.4), then the invisible flag from DOS attributes (11.2).

Items the Finder creates when it mounts a disk writable — `TheVolumeSettingsFolder`, `Desktop Folder`, `Trash` and a
hidden `Desktop` file with a resource fork — are ordinary FAT entries, and are listed like any other [Verified].

### 14.2 Verification

On two FAT12 floppies written by File Exchange in SheepShaver, Mac OS 9.0, ClassicMac's listing, with the
extension-map entries the Mac showed, matches Mac OS 9's own for every file the Mac listed: folder, name, data and
resource fork lengths, type, creator, Finder flags, creation and modification dates [Verified]. The only exception is
the Finder's `Desktop` file, which the Finder rewrote after the listing was taken. The only diagnostics are
`fat.suspect-name` for the garbage names of 10.5, which match the Mac's listing byte for byte.

---

## 15. What File Exchange writes

ClassicMac does not write FAT volumes. For a writer, and for reading what File Exchange wrote, its rules are
[Code] [Verified] unless marked; [HOST-FOLDERS.md](HOST-FOLDERS.md) covers the same names on host folders.

### 15.1 Mac name to 8.3 name, on creating a file

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
stored as DOS 2032–2107 (9.2).

### 15.2 Formatting

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

## 16. Diagnostics

ClassicMac reports each problem and reads on; it throws only when the input is not a FAT volume or partition table at
all, or when one fork or directory would exceed the per-input size limit
(`ContainerReadOptions.MaxExpandedBytesPerInput`, 1 GiB), which the container unwrapper reports as
`container.unreadable`.

| Code | Severity | Meaning | ClassicMac does | The Mac |
| --- | --- | --- | --- | --- |
| `mbr.skipped` | Info | A partition entry has a type that is not FAT (3.3) | Skips it | Not traced |
| `mbr.outside` | Error | A FAT partition starts at sector 0 or past the end of the image | Skips it | Not traced |
| `mbr.truncated` | Error | A FAT partition runs past the end of the image | Reads the part that is there | Not traced |
| `fat.bad-chain` | Error | A cluster chain reaches a free cluster, a cluster number outside the volume (including the bad-cluster mark) or a cluster it already passed | Ends the chain there; the fork is short (`fat.short` follows when a size was expected) | Not traced |
| `fat.short` | Error | A file, fork, `FINDER.DAT` or directory has fewer bytes in the image than its size or chain calls for | Returns the bytes that are there | Not traced |
| `fat.folder-loop` | Error | A subdirectory's first cluster is 0 or was already read as a directory | Skips the subdirectory and its contents | Not traced |
| `fat.too-many-entries` | Error | The volume holds more files and folders than `ContainerReadOptions.MaxVolumeEntries` (default 1,000,000) | Stops the walk; files read so far are kept | No such limit |
| `fat.suspect-name` | Warning | A `FINDER.DAT` record's Mac name contains bytes `$00`–`$1F` | Shows the name as it is | Shows it as it is [Verified] |

Corrupt `FINDER.DAT` records are not diagnosed: the Mac uses them as they are [Code], and so does ClassicMac, except
for the free-record rules of 10.4.

---

## 17. Not covered

- Writing FAT volumes (section 15 describes what File Exchange writes, for reference).
- Extended partitions, logical drives, GUID partition tables and hidden partition types (`$11`, `$14`, `$16`, `$1B`,
  `$1C`, `$1E`).
- FAT32 FSInfo, the backup boot sector, and active-FAT selection by `BPB_ExtFlags` (6.2).
- The locked state from DOS attributes and the `'scut'`/`'dosa'` alias (11.2); backup dates (11.3); folder Finder
  info (11.6).
- Open on the Mac side, to be checked on a running Mac OS 9.0: the rollover of impossible DOS dates (9.2), which
  ClassicMac does not reproduce yet; whether the Text Encoding Converter composes decomposed long names (12.1); what
  a file with a fork but no record shows on a read-only volume (11.2).
- ProDOS volumes, which File Exchange also mounts with ProDOS's own extended files, and ISO 9660
  ([ISO9660.md](ISO9660.md)).
- PC Exchange 2.x (Mac OS 7.5–8.x), not available for tracing; its creation-date rule is unknown.
