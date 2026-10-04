# HFS

The Hierarchical File System is the volume format every Mac used from 1986 until HFS Plus: floppies, hard disks, CDs
and the disk images made of them (Disk Copy 4.2, NDIF, DART, UDIF). A volume has a master directory block, a volume
bitmap and two B-trees, the catalog (every folder and file) and the extents overflow file (the extents a fork cannot
keep in its catalog record). This document also holds what the volume formats share: the units (§1.1) and how a
volume is found at byte 1024 (§2.1). ClassicMac lists every file on a volume with its folder path, Finder information,
dates and both forks, and edits plain HFS volumes conservatively, returning a new image.

| | |
| --- | --- |
| Identified by | `$4244` (`'BD'`) at byte 1024; on a partitioned disk, an `Apple_HFS` partition ([partition-map.md](partition-map.md)) |
| ClassicMac | Reads and writes: `ClassicMac.Files.Hfs` (`HfsReader`, `HfsWriter`); `ForkSaver.SaveHfsImageAs` |
| Verified against | Volumes Mac OS wrote, in Disk Copy 6 images made in Basilisk II<br>Images Disk Copy 6.1.2 made in SheepShaver, Mac OS 9.0<br>hfsutils-formatted volumes, edited and remounted with hfsutils |
| Sources | *Inside Macintosh: Files*, *Text*, *Macintosh Toolbox Essentials*; TN1150; the Mac OS 9.0 ROM's File Manager and B-tree manager, the System 7.1 File Manager, Disk Copy 6.3.3 (disassembly); Apple's `fsck_hfs` source; hfsutils (behaviour only) |

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

The shared conventions of [README.md](../README.md#conventions) hold. Every table in this section is [Doc: Inside
Macintosh: Files] unless a row or note says otherwise.

### 1.1 Units

These hold for MFS ([mfs.md](mfs.md)), HFS, HFS Plus ([hfs-plus.md](hfs-plus.md)) and Apple partition maps
([partition-map.md](partition-map.md)).

- A **logical block** (or sector) is 512 bytes, numbered from the start of the volume (in a partition map, of the
  disk). Logical block 2 is byte offset 1024 [Doc: Inside Macintosh: Files; Inside Macintosh II].
- An **allocation block** is the unit a volume gives to files: a multiple of 512 bytes, set per volume. MFS numbers
  allocation blocks from 2, HFS from 0 [Doc: Inside Macintosh II; Inside Macintosh: Files].
- `Str27` and `Str31` are Pascal strings in fields of 28 and 32 bytes; only the length byte and that many bytes
  count.

### 1.2 Volume blocks

