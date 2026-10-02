# DiskDoubler

The formats of Salient's DiskDoubler and AutoDoubler (later Symantec's DiskDoubler Pro): files compressed in place
(standalone files), `DDA2` archives, the older `DDAR` archives (DiskDoubler 3.7.7's combines), and the split files of
its Split command. A compressed file keeps its data and resource forks, each coded by one of eleven methods with an
optional delta filter. ClassicMac reads all four kinds and methods 0–10.

| | |
| --- | --- |
| Identified by | `DDA2` at +$00 with a valid header CRC; `DDAR` at +$00; `$ABCD0054` at +$00 (a standalone file); `SPLT` at +$00 and +$5A (a split part) |
| ClassicMac | Reads; `ClassicMac.Files.Archives.DiskDoublerReader`, `DiskDoublerSplitReader` |
| Verified against | DiskDoubler 3.7.7 and DiskDoubler Pro 4.1.1 (the CC0 DiskDoubler Test Files corpus) |
| Sources | No published specification. Other readers (behaviour only): XADMaster, macutils; RFC 1974 (Stac LZS) |

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

### 1.1 File header

A compressed file's 84-byte header, which starts a standalone file and sits inside each `DDA2` file record
[Reference: XADMaster] [Verified: DiskDoubler 3.7.7, Pro 4.1.1]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Magic | `$ABCD0054` |
| +$04 | 4 | Data fork length | Expanded |
| +$08 | 4 | Data fork compressed length | |
| +$0C | 4 | Resource fork length | Expanded |
| +$10 | 4 | Resource fork compressed length | |
| +$14 | 1 | Data fork method | Low 7 bits (§2.4) |
| +$15 | 1 | Resource fork method | Low 7 bits |
| +$16 | 1 | Info 1 | Selects the output XOR (§2.4) |
| +$17 | 1 | Reserved | Not read |
| +$18 | 4 | Modification date | Mac date |
| +$1C | 4 | Creation date | Mac date |
| +$20 | 4 | File type | |
| +$24 | 4 | Creator | |
| +$28 | 2 | Finder flags | |
| +$2A | 6 | Reserved | Not read |
| +$30 | 2 | Data fork checksum | By method (§2.4) |
| +$32 | 2 | Resource fork checksum | By method |
| +$34 | 1 | Info 2 | Selects the output XOR |
| +$35 | 1 | Reserved | Not read |
| +$36 | 2 | Data fork delta type | 0, 1 or 2 (§2.12) |
| +$38 | 2 | Resource fork delta type | |
| +$3A | 24 | Reserved | Not read, except +$3C: DD1, DD2 or DD3 (1–3) in a Pro 4.1.1 method-10 file [Verified: DiskDoubler Pro 4.1.1] |
| +$52 | 2 | Header CRC | CRC-16/XMODEM of +$00–+$51, or 0 in older files |

In a standalone file the compressed data fork follows the header, then the compressed resource fork. The host
file's name, less `.dd`, is the file's name.

### 1.2 `DDA2` archive

A 62-byte archive header, then records [Reference: XADMaster]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `DDA2` |
| +$04 | 56 | Reserved | Not read |
| +$3C | 2 | Header CRC | CRC-16/XMODEM of +$00–+$3B |

Each record starts with a 46-byte fixed part [Reference: XADMaster] [Verified: DiskDoubler Pro 4.1.1]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `DDA2` |
| +$04 | 2 | Entry type | `$BBBB`: the end of the archive (6 bytes in all). Bit 15 set: a folder. `$1000`: a raw file (§1.3). Otherwise a file |
| +$06 | 1 | Name length | 1–31 |
| +$07 | 31 | Name | Mac OS Roman |
| +$26 | 4 | Depth | The folder level plus 2 |
| +$2A | 4 | Record length | From +$00 |

A file record continues [Reference: XADMaster] [Fitted: DiskDoubler Pro 4.1.1 archives]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$2E | 8 | Reserved | Not read |
| +$36 | 2 | Record CRC | CRC-16/XMODEM of +$00–+$35 |
| +$38 | 84 | File header | §1.1; its own +$52 CRC is not checked here |
| +$8C | | Forks | The compressed data fork, then the compressed resource fork |

A folder record continues with 16 bytes of metadata at +$2E, among them Mac creation and modification dates
[Reference: XADMaster], and a record CRC at +$56, CRC-16/XMODEM of +$00–+$55 [Fitted: DiskDoubler Pro 4.1.1
archives].

### 1.3 Raw `DDA2` records

DiskDoubler Pro 4.1.1 writes entry type `$1000` for files it stored without its file header: a 44-byte metadata block
at +$2E, then the data fork and the resource fork, uncompressed [Fitted: DiskDoubler Pro 4.1.1 archives]. Offsets are
from +$2E:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 8 | Reserved | Not read |
| +$08 | 4 | Creation date | Mac date |
| +$0C | 4 | Modification date | Mac date |
| +$10 | 4 | File type | |
| +$14 | 4 | Creator | |
| +$18 | 2 | Finder flags | |
| +$1A | 6 | Reserved | Not read |
| +$20 | 4 | Data fork length | |
| +$24 | 4 | Resource fork length | |
| +$28 | 1 | Data fork check | XOR of the data fork's bytes |
| +$29 | 1 | Reserved | Not read |
| +$2A | 2 | Record CRC | CRC-16/XMODEM of record bytes +$00–+$57 |

### 1.4 `DDAR` archive

A 78-byte archive header, not read, then 124-byte records, each followed by its data fork and resource fork, stored
[Reference: XADMaster] [Verified: DiskDoubler 3.7.7]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `DDAR` |
| +$04 | 4 | Reserved | Not read |
| +$08 | 1 | Name length | Up to 63 |
| +$09 | 63 | Name | Mac OS Roman |
| +$48 | 1 | Folder | Nonzero: this record starts a folder |
| +$49 | 1 | Folder end | Nonzero: this record ends the innermost folder |
| +$4A | 4 | Data fork length | |
| +$4E | 4 | Resource fork length | |
| +$52 | 4 | Creation date | Mac date |
| +$56 | 4 | Modification date | Mac date |
| +$5A | 4 | File type | |
| +$5E | 4 | Creator | |
| +$62 | 2 | Finder flags | |
| +$64 | 24 | Reserved | Not read |

An 84-byte file header (§1.1) may follow a record's forks; it repeats the file and is skipped.

### 1.5 Split parts

DiskDoubler's Split command cuts a file into parts named `name.1`, `name.2` …, each a 94-byte header, then a slice of
the source's data fork followed by its resource fork [Fitted: DiskDoubler 3.7.7 and Pro 4.1.1 split sets]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Magic | `SPLT` |
| +$04 | 4 | Set identifier | The same in every part (Pro 4.1.1 `$0002xxxx`, 3.7.7 `$0000xxxx`) |
| +$08 | 4 | Data fork length | Of the whole file |
| +$0C | 4 | Resource fork length | Of the whole file |
| +$10 | 16 | Finder information | `FInfo` |
| +$20 | 4 | Creation date | Mac date |
| +$24 | 4 | Modification date | Mac date |
| +$28 | 2 | Part count | |
| +$2A | 2 | Part index | From 0 |
| +$2C | 4 | Payload length | The part's size less 94 |
| +$30 | 2 | Payload CRC | CRC-16/XMODEM of the payload |
| +$32 | 40 | Reserved | Zero |
| +$5A | 4 | Magic | `SPLT` |

The header carries no name.

## 2. Reading

### 2.1 Standalone files

1. Check the magic and the header CRC (§1.1): it must match or be 0.
2. Decode each fork (§2.4), check its checksum, then apply its delta filter (§2.12).

[Reference: XADMaster] [Verified: DiskDoubler 3.7.7, Pro 4.1.1]

### 2.2 `DDA2` archives

1. Check the archive header's CRC (§1.2).
2. Read records from +$3E until the `$BBBB` end record.
3. A depth below 2 is skipped. Otherwise depth − 2 is the record's folder level: the open folders deeper than that are
   closed; a level deeper than the open folders is an error.
4. A folder record opens a folder at its level.
5. A raw record (§1.3) gives a file with its forks as stored.
6. A file record gives a file: decode its forks from its file header (§2.4), check the checksums, apply the deltas.
7. Check each record's CRC (file +$36, folder +$56 when the record is at least 88 bytes, raw +$58).
8. The next record starts at the record length.

[Reference: XADMaster] [Verified: DiskDoubler Pro 4.1.1]

### 2.3 `DDAR` archives

1. Read records from +$4E to the end of the data.
2. A record starting with `$ABCD0054` is a repeated file header: skip 84 bytes.
3. A folder-end record closes the innermost folder; a folder record opens one; any other record is a file whose forks
   follow, data then resource.

[Reference: XADMaster]

### 2.4 Fork methods

| Method | Name | Steps | Checksum (+$30/+$32) |
| --- | --- | --- | --- |
| 0 | Stored | Compressed and expanded lengths are equal | Not checked |
| 1 | MacCompress (DiskDoubler A) | §2.5 | 16-bit sum of the three prefix bytes and the output |
| 2 | Adaptive Huffman | §2.6 | 16-bit sum of the output |
| 3 | RLE | §2.7 | None |
| 4 | Huffman | StuffIt's method 3 ([stuffit-methods.md §2.4](../codecs/stuffit-methods.md#24-method-3-huffman)) | 16-bit sum of the output |
| 5 | Adaptive Huffman, several trees | §2.6 | 16-bit sum of the output |
| 6 | AD2 (AutoDoubler B) | §2.10 | Not checked |
| 7 | Stac LZS | §2.8 | XOR of the output (§2.8) |
| 8 | Compact Pro (DiskDoubler B) | §2.9 | CRC-16/ARC of the output |
| 9 | AD1 (AutoDoubler A) | §2.10 | Not checked |
| 10 | DDn (DD1, DD2, DD3) | §2.11 | Not checked |

[Reference: XADMaster; method 3 macutils]

- An empty fork is stored as no bytes whatever its method, and its checksum is 0 [Verified: DiskDoubler 3.7.7].
- Methods 1, 2, 4 and 5 XOR every output byte with `$5A` when info 1 is `$2A` or more and info 2's bit 7 is clear
  [Reference: XADMaster].
- Checksums are taken on the decoded fork, before the delta filter [Reference: XADMaster].

### 2.5 Method 1: MacCompress

1. The fork starts with three bytes; the third, XORed with the output XOR, is the flags: bits 0–4 the maximum code
   width (9–16), bit 7 block mode, bits 5–6 zero.
2. Codes follow, least significant bit first, packed continuously, from 9 bits. The first free code is 257 in block
   mode, 256 otherwise.
3. Decode as LZW ([stuffit-methods.md §2.3](../codecs/stuffit-methods.md#23-method-2-compress-lzw) steps 3–4); add each
   new phrase while below 2^maximum; widen the codes when the next free code reaches 2^width.
4. In block mode code 256 clears the dictionary: advance to the next multiple of (8 × width) bits from the start of
   the codes, go back to 9 bits and a first free code of 257.

[Reference: XADMaster] [Verified: DiskDoubler 3.7.7]

### 2.6 Methods 2 and 5: adaptive Huffman

1. Method 2 keeps 256 trees, method 5 as many as its first byte says (0 means 256); method 5's stream starts after
   that byte.
2. Each tree starts with internal nodes 1–255 and leaves 256–511 (byte value + 256); node n's children are 2n and
   2n + 1.
3. Decode a byte with the current tree, most significant bit first from node 1, then update that tree: from the
   leaf, while its parent is not the root, swap the node with its parent's sibling and move up to the grandparent.
4. The next tree is the decoded byte modulo the number of trees (before the output XOR).

[Reference: XADMaster]

### 2.7 Method 3: RLE

1. A byte other than `$44` is a literal.
2. `$44 $00` is a literal `$44`.
3. `$44 n`, n > 0, adds n − 1 more copies of the last byte.
4. The output must be exactly the expanded length; a run with no byte before it or a final `$44` is an error.

[Reference: macutils, whose reader for it is marked untested]

### 2.8 Method 7: Stac LZS

1. Bytes 0–5 are a preamble; +$06 is a `u32` count n of dictionary entries; the stream starts at 18 + 2n.
2. XOR the stream bytes with `$FF`, decode them as Stac LZS (RFC 1974): bits most significant first; 0 and 8 bits is
   a literal; 1, 1 and 7 bits is a short offset (0 ends the stream); 1, 0 and 11 bits is a long offset; then the
   length code (2 bits for 2–4, then 2 more for 5–7, then 4 more for 8–22, then 4-bit extensions while all ones).
3. XOR the output with `$FF`.
4. The checksum is the XOR of the output bytes; for an even-length fork the stored value is XORed with `$00FF` first.

[Doc: RFC 1974]; the wrapper and checksum [Reference: XADMaster].

### 2.9 Method 8: Compact Pro

1. The fork starts with 16 bytes. If their sum is 0, the rest is Compact Pro LZH followed by its RLE; otherwise RLE
   alone ([compact-pro-rle-lzh.md](../codecs/compact-pro-rle-lzh.md)).
2. The checksum is CRC-16/ARC (CRC-16/IBM) of the output.

[Reference: XADMaster] [Verified: DiskDoubler 3.7.7]

### 2.10 Methods 6 and 9: ADn

A sequence of blocks, each a 12-byte header and its data [Reference: XADMaster] [Verified: DiskDoubler Pro 4.1.1]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Compressed length | The bytes after the header |
| +$02 | 2 | Expanded length | 1–8192 |
| +$04 | 5 | Reserved | Not read |
| +$09 | 1 | Flags | Bit 0: stored |
| +$0A | 1 | Reserved | Not read |
| +$0B | 1 | Header check | XOR of bytes +$00–+$0A |

1. A stored block is copied.
2. Otherwise decode tokens, most significant bit first, until the block's expanded length: a 0 bit and 8 bits is a
   literal; a 1 bit, then 1 and 12 bits or 0 and 8 bits, is a distance into this block; then the length: 0 is 2, 10
   then a bit is 3 or 4, 11 then 4 bits is 5–20. The length is cut to the block's end; a length above the distance or
   a distance before the block's start is an error.

### 2.11 Method 10: DDn

A sequence of blocks, each a 22-byte header and three streams [Reference: XADMaster] [Verified: DiskDoubler Pro
4.1.1]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Expanded length | 1–65,536 |
| +$04 | 2 | Literal count | |
| +$06 | 2 | Offset count | |
| +$08 | 2 | Length stream length | |
| +$0A | 2 | Literal stream length | |
| +$0C | 2 | Offset stream length | |
| +$0E | 1 | Flags | Bit 6: stored (the expanded bytes follow). Bit 7: literals are Huffman-coded |
| +$0F | 4 | Reserved | Not read |
| +$13 | 1 | Block check | XOR of the expanded block |
| +$14 | 1 | Reserved | Not read |
| +$15 | 1 | Header check | XOR of bytes +$00–+$14 |

1. The streams follow the header in the order offsets, literals, lengths.
2. The offset and length streams start with a canonical Huffman code; the literal stream too when flag bit 7 is set,
   otherwise it is the raw literals.
3. Decode the offsets: slots 0–3 are distances 1–4; a slot s ≥ 4 has s / 2 − 1 extra bits after the base
   ((2 + (s & 1)) << extra) + 1.
4. Read length codes: 0 takes the next literal; others give a match with the next offset, copying from earlier output.
5. Check the block's XOR.

### 2.12 Delta filters

Applied to a decoded fork after its checksum [Reference: XADMaster, macutils]:

- Type 1: each byte from the second on adds the byte before it, modulo 256.
- Type 2: the same within three interleaved lanes (bytes 0, 3, 6 …; 1, 4, 7 …; 2, 5, 8 …), each lane from 0. A
  final partial group adds only the bytes present.

### 2.13 Split files

1. Read the part's header; check both magics, a part count above 0, a part index below it and a payload that fits.
2. The siblings are the files named like this part less its `.N` extension whose headers match bytes +$04–+$29.
3. Every part 0 … count − 1 must be present. Join the payloads in index order, checking each payload's CRC.
4. The joined bytes are the data fork, then the resource fork, by the header's lengths. The file takes the host
   name less its `.N` extension, and the header's Finder information and dates.

[Fitted: DiskDoubler 3.7.7 and Pro 4.1.1 split sets]

## 3. Writing

None.

## 4. Variants

- DiskDoubler 3.7.7 writes methods 1 (DiskDoubler A), 8 (DiskDoubler B), 9 (AutoDoubler A, `ad`) and 6
  (AutoDoubler B, `ads`), and `DDAR` combines [Verified: DiskDoubler 3.7.7].
- DiskDoubler Pro 4.1.1 writes methods 9 (AD1), 6 (AD2) and 10 (DD1, DD2, DD3, told apart by file-header byte +$3C,
  1–3), `DDA2` archives with raw `$1000` records, and 0 in the fork checksum fields of methods 6, 9 and 10
  [Verified: DiskDoubler Pro 4.1.1].
- Older standalone files have 0 in place of the header CRC [Reference: XADMaster].
- DiskDoubler 3.7.7's split identifiers are `$0000xxxx`, Pro 4.1.1's `$0002xxxx` [Fitted: DiskDoubler split sets].

## 5. ClassicMac

- A `DDA2` archive whose header CRC does not match is not recognised and cannot be read. [ClassicMac]
- A record CRC mismatch is a warning and the record is read. A fork checksum mismatch, or a raw record's data XOR
  mismatch, is an error and the decoded fork kept. [ClassicMac]
- An entry with a method above 10 or a delta type above 2 is reported and skipped; the archive continues at the next
  record. A standalone file with one is reported and nothing returned. [ClassicMac]
- A `DDA2` archive without its `$BBBB` end record is reported; the entries read are kept. [ClassicMac]
- A standalone file is named from its host name less `.dd`, or "DiskDoubler file" without one; a reassembled split
  file "Untitled" without a host name. [ClassicMac]
- Split parts: a missing part, or payloads shorter than the declared forks, is reported and nothing returned rather
  than truncated forks; two parts with the same index are an error; a payload CRC mismatch is reported and the data
  kept. The reassembled file (a `DDA2` or `DDAR` archive, or a `.sea`) is unwrapped as usual. [ClassicMac]
- `ContainerReadOptions.MaxNestingDepth` limits folder depth; `MaxVolumeEntries` the records;
  `MaxExpandedBytesPerInput` the input, the parts together and the total of the expanded forks. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.fork-checksum` | Error | A fork's sum or XOR checksum (methods 1, 2, 4, 5, 7) does not match; a raw record's data XOR does not match; a split part's payload CRC does not match | Keeps the decoded data | Not traced |
| `archive.fork-crc` | Error | A method-8 fork's CRC-16 does not match | Keeps the decoded fork | Not traced |
| `archive.header-crc` | Warning | A `DDA2` record's CRC does not match | Reads the record | Not traced |
| `archive.method-unsupported` | Warning | A fork method above 10 or a delta type above 2 | Skips the entry (or the standalone file) | Not traced |
| `archive.missing-volume` | Warning | A split part is missing, or the parts end before the declared forks | Returns nothing | Not traced |
| `archive.truncated` | Warning | A `DDA2` archive has no end record | Keeps the entries read | Not traced |

## 7. Verification

- The whole CC0 DiskDoubler Test Files corpus (standalone files, 3.7.7 `DDAR` combines, Pro 4.1.1 `DDA2` archives and
  their `.sea` and `.prompt.sea` copies, BinHex- and StuffIt-wrapped copies, StuffIt 6.5.1 method-15 copies included)
  expands to the source forks. No sample uses methods 2, 3, 4, 5 or 7 or a delta type other than 0. Part of it is
  committed in `TestData/DiskDoublerOriginal` (`DiskDoublerFeatureTests`, `DiskDoublerSplitTests`):
  - `DiskDoubler377DdaTestFile.dd` and `DiskDoubler377DdbTestFile.dd` (3.7.7 standalone, methods 1 and 8): both forks
    of `testfile.PICT` against `ExpectedDataFork.pict` and `ExpectedResourceFork.bin`.
  - `DiskDoublerPro411Ad1TestFile.dd`, `Ad2TestFile.dd`, `Dd1TestFile.dd`, `Dd2TestFile.dd`, `Dd3TestFile.dd` (Pro
    4.1.1 standalone, methods 9, 6 and 10): both forks; their compressed payloads also inside hand-built `DDA2`
    records.
  - `DiskDoublerPro411Dda2Dd1Archive.dd`, `Dd2Archive.dd`, `Dd3Archive.dd` (Pro 4.1.1 `DDA2`, method 10): both forks
    of `testfile.PICT`; the raw `$1000` `testfile.jpg` and `testfile.png` entries (signatures, fork lengths, Finder
    type and creator); every record's CRC (the five Pro 4.1.1 archives of the corpus all match).
  - `StuffIt45DiskDoubler377DdaFiles.sit` (StuffIt Deluxe 4.5 of 3.7.7 method-1 files): every file through both
    containers, including the empty method-1 forks of `Test Image`, `testfile.jpg` and `testfile.png`.
  - `StuffIt651DiskDoublerPro411Ad1Files.sit`, `Ad2Files.sit` (StuffIt 6.5.1 of AD1 and AD2 files): every file through
    both containers.
  - `sources.ddpro411.ad1.dd.1`, `.2` (a Pro 4.1.1 split set): opened from either part, the payloads join to the
    unsplit file and its resource fork. The corpus's 3.7.7 set (`sources.dd377.ad.dd.1`, `.2`) and Pro 4.1.1 `.sea`
    set (`sources.ddpro411.ad1.sea.1`, `.2`) were read the same way and are not committed.
- Hand-built records in `DiskDoublerFeatureTests`: `DDAR` stored forks, folder markers, repeated file headers,
  truncation and the nesting limit; `DDA2` stored, MacCompress (dictionary references, width changes, block-mode
  reset, the XOR variant, checksums), methods 2 and 5 (tree adaptation, tree counts, the XOR), method 3 (escapes,
  runs, malformed codes, length mismatches), Huffman (byte sums, the XOR), method 7 (literals, short and long
  offsets, extended lengths, both forks, truncation, invalid references, the checksum), method 8 (LZH and RLE, the
  checksum, a truncated prefix), ADn and DDn block checks, delta types 1 and 2 (lanes, wraparound, one- and two-byte
  tails, after the checksum), unsupported methods and deltas, Finder information, dates, nested paths, folder depth,
  record CRCs, the raw record's XOR, truncation, the entry limit; standalone files and their header CRC (valid, 0,
  wrong), the host name, every method.
- `DiskDoublerSplitTests`: a part from another set is not taken; a payload CRC mismatch is reported and the data kept;
  both magics are required.

## 8. Not covered

- Methods 2, 3, 4, 5 and 7, and delta types 1 and 2, against files made by DiskDoubler: no sample uses them. Method 3
  has no established checksum.
- Methods above 10 and delta types above 2.
- The meaning of the split set identifier's bytes; BinHex-wrapped split parts (`.1.hqx`), whose siblings are not
  unwrapped before matching.
- The reserved fields.

## 9. References

1. XADMaster (The Unarchiver), `XADDiskDoublerParser.m`, `XADDiskDoublerADnHandle.m`, `XADDiskDoublerDDnHandle.m`,
   `XADDiskDoublerMethod2Handle.m`, `XADCompressHandle.m`, `XADXORSumHandle.m`. LGPL-2.1; reference only.
2. macutils (Dik T. Winter), `macunpack/dd.c` and `dd.h` (method 3, `dd_delta3`). Licence unclear; reference only.
3. RFC 1974, *PPP Stac LZS Compression Protocol* (1996).
4. Stephan Sokolow, *DiskDoubler Test Files*, <https://github.com/ssokolow/diskdoubler-test-files>. CC0 test corpus.
