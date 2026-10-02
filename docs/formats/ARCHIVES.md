# Classic Mac archives

This document records the archive formats implemented by `ClassicMac.Files.Archives`. StuffIt and Compact Pro have no
published vendor specifications in the project references; rules derived from another reader or from test fixtures are
provisional and marked **[Fitted]**. **[Reference]** marks a rule taken from another reader when no original-format
sample verifies it yet. An independently generated fixture proves the documented behavior, not that the original Mac
application writes every detail the same way.

## StuffIt 1.x–4.x (legacy format)

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
[XADMaster parser](https://sources.debian.org/src/unar/1.1-2/XADMaster/XADStuffItParser.m/). Method 6's negative-length
blocks skip Huffman coding and carry PackBits data; ClassicMac decodes those blocks in legacy and v5 archives,
including literal, repeat and no-op controls, and concatenates multiple blocks. This behavior is **[Reference]** based on
[`macutils`' method-6 decoder](https://github.com/dgilman/macutils/blob/master/macunpack/sit.c) and the
[StuffIt-Go method description](https://pkg.go.dev/github.com/ObsoleteMadness/StuffIt-Go/stuffit). Positive-length
blocks decode the fixed 258-leaf Huffman tree, use the block's byte translation table, stop at either end-marker
leaf, then expand the declared PackBits byte stream. Intermediate PackBits data is limited to the historical 32 KiB
method-6 buffer size. Feature tests cover both block forms separately and mixed in one fork, the legacy and v5
containers, and truncated Huffman and PackBits data. An original StuffIt Deluxe 4.5 archive from the CC0 test corpus
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

## StuffIt split files (SegmentIt)

Each segment begins with a 100-byte `$B0 56 00` header. Byte 3 is its one-based volume number; byte 4 and the following
bytes hold the shared MacRoman filename. Bytes 68–93 contain Finder type/creator/flags, creation and modification
dates, and the resource- and data-fork lengths. Segments in a set repeat the filename and metadata; their payloads
concatenate in volume order after each 100-byte header is removed. The reader discovers sibling segments by matching
the filename and shared metadata, opens a set from any segment, restores both forks and Finder metadata, and reports a
missing volume rather than returning truncated forks. When the embedded filename ends in `.sit`, the default unwrapper
continues into the reconstructed StuffIt archive. This layout and sibling matching are **[Fitted]** against
[XADMaster's split-file parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADStuffItSplitParser.m/); feature
tests use hand-built volumes, and an archive made with the original SegmentIt application remains to be verified.

StuffIt 1.5.1's own Other > Segment… command writes the same layout with the magic `$41A7` in place of `$B056`: a
100-byte header (bytes 2–3 the segment number, then the Pascal filename), then up to the segment size minus 100 bytes
of the file, in segment order. The segment files are type `SegM`, creator `SIT!`. Bytes 68–93 hold the source file's
type/creator/flags, dates and fork lengths as above. The bytes after the filename's length and 94–99 are uninitialised
(they differ between segments of one set) and are never read; the Pascal filename may be shorter than the file's name
(the sample set records `non` for `fx151_non.sit`), and the reassembled file takes it as is. Segments of one set must
share the magic, filename and metadata. The reader reassembles a set from any segment and reports a missing segment
as `archive.missing-volume`. This layout is **[Fitted]** to StuffIt 1.5.1's segments of a 41 974-byte archive
(`TestData/StuffIt151/fx151_non.seg1`–`seg5`), not read from its code.

## PackIt (stored, Huffman, and encrypted entries)

PackIt is a flat stream of entries with no archive header. `PMag` starts an uncompressed entry, `PEnd` ends the archive,
and the 94-byte entry metadata follows the four-byte signature. It holds a 63-byte Pascal name field, Finder type,
creator and flags, data- and resource-fork lengths, and creation/modification dates. The data fork, resource fork and a
CRC-16/XMODEM of their concatenation follow; the metadata also has a CRC-16/XMODEM **[Fitted]** against
[psx-spx's PackIt format notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/) and
[XADMaster's PackIt reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADPackItParser.m/).
ClassicMac extracts stored `PMag`, XOR-encrypted uncompressed `PMa1`, DES-encrypted uncompressed `PMa2`,
Huffman-compressed `PMa4`, XOR-encrypted Huffman `PMa5`, and DES-encrypted Huffman `PMa6` records. Passwords are
supplied through `ContainerReadOptions.ArchivePassword` as MacRoman. The XOR key used by `PMa1` and `PMa5` is
expanded from the first eight password bytes using PackIt's PC-1 selection table and cycles over seven key bytes.
`PMa2` and `PMa6` use the first eight password bytes, zero-padded, as a DES key; the payload stream is transformed in
ECB mode. Both encryption schemes pad ciphertext to an 8-byte boundary before the next entry. The stream transformations are
**[Fitted]** against [XADMaster's PackIt reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADPackItParser.m/)
and the published [PackIt format notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/).
`PMa1` and `PMa2` behavior applies those encryption layers to otherwise uncompressed entries; this pairing is fitted
from the published signature table and hand-built vectors because XADMaster only implements the encrypted Huffman
variants and no original-app fixture is available.
Header and fork CRC mismatches are reported; a failed encrypted fork checksum rejects the file as a bad password or
damaged ciphertext. `PMa3` and `PMa7` are reserved and other markers are reported as unsupported, stopping parsing at
that record. Tests cover stored and Huffman entries with both forks, Finder metadata and dates, CRC diagnostics, correct
and wrong passwords for raw and Huffman XOR/DES entries, weak DES keys, encrypted stream alignment, unsupported methods,
truncated headers, and enforcing `MaxVolumeEntries` before decoding an over-limit record.

Stored `PMag` entries and `PEnd` are **[Verified against PackIt 1.0]** (`TestData/PackIt10`, made on Mac OS 9): the
94-byte header (a 64-byte Str63 name field, type, creator, Finder flags, a locked word, data and resource lengths,
creation and modification dates, then the CRC-16/XMODEM of the 92 bytes after the magic), the data fork, the resource
fork and the CRC-16/XMODEM of data then resource (0 for an empty file). PackIt 1.0 leaves whatever was in memory after
the name in its 64-byte field, so the reader uses only the Pascal length's bytes. `PMa4` and the encrypted records
stay **[Fitted]**: PackIt III 1.3, which writes them, does not run on Mac OS 9.

## LHA / LArc (level-0 through level-3 records; stored and compressed methods)

ClassicMac recognizes LHA level-0 through level-3 records and reads uncompressed `-lh0-` files, adaptive-Huffman LZSS
`-lh1-` and `-lh2-`, legacy static-Huffman LZSS `-lh3-`, LArc `-lzs-` and `-lz5-`, plus the newer static-Huffman LZSS `-lh4-`,
`-lh5-`, `-lh6-` and `-lh7-` files. The static-Huffman methods use 4 KiB,
8 KiB, 32 KiB and 64 KiB history windows, respectively, with a maximum 256-byte match **[Fitted]** against the
[LHa for UNIX method table](https://github.com/jca02266/lha/blob/master/src/lha_macro.h) and the independent
[Lhasa `-lh4-`…`-lh7-` decoder](https://github.com/fragglet/lhasa/blob/master/lib/lh_new_decoder.c). Each compressed block begins with a
16-bit command count and three Huffman tables: the code-length table, the literal/match table and the history-offset
table. The position table count uses four bits for `-lh4-`/`-lh5-` and five bits for `-lh6-`/`-lh7-`. The initial
history window is filled with spaces. `-lh1-` uses a 4 KiB adaptive-Huffman LZSS window and match lengths from 3 to 60.
Match copies can overlap and continue from bytes just emitted.
Decoded output must exactly match the declared expanded size; truncated bitstreams, invalid tables and matches beyond
the declared output are rejected. `-lh1-` follows the 4 KiB adaptive-Huffman and offset coding in the
[Lhasa `-lh1-` decoder](https://github.com/fragglet/lhasa/blob/master/lib/lh1_decoder.c); it uses a maximum 60-byte match.
`-lzs-` uses a space-filled 2 KiB ring window and a most-significant-bit-first stream of literal or back-reference
tokens; matches contain an 11-bit ring position and a 4-bit length offset for lengths 2–17. `-lz5-` uses LArc's 4 KiB
window, preset with its format-defined byte pattern, and groups eight literal/back-reference commands under a
least-significant-bit-first flag byte. `-lh2-` uses an 8 KiB space-filled window, a dynamic literal/length tree, and a position tree that grows as output passes each 64-byte boundary; matches are 3–256 bytes. `-lh3-` uses an 8 KiB window and blocks with a 16-bit command count, a
286-symbol literal/length Huffman tree and either a transmitted or ready-made position tree; matches are 3–256 bytes.
The LHa for UNIX sources are behavioral references only; the decoders and their hand-built protocol fixtures are
independently implemented.

A level-0 record's one-byte size is the number of following header bytes, so the full header is that value plus two;
the header checksum covers the declared number of bytes beginning at the method. The header carries method, packed
and expanded sizes, DOS date/time, attributes, level, byte filename, file CRC-16 and an optional OS identifier, then
its payload.
For level 1, the same size and checksum rules apply to a base header with a two-byte next-extension size. Its packed
size field counts the extension bytes plus file payload. Each extension has a type, data and a two-byte size for the
following extension. Type 1 supplies the filename and type 2 the directory; those path components are combined before
the payload is read. Extension records are bounded by the declared skip size and archive extent. `0x00` ends the
archive. Filenames from Mac OS (`m`) archives are retained as MacRoman bytes; entries with other OS identifiers are
skipped because their filename encodings are not known. Both slash forms are treated as folder separators in returned
Mac paths. File CRC mismatches are reported while preserving decoded data. Unsupported compression methods are
diagnosed and skipped using their declared packed length. Level 2 uses a 16-bit total header size, a type-0 header-CRC
extension, and type-1/type-2 filename/directory extensions. Its packed-size field counts payload bytes only, and the
header may have one padding byte. Level 3 uses a 32-bit total header size and 32-bit extension-chain sizes with no
padding; its type-0 header CRC is checked with the stored CRC bytes treated as zero. The reader
applies `MaxExpandedBytesPerInput` to the archive and total expanded file data, and `MaxVolumeEntries` to every valid
archive record, including directories and entries skipped for unsupported compression or filename encoding.

The layout is based on the [LHa for UNIX header description](https://github.com/jca02266/lha/blob/master/header.doc.md)
and the CC0 [Kaitai LHA record specification](https://formats.kaitai.io/lzh/) **[Fitted]** to hand-built Mac OS
records. Tests cover all four supported header levels, MacRoman names and paths, stored data, extended filenames and
directories, extension-chain payload positioning, initial and updated adaptive-Huffman literals, LH2 preset-window matches and position-tree growth, and truncated-code rejection,
LArc literals, preset-window copies and overlapping matches, legacy-Huffman literals and matches using both position
tree forms, multiple LH3 blocks, literal and back-reference tokens in all four newer static-Huffman methods, multiple
compressed blocks, output-length and truncation checks, unwrapper integration, header and
data checksums, unsupported-method and unsupported-encoding continuation, input-size limits, and truncated payloads.

**[Verified against MacLHA 2.24 archives (lhasa test suite)]** (`TestData/MacLha224`): header levels 0, 1 and 2,
`-lh0-`, `-lh1-` and `-lh5-`, all 16 of lhasa's MacLHA archives:
- A level-0 header has no OS identifier: it ends with the CRC (24 + name-length bytes in all). The OS byte is an
  optional extension; an entry without one is read with its name as MacRoman bytes, like an `m` entry.
- A directory extension separates names with `$FF` (LHa's header.doc). A "full" path starts with `$FF` and the volume
  name (`$FF Untitled $FF subdir $FF subdir2 $FF`); the reader keeps the volume name as the top folder, as lhasa does,
  so nothing is lost and the path stays relative.
- Except with "non-Mac", each entry's data is a MacBinary file carrying both forks, Finder info and dates. The LHA
  reader returns it as stored and the default pipeline unwraps it as MacBinary one level down (the LHA entry's folders
  stay on the LHA node and place the Mac file on unpacking); `-lh0-` archives of a MacBinary file that holds a gzip
  file unwrap one level further.
- `-lh1-` (`l0_lh1.lzh`, `l1_lh1.lzh`, `l2_lh1.lzh`) decodes to the same MacBinary file as `-lh5-`. It is LZHUF
  (Okumura): a 314-symbol adaptive tree, rebuilt when the root's count reaches `$8000`, whose initial leaf group's
  leader is its left-most (lowest-index) leaf; the position's upper 6 bits through LZHUF's fixed `d_code`/`d_len`
  table, a canonical code of 1, 3, 8, 12, 24 and 16 codes of 3 to 8 bits (the first read is 3 bits), then 6 raw low
  bits; a space-filled 4 KiB window (positions are relative, so LZHUF's start at 4096−60 changes nothing). lhasa's
  `test/compressed/lh1.bin` decodes to its CRC-32 too.

## DiskDoubler split files (`SPLT`)

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

## DiskDoubler (DDA2)

Original-application coverage: DiskDoubler 3.7.7 writes methods 1 (DiskDoubler A), 8 (DiskDoubler B), 9 (AutoDoubler
A, `ad`) and 6 (AutoDoubler B, `ads`); DiskDoubler Pro 4.1.1 writes 9 (AD1), 6 (AD2) and 10 (DD1, DD2, DD3, told
apart by the file header's +60 byte, 1–3). Every file of the CC0 corpus (standalone files, 3.7.7 `DDAR` combines, Pro
4.1.1 `DDA2` archives and their `.sea`/`.prompt.sea` copies, BinHex and StuffIt-wrapped copies) expands to the source
forks **[Verified]**, including the StuffIt 6.5.1 (method 15) copies. No sample uses methods 2–5 or 7 or a nonzero delta type. An empty fork is stored as no bytes
whatever its method (DiskDoubler 3.7.7 writes no method-1 prefix or method-8 header for one) and its checksum is 0
**[Verified]**. Pro 4.1.1 writes 0 in the fork checksum fields of methods 6, 9 and 10. Its split files are read (see DiskDoubler split files).

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
reported as MacRoman display text in an `archive.comment` information diagnostic **[Fitted]**. A file entry's volume
number and offset select the sibling file holding both compressed forks; the directory-bearing volume remains the
source of the entry table. The normal host-file unwrapping path supplies siblings, and a referenced volume that is not
present is reported as `archive.missing-volume` while other entries continue. Tests cover both forks stored on a
second volume, missing-volume reporting, and unwrapping through the default pipeline. Compact Pro's [*User's Guide*,
“Working With Segmented Archives”](https://oldapplestuff.com/download/Macintosh/Macintosh_Garden/manuals/Compact-Pro-Users-Guide.pdf)
says a segmented set is opened from its final segment and that the application
uses information stored in the segment files to locate them. The same volume-number lookup lets the default pipeline
open a final segment and read both forks from an earlier sibling segment; a feature test covers this path. Segment
discovery depends on the host integration supplying the other segment files as siblings. Encrypted entries remain
unsupported, and an authentic segmented archive from Compact Pro has not yet been verified.

The RLE stage (used alone or after LZH) has a "half-escaped" state: after `81 81` the second `$81` is emitted and is
itself an escape for the next byte, so `81 81 82 05` gives five `$81` and `81 81 81 82 05` six, while `81 81 41` is
`81 81 41`. `81 82 00` gives `81 82`, `81 82 n` adds `n − 1` copies of the last byte (`81 82 01` adds none), and `81 x`
is `81 x` **[Reference: pmarreck/compact_pro, fixed against real archives; munbox samples]**. The LZH stage always
followed this rule; the RLE-only decoder now does too (before, `81 81 x` emitted `81`, then `x`, without a new
escape). munbox's sample archive (`TestData/CompactProMunbox`, said to be made by Compact Pro 1.52; 27 files in two
nested folders) expands every data fork to munbox's listed MD5 **[Reference: munbox samples, said to be Compact Pro
1.33/1.52]**. It exercises the half-escape chain in its LZH + RLE forks (`81 81 81` and `81 81 82`), not in its
RLE-only forks, and never `81 82 01`. Its directory comes after the forks, at the end of the archive; a fork may lie
before or after the directory but not across it or the header (the reader formerly required forks after the
directory and rejected the sample).

## StuffIt 5 (initial subset)

StuffIt 5 is recognized by the eight-byte `StuffIt ` signature and version byte 5 at offset 82. The 100-byte archive
header stores its total length at 84, the first root member's absolute offset at 88, and the root member count at 92.
The offset at 94 is the same until StuffIt Deluxe 7.0 prepends a member (its return receipt,
`StuffItReturnReceipt.txt`): then 88 points to the receipt, whose next link is the old first member, and 94 still to
the old first member, so 94 would lose the receipt **[Verified]** (Deluxe 7.0, `testfile.stuffit7_dlx.mac9.rreceipt.sit`).
An archive member begins with `A5 A5 A5 A5`; the `u16` at +6 gives its header length. The member header has flags at +9,
Mac creation and modification seconds at +10 and +14, the next sibling offset at +22, the name byte count at +30, and
the header CRC at +32. These field positions and encodings are **[Fitted]** by comparison with
[Deark's StuffIt reader](https://github.com/jsummers/deark/blob/master/modules/stuffit.c).

The member flag `$40` denotes a folder and `$20` denotes an encrypted member **[Fitted]**. Folder headers store the
first child offset at +34 and child count at +46. A file header stores logical and compressed data-fork lengths at
+34/+38, data-fork CRC at +42, and compression method at +46. The UTF-8 name follows the fixed fields (and any
password bytes). File-specific Finder information starts after the variable header: resource-fork presence is flag bit
0 of its `flags2`; type, creator and Finder flags follow. That block is 36 bytes in a member whose version byte (+4)
is 1, as every Mac StuffIt writes (Deluxe 6.5 and 7.0, DropStuff 7.0.3), and 32 bytes in the version-3 members of
StuffIt 7.0 for Windows **[Verified]**; a Windows member's type and creator fields hold Windows data (`$00000020`). When present, resource-fork lengths, CRC and method precede
resource bytes, which precede data-fork bytes. These member details are **[Fitted]** against Deark's independent
parser and the hand-built vectors in `StuffItFeatureTests`; they have not yet been checked against a corpus created by
the original StuffIt application.

ClassicMac currently extracts methods 0 (stored), 1 (RLE90), 2 (Compress/LZW), 3 (Huffman), 5 (LZAH), 6 (fixed Huffman + PackBits), 8 (MW),
13 (LZ + Huffman), 14 (Installer), and 15 (Arsenic) for either fork. RLE90 emits ordinary bytes as
literals; `$90 00` emits a literal `$90`; `$90 n` for nonzero `n` repeats the previously decoded byte until the run has
`n` copies **[Fitted]**. Truncated runs, runs without a prior byte, output-length mismatches and extents outside the
archive are rejected. Per-fork CRC mismatches are reported as errors while retaining the decoded file. Encrypted
entries and unsupported methods are reported and omitted; no password is requested. Files and folders are returned
through the normal `ContainerUnwrapper` pipeline, preserving UTF-8 paths, Finder type/creator/flags, dates and both
forks. Original v5 archives made with StuffIt Deluxe 6.5 and 7.0 for Macintosh verify the member listing and exact
data/resource fork bytes across the Mac OS 9 and Mac OS X archive variants; the specific compression method of each
member is not part of that acceptance test.

Folders are **[Verified]** against DropStuff 7.0.3 (StuffIt Standard 7.0.3) archives of a synthetic file set
(`TestData/StuffIt703`; Better Compression writes methods 15 and 0, Faster Compression 13 and 0). A folder header is
followed by the same 36-byte Finder block as a file. Right after it StuffIt writes the folder's end marker: a
48-byte folder header with no name, first child `$FFFFFFFF` and no Finder block; the folder's first member follows
the marker, and the folder's last member's next link points back to the marker. The reader walks each list by its
declared count, so it never reaches the marker, and it bounds a member's forks by its next link only when that link
points forward. StuffIt X (`.sitx`, signature `StuffIt!`) is not this format and is not recognised (on request
only).

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

A symbol is found from `code / (range / total)`; the last symbol owns the rest of the range (`range` less the other
symbols' share, which can exceed its `frequency × scale`), so a quotient of `total` or more selects the last symbol
rather than being invalid, as munbox's `sit15.c` (MIT) does. Without this, the DiskDoubler corpus's StuffIt 6.5.1
archives `sources.ddpro411.ad1.sit` and `ad2.sit` (fixtures `StuffIt651DiskDoublerPro411Ad1Files.sit`/`Ad2Files.sit`)
failed about 50 bits before the end of `testfile.jpg`'s and `testfile.PICT`'s streams; every file of both now expands
to the sources **[Verified]**.

The legacy v1/v2 reader is implemented. Original-application coverage currently includes a flat version-2 archive;
version 1, nested-folder metadata, and broader legacy interoperability remain unverified. Archive-level comment
placement has an original-app test through the `SitC` resource described above. See Phase 10 in
[the project plan](../PLAN.md).

## Zip, tar and gzip with Mac data

These are not Mac formats; Mac files travel in them with their resource fork and Finder info stored beside the data.
`ZipReader`, `TarArchiveReader` and `GzipReader` read them; the unwrapper tries them after LHA.

**Zip** (PKWARE's `APPNOTE.TXT` [Doc]). Detected by a local header (`PK\3\4`) or an empty archive's end record
(`PK\5\6`) at offset 0. The reader finds the end-of-central-directory record in the last 65,557 bytes, and the ZIP64
end record through its locator when a count, size or offset is all ones; the ZIP64 extra field (`0x0001`) supplies an
entry's 64-bit sizes and offset in the fixed order. Data before the archive (a self-extractor's code) shifts every
offset by the same amount, which is allowed for (the end record's position minus the central directory's recorded
offset and size). Multi-disk archives are refused. Each entry's data is found through its local header (name and extra
lengths from there, not the central copy). Methods 0 (stored) and 8 (deflate, `DeflateStream`) are read; other methods
are `archive.method-unsupported` and encrypted entries (flag bit 0) `archive.encrypted`, both skipped. The CRC-32 is
checked (`archive.fork-checksum`, data kept). Directories are entries ending in `/`, or with the Unix or DOS directory
attribute; a Unix symbolic link (mode `0xA000`) becomes a file whose `SymbolicLinkTarget` is its UTF-8 data.

Names are UTF-8 when general-purpose flag bit 11 is set [Doc]. Otherwise ASCII names are ASCII; a name from a
Macintosh host (version-made-by high byte 7) is Mac Roman; any other name that is valid UTF-8 is read as UTF-8, since
Mac OS X's Archive Utility writes UTF-8 without the flag **[Fitted]**; the rest are CP437, as APPNOTE says. Paths split
on `/` (and `\` from an MS-DOS host); empty and `.` components are dropped, and `..` components are dropped with
`archive.path-unsafe` (Warning). File names become Mac Roman, with `?` for characters it lacks; the Unicode names are
kept.

Dates, best first: the Mac extra fields' dates (Mac local time, used as is), the extended-timestamp field `0x5455`
(Unix UTC seconds, converted to `ContainerReadOptions.TimeZone`) [Doc: Info-ZIP `extrafld.txt`], then the DOS date and
time (local; an impossible date is no date). Unix permission bits are not used.

Mac extra fields, from Info-ZIP's `proginfo/extrafld.txt` [Doc]. The local header's copy is read first (it is the full
form) and the central copy fills gaps; a damaged field is `archive.extra-field-invalid` (Warning) and ignored.

| Tag | Writer | Layout | ClassicMac |
| --- | --- | --- | --- |
| `0x07c8` | Info-ZIP (old, J. Lee) | `"JLEE"`, FInfo (16), creation, modification (Mac dates), flags (bit 0: data fork), dirID, optional volume name; big-endian | Finder info and dates. The name has an extra `d` or `r`, removed when it matches the fork **[Fitted]**; the `r` entry becomes the file's resource fork |
| `0x334d` "M3" | Info-ZIP (new) | BSize (u32), flags (u16; bit 0 data fork, bit 2 attributes stored, bit 3 64-bit dates, bit 4 no GMT offsets), type, creator; the local copy adds a compression type (0 stored, 8 deflate) and CRC-32 unless bit 2, then the attributes: fdFlags, fdLocation, fdFldr, FXInfo (16), version, access, creation, modification, backup (Mac local), GMT offsets, charset, path, comment | Type, creator, Finder flags, location, folder, FXInfo and dates. The byte order is not stated; the numbers are read little-endian like zip's own fields **[Fitted]**. A CRC mismatch is `archive.extra-field-crc` (Warning; used anyway). A resource-fork entry is filed under `XtraStuf.mac/` + the file's path **[Fitted]**, which is stripped before pairing |
| `0x2605` | ZipIt | `"ZPIT"`, name length, Mac Roman name, type, creator, then optionally Finder flags and a reserved word **[Fitted]**; big-endian | The Mac name replaces the entry's last name component; type, creator, flags. Its entries hold MacBinary data, which the unwrapper opens in turn |
| `0x2705` | ZipIt 1.3.5+ | `"ZPIT"`, type, creator, optional Finder flags and reserved word | Type, creator, flags |
| `0x2805` | ZipIt (folders) | `"ZPIT"`, frFlags, view | Ignored (folders are not kept) |

**tar** (POSIX ustar and pax, GNU long names, V7), read with `System.Formats.Tar`. Detected by the first header
block's checksum (the byte sum with the checksum field taken as spaces, octal at +148 [Doc: POSIX]) together with the
`ustar` magic at +257 or, for V7, a name and a type flag of `0`, `1`, `2`, `5` or NUL. Pax `path` records and GNU `L`
long names give the full path; names are UTF-8. Regular and contiguous files become files; directories become folder
paths; symbolic links become files with `SymbolicLinkTarget`; a hard link copies an earlier entry's data
(`archive.link-target-missing`, Warning, when there is none); other types (devices, FIFOs) are `archive.entry-skipped`
(Info). The modification time (Unix UTC) is converted to the reading zone; there is no creation date. A truncated
archive keeps what was read (`archive.truncated`).

**gzip** (RFC 1952 [Doc]). Detected by `1F 8B`, method 8 and no reserved flag bits. It expands to one file through
`GZipStream`, which also reads concatenated members, under `MaxExpandedBytesPerInput`. The name is the header's FNAME
(ISO 8859-1, last path component) or else the input's name without `.gz`, `-gz`, `_gz` or `.z`, with `.tgz` becoming
`.tar`. MTIME (if nonzero) is the modification date. A tar inside (`.tgz`, `.tar.gz`) or a MacBinary file inside
(MacGzip) is opened by the unwrapper in turn.

**AppleDouble pairing** (zip and tar alike). An entry named `._name`, in the file's own folder or in the same folder
under a top-level `__MACOSX/`, is an AppleDouble header file ([CONTAINERS.md](CONTAINERS.md) §5) for `name`: its
resource fork, Finder info (unless all zero) and dates go to the file of that path. Without such a file it becomes a
file of its own with an empty data fork (a Mac application's empty data fork is often left out); when the path is a
folder it is the folder's Finder info and is dropped. A `._` entry that is not an AppleDouble header stays an ordinary
file (`archive.appledouble-invalid`, Warning). Two entries with the same path keep the last (`archive.duplicate-entry`,
Warning). The `__MACOSX` folder itself is not part of the result.

Not covered: empty folders (a `MacFile` has no folder record, so folders exist only in their files' paths) and folder
Finder info; zip methods other than stored and deflate (deflate64, bzip2, LZMA, …) and any encryption; the Info-ZIP
Unicode path field `0x7075`; macOS tar's extended attributes in pax records (`SCHILY.xattr.com.apple.*`,
`LIBARCHIVE.xattr.*`), since only `._` entries are read. The Mac extra-field rules are tested on
hand-built archives only; no zip made by Info-ZIP's Mac port or ZipIt has been checked yet.

## Self-extracting archives (`.sea`)

A self-extracting archive is an application (type `APPL`) whose resource fork holds the extractor and whose data fork
is the archive, from offset 0 to the end of the fork:

- StuffIt's classic extractor (creator `aust`): the data fork is a `SIT!` archive whose archive length equals the
  fork's length. The extractor opens its own data fork by name and reads from offset 0 without checking the
  signature [Code] [Verified on StuffIt SEA 3.5 samples].
- StuffIt 5 extractors: the data fork is a complete StuffIt 5 archive whose length field equals the fork's length
  [Verified].
- Compact Pro extractors: the archive at data-fork offset 0 [Doc]; no sample checked, and the creator code is
  unverified.

ClassicMac needs no special rule: the unwrapper tries every reader on a file's data fork whatever its type, so a
`.sea` lists as the application with the archive's files inside it. The extractor's own resources are often
compressed with Aladdin's own `dcmp` (128) and are listed, not decompressed.

Not covered: InstallerMaker installers (creator `STi0`, data fork signature `ST65`) and other installer formats.
