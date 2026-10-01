# Partition maps, MFS and HFS — an implementer's specification

This document describes how classic Mac OS lays out a disk: the Apple partition map that divides a hard disk or CD,
the flat Macintosh File System (MFS) of the first 400K floppies, and the Hierarchical File System (HFS) that every
Mac used from 1986 until HFS Plus. It is complete enough to write a reader that lists every file on a volume with its
folder path, Finder information, dates and both forks, without reading ClassicMac's code. It is the behaviour of
`PartitionMapReader`, `MfsReader` and `HfsReader` in `ClassicMac.Files.Hfs`. HFS Plus and HFSX are read-only formats
supported by the phase 10 implementation described in section 11. Disk images that hold these volumes (Disk Copy
4.2, NDIF, DART, UDIF) are in
[DISK-IMAGES.md](DISK-IMAGES.md).

References:

- *Inside Macintosh: Files* (1992), chapter 2 "File Manager", "Data Organization on Volumes": HFS volumes, the master
  directory block, B-trees, the catalog and extents overflow files.
- *Inside Macintosh II* (1985), "The File Manager": MFS volumes, the volume information and the block map.
- *Inside Macintosh: Devices* (1994), the SCSI Manager chapter: the driver descriptor map and the partition map.
- *Inside Macintosh: Text*, Text Utilities: `RelString` and the system sort order for names.
- *Inside Macintosh: Macintosh Toolbox Essentials*, the Finder Interface chapter: `FInfo`, `FXInfo`, `DInfo`, `DXInfo`.
- Technical Note TN1150, *HFS Plus Volume Format*: the HFS wrapper around an HFS Plus volume, and the volume
  attribute bits HFS and HFS Plus share.
- The Mac OS 9.0 ROM (File Manager, B-tree manager, boot loaders) and Apple CD/DVD Driver 1.3.1, traced in
  disassembly; the System 7.1 File Manager for the routines found unchanged in that ROM; Disk Copy 6.5's partition
  map code.

Contents

