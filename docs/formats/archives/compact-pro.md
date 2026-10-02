# Compact Pro (RLE and LZH subset)

A Compact Pro archive begins with an 8-byte header: marker `$01`, segment number (1 in an unsegmented archive), a
16-bit archive id, and a big-endian offset to the directory. At that offset are a raw CRC-32 state, the total entry
count, a Pascal comment, and a flattened sequence of entries. The checksum covers the entry count, comment length and
bytes, and every entry record. A directory record has its high name-length bit set and carries a count of all
descendant entries; a file record carries a volume byte, fork-data offset, Finder type/creator/flags, dates, combined
fork checksum, method flags, expanded fork lengths, and compressed fork lengths. Resource bytes precede data bytes.
The fields were fitted against the [Compact Pro format description](https://docs.rs/crate/compact-pro/latest) and
[XADMaster's independent reader](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADCompactProParser.m/), and are
**[Verified against Compact Pro 1.52]** (`TestData/CompactPro152`, made on Mac OS 9 from the synthetic file set): the
forks come first, from offset 8, and the directory follows them at the end of the archive; folders are a directory
record followed by its descendants (`Folder` with one child, `Inner`); the archive id differs between two saves of
the same files; the fork checksum is the CRC-32 register over the resource then the data fork without the final inversion
(`$FFFFFFFF` for an empty file); the archive's own file is
`PACT`/`CPCT`.

Method flag bit 0 marks encryption, bit 1 selects LZH+RLE for the resource fork, and bit 2 selects LZH+RLE for the
data fork. The reader extracts both RLE-only and LZH+RLE forks. LZH uses an 8 KiB history window, three
per-block canonical Huffman trees, MSB-first bits, literal tokens, and length/displacement matches; its output is
then passed through RLE. Blocks switch after the documented token-count threshold and discard alignment bits before
reading the next trees **[Fitted]** against
[psx-spx's Compact Pro notes](https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/). Tests cover both
fork encodings, literal and overlapping-match LZH tokens, a block boundary, escaped bytes, repeat runs, nested
directory paths, archive comments, checksums, default unwrapping, and directory/data overlap rejection. Comments are
reported as MacRoman display text in an `archive.comment` information diagnostic **[Fitted]**. Compact Pro 1.52 used
LZH + RLE for the forks it could shrink (`Big.txt`'s data, `ReadMe`'s resource fork) and RLE alone for the others,
including 120000 bytes of noise; both decode to the source forks **[Verified against Compact Pro 1.52]**.

**Segments** **[Verified against Compact Pro 1.52]** (Misc > Segment…, `cpnoise.#1`–`#3`). Compact Pro cuts the
whole archive into pieces and puts an 8-byte header on each: `$01`, the segment number (1, 2, 3 …), a 16-bit id shared
by the set (not the archive's own id), and the directory offset, 0 in every segment but the last, where it counts
from the start of that segment file. Segment 1 holds the archive's first bytes after its header; each later segment
holds the next bytes after its own. The directory is not rewritten: its fork offsets (and the volume byte, 1) are the
same as in the unsegmented archive, so an offset is into the joined archive (segment 1, then each later segment
without its header) and a fork may run across segments (here one 120001-byte fork from offset 8 spans all three).
The reader therefore opens only the last segment (as Compact Pro's [*User's Guide*, “Working With Segmented
Archives”](https://oldapplestuff.com/download/Macintosh/Macintosh_Garden/manuals/Compact-Pro-Users-Guide.pdf)
says): a header whose segment number is above 1 with a directory offset is the last of that many segments, and the
others are the siblings (from the host folder through the default pipeline) with the same set id and a zero
directory offset. Earlier segments are not recognised on their own. A missing segment is an `archive.missing-volume`
error naming the missing numbers; the entries whose forks lie in the segments before the first missing one are
still read, and each other entry is an `archive.missing-volume` warning. Two siblings with the same number are
rejected, and all segments together count against the input-size limit. (Before, the reader took the entry's volume
byte as the file holding the forks, at an offset into that file; that fails on a fork that crosses segments, and
matched siblings by number alone, so any other archive in the folder was taken for segment 1.) Not checked: a set
written across floppies while saving, rather than with Segment…, and what the volume byte means then. Encrypted
entries remain unsupported.

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
