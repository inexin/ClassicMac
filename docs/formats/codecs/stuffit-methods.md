# StuffIt compression methods

The compression methods of StuffIt's archives: one method byte per fork names how that fork's bytes are coded, in
StuffIt 1.x–4.x archives ([stuffit.md](../archives/stuffit.md)) and StuffIt 5 archives
([stuffit5.md](../archives/stuffit5.md)) alike. Aladdin published no specification; the methods are known from other
readers and checked against archives made by StuffIt itself. ClassicMac decodes methods 0, 1, 2, 3, 5, 6, 8, 13, 14
and 15.

| | |
| --- | --- |
| Identified by | The fork's method byte in its archive member's header |
| ClassicMac | Reads; `ClassicMac.Files.Archives` (`StuffItReader`, `StuffItMethod13Decoder`, `StuffItMethod14Decoder`, `StuffItMethod15Decoder`) |
| Verified against | StuffIt 1.5.1 (methods 0, 1, 2, 3)<br>StuffIt Deluxe 4.5 (method 13)<br>StuffIt Deluxe 6.5.1 (method 15)<br>DropStuff 7.0.3 (methods 0, 13, 15) |
| Sources | Other readers (behaviour only): XADMaster, macutils, StuffIt-Go, compcol, Matthew T. Russotto's method-15 description, munbox |

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

### 1.1 Method numbers

