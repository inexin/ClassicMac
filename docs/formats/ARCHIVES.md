# Classic Mac archives

This document records the archive formats implemented by `ClassicMac.Files.Archives`. StuffIt and Compact Pro have no
published vendor specifications in the project references; rules derived from another reader or from test fixtures are
provisional and marked **[Fitted]**. An independently generated fixture proves the documented behavior, not that the
original Mac application writes every detail the same way.

## StuffIt 1.x–2.x (initial subset)

Legacy archives start with `SIT!`, the root entry count, total archive length, and `rLau`. Version 1 uses sequential
112-byte member headers beginning at offset 22; version 2 uses linked member records and the root offset at +16.
Member headers hold the two fork methods, a Pascal-style byte name, Finder type/creator/flags, dates, expanded and
compressed fork lengths, fork CRCs, and the header CRC at +110. In version 1, method `$20` starts a folder and `$21`
ends it; folder members have no fork payload. Version 1 names can be up to 63 bytes and are decoded as MacRoman.
Version 2 currently accepts names up to 31 bytes and follows first-child, next, and declared child-count fields.
These layouts and folder markers are **[Fitted]** against the published format table in
[psx-spx](https://psx-spx.consoledev.net/psx-spx.pdf) and the independent
[XADMaster parser](https://sources.debian.org/src/unar/1.1-2/XADMaster/XADStuffItParser.m/). Hand-built feature tests
cover stored forks, Finder metadata, dates, nested v1 folder markers, and malformed folder structure. Neither version
has yet been verified against an archive made by the original application. Archive-level comments and method 6 are
not implemented; encrypted entries are reported and skipped.

## PackIt (stored, Huffman, and encrypted entries)

PackIt is a flat stream of entries with no archive header. `PMag` starts an uncompressed entry, `PEnd` ends the archive,
and the 94-byte entry metadata follows the four-byte signature. It holds a 63-byte Pascal name field, Finder type,
creator and flags, data- and resource-fork lengths, and creation/modification dates. The data fork, resource fork and a
CRC-16/XMODEM of their concatenation follow; the metadata also has a CRC-16/XMODEM **[Fitted]** against
[psx-spx's PackIt format notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/) and
[XADMaster's PackIt reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADPackItParser.m/).
ClassicMac extracts stored `PMag`, Huffman-compressed `PMa4`, XOR-encrypted Huffman `PMa5`, and DES-encrypted Huffman
`PMa6` records. Passwords are supplied through `ContainerReadOptions.ArchivePassword` as MacRoman. The `PMa5` key is
expanded from the first eight password bytes using PackIt's PC-1 selection table and cycles over seven key bytes.
`PMa6` uses the first eight password bytes, zero-padded, as a DES key; the Huffman stream is transformed in ECB mode.
Both encrypted methods pad their ciphertext to an 8-byte boundary before the next entry. The stream transformations are
**[Fitted]** against [XADMaster's PackIt reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADPackItParser.m/)
and the published [PackIt format notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/).
Header and fork CRC mismatches are reported; a failed encrypted fork checksum rejects the file as a bad password or
damaged ciphertext. Other markers are reported as unsupported and stop parsing at that record. Tests cover stored and
Huffman entries with both forks, Finder metadata and dates, CRC diagnostics, correct and wrong XOR/DES passwords,
weak DES keys, encrypted stream alignment, unsupported methods, and truncated headers. Original-application verification
remains.

## DiskDoubler (DDA2)

The DDA2 archive header is 62 bytes; its big-endian checksum at +60 is CRC-16/XMODEM over bytes 0–59 **[Fitted]**
against [XADMaster's parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/). A bad header
checksum prevents recognition and makes direct reads fail. Records begin with `DDA2`, a record type, a 31-byte Pascal
name field, a directory depth, and the record's total byte length. Directory records carry Mac creation and
modification dates.
File records contain a `0xABCD0054` file header with expanded and stored fork lengths, per-fork methods, dates, Finder
type/creator/flags, checksums and delta-method fields. ClassicMac reads DDA2 folder paths, method-0 stored forks,
method-1 MacCompress LZW forks, method-2 adaptive Huffman forks, method-4 Huffman forks, method-6 AD2 forks,
method-8 Compact Pro compatible forks, method-9 AD1 forks and method-10 DDn forks. Methods 6 and 9 use ADn blocks:
each block has a 12-byte XOR-checked header, expands to at most 8 KiB, and is either raw or LZSS-coded with literal,
near/far offset and length tokens **[Fitted]** against
[XADMaster's ADn decoder](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerADnHandle.m/) and
checked against original DiskDoubler Pro 4.1.1 AD1 and AD2 standalone files. Method 2 maintains 256 adaptive trees,
selecting the next tree by the previous decoded byte; it
uses the optional fitted `0x5A` output transform selected by Info1 and Info2 and a decoded-byte-sum checksum. This
behavior is **[Fitted]** against [XADMaster's parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/)
and [method-2 decoder](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerMethod2Handle.m/). Method 1 uses a
three-byte prefix, variable 9–16-bit LZW codes, block-mode dictionary resets and an optional fitted `0x5A` output
transform selected by Info1 and Info2. Its 16-bit checksum includes the decoded fork and the decoded prefix bytes.
Method 4 uses the tree-described Huffman stream also used by StuffIt and the same optional `0x5A` output transform;
its 16-bit checksum is the decoded fork byte sum. Method 8 has a 16-byte prefix; a zero byte sum selects LZH followed
by RLE, otherwise the fork is RLE-only. Other compression and delta methods are diagnosed and skipped while parsing
continues at the next bounded record. Method-8 forks are checked with CRC-16/IBM.

The older `DDAR` archive has a 78-byte archive header and fixed 124-byte entry headers, followed by stored data and
resource forks. Its directory and end-directory markers build folder paths; redundant standalone file headers found
after records are skipped. These layouts are **[Fitted]** against [XADMaster's DiskDoubler parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/).

A standalone compressed file starts with the same `0xABCD0054` file header and stores its compressed data and resource
forks after the 84-byte header. Its checksum at +82 covers bytes 0–81; older files with a zero checksum are accepted
**[Fitted]** against XADMaster. ClassicMac extracts methods 0, 1, 2, 4, 6, 8, 9 and 10 from standalone files as it does from
DDA2 entries, preserving Finder metadata and deriving the Mac filename from the host name (a `.dd` suffix is removed).
Methods 6 (`AD2`) and 9 (`AD1`) use the ADn block decoder described above; both original-app files expand to the
uncompressed data and resource forks in the CC0 corpus.
Method 10 (`DDn`) is block-based: each block has a 22-byte header, an XOR header check, an expanded-output XOR check,
and separate offset, literal, and length streams. The offset and length streams use canonical Huffman codes; literals
may be raw or Huffman-coded, and matches refer to prior output. This layout is **[Fitted]** against
[XADMaster's DDn decoder](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerDDnHandle.m/) and checked
against a standalone DD3 file produced by DiskDoubler Pro 4.1.1 in the CC0
[DiskDoubler Test Files corpus](https://github.com/ssokolow/diskdoubler-test-files); the expanded data and resource
forks match the uncompressed corpus originals. Methods 3, 5, 7 and delta processing are diagnosed and skipped.

The record layout is **[Fitted]** against [XADMaster's DiskDoubler parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADDiskDoublerParser.m/).
Tests use hand-built records to check DDAR stored forks and directory markers, and DDA2 stored, MacCompress, adaptive Huffman, Huffman
and method-8 fork bytes, including LZW dictionary references, variable-width transitions, block-mode reset, XOR
variants, checksum mismatch reporting, Finder metadata, dates, nested paths, unsupported-method recovery, truncation,
invalid folder depth, entry limits, standalone files and their header checksums, standalone fork methods, and
unsupported standalone delta processing. Original DiskDoubler Pro 4.1.1 AD1, AD2 and DD3 standalone files verify
methods 9, 6 and 10 against both fork outputs; their compressed payloads are also tested inside DDA2 records.
Method-0 fork checksums are not verified. DDA2 compression methods other than 0, 1, 2, 4, 6, 8, 9 and 10 remain
unsupported; standalone methods 3, 5 and 7 and delta processing remain unsupported. Original
application interoperability for DDA2 archives remains unverified.

## Compact Pro (RLE and LZH subset)

A Compact Pro archive begins with an 8-byte header: marker `$01`, volume number, a 16-bit cross-volume field, and a
big-endian offset to the directory. At that offset are a raw CRC-32 state, the total entry count, a Pascal comment,
and a flattened sequence of entries. The checksum covers the entry count, comment length and bytes, and every entry
record. A directory record has its high name-length bit set and carries a count of all descendant entries; a file
record carries its volume, fork-data offset, Finder type/creator/flags, dates, combined fork checksum, method flags,
expanded fork lengths, and compressed fork lengths. Resource bytes precede data bytes. These fields are **[Fitted]**
against the [Compact Pro format description](https://docs.rs/crate/compact-pro/latest) and
[XADMaster's independent reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADCompactProParser.m/).

Method flag bit 0 marks encryption, bit 1 selects LZH+RLE for the resource fork, and bit 2 selects LZH+RLE for the
data fork. The reader extracts both RLE-only and LZH+RLE forks. LZH uses an 8 KiB history window, three
per-block canonical Huffman trees, MSB-first bits, literal tokens, and length/displacement matches; its output is
then passed through RLE. Blocks switch after the documented token-count threshold and discard alignment bits before
reading the next trees **[Fitted]** against
[psx-spx's Compact Pro notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/). Tests cover both
fork encodings, literal and overlapping-match LZH tokens, a block boundary, escaped bytes, repeat runs, nested
directory paths, archive comments, checksums, default unwrapping, and directory/data overlap rejection. Comments are
reported as MacRoman display text in an `archive.comment` information diagnostic **[Fitted]**. Encrypted entries and
multi-volume sets remain unsupported; no original Compact Pro application archive has yet been verified.

## StuffIt 5 (initial subset)

StuffIt 5 is recognized by the eight-byte `StuffIt ` signature and version byte 5 at offset 82. The 100-byte archive
header stores its total length at 84, the root member count at 92, and the first root member's absolute offset at 94.
An archive member begins with `A5 A5 A5 A5`; the `u16` at +6 gives its header length. The member header has flags at +9,
Mac creation and modification seconds at +10 and +14, the next sibling offset at +22, the name byte count at +30, and
the header CRC at +32. These field positions and encodings are **[Fitted]** by comparison with
[Deark's StuffIt reader](https://github.com/jsummers/deark/blob/master/modules/stuffit.c).

The member flag `$40` denotes a folder and `$20` denotes an encrypted member **[Fitted]**. Folder headers store the
first child offset at +34 and child count at +46. A file header stores logical and compressed data-fork lengths at
+34/+38, data-fork CRC at +42, and compression method at +46. The UTF-8 name follows the fixed fields (and any
password bytes). File-specific Finder information starts after the variable header: resource-fork presence is flag bit
0 of its `flags2`; type, creator and Finder flags follow. When present, resource-fork lengths, CRC and method precede
resource bytes, which precede data-fork bytes. These member details are **[Fitted]** against Deark's independent
parser and the hand-built vectors in `StuffItFeatureTests`; they have not yet been checked against a corpus created by
the original StuffIt application.

ClassicMac currently extracts methods 0 (stored), 1 (RLE90), 2 (Compress/LZW), 3 (Huffman), 5 (LZAH), 8 (MW),
13 (LZ + Huffman), 14 (Installer), and 15 (Arsenic) for either fork. RLE90 emits ordinary bytes as
literals; `$90 00` emits a literal `$90`; `$90 n` for nonzero `n` repeats the previously decoded byte until the run has
`n` copies **[Fitted]**. Truncated runs, runs without a prior byte, output-length mismatches and extents outside the
archive are rejected. Per-fork CRC mismatches are reported as errors while retaining the decoded file. Encrypted
entries and unsupported methods are reported and omitted; no password is requested. Files and folders are returned
through the normal `ContainerUnwrapper` pipeline, preserving UTF-8 paths, Finder type/creator/flags, dates and both
forks.

Method 2 uses the Compress-style LZW stream: codes are least-significant-bit first, begin at 9 bits and grow to 14;
in block mode, code 256 clears the dictionary and the remainder of its eight-code group is skipped before reading
again **[Fitted]**. Decoder vectors cover dictionary reset, width growth, the full 14-bit table, the LZW next-code
case, invalid codes and declared output-length checks. The method dispatch and block-mode parameters are cross-checked
against [XADMaster's StuffIt parser](https://sources.debian.org/src/unar/1.8.1-3/XADMaster/XADStuffItParser.m/) and
[Compress decoder](https://sources.debian.org/src/unar/1.8.1-3/XADMaster/XADCompressHandle.m/); vectors are
hand-built and have not yet been checked against an archive created by the original StuffIt application.

Method 3 stores its prefix tree at the start of the compressed fork, most-significant-bit first. A `1` bit introduces
a leaf followed by its 8-bit symbol; a `0` bit introduces an internal node whose zero subtree precedes its one
subtree **[Fitted]**. The declared fork length determines how many symbols to decode. Tests cover both forks, both
branches, a truncated tree and truncated symbol data. The bitstream layout is cross-checked against
[XADMaster's StuffIt Huffman reader](https://sources.debian.org/src/unar/1.1-2/XADMaster/XADStuffItHuffmanHandle.m/);
hand-built vectors have not yet been checked against archives made by the original StuffIt application.

Method 5 is LZSS with a 4 KiB pre-seeded history window and one adaptive sibling-property Huffman tree for literals and
match lengths. Symbols 0–255 are literals; symbols 256–313 represent lengths 3–60. A match then carries a prefix code
for the upper six offset bits and six raw bits for the lower offset bits, most-significant-bit first. The window seed
and output length are independent of the fork bitstream. Tests exercise literals, the seeded space run, a maximum-
distance reference, truncated input and long forks that trigger adaptive-tree renormalization at root frequency
`0x8000`. The offset-prefix lengths are **[Fitted]** against
[macutils' method-5 decoder](https://sources.debian.org/src/macutils/2.0b3-17/macunpack/de_lzah.c/) and hand-built
fixtures; no original-application archive has yet been verified.

Method 8 is the StuffIt MW (Miller–Wegman) dictionary method. Codes are read least-significant-bit first, begin at
nine bits, and grow as the dictionary reaches powers of two. Literal codes emit one byte; later codes refer to
dictionary phrases assembled from earlier phrases. The next-free code ends a group, after which decoding restarts
with a fresh nine-bit dictionary **[Fitted]** against
[XADMaster's MW decoder](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADStuffItOldHandles.m/) and hand-built
vectors. Tests cover phrase reconstruction, group reset, width growth and invalid/truncated input. These vectors do
not establish compatibility with files produced by the original StuffIt application.

Method 13 reads its control and Huffman-coded symbols least-significant-bit first. It supports preset code tables
and dynamically described literal/length and distance tables, followed by LZ references. Its table data is
transcribed from compcol's MIT-licensed method-13 tables (see `THIRD-PARTY-NOTICES.md`); the decoder is an independent
implementation. The CC0 StuffIt Deluxe 4.5 corpus vectors cover preset tables, dynamic tables, both forks, and exact
decoded bytes or CRC-16 checks. The corpus has aliased code alphabets; a separate hand-built vector covers distinct
literal/length trees and a distance-one back-reference. Wider validation against archives from different StuffIt
versions remains useful.

Method 14 is the block-based LZ+Huffman codec associated with StuffIt Installer Maker. A v5 fork begins with a
little-endian block count. Each block carries its compressed and expanded byte lengths, then separate 308-symbol
literal/length and 75-symbol distance trees. The tree descriptions can store lengths directly or through a recursively
described Huffman tree. Matches use a zero-initialized 256 KiB sliding window that carries across blocks. This layout
is **[Fitted]** against
[XADMaster's method-14 reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADMaster/XADStuffItOldHandles.m/).
Hand-built vectors cover literals, matches, direct and recursively encoded tree lengths, multiple blocks, both forks,
and truncated block bounds; no original-application method-14 archive has yet been verified.

Method 15 begins with arithmetic-coded `As` and a block-size selector. Each block carries its randomized flag and BWT
primary index, followed by adaptive arithmetic-coded selector/MTF symbols and zero-run coding. Decoding applies the
inverse BWT, optional bit de-randomization, and the final repeat-byte RLE, then checks the stream's CRC-32. The model
parameters and range-coder behavior follow Matthew T. Russotto's published
[method-15 description](http://www.russotto.net/arseniccomp.html); the randomization table is transcribed from
compcol's MIT-licensed interoperability data (see `THIRD-PARTY-NOTICES.md`). The decoder is independently written.
Vectors use original StuffIt Deluxe 6.5.1 archives from the CC0 test corpus and assert exact data- and resource-fork
bytes, including randomized blocks. The v5 member layout and per-fork integration are **[Fitted]** against those
original-application archives.

The v1–4 record layout, archive-level comments, complete folder metadata and
verification against original-application archives remain unimplemented. See Phase 10 in [the project plan](../PLAN.md).
