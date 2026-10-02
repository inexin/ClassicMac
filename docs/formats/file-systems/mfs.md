# MFS volumes

MFS, the file system of the original Macintosh and its 400K floppies, is flat: one directory lists every file on the
volume, and each fork is a chain of allocation blocks through a block map **[Doc]** *Inside Macintosh II*.

Contents

1. [Layout](#1-layout)
2. [The volume information](#2-the-volume-information)
3. [The allocation block map](#3-the-allocation-block-map)
4. [The file directory](#4-the-file-directory)
5. [Reading an MFS fork](#5-reading-an-mfs-fork)
6. [Folders on MFS](#6-folders-on-mfs)
7. [What comes out](#7-what-comes-out)
8. [Diagnostics](#8-diagnostics)

---

## 1. Layout

| Logical blocks | Contents |
| --- | --- |
| 0–1 | Boot blocks |
| 2 onwards | The volume information (64 bytes at byte 1024), followed at byte 1088 by the allocation block map |
| `drDirSt` to `drDirSt + drBlLen − 1` | The file directory |
| from `drAlBlSt` | Allocation blocks 2, 3, … |

All of it **[Doc]** *Inside Macintosh II*. Allocation block `n` (n ≥ 2) starts at byte
`drAlBlSt × 512 + (n − 2) × drAlBlkSiz` **[Doc]** *Inside Macintosh II*.

---

## 2. The volume information

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
end of the image (the reader throws; §8).

---

## 3. The allocation block map

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

---

## 4. The file directory

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

---

## 5. Reading an MFS fork

A fork starts at `flStBlk` (or `flRStBlk`) and follows the map: each block's entry names the next, until an entry of
1 **[Doc]** *Inside Macintosh II*. The fork's bytes are those blocks in chain order, cut to the logical length; the
physical length only says how much was allocated **[Doc]** *Inside Macintosh II*. A logical length of 0 is an empty
fork, whatever the start block says.

ClassicMac walks the chain only until it covers the logical length, merging consecutive blocks into one range and
reading the fork in place ([hfs.md §7](hfs.md#7-reading-a-fork)). It stops the chain, keeps what it has and reports `mfs.bad-chain` when:

- a block number is below 2, at or past `drNmAlBlks + 2`, or already visited (a loop);
- a block's entry is 0 (a free block) before the length is covered.

If the chain, or the image, holds fewer bytes than the logical length, the fork is cut and `mfs.fork-short` reported.

---

## 6. Folders on MFS

MFS has no directories. The folders a user saw were kept by the Finder: each file's `fdFldr` (in its `FInfo`) names
the Finder folder it appears in **[Doc]** *Inside Macintosh II*; *Inside Macintosh: Macintosh Toolbox Essentials*.

ClassicMac gives MFS files an empty folder path and keeps `fdFldr` in the Finder information as stored.

---

## 7. What comes out

ClassicMac reports, for each entry: the name (the raw bytes of `flNam`), the Finder information (`flUsrWds`, with
an all-zero `FXInfo`), the creation and modification dates (a stored 0 comes out as "no date"), and both forks. The
directory's entry count is compared with `drNmFls` ([hfs.md §8](hfs.md#8-consistency-checks)).

---

## 8. Diagnostics

Severity: **I** Info, **W** Warning, **E** Error. "Not traced" means the Mac's behaviour in that case has not been
followed in its code.

| Code | Sev. | Meaning | ClassicMac | The Mac |
| --- | --- | --- | --- | --- |
| `mfs.directory-truncated` | E | The file directory runs past the end of the image | Reads the whole blocks there | Not traced |
| `mfs.bad-entry` | E | A directory entry's name runs past its block | Ends that block's entries | Not traced |
| `mfs.bad-chain` | E | A fork's block chain leaves the volume, loops, or reaches a free block | Keeps the blocks before it | Not traced |
| `mfs.fork-short` | E | A fork's chain or the image holds fewer bytes than its logical length | Cuts the fork | Not traced |
| `mfs.too-many-entries` | E | More than `MaxVolumeEntries` files | Stops reading | — |
| `mfs.counts` | I | The directory's file count differs from `drNmFls` | Reports only | Not traced |