1. [Conventions](#1-conventions)
2. [Finding the volume](#2-finding-the-volume)
3. [Apple partition maps](#3-apple-partition-maps)
4. [MFS volumes](#4-mfs-volumes)
5. [HFS volume layout](#5-hfs-volume-layout)
6. [HFS B-trees](#6-hfs-b-trees)
7. [The catalog file](#7-the-catalog-file)
8. [The extents overflow file](#8-the-extents-overflow-file)
9. [Reading a fork](#9-reading-a-fork)
10. [Consistency checks](#10-consistency-checks)
11. [HFS Plus](#11-hfs-plus)
12. [Diagnostics](#12-diagnostics)
13. [Not covered and open questions](#13-not-covered-and-open-questions)

---

## 1. Conventions

The shared conventions of [README.md](README.md) hold: big-endian values, offsets in hex, sizes in decimal, Mac OS
Roman text, Mac dates as local-time seconds since 1904. In addition:

- A **logical block** (or sector) is 512 bytes and is numbered from the start of the volume (or, in section 3, of
  the disk). Logical block 2 is the byte offset 1024.
- An **allocation block** is the unit a volume gives to files: a multiple of 512 bytes, set per volume. MFS numbers
  allocation blocks from 2, HFS from 0.
- `Str27` and `Str31` are Pascal strings stored in a field of 28 and 32 bytes; only the length byte and that many
  bytes count.
- Each rule carries a source tag from [README.md](README.md). **[Code]** names the software: "Mac OS 9.0 ROM" for
  the File Manager and boot code in the Mac OS 9.0 ROM, "System 7.1 File Manager" for a routine read in that older
  code only. What is still open is listed in section 13.
- Paragraphs that begin **"ClassicMac"** describe the reader's own choices for damaged or unusual input. They are
  not format rules and carry no tag; the diagnostics they raise are in section 12.

---

## 2. Finding the volume

A volume starts with two logical blocks of boot blocks, and its identifying header is at byte 1024 (logical block
2) **[Doc]** *Inside Macintosh: Files*; *Inside Macintosh II*. The signature word there says what it is:

| Word at +$400 | Volume | Section |
| --- | --- | --- |
| `$D2D7` | MFS **[Doc]** *Inside Macintosh II* | 4 |
| `$4244` (`'BD'`) | HFS **[Doc]** *Inside Macintosh: Files* | 5 |
| `$482B` (`'H+'`) | HFS Plus **[Doc]** TN1150 | 11 |
| `$4858` (`'HX'`) | HFSX **[Doc]** TN1150; not a volume Mac OS 9.0 knows **[Code]** Mac OS 9.0 ROM | 11 |

A whole disk (a hard disk, a CD) instead starts with a driver descriptor map in logical block 0 and a partition map
from block 1 (section 3); each volume sits inside a partition.

When Mac OS 9.0 mounts a drive, the File Manager's `MountVol` reads the block at byte 1024 and tries HFS first; only
if that fails does it pass the drive to the external file systems (Foreign File Access and its plug-ins) **[Code]**
Mac OS 9.0 File Manager. The ROM's `MountVol` knows only `'BD'` and `$D2D7`; its own answer for any other signature
is `noMacDskErr` (−57), and `'H+'` is handled by code in the System file **[Code]** Mac OS 9.0 ROM and System. The ISO
9660 plug-in answers `extFSErr` (−58) for a volume that is not ISO 9660 **[Code]** ISO 9660 File Access 5.3. So a
hybrid CD whose partition map holds an HFS volume and which also carries ISO 9660 descriptors mounts as HFS, and the
ISO 9660 view is never used **[Code]** Mac OS 9.0 File Manager and CD-ROM driver.

ClassicMac tries its readers in the same spirit: the partition map reader first, then the disk image formats, then
HFS, MFS, FAT and last ISO 9660. A file that is none of these is left as a plain file.

- HFS (and HFS Plus) needs at least 1024 + 162 bytes and `'BD'` or `'H+'` at 1024.
- MFS needs at least 1024 + 64 bytes and `$D2D7` at 1024.
- A partition map needs `'ER'` or a zero word at 0, and `'PM'` at 512 or, failing that, at 2048 (section 3.4).

---

## 3. Apple partition maps

### 3.1 The driver descriptor map (block 0)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | `sbSig`: `$4552` (`'ER'`) |
| +$02 | 2 | u16 | `sbBlkSize`: the device's block size in bytes |
| +$04 | 4 | u32 | `sbBlkCount`: the number of device blocks |
| +$08 | 2 | u16 | `sbDevType`: device type (reserved) |
| +$0A | 2 | u16 | `sbDevId`: device ID (reserved) |
| +$0C | 4 | u32 | `sbData`: reserved |
| +$10 | 2 | u16 | `sbDrvrCount`: the number of driver descriptors that follow |
| +$12 | 8 × n | | driver descriptors: `ddBlock` (u32, first block of the driver), `ddSize` (u16, its size in 512-byte blocks), `ddType` (u16, operating system type, 1 = Mac OS) |
| … | | | zero to the end of the block |

All of it **[Doc]** *Inside Macintosh: Devices*. The drivers themselves live in partitions of type `Apple_Driver…`
(section 3.3), which is how the Mac loads a disk's driver at startup **[Doc]** *Inside Macintosh: Devices*.

Mac OS 9.0's CD-ROM driver reads only `sbSig` and goes on when it is `'ER'` **or 0**; nothing else in block 0 is
read, not even `sbBlkSize` **[Code]** Mac OS 9.0 CD-ROM driver (Apple CD/DVD Driver 1.3.1). Two boot loaders use
`sbBlkSize` only as the unit of `ddSize` **[Code]** Mac OS 9.0 ROM.

ClassicMac does the same: it checks only `sbSig`, accepting `'ER'` or 0, and needs no driver.

### 3.2 Partition entries (blocks 1 to n)

Each partition, including the map itself, has one 512-byte entry; the entries fill consecutive blocks from block 1
**[Doc]** *Inside Macintosh: Devices*.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | `pmSig`: `$504D` (`'PM'`) |
| +$02 | 2 | u16 | `pmSigPad`: reserved, 0 |
| +$04 | 4 | u32 | `pmMapBlkCnt`: the number of entries in the map (the same in every entry) |
| +$08 | 4 | u32 | `pmPyPartStart`: the partition's first physical block |
| +$0C | 4 | u32 | `pmPartBlkCnt`: the partition's size in blocks |
| +$10 | 32 | char[32] | `pmPartName`: the partition's name, NUL-terminated if shorter than 32 |
| +$30 | 32 | char[32] | `pmParType`: the partition's type, NUL-terminated if shorter than 32 (section 3.3) |
| +$50 | 4 | u32 | `pmLgDataStart`: the first block of the data area, relative to the partition's start |
| +$54 | 4 | u32 | `pmDataCnt`: the size of the data area in blocks |
| +$58 | 4 | u32 | `pmPartStatus`: status flags (valid, allocated, in use, bootable, readable, writable, …) |
| +$5C | 4 | u32 | `pmLgBootStart`: the first block of the boot code, relative to the partition's start |
| +$60 | 4 | u32 | `pmBootSize`: the boot code's size in bytes |
| +$64 | 4 | u32 | `pmBootAddr`: the address to load the boot code at |
| +$68 | 4 | u32 | `pmBootAddr2`: reserved |
| +$6C | 4 | u32 | `pmBootEntry`: the boot code's entry point |
| +$70 | 4 | u32 | `pmBootEntry2`: reserved |
| +$74 | 4 | u32 | `pmBootCksum`: the boot code's checksum |
| +$78 | 16 | char[16] | `pmProcessor`: the processor the boot code is for (`"68000"`, …) |
| +$88 | 376 | | `pmPad`: reserved, zero |

All of it **[Doc]** *Inside Macintosh: Devices*. An older map format with the signature `'TS'` preceded this one
**[Doc]** *Inside Macintosh: Devices*; the CD-ROM driver still accepts it at byte 512 **[Code]** Mac OS 9.0 CD-ROM
driver. ClassicMac does not read it.

The block numbers in an entry (`pmPyPartStart`, `pmPartBlkCnt`, `pmLgDataStart`) count blocks of the size the reader
addresses the device in, **never `sbBlkSize`** **[Code]** Mac OS 9.0 ROM and CD-ROM driver; Disk Copy 6.5:

| Reader | Entries at | Unit of the block numbers |
| --- | --- | --- |
| ROM boot-volume locator, ATA driver loader | 512-byte blocks 1, 2, … | 512 |
| ROM ATAPI boot loader | 2048-byte blocks 1, 2, … (the first 512 bytes of each) | 2048 |
| CD-ROM driver, ATAPI | a probed stride of 512 or 2048 bytes (section 3.4) | the stride |
| CD-ROM driver, SCSI | 512-byte blocks | 512 |
| Disk Copy 6.5 | a probed stride of 8, 4, 2 or 1 × 512 bytes | the stride, for `Apple_HFS` |

So a disk or disk image uses 512-byte units, and a CD mastered with its map at a 2048-byte stride uses 2048-byte
units throughout. ClassicMac takes the unit from the stride, as the CD-ROM driver does (section 3.4).

### 3.3 Partition types

| `pmParType` | Contents |
| --- | --- |
| `Apple_partition_map` | The map itself, from block 1 **[Doc]** *Inside Macintosh: Devices* |
| `Apple_Driver`, `Apple_Driver43` | A device driver **[Doc]** *Inside Macintosh: Devices* |
| `Apple_MFS` | An MFS volume (section 4) **[Doc]** *Inside Macintosh: Devices* |
| `Apple_HFS` | An HFS volume (section 5), or an HFS Plus volume, which keeps the same type **[Doc]** *Inside Macintosh: Devices*; TN1150 |
| `Apple_Free` | Unused space **[Doc]** *Inside Macintosh: Devices* |
| `Apple_Scratch` | An empty partition **[Doc]** *Inside Macintosh: Devices* |
| `Apple_PRODOS`, `Apple_Unix_SVR2` | Apple II and A/UX file systems **[Doc]** *Inside Macintosh: Devices* |

Later disks carry more driver and patch types (for ATA and ATAPI drives, for example); a reader only needs to
recognise the two volume types.

### 3.4 Which partitions are Mac volumes

Mac OS 9.0's ATAPI CD-ROM driver reads a disc's map this way **[Code]** Mac OS 9.0 CD-ROM driver:

1. Block 0 must start with `'ER'` or a zero word (section 3.1); otherwise there is no map.
2. **The stride is probed:** `'PM'` at byte 512 gives a stride of 512; otherwise `'PM'` at byte 2048 gives 2048;
   otherwise there is no map. Nothing else is tried.
3. The entries are at byte `i × stride` for i = 1 to the first entry's `pmMapBlkCnt`; the walk stops at the first
   entry without `'PM'`.
4. Every block number is multiplied by the stride. The drive starts at `(pmPyPartStart + pmLgDataStart) × stride`
   and is `pmPartBlkCnt × stride` bytes long: `pmLgDataStart` is not taken off the size, and **`pmDataCnt` is never
   read**. (The session base of a multisession disc is multiplied by the stride too, a quirk that matters only for a
   2048-byte map outside the first session.)
5. An entry whose type's first nine bytes are `Apple_HFS` becomes a drive; every other type is ignored. A partition
   whose `pmPartStatus` bit 5 (writable) is clear is write-protected.

So **every** `Apple_HFS` partition becomes a drive of its own, and several volumes on one disc all mount. The SCSI
CD-ROM driver is simpler: a 512-byte stride only, `pmLgDataStart` ignored, and only the **first** `Apple_HFS`
partition mounted **[Code]** Mac OS 9.0 CD-ROM driver. A disc without a partition map becomes one drive covering the
whole disc **[Code]** Mac OS 9.0 CD-ROM driver.

*Inside Macintosh: Devices* describes `pmDataCnt` as the size of the data area **[Doc]**, but no Mac OS 9.0 mounting
path reads it: the CD-ROM drivers, the ROM's boot-volume locator and ATA driver loader all size a partition from
`pmPartBlkCnt` **[Code]** Mac OS 9.0 ROM and CD-ROM driver. Disk Copy 6.5, rescaling a map, rewrites `pmDataCnt`
only when it is non-zero, so 0 means "not given" **[Code]** Disk Copy 6.5.

ClassicMac reads the map as the ATAPI driver does: block 0 with `'ER'` or a zero word, the stride probed at 512 then
2048, `pmMapBlkCnt` from the first entry, every block number times the stride, and `pmDataCnt` ignored. Each
`Apple_HFS` or `Apple_MFS` partition becomes one file, named after `pmPartName`, whose data fork is the volume's
bytes; the volume readers then open it like any other volume. Every other type (the map itself, drivers, free space)
is skipped with an Info diagnostic. Where it differs from the driver:

- the volume runs from `(pmPyPartStart + pmLgDataStart) × stride` for `pmPartBlkCnt − pmLgDataStart` blocks, so it
  ends where the partition ends;
- `Apple_MFS` partitions are read too;
- the type must be exactly `Apple_HFS` or `Apple_MFS`, case included, where the driver compares nine bytes;
- an entry without `'PM'` is skipped and the walk goes on (section 3.5), where the driver stops;
- the image is one session: no session base is added.

### 3.5 Damaged and truncated maps

ClassicMac, for each entry:

- stops if the image ends before the entry (`partition.map-truncated`);
- skips an entry without `'PM'` (`partition.bad-entry`) and goes on to the next;
- skips a volume that starts at or past the end of the image (`partition.outside`);
- cuts a volume that runs past the end of the image to what is there (`partition.truncated`). The volume reader then
  reports each fork that loses bytes (`hfs.image-truncated`, `mfs.fork-short`).

---

## 4. MFS volumes

MFS, the file system of the original Macintosh and its 400K floppies, is flat: one directory lists every file on the
volume, and each fork is a chain of allocation blocks through a block map **[Doc]** *Inside Macintosh II*.

### 4.1 Layout

| Logical blocks | Contents |
| --- | --- |
| 0–1 | Boot blocks |
| 2 onwards | The volume information (64 bytes at byte 1024), followed at byte 1088 by the allocation block map |
| `drDirSt` to `drDirSt + drBlLen − 1` | The file directory |
| from `drAlBlSt` | Allocation blocks 2, 3, … |

All of it **[Doc]** *Inside Macintosh II*. Allocation block `n` (n ≥ 2) starts at byte
`drAlBlSt × 512 + (n − 2) × drAlBlkSiz` **[Doc]** *Inside Macintosh II*.

### 4.2 The volume information

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | `drSigWord`: `$D2D7` |
| +$02 | 4 | u32 | `drCrDate`: when the volume was initialised |
| +$06 | 4 | u32 | `drLsBkUp`: when it was last backed up |
| +$0A | 2 | u16 | `drAtrb`: volume attributes (bit 7 locked by hardware, bit 15 locked by software) |
| +$0C | 2 | u16 | `drNmFls`: the number of files in the directory |
| +$0E | 2 | u16 | `drDirSt`: the directory's first logical block |
| +$10 | 2 | u16 | `drBlLen`: the directory's length in logical blocks |
| +$12 | 2 | u16 | `drNmAlBlks`: the number of allocation blocks |
| +$14 | 4 | u32 | `drAlBlkSiz`: the allocation block size in bytes, a multiple of 512 |
| +$18 | 4 | u32 | `drClpSiz`: the clump size (bytes to allocate at a time) |
| +$1C | 2 | u16 | `drAlBlSt`: the logical block where allocation block 2 starts |
| +$1E | 4 | u32 | `drNxtFNum`: the next unused file number |
| +$22 | 2 | u16 | `drFreeBks`: the number of free allocation blocks |
| +$24 | 28 | Str27 | `drVN`: the volume name |

All of it **[Doc]** *Inside Macintosh II*.

ClassicMac rejects a volume whose `drAlBlkSiz` is 0 or not a multiple of 512, and one whose block map runs past the
end of the image (the reader throws; section 12).

### 4.3 The allocation block map

The map follows the volume information directly, one **12-bit** entry per allocation block, packed: the entry for
allocation block `n` occupies bits `(n − 2) × 12` to `(n − 2) × 12 + 11` of the map, most significant bit first
**[Doc]** *Inside Macintosh II*. The map is `⌈drNmAlBlks × 3 / 2⌉` bytes long. For index `i = n − 2` and
`p = i × 3 / 2` (integer division):

- `i` even: `entry = map[p] << 4 | map[p + 1] >> 4`;
- `i` odd: `entry = (map[p] & $0F) << 8 | map[p + 1]`.

| Entry | Meaning |
| --- | --- |
| 0 | The block is free |
| 1 | The block is the last of its fork |
| 2 to $FFF | The block is in use; the value is the number of the fork's next block |

All of it **[Doc]** *Inside Macintosh II*.

### 4.4 The file directory

The directory is a run of variable-length entries, each starting on an even offset. An entry never crosses a
logical block; the space after a block's last entry is unused **[Doc]** *Inside Macintosh II*.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | `flFlags`: bit 7 set if the entry is in use, bit 0 set if the file is locked |
| +$01 | 1 | u8 | `flTyp`: version number, 0 |
| +$02 | 16 | FInfo | `flUsrWds`: the Finder information (type, creator, flags, location, folder) |
| +$12 | 4 | u32 | `flFlNum`: the file number |
| +$16 | 2 | u16 | `flStBlk`: the data fork's first allocation block (0 if empty) |
| +$18 | 4 | u32 | `flLgLen`: the data fork's logical length in bytes |
| +$1C | 4 | u32 | `flPyLen`: the data fork's physical length (whole allocation blocks) |
| +$20 | 2 | u16 | `flRStBlk`: the resource fork's first allocation block (0 if empty) |
| +$22 | 4 | u32 | `flRLgLen`: the resource fork's logical length |
| +$26 | 4 | u32 | `flRPyLen`: the resource fork's physical length |
| +$2A | 4 | u32 | `flCrDat`: when the file was created |
| +$2E | 4 | u32 | `flMdDat`: when it was last modified |
| +$32 | 1+n | Str255 | `flNam`: the file name |
| +$33+n | 0 or 1 | | a pad byte to make the entry's length even |

All of it **[Doc]** *Inside Macintosh II*. The entry's length is `51 + n`, rounded up to even **[Doc]** *Inside
Macintosh II*.

The File Manager scans each directory block from its start **[Code]** Mac OS 9.0 ROM:

- A `flFlags` byte of **0** ends the block's entries. The whole byte is tested, not bit 7: a non-zero flags byte
  without bit 7 is still an entry. Bit 7 is only set when an entry is made.
- The next entry is at `offset + 51 + n`, rounded up to even, and the scan goes on only while that is below **460**.
- Deleting an entry slides the later ones down and zeroes the tail, so a block has no holes **[Code]** System 7.1
  File Manager.

ClassicMac scans the same way: an entry is any non-zero flags byte, a zero one ends the block, and no entry starts at
block offset 460 or later. An entry whose name would run past the block ends the block too (`mfs.bad-entry`). A
directory that runs past the end of the image is read as far as whole blocks go (`mfs.directory-truncated`).

### 4.5 Reading an MFS fork

A fork starts at `flStBlk` (or `flRStBlk`) and follows the map: each block's entry names the next, until an entry of
1 **[Doc]** *Inside Macintosh II*. The fork's bytes are those blocks in chain order, cut to the logical length; the
physical length only says how much was allocated **[Doc]** *Inside Macintosh II*. A logical length of 0 is an empty
fork, whatever the start block says.

ClassicMac walks the chain only until it covers the logical length, merging consecutive blocks into one range and
reading the fork in place (section 9). It stops the chain, keeps what it has and reports `mfs.bad-chain` when:

- a block number is below 2, at or past `drNmAlBlks + 2`, or already visited (a loop);
- a block's entry is 0 (a free block) before the length is covered.

If the chain, or the image, holds fewer bytes than the logical length, the fork is cut and `mfs.fork-short` reported.

### 4.6 Folders on MFS

MFS has no directories. The folders a user saw were kept by the Finder: each file's `fdFldr` (in its `FInfo`) names
the Finder folder it appears in **[Doc]** *Inside Macintosh II*; *Inside Macintosh: Macintosh Toolbox Essentials*.

ClassicMac gives MFS files an empty folder path and keeps `fdFldr` in the Finder information as stored.

### 4.7 What comes out

ClassicMac reports, for each entry: the name (the raw bytes of `flNam`), the Finder information (`flUsrWds`, with
an all-zero `FXInfo`), the creation and modification dates (a stored 0 comes out as "no date"), and both forks. The
directory's entry count is compared with `drNmFls` (section 10).

---

## 5. HFS volume layout

### 5.1 Blocks

| Logical blocks | Contents |
| --- | --- |
| 0–1 | Boot blocks (signature `'LK'` on a startup disk, otherwise zero) |
| 2 | The master directory block (MDB) |
| from `drVBMSt` (normally 3) | The volume bitmap: one bit per allocation block, most significant bit first, 1 = in use |
| from `drAlBlSt` | Allocation blocks 0, 1, … |
| N − 2 | The alternate MDB (N = the volume's size in logical blocks) |
| N − 1 | Reserved |

All of it **[Doc]** *Inside Macintosh: Files*. Allocation block `n` starts at byte
`drAlBlSt × 512 + n × drAlBlkSiz` **[Doc]** *Inside Macintosh: Files*. Since `drNmAlBlks` is 16 bits, a volume has at
most 65,535 allocation blocks, and larger volumes use larger allocation blocks **[Doc]** *Inside Macintosh: Files*.

Everything the File Manager keeps about files, including the catalog and the extents overflow file themselves, is
in allocation blocks; the boot blocks, MDB, bitmap and alternate MDB are not **[Doc]** *Inside Macintosh: Files*.

ClassicMac does not read the boot blocks or the volume bitmap.

### 5.2 The master directory block

The MDB is 162 bytes at the start of logical block 2 (byte 1024) **[Doc]** *Inside Macintosh: Files*.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | `drSigWord`: `$4244` (`'BD'`) |
| +$02 | 4 | u32 | `drCrDate`: when the volume was created |
| +$06 | 4 | u32 | `drLsMod`: when it was last modified |
| +$0A | 2 | u16 | `drAtrb`: volume attributes (below) |
| +$0C | 2 | u16 | `drNmFls`: the number of files in the root folder |
| +$0E | 2 | u16 | `drVBMSt`: the first logical block of the volume bitmap |
| +$10 | 2 | u16 | `drAllocPtr`: where the next allocation search starts |
| +$12 | 2 | u16 | `drNmAlBlks`: the number of allocation blocks |
| +$14 | 4 | u32 | `drAlBlkSiz`: the allocation block size in bytes, a multiple of 512 |
| +$18 | 4 | u32 | `drClpSiz`: the default clump size |
| +$1C | 2 | u16 | `drAlBlSt`: the logical block where allocation block 0 starts |
| +$1E | 4 | u32 | `drNxtCNID`: the next unused catalog node ID |
| +$22 | 2 | u16 | `drFreeBks`: the number of free allocation blocks |
| +$24 | 28 | Str27 | `drVN`: the volume name |
| +$40 | 4 | u32 | `drVolBkUp`: when the volume was last backed up |
| +$44 | 2 | u16 | `drVSeqNum`: the backup sequence number |
| +$46 | 4 | u32 | `drWrCnt`: the volume write count |
| +$4A | 4 | u32 | `drXTClpSiz`: the extents overflow file's clump size |
| +$4E | 4 | u32 | `drCTClpSiz`: the catalog file's clump size |
| +$52 | 2 | u16 | `drNmRtDirs`: the number of folders in the root folder |
| +$54 | 4 | u32 | `drFilCnt`: the number of files on the volume |
| +$58 | 4 | u32 | `drDirCnt`: the number of folders on the volume |
| +$5C | 32 | u32[8] | `drFndrInfo`: Finder information (the first long is the blessed System Folder's ID) |
| +$7C | 2 | u16 | `drVCSize` (size of the volume cache); in an HFS wrapper, `drEmbedSigWord` (section 11) |
| +$7E | 2 | u16 | `drVBMCSize` (size of the bitmap cache); in a wrapper, the start of `drEmbedExtent` |
| +$80 | 2 | u16 | `drCtlCSize` (size of the common cache); in a wrapper, the count of `drEmbedExtent` |
| +$82 | 4 | u32 | `drXTFlSize`: the extents overflow file's logical length in bytes |
| +$86 | 12 | ExtDataRec | `drXTExtRec`: the extents overflow file's extents (section 5.4) |
| +$92 | 4 | u32 | `drCTFlSize`: the catalog file's logical length in bytes |
| +$96 | 12 | ExtDataRec | `drCTExtRec`: the catalog file's first three extents |

All of it **[Doc]** *Inside Macintosh: Files*, except the wrapper fields and the first long of `drFndrInfo`,
**[Doc]** TN1150.

`drAtrb` bits: 7 locked by hardware, 8 unmounted cleanly, 9 bad blocks spared, 15 locked by software **[Doc]**
*Inside Macintosh: Files*; TN1150.

ClassicMac reads `drNmAlBlks`, `drAlBlkSiz`, `drAlBlSt`, `drVN`, `drFilCnt`, `drDirCnt`, the two files' lengths and
extents, and `drEmbedSigWord`. It rejects a volume whose `drAlBlkSiz` is 0 or not a multiple of 512 (section 12).
A volume name longer than 27 bytes is cut to 27.

### 5.3 The alternate MDB

A copy of the MDB, the alternate MDB, is kept in the second-to-last logical block of the volume, for disk repair
utilities to use when the MDB is damaged **[Doc]** *Inside Macintosh: Files*. The File Manager does not update it on
every change, so its counts and dates may be older than the MDB's **[Doc]** *Inside Macintosh: Files*. Disk Copy
treats that block as part of the volume's structure: making a read-only or compressed image, it stores the blocks up
to the first allocation block, the used allocation blocks and block N − 2 as data, and everything else as zeros
**[Code]** Disk Copy 6.3.3; **[Verified]** on images Disk Copy 6.1.2 made in SheepShaver, Mac OS 9.0.

ClassicMac reads only the MDB at 1024 and never falls back to the alternate. A volume whose MDB is damaged is
reported, not repaired.

### 5.4 Extents

An **extent** is a run of contiguous allocation blocks; an **extent record** (`ExtDataRec`) is three extent
descriptors, 12 bytes **[Doc]** *Inside Macintosh: Files*:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | `xdrStABN`: the first allocation block |
| +$02 | 2 | u16 | `xdrNumABlks`: the number of allocation blocks (0 = unused descriptor) |

A fork's first three extents are in its catalog record (or, for the two B-tree files, in the MDB); further extents
are in the extents overflow file, three per record (section 8) **[Doc]** *Inside Macintosh: Files*. The extents
overflow file keeps all its extents in the MDB and never overflows itself; the catalog file can **[Doc]** *Inside
Macintosh: Files*.

### 5.5 Reserved catalog node IDs

Every file and folder has a catalog node ID (CNID), unique on the volume **[Doc]** *Inside Macintosh: Files*:

| CNID | Meaning |
| --- | --- |
| 1 | The parent of the root folder |
| 2 | The root folder |
| 3 | The extents overflow file |
| 4 | The catalog file |
| 5 | The bad block file (its extents, in the extents overflow file, cover the volume's bad blocks) |
| 6–15 | Reserved |
| 16 and up | Ordinary files and folders |

All of it **[Doc]** *Inside Macintosh: Files*; TN1150 for 6–15.

---

## 6. HFS B-trees

The catalog and the extents overflow file are both B-trees: files made of **512-byte nodes**, numbered from 0 in the
order they appear in the file's forks **[Doc]** *Inside Macintosh: Files*.

### 6.1 The node descriptor

Every node starts with a 14-byte descriptor **[Doc]** *Inside Macintosh: Files*:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | u32 | `ndFLink`: the next node of the same type and level (0 = none) |
| +$04 | 4 | u32 | `ndBLink`: the previous node of the same type and level (0 = none) |
| +$08 | 1 | i8 | `ndType`: −1 ($FF) leaf, 0 index, 1 header, 2 map |
| +$09 | 1 | u8 | `ndNHeight`: the node's level (leaves are 1) |
| +$0A | 2 | u16 | `ndNRecs`: the number of records in the node |
| +$0C | 2 | u16 | `ndResv2`: reserved |

### 6.2 Records and offsets

Records follow the descriptor from +$0E. The end of the node holds a table of `u16` offsets from the start of the
node, read backwards: the offset of record 0 is at +$1FE, record 1 at +$1FC, and so on; after the last record's
offset comes one more, the offset of the node's free space **[Doc]** *Inside Macintosh: Files*. Record `i` therefore
runs from `offset[i]` to `offset[i + 1]`. Offsets are even **[Doc]** *Inside Macintosh: Files*.

A record in an index or leaf node is a key followed by data. The key starts with its length byte, which does not
count itself; the data starts at the next even offset after the key **[Doc]** *Inside Macintosh: Files*.

ClassicMac checks each record's offsets before use: the start must be at least 14, the end must be after the start
and must not reach into the offset table. A record that fails is skipped (`hfs.bad-record-offset`); the node's
other records are still read. A key that leaves no room for data is skipped silently.

### 6.3 The header node (node 0)

Node 0 is the header node, with three records: the header record, a 128-byte reserved record, and the map record,
a bitmap of the nodes in use (1 bit per node, most significant bit first) **[Doc]** *Inside Macintosh: Files*. A tree
with more nodes than the map record covers continues the bitmap in map nodes (type 2), linked from the header node
through `ndFLink` **[Doc]** *Inside Macintosh: Files*.

The header record (106 bytes, at +$0E in node 0):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | `bthDepth`: the tree's depth (0 = empty) |
| +$02 | 4 | u32 | `bthRoot`: the root node (0 = empty tree) |
| +$06 | 4 | u32 | `bthNRecs`: the number of leaf records |
| +$0A | 4 | u32 | `bthFNode`: the first leaf node (0 = none) |
| +$0E | 4 | u32 | `bthLNode`: the last leaf node |
| +$12 | 2 | u16 | `bthNodeSize`: the node size, 512 |
| +$14 | 2 | u16 | `bthKeyLen`: the maximum key length (37 in the catalog, 7 in the extents overflow file) |
| +$16 | 4 | u32 | `bthNNodes`: the number of nodes in the tree |
| +$1A | 4 | u32 | `bthFree`: the number of free nodes |
| +$1E | 76 | | `bthResv`: reserved |

All of it **[Doc]** *Inside Macintosh: Files*.

ClassicMac reads only `bthFNode`. It uses a node size of 512 whatever `bthNodeSize` says, and counts the tree's nodes
from the file's length rather than `bthNNodes`.

### 6.4 Index and leaf nodes

Leaf nodes (type −1, level 1) hold the tree's data records, in ascending key order within a node and from node to
node along the `ndFLink` chain, which starts at `bthFNode` and ends at `bthLNode` **[Doc]** *Inside Macintosh: Files*.

Index nodes (type 0, level 2 and up) hold pointer records: a key and a `u32` child node number; the key is the first
key in that child **[Doc]** *Inside Macintosh: Files*. In an HFS tree the keys in index nodes are always stored at
the maximum length: the key length byte is `bthKeyLen`, the key is padded with zeros to that length, and the child
node number follows at `(bthKeyLen + 2)` rounded down to even **[Code]** Mac OS 9.0 ROM. A catalog index key is
therefore 38 bytes (key length 37) and an extents index key 8 bytes (key length 7), each followed by the `u32` child;
the initializer sets `bthKeyLen` to 37 and 7 **[Code]** System 7.1 File Manager. Only leaf keys have their actual
length (section 7.1).

To find a key, start at `bthRoot`; in each index node take the last record whose key is less than or equal to the
key sought (none: the key is not in the tree) and go to its child; in the leaf, look for an equal key **[Doc]**
*Inside Macintosh: Files*.

### 6.5 Walking the leaves

A reader that wants every record needs no index node and no key comparison: it starts at `bthFNode` and follows
`ndFLink` until 0, reading each leaf's records in order. This is what ClassicMac does. It reads the whole B-tree file
into memory once (limited by `MaxExpandedBytesPerInput`, 1 GiB by default), and stops the walk, keeping the records
read so far, when:

- a link names a node at or past the end of the file, or a node already visited (`hfs.bad-link`);
- a linked node's `ndType` is not −1 (`hfs.not-leaf`).

A B-tree file shorter than one node is read as empty.

### 6.6 Key comparison

Keys are kept in ascending order **[Doc]** *Inside Macintosh: Files*. Names compare as the File Manager compares all
names: uppercase and lowercase letters are equal, but letters with diacritical marks differ from those without
**[Doc]** *Inside Macintosh: Files*. The exact order is that of the File Manager's compare routines, the same in the
Mac OS 9.0 ROM as in System 7.1 **[Code]** Mac OS 9.0 ROM.

**Catalog keys** **[Code]** Mac OS 9.0 ROM:

1. The parent ID, unsigned 32-bit.
2. For equal parent IDs, the names, with plain `_RelString`: case-insensitive, diacritical-sensitive, each name
   taking its own length byte (not `ckrKeyLen`).

`_RelString` works on weights, from its tables in the ROM (the same bytes as in System 7.1) **[Code]** Mac OS 9.0 ROM:

- Each byte `c` weighs `w(c) = CmpTab[UpperTab[c]]`, a `u16` whose high byte is the base letter and low byte the
  modifier. `UpperTab` folds case; `CmpTab` gives the order.
- The names are compared position by position over the shorter length; the **first unequal weight** decides
  (unsigned).
- If all are equal, **the shorter name sorts first** (a prefix before the longer name, `AB` < `ABC`); equal lengths
  are equal. So the empty name of a thread record (section 7.4) sorts before every other key with the same parent ID.
- **An accent decides at its own position**, not as a tie-break at the end: `É` weighs `$4502`, between `E` (`$4500`)
  and `F` (`$4600`), so `Éa` sorts after `Ez` and before `F`.
- Within a letter the modifiers run: plain `$00` < acute `$02` < grave `$04` < circumflex `$06` < umlaut `$08` <
  tilde `$0A` < ring `$0C` < slash `$0E` < cedilla `$10` < under `$12` < ligature `$14`. After them come the
  lowercase accented letters that `UpperTab` does not fold, at `$80` + the same modifier (`á` = `$4182`, `è` =
  `$4584`).
- Case folding covers `a`–`z` and only these accented letters: `äÄ åÅ çÇ éÉ ñÑ öÖ üÜ àÀ ãÃ õÕ æÆ øØ œŒ`.

Quirks of the tables **[Code]** Mac OS 9.0 ROM:

- `á` and `Á` differ (and likewise the other accented letters not in the fold list), so two names in one folder can
  differ in the case of such a letter.
- The uppercase accented letters `$E5`–`$F4` (`Â Ê Á Ë È Í Î Ï Ì Ó Ô Ò Ú Û Ù`) and `Ÿ` (`$D9`) weigh their own code
  times 256: they sort by code, after `~` and after every letter.
- `` ` `` (`$60`) folds to `$61` and weighs `$4180`: it sorts as a variant of A.
- `ß` weighs `$5382`, between S and T; `ÿ` weighs `$5988`.
- `$CA` (non-breaking space) weighs `$2000`, **equal to a space**.
- Curly quotes and guillemets sort as variants of the straight quotes: `“ ”` are `"` + `$02`/`$04`, `« »` are `"` +
  `$06`/`$08`, `‘ ’` are `'` + `$02`/`$04`.

**Extents keys**: the file ID (unsigned 32-bit), then the fork type (`$00` data before `$FF` resource), then the
starting block (unsigned 16-bit) **[Code]** Mac OS 9.0 ROM.

ClassicMac walks the leaves (section 6.5) and never compares keys, so it does not depend on this order; a lookup or a
writer does (writing HFS is phase 8 of the plan).

---

## 7. The catalog file

The catalog file (CNID 4) is a B-tree with one record for each folder and file, and a thread record for each
folder and for each file that needs one **[Doc]** *Inside Macintosh: Files*. Its length and first extents are in
the MDB (`drCTFlSize`, `drCTExtRec`); more extents may be in the extents overflow file under file ID 4 **[Doc]**
*Inside Macintosh: Files*.

### 7.1 The catalog key

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | `ckrKeyLen`: the key's length, not counting this byte |
| +$01 | 1 | u8 | `ckrResrv1`: reserved, 0 |
| +$02 | 4 | u32 | `ckrParID`: the parent folder's CNID (for a thread, the CNID the thread is for) |
| +$06 | 1+n | Str31 | `ckrCName`: the name (empty for a thread) |

In a leaf, `ckrKeyLen` covers the name and any alignment byte: `6 + n` for an odd-length name and `7 + n` for
an even-length name. The data starts at the next even offset. Names are at most 31 bytes **[Doc]**
*Inside Macintosh: Files*.

Every catalog data record starts with a type byte and a reserved byte **[Doc]** *Inside Macintosh: Files*:

| `cdrType` | Record |
| --- | --- |
| 1 | Folder (`cdrDirRec`), 70 bytes |
| 2 | File (`cdrFilRec`), 102 bytes |
| 3 | Folder thread (`cdrThdRec`), 46 bytes |
| 4 | File thread (`cdrFThdRec`), 46 bytes |

ClassicMac takes a folder record of 70 bytes or more and a file record of 102 bytes or more, skips thread records,
and reports any other type, or a folder or file record too short, as `hfs.bad-record`. Keys shorter than 7 bytes
and data shorter than 2 bytes are skipped silently. After a million folders and files
(`MaxVolumeEntries`) it stops reading the catalog (`hfs.too-many-entries`).

### 7.2 Folder records

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | i8 | `cdrType`: 1 |
| +$01 | 1 | u8 | `cdrResrv2`: reserved |
| +$02 | 2 | u16 | `dirFlags`: folder flags |
| +$04 | 2 | u16 | `dirVal`: valence, the number of files and folders directly inside |
| +$06 | 4 | u32 | `dirDirID`: the folder's CNID |
| +$0A | 4 | u32 | `dirCrDat`: when it was created |
| +$0E | 4 | u32 | `dirMdDat`: when it was last modified |
| +$12 | 4 | u32 | `dirBkDat`: when it was last backed up |
| +$16 | 16 | DInfo | `dirUsrInfo`: the Finder's folder information (window rectangle, flags, location, view) |
| +$26 | 16 | DXInfo | `dirFndrInfo`: the Finder's extended folder information |
| +$36 | 16 | u32[4] | `dirResrv`: reserved |

All of it **[Doc]** *Inside Macintosh: Files*; `DInfo` and `DXInfo` **[Doc]** *Inside Macintosh: Macintosh Toolbox
Essentials*.

The root folder's record has the key (1, volume name) and `dirDirID` 2 **[Doc]** *Inside Macintosh: Files*.

ClassicMac uses a folder record only for folder paths (section 7.5): it keeps `dirDirID`, the key's parent ID and
the key's name. Folders do not come out as entries of their own, so an empty folder is not listed.

### 7.3 File records

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | i8 | `cdrType`: 2 |
| +$01 | 1 | u8 | `cdrResrv2`: reserved |
| +$02 | 1 | u8 | `filFlags`: bit 0 locked, bit 1 a file thread exists, bit 7 record in use |
| +$03 | 1 | u8 | `filTyp`: file type (version), 0 |
| +$04 | 16 | FInfo | `filUsrWds`: type, creator, Finder flags, icon location, window |
| +$14 | 4 | u32 | `filFlNum`: the file's CNID |
| +$18 | 2 | u16 | `filStBlk`: the data fork's first allocation block |
| +$1A | 4 | u32 | `filLgLen`: the data fork's logical length in bytes |
| +$1E | 4 | u32 | `filPyLen`: the data fork's physical length |
| +$22 | 2 | u16 | `filRStBlk`: the resource fork's first allocation block |
| +$24 | 4 | u32 | `filRLgLen`: the resource fork's logical length |
| +$28 | 4 | u32 | `filRPyLen`: the resource fork's physical length |
| +$2C | 4 | u32 | `filCrDat`: when the file was created |
| +$30 | 4 | u32 | `filMdDat`: when it was last modified |
| +$34 | 4 | u32 | `filBkDat`: when it was last backed up |
| +$38 | 16 | FXInfo | `filFndrInfo`: the Finder's extended information |
| +$48 | 2 | u16 | `filClpSize`: the file's clump size |
| +$4A | 12 | ExtDataRec | `filExtRec`: the data fork's first three extents |
| +$56 | 12 | ExtDataRec | `filRExtRec`: the resource fork's first three extents |
| +$62 | 4 | u32 | `filResrv`: reserved |

All of it **[Doc]** *Inside Macintosh: Files*, except the meaning of `filFlags` bit 1, **[Doc]** TN1150;
`FInfo` and `FXInfo` **[Doc]** *Inside Macintosh: Macintosh Toolbox Essentials*.

ClassicMac reports, for each file record: the name (the raw bytes of the key's name), the folder path (section 7.5),
the Finder information (`filUsrWds` followed by `filFndrInfo`, 32 bytes), the creation and modification dates (a
stored 0 comes out as "no date"), and both forks, read through `filExtRec` / `filRExtRec` and the extents overflow
file (section 9). It ignores `filStBlk`, `filRStBlk` and the physical lengths.

### 7.4 Thread records

A thread record lets the File Manager find a folder (or file) from its CNID alone. Its key is (the CNID, empty name)
**[Doc]** *Inside Macintosh: Files*, so it sorts first among the records with that parent ID (section 6.6).

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | i8 | `cdrType`: 3 (folder) or 4 (file) |
| +$01 | 1 | u8 | `cdrResrv2`: reserved |
| +$02 | 8 | u32[2] | `thdResrv`: reserved |
| +$0A | 4 | u32 | `thdParID`: the parent's CNID |
| +$0E | 32 | Str31 | `thdCName`: the folder's or file's name |

All of it **[Doc]** *Inside Macintosh: Files*. Every folder has a thread; a file has one only when something asked
for it (a file ID reference) **[Doc]** *Inside Macintosh: Files*.

ClassicMac skips thread records: the folder records already give each folder's parent and name.

### 7.5 Folder paths

A file's path is found by going up from its key's parent ID: each folder's record gives its name and its own
parent, until the root folder (CNID 2) or its parent (CNID 1) **[Doc]** *Inside Macintosh: Files*. A Mac path joins
the names with `:` **[Doc]** *Inside Macintosh: Files*.

ClassicMac reads every folder record first, then builds each file's path from them. The path runs from the root
down and does not include the root folder (the volume name). If a parent is missing from the catalog, or the chain
loops back on itself, the path is cut there (the file keeps the folders below that point, placed at the root) and
`hfs.orphan` is reported.

---

## 8. The extents overflow file

The extents overflow file (CNID 3) is a B-tree holding the extents a fork cannot fit in its first extent record
**[Doc]** *Inside Macintosh: Files*. Its length and extents are in the MDB (`drXTFlSize`, `drXTExtRec`). Each leaf
record is an 8-byte key and one 12-byte extent record:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | `xkrKeyLen`: 7 |
| +$01 | 1 | u8 | `xkrFkType`: $00 data fork, $FF resource fork |
| +$02 | 4 | u32 | `xkrFNum`: the file's CNID |
| +$06 | 2 | u16 | `xkrFABN`: the fork's allocation block (counted from the fork's start) that the record's first extent holds |
| +$08 | 12 | ExtDataRec | the next three extents |

All of it **[Doc]** *Inside Macintosh: Files*. So a fork whose first record covers 30 blocks continues in the record
keyed with `xkrFABN` 30, and so on **[Doc]** *Inside Macintosh: Files*.

ClassicMac reads the whole extents overflow file first, grouping the records by (fork type, CNID) and ordering each
group by `xkrFABN`. It does not check that each record's `xkrFABN` equals the blocks the previous extents covered. If
the extents overflow file's own extents are unusable (section 9), it goes on without overflow extents.

---

## 9. Reading a fork

A fork is the concatenation of its extents' allocation blocks in order, first the three in its first extent record,
then the overflow records in `xkrFABN` order, cut to the logical length; the physical length and any blocks past the
logical length are ignored **[Doc]** *Inside Macintosh: Files*. A logical length of 0 is an empty fork.

ClassicMac:

- takes extents only until they cover the logical length, and passes over descriptors with a count of 0;
- rejects the whole fork if any extent it needs lies outside the volume (`xdrStABN + xdrNumABlks` past `drNmAlBlks`),
  reporting `hfs.extent-outside`; the file keeps an empty fork (for the catalog file, the volume cannot be read);
- cuts a fork whose extents cover less than its logical length, reporting `hfs.fork-short`;
- cuts a fork whose extents run past the end of the image, reporting `hfs.image-truncated`.

Forks are **read in place**: a fork is kept as a list of byte ranges in the image (`ExtentForkData`), and its bytes
are read from the image each time it is opened. Nothing is copied when a volume is listed, so a large disk image
costs only its catalog. The same holds for MFS forks and for volumes inside partition maps, which are ranges of the
disk image.

---

## 10. Consistency checks

The volume header's counts let a reader check it saw the whole directory:

- **MFS:** `drNmFls` is the number of files on the volume **[Doc]** *Inside Macintosh II*.
- **HFS:** `drFilCnt` is the number of files and `drDirCnt` the number of folders on the volume **[Doc]** *Inside
  Macintosh: Files*. `drDirCnt` does not count the root folder **[Doc]** TN1150 (for the HFS Plus field that
  replaces it); **[Code]** Mac OS 9.0 ROM: mounting a volume that was not unmounted cleanly, `MountVol` finds the
  root folder's record, counts the folder records after it and rewrites `drDirCnt` if it differs. The initializer
  leaves it 0, and only folders made or deleted through the File Manager change it **[Code]** System 7.1 File
  Manager.
  `drNmFls` and `drNmRtDirs` count only the root folder's contents **[Doc]** *Inside Macintosh: Files*.

ClassicMac compares the files it read with `drNmFls` (MFS) or `drFilCnt` (HFS), and the folders other than the root
with `drDirCnt`, and reports a difference as `mfs.counts` / `hfs.counts` (Info: the files read are still good, but
some may be missing or the header may be stale).

---

## 11. HFS Plus

HFS Plus (Mac OS 8.1 and later) keeps its volume header at byte 1024, signature `'H+'`, version 4 **[Doc]** TN1150.
HFSX uses signature `'HX'`, version 5. ClassicMac reads either volume directly and reads an HFS Plus volume embedded
in a classic HFS wrapper. The wrapper's MDB has `drEmbedSigWord` (+$7C) and `drEmbedExtent` (+$7E, start and count);
the embedded byte offset is `drAlBlSt × 512 + drEmbedExtent.start × drAlBlkSiz` **[Doc]** TN1150. Wrapper reads follow
that extent and return the embedded volume's entries, not the wrapper's placeholder file. The reader checks the
alternate volume header 1,024 bytes before the volume end for a matching signature and version; it reports a warning
if the recovery copy is absent or invalid and continues using the primary header **[Doc]** TN1150. A cleared clean-
unmount bit or set boot-inconsistent bit produces `hfs.plus-volume-inconsistent`; the reader completes its structural
checks and returns files when those checks succeed **[Doc]** TN1150. It also recognizes bit 14 as Apple's later
`kHFSVolumeInconsistentBit` and reports the same warning **[Code]**
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h), although TN1150 marks
that bit reserved. If the journaled bit is set, the reader reports `hfs.plus-journal-not-replayed`; it returns the
on-disk structures without applying journal transactions **[Doc]** TN1150. Other reserved volume-attribute bits are
ignored **[Doc]** TN1150.

For each catalog file and folder, the reader checks that the volume header's `encodingsBitmap` contains the bit for
the record's `textEncoding` hint. Values below 64 use the same-numbered bit; MacFarsi (140) uses bit 49 and
MacUkrainian (152) uses bit 48 **[Doc]** TN1150. Missing bits produce the informational diagnostic
`hfs.plus-encoding-bitmap`; extra bits are accepted because TN1150 permits them to remain set after the last name using
an encoding has been deleted.

The reader checks the catalog B-tree's root index graph, node heights and same-level sibling links, then walks its
linked leaf nodes, checking the header's leaf endpoints, backward links and node range. For each tree, the total
records found across all leaf nodes must match that tree's header `leafRecords` count **[Doc]** TN1150. It also requires
the B-tree header node to contain three records and a zero backward link **[Doc]**. It checks that the catalog and
extents B-tree headers have the required control-file type **[Doc]**, that catalog B-tree nodes meet TN1150's 4 KiB
minimum **[Doc]**, that the catalog, extents-overflow and attributes B-tree headers use the control-file type **[Doc]** TN1150, and that their key-layout attributes use 16-bit key lengths, with variable-length index keys in
the catalog and attributes trees and fixed-length index keys in the extents tree **[Doc]** TN1150. The B-tree map is one
most-significant-bit-first bit per node and
continues in linked map nodes when the header map record is too small **[Doc]** TN1150. The reader requires exactly
enough continuation map nodes to cover the tree, checks their descriptors, record boundaries and bitmap coverage, and requires the header, index, leaf and map nodes to be marked
allocated with no additional nodes marked allocated, matching the tree's reachable nodes **[Code]** as Apple's HFS
verifier's `CmpBTM` does. It verifies `freeNodes` against the complete bitmap, and requires unused bytes after the
final byte containing node bits in the last map record to be zero **[Code]** as `CmpBTM` does in
[`SVerify2.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/SVerify2.c). Every node
marked free must also be zero-filled **[Code]**, as Apple's `BTCheckUnusedNodes` checks in the same verifier. It checks that
catalog key lengths exactly match their stored Unicode name lengths, as TN1150 specifies, B-tree fork length matches
`totalNodes × nodeSize`, and folder and file catalog records are exactly 88 and 248 bytes respectively **[Code]** as
Apple's `CheckCatalogRecord` verifies in
[`CatalogCheck.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).
Variable-length catalog thread records are at most 520 bytes, the full `HFSPlusCatalogThread` size; Apple's verifier
accepts that maximum and rejects larger records **[Code]** in `CheckCatalogRecord`. Header nodes have height zero and use the required 106-byte header record, 128-byte user
record and remaining map record, index records contain exactly the padded key and child pointer, and leaf-record keys are
unique. It checks the catalog, extents-overflow and attributes B-trees' `maxKeyLength` against their defined maxima
(516, 10 and 266 bytes respectively). The attributes maximum follows `kHFSPlusAttrKeyMaximumLength` in Apple's
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h); the catalog and
extents maxima are defined by TN1150 **[Doc]**. Catalog B-tree materialization obeys
`ContainerReadOptions.MaxExpandedBytesPerInput`, and throws `InvalidDataException` when file and folder
records exceed `ContainerReadOptions.MaxVolumeEntries` (including the root folder). File catalog IDs must be at least
16, and folder catalog IDs must be
at least 16 except for the root folder's ID 2 **[Doc]** [TN1150](https://developer.apple.com/library/archive/technotes/tn/tn1150.html);
Apple's `CheckFile` and `CheckDirectory` enforce these bounds **[Code]** in
[`CatalogCheck.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).
Folder records must not set the file-locked or thread-exists flags **[Code]**; Apple's `CheckDirectory` rejects
those file-only flags in the same verifier.
An initialized BSD permission record must identify a folder as a directory and a file as a supported BSD node type;
zero mode is accepted as uninitialized, while mismatches and unknown types produce the
`hfs.plus-invalid-bsd-mode` warning **[Code]** Apple's
[`CheckBSDInfo`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).
The root folder's catalog key must use parent ID 1, `kHFSRootParentID` **[Doc]** TN1150. File and folder catalog keys
must have nonempty names **[Doc]** TN1150. Catalog key names and thread names must be fully decomposed in canonical
combining-mark order **[Doc]** TN1150. Their 16-bit sequences must also be well-formed UTF-16: supplementary
characters use valid surrogate pairs, and isolated surrogate code units are rejected **[Doc]** Unicode Standard §3.9.
The reader validates this with fixed Unicode 3.2 canonical decomposition data, including algorithmic Hangul decomposition
and TN1150's preserved ranges U+2000–U+2FFF, U+F900–U+FAFF and U+2F800–U+2FAFF; it does not depend on the host
runtime's evolving normalization tables. HFSX rejects the legacy doubled U+0307 and U+0306 + U+0307 sequences, plus
Greek tonos sequences formed by U+030D after Greek tonos bases or diaeresis, and also these `fsck_hfs` fixes **[Code]** Apple's
[`FixDecomps`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c): Bengali BA plus
nukta to RA with middle diagonal, Odia YA plus nukta to U+0B5F, Gurmukhi DDA plus nukta to U+0A5C, Thai and Lao vowel
sequences to their AM letters, and two Tibetan three-character sequences to U+0F77 and U+0F79. Since HFS+ has no
field recording which decomposition version created its names, the reader continues to accept these legacy sequences
there. File and folder records named exactly `.` or `..` are rejected; Apple `fsck_hfs` marks those catalog names
illegal **[Code]** [`CheckCatalogName`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).
Names such as `.hidden` and `...` remain valid. It also accepts the Unicode 2.1 forms of 44 code points whose
canonical decomposition changed by Unicode 3.2, including U+01F8 and U+01F9 **[Doc]** TN1150; HFSX requires the
updated forms **[Code]** Apple's
[`FixDecomps`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c) and
[`DecompData.h`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/DecompData.h).
Other Unicode 2.1/3.2 sequence changes and `fsck_hfs` fixup cases remain open.
Catalog IDs are unique, every nonroot file or folder record's `parentID` names an existing folder, and each required
file and folder thread points back to its record's parent and name **[Doc]** TN1150. `nextCatalogID`
must be at least 16 even when IDs have been reused; otherwise, TN1150 requires it to exceed every catalog ID.
TN1150 requires leaf-record keys to be unique **[Doc]**. It
checks each folder's recorded valence against its direct file and folder records, and checks the ancestry of every
nonroot folder, including empty folders. TN1150 defines valence as the count of file and folder records whose key
parent ID is that folder's ID **[Doc]**. Unless the volume's catalog-ID-reuse flag is set, `nextCatalogID` must be
greater than all file and folder IDs, as TN1150 requires **[Doc]**. It then resolves file paths and reads both forks,
Finder info and dates. It
also reads data and resource fork overflow
extents and requires the primary and overflow extents to account for each fork's declared allocation-block count
(which may exceed the blocks needed by its logical length). Overflow records are allowed only after all eight initial
extent descriptors are occupied, every non-final overflow record must contain eight extents, and no matching records
may remain after the fork's declared block count is covered **[Doc]** TN1150. It rejects overlapping allocation ranges among the
extents it reads, following TN1150's allocation-file ownership model. Extent descriptors must be contiguous from the
first descriptor, and every unused descriptor must be all zero **[Doc]** TN1150. Empty forks cannot retain extent
descriptors or overflow records. The volume header must provide the required
allocation file **[Doc]** TN1150; the reader requires its bitmap to cover the declared allocation blocks and to mark
each parsed extent as allocated, along with the blocks containing the first 1,536 and last 1,024 volume bytes
**[Doc]** TN1150. Any bitmap bits beyond the declared allocation-block count must be clear **[Doc]** TN1150. It checks
that parsed fork extents do not claim any allocation blocks containing the volume's primary or alternate header or
the reserved areas around them, following TN1150's description of those areas as reserved. It checks
that the number of free bitmap bits agrees with the volume header's `freeBlocks`; a mismatch is reported as an
informational diagnostic **[Doc]** TN1150. Bit 9 (`kHFSVolumeSparedBlocksBit`) indicates that bad-block records
exist; a mismatch between that flag and CNID 5 extent records is reported as an informational diagnostic **[Doc]**
TN1150. It checks the attributes and startup special-file forks from the volume
header, including their overflow extents, and accounts for allocated blocks even when a special fork's logical size is
zero. The attributes B-tree exists when its fork has allocated blocks, as TN1150 specifies; an allocated but empty
attributes fork is rejected as a missing B-tree header. It accounts
for every extent record in the extents-overflow tree, including bad-block records and records not needed to read a
catalog fork **[Doc]** TN1150. Every non-bad-block overflow record must also resolve to a catalog or special-file
fork; by inference from TN1150's key semantics, orphan records are rejected because their file ID, fork type and
`startBlock` must identify the fork extents they extend. Bad-block records remain keyed to CNID 5 and the data fork
**[Doc]** TN1150. It walks
the attributes B-tree
and includes defined fork-data and extent attribute records in allocation checks; inline and unknown attribute
records do not claim extents **[Doc]** TN1150. For each extent-backed attribute it requires one fork-data record at
key `startBlock` zero, matches extension records by file ID and attribute name, and requires each extension's
`startBlock` to continue the preceding extent count. The initial record must contain eight extents when overflow is
present, and each non-final extension record must contain eight **[Doc]** TN1150. Their combined extent count must equal
`HFSPlusForkData.totalBlocks`, and the logical size must fit in those allocated blocks **[Doc]** TN1150 / Apple's
`HFSPlusAttrForkData` and `HFSPlusAttrExtents` definitions in
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h). Orphan extensions and
gaps in an attribute fork's extent sequence are rejected. Attribute keys are validated and ordered by file ID, name length,
binary UTF-16 name and start block in leaves and index nodes; the key's reserved padding field must be zero, and
separators must bound their child key ranges. The key
ordering uses Apple's HFS comparator for the rule that
TN1150 leaves unfinished **[Code]** [Apple HFS `hfs_attrkeycompare`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_xattr.c#L2082-L2144).
It checks extents-overflow keys are strictly ordered by
file ID, fork type and start block in both index and leaf records, as TN1150 specifies **[Doc]**, including ranges across index sibling nodes. Unicode names are
retained in `MacFile.MacPath`; the HFSX catalog's `keyCompareType` selects binary or case-folding mode. Catalog keys
are checked in order in leaves and index nodes, across sibling ranges, and against child key bounds. HFSX's `0xBC`
mode compares unsigned UTF-16 code units; HFS+ and HFSX `0xCF` use TN1150's `FastUnicodeCompare` behavior with
Unicode 3.2 simple lowercase mappings and default-ignorable characters skipped **[Doc]**. U+0000 sorts after other
characters, and controls and surrogates remain significant. The
legacy MacRoman `Name` field is a best-effort representation. HFS+ and HFSX remain read-only. Structural damage to
the volume header, B-trees, catalog records, forks or wrapper extent is rejected as unreadable input. HFS+ and HFSX
symbolic links are identified by `S_IFLNK` plus the required Finder type/creator codes; their data fork is retained and also
exposed as a strict UTF-8 `MacFile.SymbolicLinkTarget`. Null bytes, invalid UTF-8 and a nonempty resource fork are
rejected **[Doc]** TN1150. Targets are not resolved. Hard links are identified by Finder type/creator `hlnk`/`hfs+`
and their nonzero BSD `special` link reference **[Doc]** TN1150. A file with only one of the hard-link Finder codes is
retained as an ordinary file and reported as `hfs.plus-hardlink-signature-invalid`. A valid reference resolves to
`iNode<decimal-reference>` in the root's
`\0\0\0\0HFS+ Private Data` directory **[Doc]** TN1150. The link's visible path is kept while the indirect node's
forks and file metadata are used; the private directory subtree is omitted from the file list. The reference is exposed
as `MacFile.HardLinkReference`. Indirect-node names must use canonical decimal text without leading zeroes **[Doc]**
TN1150; malformed names are skipped and reported as `hfs.plus-hardlink-indirect-name-invalid`. A matching reference
that names a folder instead of a file is reported as `hfs.plus-hardlink-indirect-not-file`. A nonzero link reference
without a matching node is retained with its catalog forks and reported as `hfs.plus-hardlink-target-missing`. The
indirect node's BSD special field is treated as its estimated link count; a difference from the number of catalog hard
links is reported as the informational `hfs.plus-hardlink-count-mismatch` because TN1150 says traditional Mac OS can
make this estimate inaccurate. An indirect node with no referring hard link is reported as the informational
`hfs.plus-hardlink-indirect-orphan`. The
B-tree reader requires every record start and end offset to be
even, including each node's free-space offset **[Doc]** TN1150. It requires extents-overflow records to have the fixed
64-byte `HFSPlusExtentRecord` payload and defined attribute fork-data and extents payloads to have their fixed 88- and
72-byte lengths. TN1150 says undefined attribute record types must be ignored, so their payloads are not interpreted.
Catalog thread records may contain trailing bytes up to the 520-byte structure maximum; Apple's verifier accepts that
maximum, so the reader does not require a thread payload to end immediately after its declared name **[Code]** in
[`CatalogCheck.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).

The current implementation checks the header and node kinds/heights, root-to-leaf graph, per-level sibling chains,
leaf chain and endpoints, key ordering, child key ranges, record counts, node-map coverage/allocation and free-node
accounting. Header fields documented as reserved are intentionally not treated as required-zero checks. Reachable
empty leaves are accepted: TN1150 does not explicitly forbid them, and Apple's verifier's leaf traversal counts their
zero records without a separate nonempty-node check **[Code]** in
[`SVerify2.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/SVerify2.c).
As TN1150 permits, allocation
blocks marked used but not described by known
fork extents are not rejected; the reader checks that every extent it recognizes is marked allocated.

HFSX, the variant of HFS Plus with case-sensitive names (Mac OS X 10.3 and later), has the signature `'HX'` at 1024
**[Doc]** TN1150. Nothing in Mac OS 9.0 recognises it: no code in the ROM or the System file compares with `'HX'`,
and the ROM's `MountVol` accepts only `'BD'` and `$D2D7` **[Code]** Mac OS 9.0 ROM and System. An HFSX volume
presumably fails to mount with `noMacDskErr` (−57); that outcome is inferred, not run. ClassicMac recognizes HFSX
independently of Mac OS 9.0 and reads its catalog and forks. It validates key order using the volume's comparison mode;
catalog lookup is not currently exposed.

---

## 12. Diagnostics

Severity: **I** Info, **W** Warning, **E** Error. "Not traced" means the Mac's behaviour in that case has not been
followed in its code.

| Code | Sev. | Meaning | ClassicMac | The Mac |
| --- | --- | --- | --- | --- |
| `partition.map-truncated` | E | The image ends before all `pmMapBlkCnt` entries | Stops; keeps the volumes found | Not traced |
| `partition.bad-entry` | E | An entry within the map has no `'PM'` | Skips it | The CD-ROM driver stops reading the map there **[Code]** Mac OS 9.0 |
| `partition.skipped` | I | A partition is not `Apple_HFS` or `Apple_MFS` (the map, drivers, free space, …) | Skips it | The CD-ROM driver makes a drive of each `Apple_HFS` partition and ignores the rest **[Code]** Mac OS 9.0 |
| `partition.outside` | E | A volume starts at or past the end of the image | Skips it | Not traced |
| `partition.truncated` | E | A volume runs past the end of the image | Keeps the part that is there | Not traced |
| `mfs.directory-truncated` | E | The file directory runs past the end of the image | Reads the whole blocks there | Not traced |
| `mfs.bad-entry` | E | A directory entry's name runs past its block | Ends that block's entries | Not traced |
| `mfs.bad-chain` | E | A fork's block chain leaves the volume, loops, or reaches a free block | Keeps the blocks before it | Not traced |
| `mfs.fork-short` | E | A fork's chain or the image holds fewer bytes than its logical length | Cuts the fork | Not traced |
| `mfs.too-many-entries` | E | More than `MaxVolumeEntries` files | Stops reading | — |
| `mfs.counts` | I | The directory's file count differs from `drNmFls` | Reports only | Not traced |
| `hfs.plus-counts` | I | HFS Plus catalog file/folder counts differ from the volume header | Reports only | Not traced |
| `hfs.plus-encoding-bitmap` | I | A catalog file or folder uses an encoding whose bit is absent from `encodingsBitmap` | Reports only | Not traced |
| `hfs.plus-free-blocks` | I | The allocation bitmap free-block count differs from `freeBlocks` in the volume header | Reports only | Not traced |
| `hfs.plus-spared-blocks` | I | The volume header's spared-blocks flag disagrees with bad-block extent records | Reports only | Not traced |
| `hfs.plus-volume-inconsistent` | W | Volume attributes indicate an unclean unmount, inconsistent boot volume, or serious inconsistency | Completes structural checks and returns files if valid | Not traced |
| `hfs.plus-journal-not-replayed` | I | Volume is marked journaled, but ClassicMac has not replayed its journal | Reads recorded structures | Not traced |
| `hfs.plus-alternate-header` | W | The alternate HFS Plus volume header is missing or has an invalid signature/version | Reads using the primary header | Not traced |
| `hfs.plus-hardlink-target-missing` | W | A hard-link reference has no matching private indirect node | Keeps the link record, using its catalog forks | Not traced |
| `hfs.bad-link` | E | A leaf link leaves the B-tree or returns to a node already read | Stops the walk; keeps the records read | Not traced |
| `hfs.not-leaf` | E | A node on the leaf chain is not a leaf | Stops the walk; keeps the records read | Not traced |
| `hfs.bad-record-offset` | E | A record's offsets in its node are impossible | Skips the record | Not traced |
| `hfs.bad-record` | W | A catalog record of an unknown type, or a folder or file record too short | Skips it | Not traced |
| `hfs.too-many-entries` | E | More than `MaxVolumeEntries` folders and files | Stops reading the catalog | — |
| `hfs.orphan` | W | A file's parent folder is missing, or the parents loop | Cuts the path there | Not traced |
| `hfs.extent-outside` | E | An extent lies past `drNmAlBlks` | Empty fork | Not traced |
| `hfs.fork-short` | E | A fork's extents cover less than its logical length | Cuts the fork | Not traced |
| `hfs.image-truncated` | E | The image ends inside a fork | Cuts the fork | — |
| `hfs.counts` | I | The files or folders read differ from `drFilCnt` / `drDirCnt` | Reports only | Not traced |

Some damage makes a volume unreadable. The reader then throws, and the unwrapper reports `container.unreadable`
with the reason:

- MFS or HFS: an allocation block size of 0 or not a multiple of 512.
- MFS: an allocation block map that runs past the end of the image.
- HFS: a catalog file whose extents lie outside the volume.
- HFS: a B-tree file larger than `MaxExpandedBytesPerInput`.
- HFS Plus: a malformed volume header, B-tree, catalog record, fork extent or wrapper embed extent.

## 13. Conservative HFS writing

`HfsWriter.ReplaceFork` **[Author]** accepts a plain HFS volume, a colon-separated file path, a data or resource fork,
and replacement bytes. It returns a new image; the input is never modified. `ForkSaver.SaveHfsImageAs` writes that
verified image to a separate destination using a temporary file and rename; it refuses a destination that is the source
image. The editor exposes this as Save As ▸ HFS Volume Image. It does not write partition maps, Disk Copy images, MFS,
or HFS Plus volumes.

Before editing, it checks that the target extents are allocated in the volume bitmap, are in bounds, and do not overlap
the system files or other catalogued forks. It also validates the extents B-tree header, index keys and child pointers,
node ordering and sibling links, node map, and free-node and leaf-record counts.

The writer follows the extents in the file record and extents-overflow file. It writes the bytes into those allocation
blocks, clears the remaining bytes in those blocks, and updates the fork's logical and physical EOFs. When more blocks
are needed, it first extends the final extent over immediately following free blocks, then uses other free contiguous
runs if the terminal extent record has enough unused descriptors. If more descriptors are needed, it inserts new
records into the extents-overflow B-tree in key order. Insertion and deletion rebuild the tree's leaf and index nodes
from the sorted records, including first-key changes and transitions to or from an empty tree. The rebuilt tree updates
sibling links, root and leaf header fields, the node map, and its free-node count. It uses only nodes already present in
the tree file. When more nodes are needed, it allocates free blocks to the tree file, updates its primary MDB extents
and physical length, and adds linked map nodes when the header map fills. It refuses growth that needs more than three
extents for the extents-overflow file itself. It marks the volume bitmap and decrements
`drFreeBks`. When tree growth changes the MDB and an alternate MDB exists beyond the allocation area, it refreshes that
copy too. On shrink, it releases trailing blocks until the fork uses only the number required by its new logical
length; fully released descriptors are zeroed, and empty overflow records are removed. It marks released blocks free and
increments `drFreeBks`. The target file's modification date and the MDB's last-modified date are set to the local
write time, and `drWrCnt` is incremented once. A locked file and a software-locked volume are
refused.

Before returning, the writer reopens the result with `HfsReader` and checks the file list, Finder information, dates,
paths, both target forks, and both forks of every unrelated file. Any structural diagnostic or mismatch fails the
operation. It validates the extents B-tree again from the output image, including its index graph and node map.
`HfsWriter.CreateFile` and `DeleteFile` add or remove catalog file records and allocate or reclaim both forks.
`CreateFolder` and `DeleteFolder` maintain folder records, folder threads, parent valence and MDB file/folder counts;
deleting a nonempty folder or locked file is refused. Catalog insertion and deletion rebuild the catalog B-tree and
can extend its extents with free blocks, adding linked map nodes and catalog overflow-extent records as needed. These operations return new images
and reopen them through the HFS reader. Before and after an edit, the writer validates both B-trees' index graph,
sibling links, node maps, free-node counts, record counts, and key order. New catalog keys include their alignment byte
in `ckrKeyLen` as real HFS volumes do. File and folder creation accept optional Mac creation and modification dates;
when omitted, both are set to the local creation time. A real hfsutils-formatted volume has also been used to verify
fork edits, folder changes and catalog growth by remounting the output with hfsutils.
Before catalog mutation, the writer also checks that every catalogued extent is allocated, has no overlap with another
file or system fork, that `drFreeBks` matches the volume bitmap, that catalog file and folder IDs are unique, and that
folder valences and MDB file/folder counts match the catalog. Deleting a file removes its optional file thread.

The extents-overflow file is limited to its three MDB extents. Apple's `ExtendFileC` returns `fxOvFlErr` when its first
extent record is full, so the writer refuses further growth at that limit **[Code]** Apple
[`SExtents.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/SExtents.c). Catalog edits
compare Mac Roman bytes with the full `_RelString` weight rules in section 6.6, including distinct `Á` and `á`, equal
space and non-breaking space, and the feminine and masculine ordinal weights. The generated 256 weights were checked
against Apple's `gCompareTable` with no differences; the table itself is not included here. Fork lookup applies the
same comparison to every path component.

When a drive's block at 1024 is not a volume the File Manager knows, `MountVol` hands it to the external file
systems and, if none takes it, the volume does not mount **[Code]** Mac OS 9.0 File Manager.

---

## 14. Not covered and open questions

Not covered: the boot blocks; complete HFS Plus/HFSX semantics and structural validation (section 11); the old `'TS'`
partition map.

No rule in this document is fitted to data alone. Still open:

1. The partition map rules (sections 3.1–3.4) come from the code only. They cannot be checked in SheepShaver, which
   uses its own disk and CD drivers rather than Apple's.
2. That an HFSX volume fails to mount on Mac OS 9.0 with −57 is inferred from the code, not run (section 11).
3. The Mac OS 9.0 initializer's `drDirCnt` was not traced; the System 7.1 one leaves it 0 (section 10).
4. TN1150 says Mac OS 8.1–10.2 used Unicode 2.1 decompositions, while Mac OS X 10.3 and later use Unicode 3.2.
   The volume does not record which version produced its names. The reader validates against 3.2 and preserves the
   historical dot-above, Greek tonos and Bengali spellings noted in section 11; the remaining uncommon Unicode version
   changes and Apple's `fsck_hfs` fixup cases need compatibility coverage.

Differences from the Mac that ClassicMac knowingly keeps (section 3.4): a partition's volume ends where the partition
ends (`pmPartBlkCnt − pmLgDataStart` blocks, where the CD-ROM driver takes `pmPartBlkCnt` from the data start);
`Apple_MFS` partitions are read too; partition types must match exactly, where the driver compares the first nine
bytes with `Apple_HFS`; an entry without `'PM'` is skipped rather than ending the map; and no multisession base is
applied.
