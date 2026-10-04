# HFS Plus

HFS Plus (Mac OS 8.1 and later) replaced HFS with 32-bit allocation blocks, Unicode names and B-trees of larger nodes;
HFSX (Mac OS X 10.3 and later) is the variant with case-sensitive names. A volume has a 512-byte volume header at byte
1024 and five special files: the allocation file (a bitmap), the extents overflow, catalog and attributes B-trees, and
the startup file [Doc: TN1150]. Volumes made for older Macs wrap an HFS Plus volume in an HFS volume. ClassicMac reads
both formats, bare and wrapped, checks the structures it needs to list files against TN1150 and Apple's `fsck_hfs`,
and lists every visible file with its path, Finder information, dates and both forks, resolving hard links. It does not
write them.

| | |
| --- | --- |
| Identified by | `$482B` (`'H+'`), version 4, or `$4858` (`'HX'`), version 5, at byte 1024; an HFS MDB whose `drEmbedSigWord` is `'H+'` (§1.2); an `Apple_HFS` partition |
| ClassicMac | Reads: `ClassicMac.Files.Hfs` (`HfsPlusReader`, through `HfsReader`) |
| Verified against | A journaled Mac OS X HFS Plus image from Digital Corpora's `nps-2009-hfsjtest1` corpus |
| Sources | TN1150; Apple's `hfs` source (`hfs_format.h`, `hfs_xattr.c`, `fsck_hfs`) and XNU's `kauth.h`; the Mac OS 9.0 ROM and System (disassembly); the Unicode Standard |

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

