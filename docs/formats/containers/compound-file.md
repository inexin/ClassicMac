# Compound files (OLE2 structured storage)

Microsoft's compound file is a small file system in one file: storages (folders) and streams (files) in fixed-size
sectors, linked through a file allocation table. Word 6 and later keep their documents in one, on the Macintosh as on
Windows (Word 6 for the Macintosh `'W6BN'`, Word 98 `'W8BN'`), and so do Excel and PowerPoint. ClassicMac reads the
directory and the streams, for the Word reader ([word-binary.md](../documents/word-binary.md)).

| | |
| --- | --- |
| Identified by | The first 8 bytes `D0 CF 11 E0 A1 B1 1A E1` |
| ClassicMac | Reads; `ClassicMac.Core` (`CompoundFile`) |
| Verified against | Nothing yet: fixtures built from the specification |
| Sources | Microsoft's [MS-CFB] (the format's author) |

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

All values are **little-endian**. The file is a 512-byte header, then sectors: sector *n* starts at (*n* + 1) ×
sector size. [Author: [MS-CFB] §2.2]

### 1.1 Header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 8 | Signature | `D0 CF 11 E0 A1 B1 1A E1` |
| +$08 | 16 | CLSID | Zero |
| +$18 | 2 | Minor version | `$003E` |
| +$1A | 2 | Major version | 3 or 4 |
| +$1C | 2 | Byte order | `$FFFE` |
| +$1E | 2 | Sector shift | 9 (512-byte sectors) in version 3, 12 (4096) in version 4 |
| +$20 | 2 | Mini sector shift | 6 (64-byte mini sectors) |
| +$22 | 6 | Reserved | Zero |
| +$28 | 4 | Directory sectors | Version 4 only; zero in version 3 |
| +$2C | 4 | FAT sectors | How many |
| +$30 | 4 | First directory sector | |
| +$34 | 4 | Transaction signature | |
| +$38 | 4 | Mini stream cutoff | `$1000`: streams smaller than this are in the mini stream |
| +$3C | 4 | First mini FAT sector | |
| +$40 | 4 | Mini FAT sectors | How many |
| +$44 | 4 | First DIFAT sector | |
| +$48 | 4 | DIFAT sectors | How many |
| +$4C | 436 | DIFAT | The first 109 FAT sectors' numbers |

In version 4 the rest of the first 4096-byte sector is zeros. [Author: [MS-CFB] §2.2]

### 1.2 Sector numbers

| Value | Meaning |
| --- | --- |
| `$00000000`–`$FFFFFFFA` | A sector |
| `$FFFFFFFC` | A DIFAT sector (in the FAT) |
| `$FFFFFFFD` | A FAT sector (in the FAT) |
| `$FFFFFFFE` | End of a chain |
| `$FFFFFFFF` | Free; in a directory entry, no entry |

[Author: [MS-CFB] §2.1]

### 1.3 FAT, DIFAT and mini FAT

- The FAT is an array of sector numbers, one per sector: entry *n* is the sector after sector *n* in its chain. Its own
  sectors are listed by the header's DIFAT, then by DIFAT sectors: each holds sector size ÷ 4 − 1 FAT sector numbers
  and, last, the next DIFAT sector. Each FAT sector is marked `$FFFFFFFD` in the FAT, each DIFAT sector `$FFFFFFFC`.
  [Author: [MS-CFB] §2.3, §2.5]
- The mini FAT is a chain of sectors holding the same kind of array for 64-byte mini sectors of the mini stream, which
  is the root entry's stream. [Author: [MS-CFB] §2.4]

### 1.4 Directory

A chain of sectors of 128-byte entries; entry 0 is the root. [Author: [MS-CFB] §2.6]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 64 | Name | UTF-16, null-terminated |
| +$40 | 2 | Name length | In bytes, the terminator included |
| +$42 | 1 | Type | 0 unused, 1 storage, 2 stream, 5 root |
| +$43 | 1 | Colour | 0 red, 1 black |
| +$44 | 4 | Left sibling | Entry number, or `$FFFFFFFF` |
| +$48 | 4 | Right sibling | Entry number, or `$FFFFFFFF` |
| +$4C | 4 | Child | A storage's first entry: the root of its children's tree |
| +$50 | 16 | CLSID | |
| +$60 | 4 | State bits | |
| +$64 | 8 | Created | FILETIME |
| +$6C | 8 | Modified | FILETIME |
| +$74 | 4 | Starting sector | The data's first sector, or mini sector for a small stream; for the root, the mini stream's |
| +$78 | 8 | Size | In bytes; version 3 uses only the low 4 bytes |

A storage's children form a red-black tree by name (compared by length, then upper-cased) through the sibling links,
under its child link. [Author: [MS-CFB] §2.6.1, §2.6.4]

## 2. Reading

1. Check the signature, byte order, version and sector shifts (§1.1).
2. Gather the FAT sectors: the header's DIFAT entries, then each DIFAT sector's, up to the header's count; read the FAT.
3. Read the mini FAT along its chain.
4. Read the directory along its chain; read the root's stream: the mini stream.
5. Walk the tree from the root's child: each entry's left subtree, the entry (and a storage's children), its right
   subtree.
6. A stream smaller than the cutoff is read in mini sectors along the mini FAT, any other in sectors along the FAT.

[Author: [MS-CFB] §2]

## 3. Writing

None.

## 4. Variants

Version 3 has 512-byte sectors and 32-bit stream sizes; version 4 has 4096-byte sectors. [Author: [MS-CFB] §2.2]

## 5. ClassicMac

- A chain is followed until its stream's size is read. A chain that ends early, leaves the FAT, points past the file
  or comes back to a sector already read is cut there; the rest of the stream is zeros. [ClassicMac]
- A sibling or child link that is past the directory, or that leads to an entry already read, is left out. The tree's
  colours and order are not checked. [ClassicMac]
- Entries are found by their path, names compared ignoring case. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `cfb.bad-chain` | Warning | A stream's chain ends early, loops, or leaves the FAT or the file | Cuts the stream there; zeros for the rest | Not traced |
| `cfb.bad-directory` | Warning | A directory link is past the directory or repeats an entry, or a directory sector is past the file | Leaves it out | Not traced |
| `cfb.bad-fat` | Warning | Fewer FAT sectors are listed than the header says, or one is not marked as a FAT sector | Reads what is listed | Not traced |

A file whose header is not a compound file's, or whose first directory entry is not a root, throws
`InvalidDataException`.

## 7. Verification

`tests/ClassicMac.Core.Tests/CompoundFileTests.cs` builds compound files byte by byte (`CompoundFileBuilder.cs`):
streams in sectors and in the mini stream, an empty stream, storages, version 4, more than 109 FAT sectors through a
DIFAT sector, a bad header, a looping chain and a bad directory link. No real Microsoft file has been read yet.

## 8. Not covered

- Writing compound files.
- Property set streams (`\005SummaryInformation`) and the creation and modification times.

## 9. References

1. Microsoft, *[MS-CFB]: Compound File Binary File Format*, protocol revision 12.0 (2024), §2.1–§2.6.
