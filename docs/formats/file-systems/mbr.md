# DOS partition tables

The DOS partition table (master boot record) divides a PC hard disk or removable medium into up to four partitions,
listed in sector 0 with boot code and the signature `55 AA` [Doc: UEFI specification §5.2.1]. Mac OS met it on DOS
disks through PC Exchange and File Exchange. ClassicMac turns each FAT partition into one file whose data fork is the
volume, for the FAT reader ([fat.md](fat.md)) to open.

| | |
| --- | --- |
| Identified by | `55 AA` at byte 510 of sector 0, not a FAT boot sector, and a FAT partition listed (§2.1) |
| ClassicMac | Reads: `ClassicMac.Files.Fat` (`MbrReader`) |
| Verified against | Nothing yet |
| Sources | UEFI specification §5.2.1 "Legacy Master Boot Record"; Microsoft's partition-type constants; File Exchange 3.0.2 (disassembly) |

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

Values are **little-endian**, as on the PC ([fat.md §1](fat.md#1-layout)). Sector numbers are always in 512-byte
sectors.

### 1.1 Sector 0

[Doc: UEFI specification]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 446 | boot code | Ignored |
| +$1BE | 16 | partition 1 | §1.2 |
| +$1CE | 16 | partition 2 | |
| +$1DE | 16 | partition 3 | |
| +$1EE | 16 | partition 4 | |
| +$1FE | 2 | signature | `55 AA` |

### 1.2 Partition entries

[Doc: UEFI specification]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$0 | 1 | status | `$80` bootable, `$00` not |
| +$1 | 3 | first CHS | CHS address of the first sector; UEFI firmware ignores it |
| +$4 | 1 | type | Partition type (§1.3); `$00` for an unused entry |
| +$5 | 3 | last CHS | CHS address of the last sector; ignored likewise |
| +$8 | 4 | first sector | u32, LBA, in 512-byte sectors from the start of the disk |
| +$C | 4 | sector count | u32, in 512-byte sectors |

### 1.3 Partition types

The values are Microsoft's [Doc: Microsoft partition-type constants].

| Type | Meaning | FAT |
| --- | --- | --- |
| `$01` | FAT12 | yes |
| `$04` | FAT16, under 32 MB | yes |
| `$06` | FAT16, 32 MB and over ("huge") | yes |
| `$0B` | FAT32 | yes |
| `$0C` | FAT32, LBA addressing | yes |
| `$0E` | FAT16, LBA addressing | yes |
| `$05`, `$0F` | Extended partition | no |
| anything else | Not FAT (`$07` NTFS, `$83` Linux, `$EE` GPT protective, …) | no |

The partition type is only a hint: the volume's own boot sector decides the FAT type ([fat.md §1.3](fat.md#13-regions-and-the-fat-type))
[Doc: Microsoft FAT specification].

## 2. Reading

### 2.1 Recognising a table

Sector 0 is a DOS partition table when all of these hold:

1. It ends in `55 AA` [Doc: UEFI specification].
2. It is not a FAT boot sector ([fat.md §2.2](fat.md#22-recognising-a-boot-sector)).
3. Every entry's status byte is `$00` or `$80` [Doc: UEFI specification].
4. At least one entry has a FAT type (§1.3) and a non-zero first sector.

Conditions 2 to 4 are [ClassicMac]: the Mac's own recognition of DOS partitions is not traced.

### 2.2 Reading the entries

For each of the four entries, in order:

1. Type `$00`: unused; skip it.
2. Any other non-FAT type (§1.3): skip it.
3. A FAT entry whose first sector is 0 or lies past the end of the image: skip it.
4. Otherwise the volume runs from byte `first × 512` for `count × 512` bytes. The CHS fields are not used.

## 3. Writing

ClassicMac does not write partition tables. File Exchange's formatter [Code: File Exchange 3.0.2] gives floppies and
file-backed devices no partition table, and other disks one partition at sector 32, with a geometry of 64 heads × 32
sectors per track, of type `$01`, `$04`, `$06`, `$0B` or `$0C` by size. This was read from the code only and not
checked on a running Mac. The volume it writes is in [fat.md §3](fat.md#3-writing).

## 4. Variants

None: extended partitions and GUID partition tables are not read (§8).

## 5. ClassicMac

- Recognition is as in §2.1; the FAT boot-sector test comes first, so a bare FAT volume is never taken for a table.
- Each FAT partition comes out as one file named `Partition n`, n being the entry's slot, 1–4.
- A FAT partition running past the end of the image is cut to the image and read.
- Apple's formats are tried before the DOS table ([hfs.md §5.1](hfs.md#51-recognising-a-volume)), so a disk that is
  both reads as the Apple format.
- The reader throws only when the input is not a partition table at all; the unwrapper reports
  `container.unreadable`.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `mbr.outside` | Error | A FAT partition starts at sector 0 or past the end of the image | Skips it | Not traced |
| `mbr.skipped` | Info | A partition entry has a type that is not FAT and not `$00` (§1.3) | Skips it | Not traced |
| `mbr.truncated` | Error | A FAT partition runs past the end of the image | Reads the part that is there | Not traced |

## 7. Verification

`tests/ClassicMac.Files.Tests/FatTests.cs`, `Partitioned_disks_unwrap_to_their_FAT_volumes`, builds a partitioned
disk with `FatBuilder.cs` and reads its FAT volumes through the unwrapper; `Non_FAT_boot_sectors_are_not_read` checks
recognition.

## 8. Not covered

- Extended partitions and the logical drives inside them; GUID partition tables.
- Hidden partition types (`$11`, `$14`, `$16`, `$1B`, `$1C`, `$1E`).
- How the Mac recognises a DOS partition table.

## 9. References

1. UEFI Forum, *Unified Extensible Firmware Interface Specification*, §5.2.1 "Legacy Master Boot Record".
2. Microsoft, the partition-type constants (`PARTITION_FAT_12` and the rest) of Windows `PARTITION_INFORMATION`.
3. Microsoft, *FAT32 File System Specification*, version 1.03 (December 2000).
4. File Exchange 3.0.2, as shipped with Mac OS 9.0, traced in disassembly: its formatter.
