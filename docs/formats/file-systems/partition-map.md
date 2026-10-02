# Apple partition maps

Contents

1. [The driver descriptor map (block 0)](#1-the-driver-descriptor-map-block-0)
2. [Partition entries (blocks 1 to n)](#2-partition-entries-blocks-1-to-n)
3. [Partition types](#3-partition-types)
4. [Which partitions are Mac volumes](#4-which-partitions-are-mac-volumes)
5. [Damaged and truncated maps](#5-damaged-and-truncated-maps)
6. [Diagnostics](#6-diagnostics)
7. [Not covered and open questions](#7-not-covered-and-open-questions)

---

## 1. The driver descriptor map (block 0)

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
(§3), which is how the Mac loads a disk's driver at startup **[Doc]** *Inside Macintosh: Devices*.

Mac OS 9.0's CD-ROM driver reads only `sbSig` and goes on when it is `'ER'` **or 0**; nothing else in block 0 is
read, not even `sbBlkSize` **[Code]** Mac OS 9.0 CD-ROM driver (Apple CD/DVD Driver 1.3.1). Two boot loaders use
`sbBlkSize` only as the unit of `ddSize` **[Code]** Mac OS 9.0 ROM.

ClassicMac does the same: it checks only `sbSig`, accepting `'ER'` or 0, and needs no driver.

---

## 2. Partition entries (blocks 1 to n)

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
| +$30 | 32 | char[32] | `pmParType`: the partition's type, NUL-terminated if shorter than 32 (§3) |
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
| CD-ROM driver, ATAPI | a probed stride of 512 or 2048 bytes (§4) | the stride |
| CD-ROM driver, SCSI | 512-byte blocks | 512 |
| Disk Copy 6.5 | a probed stride of 8, 4, 2 or 1 × 512 bytes | the stride, for `Apple_HFS` |

So a disk or disk image uses 512-byte units, and a CD mastered with its map at a 2048-byte stride uses 2048-byte
units throughout. ClassicMac takes the unit from the stride, as the CD-ROM driver does (§4).

---

## 3. Partition types

| `pmParType` | Contents |
| --- | --- |
| `Apple_partition_map` | The map itself, from block 1 **[Doc]** *Inside Macintosh: Devices* |
| `Apple_Driver`, `Apple_Driver43` | A device driver **[Doc]** *Inside Macintosh: Devices* |
| `Apple_MFS` | An MFS volume ([mfs.md](mfs.md)) **[Doc]** *Inside Macintosh: Devices* |
| `Apple_HFS` | An HFS volume ([hfs.md §3](hfs.md#3-hfs-volume-layout)), or an HFS Plus volume, which keeps the same type **[Doc]** *Inside Macintosh: Devices*; TN1150 |
| `Apple_Free` | Unused space **[Doc]** *Inside Macintosh: Devices* |
| `Apple_Scratch` | An empty partition **[Doc]** *Inside Macintosh: Devices* |
| `Apple_PRODOS`, `Apple_Unix_SVR2` | Apple II and A/UX file systems **[Doc]** *Inside Macintosh: Devices* |

Later disks carry more driver and patch types (for ATA and ATAPI drives, for example); a reader only needs to
recognise the two volume types.

---

## 4. Which partitions are Mac volumes

Mac OS 9.0's ATAPI CD-ROM driver reads a disc's map this way **[Code]** Mac OS 9.0 CD-ROM driver:

1. Block 0 must start with `'ER'` or a zero word (§1); otherwise there is no map.
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
- an entry without `'PM'` is skipped and the walk goes on (§5), where the driver stops;
- the image is one session: no session base is added.

---

## 5. Damaged and truncated maps

ClassicMac, for each entry:

- stops if the image ends before the entry (`partition.map-truncated`);
- skips an entry without `'PM'` (`partition.bad-entry`) and goes on to the next;
- skips a volume that starts at or past the end of the image (`partition.outside`);
- cuts a volume that runs past the end of the image to what is there (`partition.truncated`). The volume reader then
  reports each fork that loses bytes (`hfs.image-truncated`, `mfs.fork-short`).

---

## 6. Diagnostics

Severity: **I** Info, **W** Warning, **E** Error. "Not traced" means the Mac's behaviour in that case has not been
followed in its code.

| Code | Sev. | Meaning | ClassicMac | The Mac |
| --- | --- | --- | --- | --- |
| `partition.map-truncated` | E | The image ends before all `pmMapBlkCnt` entries | Stops; keeps the volumes found | Not traced |
| `partition.bad-entry` | E | An entry within the map has no `'PM'` | Skips it | The CD-ROM driver stops reading the map there **[Code]** Mac OS 9.0 |
| `partition.skipped` | I | A partition is not `Apple_HFS` or `Apple_MFS` (the map, drivers, free space, …) | Skips it | The CD-ROM driver makes a drive of each `Apple_HFS` partition and ignores the rest **[Code]** Mac OS 9.0 |
| `partition.outside` | E | A volume starts at or past the end of the image | Skips it | Not traced |
| `partition.truncated` | E | A volume runs past the end of the image | Keeps the part that is there | Not traced |

---

## 7. Not covered and open questions

No rule in this document is fitted to data alone. Still open:

1. The partition map rules (§§1–4) come from the code only. They cannot be checked in SheepShaver, which
   uses its own disk and CD drivers rather than Apple's.

Differences from the Mac that ClassicMac knowingly keeps (§4): a partition's volume ends where the partition
ends (`pmPartBlkCnt − pmLgDataStart` blocks, where the CD-ROM driver takes `pmPartBlkCnt` from the data start);
`Apple_MFS` partitions are read too; partition types must match exactly, where the driver compares the first nine
bytes with `Apple_HFS`; an entry without `'PM'` is skipped rather than ending the map; and the multisession base is
applied only to a cue sheet's last session ([cd-images.md §3](../disk-images/cd-images.md#3-multisession-discs)), without the driver's multiplication by the stride.
