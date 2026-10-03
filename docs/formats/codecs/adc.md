# ADC

Apple Data Compression, the "Faster (ADC)" compression of Disk Copy 6: a byte-oriented LZ77 code in which every token
starts with an opcode byte, either a literal run or a match into the bytes already decoded. NDIF images store it as
chunk type `$83` and UDIF images as run type `$80000004`. ClassicMac decodes it, and encodes it as Disk Copy 6.3.3 does.

| | |
| --- | --- |
| Used by | [ndif.md](../disk-images/ndif.md) (chunk type `$83`), [udif.md](../disk-images/udif.md) (run type `$80000004`) |
| ClassicMac | Reads and writes; `ClassicMac.Files.Compression.Adc` |
| Verified against | Disk Copy 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF |
| Sources | Disk Copy 6.3.3's `hdi2` codec and its encoder (`ADCCOMPRESSDATA`, `TreeSearch`, `InitNodes`), traced |

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

A chunk is a sequence of tokens. The top bits of the opcode byte choose the token
[Code: Disk Copy 6.3.3 `hdi2` codec] [Verified: 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF].

Literal run, opcode `1LLLLLLL`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Opcode | Bit 7 set; bits 0–6 `L`: the length is `L` + 1 (1–128) |
| +$01 | length | Bytes | Copied to the output |

Short match, opcode `00LLLLDD`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Opcode | Bits 6–7 zero; bits 2–5 `L`: the length is `L` + 3 (3–18); bits 0–1: the distance's upper 2 bits |
| +$01 | 1 | Distance | The distance's lower 8 bits; the distance is the 10-bit value + 1 (1–1024) |

Long match, opcode `01LLLLLL`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Opcode | Bits 6–7 `01`; bits 0–5 `L`: the length is `L` + 4 (4–67) |
| +$01 | 2 | Distance | The 16-bit value + 1 (1–65,536) |

No state carries from one chunk to the next [Code: Disk Copy 6.3.3 `hdi2` codec].

## 2. Reading

The output is the chunk's decoded size, known from the image's map
[Code: Disk Copy 6.3.3 `hdi2` codec] [Verified: 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF]:

1. While the output is not full, read an opcode and its operands (§1).
2. If the token would pass the end of the output, stop: the chunk is damaged. This is checked before any of the token
   is written. Disk Copy reports −8819 (damaged) at read time.
3. A literal run copies its bytes to the output.
4. A match copies *length* bytes from *distance* bytes back in the output, one byte at a time, so a match may overlap
   what it writes (distance 1 repeats the last byte).
5. Stop when the output is full.

Disk Copy checks nothing else: a match reaching before the output's start reads the memory before its buffer, and
input is read past the stored length [Code: Disk Copy 6.3.3 `hdi2` codec].

## 3. Writing

Disk Copy 6.3.3's encoder, which writes each chunk on its own [Code: Disk Copy 6.3.3 `ADCCOMPRESSDATA`, `TreeSearch`,
`InitNodes`, `CompareStrings`; the 68k `CODE` 8 versions are the same]. A reimplementation of these rules re-encodes
every ADC chunk of the sample images byte for byte [Verified: 14 chunks, 512- and 64-sector chunks].

1. **Trees.** Every position of the chunk goes into a binary search tree of the positions before it, chosen by its
   first two bytes (one tree per 2-byte value). `InitNodes` uses the first byte instead for chunks under 4,000 bytes,
   and a single tree for chunks under 200.
2. **Search and insert** (`TreeSearch`). From the tree's root, each node is compared with the new position on the bytes
   after the prefix, at most 67 − (prefix length) bytes and never past the chunk's last byte. The match length is the
   prefix length plus the bytes that agree. The best match is the first node met with a strictly longer match (so of
   equal ones the node higher in the tree wins). The descent goes left when the first differing byte is lower, right
   when higher, and the new position is added as a leaf. When all compared bytes agree, the new position takes that
   node's place (its parent link or root, its children), and the old one leaves the tree.
3. **End of a 2-byte-prefix chunk.** A position within two bytes of the chunk's end (position + 2 > last index) is not
   searched and not inserted; it is a literal.
