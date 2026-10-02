# LHA / LArc (level-0 through level-3 records; stored and compressed methods)

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
