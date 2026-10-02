# StuffIt 5

The archive format of StuffIt 5 to 7 (Aladdin Systems, from 1998), also written by StuffIt for Windows; extension
`.sit`. Its members are linked by offset, hold UTF-8 names, and compress each fork with one of the StuffIt methods
([stuffit-methods.md](../codecs/stuffit-methods.md)). ClassicMac lists and expands it with folders, both forks, Finder
information and dates.

| | |
| --- | --- |
| Identified by | `StuffIt ` (8 bytes, with the space) at +$00 and version byte 5 at +$52 of the data fork. Files of type `SIT5`, creator `SIT!`; extension `.sit` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.StuffItReader` |
| Verified against | StuffIt Deluxe 6.5 and 7.0 for Macintosh, Mac OS 9 and Mac OS X variants (CC0 StuffIt corpus)<br>StuffIt Deluxe 6.5.1 (CC0 StuffIt and DiskDoubler corpora)<br>DropStuff 7.0.3 (StuffIt Standard 7.0.3) on Mac OS 9.0<br>StuffIt 7.0 for Windows (CC0 StuffIt corpus) |
| Sources | No published specification. Other readers (behaviour only): Deark |

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

A 100-byte header [Reference: Deark] [Verified: StuffIt Deluxe 6.5, 7.0]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 82 | Signature text | Starts with `StuffIt ` |
| +$52 | 1 | Version | 5 |
| +$53 | 1 | Reserved | Not read |
| +$54 | 4 | Archive length | Total length; 0 is accepted |
| +$58 | 4 | First root member | Offset |
| +$5C | 2 | Root member count | |
| +$5E | 4 | First root member (copy) | The same as +$58, except as below |
| +$62 | 2 | Reserved | Not read |

When StuffIt Deluxe 7.0 prepends a member (its return receipt, `StuffItReturnReceipt.txt`), +$58 points to the
receipt, whose next link is the old first member, while +$5E still points to the old first member; reading from +$5E
would lose the receipt [Verified: StuffIt Deluxe 7.0].

### 1.2 Member header

Every member, file or folder, starts with this header; its length is at +$06 [Reference: Deark]
[Verified: StuffIt Deluxe 6.5, 7.0, DropStuff 7.0.3]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `$A5A5A5A5` |
| +$04 | 1 | Member version | 1 in every Mac StuffIt (Deluxe 6.5 and 7.0, DropStuff 7.0.3); 3 in StuffIt 7.0 for Windows |
| +$05 | 1 | Reserved | Not read |
| +$06 | 2 | Header length | From +$00 to the end of the name and anything after it |
| +$08 | 1 | Reserved | Not read |
| +$09 | 1 | Flags | `$40`: folder. `$20`: encrypted |
| +$0A | 4 | Creation date | Mac date |
| +$0E | 4 | Modification date | Mac date |
| +$12 | 4 | Reserved | Not read |
| +$16 | 4 | Next | Offset of the next member in the same list |
| +$1A | 4 | Reserved | Not read |
| +$1E | 2 | Name length | Bytes |
| +$20 | 2 | Header CRC | CRC-16/ARC of the whole header with these two bytes taken as zero |
| +$22 | 4 | Data fork length / first child | File: expanded data fork length. Folder: offset of its first member |
| +$26 | 4 | Data fork compressed length | File only |
| +$2A | 2 | Data fork CRC | File only: CRC-16/ARC of the expanded fork ([stuffit-methods.md §2.1](../codecs/stuffit-methods.md#21-fork-length-and-checksum)) |
| +$2C | 2 | Reserved | Not read |
| +$2E | 1 | Data fork method / child count | File: the method ([stuffit-methods.md §1.1](../codecs/stuffit-methods.md#11-method-numbers)). Folder: +$2E–+$2F are the `u16` member count |
| +$2F | 1 | Password length | File only: bytes of password data that follow at +$30 |
| +$30 | n | Password data, then name | The name is UTF-8, without `:` |

The header length may leave bytes after the name; they are not read.

### 1.3 Resource fork information

Right after a header comes a block of Finder information, 36 bytes in a version-1 member and 32 in a version-3
member [Verified: StuffIt Deluxe 6.5, 7.0, DropStuff 7.0.3, StuffIt 7.0 for Windows]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Flags 2 | Bit 0: the file has a resource fork |
| +$02 | 2 | Reserved | Not read |
| +$04 | 4 | File type | A Windows member holds Windows data (`$00000020`) |
| +$08 | 4 | Creator | As the type |
| +$0C | 2 | Finder flags | |
| +$0E | 22 or 18 | Reserved | Not read |

When flags 2 bit 0 is set, the resource fork's fields follow:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Resource fork length | Expanded |
| +$04 | 4 | Resource fork compressed length | |
| +$08 | 2 | Resource fork CRC | CRC-16/ARC of the expanded fork |
| +$0A | 2 | Reserved | Not read |
| +$0C | 1 | Resource fork method | |
| +$0D | 1 | Password length | Bytes of password data that follow |

Then come the compressed resource fork and the compressed data fork [Reference: Deark] [Verified: StuffIt Deluxe 6.5,
7.0].

### 1.4 Folders

A folder header is followed by the same Finder block as a file. Right after it StuffIt writes the folder's end
marker: a 48-byte folder header with no name, first child `$FFFFFFFF` and no Finder block. The folder's first member
follows the marker, and the folder's last member's next link points back to the marker
[Verified: DropStuff 7.0.3].

## 2. Reading

1. Check the signature and version (§1.1). An archive length past the data fork is an error.
2. Start with the root list: the member at +$58 and the count at +$5C.
3. For each list, read as many members as its count, following each member's next link (+$16).
4. A member with flag `$40` is a folder: its list is the member at +$22 with the count at +$2E. Read it with the
   folder's name added to the path. Counting members, never reaching past the count, keeps the reader off the
   folder's end marker (§1.4).
5. Otherwise the member is a file. After its header, read the Finder block (§1.3) and, if flags 2 bit 0 is set, the
   resource fork fields. The resource fork's compressed bytes follow, then the data fork's.
6. Decode each fork by its method and check its CRC
   ([stuffit-methods.md §2.1](../codecs/stuffit-methods.md#21-fork-length-and-checksum)).

[Reference: Deark] [Verified: StuffIt Deluxe 6.5, 7.0, DropStuff 7.0.3]

## 3. Writing

None.

## 4. Variants

- StuffIt 7.0 for Windows writes version-3 members, whose Finder block is 32 bytes (§1.3) [Verified: StuffIt 7.0 for
  Windows].
- StuffIt Deluxe 7.0's return receipt (§1.1) [Verified: StuffIt Deluxe 7.0].
- The Mac OS 9 and Mac OS X archive variants of Deluxe 6.5 and 7.0 read alike [Verified: StuffIt Deluxe 6.5, 7.0].
- StuffIt X (`.sitx`, signature `StuffIt!`, type `SITX`) is a different format.
- The older StuffIt 1.x–4.x format is [stuffit.md](stuffit.md); a self-extracting StuffIt 5 archive is
  [sea.md](sea.md).

## 5. ClassicMac

- A member's forks are bounded by its next link only when that link points forward (§1.4); otherwise by the end of
  the archive. [ClassicMac]
- A list that ends (next link 0) before its count is reported and the members read so far kept. A link to a member
  already visited, a header length outside 48–2000, a name that is not UTF-8 or that is empty or holds `:` is an
  error. [ClassicMac]
- An entry with the encrypted flag or password data is reported and skipped; no password is asked for. An entry
  with a method ClassicMac does not decode is reported and skipped. [ClassicMac]
- A header CRC mismatch is a warning; a fork CRC mismatch is an error and the decoded fork is kept. [ClassicMac]
- Files keep their UTF-8 names and paths; the Mac OS Roman name has `?` for a name that does not convert.
  [ClassicMac]
- StuffIt X archives are not recognised (on request only). [ClassicMac]
- `ContainerReadOptions.MaxVolumeEntries` limits the members and each folder's count; `MaxExpandedBytesPerInput`
  limits the archive and the total of its expanded forks. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.compression-unsupported` | Warning | A file uses a method ClassicMac does not decode | Skips the file | Not traced |
