# Apple partition maps

The Apple partition map divides a hard disk, a CD or a disk image of one into partitions: a driver descriptor map in
block 0, then one 512-byte entry per partition from block 1, the map itself included [Doc: Inside Macintosh: Devices].
Mac volumes sit in `Apple_HFS` (and on early disks `Apple_MFS`) partitions; drivers, patches and free space have
partitions of their own. ClassicMac turns each Mac volume partition into one file whose data fork is the volume, for
the volume readers to open.

| | |
| --- | --- |
| Identified by | `'ER'` (`$4552`) or a zero word at byte 0, and `'PM'` (`$504D`) at byte 512 or 2048 |
| ClassicMac | Reads: `ClassicMac.Files.Hfs` (`PartitionMapReader`) |
| Verified against | Nothing yet: SheepShaver uses its own disk and CD drivers, not Apple's |
| Sources | *Inside Macintosh: Devices*; TN1150; the Mac OS 9.0 ROM, Apple CD/DVD Driver 1.3.1 and Disk Copy 6.5 (disassembly) |

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

Blocks are as in [hfs.md §1.1](hfs.md#11-units). Every table in this section is [Doc: Inside Macintosh: Devices]
unless a row says otherwise.

### 1.1 The driver descriptor map (block 0)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `sbSig` | `$4552` (`'ER'`) |
| +$02 | 2 | `sbBlkSize` | The device's block size in bytes |
| +$04 | 4 | `sbBlkCount` | Number of device blocks |
| +$08 | 2 | `sbDevType` | Device type, reserved |
| +$0A | 2 | `sbDevId` | Device ID, reserved |
| +$0C | 4 | `sbData` | Reserved |
| +$10 | 2 | `sbDrvrCount` | Number of driver descriptors that follow |
| +$12 | 8 × n | driver descriptors | Each: `ddBlock` (u32, the driver's first block), `ddSize` (u16, its size in 512-byte blocks), `ddType` (u16, operating system type, 1 = Mac OS) |
| … | | | Zero to the end of the block |

The drivers themselves live in partitions of type `Apple_Driver…` (§1.3), which is how the Mac loads a disk's driver
at startup.

### 1.2 Partition entries (blocks 1 to n)

Each partition, the map included, has one 512-byte entry; the entries fill consecutive blocks from block 1.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `pmSig` | `$504D` (`'PM'`) |
| +$02 | 2 | `pmSigPad` | Reserved, 0 |
| +$04 | 4 | `pmMapBlkCnt` | Entries in the map; the same in every entry |
| +$08 | 4 | `pmPyPartStart` | The partition's first physical block |
| +$0C | 4 | `pmPartBlkCnt` | The partition's size in blocks |
| +$10 | 32 | `pmPartName` | The partition's name, NUL-terminated if shorter than 32 |
| +$30 | 32 | `pmParType` | The partition's type, NUL-terminated if shorter than 32 (§1.3) |
| +$50 | 4 | `pmLgDataStart` | First block of the data area, relative to the partition's start |
| +$54 | 4 | `pmDataCnt` | Size of the data area in blocks; no Mac OS 9.0 mounting path reads it (§2.2) |
| +$58 | 4 | `pmPartStatus` | Status flags (valid, allocated, in use, bootable, readable, writable, …); bit 5 writable |
| +$5C | 4 | `pmLgBootStart` | First block of the boot code, relative to the partition's start |
| +$60 | 4 | `pmBootSize` | Boot code's size in bytes |
| +$64 | 4 | `pmBootAddr` | Address to load the boot code at |
| +$68 | 4 | `pmBootAddr2` | Reserved |
| +$6C | 4 | `pmBootEntry` | Boot code's entry point |
| +$70 | 4 | `pmBootEntry2` | Reserved |
| +$74 | 4 | `pmBootCksum` | Boot code's checksum |
| +$78 | 16 | `pmProcessor` | The processor the boot code is for (`"68000"`, …) |
| +$88 | 376 | `pmPad` | Reserved, zero |

### 1.3 Partition types

| `pmParType` | Contents |
| --- | --- |
| `Apple_partition_map` | The map itself, from block 1 |
| `Apple_Driver`, `Apple_Driver43` | A device driver |
| `Apple_MFS` | An MFS volume ([mfs.md](mfs.md)) |
| `Apple_HFS` | An HFS volume ([hfs.md](hfs.md)), or an HFS Plus volume, which keeps the same type [Doc: Inside Macintosh: Devices; TN1150] |
| `Apple_Free` | Unused space |
| `Apple_Scratch` | An empty partition |
| `Apple_PRODOS`, `Apple_Unix_SVR2` | Apple II and A/UX file systems |

Later disks carry more driver and patch types (for ATA and ATAPI drives, for example); a reader needs to recognise only
the volume types.

## 2. Reading

### 2.1 The unit of the block numbers

The block numbers in an entry (`pmPyPartStart`, `pmPartBlkCnt`, `pmLgDataStart`) count blocks of the size the reader
addresses the device in, **never `sbBlkSize`** [Code: Mac OS 9.0 ROM and CD-ROM driver; Disk Copy 6.5]:

| Reader | Entries at | Unit of the block numbers |
| --- | --- | --- |
| ROM boot-volume locator, ATA driver loader | 512-byte blocks 1, 2, … | 512 |
| ROM ATAPI boot loader | 2048-byte blocks 1, 2, … (the first 512 bytes of each) | 2048 |
| CD-ROM driver, ATAPI | a probed stride of 512 or 2048 bytes (§2.2) | the stride |
| CD-ROM driver, SCSI | 512-byte blocks | 512 |
| Disk Copy 6.5 | a probed stride of 8, 4, 2 or 1 × 512 bytes | the stride, for `Apple_HFS` |

So a disk or disk image uses 512-byte units, and a CD mastered with its map at a 2048-byte stride uses 2048-byte units
throughout. Of block 0, Mac OS 9.0's CD-ROM driver reads only `sbSig` and goes on when it is `'ER'` or 0, not even
`sbBlkSize` [Code: Mac OS 9.0 CD-ROM driver (Apple CD/DVD Driver 1.3.1)]; two boot loaders use `sbBlkSize` only as
the unit of `ddSize` [Code: Mac OS 9.0 ROM].

### 2.2 Which partitions are volumes

Mac OS 9.0's ATAPI CD-ROM driver reads a disc's map this way [Code: Mac OS 9.0 CD-ROM driver]:

1. Block 0 must start with `'ER'` or a zero word; otherwise there is no map.
2. The stride is probed: `'PM'` at byte 512 gives a stride of 512; otherwise `'PM'` at byte 2048 gives 2048; otherwise
   there is no map. Nothing else is tried.
3. The entries are at byte `i × stride` for i = 1 to the first entry's `pmMapBlkCnt`; the walk stops at the first
   entry without `'PM'`.
4. Every block number is multiplied by the stride. The drive starts at `(pmPyPartStart + pmLgDataStart) × stride` and
   is `pmPartBlkCnt × stride` bytes long: `pmLgDataStart` is not taken off the size, and `pmDataCnt` is never read. The
   session base of a multisession disc is multiplied by the stride too, which matters only for a 2048-byte map outside
   the first session.
5. An entry whose type's first nine bytes are `Apple_HFS` becomes a drive; every other type is ignored. A partition
   whose `pmPartStatus` bit 5 (writable) is clear is write-protected.

So every `Apple_HFS` partition becomes a drive of its own, and several volumes on one disc all mount. A disc without a
partition map becomes one drive covering the whole disc [Code: Mac OS 9.0 CD-ROM driver].

*Inside Macintosh: Devices* describes `pmDataCnt` as the size of the data area [Doc], but no Mac OS 9.0 mounting path
reads it: the CD-ROM drivers, the ROM's boot-volume locator and ATA driver loader all size a partition from
`pmPartBlkCnt` [Code: Mac OS 9.0 ROM and CD-ROM driver]. Disk Copy 6.5, rescaling a map, rewrites `pmDataCnt` only when
it is non-zero, so 0 means "not given" [Code: Disk Copy 6.5].

## 3. Writing

The map is never written. A volume in a partition is changed in place, within the partition's bytes (§2.2): the volume
keeps its size, so no entry changes, and every byte outside the partition stays as it was [ClassicMac].

## 4. Variants

- **The SCSI CD-ROM driver** is simpler than the ATAPI one: a 512-byte stride only, `pmLgDataStart` ignored, and only
  the first `Apple_HFS` partition mounted [Code: Mac OS 9.0 CD-ROM driver].
- **The old `'TS'` map**, with the signature `'TS'`, preceded this one [Doc: Inside Macintosh: Devices]; the CD-ROM
  driver still accepts it at byte 512 [Code: Mac OS 9.0 CD-ROM driver].

## 5. ClassicMac

ClassicMac reads the map as the ATAPI driver does (§2.2): block 0 with `'ER'` or a zero word (nothing else in block 0
is read, and no driver is needed), the stride probed at 512 then 2048, `pmMapBlkCnt` from the first entry, every block
number times the stride, and `pmDataCnt` ignored. Each `Apple_HFS` or `Apple_MFS` partition becomes one file, named
after `pmPartName`, whose data fork is the volume's bytes, which the volume readers then open. Every other type is
skipped and reported.

Writing: each HFS partition is edited as a volume and its changed sectors written back where it lies; the map, the
drivers, other partitions and the bytes between them stay as they were. A disk with one Mac volume partition is
edited as that volume (paths inside it, as for a plain image). On a disk with several, each partition is a folder
named after `pmPartName`, so a path starts with the partition's name (`disk.img:Two:Docs`), compared as HFS compares
names; when two partitions share a name, only the first is reached. An HFS Plus partition is repaired by First Aid
only, an MFS partition not written. A partition is never resized, since the map would change.

Where it differs from the driver, knowingly:

- The volume runs from `(pmPyPartStart + pmLgDataStart) × stride` for `pmPartBlkCnt − pmLgDataStart` blocks, so it
  ends where the partition ends.
- `Apple_MFS` partitions are read too.
- The type must be exactly `Apple_HFS` or `Apple_MFS`, case included, where the driver compares nine bytes.
- An entry without `'PM'` is skipped and the walk goes on, where the driver stops.
- The image is one session: no session base is added. The multisession base is applied only to a cue sheet's last
  session ([cd-images.md](../disk-images/cd-images.md)), without the driver's multiplication by the stride.

Damaged and truncated maps, for each entry:

1. If the image ends before the entry, stop.
2. Skip an entry without `'PM'` and go on to the next.
3. Skip a volume that starts at or past the end of the image.
4. Cut a volume that runs past the end of the image to what is there. The volume reader then reports each fork that
   loses bytes (`hfs.image-truncated`, `mfs.fork-short`).

The `'TS'` map is not read.

`PartitionMapReader.Partitions` lists the Mac volume partitions with their entry number, name, type and byte range, as
`Read` finds them. A disk whose map holds exactly one Mac volume partition, a plain HFS one, is writable: the edit
session (`InputEditSession`) edits that partition as a volume ([hfs.md §3](hfs.md#3-writing)) and saves the disk with
the partition put back, the map, drivers and free space unchanged. A disk with more than one Mac volume partition, or
whose only one is MFS or wraps HFS Plus, is read only. The CLI's `check` runs the writer's checks on each HFS
partition.

## 6. Diagnostics

"Not traced" means the Mac's behaviour in that case has not been followed in its code.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `partition.bad-entry` | Error | An entry within the map has no `'PM'` | Skips it | The CD-ROM driver stops reading the map there [Code: Mac OS 9.0 CD-ROM driver] |
| `partition.map-truncated` | Error | The image ends before all `pmMapBlkCnt` entries | Stops; keeps the volumes found | Not traced |
| `partition.outside` | Error | A volume starts at or past the end of the image | Skips it | Not traced |
| `partition.skipped` | Info | A partition is not `Apple_HFS` or `Apple_MFS` (the map, drivers, free space, …) | Skips it | The CD-ROM driver makes a drive of each `Apple_HFS` partition and ignores the rest [Code: Mac OS 9.0 CD-ROM driver] |
| `partition.truncated` | Error | A volume runs past the end of the image | Keeps the part that is there | Not traced |

## 7. Verification

`tests/ClassicMac.Files.Tests/PartitionMapTests.cs` builds maps in code: Mac volumes come out and drivers are skipped,
a CD map at a 2048-byte stride, a partition past the end of the image cut, and plain volumes not taken for maps.
`PartitionedVolumeTests.cs` covers `Partitions`, an HFS partition edited and saved (as a new file and in place) with
every byte outside it unchanged, and a disk with two HFS partitions left read only. The
rules of §2 come from the code only; they cannot be checked in SheepShaver, which uses its own disk and CD drivers
rather than Apple's.

## 8. Not covered

- The `'TS'` map; drivers and boot code; writing maps; writing a disk with more than one Mac volume partition.
- No rule in this document is fitted to data alone.

## 9. References

1. Apple, *Inside Macintosh: Devices* (1994), the SCSI Manager chapter: the driver descriptor map and the partition
   map.
2. Apple, Technical Note TN1150, *HFS Plus Volume Format*: HFS Plus volumes in `Apple_HFS` partitions.
3. The Mac OS 9.0 ROM (boot-volume locator, ATA driver loader, ATAPI boot loader) and Apple CD/DVD Driver 1.3.1 (the
   SCSI and ATAPI CD-ROM drivers), traced in disassembly.
4. Disk Copy 6.5, traced in disassembly: its partition map code.
