# DiskDoubler

Contents

1. [DiskDoubler (DDA2)](#1-diskdoubler-dda2)
2. [DiskDoubler split files (`SPLT`)](#2-diskdoubler-split-files-splt)

## 1. DiskDoubler (DDA2)

Original-application coverage: DiskDoubler 3.7.7 writes methods 1 (DiskDoubler A), 8 (DiskDoubler B), 9 (AutoDoubler
A, `ad`) and 6 (AutoDoubler B, `ads`); DiskDoubler Pro 4.1.1 writes 9 (AD1), 6 (AD2) and 10 (DD1, DD2, DD3, told
apart by the file header's +60 byte, 1–3). Every file of the CC0 corpus (standalone files, 3.7.7 `DDAR` combines, Pro
4.1.1 `DDA2` archives and their `.sea`/`.prompt.sea` copies, BinHex and StuffIt-wrapped copies) expands to the source
forks **[Verified]**, including the StuffIt 6.5.1 (method 15) copies. No sample uses methods 2–5 or 7 or a nonzero delta type. An empty fork is stored as no bytes
whatever its method (DiskDoubler 3.7.7 writes no method-1 prefix or method-8 header for one) and its checksum is 0
**[Verified]**. Pro 4.1.1 writes 0 in the fork checksum fields of methods 6, 9 and 10. Its split files are read (§2).

The DDA2 archive header is 62 bytes; its big-endian checksum at +60 is CRC-16/XMODEM over bytes 0–59 **[Fitted]**
against [XADMaster's parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/). A bad header
checksum prevents recognition and makes direct reads fail. Records begin with `DDA2`, a record type, a 31-byte Pascal
name field, a directory depth, and the record's total byte length. Directory records carry Mac creation and
modification dates.
File records contain a `0xABCD0054` file header with expanded and stored fork lengths, per-fork methods, dates, Finder
type/creator/flags, checksums and delta-method fields. ClassicMac reads DDA2 folder paths and all currently identified
method IDs 0 through 10: stored, MacCompress LZW (1), adaptive Huffman (2), RLE (3), Huffman (4), adaptive-tree Huffman
(5), AD2 (6), Stac LZS (7), Compact Pro-compatible (8), AD1 (9) and DDn (10) forks. Methods 6 and 9 use ADn blocks:
each block has a 12-byte XOR-checked header, expands to at most 8 KiB, and is either raw or LZSS-coded with literal,
near/far offset and length tokens **[Fitted]** against
[XADMaster's ADn decoder](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerADnHandle.m/) and
checked against original DiskDoubler Pro 4.1.1 AD1 and AD2 standalone files. Method 2 maintains 256 adaptive trees,
selecting the next tree by the previous decoded byte; it
uses the optional fitted `0x5A` output transform selected by Info1 and Info2 and a decoded-byte-sum checksum. This
behavior is **[Fitted]** against [XADMaster's parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/)
and [method-2 decoder](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerMethod2Handle.m/). Method 1 uses a
three-byte prefix, variable 9–16-bit LZW codes packed continuously across width changes, block-mode dictionary resets
with alignment after clear codes, and an optional fitted `0x5A` output transform selected by Info1 and Info2. Its
16-bit checksum includes the decoded fork and the decoded prefix bytes. The LZW packing and clear behavior follow
[XADMaster's MacCompress handle](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADCompressHandle.m/); both forks of
an authentic DiskDoubler 3.7.7 standalone file verify method 1.
Method 3 uses `0x44` as an escape: an escape followed by zero emits a literal `0x44`; a nonzero count repeats the
previous decoded byte `count - 1` more times. This layout is **[Reference]** based on the explicitly untested RLE
decoder in [macutils' DiskDoubler reader](https://sources.debian.org/src/macutils/2.0b3-17/macunpack/dd.c/) and its
[escape definition](https://sources.debian.org/src/macutils/2.0b3-17/macunpack/dd.h/). The reader requires decoded
data to match the fork's declared length and rejects truncated escapes, repeats without a preceding byte, and
output that exceeds or falls short of that length. Method 4 uses the tree-described Huffman stream also used by StuffIt and the same optional `0x5A` output transform;
its 16-bit checksum is the decoded fork byte sum **[Fitted]** against
[XADMaster's parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/), with hand-built
vectors and no original-app fixture yet. Method 8 has a 16-byte prefix; a zero byte sum selects LZH followed
by RLE, otherwise the fork is RLE-only. Delta type 1 applies a byte-wise cumulative sum modulo 256 after fork
decompression **[Reference]**, with hand-built vectors and no original-app fixture yet. Delta type 2 cumulatively sums three interleaved byte lanes, with each lane wrapping modulo 256. This
is **[Reference]** based on the `dd_delta3` routine in
[macutils' DiskDoubler reader](https://sources.debian.org/src/macutils/2.0b3-17/macunpack/dd.c/). The reader transforms
only the bytes present in a final partial group; that tail handling is inferred from the lane layout and has no
original-app fixture yet. Other delta types and unsupported compression methods are diagnosed and skipped while
parsing continues at the next bounded record. Fork checksums are checked on decompressed bytes before delta
preprocessing; method-3 has no checksum rule established by the available reference and is not included in fork
checksum validation; method-8 forks use CRC-16/IBM.

The older `DDAR` archive has a 78-byte archive header and fixed 124-byte entry headers, followed by stored data and
resource forks. Its directory and end-directory markers build folder paths; redundant standalone file headers found
after records are skipped. These layouts are **[Fitted]** against [XADMaster's DiskDoubler parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/).

A standalone compressed file starts with the same `0xABCD0054` file header and stores its compressed data and resource
forks after the 84-byte header. Its checksum at +82 covers bytes 0–81; older files with a zero checksum are accepted
**[Fitted]** against XADMaster. ClassicMac extracts methods 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 and 10 from standalone files as it does from
DDA2 entries, preserving Finder metadata and deriving the Mac filename from the host name (a `.dd` suffix is removed).
Methods 6 (`AD2`) and 9 (`AD1`) use the ADn block decoder described above; both original-app files expand to the
uncompressed data and resource forks in the CC0 corpus.
Method 10 (`DDn`) is block-based: each block has a 22-byte header, an XOR header check, an expanded-output XOR check,
and separate offset, literal, and length streams. The offset and length streams use canonical Huffman codes; literals
may be raw or Huffman-coded, and matches refer to prior output. This layout is **[Fitted]** against
[XADMaster's DDn decoder](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerDDnHandle.m/) and checked
against a standalone DD3 file produced by DiskDoubler Pro 4.1.1 in the CC0
[DiskDoubler Test Files corpus](https://github.com/ssokolow/diskdoubler-test-files); the expanded data and resource
forks match the uncompressed corpus originals. Method 8 also matches both forks of an authentic DiskDoubler 3.7.7
standalone file against the corpus's uncompressed originals. Method 5 reads a leading adaptive-tree count (zero means 256), then
uses the method-2 adaptive Huffman stream with decoded symbols selecting the next tree modulo that count. This layout
is **[Fitted]** against [XADMaster's method-5 handling](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/)
and has hand-built feature vectors; original-application interoperability remains unverified. Method 7 uses the
Stac LZS stream grammar from [RFC 1974](https://www.rfc-editor.org/rfc/rfc1974) plus a six-byte preamble, an
entry-counted dictionary area, and input/output XOR transforms fitted to
[XADMaster's DiskDoubler wrapper](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/).
The fork checksum is the XOR of expanded bytes with the even-length `0xff` correction fitted to
[XADMaster's XOR-sum handle](https://github.com/MacPaw/XADMaster/blob/master/XADXORSumHandle.m). Tests cover literal
and backreference streams, checksum parity, and malformed input; an original-app fixture remains. Method 3 is tested
with escaped literals, repeated bytes, malformed codes, and declared-length mismatches; original-app verification
remains unavailable. Delta type 2 has hand-built vectors for lane ordering, modulo wraparound, and one- and two-byte
tails; original-app verification remains unavailable. Delta types other than 0, 1, and 2 are diagnosed and skipped.

The record layout is **[Fitted]** against [XADMaster's DiskDoubler parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/).
Original DiskDoubler Pro 4.1.1 DDA2 archives can contain entry-type `0x1000` records whose payload does not use the
standard file-header layout. In the corpus archive noted below, these records use a 44-byte metadata block followed by
the raw data and resource forks. The metadata includes creation and modification times, Finder information and fork
lengths. This layout is **[Fitted]** to the original Pro 4.1.1 JPEG and PNG entries; their checksum fields are
described next. Tests verify both image signatures, fork lengths and Finder type/creator values.
Each DDA2 record's fixed part ends in a CRC-16/XMODEM of the record bytes before it: at +54 in a file record (just
before its `0xABCD0054` file header), +86 in a directory record and +88 in a raw `0x1000` record; byte +40 of a raw
record's metadata is the XOR of its data-fork bytes. These are **[Fitted]** to every record of the five DiskDoubler
Pro 4.1.1 archives in the CC0 corpus (not read from code). A record CRC mismatch is a warning (`archive.header-crc`),
a raw data XOR mismatch an error (`archive.fork-checksum`).
Tests use hand-built records to check DDAR stored forks and directory markers, and DDA2 stored, MacCompress, adaptive
Huffman (methods 2 and 5), method-3 RLE literals and repeats, Huffman, Stac LZS literals and backreferences, and method-8 fork bytes, including LZW dictionary references, variable-width transitions, block-mode reset, XOR
variants, checksum mismatch reporting, Finder metadata, dates, nested paths, unsupported-method recovery, truncation,
invalid folder depth, entry limits, standalone files and their header checksums, standalone fork methods, and
delta types 1 and 2, and unsupported standalone delta types. Original DiskDoubler Pro 4.1.1 AD1, AD2 and DD3 standalone files verify
methods 9, 6 and 10 against both fork outputs; their compressed payloads are also tested inside DDA2 records. An
original DiskDoubler 3.7.7 standalone files verify methods 1 and 8 against both fork outputs.
An original Pro 4.1.1 DDA2 archive from the CC0 corpus verifies extraction of both `testfile.PICT` forks against the
uncompressed source files, plus its raw `testfile.jpg` and `testfile.png` entries. Broader original-application archive
interoperability remains unverified.
Method-0 and method-3 fork checksums are not verified. DDA2 compression methods other than 0, 1, 2, 3, 4, 5, 6, 7,
8, 9 and 10 remain unsupported; delta types other than 0, 1 and 2 remain unsupported. Methods 3, 5 and 7, and
delta type 2, have no
original-app fixtures yet.

## 2. DiskDoubler split files (`SPLT`)

DiskDoubler's Split command (3.7.7 and Pro 4.1.1) cuts a file into parts named `name.1`, `name.2` …, each a 94-byte
header and then a slice of the source's data fork followed by its resource fork. Header fields (big-endian):

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `SPLT` |
| 4 | 4 | Set identifier, the same in every part (Pro 4.1.1 `$0002xxxx`, 3.7.7 `$0000xxxx`) |
| 8 | 4 | Data-fork length |
| 12 | 4 | Resource-fork length |
| 16 | 16 | Finder info (`FInfo`) |
| 32 | 4 | Creation date |
| 36 | 4 | Modification date |
| 40 | 2 | Part count |
| 42 | 2 | Part index, from 0 |
| 44 | 4 | Payload length (the part's size less 94) |
| 48 | 2 | CRC-16/XMODEM of the payload |
| 50 | 40 | Zero |
| 90 | 4 | `SPLT` |

The header carries no name: the reassembled file takes the host name less its `.N` extension. The reader opens a set
from any part and takes as siblings the files of the same name whose bytes 4–41 match; it reports a missing part as
`archive.missing-volume` (returning nothing rather than truncated forks), and a payload CRC mismatch as
`archive.fork-checksum`, keeping the data. The reassembled file (a `DDA2`/`DDAR` archive or a `.sea`) unwraps as
usual. **[Fitted]** to the CC0 DiskDoubler corpus's 3.7.7 (`sources.dd377.ad.dd.1`/`.2`) and Pro 4.1.1
(`sources.ddpro411.ad1.dd.1`/`.2`, `sources.ddpro411.ad1.sea.1`/`.2`) sets: the payloads concatenate to the corpus's
unsplit file and its resource fork. Not covered: BinHex-wrapped parts (`.1.hqx`), whose siblings are not unwrapped
before matching; the meaning of the identifier's bytes.
