# bzip2

bzip2 is UDIF run type `$80000006` ([udif.md §1.4](../disk-images/udif.md#14-runs)), a block-sorting compressor. The rules are
Julian Seward's bzip2 1.0 format [Author]; ClassicMac's decoder is written from the format, with no GPL code, and is
checked on streams libbzip2 1.0 wrote (Python's `bz2`: a short stream, one of three 100 kB blocks, and two streams
back to back). No UDIF image with bzip2 runs is in the corpus.

Bits are read **most significant first**, with no byte alignment inside a stream. Decoding a block runs the encoder's
five stages backwards: Huffman codes, zero runs (RUNA/RUNB), move-to-front, the Burrows–Wheeler transform, and a first
run-length stage.

Contents

1. [Streams and blocks](#1-streams-and-blocks)
2. [Selectors and Huffman tables](#2-selectors-and-huffman-tables)
3. [Symbols](#3-symbols)
4. [Inverse BWT and the first run-length stage](#4-inverse-bwt-and-the-first-run-length-stage)
5. [The CRCs](#5-the-crcs)

---

## 1. Streams and blocks

| Bits | Field |
| --- | --- |
| 24 | `"BZh"` |
| 8 | Level, `'1'`–`'9'`: a block holds at most level × 100,000 bytes before the last stage is undone |
| … | Blocks, each starting with the 48-bit magic `$314159265359` |
| 48 | End of stream: `$177245385090` |
| 32 | Combined CRC |
| 0–7 | Padding to a byte boundary |

Streams may be concatenated; a reader decodes one after another while the next bytes are `"BZh"`.

A block:

| Bits | Field |
| --- | --- |
| 48 | `$314159265359` |
| 32 | Block CRC of the block's output ([§5](#5-the-crcs)) |
| 1 | Randomised: 1 only from bzip2 0.9.0 and earlier. ClassicMac refuses such a block [ClassicMac] |
| 24 | Origin pointer: the row of the original string in the sorted matrix ([§4](#4-inverse-bwt-and-the-first-run-length-stage)) |
| 16 | Range map: bit *i* (from the top) set when bytes 16*i* … 16*i* + 15 are used |
| 16 × ranges | For each range present, a 16-bit map of its bytes, top bit first |
| 3 | Group count: 2–6 Huffman tables |
| 15 | Selector count, at least 1 |
| … | Selectors ([§2](#2-selectors-and-huffman-tables)) |
| … | Code lengths, per table ([§2](#2-selectors-and-huffman-tables)) |
| … | The Huffman-coded symbols, up to the end-of-block symbol ([§3](#3-symbols)) |

The used bytes, in increasing order, form the **symbol map** (*inUse* entries, at least 1). The alphabet is *inUse* +
2 symbols: 0 = RUNA, 1 = RUNB, 2 … *inUse* = move-to-front indexes 1 … *inUse* − 1, and *inUse* + 1 = end of block.

---

## 2. Selectors and Huffman tables

- **Selectors.** Each is a unary number *j*: count 1 bits up to the ending 0; *j* must be below the group count. *j*
  indexes a move-to-front list of the tables, which starts 0, 1, 2, …: the table at position *j* is the selector,
  and moves to the front.
- **Code lengths.** For each table: a 5-bit starting length; then for each symbol, while the next bit is 1, read one
  more bit and add 1 (bit 0) or subtract 1 (bit 1); a 0 bit ends the symbol, whose length is the current value. The
  length carries to the next symbol and must stay 1–20.
- **Codes** are canonical: assigned in increasing order of length, and of symbol within a length, each code one more
  than the last, doubled when the length grows.

---

## 3. Symbols

- Symbols come in **groups of 50**: each group is decoded with the table the next selector names; running out of
  selectors is an error.
- **Zero runs.** RUNA and RUNB write a run of move-to-front index 0 (the byte at the list's front) in bijective base
  2: with a weight starting at 1, RUNA adds the weight and RUNB twice the weight, and the weight then doubles. The run
  ends at the next other symbol, which resets the weight to 1.
- **Move-to-front.** The list starts 0, 1, … *inUse* − 1 (positions in the symbol map). Symbol *s* ≥ 2 takes the
  entry at position *s* − 1, moves it to the front and outputs the symbol map's byte for it.
- The output (at most level × 100,000 bytes) is the last column *L* of the sorted matrix; the origin pointer must be
  below its length.

---

## 4. Inverse BWT and the first run-length stage

- **Links.** Count each byte value in *L*, and let *start*[*b*] be the number of bytes below *b*. For each position
  *i* in order: *next*[*start*[*L*[*i*]]++] = *i*.
- **Walk.** *p* = *next*[origin]; then, *n* times (*n* = the length of *L*), output *L*[*p*] and set *p* =
  *next*[*p*].
- **Run-length stage.** After four equal bytes in a row, the next byte walked is a count (0–255) of further copies
  of that byte, not a byte of data; counting starts again after it.

---

## 5. The CRCs

- **Block CRC:** polynomial `$04C11DB7`, **most significant bit first**
  (`crc = (crc << 8) ^ table[(crc >> 24) ^ byte]`), starting at `$FFFFFFFF`, complemented at the end, over the
  block's final output. `123456789` gives `$FC891918`.
- **Combined CRC:** start at 0; after each block, `c = ((c << 1) | (c >> 31)) ^ blockCRC`.

ClassicMac checks both; a mismatch, bad data, output that overflows the run, or a stream ending short of the run's
size is a damaged run (`udif.bad-run`, [udif.md §6](../disk-images/udif.md#6-diagnostics)).
