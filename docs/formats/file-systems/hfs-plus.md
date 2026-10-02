# HFS Plus

HFS Plus (Mac OS 8.1 and later) keeps its volume header at byte 1024, signature `'H+'`, version 4 **[Doc]** TN1150.
HFSX uses signature `'HX'`, version 5. ClassicMac reads either volume directly and reads an HFS Plus volume embedded
in a classic HFS wrapper. The wrapper's MDB has `drEmbedSigWord` (+$7C) and `drEmbedExtent` (+$7E, start and count);
the embedded byte offset is `drAlBlSt × 512 + drEmbedExtent.start × drAlBlkSiz` **[Doc]** TN1150. Wrapper reads follow
that extent and return the embedded volume's entries, not the wrapper's placeholder file. Each allocation block
covered by the embedded extent must be marked in use in the wrapper's volume bitmap; a clear bit produces the
`hfs.wrapper-extent-unallocated` warning while the embedded volume remains readable. The reader checks the alternate
volume header 1,024 bytes before the volume end for a matching signature and version; it reports a warning
if the recovery copy is absent or invalid and continues using the primary header **[Doc]** TN1150. A cleared clean-
unmount bit or set boot-inconsistent bit produces `hfs.plus-volume-inconsistent`; the reader completes its structural
checks and returns files when those checks succeed **[Doc]** TN1150. It also recognizes bit 14 as Apple's later
`kHFSVolumeInconsistentBit` and reports the same warning **[Code]**
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h), although TN1150 marks
that bit reserved. If the journaled bit is set, the reader reports `hfs.plus-journal-not-replayed`, validates that
`journalInfoBlock` points to an allocated block, and checks the JournalInfoBlock's in-volume flags and journal range
against the allocation area. It also checks that the root `.journal_info_block` file is one extent at the volume-header
pointer and that the root `.journal` file is one extent whose start and logical size match the JournalInfoBlock.
For an initialized journal, the reader also checks the journal header's magic, endian marker, size fields, circular-buffer
offsets, and checksum over the full sector declared by `jhdr_size`; feature tests cover 512- and 2,048-byte sectors,
including a changed byte beyond the fixed header fields. A `NeedInit` JournalInfoBlock flag skips header validation
because TN1150 defines the header as invalid until initialized. Missing files or inconsistent journal structures
produce `hfs.plus-journal-info-invalid`;
readable catalog files are retained. ClassicMac still returns the on-disk structures without applying journal
transactions **[Doc]** TN1150. Other reserved volume-attribute bits are ignored **[Doc]** TN1150. The JournalInfoBlock
location and fields follow TN1150's [Journal Info Block definition](https://developer.apple.com/library/archive/technotes/tn/tn1150.html#JournalInfoBlock),
and journal header layout and checksum follow its [Journal Header definition](https://developer.apple.com/library/archive/technotes/tn/tn1150.html#JournalHeader).

For each catalog file and folder, the reader checks that the volume header's `encodingsBitmap` contains the bit for
the record's `textEncoding` hint. Values below 64 use the same-numbered bit; MacFarsi (140) uses bit 49 and
MacUkrainian (152) uses bit 48 **[Doc]** TN1150. Missing bits produce the informational diagnostic
`hfs.plus-encoding-bitmap`; extra bits are accepted because TN1150 permits them to remain set after the last name using
an encoding has been deleted.

The volume header's `fileCount` must equal the number of file records in the catalog, and `folderCount` must equal
the number of folder records minus the root folder. These counts include private hard-link inode files and directory
inode folders even though ClassicMac hides those records when it returns the visible file tree **[Doc]**
[`TN1150`](https://developer.apple.com/library/archive/technotes/tn/tn1150.html). The reader compares counts against
catalog records before hard-link resolution and reports a mismatch as `hfs.plus-counts`.

The reader requires an extents-overflow B-tree even when it contains no overflow records; [TN1150](https://developer.apple.com/library/archive/technotes/tn/tn1150.html)
lists it as a special file needed to access the volume and permits its empty-tree root and leaf fields to be zero **[Doc]**. It checks the
catalog B-tree's root index graph, node heights and same-level sibling links, then walks its
linked leaf nodes, checking the header's leaf endpoints, backward links and node range. For each tree, the total
records found across all leaf nodes must match that tree's header `leafRecords` count **[Doc]** TN1150. It also requires
an empty root leaf to be valid when no parent index key needs to identify its first record; an empty non-root child
subtree is rejected because Apple's verifier compares each child separator with the first record in that child
**[Code]** in [Apple's `SVerify2.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/SVerify2.c#L3198-L3210).
The reader also respects the node size declared by each B-tree header; feature tests cover valid 512-, 1024- and 2048-byte
extents-overflow nodes in addition to the standard fixture size. Catalog nodes retain TN1150's 4 KiB minimum.
the B-tree header node to contain three records and a zero backward link **[Doc]**. It checks that the catalog and
extents B-tree headers have the required control-file type **[Doc]**, that catalog B-tree nodes meet TN1150's 4 KiB
minimum **[Doc]**, and that the catalog, extents-overflow and attributes B-tree headers use the control-file type
**[Doc]** TN1150. For compatibility with the corpus image, an attributes-tree type of `0xFF` is also accepted with the
warning `hfs.plus-btree-type`; its attributes-tree `keyCompareType` value is reserved and ignored. Other nonzero
tree types remain invalid. Key-layout attributes use 16-bit key lengths, with variable-length index keys in
the catalog and attributes trees and fixed-length index keys in the extents tree **[Doc]** TN1150. The B-tree map is one
most-significant-bit-first bit per node and
continues in linked map nodes when the header map record is too small **[Doc]** TN1150. The reader requires exactly
enough continuation map nodes to cover the tree, checks their descriptors, record boundaries and bitmap coverage, and requires the header, index, leaf and map nodes to be marked
allocated with no additional nodes marked allocated, matching the tree's reachable nodes **[Code]** as Apple's HFS
verifier's `CmpBTM` does. It verifies `freeNodes` against the complete bitmap, and requires unused bytes after the
final byte containing node bits in the last map record to be zero **[Code]** as `CmpBTM` does in
[`SVerify2.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/SVerify2.c). Every node
marked free in the Catalog B-tree must also be zero-filled **[Code]**, as Apple's verifier calls
`BTCheckUnusedNodes` for that tree and `hfs_format.h` names the corresponding volume bit for unused Catalog B-tree
nodes. TN1150 does not apply this rule to the extents-overflow or attributes trees, whose free-node contents are ignored.
It checks that
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
runtime's evolving normalization tables. HFSX rejects the legacy U+0306 + U+0307 sequence, plus
Greek tonos sequences formed by U+030D after Greek tonos bases or diaeresis, and also these `fsck_hfs` fixes **[Code]** Apple's
[`FixDecomps`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c): Bengali BA plus
nukta to RA with middle diagonal, Odia YA plus nukta to U+0B5F, Gurmukhi DDA plus nukta to U+0A5C, Thai and Lao vowel
sequences to their AM letters, and two Tibetan three-character sequences to U+0F77 and U+0F79. Since HFS+ has no
field recording which decomposition version created its names, the reader continues to accept these legacy sequences
there. File and folder records named exactly `.` or `..` are rejected; Apple `fsck_hfs` marks those catalog names
illegal **[Code]** [`CheckCatalogName`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).
Repeated combining marks with equal canonical combining classes, such as two U+0307 marks, retain their original order
and are accepted; Apple's [`DecompMakeData.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/DecompMakeData.c)
defines no correction for that sequence.
Names such as `.hidden` and `...` remain valid. It also accepts the Unicode 2.1 forms of 44 code points whose
canonical decomposition changed by Unicode 3.2, including U+01F8 and U+01F9 **[Doc]** TN1150; HFSX requires the
updated forms **[Code]** Apple's
[`FixDecomps`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c) and
[`DecompData.h`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/DecompData.h).
Feature tests cover the Unicode 2.1 scalar forms and sequence corrections identified by Apple's `FixDecomps`; no additional
Unicode 2.1/3.2 name-compatibility case is currently known. HFS+ continues to accept the legacy forms because the volume
does not record which decomposition version produced its names.
Catalog IDs are unique, every nonroot file or folder record's `parentID` names an existing folder, and each required
file and folder thread points back to its record's parent and name **[Doc]** TN1150. `nextCatalogID`
must be at least 16 even when IDs have been reused; otherwise, TN1150 requires it to exceed every catalog ID.
TN1150 requires leaf-record keys to be unique **[Doc]**. It
checks each folder's recorded valence against its direct file and folder records, and checks the ancestry of every
nonroot folder, including empty folders. TN1150 defines valence as the count of file and folder records whose key
parent ID is that folder's ID **[Doc]**. On HFSX it also checks `folderCount`, which counts enclosed folder records
and directory hard-link aliases rather than all catalog children. Apple's verifier sets a missing
`kHFSHasFolderCountMask` before comparing the field; the read-only reader reports a mismatch as
`hfs.plus-folder-count` whether or not the flag is set, without preventing the volume from being read **[Code]** Apple's
[`CheckFolderCount`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c)
and the `HFSPlusCatalogFolder` definition in
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h). Unless the volume's
catalog-ID-reuse flag is set, `nextCatalogID` must be
greater than all file and folder IDs, as TN1150 requires **[Doc]**. It then resolves file paths and reads both forks,
Finder info and dates. It
also reads data and resource fork overflow
extents and requires the primary and overflow extents to account for each fork's declared allocation-block count
(which may exceed the blocks needed by its logical length). Overflow records are allowed only after all eight initial
extent descriptors are occupied, every non-final overflow record must contain eight extents, and no matching records
may remain after the fork's declared block count is covered **[Doc]** TN1150. It rejects overlapping allocation ranges among the
extents it reads, following TN1150's allocation-file ownership model. Each fork's logical size must fit within its
declared allocated blocks; feature tests exercise this for both catalog data and resource forks, plus the startup
special-file fork **[Doc]** TN1150.
Extent descriptors must be contiguous from the
first descriptor, and every unused descriptor must be all zero **[Doc]** TN1150. Empty forks cannot retain extent
descriptors or overflow records. The volume header must provide the required
allocation file **[Doc]** TN1150; the reader requires its bitmap to cover the declared allocation blocks and to mark
each parsed extent as allocated, along with the blocks containing the first 1,536 and last 1,024 volume bytes
**[Doc]** TN1150. Any bitmap bits beyond the declared allocation-block count must be clear **[Doc]** TN1150. It checks
that parsed fork extents do not claim any allocation blocks containing the volume's primary or alternate header or
the reserved areas around them, following TN1150's description of those areas as reserved. The alternate header is
located 1,024 bytes from the actual end of the volume; it may therefore lie in the trailing partial allocation unit
when the volume length is not a multiple of the allocation block size. It checks
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
and includes defined fork-data and extent attribute records in allocation checks; inline attribute records are
checked against the current Apple `HFSPlusAttrData` layout, including the declared data size and the B-tree's
even-byte record padding; reserved fields are ignored when reading, as TN1150 requires. Inline and unknown records
do not claim extents **[Doc]** TN1150 / Apple's
[`HFSPlusAttrData`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h). For each
extent-backed attribute it requires one fork-data record at
key `startBlock` zero, matches extension records by file ID and attribute name, and requires each extension's
`startBlock` to continue the preceding extent count. The initial record must contain eight extents when overflow is
present, and each non-final extension record must contain eight **[Doc]** TN1150. Their combined extent count must equal
`HFSPlusForkData.totalBlocks`, and the logical size must fit in those allocated blocks **[Doc]** TN1150 / Apple's
`HFSPlusAttrForkData` and `HFSPlusAttrExtents` definitions in
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h). Orphan extensions and
gaps in an attribute fork's extent sequence are rejected. Attribute keys are validated and ordered by file ID, name length,
binary UTF-16 name and start block in leaves and index nodes; the key's reserved padding field must be zero, and
each index key must compare equal to the first key in its child subtree, as Apple's verifier's `BTCheck` requires
**[Code]** [`SVerify2.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/SVerify2.c);
separators are also checked against adjacent child key ranges. The key
ordering uses Apple's HFS comparator for the rule that
TN1150 leaves unfinished **[Code]** [Apple HFS `hfs_attrkeycompare`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_xattr.c#L2082-L2144).
Attribute keys whose file ID does not identify a catalog object or one of the special-file CNIDs 3 through 8
produce a warning; the catalog remains readable. For cataloged files and folders, the `HasAttributes` catalog flag
must agree with whether the attributes tree contains a record for that CNID. A mismatch produces the
`hfs.plus-attribute-flag-mismatch` warning and leaves the object readable **[Code]** Apple
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h).
The `HasSecurity` flag must agree with the presence of the `com.apple.system.Security` ACL attribute for that CNID;
mismatches produce `hfs.plus-security-flag-mismatch` and leave the object readable **[Code]** Apple's
[`hfs_format.h`](https://github.com/apple-oss-distributions/hfs/blob/main/core/hfs_format.h) defines the flag, and
[Linux's HFS+ implementation](https://github.com/torvalds/linux/blob/master/include/linux/hfs_common.h) names the ACL attribute.
The ACL attribute's inline value is checked for Apple's `kauth_filesec` magic, a bounded entry count (maximum 128),
and a payload size of 44 bytes plus 24 bytes per ACE; the `KAUTH_FILESEC_NOACL` sentinel uses the 44-byte header
alone. ACE kind values 1 through 4 (permit, deny, audit and alarm) are accepted; undefined kinds produce
`hfs.plus-acl-invalid` while the catalog object remains readable. The reader checks the ACL container layout and ACE
kinds, not individual ACE principals, other flags or rights **[Code]** Apple's
[`kauth.h`](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/kauth.h).
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
as `MacFile.HardLinkReference`. An alias with allocated data-fork blocks is kept readable through its indirect node and
reported as `hfs.plus-hardlink-alias-has-data` **[Code]** Apple's
[`CatalogCheck.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c#L1001-L1004).
Indirect-node names must use canonical decimal text without leading zeroes **[Doc]**
TN1150; malformed names are skipped and reported as `hfs.plus-hardlink-indirect-name-invalid`. A matching reference
that names a folder instead of a file is reported as `hfs.plus-hardlink-indirect-not-file`. A nonzero link reference
without a matching node is retained with its catalog forks and reported as `hfs.plus-hardlink-target-missing`. The
indirect node's BSD special field is treated as its estimated link count; a difference from the number of catalog hard
links is reported as the informational `hfs.plus-hardlink-count-mismatch` because TN1150 says traditional Mac OS can
make this estimate inaccurate. An indirect node with no referring hard link is reported as the informational
`hfs.plus-hardlink-indirect-orphan`. The
reader also resolves directory hard-link aliases. A catalog file with the hard-link-chain flag and either Finder type
`alis` or creator `MACS` is treated as a candidate; it must have both codes and Finder's `IsAlias` flag to be followed.
An incomplete candidate is retained as a file and reported as `hfs.plus-hardlink-signature-invalid`. A valid alias
targets a `dir_<catalog-ID>` folder in the root's `.HFS+ Private Directory Data\r` folder. Files below that directory
inode are exposed at each valid alias path, and both private metadata subtrees are omitted from the file list. An alias
whose `dir_<CNID>` folder lacks the hard-link-chain flag is treated as having no valid inode; the alias is retained and
reported as `hfs.plus-hardlink-target-missing`. The reader also checks Apple's private
`com.apple.system.hfs.firstlink` inline attribute on each directory inode, follows the catalog-ID chain through aliases,
and checks each alias's previous-link ID, the inode's link count, and that the chain covers all visible aliases. A missing
or malformed attribute, missing alias, broken/cyclic chain, or count mismatch is reported as
`hfs.plus-hardlink-chain-invalid`; the visible files remain readable. These checks follow the corresponding chain
validation in Apple's `dirhardlink.c`; the private directory must also have the immutable owner flag and sticky mode bit,
and every folder ancestor of an alias (up to the root or private directory) must carry the `HasChildLink` catalog flag,
or the reader reports `hfs.plus-hardlink-private-directory-invalid` or
`hfs.plus-hardlink-ancestor-flag-missing`, respectively. These checks do not implement the verifier's repair
behavior. TN1150's file-hard-link
section predates this directory-link representation **[Code]**
[Apple `dirhardlink.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/dirhardlink.c).
Outside the private hard-link folders, a file with the directory hard-link chain flag but neither a recognized file-hard-link
signature nor a directory-alias signature is retained and reported as `hfs.plus-hardlink-chain-flag-unexpected`; Apple's
catalog checker treats such a record as a stray link-chain flag **[Code]**
[Apple `CatalogCheck.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).
An ordinary BSD regular file with a link count greater than one is also retained and reported as
`hfs.plus-file-link-count-invalid`, except for recognized links, the private file-hard-link folder and journal files.
Apple's catalog checker treats this count as inconsistent for an ordinary regular file **[Code]**
[Apple `CatalogCheck.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).
Files nested below a directory inode are mapped through every visible alias path.
The B-tree reader requires every record start and end offset to be
even, including each node's free-space offset **[Doc]** TN1150. It requires extents-overflow records to have the fixed
64-byte `HFSPlusExtentRecord` payload and defined attribute fork-data and extents payloads to have their fixed 88- and
72-byte lengths. TN1150 says undefined attribute record types must be ignored, so their payloads are not interpreted.
Catalog thread records may contain trailing bytes up to the 520-byte structure maximum; Apple's verifier accepts that
maximum, so the reader does not require a thread payload to end immediately after its declared name **[Code]** in
[`CatalogCheck.c`](https://github.com/apple-oss-distributions/hfs/blob/main/lib_fsck_hfs/dfalib/CatalogCheck.c).

The current implementation checks the header and node kinds/heights, root-to-leaf graph, per-level sibling chains,
leaf chain and endpoints, key ordering, child key ranges, record counts, node-map coverage/allocation and free-node
accounting. Header fields documented as reserved are intentionally not treated as required-zero checks.
As TN1150 permits, allocation
blocks marked used but not described by known
fork extents are not rejected; the reader checks that every extent it recognizes is marked allocated.

The HFS+ validation audit is bounded to the read path: TN1150's catalog-ID and allocation-file consistency checks,
the B-tree structure and ordering used to find catalog data, and the catalog, fork, attribute and link rules needed to
enumerate files and expose their forks. Feature tests build both valid and damaged volumes for those rules. This is
not full `fsck_hfs` parity and does not implement repair or journal replay. The reader does not validate Hot Files
records, boot blocks, or the HFS wrapper's software-lock bit and bad-block-file membership; its wrapper check covers
the embedded extent bounds and allocation bitmap. The optional interoperability test uses a 10 MiB, journaled
Mac OS X HFS+ image from Digital Corpora's `nps-2009-hfsjtest1` corpus (SHA-256
`BEB7795DD6D1A5319F9C20101855FFFF9665FCC11C6B23DE822D50C0D1E388EE`); it verifies the published contents of
`file1.txt` and `file2.txt`. The image is not included in the repository; see
[`TestData/HfsPlusOriginal/README.md`](../../../tests/ClassicMac.Files.Tests/TestData/HfsPlusOriginal/README.md).

HFSX, the variant of HFS Plus with case-sensitive names (Mac OS X 10.3 and later), has the signature `'HX'` at 1024
**[Doc]** TN1150. Nothing in Mac OS 9.0 recognises it: no code in the ROM or the System file compares with `'HX'`,
and the ROM's `MountVol` accepts only `'BD'` and `$D2D7` **[Code]** Mac OS 9.0 ROM and System. An HFSX volume
presumably fails to mount with `noMacDskErr` (−57); that outcome is inferred, not run. ClassicMac recognizes HFSX
independently of Mac OS 9.0 and reads its catalog and forks. It validates key order using the volume's comparison mode;
catalog lookup is not currently exposed.

Contents

1. [Diagnostics](#1-diagnostics)
2. [Not covered and open questions](#2-not-covered-and-open-questions)

---

## 1. Diagnostics

Severity: **I** Info, **W** Warning, **E** Error. "Not traced" means the Mac's behaviour in that case has not been
followed in its code.

| Code | Sev. | Meaning | ClassicMac | The Mac |
| --- | --- | --- | --- | --- |
| `hfs.plus-counts` | I | HFS Plus catalog file/folder counts differ from the volume header | Reports only | Not traced |
| `hfs.plus-folder-count` | I | An HFSX folder count differs from its enclosed folders and directory hard-link aliases | Reports only | `fsck_hfs` repairs the stored count **[Code]** |
| `hfs.plus-encoding-bitmap` | I | A catalog file or folder uses an encoding whose bit is absent from `encodingsBitmap` | Reports only | Not traced |
| `hfs.plus-free-blocks` | I | The allocation bitmap free-block count differs from `freeBlocks` in the volume header | Reports only | Not traced |
| `hfs.plus-spared-blocks` | I | The volume header's spared-blocks flag disagrees with bad-block extent records | Reports only | Not traced |
| `hfs.plus-attribute-orphan` | W | An attributes B-tree key refers to a missing catalog object other than special-file CNIDs 3 through 8 | Keeps the readable catalog and files | TN1150 attribute keys identify their owning file or folder **[Doc]** |
| `hfs.plus-attribute-flag-mismatch` | W | A catalog object's `HasAttributes` flag disagrees with attribute-record presence for its CNID | Keeps the object readable | Apple `hfs_format.h` defines the flag as indicating extended attributes **[Code]** |
| `hfs.plus-security-flag-mismatch` | W | A catalog object's `HasSecurity` flag disagrees with presence of its `com.apple.system.Security` ACL attribute | Keeps the object readable | Apple `hfs_format.h` defines the flag; Linux HFS+ names the ACL attribute **[Code]** |
| `hfs.plus-acl-invalid` | W | The ACL attribute has invalid file-security magic, entry count, ACE payload length or ACE kind | Keeps the catalog object readable | Apple XNU `kauth.h` defines the file-security layout and ACE kinds **[Code]** |
| `hfs.plus-btree-type` | W | The attributes B-tree uses reserved type `0xFF`, observed in the journaled Mac OS X reference image | Reads the attributes tree for compatibility | TN1150 specifies type zero for system B-trees **[Doc]** |
| `hfs.plus-volume-inconsistent` | W | Volume attributes indicate an unclean unmount, inconsistent boot volume, or serious inconsistency | Completes structural checks and returns files if valid | Not traced |
| `hfs.plus-journal-not-replayed` | I | Volume is marked journaled, but ClassicMac has not replayed its journal | Reads recorded structures | Not traced |
| `hfs.plus-journal-info-invalid` | W | The journal info block pointer, storage flags, journal range, allocation bits, root journal files or their extents are inconsistent | Reads catalog files without replaying the journal | TN1150 Journal Info Block definition |
| `hfs.plus-alternate-header` | W | The alternate HFS Plus volume header is missing or has an invalid signature/version | Reads using the primary header | Not traced |
| `hfs.plus-hardlink-target-missing` | W | A hard-link reference has no matching private indirect node | Keeps the link record, using its catalog forks | Not traced |
| `hfs.plus-hardlink-alias-has-data` | W | A hard-link alias has allocated data-fork blocks | Keeps the link readable through its indirect node | Apple `CatalogCheck.c` checks alias data-fork allocation **[Code]** |
| `hfs.plus-hardlink-chain-flag-unexpected` | W | A non-private file has the directory hard-link chain flag but no hard-link Finder signature | Keeps the file and its forks | Apple `CatalogCheck.c` checks link-chain flags **[Code]** |
| `hfs.plus-file-link-count-invalid` | W | A regular file that is not a recognized link has a BSD link count greater than one | Keeps the file and its forks | Apple `CatalogCheck.c` checks the count **[Code]** |
| `hfs.plus-hardlink-chain-invalid` | W | A directory hard-link inode has a missing or malformed first-link attribute, a broken alias chain, or a mismatched link count | Keeps the readable directory aliases and their contents | Apple `dirhardlink.c` checks the chain **[Code]** |
| `hfs.plus-hardlink-private-directory-invalid` | W | The directory-hard-link private folder lacks the immutable owner flag or sticky mode bit | Keeps reading the volume | Apple `dirhardlink.c` checks these flags **[Code]** |
| `hfs.plus-hardlink-ancestor-flag-missing` | W | A directory hard-link alias ancestor lacks the `HasChildLink` catalog flag | Keeps the readable directory aliases and their contents | Apple `dirhardlink.c` checks ancestor flags **[Code]** |
| `hfs.wrapper-extent-unallocated` | W | An allocation block occupied by the embedded HFS Plus volume is marked free in the wrapper bitmap | Reads the embedded volume | TN1150 wrapper extent and HFS allocation bitmap |

---

## 2. Not covered and open questions

No rule in this document is fitted to data alone. Still open:

1. That an HFSX volume fails to mount on Mac OS 9.0 with −57 is inferred from the code, not run.
2. TN1150 says Mac OS 8.1–10.2 used Unicode 2.1 decompositions, while Mac OS X 10.3 and later use Unicode 3.2.
   The volume does not record which version produced its names. The reader validates against 3.2, preserves the 44
   Unicode 2.1 scalar spellings, and covers the sequence corrections identified by Apple's `fsck_hfs` `FixDecomps`.