| `archive.count-mismatch` | Warning | A list ends before its declared count | Keeps the members read | Not traced |
| `archive.encrypted` | Warning | A file is encrypted | Lists it in the message and skips it | Asks for the password |
| `archive.fork-crc` | Error | An expanded fork's CRC-16 does not match | Keeps the decoded fork | Not traced |
| `archive.header-crc` | Warning | A member header's CRC does not match | Reads the member | Not traced |

## 7. Verification

- `TestData/StuffItOriginalCrossVersion` (CC0 StuffIt corpus; `StuffItFeatureTests`):
  `testfile.stuffit651_dlx.mac9.sit`, `testfile.stuffit651_dlx.macx1.sit`, `testfile.stuffit7_dlx.mac9.sit` and
  `testfile.stuffit7_dlx.macx1.sit` (Deluxe 6.5 and 7.0, Mac OS 9 and Mac OS X variants): all six members listed and
  their data forks, both forks of `testfile.PICT` and the resource fork of `Test Image`, byte for byte. Which method
  each member uses is not part of the test.
- `TestData/StuffItOriginalCrossVersion/testfile.stuffit7_dlx.mac9.rreceipt.sit` (Deluxe 7.0): the return receipt is
  the first root member (`StuffItOriginalLayoutTests`).
- `TestData/StuffItOriginalCrossVersion/testfile.stuffit7.win.sit` (StuffIt 7.0 for Windows): version-3 members with
  a 32-byte Finder block in a `sources` folder; its `testfile.txt` ends in LF.
- `TestData/StuffIt703/S703b.sit` and `S703f.sit` (DropStuff 7.0.3 on Mac OS 9.0, a synthetic file set): a folder as
  the one root member, with `Folder:Inner` inside it; every file with both forks and Finder information; the end
  marker before the folder's first member, and the last member's link back to it. `S703x.sitx` is not taken for a
  StuffIt 5 archive.
- `TestData/StuffItMethod15/StuffItDeluxe651.sit` (Deluxe 6.5.1): the end-to-end container of the method-15 vectors.
- Hand-built archives in `StuffItFeatureTests`: a stored file with both forks and Finder information, folder paths,
  UTF-8 names, encrypted and unsupported entries, header and fork CRC mismatches, forks past the archive, a root
  offset and fork length past the input, a member cycle, an invalid UTF-8 name, the entry limit, the signature and
  version probe.

## 8. Not covered

- Encrypted entries.
- StuffIt X (`.sitx`).
- The fields marked "Not read".

## 9. References

1. Deark (Jason Summers), `modules/stuffit.c`, <https://github.com/jsummers/deark>. MIT-style licence; reference only.
2. Stephan Sokolow, *StuffIt Test Files*, <https://github.com/ssokolow/stuffit-test-files>. CC0 test corpus.
