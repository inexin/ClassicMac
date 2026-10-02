# StuffIt 1.x–4.x

The archive format of StuffIt 1.0 to 4.5 (Raymond Lau, later Aladdin Systems), the common Mac archive of the late
1980s and 1990s, usually with the extension `.sit`. It comes in two layouts: version 1 (StuffIt 1.0–1.5), a sequence
of member headers with folder start and end entries, and version 2 (StuffIt 1.6–4.5), whose members are linked by
offset. Each member holds a file's data and resource forks, each compressed by one of the StuffIt methods
([stuffit-methods.md](../codecs/stuffit-methods.md)). ClassicMac lists and expands both layouts and reads the
archive comment from the archive file's resource fork.

| | |
| --- | --- |
| Identified by | `SIT!` at +$00 and `rLau` at +$0A of the data fork; version byte 1 or 2 at +$0E. Files of type `SIT!`, creator `SIT!`; extension `.sit` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.StuffItReader` |
| Verified against | StuffIt 1.5.1 on Mac OS 9.0 (version 1)<br>StuffIt Deluxe 4.5 (version 2; the CC0 StuffIt and DiskDoubler test corpora) |
| Sources | No published specification. Other readers (behaviour only): psx-spx's format table, XADMaster |

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

### 1.1 Archive header

The archive is the data fork; it starts with a 22-byte header [Reference: psx-spx, XADMaster]
[Verified: StuffIt 1.5.1, StuffIt Deluxe 4.5]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `SIT!` |
| +$04 | 2 | Root entry count | Members at the top level (version 2) |
| +$06 | 4 | Archive length | Total length of the archive; 0 is accepted as "to the end of the data" |
| +$0A | 4 | Signature 2 | `rLau` |
| +$0E | 1 | Version | 1 or 2 |
| +$0F | 1 | Reserved | Not read |
| +$10 | 4 | First member | Version 2: offset of the first root member. Not read in version 1 |
| +$14 | 2 | Reserved | Not read |

### 1.2 Member header

Each member starts with a 112-byte header [Reference: psx-spx, XADMaster] [Verified: StuffIt 1.5.1, StuffIt Deluxe 4.5]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Resource method | Low 4 bits: the method ([stuffit-methods.md §1.1](../codecs/stuffit-methods.md#11-method-numbers)). `$10` or `$80`: encrypted. `$20`: folder start; `$21`: folder end |
| +$01 | 1 | Data method | As the resource method |
| +$02 | 1 | Name length | Version 1: 1–63; version 2: 1–31 (0 in a version-1 folder end) |
| +$03 | 63 | Name | Mac OS Roman; only the first *length* bytes count. In version 2 bytes +$30–+$41 hold the links below |
| +$30 | 2 | Child count | Version 2 folder: the number of members in the folder |
| +$32 | 4 | Previous | Version 2: offset of the previous member in the same list |
| +$36 | 4 | Next | Version 2: offset of the next member in the same list, 0 at the end |
| +$3A | 4 | Parent | Version 2: offset of the containing folder, 0 at the top level |
| +$3E | 4 | First child | Version 2 folder: offset of its first member. Not a link in a file (below) |
| +$42 | 4 | File type | |
| +$46 | 4 | Creator | |
| +$4A | 2 | Finder flags | |
| +$4C | 4 | Creation date | Mac date |
| +$50 | 4 | Modification date | Mac date |
| +$54 | 4 | Resource fork length | Expanded |
| +$58 | 4 | Data fork length | Expanded |
| +$5C | 4 | Resource fork compressed length | |
| +$60 | 4 | Data fork compressed length | |
| +$64 | 2 | Resource fork CRC | CRC-16/ARC of the expanded fork ([stuffit-methods.md §2.1](../codecs/stuffit-methods.md#21-fork-length-and-checksum)) |
| +$66 | 2 | Data fork CRC | As the resource fork CRC |
| +$68 | 6 | Reserved | Not read |
| +$6E | 2 | Header CRC | CRC-16/ARC of bytes +$00–+$6D |

The compressed resource fork follows the header, then the compressed data fork. Folder entries carry no fork bytes,
whatever their length fields say [Verified: StuffIt 1.5.1].

Version 2 as StuffIt Deluxe 4.5 writes it [Verified: StuffIt Deluxe 4.5]:

- A folder is a folder-start record, method `$20` in both method bytes, with its first member at +$3E and its member
  count at +$30; its fork length fields hold the totals of its contents.
- A file's +$3E is not a link: Deluxe 4.5 leaves other bytes there (`$00400001` and the like). Only the method byte
  marks a folder.
- The folder's closing record (method `$21`) follows its last member and is in no list.
- A folder's first member's previous link is the folder's own offset; a root member's first previous link is 0; later
  members link to the member before them.

### 1.3 Archive comment

The archive comment is not in the data fork: it is resource `'SitC'` ID 0 in the archive file's resource fork, Mac OS
Roman text [Verified: StuffIt Deluxe 4.5].

## 2. Reading

### 2.1 Recognising the archive

1. The data fork is at least 22 bytes, starts with `SIT!`, has `rLau` at +$0A and 1 or 2 at +$0E.
2. An archive length that is neither 0 nor within the data fork is an error.

### 2.2 Version 1

1. Read member headers one after another from +$16, while a whole header fits before the archive's end.
2. A method byte `$20` (in either method field) starts a folder: push its name. `$21` ends the innermost folder: pop
   it. Neither has fork bytes.
3. Otherwise the member is a file in the current folder path. Its resource fork's compressed bytes follow the header,
   then the data fork's. Decode each by its method and check its CRC
   ([stuffit-methods.md §2.1](../codecs/stuffit-methods.md#21-fork-length-and-checksum)).
4. The next member starts after the file's compressed forks.
5. Folders still open at the end are an error in the archive.

[Verified: StuffIt 1.5.1]

### 2.3 Version 2

1. Start with the root list: the member at +$10 and the root entry count.
2. Read the list's members by following each member's next link, as many as the list's count.
3. A member with method `$20` is a folder: its list is the member at its first-child link (+$3E) with its child count
   (+$30). Read it with the folder's name added to the path.
4. Otherwise the member is a file: decode its forks as in §2.2 step 3.
5. Each member's previous link should be the member before it in the list, or for a folder's first member the
   folder's own offset; its parent link should be the folder whose list it is in (0 at the top level).

[Reference: XADMaster] [Verified: StuffIt Deluxe 4.5]

### 2.4 Comment

When the archive file has a resource fork, read resource `'SitC'` 0 and decode it as Mac OS Roman (§1.3).

## 3. Writing

None.

## 4. Variants

- Version 1 (StuffIt 1.0–1.5) and version 2 (StuffIt 1.6–4.5) differ only in how members are found: in sequence with
  folder start and end entries, or linked by offset (§1.2).
- StuffIt 1.5.1 leaves stale lengths in a folder-end entry: data lengths 32 and 256 in three of its four sample
  archives, as in the folder-start entry [Verified: StuffIt 1.5.1].
- Encryption sets bit 7 of a method byte (`$8D`: encrypted method 13; `$80`: encrypted stored, padded to 16-byte
  blocks), as StuffIt Deluxe 4.5 writes it [Verified: StuffIt Deluxe 4.5]; older archives set bit 4 (`$10`)
  [Reference: XADMaster].
- StuffIt 5 is a different format ([stuffit5.md](stuffit5.md)); StuffIt split files are in
  [stuffit-segments.md](stuffit-segments.md); a self-extracting archive's data fork is one of these archives
  ([sea.md](sea.md)).

## 5. ClassicMac

- The version-2 reader follows the next and first-child links; a previous or parent link that does not match is
  reported and the member read anyway. [ClassicMac]
- A list that ends (next link 0) before its declared count is reported; the members read so far are kept. A link to a
  member already visited is an error. [ClassicMac]
- Encrypted entries are reported and skipped; no password is asked for. An entry with a method ClassicMac does not
  decode ([stuffit-methods.md §8](../codecs/stuffit-methods.md#8-not-covered)) is reported and skipped, not returned
  as an empty file. [ClassicMac]
- A header CRC mismatch is a warning and the member is read; a fork CRC mismatch is an error and the decoded fork is
  kept. [ClassicMac]
- In version 1, the lengths of folder entries are ignored, a trailing `PEnd` after the last member is skipped, and
  folders left open at the end are reported. Fewer than 112 bytes after the last member are ignored; a longer
  remainder that is not a whole member is an error. [ClassicMac]
- A version-1 name longer than 63 bytes or a version-2 name outside 1–31 is an error. Names are kept as Mac OS Roman
  bytes. [ClassicMac]
- The comment is reported as an `archive.comment` diagnostic, not returned as a file. [ClassicMac]
- `ContainerReadOptions.MaxVolumeEntries` limits the members and each folder's count;
  `MaxExpandedBytesPerInput` limits the archive and the total of its expanded forks. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.comment` | Info | The archive file has a non-empty `'SitC'` 0 resource | Reports its text | Shows the comment |
