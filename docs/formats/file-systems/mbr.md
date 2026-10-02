# DOS partition tables

Contents

1. [Layout](#1-layout)
2. [Recognising a table](#2-recognising-a-table)
3. [Partition types](#3-partition-types)
4. [Reading](#4-reading)
5. [Diagnostics](#5-diagnostics)
6. [Not covered](#6-not-covered)

---

## 1. Layout

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

---

## 2. Recognising a table

ClassicMac takes sector 0 as a DOS partition table when all of these hold:

- it ends in `55 AA` [Doc];
- it is not a FAT boot sector ([fat.md §3.3](fat.md#33-recognising-a-boot-sector));
- every entry's status byte is `$00` or `$80` [Doc];
- at least one entry has a FAT type (§3) and a non-zero first sector.

The last two conditions are ClassicMac's heuristic; the Mac's own partition recognition for DOS disks is not traced.

---

## 3. Partition types

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
[Code, not verified]. The partition type is only a hint: the volume's own boot sector decides the FAT type ([fat.md §4.2](fat.md#42-the-fat-type)) [Doc].

---

## 4. Reading

- An entry of type `$00` is unused and skipped silently.
- An entry of another non-FAT type is skipped (`mbr.skipped`).
- A FAT entry whose first sector is 0 or lies past the end of the image is skipped (`mbr.outside`).
- A FAT entry running past the end of the image is cut to the image (`mbr.truncated`) and read.
- Each remaining entry is a FAT volume from byte `first × 512`, `count × 512` bytes long. ClassicMac names it
  `Partition n`, where n is the entry's slot, 1–4.

Extended partitions and the logical partitions inside them are not read; neither is a GUID partition table.

---

## 5. Diagnostics

ClassicMac reports each problem and reads on; it throws only when the input is not a FAT volume or partition table at
all, or when one fork or directory would exceed the per-input size limit
(`ContainerReadOptions.MaxExpandedBytesPerInput`, 1 GiB), which the container unwrapper reports as
`container.unreadable`.

| Code | Severity | Meaning | ClassicMac does | The Mac |
| --- | --- | --- | --- | --- |
| `mbr.skipped` | Info | A partition entry has a type that is not FAT (§3) | Skips it | Not traced |
| `mbr.outside` | Error | A FAT partition starts at sector 0 or past the end of the image | Skips it | Not traced |
| `mbr.truncated` | Error | A FAT partition runs past the end of the image | Reads the part that is there | Not traced |

---

## 6. Not covered

- Extended partitions, logical drives, GUID partition tables and hidden partition types (`$11`, `$14`, `$16`, `$1B`,
  `$1C`, `$1E`).
