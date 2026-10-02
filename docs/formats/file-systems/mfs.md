# MFS volumes

The Macintosh File System is the flat file system of the original Macintosh and its 400K floppies: one directory lists
every file on the volume, and each fork is a chain of allocation blocks through a block map [Doc: Inside Macintosh
II]. Folders existed only in the Finder's information. ClassicMac lists every file with its Finder information, dates
and both forks.

| | |
| --- | --- |
| Identified by | `$D2D7` at byte 1024; on a partitioned disk, an `Apple_MFS` partition ([partition-map.md](partition-map.md)) |
| ClassicMac | Reads: `ClassicMac.Files.Hfs` (`MfsReader`) |
| Verified against | Nothing yet |
| Sources | *Inside Macintosh II*, "The File Manager"; the Mac OS 9.0 ROM and System 7.1 File Manager (disassembly) |

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

Logical and allocation blocks are as in [hfs.md §1.1](hfs.md#11-units); MFS numbers allocation blocks from 2. Every
table in this section is [Doc: Inside Macintosh II].

### 1.1 Volume blocks

| Logical blocks | Contents |
| --- | --- |
| 0–1 | Boot blocks |
| 2 onwards | The volume information (64 bytes at byte 1024), followed at byte 1088 by the allocation block map |
| `drDirSt` to `drDirSt + drBlLen − 1` | The file directory |
| from `drAlBlSt` | Allocation blocks 2, 3, … |

Allocation block `n` (n ≥ 2) starts at byte `drAlBlSt × 512 + (n − 2) × drAlBlkSiz`.

### 1.2 The volume information

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `drSigWord` | `$D2D7` |
| +$02 | 4 | `drCrDate` | When the volume was initialised |
| +$06 | 4 | `drLsBkUp` | When it was last backed up |
| +$0A | 2 | `drAtrb` | Volume attributes: bit 7 locked by hardware, bit 15 locked by software |
| +$0C | 2 | `drNmFls` | Files in the directory, that is on the volume |
| +$0E | 2 | `drDirSt` | The directory's first logical block |
| +$10 | 2 | `drBlLen` | The directory's length in logical blocks |
| +$12 | 2 | `drNmAlBlks` | Number of allocation blocks |
| +$14 | 4 | `drAlBlkSiz` | Allocation block size in bytes, a multiple of 512 |
| +$18 | 4 | `drClpSiz` | Clump size: bytes to allocate at a time |
| +$1C | 2 | `drAlBlSt` | Logical block where allocation block 2 starts |
| +$1E | 4 | `drNxtFNum` | Next unused file number |
| +$22 | 2 | `drFreeBks` | Free allocation blocks |
| +$24 | 28 | `drVN` | Volume name, `Str27` |

### 1.3 The allocation block map

The map follows the volume information directly: one **12-bit** entry per allocation block, packed. The entry for
allocation block `n` occupies bits `(n − 2) × 12` to `(n − 2) × 12 + 11` of the map, most significant bit first. The map
is `⌈drNmAlBlks × 3 / 2⌉` bytes long. For index `i = n − 2` and `p = i × 3 / 2` (integer division):

- `i` even: `entry = map[p] << 4 | map[p + 1] >> 4`;
- `i` odd: `entry = (map[p] & $0F) << 8 | map[p + 1]`.

| Entry | Meaning |
| --- | --- |
| 0 | The block is free |
| 1 | The block is the last of its fork |
| 2 to $FFF | The block is in use; the value is the number of the fork's next block |

### 1.4 The file directory

The directory is a run of variable-length entries, each starting on an even offset. An entry never crosses a logical
block; the space after a block's last entry is unused.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | `flFlags` | Bit 7 set if the entry is in use, bit 0 set if the file is locked |
| +$01 | 1 | `flTyp` | Version number, 0 |
| +$02 | 16 | `flUsrWds` | `FInfo`: type, creator, flags, location, folder (`fdFldr`) |
| +$12 | 4 | `flFlNum` | File number |
| +$16 | 2 | `flStBlk` | Data fork's first allocation block; 0 if empty |
| +$18 | 4 | `flLgLen` | Data fork's logical length in bytes |
| +$1C | 4 | `flPyLen` | Data fork's physical length (whole allocation blocks) |
| +$20 | 2 | `flRStBlk` | Resource fork's first allocation block; 0 if empty |
| +$22 | 4 | `flRLgLen` | Resource fork's logical length |
| +$26 | 4 | `flRPyLen` | Resource fork's physical length |
| +$2A | 4 | `flCrDat` | When the file was created |
| +$2E | 4 | `flMdDat` | When it was last modified |
| +$32 | 1 + n | `flNam` | File name, `Str255` |
| +$33 + n | 0 or 1 | pad | Makes the entry's length even |

An entry's length is `51 + n`, rounded up to even.

## 2. Reading

### 2.1 The directory

The File Manager scans each directory block from its start [Code: Mac OS 9.0 ROM]:

1. A `flFlags` byte of **0** ends the block's entries. The whole byte is tested, not bit 7: a non-zero flags byte
   without bit 7 is still an entry. Bit 7 is only set when an entry is made.
2. Otherwise read the entry (§1.4).
3. The next entry is at `offset + 51 + n`, rounded up to even; the scan goes on only while that is below **460**.

Deleting an entry slides the later ones down and zeroes the tail, so a block has no holes [Code: System 7.1 File
Manager].

### 2.2 Reading a fork

1. A logical length of 0 is an empty fork, whatever the start block says.
2. Otherwise start at `flStBlk` (or `flRStBlk`) and follow the map: each block's entry names the next, until an entry
   of 1.
3. The fork's bytes are those blocks in chain order, cut to the logical length; the physical length only says how much
   was allocated.

All of it [Doc: Inside Macintosh II].

### 2.3 Folders

MFS has no directories. The folders a user saw were kept by the Finder: each file's `fdFldr` (in its `FInfo`) names
the Finder folder it appears in [Doc: Inside Macintosh II; Inside Macintosh: Macintosh Toolbox Essentials].

### 2.4 Counts

`drNmFls` is the number of files on the volume [Doc: Inside Macintosh II], so a reader can check it saw the whole
directory.

## 3. Writing

None.

## 4. Variants

None. HFS replaced MFS ([hfs.md](hfs.md)); the Mac OS 9.0 ROM's `MountVol` still recognises `$D2D7` [Code: Mac OS
9.0 ROM] ([hfs.md §2.1](hfs.md#21-finding-the-volume)).

## 5. ClassicMac

- The volume is recognised by `$D2D7` at 1024 in an input of at least 1024 + 64 bytes ([hfs.md §5.1](hfs.md#51-recognising-a-volume)).
- The directory is scanned as in §2.1: an entry is any non-zero flags byte, a zero one ends the block, and no entry
  starts at block offset 460 or later. An entry whose name would run past its block ends that block. A directory that
  runs past the end of the image is read as far as whole blocks go.
- A fork's chain is followed only until it covers the logical length, consecutive blocks merged into one range and the
  fork read in place ([hfs.md §5.2](hfs.md#52-what-comes-out)). The chain stops, keeping what it has, at a block number
  below 2, at or past `drNmAlBlks + 2` or already visited (a loop), and at a free block (entry 0) before the length is
  covered. A chain or an image that holds fewer bytes than the logical length cuts the fork.
- Each entry comes out with its name (the raw bytes of `flNam`), its Finder information (`flUsrWds`, with an all-zero
  `FXInfo`), its creation and modification dates (a stored 0 is "no date") and both forks. Files have an empty folder
  path; `fdFldr` is kept in the Finder information as stored.
- The entries read are compared with `drNmFls`. After `ContainerReadOptions.MaxVolumeEntries` files (1,000,000 by
  default) reading stops.
- The reader throws, and the unwrapper reports `container.unreadable`, when `drAlBlkSiz` is 0 or not a multiple of 512,
  or when the allocation block map runs past the end of the image.

## 6. Diagnostics

"Not traced" means the Mac's behaviour in that case has not been followed in its code.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `mfs.bad-chain` | Error | A fork's block chain leaves the volume, loops, or reaches a free block | Keeps the blocks before it | Not traced |
| `mfs.bad-entry` | Error | A directory entry's name runs past its block | Ends that block's entries | Not traced |
| `mfs.counts` | Info | The directory's file count differs from `drNmFls` | Reports only | Not traced |
| `mfs.directory-truncated` | Error | The file directory runs past the end of the image | Reads the whole blocks there | Not traced |
| `mfs.fork-short` | Error | A fork's chain or the image holds fewer bytes than its logical length | Cuts the fork | Not traced |
| `mfs.too-many-entries` | Error | More than `MaxVolumeEntries` files | Stops reading | No such limit |

## 7. Verification

`tests/ClassicMac.Files.Tests/MfsTests.cs` builds MFS volumes in code: forks following their block chains, the
directory scan ending at a zero flags byte, a looping chain stopped, and an MFS volume unwrapped through the container
readers. No volume made by a Mac has been read yet.

## 8. Not covered

- Writing MFS volumes.
- The Finder's folders (`fdFldr`) are not turned into folder paths.
- No rule in this document is fitted to data alone.

## 9. References

1. Apple, *Inside Macintosh II* (1985), "The File Manager": MFS volumes, the volume information, the block map and the
   file directory.
2. Apple, *Inside Macintosh: Macintosh Toolbox Essentials*, the Finder Interface chapter: `FInfo`.
3. The Mac OS 9.0 ROM and the System 7.1 File Manager, traced in disassembly.
