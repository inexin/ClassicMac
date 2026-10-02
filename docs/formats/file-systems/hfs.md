# HFS

This document describes how classic Mac OS lays out a disk: the Apple partition map that divides a hard disk or CD,
the flat Macintosh File System (MFS) of the first 400K floppies, and the Hierarchical File System (HFS) that every
Mac used from 1986 until HFS Plus. It is complete enough to write a reader that lists every file on a volume with its
folder path, Finder information, dates and both forks, without reading ClassicMac's code. It is the behaviour of
`PartitionMapReader`, `MfsReader` and `HfsReader` in `ClassicMac.Files.Hfs`. HFS Plus and HFSX are read-only formats
supported by the phase 10 implementation described in [hfs-plus.md](hfs-plus.md). Disk images that hold these volumes (Disk Copy
4.2, NDIF, DART, UDIF) are in
[disk-images/](../README.md#disk-images).

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
3. [HFS volume layout](#3-hfs-volume-layout)
4. [HFS B-trees](#4-hfs-b-trees)
5. [The catalog file](#5-the-catalog-file)
6. [The extents overflow file](#6-the-extents-overflow-file)
7. [Reading a fork](#7-reading-a-fork)
8. [Consistency checks](#8-consistency-checks)
9. [Conservative HFS writing](#9-conservative-hfs-writing)
10. [Diagnostics](#10-diagnostics)
11. [Not covered and open questions](#11-not-covered-and-open-questions)

---

## 1. Conventions

The shared conventions of [README.md](../README.md) hold: big-endian values, offsets in hex, sizes in decimal, Mac OS
Roman text, Mac dates as local-time seconds since 1904. In addition:

- A **logical block** (or sector) is 512 bytes and is numbered from the start of the volume (or, in [partition-map.md](partition-map.md), of
  the disk). Logical block 2 is the byte offset 1024.
- An **allocation block** is the unit a volume gives to files: a multiple of 512 bytes, set per volume. MFS numbers
  allocation blocks from 2, HFS from 0.
- `Str27` and `Str31` are Pascal strings stored in a field of 28 and 32 bytes; only the length byte and that many
  bytes count.
- Each rule carries a source tag from [README.md](../README.md). **[Code]** names the software: "Mac OS 9.0 ROM" for
  the File Manager and boot code in the Mac OS 9.0 ROM, "System 7.1 File Manager" for a routine read in that older
  code only. What is still open is listed in §11.
- Paragraphs that begin **"ClassicMac"** describe the reader's own choices for damaged or unusual input. They are
  not format rules and carry no tag; the diagnostics they raise are in §10.

---

## 2. Finding the volume

A volume starts with two logical blocks of boot blocks, and its identifying header is at byte 1024 (logical block
2) **[Doc]** *Inside Macintosh: Files*; *Inside Macintosh II*. The signature word there says what it is:

| Word at +$400 | Volume | Section |
| --- | --- | --- |
| `$D2D7` | MFS **[Doc]** *Inside Macintosh II* | [mfs.md](mfs.md) |
| `$4244` (`'BD'`) | HFS **[Doc]** *Inside Macintosh: Files* | §3 |
| `$482B` (`'H+'`) | HFS Plus **[Doc]** TN1150 | [hfs-plus.md](hfs-plus.md) |
| `$4858` (`'HX'`) | HFSX **[Doc]** TN1150; not a volume Mac OS 9.0 knows **[Code]** Mac OS 9.0 ROM | [hfs-plus.md](hfs-plus.md) |

A whole disk (a hard disk, a CD) instead starts with a driver descriptor map in logical block 0 and a partition map
from block 1 ([partition-map.md](partition-map.md)); each volume sits inside a partition.

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
- A partition map needs `'ER'` or a zero word at 0, and `'PM'` at 512 or, failing that, at 2048 ([partition-map.md §4](partition-map.md#4-which-partitions-are-mac-volumes)).

---

## 3. HFS volume layout

### 3.1 Blocks

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

ClassicMac does not read the boot blocks. It reads the declared volume bitmap, counts the bits for the declared
allocation blocks, and reports an unreadable bitmap as `hfs.bitmap-truncated` or a mismatch with `drFreeBks` as
`hfs.free-blocks`. It also reports `hfs.extent-unallocated` when a declared extent used by the extents-overflow file,
catalog, or file fork includes a block marked free. For nonempty forks, it checks every descriptor in the initial and
overflow extent records, including allocated blocks beyond the logical EOF; only the extents needed for the logical
fork bytes contribute to returned data. A zero-length fork returns no bytes but any extents it declares are still
checked for bounds and bitmap ownership. These bitmap diagnostics leave readable catalog and fork data available.

### 3.2 The master directory block

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
| +$7C | 2 | u16 | `drVCSize` (size of the volume cache); in an HFS wrapper, `drEmbedSigWord` ([hfs-plus.md](hfs-plus.md)) |
| +$7E | 2 | u16 | `drVBMCSize` (size of the bitmap cache); in a wrapper, the start of `drEmbedExtent` |
| +$80 | 2 | u16 | `drCtlCSize` (size of the common cache); in a wrapper, the count of `drEmbedExtent` |
| +$82 | 4 | u32 | `drXTFlSize`: the extents overflow file's logical length in bytes |
| +$86 | 12 | ExtDataRec | `drXTExtRec`: the extents overflow file's extents (§3.4) |
| +$92 | 4 | u32 | `drCTFlSize`: the catalog file's logical length in bytes |
| +$96 | 12 | ExtDataRec | `drCTExtRec`: the catalog file's first three extents |

All of it **[Doc]** *Inside Macintosh: Files*, except the wrapper fields and the first long of `drFndrInfo`,
**[Doc]** TN1150.

`drAtrb` bits: 7 locked by hardware, 8 unmounted cleanly, 9 bad blocks spared, 15 locked by software **[Doc]**
*Inside Macintosh: Files*; TN1150.

ClassicMac reads `drNmAlBlks`, `drAlBlkSiz`, `drAlBlSt`, `drVN`, `drFilCnt`, `drDirCnt`, the two files' lengths and
extents, and `drEmbedSigWord`. It rejects a volume whose `drAlBlkSiz` is 0 or not a multiple of 512 (§10).
A volume name longer than 27 bytes is cut to 27.

### 3.3 The alternate MDB

A copy of the MDB, the alternate MDB, is kept in the second-to-last logical block of the volume, for disk repair
utilities to use when the MDB is damaged **[Doc]** *Inside Macintosh: Files*. The File Manager does not update it on
every change, so its counts and dates may be older than the MDB's **[Doc]** *Inside Macintosh: Files*. Disk Copy
treats that block as part of the volume's structure: making a read-only or compressed image, it stores the blocks up
to the first allocation block, the used allocation blocks and block N − 2 as data, and everything else as zeros
**[Code]** Disk Copy 6.3.3; **[Verified]** on images Disk Copy 6.1.2 made in SheepShaver, Mac OS 9.0.

ClassicMac reads only the MDB at 1024 and never falls back to the alternate. It checks the HFS signature in the
alternate MDB at `image length − 1024`, reports a missing or invalid signature as `hfs.alternate-mdb`, and continues
reading from the primary MDB. It does not compare the remaining fields because the copy may be stale **[Doc]**
*Inside Macintosh: Files*; the alternate MDB is not used to repair a damaged primary.

### 3.4 Extents

An **extent** is a run of contiguous allocation blocks; an **extent record** (`ExtDataRec`) is three extent
descriptors, 12 bytes **[Doc]** *Inside Macintosh: Files*:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | `xdrStABN`: the first allocation block |
| +$02 | 2 | u16 | `xdrNumABlks`: the number of allocation blocks (0 = unused descriptor) |

A fork's first three extents are in its catalog record (or, for the two B-tree files, in the MDB); further extents
are in the extents overflow file, three per record (§6) **[Doc]** *Inside Macintosh: Files*. The extents
overflow file keeps all its extents in the MDB and never overflows itself; the catalog file can **[Doc]** *Inside
Macintosh: Files*.

### 3.5 Reserved catalog node IDs

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

## 4. HFS B-trees

The catalog and the extents overflow file are both B-trees: files made of **512-byte nodes**, numbered from 0 in the
order they appear in the file's forks **[Doc]** *Inside Macintosh: Files*.

### 4.1 The node descriptor

Every node starts with a 14-byte descriptor **[Doc]** *Inside Macintosh: Files*:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | u32 | `ndFLink`: the next node of the same type and level (0 = none) |
| +$04 | 4 | u32 | `ndBLink`: the previous node of the same type and level (0 = none) |
| +$08 | 1 | i8 | `ndType`: −1 ($FF) leaf, 0 index, 1 header, 2 map |
| +$09 | 1 | u8 | `ndNHeight`: the node's level (leaves are 1) |
| +$0A | 2 | u16 | `ndNRecs`: the number of records in the node |
| +$0C | 2 | u16 | `ndResv2`: reserved |

### 4.2 Records and offsets

Records follow the descriptor from +$0E. The end of the node holds a table of `u16` offsets from the start of the
node, read backwards: the offset of record 0 is at +$1FE, record 1 at +$1FC, and so on; after the last record's
offset comes one more, the offset of the node's free space **[Doc]** *Inside Macintosh: Files*. Record `i` therefore
runs from `offset[i]` to `offset[i + 1]`. Offsets are even **[Doc]** *Inside Macintosh: Files*.

A record in an index or leaf node is a key followed by data. The key starts with its length byte, which does not
count itself; the data starts at the next even offset after the key **[Doc]** *Inside Macintosh: Files*.

ClassicMac checks each record's offsets before use: the start must be at least 14, the end must be after the start
and must not reach into the offset table. A record that fails is skipped (`hfs.bad-record-offset`); the node's
other records are still read. A key that leaves no room for data is skipped silently.

### 4.3 The header node (node 0)

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

ClassicMac reads `bthFNode` and uses the fixed 512-byte HFS node size. It warns with `hfs.bad-btree-header` if the
header node has a nonzero backward link, is not type 1, has nonzero height or reserved field, or does not contain three records. If `bthNodeSize`
disagrees, it reports the same diagnostic and continues using 512-byte nodes. It also compares `bthNNodes` with the
number of complete 512-byte nodes in the fork, checks that `bthRoot` is in range and agrees with `bthDepth`, and warns
if these fields are invalid; a fork ending with a partial node also warns. It checks that `bthKeyLen` is 37 for the catalog and 7 for the extents-overflow tree; a
mismatch is also a warning, and reading still uses the fork's actual length.
Header-node record starts must match the 106-byte header record, 128-byte user record and following map record layout;
invalid boundaries produce `hfs.bad-btree-header`.
It checks that the bitmap covers the nodes, follows required linked map nodes, and compares its free-bit count with
`bthFree`. It warns (`hfs.bad-btree-map`) if these disagree, if the header or a leaf node is marked free, or if a map
node is malformed or marked free; catalog files and forks remain readable. The leaf-chain endpoint and sum of leaf-node record counts are compared with
`bthLNode` and `bthNRecs`, with mismatches reported as `hfs.bad-btree-header`; each leaf's backward link is also
checked against the preceding leaf, with defects reported as `hfs.bad-link`.

### 4.4 Index and leaf nodes

Leaf nodes (type −1, level 1) hold the tree's data records, in ascending key order within a node and from node to
node along the `ndFLink` chain, which starts at `bthFNode` and ends at `bthLNode` **[Doc]** *Inside Macintosh: Files*.

Index nodes (type 0, level 2 and up) hold pointer records: a key and a `u32` child node number; the key is the first
key in that child **[Doc]** *Inside Macintosh: Files*. In an HFS tree the keys in index nodes are always stored at
the maximum length: the key length byte is `bthKeyLen`, the key is padded with zeros to that length, and the child
node number follows at `(bthKeyLen + 2)` rounded down to even **[Code]** Mac OS 9.0 ROM. A catalog index key is
therefore 38 bytes (key length 37) and an extents index key 8 bytes (key length 7), each followed by the `u32` child;
the initializer sets `bthKeyLen` to 37 and 7 **[Code]** System 7.1 File Manager. Only leaf keys have their actual
length (§5.1).

To find a key, start at `bthRoot`; in each index node take the last record whose key is less than or equal to the
key sought (none: the key is not in the tree) and go to its child; in the leaf, look for an equal key **[Doc]**
*Inside Macintosh: Files*.

### 4.5 Walking the leaves

A reader that wants every record needs no index node and no key comparison: it starts at `bthFNode` and follows
`ndFLink` until 0, reading each leaf's records in order. This is what ClassicMac does. It reads the whole B-tree file
into memory once (limited by `MaxExpandedBytesPerInput`, 1 GiB by default), and stops the walk, keeping the records
read so far, when:

- a link names a node at or past the end of the file, or a node already visited (`hfs.bad-link`);
- a linked node is not a level-1 leaf (`ndType` −1 and `ndNHeight` 1; `hfs.not-leaf`).

A B-tree file shorter than one node is read as empty.

### 4.6 Key comparison

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
  are equal. So the empty name of a thread record (§5.4) sorts before every other key with the same parent ID.
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

ClassicMac checks leaf keys in this order while walking the tree; duplicate or descending keys produce the
`hfs.key-order` warning while readable records remain available. The writer uses the same Mac OS 9 catalog comparator.

---

## 5. The catalog file

The catalog file (CNID 4) is a B-tree with one record for each folder and file, and a thread record for each
folder and for each file that needs one **[Doc]** *Inside Macintosh: Files*. Its length and first extents are in
the MDB (`drCTFlSize`, `drCTExtRec`); more extents may be in the extents overflow file under file ID 4 **[Doc]**
*Inside Macintosh: Files*.

### 5.1 The catalog key

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | `ckrKeyLen`: the key's length, not counting this byte |
| +$01 | 1 | u8 | `ckrResrv1`: reserved, 0 |
| +$02 | 4 | u32 | `ckrParID`: the parent folder's CNID (for a thread, the CNID the thread is for) |
| +$06 | 1+n | Str31 | `ckrCName`: the name (empty for a thread) |

In a leaf, `ckrKeyLen` is `6 + n` as the Mac OS File Manager writes it (a thread's key is 6, the Finder's
`Desktop DB` key 16), or `7 + n` for an even-length name when the alignment byte is counted, as hfsutils and
ClassicMac's writer do. Either way the data starts at the next even offset. ClassicMac accepts both
**[Verified]** on volumes Mac OS wrote in Basilisk II (Disk Copy 6 images). Names are at most 31 bytes **[Doc]**
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
or with invalid lengths, reserved byte or name length are skipped with `hfs.bad-record`; data shorter than 2 bytes is
skipped. After a million folders and files
(`MaxVolumeEntries`) it stops reading the catalog (`hfs.too-many-entries`).
Folder and file catalog IDs must be unique. Duplicate IDs are reported as `hfs.duplicate-id`; duplicate folders do not
replace the first folder mapping, and file records remain available. IDs 6–15 are reserved; only the root folder may
use the reserved root ID 2, and ordinary file/folder records must use IDs of at least 16. Violations produce
`hfs.reserved-id` while the records remain available.

### 5.2 Folder records

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

ClassicMac uses a folder record only for folder paths (§5.5): it keeps `dirDirID`, the key's parent ID and
the key's name. Folders do not come out as entries of their own, so an empty folder is not listed.

### 5.3 File records

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

ClassicMac reports, for each file record: the name (the raw bytes of the key's name), the folder path (§5.5),
the Finder information (`filUsrWds` followed by `filFndrInfo`, 32 bytes), the creation and modification dates (a
stored 0 comes out as "no date"), and both forks, read through `filExtRec` / `filRExtRec` and the extents overflow
file (§7). It ignores `filStBlk`, `filRStBlk` and the physical lengths.

### 5.4 Thread records

A thread record lets the File Manager find a folder (or file) from its CNID alone. Its key is (the CNID, empty name)
**[Doc]** *Inside Macintosh: Files*, so it sorts first among the records with that parent ID (§4.6).

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

### 5.5 Folder paths

A file's path is found by going up from its key's parent ID: each folder's record gives its name and its own
parent, until the root folder (CNID 2) or its parent (CNID 1) **[Doc]** *Inside Macintosh: Files*. A Mac path joins
the names with `:` **[Doc]** *Inside Macintosh: Files*.

ClassicMac reads every folder record first, then builds each file's path from them. The path runs from the root
down and does not include the root folder (the volume name). If a parent is missing from the catalog, or the chain
loops back on itself, the path is cut there (the file keeps the folders below that point, placed at the root) and
`hfs.orphan` is reported.

---

## 6. The extents overflow file

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
group by `xkrFABN`. For each fork, it checks that a record's `xkrFABN` equals the number of allocation blocks covered
by the preceding extents; a mismatch produces the `hfs.overflow-start` warning while the fork remains readable using
the records in key order. Each record must have a seven-byte key and a 12-byte extent record; malformed records
produce `hfs.overflow-record`, and records without a complete key or extent payload are skipped. If a payload has extra
bytes, ClassicMac uses its defined first 12 bytes after reporting the warning. These checks follow the
`xkrFABN` definition in *Inside Macintosh: Files*: it identifies the first allocation block of the first extent
descriptor in that record. If the extents overflow file's own extents are unusable (§7), it goes on without
overflow extents.

---

## 7. Reading a fork

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

## 8. Consistency checks

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

## 9. Conservative HFS writing

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
in `ckrKeyLen`, as hfsutils does (Mac OS leaves it out; both read the same). File and folder creation accept optional Mac creation and modification dates;
when omitted, both are set to the local creation time. A real hfsutils-formatted volume has also been used to verify
fork edits, folder changes and catalog growth by remounting the output with hfsutils.
Before catalog mutation, the writer also checks that every catalogued extent is allocated, has no overlap with another
file or system fork, that `drFreeBks` matches the volume bitmap, that catalog file and folder IDs are unique, and that
folder valences and MDB file/folder counts match the catalog. Deleting a file removes its optional file thread.

The extents-overflow file is limited to its three MDB extents. Apple's `ExtendFileC` returns `fxOvFlErr` when its first
extent record is full, so the writer refuses further growth at that limit **[Code]** Apple
[`SExtents.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/SExtents.c). Catalog edits
compare Mac Roman bytes with the full `_RelString` weight rules in §4.6, including distinct `Á` and `á`, equal
space and non-breaking space, and the feminine and masculine ordinal weights. The generated 256 weights were checked
against Apple's `gCompareTable` with no differences; the table itself is not included here. Fork lookup applies the
same comparison to every path component.

When a drive's block at 1024 is not a volume the File Manager knows, `MountVol` hands it to the external file
systems and, if none takes it, the volume does not mount **[Code]** Mac OS 9.0 File Manager.

---

## 10. Diagnostics

Severity: **I** Info, **W** Warning, **E** Error. "Not traced" means the Mac's behaviour in that case has not been
followed in its code.

| Code | Sev. | Meaning | ClassicMac | The Mac |
| --- | --- | --- | --- | --- |
| `hfs.bad-link` | E | A leaf link leaves the B-tree or returns to a node already read | Stops the walk; keeps the records read | Not traced |
| `hfs.bad-btree-header` | W | A B-tree header node has a bad descriptor, record boundaries, root/depth, node count, node size, maximum key length or leaf totals, or the fork holds a partial node | Reads on | Not traced |
| `hfs.bad-btree-map` | W | The node map does not cover the tree, its map nodes are malformed or marked free, a used node is marked free, or the free count differs from `bthFree` | Reads on | Not traced |
| `hfs.overflow-start` | W | An extents-overflow key's `xkrFABN` differs from the blocks covered by the extents before it | Keeps the fork readable from its records | Not traced |
| `hfs.overflow-record` | W | An extents-overflow key or extent record has the wrong fixed size | Skips incomplete records; reads the rest | Not traced |
| `hfs.not-leaf` | E | A node on the leaf chain is not a leaf | Stops the walk; keeps the records read | Not traced |
| `hfs.bad-record-offset` | E | A record's offsets in its node are impossible | Skips the record | Not traced |
| `hfs.bad-record` | W | A catalog key is malformed, its record type is unknown, or a folder/file record is too short | Skips it | Not traced |
| `hfs.key-order` | W | Catalog or extents-overflow leaf keys are duplicate or out of order | Keeps readable records | Not traced |
| `hfs.duplicate-id` | W | A catalog file or folder CNID appears more than once | Keeps file records; retains the first folder mapping | Not traced |
| `hfs.reserved-id` | W | A nonroot catalog file/folder uses a reserved CNID below 16 | Keeps the record | Not traced |
| `hfs.too-many-entries` | E | More than `MaxVolumeEntries` folders and files | Stops reading the catalog | — |
| `hfs.orphan` | W | A file's parent folder is missing, or the parents loop | Cuts the path there | Not traced |
| `hfs.extent-outside` | E | An extent lies past `drNmAlBlks` | Empty fork | Not traced |
| `hfs.fork-short` | E | A fork's extents cover less than its logical length | Cuts the fork | Not traced |
| `hfs.image-truncated` | E | The image ends inside a fork | Cuts the fork | — |
| `hfs.counts` | I | The files or folders read differ from `drFilCnt` / `drDirCnt` | Reports only | Not traced |
| `hfs.alternate-mdb` | W | The HFS alternate MDB at `image length − 1024` is missing or has an invalid signature | Reads using the primary MDB | JotaRandom/hfsutils specification test |
| `hfs.bitmap-truncated` | W | The bitmap bytes for all declared allocation blocks do not fit in the image | Reads the catalog without bitmap validation | Not traced |
| `hfs.free-blocks` | I | The volume bitmap's free-block count differs from `drFreeBks` | Reports only | HFS bitmap layout in JotaRandom/hfsutils specification |
| `hfs.extent-unallocated` | W | A catalog, extents-overflow, or file-fork extent contains an allocation block marked free | Reads available fork data | HFS allocation bitmap and extent records |

Some damage makes a volume unreadable. The reader then throws, and the unwrapper reports `container.unreadable`
with the reason:

- MFS or HFS: an allocation block size of 0 or not a multiple of 512.
- MFS: an allocation block map that runs past the end of the image.
- HFS: a catalog file whose extents lie outside the volume.
- HFS: a B-tree file larger than `MaxExpandedBytesPerInput`.
- HFS Plus: a malformed volume header, B-tree, catalog record, fork extent or wrapper embed extent.

---

## 11. Not covered and open questions

Not covered: boot blocks; Hot Files B-tree semantics; journal replay and `fsck_hfs` repair behavior; HFS wrapper
software-lock and bad-block-file consistency; the old `'TS'` partition map. HFS+ read-path structural checks and
their explicit limits are recorded in [hfs-plus.md](hfs-plus.md).

No rule in this document is fitted to data alone. Still open:

1. The Mac OS 9.0 initializer's `drDirCnt` was not traced; the System 7.1 one leaves it 0 (§8).
