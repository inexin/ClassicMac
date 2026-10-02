# KenCode

KenCode is NDIF chunk type `$80`, Disk Copy's "Smaller (KC)" compression [Code: Disk Copy 6.3.3 codec],
[Verified: images made by Disk Copy 6.1.2 and 6.3.3 decode to their CRC, with 20-, 32- and 512-sector chunks]; by
the release history it was the only compression of Disk Image Mounter and Disk Copy 6.0.1 [Fitted?]. It is the
**System's `dcmp` 3 codec** ([compressed-resources.md §4](../resources/compressed-resources.md#4-dcmp-3)), with the distance classes capped for large
outputs: the length, literal and distance codes are identical [Code: Mac OS 9.0 System, Disk Copy 6.3.3], and a model
of `dcmp` 3 verified in emulation decodes real KenCode chunks of up to 33 sectors byte for byte [Verified]. The two
choose the same distance class up to `$5400` bytes of output; from `$5401` `dcmp` 3 moves on to classes 11–14 while
KenCode stays at 10 ([§4](#4-distance-class)): every Disk Copy KenCode decoder (6.1.2, 6.3.3, 6.5b13, and the
self-mounting `oneb` code) also tests a window value set to `$2800`, so class 11 is never reached [Code]. A `dcmp` 3
decoder goes wrong on real KenCode images at the first match past `$5400` [Verified]. ClassicMac keeps its own KenCode
decoder.

Contents

1. [Bits](#1-bits)
2. [Tokens](#2-tokens)
3. [Length code](#3-length-code)
4. [Distance class](#4-distance-class)
5. [Distance](#5-distance)
6. [Literal count](#6-literal-count)

---

## 1. Bits

The stream is read **most significant bit first**, with no stored tables: every code is a fixed prefix code. Past the
end of the input, bits read as 0. **The decoder may not read more than 8 × the output size bits**: a read that would
pass that limit is an error (−8819 in Disk Copy) [Code: 6.3.3]. The unary runs of 1 bits below are not counted
against the limit [Code: 6.3.3].

---

## 2. Tokens

`total` counts the bytes produced so far. A flag *afterRun* starts false. Until `total` reaches the output size:

1. Read a **length code** *v* ([§3](#3-length-code)).
2. If *v* > 0, or *afterRun* is set: a **match**. Its length is *v* + 2, or *v* + 3 when *afterRun* is set (so it may
   be 3 with *v* = 0); clear *afterRun*. Read a distance ([§5](#5-distance)) for the class of `total`
   ([§4](#4-distance-class)) and copy *length* bytes from *distance* back, one at a time.
3. Otherwise (*v* = 0, *afterRun* clear): a **literal run**. Read a count ([§6](#6-literal-count)), then that many
   8-bit bytes. Set *afterRun* when the count is below 63: a short run must be followed by a match.

A token may run past the output's end; the extra bytes are dropped and decoding ends [Code: 6.3.3]. A match reaching
before the output's start is not checked by Disk Copy (it reads memory before its buffer); ClassicMac reports it.

---

## 3. Length code

Count 1 bits, up to ten; the 0 that ends a shorter run is consumed. Then:

| 1 bits | Then | *v* |
| --- | --- | --- |
| 0 | 1 bit *b* | *b* (0–1) |
| 1 | bit *a* = 0 | 2 |
| 1 | bit *a* = 1, 1 bit *b* | 3 + *b* (3–4) |
| 2 | bit *a* = 0, 1 bit *b* | 5 + *b* (5–6) |
| 2 | bit *a* = 1, 2 bits *b* | 7 + *b* (7–10) |
| 3 | 3 bits | 11–18 |
| 4 | 3 bits | 19–26 |
| 5 | 5 bits | 27–58 |
| 6 | 6 bits | 59–122 |
| 7 | 7 bits | 123–250 |
| 8 | 8 bits | 251–506 |
| 9 | 9 bits | 507–1018 |
| 10 | 10 bits | 1019–2042 |

[Code: 6.3.3], [Verified]

---

## 4. Distance class

The distance code's width class *k* grows with `total` [Code: 6.3.3]:

| `total` below | `$B` | `$15` | `$29` | `$51` | `$A1` | `$2A1` | `$3E9` | `$A81` | `$1501` | `$2A01` | otherwise |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| *k* | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |

Disk Copy's table goes on to classes 11–14 (from `$5401`, as in `dcmp` 3), but each of those needs a window of
`$4001` bytes or more, and the decoder's workspace is set up with a `$2800`-byte window, so KenCode stays at class 10
for all output from `$2A01` on [Code: 6.3.3], [Verified: 512-sector chunks, 256 KB each, decode to the stored CRC].

---

## 5. Distance

| Prefix | Then | Distance |
| --- | --- | --- |
| `0` | *k* bits *x* | *x* + 1 |
| `10` | *k* + 2 bits *x* | 2^*k* + *x* + 1 |
| `11` | *w* bits *x* | 5 × 2^*k* + *x* + 1 |

The width *w* after `11` depends on `total`, with *base* = 5 × 2^*k* [Code: 6.3.3]:

- *w* = 1 if `total` ≤ *base* + 2; else *w* = 2 if `total` ≤ *base* + 4;
- else start with *threshold* = *base* + 4, *step* = 4, *w* = 3 and repeat: add *step* to *threshold*; stop if
  `total` ≤ *threshold* (a threshold of `$680` is compared as `$66C`, a quirk of Disk Copy's code) or *w* = *k* + 4;
  otherwise double *step* and add 1 to *w*.

---

## 6. Literal count

| Bits | Count |
| --- | --- |
| `0` | 1 |
| `1 00` | 2 |
| `1 01` | 3 |
| `1 10` + 2 bits *z* | 4 + *z* (4–7) |
| `1 11` + 4 bits *y*, *y* ≤ 7 | *y* + 8 (8–15) |
| `1 11` + 4 bits *y*, 8 ≤ *y* ≤ 11, + 2 bits *l* | 4*y* + *l* − 16 (16–31) |
| `1 11` + 4 bits *y*, *y* ≥ 12, + 3 bits *l* | 8*y* + *l* − 64 (32–63) |

[Code: 6.3.3], [Verified]

**Example.** `00 101 01100001 01100010 01100011 00 1001` decodes to `abcabc`: length code 0 with *afterRun* clear
starts a literal run of 3 (`abc`); *afterRun* is then set, so length code 0 is a 3-byte match; at `total` 3 the class
is 0, and `10` + 2 bits `01` gives distance 1 + 1 + 1 = 3.
