# DART LZH (LZHUF)

DART's "best" compression, and NDIF chunk type `$82`, is Okumura and Yoshizaki's LZHUF: LZSS over a 4,096-byte ring
buffer with matches of 3–60 bytes, coded with an adaptive Huffman tree for characters and lengths and fixed tables
for the upper bits of positions [Author: `lzhuf.c`, 1988]. Disk Copy 6.3.3's codec follows it with the differences
marked **DART** below [Code: 6.3.3], [Verified: DART 1.5.3's files decode to their source disks].

Contents

1. [Constants and tables](#1-constants-and-tables)
2. [Bit input](#2-bit-input)
3. [The adaptive tree](#3-the-adaptive-tree)
4. [Decoding a block](#4-decoding-a-block)

---

## 1. Constants and tables

`N` = 4096, `F` = 60, `THRESHOLD` = 2, `N_CHAR` = 256 − `THRESHOLD` + `F` = 314 (256 literals and 58 lengths),
`T` = 2 × `N_CHAR` − 1 = 627 (tree nodes), `R` = `T` − 1 = 626 (root), `MAX_FREQ` = `$8000` [Author].

The position tables map the first byte read for a position to its upper 6 bits (`d_code`) and to the total number of
bits in its code (`d_len`) [Author]:

| First byte | `d_code` | `d_len` |
| --- | --- | --- |
| `$00`–`$1F` | 0 | 3 |
| `$20`–`$4F` | 1–3, 16 bytes each | 4 |
| `$50`–`$8F` | 4–11, 8 bytes each | 5 |
| `$90`–`$BF` | 12–23, 4 bytes each | 6 |
| `$C0`–`$EF` | 24–47, 2 bytes each | 7 |
| `$F0`–`$FF` | 48–63, 1 byte each | 8 |

---

## 2. Bit input

Bits are taken most significant first from a 16-bit buffer refilled a byte at a time whenever it holds 8 bits or
fewer [Author]. **DART:** past the end of the input, a refill made while reading a bit supplies zeros and one made
while reading a byte supplies ones [Code: 6.3.3].

---

## 3. The adaptive tree

Arrays: `freq[T + 1]` (16-bit), `prnt[T + N_CHAR]`, `son[T]` [Author].

- **Start:** for each `i` < `N_CHAR`: `freq[i] = 1`, `son[i] = i + T`, `prnt[i + T] = i`. Then for `j` from `N_CHAR`
  to `R`, with `i` = 0, 2, 4, …: `freq[j] = freq[i] + freq[i + 1]`, `son[j] = i`, `prnt[i] = prnt[i + 1] = j`.
  Finally `freq[T] = $FFFF`, `prnt[R] = 0`. **DART:** the tree is rebuilt at the start of every block [Code: 6.3.3].
- **Decode a character:** `c = son[R]`; while `c < T`, `c = son[c + bit]`; the symbol is `c − T`; update it.
- **Update** symbol `c`: if `freq[R]` = `MAX_FREQ`, reconstruct first. Then `c = prnt[c + T]` and repeat until `c`
  is 0: `k = ++freq[c]`; if `k > freq[c + 1]`, set `l = c + 1` and advance `l` while `k > freq[l + 1]`; then swap
  the nodes: `freq[c] = freq[l]`, `freq[l] = k`; `i = son[c]`,
  `prnt[i] = l` (and `prnt[i + 1] = l` if `i < T`); `j = son[l]`, `son[l] = i`; `prnt[j] = c` (and `prnt[j + 1] = c`
  if `j < T`); `son[c] = j`; `c = l`. Then `c = prnt[c]`. **DART:** `k` is compared as a signed 16-bit number with
  the unsigned counts, so a count of `$8000` never moves [Code: 6.3.3].
- **Reconstruct:** gather the leaves (`son[i] ≥ T`) to the front in order, halving each count rounded up
  (`(freq + 1) / 2`). Then for `j` from `N_CHAR` to `T − 1`, with `i` = 0, 2, 4, …: `f = freq[i] + freq[i + 1]`;
  `k = j − 1`, decrease `k` while `f < freq[k]`, then add 1; shift `freq` and `son` from `k` to `j − 1` up by one
  place; `freq[k] = f`, `son[k] = i`. Finally, for every `i` < `T`: `k = son[i]`, `prnt[k] = i`,
  and `prnt[k + 1] = i` if `k < T` [Author].

---

## 4. Decoding a block

- **Window:** the ring buffer `text[N]` persists for the whole file. **DART:** at the start of every block,
  `text[0 … 4035]` is filled with **zeros** (lzhuf.c uses spaces), while `text[4036 … 4095]` keeps what the previous
  block left there (zeros before the first block); `r` = 4036 (`N − F`) [Code: 6.3.3], [Verified].
- **Loop:** decode a character `c`.
  - `c` < 256: output it, `text[r] = c`, `r = (r + 1) mod N`.
  - Otherwise: read a position — take a byte `i`, keep `d_code[i]` as the upper 6 bits, read `d_len[i] − 2` more
    bits into `i` (`i = (i << 1) + bit`), and the lower 6 bits are `i & $3F`. The source is
    `(r − position − 1) mod N` and the length `c − 253` (3–60). Copy byte by byte from the ring, writing each byte to
    the output and to `text[r]`, advancing `r`.
- **End:** stop when the block's 20,960 bytes are written, or **after any token once the bit reader has fetched a
  byte past the end of the input** (DART's own files can end a block one byte short, [dart.md §2](../disk-images/dart.md#2-reading))
  [Code: 6.3.3], [Verified]. A token that would write past the block's end is an error in Disk Copy (−50); ClassicMac
  stops at the end.

In an NDIF image, Disk Copy 6.3.3 and 6.5b13 allocate the LZH workspace once per driver and clear only ring bytes
0–`$FC3` for each `$82` chunk, so the 60-byte tail is left from the last chunk decoded, in read order; 6.1.2 keeps its
state on the stack, so its tail is stack garbage [Code]. The tail makes no difference in practice: every block of
DART's own files decodes identically with a random tail and in reverse order [Verified]. ClassicMac clears the whole
ring for each chunk. No Disk Copy writes `$82` chunks (none has the LZH encoder); the type exists only in the map
the driver builds in memory for DART files [Code].