| Logical blocks | Contents |
| --- | --- |
| 0–1 | Boot blocks (signature `'LK'` on a startup disk, otherwise zero) |
| 2 | The master directory block (MDB, §1.3) |
| from `drVBMSt` (normally 3) | The volume bitmap: one bit per allocation block, most significant bit first, 1 = in use |
| from `drAlBlSt` | Allocation blocks 0, 1, … |
| N − 2 | The alternate MDB (N = the volume's size in logical blocks) |
| N − 1 | Reserved |

- Allocation block `n` starts at byte `drAlBlSt × 512 + n × drAlBlkSiz`.
- `drNmAlBlks` is 16 bits, so a volume has at most 65,535 allocation blocks; larger volumes use larger allocation
  blocks.
- Everything the File Manager keeps about files, the catalog and the extents overflow file included, is in
  allocation blocks; the boot blocks, MDB, bitmap and alternate MDB are not.

### 1.3 The master directory block

162 bytes at byte 1024 (logical block 2).

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `drSigWord` | `$4244` (`'BD'`) |
| +$02 | 4 | `drCrDate` | When the volume was created, in local time [Doc: Inside Macintosh: Files] |
| +$06 | 4 | `drLsMod` | When it was last modified, in local time |
| +$0A | 2 | `drAtrb` | Volume attributes: bit 7 locked by hardware, 8 unmounted cleanly, 9 bad blocks spared, 15 locked by software [Doc: Inside Macintosh: Files; TN1150] |
| +$0C | 2 | `drNmFls` | Files in the root folder |
| +$0E | 2 | `drVBMSt` | First logical block of the volume bitmap |
| +$10 | 2 | `drAllocPtr` | Where the next allocation search starts |
| +$12 | 2 | `drNmAlBlks` | Number of allocation blocks |
| +$14 | 4 | `drAlBlkSiz` | Allocation block size in bytes, a multiple of 512 |
| +$18 | 4 | `drClpSiz` | Default clump size |
| +$1C | 2 | `drAlBlSt` | Logical block where allocation block 0 starts |
| +$1E | 4 | `drNxtCNID` | Next unused catalog node ID |
| +$22 | 2 | `drFreeBks` | Free allocation blocks |
| +$24 | 28 | `drVN` | Volume name, `Str27` |
| +$40 | 4 | `drVolBkUp` | When the volume was last backed up, in local time; 0 = never |
| +$44 | 2 | `drVSeqNum` | Backup sequence number |
| +$46 | 4 | `drWrCnt` | Volume write count |
| +$4A | 4 | `drXTClpSiz` | Extents overflow file's clump size |
| +$4E | 4 | `drCTClpSiz` | Catalog file's clump size |
| +$52 | 2 | `drNmRtDirs` | Folders in the root folder |
| +$54 | 4 | `drFilCnt` | Files on the volume |
| +$58 | 4 | `drDirCnt` | Folders on the volume, the root not counted (§2.7) |
| +$5C | 32 | `drFndrInfo` | Finder information, 8 × u32; the first long is the blessed System Folder's ID [Doc: TN1150] |
| +$7C | 2 | `drVCSize` | Volume cache size; in an HFS wrapper, `drEmbedSigWord` [Doc: TN1150] ([hfs-plus.md §1.2](hfs-plus.md#12-the-hfs-wrapper)) |
| +$7E | 2 | `drVBMCSize` | Bitmap cache size; in a wrapper, `drEmbedExtent`'s start block [Doc: TN1150] |
| +$80 | 2 | `drCtlCSize` | Common cache size; in a wrapper, `drEmbedExtent`'s block count [Doc: TN1150] |
| +$82 | 4 | `drXTFlSize` | Extents overflow file's logical length in bytes |
| +$86 | 12 | `drXTExtRec` | Extents overflow file's extents (§1.4) |
| +$92 | 4 | `drCTFlSize` | Catalog file's logical length in bytes |
| +$96 | 12 | `drCTExtRec` | Catalog file's first three extents |

The alternate MDB, in the second-to-last logical block, is a copy for disk repair utilities. The File Manager does not
update it on every change, so its counts and dates may be older than the MDB's.

### 1.4 Extent records

An **extent** is a run of contiguous allocation blocks. An extent record (`ExtDataRec`, 12 bytes) is three extent
descriptors:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `xdrStABN` | First allocation block |
| +$02 | 2 | `xdrNumABlks` | Number of allocation blocks; 0 = unused descriptor |

A fork's first three extents are in its catalog record (for the two B-tree files, in the MDB). Further extents are in
the extents overflow file, three per record (§1.10). The extents overflow file keeps all its extents in the MDB and
never overflows itself; the catalog file can.

### 1.5 Catalog node IDs

Every file and folder has a catalog node ID (CNID), unique on the volume:

| CNID | Meaning |
| --- | --- |
| 1 | The parent of the root folder |
| 2 | The root folder |
| 3 | The extents overflow file |
| 4 | The catalog file |
| 5 | The bad block file: its extents, in the extents overflow file, cover the volume's bad blocks |
| 6–15 | Reserved [Doc: TN1150] |
| 16 and up | Ordinary files and folders |

### 1.6 B-tree nodes

The catalog and the extents overflow file are B-trees: files of **512-byte nodes**, numbered from 0 in the order they
appear in the file's forks. Every node starts with a 14-byte descriptor:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `ndFLink` | Next node of the same type and level; 0 = none |
| +$04 | 4 | `ndBLink` | Previous node of the same type and level; 0 = none |
| +$08 | 1 | `ndType` | i8: −1 (`$FF`) leaf, 0 index, 1 header, 2 map |
| +$09 | 1 | `ndNHeight` | The node's level; leaves are 1 |
| +$0A | 2 | `ndNRecs` | Records in the node |
| +$0C | 2 | `ndResv2` | Reserved |

Records follow from +$0E. The end of the node holds a table of `u16` offsets from the node's start, read backwards:
record 0's offset is at +$1FE, record 1's at +$1FC, and so on; after the last record's offset comes the offset of the
node's free space. Record `i` runs from `offset[i]` to `offset[i + 1]`. Offsets are even.

A record in an index or leaf node is a key and then data. The key starts with its length byte, which does not count
itself; the data starts at the next even offset after the key.

### 1.7 The header node

Node 0 is the header node, with three records: the header record (106 bytes, at +$0E), a 128-byte reserved record,
and the map record, a bitmap of the nodes in use (one bit per node, most significant bit first). A tree with more
nodes than the map record covers continues the bitmap in map nodes (type 2), linked from the header node through
`ndFLink`.

The header record:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `bthDepth` | Tree depth; 0 = empty |
| +$02 | 4 | `bthRoot` | Root node; 0 = empty tree |
| +$06 | 4 | `bthNRecs` | Leaf records |
| +$0A | 4 | `bthFNode` | First leaf node; 0 = none |
| +$0E | 4 | `bthLNode` | Last leaf node |
| +$12 | 2 | `bthNodeSize` | Node size, 512 |
| +$14 | 2 | `bthKeyLen` | Maximum key length: 37 in the catalog, 7 in the extents overflow file |
| +$16 | 4 | `bthNNodes` | Nodes in the tree |
| +$1A | 4 | `bthFree` | Free nodes |
| +$1E | 76 | `bthResv` | Reserved |

### 1.8 Index and leaf records

Leaf nodes (type −1, level 1) hold the tree's data records in ascending key order, within a node and from node to
node along the `ndFLink` chain, which starts at `bthFNode` and ends at `bthLNode`.

Index nodes (type 0, level 2 and up) hold pointer records: a key and a `u32` child node number, the key being the
first key in that child. In an HFS tree the keys in index nodes are stored at the maximum length: the key length byte
is `bthKeyLen`, the key is padded with zeros to that length, and the child node number follows at
`(bthKeyLen + 2)` rounded down to even [Code: Mac OS 9.0 ROM]. A catalog index key is therefore 38 bytes (key length
37) and an extents index key 8 bytes (key length 7), each followed by the `u32` child; the initializer sets
`bthKeyLen` to 37 and 7 [Code: System 7.1 File Manager]. Only leaf keys have their actual length (§1.9).

How the BTree manager changes a tree [Doc: Apple's hfs sources, `BTreeTreeOps.c`, `BTreeAllocate.c`, `BTree.c`: the
manager written for Mac OS 8.1's File Manager, which serves HFS and HFS Plus; not traced in Mac OS 9's own code]:

- **Insert into a full node.** In order: the record is inserted at its place; if it does not fit, records are rotated
  into the left sibling (when there is one); if that fails, the node is split to the left. There is no right rotation
  and no right split. The second of two parent inserts skips the rotation.
- **Rotation balances by bytes.** Each record's size counts its offset slot (key, its length byte and data, padded to
  even, + 2). Records move from the front of the right node, the new one counted at its place, while the left node's
  bytes are fewer than the right's, undoing the last move if it overfills the left; if the right node still overflows,
  the rotation fails and a split follows. The new record lands on the side its index falls on.
- **A split** takes a new node as the full node's left sibling (linked before it, the first leaf if it now is; kind and
  height copied) and rotates into it, so about half the bytes move left.
- **The new node** is the first free one by the node map: the map records in order (the header node's, then the map
  nodes), the first 16-bit word that is not `$FFFF`, its highest clear bit.
- **A root split** takes another free node as the new root (index, depth + 1) with two records: the new left node's
  first key and the old node's, at the index key length (§1.8).
- **First keys.** When a node's first record changes, its parent's index record for it is deleted and inserted again
  with the new key, which may itself rotate or split upward; a split also inserts a record for the new left node.
- **Delete.** A node left empty is unlinked from its siblings (`bthFNode`/`bthLNode` moved if it was an end leaf),
  zeroed, freed in the map (`bthFree` + 1), and its parent's record for it deleted, up the tree. An emptied root leaves
  an empty tree (root 0, depth 0); a root left with one record is replaced by its child, depth − 1, as long as that
  holds. Siblings are never merged or rebalanced.
- **Growth.** Before an insert, replace or delete (of a node's first record), when `bthFree` < depth + 1, the tree file
  is extended to at least (`bthNNodes` + depth + 1 − `bthFree`) nodes (one more when the map is too small) through the
  file system's set-EOF routine, whose clump and contiguity are the File Manager's (not traced); every node of the new
  length counts. New map nodes go at the old end, one after another, chained by forward links from the last map node,
  their own bits set; each is a map node (kind 2, height 0) of one record, offsets 14 and nodeSize − 6 (a record of
  nodeSize − 20 bytes), and its backward link stays 0. Mac OS 9.0 extending a full 6,000-node catalog did exactly this
  (node 6000, backward link 0), grew the file by one clump (64 KB), and rewrote the catalog's extents in both the MDB
  and the alternate MDB [Verified: Mac OS 9.0]. Readers take a map record's length from its offsets. A tree file never
  shrinks. ClassicMac's writer adds map nodes so, and does not read their backward links (Disk First Aid's map check
  does not either [Code: Disk First Aid 8.5.5]).

ClassicMac's writer edits the catalog by these rules (§5.5).

### 1.9 Catalog records

The catalog file (CNID 4) has one record for each folder and file, and a thread record for each folder and for each
file that needs one. Its length and first extents are in the MDB (`drCTFlSize`, `drCTExtRec`); more extents may be in
the extents overflow file under file ID 4.

The catalog key:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | `ckrKeyLen` | The key's length, not counting this byte (below) |
| +$01 | 1 | `ckrResrv1` | Reserved, 0 |
| +$02 | 4 | `ckrParID` | The parent folder's CNID; for a thread, the CNID the thread is for |
| +$06 | 1 + n | `ckrCName` | The name, `Str31`; empty for a thread |

In a leaf, `ckrKeyLen` is `6 + n` as the Mac OS File Manager writes it (a thread's key is 6, the Finder's `Desktop DB`
key 16), or `7 + n` for an even-length name when the alignment byte is counted, as hfsutils writes it. Either way the
data starts at the next even offset [Verified: volumes Mac OS wrote, in Basilisk II]. Names are at most 31 bytes.

Every catalog data record starts with a type byte (`cdrType`) and a reserved byte (`cdrResrv2`):

| `cdrType` | Record | Length |
| --- | --- | --- |
| 1 | Folder (`cdrDirRec`) | 70 |
| 2 | File (`cdrFilRec`) | 102 |
| 3 | Folder thread (`cdrThdRec`) | 46; hfsutils writes 15 + the name's length (§1.9) |
| 4 | File thread (`cdrFThdRec`) | 46; hfsutils writes 15 + the name's length (§1.9) |

Folder record:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | `cdrType` | 1 |
| +$01 | 1 | `cdrResrv2` | Reserved |
| +$02 | 2 | `dirFlags` | Folder flags |
| +$04 | 2 | `dirVal` | Valence: files and folders directly inside |
| +$06 | 4 | `dirDirID` | The folder's CNID |
| +$0A | 4 | `dirCrDat` | When it was created |
| +$0E | 4 | `dirMdDat` | When it was last modified |
| +$12 | 4 | `dirBkDat` | When it was last backed up |
| +$16 | 16 | `dirUsrInfo` | `DInfo`: window rectangle, flags, location, view [Doc: Inside Macintosh: Macintosh Toolbox Essentials] ([finder-windows.md §1.1](finder-windows.md#11-folder-finder-information-dinfo-dxinfo)) |
| +$26 | 16 | `dirFndrInfo` | `DXInfo`: scroll position, open chain, script, flags, comment, put-away folder [Doc: Inside Macintosh: Macintosh Toolbox Essentials] |
| +$36 | 16 | `dirResrv` | Reserved, 4 × u32 |

The root folder's record has the key (1, volume name) and `dirDirID` 2.

File record:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | `cdrType` | 2 |
| +$01 | 1 | `cdrResrv2` | Reserved |
| +$02 | 1 | `filFlags` | Bit 0 locked, bit 1 a file thread exists [Doc: TN1150], bit 7 record in use |
| +$03 | 1 | `filTyp` | File type (version), 0 |
| +$04 | 16 | `filUsrWds` | `FInfo`: type, creator, Finder flags, icon location, window [Doc: Inside Macintosh: Macintosh Toolbox Essentials] |
| +$14 | 4 | `filFlNum` | The file's CNID |
| +$18 | 2 | `filStBlk` | Data fork's first allocation block |
| +$1A | 4 | `filLgLen` | Data fork's logical length in bytes |
| +$1E | 4 | `filPyLen` | Data fork's physical length |
| +$22 | 2 | `filRStBlk` | Resource fork's first allocation block |
| +$24 | 4 | `filRLgLen` | Resource fork's logical length |
| +$28 | 4 | `filRPyLen` | Resource fork's physical length |
| +$2C | 4 | `filCrDat` | When the file was created |
| +$30 | 4 | `filMdDat` | When it was last modified |
| +$34 | 4 | `filBkDat` | When it was last backed up |
| +$38 | 16 | `filFndrInfo` | `FXInfo` [Doc: Inside Macintosh: Macintosh Toolbox Essentials] |
| +$48 | 2 | `filClpSize` | The file's clump size |
| +$4A | 12 | `filExtRec` | Data fork's first three extents |
| +$56 | 12 | `filRExtRec` | Resource fork's first three extents |
| +$62 | 4 | `filResrv` | Reserved |

Mac OS writes `filStBlk`, `filRStBlk` and `filResrv` as 0 (every Mac-made file record seen). hfsutils leaves the first
blocks in `filStBlk` and `filRStBlk`; Disk First Aid 8.5 reports such a record as "Reserved fields in the catalog record
have incorrect data, *CNID*, *n*" (the first number is the file's CNID), and its Repair clears both fields and nothing
else in the record; `fsck_hfs` reports the same [Verified]. An edit session's first change clears them (and
`filResrv`), listed as a `repair` change [ClassicMac]; an hfsutils floppy edited so passes Disk First Aid 8.5 and
`fsck_hfs` [Verified].

Thread record. A thread lets the File Manager find a folder (or file) from its CNID alone; its key is (the CNID, empty
name), so it sorts first among the records with that parent ID (§1.11). Every folder has a thread; a file has one only
when something asked for it (a file ID reference).

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | `cdrType` | 3 (folder) or 4 (file) |
| +$01 | 1 | `cdrResrv2` | Reserved |
| +$02 | 8 | `thdResrv` | Reserved, 2 × u32 |
| +$0A | 4 | `thdParID` | The parent's CNID |
| +$0E | 32 | `thdCName` | The folder's or file's name, `Str31` |

A thread record (type 3 or 4) is 46 bytes as Mac OS writes it: the type, a reserved byte, 8 reserved bytes, `thdParID`
at +10 and the name at +14 as a `Str31`. hfsutils writes a thread only as long as its name, 15 bytes and the name's
length (padded to even). Mac OS 9's File Manager reads and keeps such threads: a volume hfsutils made, holding them, was
mounted and used under Mac OS 9, which added its own 46-byte threads beside them [Verified]. Disk First Aid 8.5 rejects
them ("Invalid thread record length, *CNID*, *n*", the first number the thread's own CNID), stops before checking the volume's counts, and cannot repair them [Verified]. ClassicMac reads either, and its writer
accepts either (a renamed item's thread written at 46 bytes, only `thdParID` changed when an item moves). An edit
session's first change to such a volume writes every short thread at 46 bytes, listed as a `repair` change, so the
saved volume passes Disk First Aid [ClassicMac]. `fsck_hfs` reports the short form as "Reserved fields in the catalog record have incorrect data" (§7).

### 1.10 Extents overflow records

The extents overflow file (CNID 3) holds the extents a fork cannot fit in its first extent record. Its length and
extents are in the MDB (`drXTFlSize`, `drXTExtRec`). Each leaf record is an 8-byte key and one extent record:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | `xkrKeyLen` | 7 |
| +$01 | 1 | `xkrFkType` | `$00` data fork, `$FF` resource fork |
| +$02 | 4 | `xkrFNum` | The file's CNID |
| +$06 | 2 | `xkrFABN` | The fork's allocation block (counted from the fork's start) that the record's first extent holds |
| +$08 | 12 | extents | The next three extents (§1.4) |

So a fork whose first record covers 30 blocks continues in the record keyed with `xkrFABN` 30, and so on.

### 1.11 Key order

Keys are kept in ascending order. Names compare as the File Manager compares all names: uppercase and lowercase letters
are equal, letters with diacritical marks differ from those without. The exact order is that of the File Manager's
compare routines, the same in the Mac OS 9.0 ROM as in System 7.1 [Code: Mac OS 9.0 ROM].

**Catalog keys** [Code: Mac OS 9.0 ROM]:

1. The parent ID, unsigned 32-bit.
2. For equal parent IDs, the names, with plain `_RelString`: case-insensitive, diacritical-sensitive, each name taking
   its own length byte (not `ckrKeyLen`).

`_RelString` works on weights, from its tables in the ROM, the same bytes as in System 7.1 [Code: Mac OS 9.0 ROM]
[Doc: Inside Macintosh: Text, `RelString`]:

1. Each byte `c` weighs `w(c) = CmpTab[UpperTab[c]]`, a `u16` whose high byte is the base letter and low byte the
   modifier. `UpperTab` folds case; `CmpTab` gives the order.
2. The names are compared position by position over the shorter length; the first unequal weight decides (unsigned).
3. If all are equal, the shorter name sorts first (`AB` < `ABC`); equal lengths are equal. So the empty name of a
   thread record sorts before every other key with the same parent ID.

Consequences of the tables [Code: Mac OS 9.0 ROM]:

- An accent decides at its own position, not as a tie-break at the end: `É` weighs `$4502`, between `E` (`$4500`) and
  `F` (`$4600`), so `Éa` sorts after `Ez` and before `F`.
- Within a letter the modifiers run: plain `$00` < acute `$02` < grave `$04` < circumflex `$06` < umlaut `$08` <
  tilde `$0A` < ring `$0C` < slash `$0E` < cedilla `$10` < under `$12` < ligature `$14`. After them come the lowercase
  accented letters that `UpperTab` does not fold, at `$80` + the same modifier (`á` = `$4182`, `è` = `$4584`).
- Case folding covers `a`–`z` and only these accented letters: `äÄ åÅ çÇ éÉ ñÑ öÖ üÜ àÀ ãÃ õÕ æÆ øØ œŒ`. So `á` and
  `Á` differ (and likewise the other accented letters not in the list): two names in one folder can differ in the case
  of such a letter.
- The uppercase accented letters `$E5`–`$F4` (`Â Ê Á Ë È Í Î Ï Ì Ó Ô Ò Ú Û Ù`) and `Ÿ` (`$D9`) weigh their own code
  times 256: they sort by code, after `~` and after every letter.
- `` ` `` (`$60`) folds to `$61` and weighs `$4180`: it sorts as a variant of A.
- `ß` weighs `$5382`, between S and T; `ÿ` weighs `$5988`.
- `$CA` (non-breaking space) weighs `$2000`, equal to a space.
- Curly quotes and guillemets sort as variants of the straight quotes: `“ ”` are `"` + `$02`/`$04`, `« »` are `"` +
  `$06`/`$08`, `‘ ’` are `'` + `$02`/`$04`.
- The feminine and masculine ordinals (`ª`, `º`) have weights of their own in the table, which a comparer must take
  from it.

**Extents keys** [Code: Mac OS 9.0 ROM]: the file ID (unsigned 32-bit), then the fork type (`$00` data before `$FF`
resource), then the starting block (unsigned 16-bit).

## 2. Reading

### 2.1 Finding the volume

A volume starts with two logical blocks of boot blocks; its identifying header is at byte 1024 (logical block 2)
[Doc: Inside Macintosh: Files; Inside Macintosh II]. The signature word there says what it is:

| Word at +$400 | Volume | Specified in |
| --- | --- | --- |
| `$D2D7` | MFS [Doc: Inside Macintosh II] | [mfs.md](mfs.md) |
| `$4244` (`'BD'`) | HFS [Doc: Inside Macintosh: Files] | this document |
| `$482B` (`'H+'`) | HFS Plus [Doc: TN1150] | [hfs-plus.md](hfs-plus.md) |
| `$4858` (`'HX'`) | HFSX [Doc: TN1150]; not a volume Mac OS 9.0 knows [Code: Mac OS 9.0 ROM] | [hfs-plus.md](hfs-plus.md) |

A whole disk (a hard disk, a CD) instead starts with a driver descriptor map in logical block 0 and a partition map
from block 1 ([partition-map.md](partition-map.md)); each volume sits inside a partition.

How Mac OS 9.0 mounts a drive:

1. The File Manager's `MountVol` reads the block at byte 1024 and tries HFS [Code: Mac OS 9.0 File Manager]. The
   ROM's `MountVol` knows only `'BD'` and `$D2D7`; its own answer for any other signature is `noMacDskErr` (−57).
   `'H+'` is handled by code in the System file [Code: Mac OS 9.0 ROM and System].
2. Only when that fails is the drive passed to the external file systems (Foreign File Access and its plug-ins) [Code:
   Mac OS 9.0 File Manager]. The ISO 9660 plug-in answers `extFSErr` (−58) for a volume that is not ISO 9660 [Code:
   ISO 9660 File Access 5.3].
3. If no external file system takes it, the volume does not mount [Code: Mac OS 9.0 File Manager].

So a hybrid CD whose partition map holds an HFS volume and which also carries ISO 9660 descriptors mounts as HFS; the
ISO 9660 view is never used [Code: Mac OS 9.0 File Manager and CD-ROM driver] ([iso9660.md §2.11](iso9660.md#211-hybrid-discs)).

### 2.2 Reading a volume

1. Read the MDB at byte 1024 (§1.3). If `drEmbedSigWord` is `'H+'`, the volume is an HFS wrapper around an HFS Plus
   volume; read that instead ([hfs-plus.md §1.2](hfs-plus.md#12-the-hfs-wrapper)).
2. Read the extents overflow file through `drXTExtRec`, cut to `drXTFlSize` (§2.5). Collect its leaf records (§2.3)
   by (fork type, CNID), each group ordered by `xkrFABN`.
3. Read the catalog file through `drCTExtRec` and any overflow records for CNID 4, cut to `drCTFlSize`.
4. Walk the catalog's leaf records (§2.3). Keep each folder record's `dirDirID`, its key's parent ID and its key's
   name; keep each file record with its key. Threads repeat what the folder records give and can be passed over.
5. For each file, build its folder path (§2.4) and read both forks (§2.5) through `filExtRec`/`filRExtRec` and the
   overflow records for its CNID with fork type `$00` or `$FF`. `filStBlk`, `filRStBlk` and the physical lengths are
   not needed.

### 2.3 Walking the B-trees

A reader that wants every record needs no index node and no key comparison: start at `bthFNode` and follow `ndFLink`
until 0, reading each leaf's records in order. Each leaf's `ndBLink` names the leaf before it, and the last leaf
reached is `bthLNode`; the leaves hold `bthNRecs` records in all.

To find one key [Doc: Inside Macintosh: Files]:

1. Start at `bthRoot`.
2. In an index node, take the last record whose key is less than or equal to the key sought (§1.11) and go to its
   child. If there is none, the key is not in the tree.
3. In the leaf, look for an equal key.

### 2.4 Folder paths

A file's path is found by going up from its key's parent ID: each folder's record gives its name and its own parent,
until the root folder (CNID 2) or its parent (CNID 1). A Mac path joins the names with `:` [Doc: Inside Macintosh:
Files].

### 2.5 Reading a fork

A fork is the concatenation of its extents' allocation blocks in order: first the three in its first extent record,
then the overflow records in `xkrFABN` order, cut to the logical length. The physical length and any blocks past the
logical length are ignored. A logical length of 0 is an empty fork [Doc: Inside Macintosh: Files]. Descriptors with a
count of 0 are unused.

### 2.6 The alternate MDB

The alternate MDB is at the volume's second-to-last logical block (§1.2): for a volume that fills its image, at
`image length − 1024`. It is a recovery copy and may be stale (§1.3), so a reader takes everything from the MDB at 1024
and uses the alternate only to repair a damaged primary.

Disk Copy treats block N − 2 as part of the volume's structure: making a read-only or compressed image, it stores the
blocks up to the first allocation block, the used allocation blocks and block N − 2 as data, and everything else as
zeros [Code: Disk Copy 6.3.3] [Verified: images Disk Copy 6.1.2 made in SheepShaver, Mac OS 9.0].

### 2.7 Volume counts

The MDB's counts let a reader check it saw the whole catalog:

- `drFilCnt` is the number of files and `drDirCnt` the number of folders on the volume [Doc: Inside Macintosh:
  Files]. `drNmFls` and `drNmRtDirs` count only the root folder's contents.
- `drDirCnt` does not count the root folder [Doc: TN1150, for the HFS Plus field that replaces it] [Code: Mac OS 9.0
  ROM]: mounting a volume that was not unmounted cleanly, `MountVol` finds the root folder's record, counts the folder
  records after it and rewrites `drDirCnt` if it differs.
- The System 7.1 initializer leaves `drDirCnt` 0, and only folders made or deleted through the File Manager change it
  [Code: System 7.1 File Manager].
- A wrong `drNmFls` is not damage to Mac OS 9.0: Disk First Aid 8.5 verifies a volume whose `drNmFls` is one too many
  as "appears to be OK", and the File Manager keeps the value when it rewrites the MDB [Verified]. The writer does not
  check it, and changes it by each file it adds to or removes from the root, as the File Manager does [ClassicMac].
  Disk First Aid's Repair, run on a volume with other damage, does set it to the root's file count [Verified].

## 3. Writing

What a writer that changes a volume must keep consistent. Each rule follows from the structures of §1 [Doc: Inside
Macintosh: Files] unless tagged; the order in which a fork grows (item 2) and the refresh of the alternate MDB (item 7)
are [ClassicMac].

1. **Allocation.** Every allocation block a fork or B-tree uses is marked in the volume bitmap, and `drFreeBks` is the
   number of clear bits. Blocks released are cleared and `drFreeBks` raised.
2. **Fork extents.** `filLgLen`/`filRLgLen` is the logical length and `filPyLen`/`filRPyLen` the allocated bytes. A
   fork grows first by extending its last extent over immediately following free blocks, then by new extents in the
   unused descriptors of its last extent record, then by new extents overflow records keyed (§1.10) with the number of
   blocks the extents before them cover. A fork shrinks by releasing trailing blocks; a fully released descriptor is
   zeroed and an empty overflow record removed.
3. **The extents overflow file never overflows.** It has only the three extents in `drXTExtRec`; Apple's
   `ExtendFileC` returns `fxOvFlErr` when that first extent record is full [Code: Apple fsck_hfs `SExtents.c`].
4. **B-trees.** After inserting or deleting records, the leaf chain is in key order (§1.11), every index key is its
   child's first key at the maximum length (§1.8), sibling links run both ways, and `bthRoot`, `bthDepth`, `bthFNode`,
   `bthLNode`, `bthNRecs`, `bthNNodes`, `bthFree` and the node map (with its map nodes) agree with the nodes. A tree
   that needs more nodes takes free allocation blocks into its file: the MDB's extents and length for that file change.
5. **Catalog records.** A new folder has a folder record and a folder thread; a file thread exists only when
   `filFlags` bit 1 says so, and goes with its file. The parent folder's `dirVal`, and `drFilCnt`, `drDirCnt`,
   `drNmFls`, `drNmRtDirs` in the MDB, follow every file and folder added or removed. Names in one folder are unique
   under `_RelString` (§1.11). A name is at most 31 bytes. A renamed item's record moves to its new key in the same
   folder, keeping its CNID, and its thread record (a folder's, or a file's when it has one) takes the new name; the
   counts do not change. A moved item (PBCatMove) keeps its name and CNID: its record moves to a key with the new
   folder's CNID as the parent, its thread's `thdParID` becomes that CNID, the old folder's `dirVal` goes down by one
   and the new one's up by one, and `drNmFls` or `drNmRtDirs` change when it leaves or enters the root. A folder cannot
   move into itself or a folder inside it (`badMovErr`), nor onto a name the destination holds (`dupFNErr`)
   [Doc: Inside Macintosh: Files, PBCatMove]. A file is locked by `filFlags` bit 0, as PBHSetFLock sets it; an HFS
   folder has no lock [Doc: Inside Macintosh: Files, PBHSetFLock]. A blessed System Folder's ID is `drFndrInfo[0]`
   (§1.3); ClassicMac blesses only a folder that holds a file of type `zsys` (the System file the boot code opens)
   [ClassicMac]. A file's Finder info is its record's `filUsrWds` and `filFndrInfo`; a folder's flags are
   `dirUsrInfo.frFlags`.
6. **Dates and counters.** The changed file's `filMdDat` and the MDB's `drLsMod` are the write time, as local time;
   `drWrCnt` goes up. A new CNID comes from `drNxtCNID`.
7. **The alternate MDB.** When the MDB's extents for a B-tree change, the copy at block N − 2 is refreshed too.
8. **Locks.** A file with `filFlags` bit 0 set, and a volume with `drAtrb` bit 15 set, are not changed.

### 3.1 A new volume

Mac OS 9.0's HFS initializer, in the File Manager's HFS format code, computes the layout for a volume of N 512-byte
sectors when its caller gives no values of its own [Code: Mac OS 9.0 ptch −20217 0x2C12]. Volumes Mac OS 9.0 initialized
in SheepShaver (Finder's Initialize, Mac OS Standard) at 800 KB, 1.4 MB, 20 MB, 100 MB, 500 MB and 2 GB match these rules
byte for byte: the alternate MDB (which keeps the initializer's state; mounting rewrites only the primary), the extents
header node and the layout fields of the primary MDB; the boot blocks are zero [Verified]. Below, a is the allocation block
size in sectors.

| Part | Rule | Source |
| --- | --- | --- |
| Sectors 0 to `drAlBlSt` + both B-trees | Zero (no boot blocks); the rest of the disk is not written | [Code: 0x1D254] |
| `drAlBlkSiz` | ((N >> 16) + 1) × 512, plus 512 when that is a multiple of 65,536. Not the smallest size that fits: 32 MB gets 1,024, 64 MB 1,536 | [Code: 0x2C22] |
| `drVBMSt`, bitmap | Sector 3; ⌈⌊N ÷ a⌋ ÷ 4096⌉ sectors | [Code] |
| `drAlBlSt` | 3 + the bitmap's sectors | [Code] |
| `drNmAlBlks` | ⌊(N − `drAlBlSt` − 2) ÷ a⌋: the alternate MDB and the last sector outside | [Code] |
| Extents and catalog files | Each min(N ÷ 128, 2,048) sectors rounded down to whole allocation blocks (one block when a block is 1 MB or more; four when N ≤ 128); the extents file at allocation block 0, the catalog after it; the size is also `drXTClpSiz`, `drCTClpSiz` and each header node's clump size (`+$2E`) | [Code: 0x2F7A] |
| `drClpSiz` | 4 allocation blocks, or 1 when 4 would exceed 1 MB | [Code] |
| B-trees | 512-byte nodes, `bthKeyLen` 7 and 37 (§1.8), type and attributes 0; the extents tree empty (free nodes: all but the header); the catalog one leaf (type `$FF`, height 1) holding the root folder (key length ((n + 2) & ~1) + 5 for an n-byte name, parent 1; `dirDirID` 2, `dirVal` 0, created and modified now) and its thread (key length 6, parent 2, no name; parent 1 and the volume's name) | [Code: 0x1D254] |
| MDB | `drAtrb` `$0100`, `drCrDate` and `drLsMod` now (local time), `drWrCnt` 2, `drNxtCNID` 16, `drFilCnt`, `drDirCnt`, `drNmFls`, `drNmRtDirs` 0 (the root is not counted), `drFreeBks` `drNmAlBlks` − both B-trees' blocks, `drVN` the name | [Code] |
| Alternate MDB | Sector N − 2, the MDB's copy | [Code] |

| Size | N | `drAlBlkSiz` | `drAlBlSt` | `drNmAlBlks` | Each B-tree | `drFreeBks` |
| --- | --- | --- | --- | --- | --- | --- |
| 400 KB | 800 | 512 | 4 | 794 | 3,072 | 782 |
| 800 KB | 1,600 | 512 | 4 | 1,594 | 6,144 | 1,570 |
| 1.4 MB | 2,880 | 512 | 4 | 2,874 | 11,264 | 2,830 |
| 20 MB | 40,960 | 512 | 13 | 40,945 | 163,840 | 40,305 |
| 100 MB | 204,800 | 2,048 | 16 | 51,195 | 819,200 | 50,395 |
| 500 MB | 1,024,000 | 8,192 | 19 | 63,998 | 1,048,576 | 63,742 |
| 2 GB | 4,193,280 | 32,768 | 19 | 65,519 | 1,048,576 | 65,455 |

ClassicMac's `Format` returns this layout as an image in memory (400 KB to just under 2 GB); `FormatTo` writes it to a
file made the volume's length, writing only the MDB, bitmap, B-trees and alternate MDB (400 KB to 2 TB).

- Mac OS 9.0 does not offer to initialize an 800-sector (400 KB) disk: its format list clears the HFS bit for the 400K and
  720K entries [Code: 0x27F6], [Verified: no dialog in SheepShaver]. ClassicMac makes 400 KB HFS volumes all the same,
  laid out by the same rules [ClassicMac].
- At the first mount the Finder adds Desktop DB, Desktop DF and DesktopPrinters DB, and the folders Desktop Folder,
  TheVolumeSettingsFolder and Trash, and raises `drNxtCNID` and `drWrCnt`; ClassicMac's new volume is the state before
  that mount, as the initializer leaves it [Verified].
Finder's Initialize uses the default path (the live volumes above).

### 3.2 Growing a volume

A volume grows within its allocation block size; allocation block numbers are counted from `drAlBlSt`, so no extent,
catalog record or B-tree changes [Doc: Inside Macintosh: Files]. For the new size of N' logical blocks:

1. `drNmAlBlks` becomes ⌊(N' − `drAlBlSt` − 2) ÷ (`drAlBlkSiz` ÷ 512)⌋, which must stay at most 65,535.
2. When the sectors from `drVBMSt` to `drAlBlSt` hold fewer bits than that, the allocation area moves up by whole
   sectors and `drAlBlSt` with it, until they hold enough; the new bitmap bits are 0 (free).
3. `drFreeBks` goes up by the blocks added; `drLsMod` and `drWrCnt` change as for any write (§3).
4. The old alternate MDB and the block after it, now inside the allocation area, are zero; the MDB's block is copied to
   N' − 2.

ClassicMac's `Resize` checks the result as it checks a deletion (§5.5), comparing each block in use at its new place.

When the new size needs more than 65,535 blocks of the volume's block size, the volume is laid out again [ClassicMac]:

1. The geometry is what Mac OS 9.0's initializer computes for N' (§3.1): `drAlBlkSiz`, `drVBMSt`, `drAlBlSt`,
   `drNmAlBlks`, `drClpSiz`, the trees' clump sizes, and an empty extents tree at block 0.
2. The catalog keeps its nodes, its records and their order, from the block after the extents tree; it grows with free
   nodes to whole blocks and at least the initializer's catalog size.
3. Each file's forks, in catalog order, take one extent each after the catalog. The file record gets that extent and a
   physical length of whole blocks; nothing else in it changes. No overflow record is needed. A volume with bad blocks
   is refused, and so is an HFS wrapper.
4. The MDB keeps every other field (name, dates, attributes, counts, `drNxtCNID`, Finder info, boot blocks before
   it), with `drFreeBks` and `drAllocPtr` set by the new layout, and is copied to N' − 2.

The result must pass the writer's checks, and every file must read back as the source's, by catalog ID and path.

### 3.3 Shrinking a volume

A volume shrinks within its allocation block size, its allocation area keeping its start (so the bitmap keeps its
sectors). For the new size of N' logical blocks [ClassicMac]:

1. `drNmAlBlks` becomes ⌊(N' − `drAlBlSt` − 2) ÷ (`drAlBlkSiz` ÷ 512)⌋. A size whose blocks are fewer than those in
   use is refused.
2. Every extent that reaches past the new last block moves whole to the first run of free blocks below it that holds
   it, and its descriptor is rewritten where it lies: a file record's `filExtRec` or `filRExtRec`, an overflow
   record, or the MDB's `drXTExtRec` or `drCTExtRec`. Overflow keys hold logical block numbers, so no key changes and
   no extent is split. When no free run holds an extent, the shrink is refused (the volume needs defragmenting);
   bad blocks past the new end are refused too, and so is an HFS wrapper.
3. The bitmap's bits from the new end on are 0, `drFreeBks` is the new count less the blocks in use, `drAllocPtr` goes
   to 0 when it lies past the end, and `drLsMod` and `drWrCnt` change as for any write (§3).
4. The MDB's block is copied to N' − 2.

The result must pass the writer's checks (§5.5), and every file must read back as the source's, both forks byte for
byte. A shrink refused for want of a free run succeeds after a defragmentation (§3.4).

### 3.4 Defragmenting a volume

`Defragment` lays the volume out again in its own size and geometry, as a growth past 65,535 blocks does in a new one
(§3.2) [ClassicMac]:

1. `drAlBlkSiz`, `drVBMSt`, `drAlBlSt`, `drNmAlBlks` and the clump sizes stay. The extents tree keeps its size, but
   empty and in one extent from block 0.
2. The catalog keeps its nodes, records and size, in one extent after the extents tree.
3. Each file's forks, in catalog order, take one extent each after the catalog. Its file record gets that extent and a
   physical length of whole blocks; nothing else in it changes, and no overflow record is left.
4. The bitmap marks the blocks laid out, so the free space is one run at the end; `drFreeBks` stays, `drAllocPtr` is
   the first free block, and `drLsMod` and `drWrCnt` change as for any write. The MDB's other fields and the boot
   blocks stay, and the MDB is copied to N' − 2.

A volume with bad blocks, and an HFS wrapper, are refused. The result must pass the writer's checks, and every file
must read back as the source's. The edit session writes over the volume only the sectors that differ, so a
defragmentation inside a partition or a disk image is saved as any edit is.

## 4. Variants

- **MFS**, the flat file system of the first Macs, is specified in [mfs.md](mfs.md).
- **The HFS wrapper**: an HFS volume whose MDB names an embedded HFS Plus volume through `drEmbedSigWord` and
  `drEmbedExtent` ([hfs-plus.md §1.2](hfs-plus.md#12-the-hfs-wrapper)).
- **HFS Plus and HFSX** have their own volume header ([hfs-plus.md](hfs-plus.md)).
- **Mac OS 9.0, the 68k ROM and System 7.1**: the key comparison tables and the B-tree routines traced are the same in
  all three (§1.11). The Mac OS 9.0 initializer's `drDirCnt` was not traced (§8).

## 5. ClassicMac

One B-tree layout (`BTreeFile`: node descriptors, records by the offset table, the header record, the node map across
the header and map nodes) serves the HFS reader, the HFS Plus reader and the writer; each keeps its own checks and
diagnostics on top. [ClassicMac]

### 5.1 Recognising a volume

- The readers are tried in a fixed order ([unwrapping.md](../containers/unwrapping.md)); for volumes it is: UDIF,
  the Apple partition map, Disk Copy 4.2, NDIF, DART, HFS (and HFS Plus), MFS, the DOS partition table, FAT, raw CD,
  cue sheet, ISO 9660. A file that is none of these is left as a plain file.
- HFS needs at least 1024 + 162 bytes and `'BD'`, `'H+'` or `'HX'` at 1024; `'H+'` and `'HX'` go to the HFS Plus
  reader. MFS needs 1024 + 64 bytes and `$D2D7`. A partition map needs `'ER'` or a zero word at 0 and `'PM'` at 512 or
  2048 ([partition-map.md §2](partition-map.md#2-reading)).
- The boot blocks are not read.

### 5.2 What comes out

- One entry per file record: the name (the raw bytes of the key's name), the folder path from the root down without
  the volume name, the Finder information (`filUsrWds` then `filFndrInfo`, 32 bytes), the creation and modification
  dates (a stored 0 is "no date"), both forks, and `filFlags` bit 0 as `MacFile.IsLocked`.
- `HfsReader.ReadVolumeInfo` (the `IVolumeReader` interface) gives the volume's own dates as a `VolumeInfo`: `drCrDate`,
  `drLsMod` and `drVolBkUp`, a stored 0 as "no date"; its name (`drVN`), `drAlBlkSiz`, `drNmAlBlks`, `drFreeBks`,
  `drFilCnt`, `drDirCnt`, `drAtrb`'s software lock (bit 15) and hardware lock (bit 7), and `drFndrInfo[0]` as the
  blessed folder's ID; for HFS Plus, plain or wrapped, see
  [hfs-plus.md §5.1](hfs-plus.md#51-what-comes-out). The unwrapper keeps them on the `ContainerNode` whose data fork is
  the volume (`ContainerNode.Volume`) [ClassicMac].
- Folder records serve for paths: folders do not come out as entries of `Read`, so an empty folder is not listed.
  Thread records are skipped.
- Each file keeps its catalog IDs (`MacFile.CatalogId`, `filFlNum`, and `ParentId`), and each folder record its
  directory ID (`MacFolder.CatalogId`), for resolving aliases ([aliases.md §2.1](../resources/aliases.md#21-resolving));
  HFS Plus files and folders keep theirs the same way [ClassicMac].
- `HfsReader.ReadFolders` reads the volume the same way, with the same checks and diagnostics, and returns its folder
  records instead (`MacFolder`): the name (the volume name for the root, `IsRoot`), the folder path above it, the
  `DInfo` and `DXInfo` (`FolderFinderInfo`, 32 bytes) and the creation and modification dates. A folder whose parent
  is missing is reported (`hfs.orphan`) and its path starts there. The root also carries the volume's free space
  (`FreeBytes`: `drFreeBks` × `drAlBlkSiz` as the MDB records them). The viewer's folder previews use them
  ([finder-windows.md](finder-windows.md)).
- The volume name is cut to 27 bytes. The MDB fields used are `drNmAlBlks`, `drAlBlkSiz`, `drAlBlSt`, `drVBMSt`,
  `drFreeBks`, `drVN`, `drFilCnt`, `drDirCnt`, the two B-tree files' lengths and extents, and `drEmbedSigWord`.
- Forks are read in place: a fork is a list of byte ranges in the image (`ExtentForkData`), read each time it is
  opened. Nothing is copied when a volume is listed, so a large image costs only its catalog. MFS forks and volumes
  inside partition maps are ranges of the image the same way.
- Each B-tree file is read into memory once, limited by `ContainerReadOptions.MaxExpandedBytesPerInput` (1 GiB by
  default). After `ContainerReadOptions.MaxVolumeEntries` folders and files (1,000,000 by default) the catalog is no
  longer read.

### 5.3 Checks and recovery

ClassicMac reads damaged volumes as far as it can and reports each problem (§6):

- **Bitmap.** The bitmap's bits for the declared allocation blocks are counted and compared with `drFreeBks`; padding
  bits after the last block are ignored. Every descriptor of the extents overflow file, the catalog and each file fork
  is checked against the bitmap, including the blocks a fork holds past its logical length and the extents a
  zero-length fork declares. A bitmap that does not fit in the image turns these checks off.
- **Alternate MDB.** Only its signature at `image length − 1024` is checked; the other fields may be stale (§1.3) and
  are not compared, and the alternate is never used for reading.
- **Header node.** Warned about, with reading going on: a nonzero `ndBLink`, a type other than 1, a nonzero height or
  `ndResv2`, a record count other than 3; record starts other than 14, 120 and 248 (the header, reserved and map
  records); a `bthNodeSize` other than 512 (512 is used anyway); a `bthNNodes` other than the number of whole nodes in
  the fork, or a fork ending in a partial node; a `bthRoot` past the last node, or a root and depth of which one is 0
  and the other not; a `bthKeyLen` other than 37 (catalog) or 7 (extents); a last leaf other than `bthLNode`; a leaf
  record total other than `bthNRecs`. The fork's actual length decides how many nodes are read.
- **Node map.** It must cover every node, through exactly the map nodes needed, each a type-2, height-0 node with one
  record at +$0E; the header node, every map node and every leaf must be marked in use, and the free bits must number
  `bthFree`. A map that fails is reported and ignored. A leaf linked as a map node stops the walk.
- **Leaf walk.** It starts at `bthFNode` and stops, keeping the records read so far, at a link to a node at or past the
  end of the file or already visited, or at a node that is not a level-1 leaf. A leaf whose `ndBLink` is not the leaf
  before it is reported. A B-tree file shorter than one node is empty.
- **Records.** A record's start must be at least 14, its end after its start and before the offset table; a record
  that fails is skipped and the node's other records read. A key that leaves no room for data is skipped silently, as
  is catalog data under 2 bytes.
- **Keys.** A catalog key must be at least 7 bytes, have a zero reserved byte, a name of at most 31 bytes and a length
  of `6 + n`, or `7 + n` for an even `n` (§1.9); others are skipped. An extents key must be 8 bytes with key length 7
  and fork type `$00` or `$FF`; a record that is not exactly 12 bytes of data is reported, used for its first 12 bytes
  when longer and skipped when shorter. Keys out of order or duplicated (§1.11) are reported and their records still
  read; the comparison is the same one the writer uses.
- **Catalog IDs.** Each folder and file CNID must be unique; a duplicate folder does not replace the first one's
  mapping, and duplicate files are all kept. Only the root folder may use an ID below 16 (2); others are reported and
  kept.
- **Paths.** If a parent is missing from the catalog or the chain loops, the path is cut there: the file keeps the
  folders below that point, placed at the root.
- **Forks.** Extents are taken until they cover the logical length. An extent past `drNmAlBlks` empties the whole
  fork, whichever descriptor it is. A fork whose extents cover less than its length, or run past the end of the image,
  is cut. An overflow record whose `xkrFABN` is not the count of blocks before it is reported; the records are still
  used in key order. If the extents overflow file's own extents are unusable, reading goes on without overflow
  extents.
- **Counts.** The files read are compared with `drFilCnt` and the folders other than the root with `drDirCnt`.

### 5.4 Unreadable volumes

The reader throws, and the unwrapper reports `container.unreadable` with the reason, when:

- `drAlBlkSiz` is 0 or not a multiple of 512;
- the catalog file has an extent outside the volume;
- a B-tree file is larger than `MaxExpandedBytesPerInput`.

### 5.5 The writer

- `HfsWriter.ReplaceFork(image, path, fork, bytes)` replaces a data or resource fork; `CreateFile` and `DeleteFile` add
  or remove a file record and both forks; `CreateFolder` and `DeleteFolder` add or remove a folder record and its
  thread; `Rename` renames a file or folder in its folder; `Move` moves one into another folder (§3), and refuses a
  move into the folder it is in, which PBCatMove allows as a no-op [ClassicMac]; `SetFinderInfo` sets a file's Finder info and
  `SetFolderFlags` a folder's Finder flags; `Format` and `FormatTo` make a new, empty volume (§3.1, up to 2 GB in memory or 2 TB to a file, checked
  with `Check` before it is returned); `Resize` grows or shrinks a volume (§3.2, §3.3); `SetLocked` locks or unlocks a file; `Bless` blesses a System Folder; `Delete` removes a file, or a folder (an empty one, or with `recursive`
  everything in it, deepest first). Paths are colon-separated with no empty part; each part is the name's Mac OS Roman text, control characters
  included (a folder's `Icon
`, a name that is only a tab), not `MacFile.MacPath`'s escaped form. Each returns a new
  image; the input is never modified.
  `ForkSaver.SaveHfsImageAs` writes the result to another file through a temporary file and a rename, and refuses the
  source image as its destination. The editor offers it as Save As ▸ HFS Volume Image. `InputEditSession` (in
  `ClassicMac.Files.Editing`) gathers these edits and resource edits on an opened input for the app and the CLI and
  saves them to a new file, or in place only when asked, keeping the original as `.orig`.
- Only plain HFS is written: not partition maps, disk image formats, MFS, HFS Plus or HFS wrappers. A volume with
  `drAtrb` bit 15 set, a locked file, a non-empty folder to delete, a duplicate name, a name over 31 bytes and a
  volume without enough free blocks are refused. Growth of the extents overflow file past its three extents is
  refused (§3).
- Before an edit the writer checks the source and refuses it on any fault: every catalogued extent allocated, in
  bounds and not shared with another fork or the B-tree files; `drFreeBks` equal to the bitmap's free count; catalog
  IDs unique; folder valences and the MDB's file and folder counts equal to the catalog's; both B-trees' header, index
  keys and child pointers, node order and sibling links, node maps, free-node and leaf-record counts, and key order.
  `HfsWriter.Check` runs these checks without an edit, even on a software-locked volume, and returns the first fault
  (the CLI's `check`).
- After an edit the result is checked without reading any fork it kept: it opens as the writer opens a volume; every
  catalog and extents record of a file kept is the source's byte for byte; and every sector written lies in the MDB,
  the alternate MDB, the bitmap, the B-tree files or the edited fork's blocks. A replaced fork's file is checked by its
  own record, which may differ from the source's only in that fork's lengths and extents and the modification date, and
  by its two forks: the replaced one the bytes given, the other as it was [ClassicMac]. Replacing a fork on a 500 MB
  Mac OS 9 volume takes about 80 ms.
- The writer follows §3. A catalog edit is made record by record as the BTree manager makes it (§1.8): the records
  removed, then those changed (in place when they fit, else deleted and inserted), then those added, each in key order
  [ClassicMac order]; rotation into the left sibling, splits to the left into the first free node, parent keys
  deleted and inserted again, emptied nodes unlinked, zeroed and freed, the root collapsing, and the tree's file grown
  first when an operation finds fewer free nodes than the depth + 1. Only the nodes, bitmap sectors and MDB that change
  are written [ClassicMac]. On a volume with no block to grow the file into, and for an empty tree, the writer instead
  rebuilds the tree's leaf and index nodes from the sorted records, each node filled in turn, using the nodes already
  in the tree file first. The extents tree is edited the same way, record by record (a fork's overflow records added,
updated and removed, a deletion's removed one by one) [ClassicMac: the catalog's rules, not checked on Mac OS for this
tree], and rebuilt only when an edit cannot be made in place (an empty tree, too few free nodes). Volumes edited so (a Mac OS-initialized 20 MB volume
  with 100 new folders, catalog depth 1 to 3; a new 20 MB volume with 600, depth 4) mount in Mac OS 9.0, pass Disk
  First Aid 8.5, take the Finder's own copies and deletions, and pass it again; so does the 600-folder volume with 300
  folders deleted (emptied leaves freed, depth kept: no merging) and with all 600 deleted (the root collapsed to one
  leaf, which Mac OS then grew again) [Verified: Mac OS 9.0, Disk First Aid 8.5, `fsck_hfs`]. It
  extends a tree file with free blocks when needed, through the MDB's extents and, for the catalog, overflow records;
  it adds linked map nodes when the header node's map is full. The bytes past a fork's end in its last block are
  zeroed. New catalog keys count the alignment byte in `ckrKeyLen`, as hfsutils does (§1.9). `CreateFile` and
  `CreateFolder` take optional creation and modification dates, both the local time of the call when omitted.
- Names are compared with the `_RelString` weights of §1.11, generated as 256 weights, for catalog order, duplicate
  names and every part of a path.
- After the edit, the writer reopens the result with the reader and validates both B-trees again (index graph, links,
  node maps, counts, key order). For a fork replaced, it compares the file's metadata and both its forks, and checks
  every other file without reading its forks: the result passes the checks made before an edit, every other catalog
  record and extents overflow record is byte for byte the source's, and every sector the edit wrote lies in the MDB,
  the alternate MDB, the bitmap, the B-tree files or the edited fork's blocks. Any structural diagnostic or difference
  fails the operation.
- Edits are made on an `HfsVolume`: the volume as read (from the file, a partition, a Disk Copy disk or a decoded NDIF
  disk) and the 512-byte sectors written over it, so an edit holds only what it changes. The edit session saves a
  volume by copying the input to a temporary file beside the destination, writing the changed sectors into it (a Disk
  Copy 4.2 image's data checksum made again), reading it back (the checks made before an edit, every changed sector
  compared) and moving it into place. Save in place keeps the original as `.orig` the first time and refuses an input
  another program has open. A resized volume is held and saved whole.
- A deletion (`Delete`, `DeleteFile`) removes a file, or a folder with everything below it, in one pass: every fork's
  blocks (overflow extents included) are freed and their overflow records removed, the records and threads removed,
  and the counts changed once. It is then checked without reading any fork: the result passes the checks made before
  an edit, every catalog record kept is byte for byte the source's (the parent's `dirVal` aside), and every allocated
  block outside the catalog and extents files is the source's. A tree holding a locked file is refused, naming it.

### 5.6 First Aid

`HfsFirstAid.Verify` checks an HFS volume as Disk First Aid 8.5.5 (Mac OS 9.0) does: in its stages and order, naming
each problem by Disk First Aid's number and words, and ending with its verdict [Code: Disk First Aid 8.5.5]. It does
not read the volume as the reader does (§2): Disk First Aid builds its view from the alternate MDB.

**Problems.** Each is printed as Disk First Aid prints it, `Problem:  <text>, <n2>, <n3>`; the text is its problem
list's entry (numbers 1–71, error code −(499 + n)). A problem either ends the check, after which Disk First Aid cannot
repair the volume, or records a repair and lets the check go on; which is decided by the check that finds it, not by
the number. The verdict is the first that applies:

| Verdict | Line | When |
| --- | --- | --- |
| Not HFS | "This is not an HFS disk." | The alternate MDB is not an HFS one |
| Cannot repair | "Test done. Problems were found, but Disk First Aid cannot repair them." | A check ended at a problem |
| Needs repair | "The volume “name” needs to be repaired." | Problems were found, all repairable |
| Appears OK | "The volume “name” appears to be OK." | Nothing was found |

An HFS Plus volume, bare or in its HFS wrapper, is checked by its own stages ([hfs-plus.md §5.4](hfs-plus.md#54-first-aid));
HFSX is checked with it [ClassicMac]. Each problem also has a code,
`firstaid.` and its text in lowercase words joined by hyphens (`firstaid.invalid-peof`; the long texts of #48, #66,
#70 and #71 are `firstaid.folder-nesting`, `firstaid.volume-header-version`, `firstaid.first-allocation-block` and
`firstaid.first-catalog-extent`), and MountCheck's lines are `firstaid.mountcheck-serious` and
`firstaid.mountcheck-minor`. `check` prints them ([cli.md §2.7](../../cli.md#27-check)).

**Stage 1, "Checking disk volume."** (IVChk):

1. With S the volume's size in 512-byte sectors, read sector S − 2, the alternate MDB (§2.6). `'H+'`, or `'BD'` with
   `drEmbedSigWord` `'H+'`, is HFS Plus; anything other than `'BD'` is "not an HFS disk", even when the primary MDB is
   good [Verified: Mac OS 9.0 mounts such a volume; Disk First Aid refuses it].
2. Show "Checking "Mac OS Standard" volume structures." and check the alternate MDB's geometry, with V = S − 2,
   A = `drAlBlkSiz`, N = `drNmAlBlks` and B = (V ÷ (A ÷ 512) + 4095) >> 12 bitmap sectors. The first that fails ends
   the check:

| # | Text | Fails when |
| --- | --- | --- |
| 7 | Invalid allocation block size | A is not a multiple of 512, is over $7FFFFE00, or is under 512·k for the smallest k with V ÷ k ≤ 65,535 |
| 8 | Invalid number of allocation blocks | N > (V − 3 − B) ÷ (A ÷ 512) |
| 9 | Invalid VBM start block | `drVBMSt` ≤ 2 |
| 10 | Invalid allocation block start | `drVBMSt` + B > `drAlBlSt` (read as a signed word) |

**B-tree files** (no line of their own). Each tree file is read through the alternate MDB's extents; its node count is
its PEOF ÷ node size, not `bthNNodes`. Each of these ends the check (%2 = the file's CNID):

| # | Text | Fails when |
| --- | --- | --- |
| 47 | Invalid extent file PEOF | The block count of `drXTExtRec`'s extents (to the first empty one) × A ≠ `drXTFlSize` |
| 46 | Invalid catalog PEOF | The block count of `drCTExtRec`'s extents and the catalog's overflow extents (fork $00, file 4) × A ≠ `drCTFlSize` [Verified] |
| 61 | Invalid BTree node size | `bthNodeSize` is not 512, 1,024 … 32,768, or node 0's first record offset is under 14, odd or past the node |

A nonzero reserved header byte (`bthResv` byte 6, node 0 + $32) is cleared by repair, with no problem printed.

**"Checking for locked volume name."** The catalog's first leaf record is the root folder's; if there is none, #56
"Catalog file entry not found for extent" ends the check (the text does not fit the condition). Its key's name is the
volume's name for the later MDB compare. A root folder with `frFlags` bit $1000 (name locked) is #55 "Directory name
locked", repaired by clearing the bit [Verified]; its %2 is 4, the catalog's file ID, which the stage sets as the record
ID and never replaces, and %3 the leaf node [Code: CODE 1 $1D4B2] [Verified: "4, 2", "4, 5"].

**"Checking extent BTree."**, **"Checking extent file."**, **"Checking catalog BTree."** Each tree is checked so (the
header node, then a depth-first walk from the root; %3 = the node):

1. Header node: unreadable → #22 "Invalid node structure"; not of kind 1 or not 3 records → #28 "Invalid header node";
   height not 0 → #5 "Invalid node height"; header record not 106 bytes → #13 "Invalid BTH length"; depth over 8 →
   #29 "Exceeded maximum BTree depth"; root past the last node → #15 "Invalid root node number"; exactly one of root
   and depth 0 → #29. An empty tree (both 0) is not walked.
2. Walk, each node once, every one of these ending the check: a node reached twice → #23 "Overlapped node
   allocation"; no records and no links → #22; a key longer than 7 (extents) or 37 (catalog) → #25 "Invalid key
   length"; a key not greater than the one before it in its node (§1.11) → #26 "Keys out of order"; links that do not
   chain each level's nodes in walk order → #21 "Invalid sibling link"; a first child pointer of 0 or past the last
   node → #20 "Invalid index link"; an extents leaf record whose extent starts or counts at or past `drNmAlBlks`, or
   has blocks after an empty extent → #11 "Invalid extent entry" (a start plus count past the end is not checked).
3. Walk, these flagged for a rebuild of the tree and the walk going on: a kind other than index (0) or leaf (−1) →
   #16 "Invalid node type"; a height other than the depth less the level, plus 1 → #5; a node's first key other than
   its parent's index key → #19 "Invalid index key"; a later child pointer of 0 or past the last node → #20; a level
   past 8 → #29.
4. Map: the header's map record and then each map node along `ndFLink` must cover the nodes exactly; a map node past
   the last node → #22, reached twice → #23, not a one-record kind 2 node → #27 "Invalid map node", a chain too short
   or too long → #24 "Invalid map node linkage" (all ending the check); a map node's height other than 0 → #5. A map
   that marks other nodes than the walk reached is rewritten by repair, with no problem printed.
5. The header record's first 30 bytes (depth, root, leaf records, first and last leaf, node size, maximum key length,
   total and free nodes) against the walk's → #54 "Invalid BTree Header", rewritten by repair [Verified: `bthNRecs`
   + 1].

**"Checking catalog file."** The catalog's leaf records in key order (%2 = the record's CNID, %3 = its node). No
thread record for the root, key (2, ""), → #35 "Missing thread record for root dir". A record type (its first word)
other than $0100, $0200, $0300 or $0400 → #31 "Invalid catalog record type". Then by type ("ends" = ends the check;
"repair" = recorded for repair, the scan going on):

| Record | # | Text | When | |
| --- | --- | --- | --- | --- |
| Thread | 33 | Invalid thread record length | Not 46 bytes | ends |
| Thread | 38 | Invalid key for thread record | Its key has a name | ends |
| Thread | 39 | Invalid parent CName in thread record | `thdCName` not 1–31 bytes | ends |
| Thread | 64 | Reserved fields in the catalog record have incorrect data | `thdResrv` not zero | repair |
| Thread | 65 | Invalid file or directory ID found | Its CNID 0 or 3–15 | ends |
| Folder thread | 37 | Missing directory record | No record at (`thdParID`, `thdCName`) | repair (the folder is made) |
| Folder thread | 37 | | The record there is not a folder | ends |
| File thread | 6 | Missing file record for file thread | No record there, or a file whose `filFlags` bit 1 is clear | repair (the thread is deleted) |
| File thread | 6 | | The record there is not a file | ends |
| Folder | 32 | Invalid directory record length | Not 70 bytes | ends |
| Folder, file | 36 | Missing thread record | Its parent ID is not the CNID of the thread before it | ends |
| Folder | 64 | (as above) | `dirFlags` not zero | repair |
| Folder | 65 | (as above) | `dirDirID` 0 or 3–15 | ends |
| Folder | 57 | Custom icon missing | `frFlags` bit $0400 and no file "Icon\r" in it | repair (bit cleared) [Verified] |
| File | 34 | Invalid file record length | Not 102 bytes | ends |
| File | 64 | (as above) | `filFlags` bits 2–6, `filStBlk`, `filRStBlk` or `filResrv` not zero | repair [Verified] |
| File | 65 | (as above) | `filFlNum` 0 or 3–15 | ends |
| File | 1 | Invalid PEOF | A fork's blocks × A < its PEOF (a PEOF short of them is not this check's) | ends [Verified] |
| File | 2 | Invalid LEOF | A fork's LEOF > its PEOF (signed) | ends [Verified] |

Each record's CNID raises the next free CNID (at least 16). A fork's blocks are its catalog extents and the overflow
records from (fork, file, the blocks so far) on while file and fork match, each record through #11. Folder and file
records without a thread are not named: unpaired counts only make repair write the missing threads. Disk First Aid
never prints #50 "File thread flag not set in file rec".

Then MountCheck's view, one line with no numbers:

- **"MountCheck found serious errors"**: the extents leave fewer free blocks than the bitmap's clear bits (a used block
  marked free) [Verified].
- **"MountCheck found minor errors"**, otherwise, for any of: files plus folders (the root aside) other than the sum of
  the valences; more folder threads than folders or more file threads than files with the thread flag; fewer (missing
  threads); a fork whose blocks × A is not its PEOF (the catalog is then rebuilt); more free blocks by the extents than
  the bitmap shows (leaked blocks) [Verified: C2, C4, C6, D1]. `drFreeBks` itself is not compared [Verified].

**"Checking catalog hierarchy."** A depth-first walk from the root (the key (1, "") must not exist, else #31):

- a folder nested more than 100 deep → #48 "Nesting of folders has exceeded the recommended limit of 100 …"; the rest
  of the hierarchy is not checked, and no repair is recorded, so on its own the volume appears to be OK;
- a folder that is its own ancestor → #41 "Loop in directory hierarchy", ending the check;
- a folder's `dirVal` other than the records under it (its thread aside) → #3 "Invalid directory valence", repaired to
  the count [Verified];
- at the end, the folders and files reached against the scan's counts: #42 "Invalid root directory count", #43
  "Invalid root file count", #44 "Invalid volume directory count", #45 "Invalid volume file count" (%2 the walk's,
  %3 the scan's), repaired; these arise only for records the walk cannot reach. A wrong `drFilCnt`, `drDirCnt` or
  `drNmRtDirs` is not these but #58 below.

**"Checking volume bit map."** A bitmap is built from every extent walked (the B-tree files' and every fork's); a
block claimed twice is #12 "Overlapped extent allocation" (once, repaired by giving the files their own copies). It is
compared with the volume's bitmap whole sector by whole sector, so bits after `drNmAlBlks` and bytes after the bitmap
in its last sector must be clear: any difference is #60 "Volume Bit Map needs minor repair", repaired by writing the
built bitmap [Verified: a leaked block, a trailing byte, the bit after the last block]. %2 and %3 are 4 and 0: the
record ID the catalog stage left, and node 0 [Code: CODE 1 $1A0CC] [Verified].

**"Checking volume info."** The primary MDB is compared with one built so: the signature, creation date, `drVBMSt`,
`drNmAlBlks`, `drAlBlSt`, the cache sizes ($7C–$81) and the B-tree files' sizes and extents from the alternate MDB;
`drAtrb` kept unless a bit 0–6 is set ($0100 then); `drClpSiz` the primary's if it is a multiple of A within
(N ÷ 4) × A, else the alternate's, else 4 blocks, and one block if over 1 MB; `drXTClpSiz` and `drCTClpSiz` the
primary's if a multiple of A within (N ÷ 4) × A, else the alternate's, else the file's first extent; `drNxtCNID` the
highest CNID + 1, or the primary's when it is above that by at most 4,096; `drVN` the root folder's name; the counts
from the scan; the rest the primary's. The first group that differs is #58 "Master Directory Block needs minor
repair" with %2 its detail, repaired by writing the built MDB:

| Detail | Fields |
| --- | --- |
| 1 | `drSigWord`, `drCrDate`, `drLsMod`, `drAtrb`, `drVBMSt`, `drNmAlBlks`, `drClpSiz`, `drAlBlSt`, `drNxtCNID`, `drVN` |
| 2 | $40–$81: `drVolBkUp` … `drCtlCSize`, the counts `drNmRtDirs`, `drFilCnt`, `drDirCnt` among them |
| 3 | `drXTFlSize` |
| 4 | `drXTExtRec` |
| 5 | `drCTFlSize` |
| 6 | `drCTExtRec` |

`drNmFls`, `drAllocPtr`, `drAlBlkSiz` and `drFreeBks` are not compared [Verified: `drNmFls` + 1 and `drFreeBks` − 1
appear to be OK].

#### Repair

`HfsFirstAid.Repair` (the CLI's `repair`, the app's Volume ▸ First Aid…) verifies, and repairs only a volume that needs repair; one that cannot be
repaired, or is not HFS, is left as it is. The repairs run in Disk First Aid's order, then the volume is
verified again, at most three passes in all:

1. **The B-trees written again** from their leaf records, when a B-tree stage flagged the tree (#5, #13, #15, #16,
   #19–#29, #54, the map, the reserved header byte) or a repair below changes its records: the header's node count
   from the file's PEOF, its node size and maximum key length Disk First Aid's (7, 37), nodes packed full, the map
   from the nodes in use (a header node whose map record is unreadable is laid out again first). The catalog's
   records are written with Disk First Aid's repair list made:
   - reserved fields cleared (#64) [Verified: RC1, `filStBlk` 5 → 0];
   - the root's name lock (#55) [Verified: RC7] and a custom-icon flag without an `Icon\r` file (#57) cleared;
   - a file thread whose file is missing deleted, and a file's thread flag set when its thread is found (#6);
   - a folder whose thread is left made again from it (#37), empty until counted;
   - a thread made for every folder and every file whose thread flag is set (MountCheck's missing threads);
   - every folder's valence set to the folders and files in it (#3) [Verified: RC6, 3 → 2].
   The extents tree is written without the records of files (ID 16 and up) the catalog does not hold (MountCheck's
   orphaned extents).
2. **The bitmap** the verify after step 1 builds, when the volume's differs (#12 aside, #60) [Verified: RD1].
3. **The MDB** that verify computes (#58) [Verified: RB6], with `drNmFls` and `drFreeBks` set as well (beyond
   Disk First Aid does not compare them).

Overlapping extents (#12) are repaired before step 1: each fork that shares blocks with one found before it (the
B-tree files first, then the catalog's files in key order) is copied whole into free blocks, in one run when one is
long enough, and its new extents written with the trees (its overflow records made again); with too few free blocks it
is left. Which fork moves, and where, is ClassicMac's choice. Each fix is a
`PlannedChange` (`repair`, detail `catalog, CNID n: …`, `extents, file n: …`, `file n: its data fork … given its
own copy`, `catalog B-tree written again (n
records)`, `volume bitmap …`, `master directory block …`).

#### Beyond Disk First Aid

First Aid is Disk First Aid's checks and repairs with more of its own, in the same report and verdict (problems with
number 0, printed `Problem:  <text>.`). They are ClassicMac's rules, not Disk First Aid's or Mac OS's:

| Code | Found | Repair |
| --- | --- | --- |
| `firstaid.alternate-mdb-missing` | the alternate MDB is not `BD` while the primary is (Disk First Aid: not an HFS disk); the volume is checked by the primary | the primary copied to sector S − 2, first |
| `firstaid.alternate-mdb-stale` | the alternate's `drVBMSt`, `drNmAlBlks`, `drAlBlkSiz`, `drAlBlSt` or B-tree files' sizes and extents differ from the primary's, and the primary is sound (its geometry passes, the extents file's extents are its size and the catalog's at most its size); the volume is checked by the primary, which Mac OS mounts by. When the primary is not sound, the alternate is used and #58 rewrites the primary | as above |
| `firstaid.extent-start` | an overflow extents record after a fork's first has a start block (`xkrFABN`) other than the fork's blocks before it | renumbered from the first record's, the extents tree written again |
| `firstaid.orphaned-extents` | overflow extents records of a file (ID 16 and up) not in the catalog | the records deleted (repair step 1), their blocks freed |
| `firstaid.short-peof` | a fork's physical length short of its blocks (Disk First Aid: MountCheck's minor errors only) | set to its blocks |
| `firstaid.mdb-counts` | `drNmFls` other than the root's files, or `drFreeBks` other than the bitmap's clear bits (Disk First Aid ignores both) | the MDB written (repair step 3) |
| `firstaid.extent-past-end` | an extent running past `drNmAlBlks` (Disk First Aid checks only its start and count) | none: the volume cannot be repaired |

### 5.7 Fragmentation

`HfsReader.ReadFragmentation` tells how a volume's files and free space lie, for telling whether a defragmentation
(§3.4) is worth it or why a shrink (§3.3) finds no room [ClassicMac]:

- Each file record's two forks are counted in extents: the descriptors with blocks among its record's three, and among
  its overflow records' (§1.8). A fork in more than one extent is fragmented, and so is a file with such a fork; the
  most extents of one fork is given too.
- The bitmap's first `drNmAlBlks` bits give the runs of free blocks and the longest.

The volume is opened as the writer opens it (§5.5); HFS Plus, a wrapper, and a volume the writer refuses give none. The
CLI's `stat` shows it for a volume, and the app's Volume card with the block size, size, free space and counts.

## 6. Diagnostics

"Not traced" means the Mac's behaviour in that case has not been followed in its code. The HFS wrapper's
`hfs.wrapper-extent-unallocated` and the `hfs.plus-*` codes are in [hfs-plus.md §6](hfs-plus.md#6-diagnostics).

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `hfs.alternate-mdb` | Warning | The alternate MDB at `image length − 1024` is missing or lacks `'BD'` | Reads from the primary MDB | Not traced |
| `hfs.bad-btree-header` | Warning | A B-tree header node has a bad descriptor or record boundaries, root and depth, node count, node size, maximum key length or leaf totals, or the fork ends in a partial node (§5.3) | Reads on | Not traced |
| `hfs.bad-btree-map` | Warning | The node map does not cover the tree, its map nodes are malformed, cyclic or marked free, a used node is marked free, a leaf is linked as a map node, or the free count differs from `bthFree` | Reads on; a leaf linked as a map node stops the walk | Not traced |
| `hfs.bad-link` | Error; Warning for a backward link | A forward leaf link leaves the B-tree or returns to a node already read (Error); a leaf's `ndBLink` is not the leaf before it (Warning) | Stops the walk and keeps the records read (Error); reads on (Warning) | Not traced |
| `hfs.bad-record` | Warning | A catalog key is malformed, a record type is unknown, or a folder or file record is too short | Skips the record | Not traced |
| `hfs.bad-record-offset` | Error | A record's offsets in its node are impossible | Skips the record | Not traced |
| `hfs.bitmap-truncated` | Warning | The bitmap bytes for all declared allocation blocks (of the volume, or of an HFS wrapper) do not fit in the image | Reads without bitmap checks | Not traced |
| `hfs.counts` | Info | The files or folders read differ from `drFilCnt` / `drDirCnt` | Reports only | Not traced |
| `hfs.duplicate-id` | Warning | A catalog file or folder CNID appears more than once | Keeps every file; keeps the first folder's mapping | Not traced |
| `hfs.extent-outside` | Error | An extent lies past `drNmAlBlks` | Empty fork; for the catalog, the volume is unreadable (§5.4) | Not traced |
| `hfs.extent-unallocated` | Warning | An extent of the catalog, the extents overflow file or a file fork includes a block marked free | Reads the fork | Not traced |
| `hfs.fork-short` | Error | A fork's extents cover less than its logical length | Cuts the fork | Not traced |
| `hfs.free-blocks` | Info | The bitmap's free-block count differs from `drFreeBks` | Reports only | Not traced |
| `hfs.image-truncated` | Error | The image ends inside a fork | Cuts the fork | — |
| `hfs.key-order` | Warning | Catalog or extents overflow leaf keys are duplicated or out of order | Keeps the records | Not traced |
| `hfs.not-leaf` | Error | A node on the leaf chain is not a level-1 leaf | Stops the walk; keeps the records read | Not traced |
| `hfs.orphan` | Warning | A file's parent folder is missing, or the parents loop | Cuts the path there | Not traced |
| `hfs.overflow-record` | Warning | An extents overflow key or extent record has the wrong size | Skips short records; uses the first 12 bytes of long ones | Not traced |
| `hfs.overflow-start` | Warning | An overflow key's `xkrFABN` differs from the blocks covered by the extents before it | Uses the records in key order | Not traced |
| `hfs.reserved-id` | Warning | A catalog file or folder other than the root uses a CNID below 16 | Keeps the record | Not traced |
| `hfs.too-many-entries` | Error | More than `MaxVolumeEntries` folders and files | Stops reading the catalog | No such limit |

## 7. Verification

- `tests/ClassicMac.Files.Tests/HfsTests.cs` builds volumes with `HfsBuilder.cs`: files with folders, Finder
  information, dates and both forks; forks through overflow records; and one test for each damage case of §5.3 and
  each diagnostic of §6 (B-tree headers, node maps and map nodes, links, keys, IDs, bitmap and alternate MDB checks,
  extents outside the volume, a truncated image, counts). `Folders_come_out_with_their_window_and_icon_info` and
  `A_folder_whose_parent_is_missing_is_reported_and_its_path_starts_there` cover `ReadFolders`, with `DInfo`,
  `DXInfo` and dates `HfsBuilder` writes into the root's and a folder's records. `A_classic_hfs_catalog_with_mac_os_key_lengths_that_leave_out_the_alignment_byte_reads_cleanly`
  covers the `6 + n` key lengths of §1.9.
- `HfsTests.Corpus_disk_images_read_cleanly` reads every disk image under `CLASSICMAC_CORPUS` (not in the repository)
  and requires no Error and no `hfs.counts`: Disk Copy 6 images of volumes Mac OS wrote in Basilisk II, whose
  `6 + n` catalog keys established §1.9 [Verified].
- `HfsWriterFeatureTests.cs`, `HfsCatalogWriterFeatureTests.cs` and `HfsSaveAsFeatureTests.cs` cover the writer:
  fork growth and shrinking through overflow records and tree splits, extents tree growth with the alternate MDB,
  catalog growth with map nodes and overflow extents, creating and deleting files and folders, the refusals of §5.5,
  and `_RelString` order for accented names. `HfsItemEditTests.cs` covers renaming files and folders (their threads
  and contents following), Finder info and folder flags, and deleting a folder with its contents;
  `A_volume_with_Mac_OS_s_fixed_length_index_keys_is_edited_and_keeps_them` edits a catalog whose index keys are at
  the maximum length, as Mac OS writes them (§1.8; `HfsBuilder.FixedIndexKeys`), and checks the rebuilt index keeps it;
  `HfsCatalogInPlaceTests.cs` covers §1.8's edits: a record into a leaf with room writing that leaf, a full leaf
  rotating into its left sibling, a full leaf with a full left sibling splitting to the left into the first free node,
  each file deleted writing its leaves and index (fixed and variable index keys), an emptied leaf zeroed and freed,
  the root collapsing, 600 folders on a new 20 MB volume (depth 1 to 3 or more), and a tree with no free node growing
  its file first;
  `HfsMoveTests.cs` covers moves of files and folders (threads, valences, root counts) and the refusals;
  `HfsFormatTests.cs` covers new volumes from 400 KB to 500 MB against §3.1's table (block sizes, bitmap, B-tree
  sizes and clumps, `drWrCnt`, the alternate MDB, the root folder), files and catalog growth on one, and the sizes and
  names refused; volumes of 400 KB to 100 MB were compared byte for byte with the traced initializer's output and
  differ only in their dates;
  `HfsResizeTests.cs` covers growing with room in the bitmap and with the allocation area moved up, and the sizes
  refused; a copy of the 500 MB Mac OS 9 volume grew to 510 MB and passed `check`;
  `HfsLockBlessTests.cs` covers locking, unlocking and blessing and their refusals;
  `HfsCheckTests.cs` covers `HfsWriter.Check` on sound, damaged, truncated and locked volumes;
  `InputEditSessionTests.cs` the edit session's volume and single-file edits, Save As and Save In Place.
- With `CLASSICMAC_HFS_INTEROP_INPUT` and the `…_OUTPUT` variables set, `ExternalClassicHfsImageCanBeEditedAndReopened`
  and `ExternalVolumeCatalogMutationsReopen` edit a real hfsutils-formatted volume; the outputs were remounted with
  hfsutils, for fork edits, folder changes and catalog growth.
- `HfsCorpusWriteTests.cs`, with `CLASSICMAC_CORPUS` set: copies of real volumes Mac OS wrote (plain, partitioned, Disk
  Copy 4.2, NDIF) get a folder, a file with both forks, renames, a move and deletions, are saved, and must pass the
  writer's checks and read back with every change; with `CLASSICMAC_HFSUTILS` set too, `fsck_hfs` must find nothing the
  source lacked. On the 500 MB Mac OS 9 boot volume it passes.
- `fsck_hfs` 540.1 (hfsprogs) reports any extent that uses a volume's last allocation block as an "Invalid extent entry",
  with a wrong block count for its file and orphaned blocks. Mac OS 9 allocates that block itself (a StuffIt 7.0.3
  installer's folder icon on the Mac OS 9 volume ends there), and a ClassicMac volume filled to its last block draws the
  same report while one filled to the block before passes; so these findings are `fsck_hfs`'s, and the interop tests
  compare its findings before and after an edit rather than require none [Verified].
- A file was deleted from a Mac OS 9 boot volume (an image SheepShaver used) and the output listed again.
- `HfsUtilsInteropTests.cs`, with `CLASSICMAC_HFSUTILS` set (hfsutils' folder, or `docker:<image>` built from
  `tools/hfsutils/Dockerfile`): a volume ClassicMac formats and edits is mounted, listed and copied from by hfsutils
  and checked by hfsprogs' `fsck.hfs -n` (Apple's `fsck_hfs` 540.1); new volumes (400 KB, 100 MB), a catalog grown to
  index levels and then deleted in one pass, and a volume grown past its bitmap's sector then locked, renamed, moved and
  blessed all pass `fsck_hfs` with nothing found; a volume `hformat` makes is checked, read and edited by ClassicMac,
  then read back by hfsutils, and the edit adds nothing to what `fsck_hfs` finds. (`hcopy`'s own file threads draw
  "Reserved fields in the catalog record have incorrect data" from `fsck_hfs` before ClassicMac touches them.)
- The 256 `_RelString` weights the writer generates were compared with Apple's `gCompareTable` with no differences
  (the table is not in the repository).
- Disk Copy's block N − 2 rule (§2.6) was checked on images Disk Copy 6.1.2 made in SheepShaver, Mac OS 9.0.

## 8. Not covered

- Boot blocks; Hot Files; the HFS wrapper's software-lock bit and bad-block file; journal replay and `fsck_hfs`
  repair.
- Writing partition maps, disk image formats, MFS or HFS Plus.
- Open: the Mac OS 9.0 initializer's `drDirCnt` was not traced; the System 7.1 one leaves it 0 (§2.7).
- Shrinking a volume by splitting extents that no free run holds (§3.3); resizing an image past 2 GB, which is made in
  memory.
- Open: whether Finder's Erase (of a volume already initialized) passes the initializer values of its own (§3.1).
- No rule in this document is fitted to data alone.

## 9. References

1. Apple, *Inside Macintosh: Files* (1992), chapter 2 "File Manager", "Data Organization on Volumes": HFS volumes,
   the master directory block, B-trees, the catalog and extents overflow files.
2. Apple, *Inside Macintosh II* (1985), "The File Manager": MFS volumes.
3. Apple, *Inside Macintosh: Text*, Text Utilities: `RelString` and the sort order for names.
4. Apple, *Inside Macintosh: Macintosh Toolbox Essentials*, the Finder Interface chapter: `FInfo`, `FXInfo`, `DInfo`,
   `DXInfo`.
5. Apple, Technical Note TN1150, *HFS Plus Volume Format*: the HFS wrapper, the volume attribute bits HFS and HFS Plus
   share, the reserved CNIDs.
6. The Mac OS 9.0 ROM (File Manager, B-tree manager, boot loaders) and the System 7.1 File Manager, traced in
   disassembly; Apple CD/DVD Driver 1.3.1; ISO 9660 File Access 5.3.
7. Disk Copy 6.3.3, traced in disassembly.
8. Apple, `hfs` (`fsck_hfs`, `lib_fsck_hfs/dfalib/SExtents.c`),
   <https://github.com/apple-oss-distributions/hfs>; Apple Public Source License 2.0.
9. Robert Leslie, hfsutils (and the JotaRandom/hfsutils fork); GPL-2.0. Behaviour only: the `7 + n` key length, and
   the volumes used to check the writer.
