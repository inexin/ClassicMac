# StuffIt 1.x–4.x (legacy format)

This document records the archive formats implemented by `ClassicMac.Files.Archives`. StuffIt and Compact Pro have no
published vendor specifications in the project references; rules derived from another reader or from test fixtures are
provisional and marked **[Fitted]**. **[Reference]** marks a rule taken from another reader when no original-format
sample verifies it yet. An independently generated fixture proves the documented behavior, not that the original Mac
application writes every detail the same way.

Legacy archives start with `SIT!`, the root entry count, total archive length, and `rLau`. The version-1 layout is used
by StuffIt 1.0–1.5; it has sequential 112-byte member headers beginning at offset 22. The version-2 layout is used by
StuffIt 1.6–4.5; it has linked member records and the root offset at +16.
Member headers hold the two fork methods, a Pascal-style byte name, Finder type/creator/flags, dates, expanded and
compressed fork lengths, fork CRCs, and the header CRC at +110. In version 1, method `$20` starts a folder and `$21`
ends it; folder members have no fork payload. Version 1 names can be up to 63 bytes and are decoded as MacRoman.
Version 2 currently accepts names up to 31 bytes and follows first-child, next, and declared child-count fields.
It also checks each member's previous-sibling and parent offsets against the list position and containing folder
established by traversal. A mismatch produces `archive.previous-link-mismatch` or `archive.parent-link-mismatch` as a
warning; extraction continues using the traversed list, so inconsistent back-links do not discard otherwise readable
files.
These layouts and folder markers are **[Fitted]** against the published format table in
[psx-spx](https://psx-spx.consoledev.net/psx-spx.pdf) and the independent
[XADMaster parser](https://sources.debian.org/src/unar/1.1-2/XADMaster/XADStuffItParser.m/).
An original StuffIt Deluxe 4.5 archive from the CC0 test corpus
verifies version-2 traversal and method-13 extraction of both `testfile.PICT` forks, as well as a method-13 resource
fork. A second original StuffIt Deluxe 4.5 sample and its AppleDouble companion verify that an archive-level comment is
read from resource type `SitC`, ID 0 in the archive file's resource fork and decoded as MacRoman. This placement was
first **[Reference]** based on [XADMaster](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADStuffItParser.m/).
Version 1 is **[Verified]** against archives made by StuffIt 1.5.1 on Mac OS 9 (`TestData/StuffIt151`): the member
layout, folder start/end entries and methods 0 (stored), 1 (RLE90), 2 (LZW) and 3 (Huffman) expand every file with
both forks, Finder type/creator/flags and dates. StuffIt 1.5.1 leaves stale lengths in a folder-end entry (data
lengths 32 and 256 in three of the four samples, as in the folder-start entry); folder entries carry no payload, so the reader ignores their
lengths. Method 6 is still **[Fitted]**: StuffIt 1.5.1 cannot write it, and it has hand-built tests only. Encrypted
entries are reported and skipped.

Version 2 as StuffIt Deluxe 4.5 writes it is **[Verified]** against the CC0 corpora's archives
(`TestData/DiskDoublerOriginal/StuffIt45DiskDoubler377DdaFiles.sit`, `TestData/StuffItOriginalCrossVersion`):
- A folder is a folder-start record: method `$20` in both method bytes, its first member at +62 and its member count
  at +48 (the folder's fork lengths hold the total of its contents). A file's +62 is not a link: Deluxe 4.5 leaves
  other bytes there (`$00400001` and the like), so +62 never marks a folder; only the method does. The folder's
  closing record (method `$21`) follows its last member and is not in any list.
- A folder's first member's previous link is the folder's own offset (a root member's is 0); later members link to
  the member before them.
- Encryption sets bit 7 of the method byte (`$8D`: encrypted method 13; `$80`: encrypted stored, padded to 16-byte
  blocks); the older `$10` bit is honoured too. Such entries are reported (`archive.encrypted`) and skipped.

The legacy v1/v2 reader is implemented. Original-application coverage currently includes a flat version-2 archive;
version 1, nested-folder metadata, and broader legacy interoperability remain unverified. Archive-level comment
placement has an original-app test through the `SitC` resource described above. See Phase 10 in
[the project plan](../../PLAN.md).