Every table in this section is [Doc: TN1150] unless a row says otherwise. Units are as in
[hfs.md §1.1](hfs.md#11-units); HFS Plus allocation blocks are numbered from 0 from the start of the volume.

### 1.1 The volume header

512 bytes at byte 1024. A copy, the alternate volume header, is at 1,024 bytes before the end of the volume; it may
lie in a trailing partial allocation block when the volume's length is not a multiple of the block size.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 2 | `signature` | `'H+'` or `'HX'` |
| +$002 | 2 | `version` | 4 for HFS Plus, 5 for HFSX |
| +$004 | 4 | `attributes` | Volume attributes (below) |
| +$008 | 4 | `lastMountedVersion` | |
| +$00C | 4 | `journalInfoBlock` | Allocation block of the JournalInfoBlock (§1.6) |
| +$010 | 4 | `createDate` | When the volume was created, in local time (unlike every other HFS Plus date) [Doc: TN1150] |
| +$014 | 4 | `modifyDate` | When it was last modified, in UTC [Doc: TN1150] |
| +$018 | 4 | `backupDate` | When it was last backed up, in UTC [Doc: TN1150] |
| +$01C | 4 | `checkedDate` | |
| +$020 | 4 | `fileCount` | File records in the catalog |
| +$024 | 4 | `folderCount` | Folder records, the root not counted |
| +$028 | 4 | `blockSize` | Allocation block size, a power of two of 512 or more |
| +$02C | 4 | `totalBlocks` | |
| +$030 | 4 | `freeBlocks` | |
| +$034 | 4 | `nextAllocation` | |
| +$038 | 4 | `rsrcClumpSize` | |
| +$03C | 4 | `dataClumpSize` | |
| +$040 | 4 | `nextCatalogID` | Next unused CNID |
| +$044 | 4 | `writeCount` | |
| +$048 | 8 | `encodingsBitmap` | One bit per text encoding used by a name on the volume (§2.4) |
| +$050 | 32 | `finderInfo` | 8 × u32 |
| +$070 | 80 | `allocationFile` | Fork data (§1.3) of the allocation file, CNID 6 |
| +$0C0 | 80 | `extentsFile` | Extents overflow B-tree, CNID 3 |
| +$110 | 80 | `catalogFile` | Catalog B-tree, CNID 4 |
| +$160 | 80 | `attributesFile` | Attributes B-tree, CNID 8 |
| +$1B0 | 80 | `startupFile` | Startup file, CNID 7 |

Volume attributes:

| Bit | Meaning |
| --- | --- |
| 7 | Locked by hardware |
| 8 | Unmounted cleanly |
| 9 | Spared blocks: bad-block extent records exist (CNID 5) |
| 10 | No cache required |
| 11 | Boot volume inconsistent |
| 12 | Catalog node IDs reused |
| 13 | Journaled |
| 14 | Reserved in TN1150; `kHFSVolumeInconsistentBit` in Apple's later `hfs_format.h` [Code: Apple hfs_format.h] |
| 15 | Locked by software |

The other bits are reserved.

The first 1,536 bytes of the volume (boot blocks and the header) and its last 1,024 bytes (the alternate header and
what follows) are reserved; the allocation blocks that hold them are marked in use and belong to no file.

### 1.2 The HFS wrapper

A wrapper is an HFS volume ([hfs.md](hfs.md)) whose MDB has `drEmbedSigWord` (+$7C) `'H+'` and `drEmbedExtent`
(+$7E start block, +$80 block count). The embedded volume starts at byte
`drAlBlSt × 512 + drEmbedExtent.start × drAlBlkSiz` and is `drEmbedExtent.count × drAlBlkSiz` bytes long; its
allocation blocks are marked in use in the wrapper's bitmap. The wrapper's own catalog holds a placeholder file for
older systems.

### 1.3 Fork data and extents

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 8 | `logicalSize` | Bytes |
| +$08 | 4 | `clumpSize` | |
| +$0C | 4 | `totalBlocks` | Allocation blocks allocated to the fork; may exceed what the logical size needs |
| +$10 | 64 | `extents` | Eight extent descriptors: `startBlock` (u32), `blockCount` (u32) |

The descriptors in use are contiguous from the first; every unused descriptor is all zero. Further extents are in the
extents overflow B-tree, eight per record, keyed:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$0 | 2 | `keyLength` | 10 |
| +$2 | 1 | `forkType` | `$00` data, `$FF` resource |
| +$3 | 1 | `pad` | |
| +$4 | 4 | `fileID` | CNID |
| +$8 | 4 | `startBlock` | The fork's allocation block the record's first extent holds |

The record's data is one 64-byte extent record. Bad blocks are extents of CNID 5's data fork.

### 1.4 B-trees

Nodes are a power of two from 512 to 32,768 bytes, given by the header record; catalog and attributes nodes are at
least 4,096 bytes (`kHFSPlusCatalogMinNodeSize`, and likewise for attributes in [Code: Apple hfs_format.h]). The node
descriptor is HFS's ([hfs.md §1.6](hfs.md#16-b-tree-nodes)); node kinds are −1 leaf, 0 index, 1 header, 2 map. Every
record offset, the free-space offset included, is even.

Node 0 is the header node: height 0, a zero `bLink`, and three records, a 106-byte header record, a 128-byte user
record and the map record. The map is one bit per node, most significant bit first, continued in linked map nodes when
the header's map record is too small.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `treeDepth` | |
| +$02 | 4 | `rootNode` | |
| +$06 | 4 | `leafRecords` | Records in all leaf nodes |
| +$0A | 4 | `firstLeafNode` | |
| +$0E | 4 | `lastLeafNode` | |
| +$12 | 2 | `nodeSize` | |
| +$14 | 2 | `maxKeyLength` | Catalog 516, extents 10, attributes 266 [Code: Apple hfs_format.h `kHFSPlusAttrKeyMaximumLength`] |
| +$16 | 4 | `totalNodes` | |
| +$1A | 4 | `freeNodes` | |
| +$1E | 2 | `reserved1` | |
| +$20 | 4 | `clumpSize` | |
| +$24 | 1 | `btreeType` | 0 for the special files' trees (control file) |
| +$25 | 1 | `keyCompareType` | HFSX catalog: `$CF` case folding, `$BC` binary; reserved elsewhere |
| +$26 | 4 | `attributes` | Bit 1 big keys (16-bit key lengths), bit 2 variable-length index keys |
| +$2A | 64 | `reserved3` | |

Keys have 16-bit lengths. Index keys are variable-length in the catalog and attributes trees and fixed-length in the
extents tree. An empty tree may have zero root and leaf fields.

### 1.5 Catalog records

Catalog key: `keyLength` (u16), `parentID` (u32), `nodeName` (u16 length, then that many UTF-16 big-endian units, at
most 255). The root folder's key has parent ID 1 (`kHFSRootParentID`) and the volume name.

| Record type | Record | Length |
| --- | --- | --- |
| 1 | Folder | 88 |
| 2 | File | 248 |
| 3 | Folder thread | up to 520: type, reserved, `parentID`, `nodeName` |
| 4 | File thread | up to 520 |

File record:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `recordType` | 2 |
| +$02 | 2 | `flags` | Catalog flags (below) |
| +$04 | 4 | `reserved1` | |
| +$08 | 4 | `fileID` | CNID |
| +$0C | 4 | `createDate` | |
| +$10 | 4 | `contentModDate` | |
| +$14 | 4 | `attributeModDate` | |
| +$18 | 4 | `accessDate` | |
| +$1C | 4 | `backupDate` | |
| +$20 | 16 | `permissions` | BSD info: `ownerID` (u32), `groupID` (u32), `adminFlags` (u8), `ownerFlags` (u8), `fileMode` (u16), `special` (u32: inode number, link count or device) |
| +$30 | 16 | `userInfo` | `FInfo` |
| +$40 | 16 | `finderInfo` | `FXInfo` |
| +$50 | 4 | `textEncoding` | The name's encoding hint |
| +$54 | 4 | `reserved2` | |
| +$58 | 80 | `dataFork` | Fork data (§1.3) |
| +$A8 | 80 | `resourceFork` | |

A folder record has the same fields to +$53 with `valence` (u32) at +$04, `folderID` at +$08, `DInfo` (`userInfo`,
+$30) and `DXInfo` (`finderInfo`, +$40) for the Finder fields ([finder-windows.md §1.1](finder-windows.md#11-folder-finder-information-dinfo-dxinfo)),
and at +$54 `folderCount` (u32): the folders it contains, used on HFSX when the has-folder-count
flag is set [Code: Apple hfs_format.h `HFSPlusCatalogFolder`].

Catalog flags: bit 0 file locked, bit 1 thread exists, bit 2 has attributes, bit 3 has security (ACL), bit 4 has folder
count, bit 5 has link chain, bit 6 has child link [Doc: TN1150 for bits 0–1] [Code: Apple hfs_format.h for bits 2–6].

### 1.6 The journal

The JournalInfoBlock, at allocation block `journalInfoBlock` [Doc: TN1150 Journal Info Block]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `flags` | Bit 0 journal in this file system, bit 1 on another device, bit 2 needs initialisation |
| +$04 | 32 | `device_signature` | |
| +$24 | 8 | `offset` | Byte offset of the journal on the volume |
| +$2C | 8 | `size` | Journal size in bytes |
| +$34 | 128 | `reserved` | |

The journal header, at the journal's start [Doc: TN1150 Journal Header]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `magic` | `'JNLx'` |
| +$04 | 4 | `endian` | `$12345678` |
| +$08 | 8 | `start` | Offset of the first transaction in the circular buffer |
| +$10 | 8 | `end` | Offset after the last transaction |
| +$18 | 8 | `size` | The journal's size |
| +$20 | 4 | `blhdr_size` | |
| +$24 | 4 | `checksum` | Over the header sector with this field zero |
| +$28 | 4 | `jhdr_size` | The header's sector size |

The header is invalid until the journal is initialised (the needs-initialisation flag). The root folder holds a
`.journal_info_block` file (one extent at the JournalInfoBlock) and a `.journal` file (one extent covering the
journal).

### 1.7 Attributes records

Attribute key [Code: Apple hfs_format.h `HFSPlusAttrKey`]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$0 | 2 | `keyLength` | |
| +$2 | 2 | `pad` | 0 |
| +$4 | 4 | `fileID` | The CNID the attribute belongs to |
| +$8 | 4 | `startBlock` | 0, or for an extents record the fork block it continues at |
| +$C | 2 | `attrNameLen` | UTF-16 units |
| +$E | 2 × n | `attrName` | Up to 127 units |

| Record type | Record | Length |
| --- | --- | --- |
| `$10` | Inline data: type, 8 reserved bytes, `attrSize` (u32), the data | 16 + `attrSize`, padded to even [Code: Apple hfs_format.h `HFSPlusAttrData`] |
| `$20` | Fork data: type, reserved u32, an 80-byte fork data | 88 |
| `$30` | Extents: type, reserved u32, an extent record | 72 |

Other record types are undefined and must be ignored [Doc: TN1150].

### 1.8 Links

- **File hard links** [Doc: TN1150]: a link is a file with Finder type and creator `hlnk`/`hfs+` whose BSD `special`
  is a nonzero link reference. The target, the indirect node, is the file `iNode<n>` (n in decimal, no leading zeros)
  in the root folder `\0\0\0\0HFS+ Private Data`. The indirect node's `special` holds its link count, which
  traditional Mac OS can leave inaccurate.
- **Directory hard links** [Code: Apple fsck_hfs `dirhardlink.c`]: an alias file with the has-link-chain flag, Finder
  type `alis`, creator `MACS` and Finder's `IsAlias` flag; its target is the folder `dir_<CNID>` in the root folder
  `.HFS+ Private Directory Data\r`, which has the has-link-chain flag. The inode's inline attribute
  `com.apple.system.hfs.firstlink` holds the first alias's CNID; the aliases form a chain through their previous- and
  next-link IDs; the inode's link count is the number of aliases. The private folder has the immutable owner flag and
  the sticky mode bit; every folder above an alias, to the root or the private folder, has the has-child-link flag.
  TN1150's hard-link section predates this representation.
- **Symbolic links** [Doc: TN1150]: a file whose BSD mode is `S_IFLNK` with the required Finder type and creator; its
  data fork is the UTF-8 target path, with no NUL bytes; its resource fork is empty.
- **ACLs** [Code: XNU `kauth.h`]: the inline attribute `com.apple.system.Security` holds a `kauth_filesec`: its magic,
  an entry count of at most 128, a 44-byte header and 24 bytes per ACE; `KAUTH_FILESEC_NOACL` is the 44-byte header
  alone. ACE kinds 1–4 are permit, deny, audit and alarm. The attribute's name is the one Linux's HFS Plus code uses
  [Reference: Linux HFS Plus].

## 2. Reading

The rules below are what a consistent volume satisfies; §5 says what ClassicMac does when one fails.

### 2.1 The volume

1. Read the header at byte 1024; through a wrapper, at byte 1024 of the embedded volume (§1.2). The signature and
   version must agree (`'H+'`/4, `'HX'`/5).
2. The alternate header, 1,024 bytes before the end of the volume, has the same signature and version; it is a
   recovery copy, and reading uses the primary [Doc].
3. `blockSize` is a power of two of 512 or more; `totalBlocks` is not 0, and the allocation blocks fit in the volume.
4. A cleared unmounted-cleanly bit, or a set boot-inconsistent bit (or bit 14), means the volume may be damaged
   [Doc] [Code: Apple hfs_format.h].
5. A set journaled bit means the on-disk structures may lag the journal; a reader that does not replay it reads the
   recorded structures [Doc].

### 2.2 Special files and allocation

1. The volume has an allocation file and an extents overflow B-tree, even an empty one: TN1150 lists both as needed to
   access the volume [Doc]. The attributes B-tree exists when its fork has allocated blocks [Doc].
2. Every fork, special forks included, takes its extents from its eight descriptors, then from the overflow records of
   its CNID and fork type in `startBlock` order. Overflow records exist only after all eight initial descriptors are
   used; every overflow record but the last holds eight extents; each record's `startBlock` is the number of blocks
   before it; and the extents add up to exactly the fork's `totalBlocks`, no record remaining after that [Doc].
3. A fork's logical size fits in its `totalBlocks`. An empty fork has no descriptors and no overflow records [Doc].
4. Every extent lies inside the allocation area and is marked in the allocation bitmap; no two extents overlap
   (TN1150's allocation-file ownership); no extent claims a block holding the reserved first 1,536 or last 1,024 bytes
   [Doc]. Allocated blocks that no known extent describes are allowed [Doc].
5. The bitmap covers `totalBlocks`; its bits past `totalBlocks` are clear; its clear bits number `freeBlocks` [Doc].
6. Bit 9 of the attributes is set exactly when CNID 5 has extent records; bad-block records are keyed to CNID 5's data
   fork [Doc].
7. Every overflow record other than a bad-block record extends a catalog or special-file fork. That orphan records are
   wrong is inferred from TN1150's key semantics: a record's file ID, fork type and `startBlock` identify the fork
   extents it extends.

### 2.3 B-trees

For the extents overflow, catalog and attributes trees [Doc] unless marked:

1. The header node is as §1.4: height 0, zero `bLink`, three records of 106 and 128 bytes and the map;
   `maxKeyLength` as the table gives; the big-keys attribute set and the variable-index-keys attribute as §1.4;
   `btreeType` 0; `totalNodes × nodeSize` equal to the fork's logical size.
2. The map covers every node through exactly as many linked map nodes as needed, each with a valid descriptor and
   record boundaries; the header, index, leaf and map nodes, and no others, are marked allocated; `freeNodes` is the
   number of clear bits; the unused bytes after the last node bit of the last map record are zero [Code: Apple fsck_hfs
   `CmpBTM` in `SVerify2.c`].
3. Every node marked free in the catalog tree is zero-filled [Code: Apple fsck_hfs `BTCheckUnusedNodes`];
   `hfs_format.h` names the volume bit for unused catalog nodes. TN1150 does not apply this to the other trees.
4. From the root, the index graph reaches the leaves with consistent heights and sibling links on every level; each
   index record is exactly its key and a child pointer; each index key equals the first key of its child subtree and
   separates the adjacent children's key ranges [Code: Apple fsck_hfs `BTCheck` in `SVerify2.c`]. An empty root leaf
   is valid when no parent key must name its first record; an empty non-root node is not, since the verifier compares
   each child separator with the child's first record [Code: Apple fsck_hfs `SVerify2.c`].
5. The leaf chain runs from `firstLeafNode` to `lastLeafNode` with matching back links; the leaves hold `leafRecords`
   records in all; leaf keys are unique and in order (§2.5).
6. Extents keys are ordered by file ID, fork type and `startBlock`, across index siblings too. Extents records are 64
   bytes.

### 2.4 The catalog

1. Folder records are 88 bytes, file records 248; thread records are at most 520 bytes and may carry bytes after the
   name [Code: Apple fsck_hfs `CheckCatalogRecord` in `CatalogCheck.c`].
2. A key's length matches its stored name length. File and folder keys have non-empty names; `.` and `..` are illegal
   names [Code: Apple fsck_hfs `CheckCatalogName`].
3. Names, in keys and threads, are fully decomposed in canonical combining-mark order [Doc] and well-formed UTF-16:
   supplementary characters as valid surrogate pairs, no lone surrogates [Doc: Unicode Standard §3.9]. TN1150's
   decomposition excludes U+2000–U+2FFF, U+F900–U+FAFF and U+2F800–U+2FAFF. Mac OS 8.1–10.2 used Unicode 2.1
   decompositions and Mac OS X 10.3 and later 3.2 [Doc]; the volume does not record which. HFSX requires the 3.2 forms
   and the sequences `fsck_hfs`'s `FixDecomps` corrects (below) [Code: Apple fsck_hfs `FixDecomps`, `DecompData.h`].
4. File CNIDs are at least 16; folder CNIDs too, except the root's 2 [Doc] [Code: Apple fsck_hfs `CheckFile`,
   `CheckDirectory`]. CNIDs are unique. Folder records do not set the file-locked or thread-exists flags [Code: Apple
   fsck_hfs `CheckDirectory`].
5. The root's key has parent ID 1. Every other record's `parentID` names an existing folder and every folder's
   ancestry reaches the root. Every file and folder has a thread record (a file sets the thread-exists flag), each
   thread points back to its record's parent and name, and no thread names a missing record [Doc].
6. `nextCatalogID` exceeds every CNID unless the IDs-reused bit is set, and is at least 16 either way [Doc].
7. A folder's `valence` is the number of file and folder records whose parent is that folder [Doc]. On HFSX,
   `folderCount` is the number of folders inside plus directory hard-link aliases, not all children [Code: Apple
   fsck_hfs `CheckFolderCount`]; Apple's verifier sets a missing has-folder-count flag before comparing.
8. `fileCount` is the number of file records and `folderCount` the number of folder records less the root, private
   hard-link inode files and directory inodes included [Doc].
9. `encodingsBitmap` has the bit of every record's `textEncoding`: encodings below 64 use the same-numbered bit,
   MacFarsi (140) bit 49 and MacUkrainian (152) bit 48. Extra bits are allowed, since bits may stay set after the last
   name using an encoding is deleted [Doc].
10. An initialised BSD record (nonzero mode) marks a folder as a directory and a file as a supported node type [Code:
    Apple fsck_hfs `CheckBSDInfo`].
11. A folder's or file's has-attributes flag is set exactly when the attributes tree has a record for its CNID; its
    has-security flag exactly when it has a `com.apple.system.Security` attribute [Code: Apple hfs_format.h].

Name corrections HFSX requires and HFS Plus tolerates: 44 code points whose canonical decomposition changed between
Unicode 2.1 and 3.2 (U+01F8 and U+01F9 among them) [Doc]; the legacy U+0306 + U+0307 sequence; Greek tonos sequences
formed by U+030D after tonos bases or diaeresis; Bengali BA plus nukta (to RA with middle diagonal); Odia YA plus nukta
(to U+0B5F); Gurmukhi DDA plus nukta (to U+0A5C); Thai and Lao vowel sequences (to their AM letters); and two Tibetan
three-character sequences (to U+0F77 and U+0F79) [Code: Apple fsck_hfs `FixDecomps`]. Repeated combining marks of equal
class, such as two U+0307, keep their order; `DecompMakeData.c` defines no correction for them.

### 2.5 Key order

- Catalog keys: parent ID, then name. HFS Plus and HFSX with `keyCompareType` `$CF` use TN1150's `FastUnicodeCompare`:
  Unicode 3.2 simple lowercase mappings, default-ignorable characters skipped, U+0000 after every other character,
  controls and surrogates significant [Doc]. HFSX with `$BC` compares unsigned UTF-16 code units [Doc].
- Attribute keys: file ID, then name length, binary UTF-16 name and start block; the key's pad is zero. TN1150 leaves
  the rule unfinished; Apple's comparator decides it [Code: Apple hfs `hfs_attrkeycompare` in `hfs_xattr.c`].

### 2.6 The attributes tree

1. Inline records match the `HFSPlusAttrData` layout and their declared size, with the tree's even-byte record padding;
   reserved fields are ignored [Doc] [Code: Apple hfs_format.h]. Inline and unknown records own no extents.
2. An extent-backed attribute has one fork-data record at `startBlock` 0 and extension records matched by file ID and
   name, each `startBlock` continuing the extents before it; the fork-data record holds eight extents when extensions
   follow, every extension but the last holds eight, and the total is the fork data's `totalBlocks`, which the logical
   size fits [Doc] [Code: Apple hfs_format.h `HFSPlusAttrForkData`, `HFSPlusAttrExtents`]. Extensions without a
   fork-data record, and gaps, are wrong. Fork-data and extents records take part in the allocation checks (§2.2).
3. Each attribute's file ID names a catalog record or one of the special-file CNIDs 3 to 8 [Doc].

### 2.7 The journal

When the journaled bit is set [Doc: TN1150 Journal Info Block, Journal Header]:

1. `journalInfoBlock` points at an allocated block inside the allocation area.
2. The JournalInfoBlock's flags say the journal is in this file system, and its range lies in the allocation area.
3. The root's `.journal_info_block` is one extent at `journalInfoBlock`; `.journal` is one extent starting at the
   journal's offset, with its size as logical size.
4. Unless the needs-initialisation flag is set, the header's magic, endian marker, sizes and circular-buffer offsets
   are consistent and its checksum, over the full `jhdr_size` sector, matches.

### 2.8 Files and links

1. Resolve each file's path from its parent IDs; read its Finder information, dates and both forks (§2.2).
2. A file hard link (§1.8) shows its own path with the indirect node's forks and file metadata [Doc]. The private
   folders and their contents are not visible files.
3. A directory hard link shows the inode folder's contents at each alias's path, nested folders included [Code: Apple
   fsck_hfs `dirhardlink.c`].
4. An alias with allocated data-fork blocks is wrong [Code: Apple fsck_hfs `CatalogCheck.c`]. A file outside the
   private folders with the link-chain flag but neither link signature is a stray flag [Code: Apple fsck_hfs
   `CatalogCheck.c`]. An ordinary regular file (not a link, not in the private file-link folder, not a journal file)
   with a link count above 1 is inconsistent [Code: Apple fsck_hfs `CatalogCheck.c`].
5. A symbolic link's target is its data fork as UTF-8; targets are paths, not resolved [Doc].

## 3. Writing

Only First Aid's repair writes HFS Plus (§5.4): B-trees written whole from their leaf records by
`HfsPlusBTreeWriter` (the header node, map nodes after it when the header's map record is too short, the leaves packed
full and the index levels above them, each level linked both ways; index keys the child's first key, padded to the
maximum in the extents tree [Doc: TN1150]), the allocation file, and the volume header with its alternate. Files and
folders are not created, deleted or renamed on HFS Plus.

## 4. Variants

- **HFSX** has signature `'HX'`, version 5, and a catalog `keyCompareType` of `$BC` (binary) or `$CF` (case folding)
  (§2.5); it requires the Unicode 3.2 name forms (§2.4) and uses the folder records' `folderCount`. Nothing in Mac OS
  9.0 recognises it: no code in the ROM or the System file compares with `'HX'`, and the ROM's `MountVol` accepts only
  `'BD'` and `$D2D7` [Code: Mac OS 9.0 ROM and System]. An HFSX volume presumably fails to mount with `noMacDskErr`
  (−57); that outcome is inferred, not run.
- **The HFS wrapper** (§1.2) lets a Mac without HFS Plus mount the volume as HFS and see its placeholder file.
- **Unicode 2.1 and 3.2** decompositions (§2.4).

## 5. ClassicMac

### 5.1 What comes out

- `HfsReader` recognises `'H+'` and `'HX'` at 1024, and a wrapper by `drEmbedSigWord`; both go to `HfsPlusReader`
  ([hfs.md §5.1](hfs.md#51-recognising-a-volume)). HFSX is read like HFS Plus, independently of Mac OS 9.0. A wrapper
  read returns the embedded volume's entries, not the wrapper's placeholder.
- `HfsReader.ReadVolumeInfo` gives the volume header's `createDate`, `modifyDate` and `backupDate` (for a wrapper, the
  embedded volume's; null when the embedded extent is unusable or its signature wrong). `VolumeInfo.UtcAfterCreation`
  says that the last two are UTC while `createDate` is local time [ClassicMac]. It also gives `blockSize`,
  `totalBlocks`, `freeBlocks`, `fileCount`, `folderCount`, `attributes`' software and hardware locks (bits 15 and
  7) and `finderInfo[0]` as the blessed folder's ID; the name is the root folder's, so it has none.
- Each visible file comes out with its path, Finder information, dates, both forks, and the file-locked catalog flag
  (bit 0) as `MacFile.IsLocked`. Unicode names are kept in
  `MacFile.MacPath`; the Mac OS Roman `Name` is a best-effort rendering.
- Hard links keep their visible path and take the indirect node's forks and metadata; the reference is
  `MacFile.HardLinkReference`. Directory hard links expose the inode's files at every valid alias path. The two private
  folders' subtrees are left out of the list.
- Symbolic links keep their data fork and expose it as `MacFile.SymbolicLinkTarget`, strict UTF-8.
- `HfsReader.ReadFolders` returns the folder records, the root included, as on HFS
  ([hfs.md §5.2](hfs.md#52-what-comes-out)): Mac OS Roman names and paths as for files, `userInfo` and `finderInfo`,
  `createDate` and `contentModDate`. The private folders and their subtrees are left out. The root's `FreeBytes` is the
  volume header's `freeBlocks` × `blockSize`.
- Catalog lookup by name is not offered; key order is only validated, with the volume's comparison. The comparison
  uses fixed Unicode 3.2 tables, decomposition and Hangul included, not the host runtime's.
- The catalog B-tree is read into memory within `ContainerReadOptions.MaxExpandedBytesPerInput`; more file and folder
  records (the root included) than `ContainerReadOptions.MaxVolumeEntries` make the volume unreadable.

### 5.2 What is unreadable

Structural damage makes the volume unreadable: the reader throws `InvalidDataException` and the unwrapper reports
`container.unreadable`. That covers a bad signature or version, an invalid allocation area, a missing allocation file
or extents tree, an invalid wrapper extent (zero, outside the wrapper's allocation area or the image, or under 1,536
bytes), an attributes fork with allocated blocks but no B-tree header, and any failure of §2.2, §2.3 (but the attributes
`btreeType`), §2.4 items 1–7 (but HFSX `folderCount`) and the name rules, and §2.6 items 1–2. HFS Plus keeps accepting the Unicode 2.1 forms and the sequences `FixDecomps` corrects, since its names may
come from either version; HFSX rejects them. Header fields TN1150 calls reserved are not required to be zero, and the
attributes tree's `keyCompareType` is ignored.

### 5.3 What is reported

Everything else is reported and the volume read (§6):

- The alternate header's absence or mismatch; the volume-inconsistent and journaled bits.
- The journal (§2.7): ClassicMac never replays it and reads the on-disk structures as they are.
- Counts: `fileCount` and `folderCount` against the catalog before hard links are resolved; HFSX `folderCount` whether
  or not its flag is set; `freeBlocks`; the spared-blocks bit; `encodingsBitmap`.
- An attributes tree with `btreeType` `$FF`, seen on the reference image (§7), is read.
- Attribute records for unknown CNIDs; has-attributes and has-security flags; the ACL attribute's magic, entry count,
  payload length and ACE kinds (principals, flags and rights are not checked); BSD modes.
- Links: a file with only one of `hlnk`/`hfs+`, or a directory-link candidate (link-chain flag and `alis` or `MACS`)
  without both codes and `IsAlias`, stays an ordinary file. A reference with no indirect node, or a `dir_<CNID>`
  without the link-chain flag, keeps the link with its own forks. Indirect-node names that are not canonical decimal
  are skipped; an indirect node that is a folder is not used. An indirect node no link refers to, and one whose
  `special` differs from its number of links, are noted. The directory-link checks of §1.8 (first-link attribute,
  chain, count, private folder flags, ancestor flags) report and leave the files readable; none of the verifier's
  repairs are made.

### 5.4 First Aid

`HfsFirstAid.Verify` checks an HFS Plus volume, bare or in its HFS wrapper, in stages like HFS's
([hfs.md §5.6](hfs.md#56-first-aid)), with the same report, verdicts and repair flags, and Disk First Aid's problem
numbers and words where they mean the same. The rules are TN1150's; where Disk First Aid's own HFS Plus checks are not
traced, the choices are ClassicMac's [ClassicMac]. HFSX is "not checked".

1. **"Checking disk volume."**: the volume is the image (`'H+'` at 1024) or a wrapper's `drEmbedExtent`.
2. **"Checking "Mac OS Extended" volume structures."**: the volume header at 1024 is used when sound (`'H+'`, version 4,
   a power-of-two block size of at least 512, a block count that fits, and the extents and catalog files' own
   extents consistent with their block counts); else a sound alternate at the volume's last 1,024 bytes is used
   (`firstaid.volume-header-damaged`, repaired by writing the header). Otherwise the first failure ends the check:
   not HFS Plus (`firstaid.invalid-volume-header`), #66 version, #7 block size, #8 block count, #47 extents file, #46
   catalog file. An alternate at the end of the volume's blocks that is missing or differs in the signature, version,
   block size and count, or the special files is `firstaid.alternate-volume-header`, repaired from the header. The
   special files' extents (their own and the overflow records', for all but the extents file) must add up to their
   block counts and lie within the volume (#11, #46, #47, `firstaid.invalid-special-file`); the allocation file must
   hold a bit per block (`firstaid.invalid-allocation-file`). The blocks holding the boot blocks and header and the
   alternate count as in use.
3. **The B-trees**, extents, catalog and (when present) attributes ("Checking attributes BTree."), by the HFS check
   (hfs.md §5.6) with 16-bit key lengths, maximum keys 10, 516 and 266, and HFS Plus's key orders (§2.5); node sizes
   must be a power of two from 512, at least 4 KB for the catalog and attributes (#61).
4. **"Checking catalog file."**: every leaf record in key order. The root thread (2, "") must exist (#35). A folder
   record is 88 bytes (#32), a file record 248 (#34), a thread 10 + 2n with an empty key name (#33, #38) and a name of
   1 to 255 units (#39); an unknown type is #31; a folder or file ID under 16 (but the root's) is #65. Each folder
   and file needs its thread naming its parent and name (#36, repaired); a thread whose folder is missing is #37
   (repaired from the thread), whose file is missing #6 (deleted). Each fork's extents must cover its block count
   (#1), its logical size lie within it (#2), and an overflow record's start block be the fork's blocks before it
   (`firstaid.extent-start`); a block count short of the extents is `firstaid.short-peof`.
5. **"Checking catalog hierarchy."**: each folder's valence is its folders and files (#3, repaired); an item whose
   parent folder is missing, with no thread to make it again, is `firstaid.missing-parent` (not repaired); a folder
   that is its own ancestor is #41. Hard links (§1.8): each file link's indirect file `iNode<n>` in
   `\0\0\0\0HFS+ Private Data`, and each directory link's folder `dir_<CNID>` in `.HFS+ Private Directory Data\r`,
   must exist (`firstaid.link-target-missing`, not repaired), and its link count (BSD `special`) is its number of links
   (`firstaid.link-count`, repaired; traditional Mac OS can leave it wrong [Doc: TN1150]). The directory links' chains
   and first-link attribute are left to the reader's diagnostics.
6. **"Checking volume bit map."**: the allocation file's bytes for the volume's blocks against the blocks the extents
   use (#60, repaired; #12 for blocks used twice).
7. **"Checking volume info."**: `fileCount`, `folderCount`, `freeBlocks` and `nextCatalogID` (above the highest CNID,
   unless `kHFSCatalogNodeIDsReusedBit`) against what the check counted: #59 "Volume Header needs minor repair".
   Overflow extents records of files not in the catalog are `firstaid.orphaned-extents`.

**Repair** (`HfsFirstAid.Repair`, the CLI's `repair`, the app's Volume ▸ First Aid…), as on HFS, at most three
passes, each verified again: the extents tree written again without orphaned records and with each fork's start blocks
renumbered; the catalog written again with the repair list (a file thread without its file deleted, a folder made
again from its thread, a thread naming another record deleted, a thread made for every folder and file without its
right one, each file's thread flag set, a fork's block count raised to its extents, every valence set, every
link count set), both at their
node size and node count and keeping their clump size, type and key comparison; the allocation file from the blocks
the extents use; and the computed header written over the header and its alternate when either differs. Forks that
share blocks and the attributes tree are not repaired yet. An edit session (`InputEditKind.HfsPlusVolume`)
holds an HFS Plus image for repair only: it is saved with its changed sectors compared, and every other edit is
refused.

## 6. Diagnostics

The classic HFS codes are in [hfs.md §6](hfs.md#6-diagnostics). "Not traced" means the Mac's behaviour in that case
has not been followed in its code; Mac OS 9.0 does not mount HFSX at all, and these checks follow Apple's later
`fsck_hfs` where it is named.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `hfs.plus-acl-invalid` | Warning | The ACL attribute has invalid magic, entry count, payload length or ACE kind | Keeps the object readable | XNU `kauth.h` defines the layout and ACE kinds [Code] |
| `hfs.plus-alternate-header` | Warning | The alternate volume header is missing or its signature or version differs | Reads from the primary header | Not traced |
| `hfs.plus-attribute-flag-mismatch` | Warning | An object's has-attributes flag disagrees with the attributes records for its CNID | Keeps the object readable | `hfs_format.h` defines the flag [Code] |
| `hfs.plus-attribute-orphan` | Warning | An attribute's file ID names no catalog object and no special-file CNID 3–8 | Keeps the catalog and files | TN1150 attribute keys identify their file or folder [Doc] |
| `hfs.plus-btree-type` | Warning | The attributes B-tree has `btreeType` `$FF` | Reads the attributes tree | TN1150 specifies type 0 for the special files' trees [Doc] |
| `hfs.plus-counts` | Info | The catalog's file or folder records differ from `fileCount`/`folderCount` | Reports only | Not traced |
| `hfs.plus-encoding-bitmap` | Info | A record's text encoding has no bit in `encodingsBitmap` | Reports only | Not traced |
| `hfs.plus-file-link-count-invalid` | Warning | A regular file that is not a recognised link has a link count over 1 | Keeps the file and its forks | `fsck_hfs` checks the count [Code] |
| `hfs.plus-folder-count` | Info | An HFSX folder's `folderCount` differs from its folders and directory-link aliases | Reports only | `fsck_hfs` repairs the stored count [Code] |
| `hfs.plus-free-blocks` | Info | The bitmap's free blocks differ from `freeBlocks` | Reports only | Not traced |
| `hfs.plus-hardlink-alias-has-data` | Warning | A hard-link alias has allocated data-fork blocks | Reads the link through its indirect node | `fsck_hfs` checks alias data forks [Code] |
| `hfs.plus-hardlink-ancestor-flag-missing` | Warning | A folder above a directory-link alias lacks the has-child-link flag | Keeps the aliases and their contents | `dirhardlink.c` checks ancestor flags [Code] |
| `hfs.plus-hardlink-chain-flag-unexpected` | Warning | A file outside the private folders has the link-chain flag but no link signature | Keeps the file and its forks | `fsck_hfs` treats it as a stray flag [Code] |
| `hfs.plus-hardlink-chain-invalid` | Warning | A directory inode's first-link attribute is missing or malformed, its alias chain is broken or cyclic or misses an alias, or its link count differs | Keeps the aliases and their contents | `dirhardlink.c` checks the chain [Code] |
| `hfs.plus-hardlink-count-mismatch` | Info | An indirect node's link count differs from the links found | Reports only | TN1150: traditional Mac OS can leave the estimate inaccurate [Doc] |
| `hfs.plus-hardlink-indirect-name-invalid` | Warning | An `iNode` name is not canonical decimal | Skips that node | Not traced |
| `hfs.plus-hardlink-indirect-not-file` | Warning | A link reference names an indirect node that is a folder | Keeps the link with its own forks | Not traced |
| `hfs.plus-hardlink-indirect-orphan` | Info | An indirect node has no link referring to it | Reports only | Not traced |
| `hfs.plus-hardlink-private-directory-invalid` | Warning | The directory-link private folder lacks the immutable owner flag or the sticky bit | Reads on | `dirhardlink.c` checks these flags [Code] |
| `hfs.plus-hardlink-signature-invalid` | Warning | A file has only one of `hlnk`/`hfs+`, or a directory-link candidate lacks `alis`, `MACS` or `IsAlias` | Keeps it as an ordinary file | Not traced |
| `hfs.plus-hardlink-target-missing` | Warning | A link reference has no indirect node, or a directory link's `dir_<CNID>` lacks the link-chain flag | Keeps the link with its catalog forks | Not traced |
| `hfs.plus-invalid-bsd-mode` | Warning | An initialised BSD mode does not match the record (folder not a directory, file of an unknown type) | Reads on | `fsck_hfs` `CheckBSDInfo` [Code] |
| `hfs.plus-journal-info-invalid` | Warning | The JournalInfoBlock pointer, flags, range or allocation, the root journal files or their extents, or the journal header are inconsistent | Reads the catalog without replaying the journal | TN1150 Journal Info Block [Doc] |
| `hfs.plus-journal-not-replayed` | Info | The volume is journaled | Reads the recorded structures | Not traced |
| `hfs.plus-security-flag-mismatch` | Warning | An object's has-security flag disagrees with its `com.apple.system.Security` attribute | Keeps the object readable | `hfs_format.h` defines the flag [Code] |
| `hfs.plus-spared-blocks` | Info | The spared-blocks bit disagrees with the bad-block extent records | Reports only | Not traced |
| `hfs.plus-volume-inconsistent` | Warning | Unmounted-cleanly clear, or boot-inconsistent or bit 14 set | Completes the checks and returns the files if they pass | Not traced |
| `hfs.wrapper-extent-unallocated` | Warning | An allocation block of the embedded volume is marked free in the wrapper's bitmap | Reads the embedded volume | TN1150 wrapper extent and HFS bitmap [Doc] |

The wrapper's bitmap not fitting in the image is `hfs.bitmap-truncated` ([hfs.md §6](hfs.md#6-diagnostics)).

## 7. Verification

- `tests/ClassicMac.Files.Tests/HfsPlusFeatureTests.cs` builds valid and damaged HFS Plus and HFSX volumes for the
  rules of §2: case folding with fixed Unicode tables, a nested file with both forks, the entry and byte limits, counts
  (hard-link inodes included), free blocks with bitmap padding, the alternate header (missing, invalid, stale, in a
  partial block), HFSX folder counts, the encodings bitmap and its special bits, the required extents tree, symbolic
  links, file and directory hard links with each of their diagnostics, B-tree node sizes of 512, 1024 and 2048 bytes
  for the extents tree, journal headers in 512- and 2,048-byte sectors (a changed byte past the fixed fields
  included), forks whose logical size exceeds their blocks (catalog data and resource forks, and the startup file), and
  the Unicode 2.1 forms and `FixDecomps` sequences; folder records' `DInfo`, `DXInfo` and dates through
  `ReadFolders`, plain, wrapped and with the private folder left out.
- `tests/ClassicMac.Files.Tests/HfsPlusOriginalImageTests.cs` reads a 10 MiB journaled Mac OS X HFS Plus image from
  Digital Corpora's `nps-2009-hfsjtest1` corpus (`image.gen1.dmg`, SHA-256
  `BEB7795DD6D1A5319F9C20101855FFFF9665FCC11C6B23DE822D50C0D1E388EE`), given through
  `CLASSICMAC_HFSPLUS_REFERENCE_IMAGE`, and checks `file1.txt` and `file2.txt` against the corpus's published hashes.
  Its attributes tree has `btreeType` `$FF` (§5.3). The image is not in the repository; see
  [`TestData/HfsPlusOriginal/README.md`](../../../tests/ClassicMac.Files.Tests/TestData/HfsPlusOriginal/README.md).
- The checks are bounded to the read path; this is not `fsck_hfs` parity.

## 8. Not covered

- Writing HFS Plus or HFSX; journal replay; `fsck_hfs` repair.
- Hot Files records, boot blocks, and the wrapper's software-lock bit and bad-block file (its extent bounds and bitmap
  are checked).
- Individual ACE principals, flags and rights.
- Open: that an HFSX volume fails to mount on Mac OS 9.0 with −57 is inferred from the code, not run.
- Open: names from Unicode 2.1 and 3.2 cannot be told apart (§2.4); no further 2.1/3.2 name case is known.
- No rule in this document is fitted to data alone.

## 9. References

1. Apple, Technical Note TN1150, *HFS Plus Volume Format*,
   <https://developer.apple.com/library/archive/technotes/tn/tn1150.html>, including its [Journal Info
   Block](https://developer.apple.com/library/archive/technotes/tn/tn1150.html#JournalInfoBlock) and [Journal
   Header](https://developer.apple.com/library/archive/technotes/tn/tn1150.html#JournalHeader) definitions.
2. Apple, `hfs`, <https://github.com/apple-oss-distributions/hfs>: `core/hfs_format.h`, `core/hfs_xattr.c`
   (`hfs_attrkeycompare`), and `fsck_hfs`'s `lib_fsck_hfs/dfalib/` `SVerify2.c` (`CmpBTM`, `BTCheck`,
   `BTCheckUnusedNodes`), `CatalogCheck.c` (`CheckCatalogRecord`, `CheckFile`, `CheckDirectory`, `CheckBSDInfo`,
   `CheckCatalogName`, `CheckFolderCount`, `FixDecomps`), `DecompData.h`, `DecompMakeData.c`, `dirhardlink.c`; Apple
   Public Source License 2.0.
3. Apple, XNU, `bsd/sys/kauth.h`, <https://github.com/apple-oss-distributions/xnu>; Apple Public Source License 2.0.
4. The Unicode Standard, §3.9 (UTF-16), and the Unicode 3.2 character database.
5. The Mac OS 9.0 ROM and System file, traced in disassembly: `MountVol` and the signatures it accepts.
6. Linux, HFS Plus (`include/linux/hfs_common.h`), <https://github.com/torvalds/linux>; GPL-2.0. Behaviour only: the
   ACL attribute's name.
7. Digital Corpora, `nps-2009-hfsjtest1`, <https://digitalcorpora.org/>: the reference image.
