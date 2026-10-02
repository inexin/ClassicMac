# PackIt

The archive format of Harry Chesley's PackIt and PackIt III: a flat sequence of entries, each a file with both
forks, with no archive header and no folders. Entries may be stored, Huffman-coded or encrypted with a password.
ClassicMac reads all of them, given the password for encrypted entries.

| | |
| --- | --- |
| Identified by | `PMag`, `PMa1`–`PMa7` or `PEnd` at +$00 of the data fork. PackIt 1.0's archives are type `PIT `, creator `PIT ` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.PackItReader` |
| Verified against | PackIt 1.0 on Mac OS 9.0 (stored entries) |
| Sources | Other readers (behaviour only): psx-spx's PackIt notes, XADMaster |

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

### 1.1 Entries

The archive is a sequence of entries, each starting with a four-byte magic, ended by `PEnd`
[Reference: psx-spx, XADMaster] [Verified: PackIt 1.0]:

| Magic | Entry |
| --- | --- |
| `PMag` | Stored |
| `PMa1` | Stored, XOR-encrypted |
| `PMa2` | Stored, DES-encrypted |
| `PMa3` | Reserved |
| `PMa4` | Huffman |
| `PMa5` | Huffman, XOR-encrypted |
| `PMa6` | Huffman, DES-encrypted |
| `PMa7` | Reserved |
| `PEnd` | End of the archive |

A stored entry is the magic, the 94-byte header (§1.2), the data fork, the resource fork, and a 2-byte CRC-16/XMODEM
of the data fork followed by the resource fork (0 for an empty file). In a Huffman entry everything after the magic
(header, forks and fork CRC) is one Huffman-coded stream (§2.2). An encrypted entry encrypts everything after the
magic, padded to a multiple of 8 bytes.

[Reference: psx-spx, XADMaster]; `PMag` and `PEnd` [Verified: PackIt 1.0].

### 1.2 Entry header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 64 | Name | `Str63`: a length byte, 1–63, and 63 bytes; only the first *length* bytes count |
| +$40 | 4 | File type | |
| +$44 | 4 | Creator | |
| +$48 | 2 | Finder flags | |
| +$4A | 2 | Locked | Not read |
| +$4C | 4 | Data fork length | |
| +$50 | 4 | Resource fork length | |
| +$54 | 4 | Creation date | Mac date |
| +$58 | 4 | Modification date | Mac date |
| +$5C | 2 | Header CRC | CRC-16/XMODEM of bytes +$00–+$5B |

Offsets are from the byte after the magic. [Reference: psx-spx, XADMaster] [Verified: PackIt 1.0]

## 2. Reading

### 2.1 The entry sequence

1. At each entry, read the magic. `PEnd` ends the archive.
2. `PMag`: read the header, the data fork, the resource fork and the fork CRC; the next entry follows.
3. `PMa4`: decode the Huffman stream (§2.2) for the 94 header bytes, the data fork, the resource fork and the two
   CRC bytes. The next entry starts at the next whole byte.
4. `PMa1`, `PMa2`, `PMa5`, `PMa6`: decrypt (§2.3), then read as `PMag` or `PMa4`. The next entry starts after the
   encrypted bytes rounded up to a multiple of 8.
5. Check the header CRC and the fork CRC (CRC-16/XMODEM: polynomial `$1021`, initial value 0, not reflected).

[Reference: psx-spx, XADMaster]

### 2.2 Huffman

The stream is the tree and code of StuffIt's method 3
([stuffit-methods.md §2.4](../codecs/stuffit-methods.md#24-method-3-huffman)): the tree first, then one symbol per byte,
most significant bit first, with no end symbol [Reference: XADMaster].

### 2.3 Encryption

The password is taken as Mac OS Roman bytes [Reference: XADMaster]:

- XOR (`PMa1`, `PMa5`): pass the first eight password bytes (zero-padded) through DES's PC-1 bit selection, giving 7
  key bytes. XOR byte i after the magic with key byte i mod 7. In a Huffman entry the XOR applies to the coded bytes.
- DES (`PMa2`, `PMa6`): the first eight password bytes, zero-padded, are the key. The bytes after the magic, in whole
  8-byte blocks, are transformed in ECB mode with DES encryption (not decryption).

The XOR and DES transforms are XADMaster's for `PMa5` and `PMa6`; XADMaster does not read `PMa1` and `PMa2`, whose
pairing of the same transforms with stored entries follows psx-spx's signature table [Reference: psx-spx].

## 3. Writing

None.

## 4. Variants

- PackIt 1.0 writes stored entries only. It leaves whatever was in memory after the name in the 64-byte name field
  [Verified: PackIt 1.0].
- PackIt III writes the Huffman and encrypted entries; it does not run on Mac OS 9, so they are not verified.

## 5. ClassicMac

- The password is `ContainerReadOptions.ArchivePassword`. Without one, the first encrypted entry is reported as
  unsupported and the rest of the archive is not read. [ClassicMac]
- With a password, an encrypted entry whose fork CRC does not match is taken as a wrong password or damaged
  ciphertext: the archive read fails. [ClassicMac]
- `PMa3`, `PMa7` and any other magic are reported as unsupported, and reading stops there. [ClassicMac]
- A header or fork CRC mismatch in an unencrypted entry is reported and the entry kept. [ClassicMac]
- DES keys that .NET calls weak are accepted. [ClassicMac]
- An archive without `PEnd` is reported; the entries read are kept. [ClassicMac]
- `ContainerReadOptions.MaxVolumeEntries` is checked before each entry is decoded; `MaxExpandedBytesPerInput` limits
  the archive and the total of its forks. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.fork-crc` | Error | An entry's fork CRC does not match | Keeps the entry | Not traced |
| `archive.header-crc` | Error | An entry's header CRC does not match | Keeps the entry | Not traced |
| `archive.method-unsupported` | Warning | A reserved or unknown magic, or an encrypted entry with no password given | Stops reading the archive | Not traced |
| `archive.truncated` | Warning | The archive has no `PEnd` | Keeps the entries read | Not traced |

## 7. Verification

- `TestData/PackIt10/pk10_plain.pit` (PackIt 1.0 on Mac OS 9.0, the synthetic file set of `TestData/StuffIt151`;
  `PackIt10OriginalTests`): three `PMag` entries and `PEnd`; the 94-byte header, the 64-byte name field with leftover
  bytes after the name, both forks, the fork CRC (0 for the empty file), Finder information and dates; the default
  unwrapper opens it.
- Hand-built entries in `PackItFeatureTests`: stored and Huffman entries with both forks, Finder information and
  dates; header and fork CRC mismatches; right and wrong passwords for stored and Huffman XOR and DES entries; a weak
  DES key; the 8-byte alignment of encrypted entries and the byte alignment of Huffman entries; a missing password;
  unsupported magics; truncated headers, payloads and Huffman data; the entry limit before an over-limit entry is
  decoded.

## 8. Not covered

- `PMa4` and the encrypted entries against archives made by PackIt III.
- `PMa3` and `PMa7`.

## 9. References

1. psx-spx, PackIt format notes, <https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/>.
   Documentation; licence not recorded.
2. XADMaster (The Unarchiver), `XADPackItParser.m`. LGPL-2.1; reference only.
