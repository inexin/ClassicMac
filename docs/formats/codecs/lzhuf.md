# DART LZH (LZHUF)

DART's "best" compression is Haruhiko Okumura and Haruyasu Yoshizaki's LZHUF: LZSS over a 4,096-byte ring buffer
with matches of 3–60 bytes, coded with an adaptive Huffman tree for characters and lengths and fixed tables for the
upper bits of positions. Disk Copy 6.3.3's codec follows the published `lzhuf.c` with a few differences (§4). DART
files use it for their blocks, and NDIF names it as chunk type `$82` for the map Disk Copy builds for DART files.
ClassicMac decodes it.

| | |
| --- | --- |
| Used by | [dart.md](../disk-images/dart.md) (compression 1), [ndif.md](../disk-images/ndif.md) (chunk type `$82`); its adaptive tree also by StuffIt's method 5 ([stuffit-methods.md §2.5](stuffit-methods.md#25-method-5-lzah)) and LHA ([lha.md](../archives/lha.md)) |
| ClassicMac | Reads; `ClassicMac.Files.Compression.DartLzh` |
| Verified against | DART 1.5.3's "best" files, decoding to their source disks |
| Sources | `lzhuf.c` (1988), the authors' published code; Disk Copy 6.3.3's codec, traced; Disk Copy 6.1.2 and 6.5b13 compared |

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

### 1.1 Constants and tables

`N` = 4096, `F` = 60, `THRESHOLD` = 2, `N_CHAR` = 256 − `THRESHOLD` + `F` = 314 (256 literals and 58 lengths),
`T` = 2 × `N_CHAR` − 1 = 627 (tree nodes), `R` = `T` − 1 = 626 (root), `MAX_FREQ` = `$8000` [Author: `lzhuf.c`].

The position tables map the first byte read for a position to its upper 6 bits (`d_code`) and to the total number of
bits in its code (`d_len`) [Author: `lzhuf.c`]:

| First byte | `d_code` | `d_len` |
| --- | --- | --- |
| `$00`–`$1F` | 0 | 3 |
| `$20`–`$4F` | 1–3, 16 bytes each | 4 |
| `$50`–`$8F` | 4–11, 8 bytes each | 5 |
| `$90`–`$BF` | 12–23, 4 bytes each | 6 |
| `$C0`–`$EF` | 24–47, 2 bytes each | 7 |
| `$F0`–`$FF` | 48–63, 1 byte each | 8 |