The archive formats say which bits of the method byte carry the method; the low four bits name it in StuffIt 1.x–4.x
([stuffit.md §1.2](../archives/stuffit.md#12-member-header)), the whole byte in StuffIt 5
([stuffit5.md §1.3](../archives/stuffit5.md#13-resource-fork-information)).

| Method | Name | Steps | Source |
| --- | --- | --- | --- |
| 0 | Stored | The fork's bytes; compressed and expanded lengths are equal | [Verified: StuffIt 1.5.1] |
| 1 | RLE90 | §2.2 | [Verified: StuffIt 1.5.1] |
| 2 | Compress (LZW) | §2.3 | [Reference: XADMaster] [Verified: StuffIt 1.5.1] |
| 3 | Huffman | §2.4 | [Reference: XADMaster] [Verified: StuffIt 1.5.1] |
| 5 | LZAH | §2.5 | [Reference: macutils] |
| 6 | Fixed Huffman + PackBits | §2.6 | [Reference: macutils, StuffIt-Go] |
| 8 | MW (Miller–Wegman) | §2.7 | [Reference: XADMaster] |
| 13 | LZ + Huffman | §2.8 | [Reference: compcol] [Verified: StuffIt Deluxe 4.5, DropStuff 7.0.3] |
| 14 | Installer | §2.9 | [Reference: XADMaster] |
| 15 | Arsenic | §2.10 | [Reference: Russotto] [Verified: StuffIt Deluxe 6.5.1, DropStuff 7.0.3] |

Methods 4, 7, 9–12 are not decoded (§8).

### 1.2 Method 6 block

A method-6 fork is a sequence of blocks [Reference: macutils, StuffIt-Go]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Block length | `i32`, counting these 4 bytes. Negative: a PackBits block of −length bytes. Positive: a Huffman block |
| +$04 | 4 | PackBits length | Huffman blocks only: the length of the PackBits stream the Huffman data expands to |
| +$08 | 2 | Symbol count | Huffman blocks only: entries in the translation table, at most 256 |
| +$0A | n | Translation table | Huffman blocks only: the byte for each leaf, in leaf order |
| … | | Huffman data | Huffman blocks only: the rest of the block, most significant bit first |

In a PackBits block the PackBits data follows the length word directly.

### 1.3 Method 14 block

A method-14 fork is a `u16` block count, little-endian, then the blocks [Reference: XADMaster]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Compressed length | `u32`, little-endian: the bytes after this 8-byte header |
| +$04 | 4 | Expanded length | `u32`, little-endian |
| +$08 | n | Coded data | A 308-symbol literal/length tree, a 75-symbol distance tree, then the tokens; padded to a byte |

### 1.4 Method 15 stream

All fields are read through the arithmetic decoder of §2.10 with a fixed one-bit model, most significant bit first
[Reference: Russotto]:

| Field | Bits | Notes |
| --- | --- | --- |
| Signature | 16 | `As` |
| Block size | 4 | The block size is 2^(9 + value) bytes, at most 2^24 |
| Per block: end flag | 1 | 1 ends the stream |
| Per block: randomized | 1 | The block's bits were randomized |
| Per block: primary index | block-size bits | The BWT primary index |
| Per block: symbols | | Adaptive-model symbols up to the end-of-block symbol |
| CRC-32 | 32 | Of the whole expanded fork |

## 2. Reading

### 2.1 Fork length and checksum

1. The member gives each fork's compressed length, expanded length, method and CRC.
2. Decode the compressed bytes by the method. Every decoder must produce exactly the expanded length; output past it,
   or input that ends before it, is an error.
3. Compare the CRC-16/ARC (polynomial `$A001` reflected, initial value 0) of the expanded fork with the stored CRC
   [Verified: StuffIt 1.5.1, StuffIt Deluxe 4.5]. Method 15 forks are not checked this way: the stream carries its own
   CRC-32 (§2.10).

### 2.2 Method 1: RLE90

1. A byte other than `$90` is a literal.
2. `$90 $00` is a literal `$90`.
3. `$90 n`, n > 0, repeats the last byte written until the run holds n copies, that is n − 1 more.
4. A run with no byte written before it, or a `$90` at the end of the input, is an error.

[Verified: StuffIt 1.5.1]

### 2.3 Method 2: Compress LZW

The stream of Unix `compress` in block mode [Reference: XADMaster]:

1. Codes are read least significant bit first, starting at 9 bits, at most 14.
2. Code 256 clears the dictionary: skip the rest of the current group of eight codes (counted from the last clear),
   go back to 9 bits and start over with the next code as a first code.
3. A first code must be a literal (below 257); output it.
4. Otherwise a code above the next free code is an error. A code equal to the next free code is the previous phrase
   followed by its own first byte (the LZW "KwKwK" case). Output the phrase.
5. Add the previous phrase plus the first byte of this one as the next free code (from 257, while below 2^14). When the
   next free code reaches 2^width, widen the codes by one bit, up to 14.

[Verified: StuffIt 1.5.1]

### 2.4 Method 3: Huffman

1. Read the code tree, most significant bit first, depth first: a 1 bit is a leaf followed by its 8-bit symbol; a 0
   bit is an internal node, its 0 subtree first, then its 1 subtree [Reference: XADMaster].
2. Decode symbols by walking the tree, 0 to the 0 subtree, until the expanded length is reached. There is no
   end symbol.

[Verified: StuffIt 1.5.1]

DiskDoubler's method 4 and PackIt's Huffman entries use the same tree and stream
([diskdoubler.md §2.4](../archives/diskdoubler.md#24-fork-methods), [packit.md §2](../archives/packit.md#2-reading)).

### 2.5 Method 5: LZAH

LZSS with Okumura and Yoshizaki's LZHUF adaptive tree [Reference: macutils]:

1. The 4,096-byte window starts at position 0 with bytes 0–17 zero, then 13 copies of each byte value 0–255, then the
   values 0–255, then 255–0, then 128 zero bytes, then 110 spaces.
2. Read a symbol from the adaptive tree of [lzhuf.md §3](lzhuf.md#3-the-adaptive-tree) (314 symbols, bits most
   significant first) and update the tree; the tree is rebuilt when the root's count reaches `$8000`.
3. Symbols 0–255 are literals: output them and put them in the window.
4. Symbols 256–313 are matches of length symbol − 253 (3–60). The upper six bits of the distance are a canonical code
   of 1, 3, 8, 12, 24 and 16 codes of 3 to 8 bits (LZHUF's `d_code`/`d_len`,
   [lzhuf.md §1](lzhuf.md#1-constants-and-tables)); six raw bits follow for the lower six. Copy `length` bytes from window position (current − distance − 1),
   putting each in the window.
5. Stop at the expanded length.

### 2.6 Method 6: fixed Huffman and PackBits

1. Read the blocks of §1.2 until the compressed fork ends; a block length under 4 or past the fork is an error.
2. A negative block: expand its PackBits data (step 5) into the fork.
3. A positive block: walk the fixed tree (step 4) from the root, a 1 bit to the one-child; a leaf 1–256 gives the
   translation table's entry leaf − 1, a leaf 257 or 258 ends the block. The bytes must fill exactly the PackBits
   length, which is at most 32,768 (the original method-6 buffer). Expand them (step 5).
4. The fixed tree has 258 leaves weighted, in leaf order: 1024 ×1, 512 ×1, 256 ×4, 128 ×12, 64 ×32, 32 ×16, 16 ×49,
   8 ×2, 16 ×2, 8 ×40, 4 ×95, 1 ×4. A node over leaves `lo`–`hi` with weight sum `s` splits after the first leaves
   whose weights reach `s / 2` (integer division, halved again at each level): those go to its 0 side, the rest to
   its 1 side; a side with one leaf is that leaf.
5. PackBits: a control byte c ≥ 0 copies the next c + 1 bytes; c from −1 to −127 repeats the next byte 1 − c times;
   −128 does nothing.

[Reference: macutils, StuffIt-Go]

### 2.7 Method 8: MW

Miller and Wegman's dictionary method [Reference: XADMaster]:

1. Start a group: a fresh dictionary, 9-bit codes read least significant bit first, next free code 256.
2. The group's first code is a literal (below 256): output it. A first code equal to 256 starts the group again.
3. Each later code below the next free code: its phrase is the dictionary phrase (a code below 256 is its byte);
   output it, and add a new entry that is the previous phrase followed by this phrase. When the next free code
   reaches 512, 1024 … widen the codes by one bit.
4. A code equal to the next free code ends the group: go to step 1. A code above it is an error.

### 2.8 Method 13: LZ and Huffman

Least significant bit first throughout [Reference: compcol]:

1. The first byte is the control byte. Its high nibble 1–5 selects one of five preset table sets (first code, second
   code, offset code); 0 means the tables follow; 6–15 are invalid.
2. Transmitted tables are read with a fixed 37-symbol meta-code: symbols 0–30 set the current length to symbol + 1,
   31 sets it to −1 (written as 0), 32 adds 1, 33 subtracts 1, 34 repeats it 0 or 1 more times (1 bit), 35 repeats
   it 2–9 more times (3 bits + 2), 36 repeats it 10–73 more times (6 bits + 10). Each symbol also writes the current
   length once. Read 321 lengths for the first code; if control bit 3 is set the second code is the first, else read
   321 more; then (control & 7) + 10 lengths for the offset code. Codes are canonical.
3. Decode with the first code after a literal (and at the start), with the second after a match.
4. Symbols 0–255 are literals. 256–317 are lengths 3–64; 318 is 10 bits + 65; 319 is 15 bits + 65; 320 ends the
   stream (an error before the expanded length).
5. After a length, an offset-code symbol k gives the distance: 1 for k = 0, 2 for k = 1, else 2^(k−1) + (k − 1 bits)
   + 1. Copy from that far back in the output.

[Verified: StuffIt Deluxe 4.5, DropStuff 7.0.3]

### 2.9 Method 14: Installer

1. Read the block count, then each block of §1.3.
2. Each block reads its literal/length tree (256 literals, 52 length symbols) and its 75-symbol distance tree. A tree's
   lengths are given either directly or through a smaller Huffman tree described the same way (recursively).
3. Literal symbols are output. A length symbol gives a length (base + 4 + extra bits); a distance symbol gives a
   distance (base + extra bits). Copy from the 256 KiB window, which starts as zeros and carries over from block to
   block.
4. At the end of a block, align to a byte; the next block starts after the compressed length.

[Reference: XADMaster]

### 2.10 Method 15: Arsenic

1. Read the stream header (§1.4) through the arithmetic decoder.
2. For each block: decode symbols with an adaptive selector model (11 symbols) and seven move-to-front models
   (ranges 2–3, 4–7, 8–15, 16–31, 32–63, 64–127, 128–255). Selector symbols 0 and 1 are bijective zero-run digits;
   2 is move-to-front index 1; 3–9 take the index from the matching model; 10 ends the block. Each index is undone
   through the move-to-front list.
3. Undo the Burrows–Wheeler transform with the block's primary index.
4. If the block is randomized, flip bit 0 of the byte at each position of the randomization sequence: the first
   position is the table's entry 0, and each next one adds the next entry, cycling over the 256 entries.
5. Undo the final run-length coding: four equal bytes are followed by a count of further copies of that byte.
6. After the last block, compare the stored CRC-32 with the expanded fork.

The model parameters and range-coder behaviour follow Russotto's description [Reference: Russotto]; the
randomization table is compcol's [Reference: compcol]. The coder finds a symbol from `code / (range / total)`, and the
model's last symbol owns the rest of the range (`range` less the other symbols' share, which can exceed its
`frequency × scale`), so a quotient of `total` or more selects the last symbol rather than being invalid
[Reference: munbox] [Verified: StuffIt 6.5.1, DiskDoubler corpus archives (§7)].

## 3. Writing

None.

## 4. Variants

None. Both archive formats use the same method numbers and streams; StuffIt 5's method-6 forks are read as in
StuffIt 1.x–4.x [Reference: macutils].

## 5. ClassicMac

- The decoders are ClassicMac's own. Method 13's tables and method 15's randomization table are transcribed from
  compcol under its MIT licence (`THIRD-PARTY-NOTICES.md`). [ClassicMac]
- A fork whose method is not in §1.1 is reported and skipped by the archive reader
  ([stuffit.md §5](../archives/stuffit.md#5-classicmac)). [ClassicMac]
- A damaged stream (bad code, truncated input, wrong length, method-15 CRC-32 mismatch) makes the archive read
  fail with an error; a fork CRC-16 mismatch is reported and the decoded fork kept. [ClassicMac]

## 6. Diagnostics

None. The decoders report nothing themselves; a fork checksum mismatch (§2.1) is reported by the archive readers
as `archive.fork-crc` ([stuffit.md §6](../archives/stuffit.md#6-diagnostics),
[stuffit5.md §6](../archives/stuffit5.md#6-diagnostics)).

## 7. Verification

- `TestData/StuffIt151` (StuffIt 1.5.1, a synthetic file set): `fx151_non.sit` method 0, `fx151_huf.sit` methods 3 and
  1, `fx151_lzw.sit` method 2, `fx151_lzw_huff.sit` methods 2 and 3; every fork expands to the sources and matches its
  CRC (`StuffIt151OriginalTests`).
- `TestData/StuffItMethod13` (CC0, from StuffIt Deluxe 4.5): preset and dynamic tables, both forks, exact bytes or the
  stored CRC-16. The corpus's code alphabets are aliased; `DistinctDynamicTrees.bin` is a hand-built stream with
  separate first and second codes and a distance-one match.
- `TestData/StuffItMethod15/StuffItDeluxe651.sit` (CC0, StuffIt Deluxe 6.5.1): exact data and resource forks,
  randomized blocks included.
- `TestData/StuffIt703` (DropStuff 7.0.3): methods 15 and 0 (`S703b.sit`), 13 and 0 (`S703f.sit`).
- `TestData/DiskDoublerOriginal/StuffIt651DiskDoublerPro411Ad1Files.sit` and `Ad2Files.sit` (CC0, StuffIt 6.5.1 method
  15): without the last-symbol rule of §2.10 they fail about 50 bits before the end of `testfile.jpg`'s and
  `testfile.PICT`'s streams; with it every file expands to the sources.
- `TestData/StuffItMethod14`: hand-built vectors only: literals, matches, extended length and distance codes, direct
  and recursive tree lengths, several blocks, both forks, truncated blocks, a match into the zeroed window.
- Hand-built vectors in `StuffItFeatureTests`: method 2 (clear, widening, the full 14-bit table, the KwKwK case,
  invalid codes, length checks), method 3 (both forks, both branches, truncated tree and data), method 5 (literals,
  the seeded window, each offset-code length, truncated input, renormalization at `$8000`), method 6 (both block forms
  alone and mixed in one fork, in both archive formats, missing end leaf, bad block length, truncated and overlong
  PackBits), method 8 (phrases, group reset, widening, invalid and truncated input).

## 8. Not covered

- Methods 4, 7, 9, 10, 11 and 12, and any method above 15.
- Methods 5, 6, 8 and 14 have no archive made by StuffIt: 1.5.1 cannot write method 6, and DropStuff 7.0.3
  (unregistered) does not offer method 14.
- Method 13 against archives from other StuffIt versions than 4.5 and 7.0.3.

## 9. References

1. XADMaster (The Unarchiver), `XADStuffItParser.m`, `XADCompressHandle.m`, `XADStuffItHuffmanHandle.m`,
   `XADStuffItOldHandles.m`. LGPL-2.1; reference only.
2. macutils (Dik T. Winter), `macunpack/sit.c` (method 6), `macunpack/de_lzah.c` (method 5). Licence unclear;
   reference only.
3. StuffIt-Go (ObsoleteMadness), the method-6 description in its package documentation. Licence not recorded;
   reference only.
4. compcol (Karpeles Lab), `src/sit13/tables.rs` and `src/arsenic/tables.rs`. MIT; tables transcribed with notice.
5. Matthew T. Russotto, *Arsenic compression*, <http://www.russotto.net/arseniccomp.html>. A published description.
6. munbox, `sit15.c`. MIT; reference only.
7. Haruhiko Okumura and Haruyasu Yoshizaki, LZHUF (1988): [lzhuf.md](lzhuf.md).