| `archive.compression-unsupported` | Warning | A file uses a method ClassicMac does not decode | Skips the file | Not traced |
| `archive.count-mismatch` | Warning | A version-2 list ends before its declared count | Keeps the members read | Not traced |
| `archive.encrypted` | Warning | A file's method byte has bit 4 or bit 7 set | Lists it in the message and skips it | Asks for the password |
| `archive.folder-unclosed` | Warning | A version-1 archive ends with folders open | Keeps the files read | Not traced |
| `archive.fork-crc` | Error | An expanded fork's CRC-16 does not match | Keeps the decoded fork | Not traced |
| `archive.header-crc` | Warning | A member header's CRC does not match | Reads the member | Not traced |
| `archive.parent-link-mismatch` | Warning | A version-2 member's parent link is not its folder | Reads the member in the folder it was reached from | Not traced |
| `archive.previous-link-mismatch` | Warning | A version-2 member's previous link is not the member before it | Reads the member | Not traced |

## 7. Verification

- `TestData/StuffIt151` (StuffIt 1.5.1 on Mac OS 9.0, a synthetic file set; `StuffIt151OriginalTests`): four version-1
  archives with folder start and end entries, methods 0, 1, 2 and 3. Every file expands with both forks, Finder type,
  creator and flags, and dates, with no diagnostic; three of the four have the stale folder-end lengths of §4.