A block decodes to 20,960 bytes in DART ([dart.md §1.2](../disk-images/dart.md#12-blocks)) and to the chunk's size in
NDIF.

## 2. Reading

### 2.1 Bit input

Bits are taken most significant first from a 16-bit buffer refilled a byte at a time whenever it holds 8 bits or
fewer [Author: `lzhuf.c`]. Past the end of the input, a refill made while reading a bit supplies zeros and one made
while reading a byte supplies ones [Code: 6.3.3].

### 2.2 The adaptive tree

Arrays: `freq[T + 1]` (16-bit), `prnt[T + N_CHAR]`, `son[T]` [Author: `lzhuf.c`].

**Start** (at the start of every block, §4):

1. For each `i` < `N_CHAR`: `freq[i] = 1`, `son[i] = i + T`, `prnt[i + T] = i`.
2. For `j` from `N_CHAR` to `R`, with `i` = 0, 2, 4, …: `freq[j] = freq[i] + freq[i + 1]`, `son[j] = i`,
   `prnt[i] = prnt[i + 1] = j`.
3. `freq[T] = $FFFF`, `prnt[R] = 0`.

**Decode a character:** `c = son[R]`; while `c < T`, `c = son[c + bit]`; the symbol is `c − T`; update it.

**Update** symbol `c`:

1. If `freq[R]` = `MAX_FREQ`, reconstruct first.
2. `c = prnt[c + T]`.
3. `k = ++freq[c]`. If `k > freq[c + 1]`: set `l = c + 1` and advance `l` while `k > freq[l + 1]`; then swap the
   nodes: `freq[c] = freq[l]`, `freq[l] = k`; `i = son[c]`, `prnt[i] = l` (and `prnt[i + 1] = l` if `i < T`);
   `j = son[l]`, `son[l] = i`; `prnt[j] = c` (and `prnt[j + 1] = c` if `j < T`); `son[c] = j`; `c = l`.
   `k` is compared as a signed 16-bit number with the unsigned counts, so a count of `$8000` never moves
   [Code: 6.3.3].
4. `c = prnt[c]`; repeat from step 3 until `c` is 0.

**Reconstruct** [Author: `lzhuf.c`]:

1. Gather the leaves (`son[i] ≥ T`) to the front in order, halving each count rounded up (`(freq + 1) / 2`).
2. For `j` from `N_CHAR` to `T − 1`, with `i` = 0, 2, 4, …: `f = freq[i] + freq[i + 1]`; `k = j − 1`, decrease `k`
   while `f < freq[k]`, then add 1; shift `freq` and `son` from `k` to `j − 1` up by one place; `freq[k] = f`,
   `son[k] = i`.
3. For every `i` < `T`: `k = son[i]`, `prnt[k] = i`, and `prnt[k + 1] = i` if `k < T`.

### 2.3 Decoding a block

1. **Window.** The ring buffer `text[N]` persists for the whole file. At the start of every block, `text[0 … 4035]`
   is filled with zeros, while `text[4036 … 4095]` keeps what the previous block left there (zeros before the first
   block); `r` = 4036 (`N − F`). The tree is rebuilt (§2.2) [Code: 6.3.3] [Verified].
2. **Loop.** Decode a character `c`.
   - `c` < 256: output it, `text[r] = c`, `r = (r + 1) mod N`.
   - Otherwise: read a position. Take a byte `i`, keep `d_code[i]` as the upper 6 bits, read `d_len[i] − 2` more bits
     into `i` (`i = (i << 1) + bit`); the lower 6 bits are `i & $3F`. The source is `(r − position − 1) mod N` and
     the length `c − 253` (3–60). Copy byte by byte from the ring, writing each byte to the output and to `text[r]`,
     advancing `r`.
3. **End.** Stop when the block's bytes are written, or after any token once the bit reader has fetched a byte past
   the end of the input: DART's own files can end a block one byte short
   ([dart.md §2](../disk-images/dart.md#2-reading)) [Code: 6.3.3] [Verified].
4. A token that would write past the block's end is an error in Disk Copy (−50) [Code: 6.3.3].

## 3. Writing

None. No Disk Copy writes `$82` chunks: none has the LZH encoder; the type exists only in the map the driver builds in
memory for DART files [Code].

## 4. Variants

Disk Copy 6.3.3's codec differs from `lzhuf.c` [Code: 6.3.3] [Verified: DART 1.5.3's files decode to their source
disks]:

- the window is cleared with zeros, not spaces, and only its first 4036 bytes; its 60-byte tail carries from block
  to block (§2.3);
- the tree is rebuilt for every block;
- past the end of the input, byte reads supply ones (§2.1);
- a node's count is compared as signed 16-bit (§2.2);
- decoding ends quietly once the input has been read past its end (§2.3).

In an NDIF image, Disk Copy 6.3.3 and 6.5b13 allocate the LZH workspace once per driver and clear only ring bytes
0–`$FC3` for each `$82` chunk, so the 60-byte tail is left from the last chunk decoded, in read order; 6.1.2 keeps its
state on the stack, so its tail is stack garbage [Code]. The tail makes no difference in practice: every block of
DART's own files decodes identically with a random tail and in reverse order [Verified].

## 5. ClassicMac

- In a DART file, the tail carries from block to block as in Disk Copy. In an NDIF image, each `$82` chunk starts with
  the whole ring cleared. [ClassicMac]
- A token that would write past the block's end stops at the end; it is not an error. [ClassicMac]
- A DART block that decodes to fewer than its 20,480 data bytes is reported; one short only in its tag bytes is not.
  An NDIF chunk short by more than one byte is reported. The rest reads as zeros. [ClassicMac]

## 6. Diagnostics

The decoder returns the bytes it wrote; the image readers raise the diagnostic.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `dart.bad-block` | Error | An LZH block decodes to fewer than its 20,480 data bytes | The rest of the block reads as zeros | −50 for a token past the block's end |
| `ndif.bad-chunk` | Error | A `$82` chunk decodes to fewer than its size less one byte | The rest of the chunk reads as zeros | −50 for a token past the chunk's end |

## 7. Verification

- `tests/ClassicMac.Files.Tests/DartTests.cs`, `DART_153_files_decode_to_their_source_disks`: with
  `CLASSICMAC_CORPUS` set, DART 1.5.3's own "best" files decode to their source disks with no diagnostics, the
  short last block included ([dart.md §7](../disk-images/dart.md#7-verification)). Not committed.
- The same files decode identically with a random window tail and with their blocks in reverse order (§4).

## 8. Not covered

- Writing LZH.
- No NDIF file with `$82` chunks has been seen.

## 9. References

1. Haruhiko Okumura and Haruyasu Yoshizaki, `lzhuf.c` (1988). The authors' published code; behaviour only.
2. Disk Copy 6.3.3 (Apple), its LZH codec, traced in disassembly; Disk Copy 6.1.2 and 6.5b13 compared.
