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
| +$02 | 4 | `drCrDate` | When the volume was created |
| +$06 | 4 | `drLsMod` | When it was last modified |
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
| +$40 | 4 | `drVolBkUp` | When the volume was last backed up |
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
| 3 | Folder thread (`cdrThdRec`) | 46 |
| 4 | File thread (`cdrFThdRec`) | 46 |

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
   under `_RelString` (§1.11). A name is at most 31 bytes.
6. **Dates and counters.** The changed file's `filMdDat` and the MDB's `drLsMod` are the write time, as local time;
   `drWrCnt` goes up. A new CNID comes from `drNxtCNID`.
7. **The alternate MDB.** When the MDB's extents for a B-tree change, the copy at block N − 2 is refreshed too.
8. **Locks.** A file with `filFlags` bit 0 set, and a volume with `drAtrb` bit 15 set, are not changed.

## 4. Variants

- **MFS**, the flat file system of the first Macs, is specified in [mfs.md](mfs.md).
- **The HFS wrapper**: an HFS volume whose MDB names an embedded HFS Plus volume through `drEmbedSigWord` and
  `drEmbedExtent` ([hfs-plus.md §1.2](hfs-plus.md#12-the-hfs-wrapper)).
- **HFS Plus and HFSX** have their own volume header ([hfs-plus.md](hfs-plus.md)).
- **Mac OS 9.0, the 68k ROM and System 7.1**: the key comparison tables and the B-tree routines traced are the same in
  all three (§1.11). The Mac OS 9.0 initializer's `drDirCnt` was not traced (§8).

## 5. ClassicMac

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
  dates (a stored 0 is "no date") and both forks.
- Folder records serve for paths: folders do not come out as entries of `Read`, so an empty folder is not listed.
  Thread records are skipped.
- `HfsReader.ReadFolders` reads the volume the same way, with the same checks and diagnostics, and returns its folder
  records instead (`MacFolder`): the name (the volume name for the root, `IsRoot`), the folder path above it, the
  `DInfo` and `DXInfo` (`FolderFinderInfo`, 32 bytes) and the creation and modification dates. A folder whose parent
  is missing is reported (`hfs.orphan`) and its path starts there. The viewer's folder previews use them
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
  thread. Paths are colon-separated with no empty part. Each returns a new image; the input is never modified.
  `ForkSaver.SaveHfsImageAs` writes the result to another file through a temporary file and a rename, and refuses the
  source image as its destination. The editor offers it as Save As ▸ HFS Volume Image.
- Only plain HFS is written: not partition maps, disk image formats, MFS, HFS Plus or HFS wrappers. A volume with
  `drAtrb` bit 15 set, a locked file, a non-empty folder to delete, a duplicate name, a name over 31 bytes and a
  volume without enough free blocks are refused. Growth of the extents overflow file past its three extents is
  refused (§3).
- Before an edit the writer checks the source and refuses it on any fault: every catalogued extent allocated, in
  bounds and not shared with another fork or the B-tree files; `drFreeBks` equal to the bitmap's free count; catalog
  IDs unique; folder valences and the MDB's file and folder counts equal to the catalog's; both B-trees' header, index
  keys and child pointers, node order and sibling links, node maps, free-node and leaf-record counts, and key order.
- The writer follows §3. It rebuilds a changed B-tree's leaf and index nodes from the sorted records (including
  first-key changes and trees becoming empty or non-empty), using the nodes already in the tree file first. It
  extends a tree file with free blocks when needed, through the MDB's extents and, for the catalog, overflow records;
  it adds linked map nodes when the header node's map is full. The bytes past a fork's end in its last block are
  zeroed. New catalog keys count the alignment byte in `ckrKeyLen`, as hfsutils does (§1.9). `CreateFile` and
  `CreateFolder` take optional creation and modification dates, both the local time of the call when omitted.
- Names are compared with the `_RelString` weights of §1.11, generated as 256 weights, for catalog order, duplicate
  names and every part of a path.
- After the edit, the writer reopens the result with the reader and compares the file list, Finder information, dates,
  paths, the changed forks and both forks of every other file, and validates both B-trees again (index graph, links,
  node maps, counts, key order). Any structural diagnostic or difference fails the operation.

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
  and `_RelString` order for accented names.
- With `CLASSICMAC_HFS_INTEROP_INPUT` and the `…_OUTPUT` variables set, `ExternalClassicHfsImageCanBeEditedAndReopened`
  and `ExternalVolumeCatalogMutationsReopen` edit a real hfsutils-formatted volume; the outputs were remounted with
  hfsutils, for fork edits, folder changes and catalog growth.
- The 256 `_RelString` weights the writer generates were compared with Apple's `gCompareTable` with no differences
  (the table is not in the repository).
- Disk Copy's block N − 2 rule (§2.6) was checked on images Disk Copy 6.1.2 made in SheepShaver, Mac OS 9.0.

## 8. Not covered

- Boot blocks; Hot Files; the HFS wrapper's software-lock bit and bad-block file; journal replay and `fsck_hfs`
  repair.
- Writing partition maps, disk image formats, MFS or HFS Plus.
- Open: the Mac OS 9.0 initializer's `drDirCnt` was not traced; the System 7.1 one leaves it 0 (§2.7).
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
