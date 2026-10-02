# bzip2

bzip2 is Julian Seward's block-sorting compressor: each block is Huffman-coded move-to-front indexes with zero runs,
over a Burrows–Wheeler transform of run-length-coded bytes, with a CRC per block and one per stream. UDIF images use
it as run type `$80000006`. ClassicMac decodes it with a decoder written from the format, with no GPL code.

| | |
| --- | --- |
| Used by | [udif.md](../disk-images/udif.md) (run type `$80000006`, [udif.md §1.4](../disk-images/udif.md#14-runs)) |
| ClassicMac | Reads; `ClassicMac.Files.Compression.BZip2` |
| Verified against | Streams written by libbzip2 1.0 (Python's `bz2`); no UDIF image with bzip2 runs |
| Sources | The bzip2 1.0 format, as its author defines it |

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

Bits are read most significant first, with no byte alignment inside a stream [Author]. Offsets and sizes in these
tables are in bits.

### 1.1 Streams

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 24 | Signature | `"BZh"` |
| +24 | 8 | Level | `'1'`–`'9'`: a block holds at most level × 100,000 bytes before the last stage (§2.4) is undone |
| +32 | … | Blocks | §1.2, each starting with the 48-bit magic `$314159265359` |
| … | 48 | End of stream | `$177245385090` |
| … | 32 | Combined CRC | §2.5 |
| … | 0–7 | Padding | To a byte boundary |

Streams may be concatenated [Author].

### 1.2 Blocks

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 48 | Magic | `$314159265359` |
| +48 | 32 | Block CRC | Of the block's output (§2.5) |
| +80 | 1 | Randomised | 1 only from bzip2 0.9.0 and earlier |
| +81 | 24 | Origin pointer | The row of the original string in the sorted matrix (§2.4) |
| +105 | 16 | Range map | Bit *i* (from the top) set when bytes 16*i* … 16*i* + 15 are used |
| +121 | 16 × ranges | Byte maps | For each range present, a 16-bit map of its bytes, top bit first |
| … | 3 | Group count | 2–6 Huffman tables |
| … | 15 | Selector count | At least 1 |
| … | … | Selectors | §2.1 |
| … | … | Code lengths | Per table, §2.1 |
| … | … | Symbols | Huffman-coded, up to the end-of-block symbol (§2.2) |

[Author]

The used bytes, in increasing order, form the symbol map (*inUse* entries, at least 1). The alphabet is *inUse* + 2
symbols: 0 = RUNA, 1 = RUNB, 2 … *inUse* = move-to-front indexes 1 … *inUse* − 1, and *inUse* + 1 = end of block
[Author].

## 2. Reading

Decoding a block runs the encoder's five stages backwards: Huffman codes, zero runs (RUNA/RUNB), move-to-front, the
Burrows–Wheeler transform, and a first run-length stage [Author]. A reader decodes one stream after another while the
next bytes are `"BZh"`.

### 2.1 Selectors and Huffman tables

1. **Selectors.** Each is a unary number *j*: count 1 bits up to the ending 0; *j* must be below the group count. *j*
   indexes a move-to-front list of the tables, which starts 0, 1, 2, …: the table at position *j* is the selector,
   and moves to the front.
2. **Code lengths.** For each table: a 5-bit starting length; then for each symbol, while the next bit is 1, read one
   more bit and add 1 (bit 0) or subtract 1 (bit 1); a 0 bit ends the symbol, whose length is the current value. The
   length carries to the next symbol and must stay 1–20.
3. **Codes** are canonical: assigned in increasing order of length, and of symbol within a length, each code one more
   than the last, doubled when the length grows.

[Author]

### 2.2 Symbols

1. Symbols come in groups of 50: each group is decoded with the table the next selector names; running out of
   selectors is an error.
2. **Zero runs.** RUNA and RUNB write a run of move-to-front index 0 (the byte at the list's front) in bijective base
   2: with a weight starting at 1, RUNA adds the weight and RUNB twice the weight, and the weight then doubles. The run
   ends at the next other symbol, which resets the weight to 1.
3. **Move-to-front.** The list starts 0, 1, … *inUse* − 1 (positions in the symbol map). Symbol *s* ≥ 2 takes the
   entry at position *s* − 1, moves it to the front and outputs the symbol map's byte for it.
4. The output (at most level × 100,000 bytes) is the last column *L* of the sorted matrix; the origin pointer must be
   below its length.

[Author]

### 2.3 Inverse BWT

1. **Links.** Count each byte value in *L*, and let *start*[*b*] be the number of bytes below *b*. For each position
   *i* in order: *next*[*start*[*L*[*i*]]++] = *i*.
2. **Walk.** *p* = *next*[origin]; then, *n* times (*n* = the length of *L*), output *L*[*p*] and set *p* =
   *next*[*p*].

[Author]

### 2.4 The first run-length stage

After four equal bytes in a row, the next byte walked is a count (0–255) of further copies of that byte, not a byte
of data; counting starts again after it [Author].

### 2.5 The CRCs

- **Block CRC:** polynomial `$04C11DB7`, most significant bit first
  (`crc = (crc << 8) ^ table[(crc >> 24) ^ byte]`), starting at `$FFFFFFFF`, complemented at the end, over the
  block's final output. `123456789` gives `$FC891918`.
- **Combined CRC:** start at 0; after each block, `c = ((c << 1) | (c >> 31)) ^ blockCRC`.

[Author]

## 3. Writing

None.

## 4. Variants

- Randomised blocks (§1.2) come only from bzip2 0.9.0 and earlier [Author].

## 5. ClassicMac

- A randomised block is refused. [ClassicMac]
- Both CRCs are checked. A CRC mismatch, bad data, output that overflows the run, or a stream ending short of the
  run's size is a damaged run; the rest of the run reads as zeros. [ClassicMac]
- Bits past the end of the input read as zeros; a Huffman code that runs out of input is bad data. [ClassicMac]

## 6. Diagnostics

The decoder reports how it ended; the UDIF reader raises the diagnostic.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `udif.bad-run` | Error | A bzip2 run has bad data, a CRC mismatch, a randomised block, overflows its run or ends short of it | The rest of the run reads as zeros | Not traced |

## 7. Verification

- `tests/ClassicMac.Files.Tests/BZip2Tests.cs`: `Streams_decode` (streams libbzip2 1.0 wrote through Python's `bz2`:
  a short stream, one of three 100 kB blocks, and two streams back to back); `Damage_is_detected` (a flipped bit,
  output too small, data that is not bzip2).
- `tests/ClassicMac.Files.Tests/UdifTests.cs`, `Every_run_type_decodes_and_the_checksums_match`: a synthetic image
  with a bzip2 run.

## 8. Not covered

- Writing bzip2.
- Randomised blocks.
- A UDIF image with bzip2 runs made by Apple's software: none is in the corpus.

## 9. References

1. Julian Seward, bzip2 and libbzip2 1.0, <https://sourceware.org/bzip2/>. BSD-style licence; the format only, no
   code used.