4. **Window.** Nodes come from a pool of 65,537, used in turn; a node still in a tree when its turn comes round is
   deleted from it first, so matches reach at most 65,536 back. With two children, its replacement is the rightmost node
   of its left subtree when the position being inserted is even, the leftmost of its right subtree when odd: a direct
   child keeps its own children and takes the other side; a deeper one leaves its near child to its parent and takes
   both of the deleted node's children. The replacement then takes the deleted node's place (its parent link, or the
   root of the deleted node's prefix); with one child that child does, with none the link is cleared.
5. **Tokens.** Greedy, no look-ahead: a match of 3–18 bytes at most 1,024 back is a short match; otherwise one of
   4–67 bytes is a long match; otherwise the byte is a literal (a 3-byte match farther than 1,024 back is dropped). The
   positions a match covers are inserted (step 2) before the next search. Matches may overlap the bytes they produce
   (a run of zeros is distance-1 matches of 67).
6. **Literals** wait in a run, written as `$80 | (n − 1)` and the bytes when 128 wait, just before a match, and at the
   chunk's end.
7. **Raw fallback.** When the output grows past the input's length the encoder gives up and the chunk is stored raw;
   output of exactly the input's length stays ADC.
8. **Overrun margin.** After each flush (a literal run with the match that follows it), *d* = input bytes covered −
   bytes written; the margin is the largest *d* (at least 0) − the final *d* + 4. NDIF's buffer size is the chunk size in
   sectors + ⌈largest margin ÷ 512⌉ ([ndif.md §4.3](../disk-images/ndif.md#43-what-disk-copy-writes)).

The pool is not cleared between chunks, only the roots and its cursor; what is left of the last chunk is never reached
again, so it does not change the output.

## 4. Variants

None.

## 5. ClassicMac

- A match reaching before the output's start, and input running out before the output is full, are reported as a
  damaged chunk (§6) instead of reading stray memory. [ClassicMac]
- A damaged chunk keeps the bytes decoded before the fault; the rest of the chunk reads as zeros
  ([ndif.md §5](../disk-images/ndif.md#5-classicmac), [udif.md §5](../disk-images/udif.md#5-classicmac)). [ClassicMac]
- `Adc.Compress` follows §3 and returns the chunk's margin (step 8); it writes Disk Copy 6.3.3's bytes for 512- and
  7-sector chunks (two- and one-byte prefix trees) [Verified: §7]; the NDIF writer uses it for changed chunks
  ([ndif.md §3](../disk-images/ndif.md#3-writing)). It starts each chunk with a fresh pool, which §3 shows gives the
  same output. [ClassicMac]

## 6. Diagnostics

The decoder reports how it ended; the image readers raise the diagnostic.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ndif.bad-chunk` | Error | An ADC chunk in an NDIF image overruns its output, runs out of input or reaches before its start | Keeps what was decoded; the rest reads as zeros | −8819 for an overrun; reads stray memory for the others |
| `udif.bad-run` | Error | An ADC run in a UDIF image fails the same way | Keeps what was decoded; the rest reads as zeros | Not traced |

## 7. Verification

- `tests/ClassicMac.Files.Tests/AdcTests.cs`: `Large_chunks_are_Disk_Copy_s_bytes` and
  `Small_chunks_are_Disk_Copy_s_bytes` compress a 2 MB volume ClassicMac made (`TestData/Adc/src2m.dsk.gz`: `format`
  2M, then a 1,500,000-byte file of byte *i* = (*i* × 7 + *i* / 251) & `$FF`) in Disk Copy 6.3.3's 512-sector and
  7-sector chunks and match the SHA-256 of every chunk Disk Copy wrote for it, and its margins (+$48 513 and 8)
  [Verified: Disk Copy 6.3.3, Read-Only Compressed]. What `Compress` writes decodes to its input (empty, one byte, zeros, random
  bytes, text, a pattern, repeats past the window); zeros shrink to 67-byte matches and random bytes grow only by their
  run headers; the short and long forms where each fits; nothing reaches past the window.

- `tests/ClassicMac.Files.Tests/NdifTests.cs`: `ADC_opcodes_decode` (a literal run, a short match, an overlapping
  long match; a match before the start, truncated input, a token passing the end detected before anything is
  written); `Damaged_data_is_reported` (a match before any output in an image is `ndif.bad-chunk`).
- Disk Copy 6.3.3's NDIF images `S800 ADC.img`, `S800 ADC.smi`, `S5M ADC.img` and `S5M ADC del.img` decode to the
  read/write images of the same volumes, and its `ndiftest` images `adc_flip.img` and `adc_garb.img` are reported as
  damaged ([ndif.md §7](../disk-images/ndif.md#7-verification); not committed).
- `tests/ClassicMac.Files.Tests/UdifTests.cs`: `Every_run_type_decodes_and_the_checksums_match` (a synthetic ADC run)
  and `Disk_Copy_6_5_images_decode_to_their_device` (Disk Copy 6.5b13's compressed image, with its checksums;
  [udif.md §7](../disk-images/udif.md#7-verification); not committed).

## 8. Not covered

- The single-tree mode (chunks under 200 bytes) has no Disk Copy sample: Disk Copy's smallest chunk is one sector.

## 9. References

1. Disk Copy 6.3.3 (Apple), its `hdi2` codec resources, traced in disassembly.