- `TestData/StuffItLegacy45/StuffItDeluxe45.sit` (CC0 StuffIt corpus, StuffIt Deluxe 4.5): version-2 traversal of its
  six entries; method 13 for both forks of `testfile.PICT` and the resource fork of `Test Image`
  (`StuffItFeatureTests`).
- `TestData/StuffItLegacy45/StuffItDeluxe45WithComment.sit` and `StuffItDeluxe45CommentAppleDouble.bin` (the corpus's
  `.comment.sit` and its AppleDouble companion): the comment from `'SitC'` 0, as Mac OS Roman.
- `TestData/DiskDoublerOriginal/StuffIt45DiskDoubler377DdaFiles.sit` (CC0 DiskDoubler corpus, StuffIt Deluxe 4.5):
  a version-2 archive with a folder; the folder-start record, its first-child and count fields, the previous links and
  the closing record of §1.2 (`DiskDoublerFeatureTests`).
- `TestData/StuffItOriginalCrossVersion/testfile.stuffit45_dlx.mac9.password.sit` (CC0, StuffIt Deluxe 4.5 with a
  password): all six entries are reported as `archive.encrypted` and none is returned (`StuffItOriginalLayoutTests`).
- `SelfExtractingArchiveTests`: the Deluxe 4.5 archive as the data fork of an `APPL`/`aust` file.
- Hand-built archives in `StuffItFeatureTests`: a version-2 stored file with both forks and Finder information;
  previous and parent links right and wrong, nested and in sequence; version-1 sequential members with folder
  markers, and a folder end without a folder; a missing `'SitC'` resource.

## 8. Not covered

- Encrypted entries.
- Signatures other than `SIT!` that other readers accept for this layout.
- Folders within folders in an archive made by StuffIt itself; StuffIt versions other than 1.5.1 and Deluxe 4.5.

## 9. References

1. psx-spx, the StuffIt format table, <https://psx-spx.consoledev.net/psx-spx.pdf>. Documentation; licence not
   recorded.
2. XADMaster (The Unarchiver), `XADStuffItParser.m`. LGPL-2.1; reference only.
3. Stephan Sokolow, *StuffIt Test Files*, <https://github.com/ssokolow/stuffit-test-files>, and *DiskDoubler Test
   Files*, <https://github.com/ssokolow/diskdoubler-test-files>. CC0 test corpora.
