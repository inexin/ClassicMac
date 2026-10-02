# KenCode

KenCode is Disk Copy's "Smaller (KC)" compression, NDIF chunk type `$80`: an LZ77 bit stream of fixed prefix codes,
with no stored tables, in which literal runs and matches alternate and the width of a distance grows with the bytes
already written. It is the System's `'dcmp'` 3 codec with the distance classes capped for large outputs. ClassicMac
decodes it with its own decoder.

| | |
| --- | --- |
| Used by | [ndif.md](../disk-images/ndif.md) (chunk type `$80`); the same codec as [compressed-resources.md §2.8](../resources/compressed-resources.md#28-dcmp-3) (`'dcmp'` 3), with the difference of §4 |
| ClassicMac | Reads; `ClassicMac.Files.Compression.KenCode` |
| Verified against | Images made by Disk Copy 6.1.2 and 6.3.3, with 20-, 32- and 512-sector chunks, decoding to their CRC |
| Sources | Disk Copy 6.3.3's codec, traced; Disk Copy 6.1.2, 6.5b13 and the self-mounting `oneb` code compared; the Mac OS 9.0 System's `'dcmp'` 3 |

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

### 1.1 Bits

The stream is read most significant bit first, with no stored tables: every code is a fixed prefix code. Past the
end of the input, bits read as 0 [Code: 6.3.3].

### 1.2 Length code

Count 1 bits, up to ten; the 0 that ends a shorter run is consumed. Then [Code: 6.3.3] [Verified]:

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

### 1.3 Distance class

The distance code's width class *k* grows with `total`, the bytes produced so far [Code: 6.3.3]:

| `total` below | `$B` | `$15` | `$29` | `$51` | `$A1` | `$2A1` | `$3E9` | `$A81` | `$1501` | `$2A01` | otherwise |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| *k* | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |

Disk Copy's table goes on to classes 11–14 (from `$5401`, as in `'dcmp'` 3), but each class from 10 on also tests a
window value, and the decoder's workspace is set up with a `$2800`-byte window, below the `$4001` those classes need;
so KenCode stays at class 10 for all output from `$2A01` on [Code: 6.3.3] [Verified: 512-sector chunks, 256 KB each,
decode to the stored CRC].

### 1.4 Distance

| Prefix | Then | Distance |
| --- | --- | --- |
| `0` | *k* bits *x* | *x* + 1 |
| `10` | *k* + 2 bits *x* | 2^*k* + *x* + 1 |
| `11` | *w* bits *x* | 5 × 2^*k* + *x* + 1 |

The width *w* after `11` depends on `total`, with *base* = 5 × 2^*k* [Code: 6.3.3]:

1. *w* = 1 if `total` ≤ *base* + 2.
2. Else *w* = 2 if `total` ≤ *base* + 4.
3. Else start with *threshold* = *base* + 4, *step* = 4, *w* = 3, and repeat: add *step* to *threshold*; stop if
   `total` ≤ *threshold* (a threshold of `$680` is compared as `$66C`, a quirk of Disk Copy's code) or *w* = *k* + 4;
   otherwise double *step* and add 1 to *w*.

### 1.5 Literal count

| Bits | Count |
| --- | --- |
| `0` | 1 |
| `1 00` | 2 |
| `1 01` | 3 |
| `1 10` + 2 bits *z* | 4 + *z* (4–7) |
| `1 11` + 4 bits *y*, *y* ≤ 7 | *y* + 8 (8–15) |
| `1 11` + 4 bits *y*, 8 ≤ *y* ≤ 11, + 2 bits *l* | 4*y* + *l* − 16 (16–31) |
| `1 11` + 4 bits *y*, *y* ≥ 12, + 3 bits *l* | 8*y* + *l* − 64 (32–63) |

[Code: 6.3.3] [Verified]

## 2. Reading

`total` counts the bytes produced so far. A flag *afterRun* starts false. Until `total` reaches the output size
[Code: 6.3.3] [Verified]:

1. Read a length code *v* (§1.2).
2. If *v* > 0, or *afterRun* is set, the token is a match. Its length is *v* + 2, or *v* + 3 when *afterRun* is set
   (so it may be 3 with *v* = 0); clear *afterRun*. Read a distance (§1.4) for the class of `total` (§1.3) and copy
   *length* bytes from *distance* back, one at a time.
3. Otherwise (*v* = 0, *afterRun* clear) the token is a literal run. Read a count (§1.5), then that many 8-bit bytes.
   Set *afterRun* when the count is below 63: a short run must be followed by a match.

Checks:

- The decoder may not read more than 8 × the output size bits: a read that would pass that limit is an error, −8819 in
  Disk Copy. The unary runs of 1 bits in length codes are not counted against the limit [Code: 6.3.3].
- A token may run past the output's end; the extra bytes are dropped and decoding ends [Code: 6.3.3].
- A match reaching before the output's start is not checked by Disk Copy: it reads the memory before its buffer
  [Code: 6.3.3].

Example: `00 101 01100001 01100010 01100011 00 1001` decodes to `abcabc`. Length code 0 with *afterRun* clear starts a
literal run of 3 (`abc`); *afterRun* is then set, so length code 0 is a 3-byte match; at `total` 3 the class is 0, and
`10` + 2 bits `01` gives distance 1 + 1 + 1 = 3.

## 3. Writing

None.

## 4. Variants

- `'dcmp'` 3 ([compressed-resources.md §2.8](../resources/compressed-resources.md#28-dcmp-3)) has identical length,
  literal and distance codes [Code: Mac OS 9.0 System, Disk Copy 6.3.3]; a model of `'dcmp'` 3 verified in emulation
  decodes real KenCode chunks of up to 33 sectors byte for byte [Verified].
- The two choose the same distance class up to `$5400` bytes of output. From `$5401`, `'dcmp'` 3 moves on to classes
  11–14 while KenCode stays at 10 (§1.3): every Disk Copy KenCode decoder (6.1.2, 6.3.3, 6.5b13, and the self-mounting
  `oneb` code) tests a window value set to `$2800`, so class 11 is never reached [Code]. A `'dcmp'` 3 decoder goes
  wrong on real KenCode images at the first match past `$5400` [Verified].

## 5. ClassicMac

- ClassicMac keeps a KenCode decoder of its own rather than sharing the `'dcmp'` 3 decoder (§4). [ClassicMac]
- A match reaching before the output's start is reported as a damaged chunk instead of reading stray memory; the bytes
  decoded before the fault are kept and the rest of the chunk reads as zeros. An overread (§2) is reported the same
  way. [ClassicMac]

## 6. Diagnostics

The decoder reports how it ended; the NDIF reader raises the diagnostic.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ndif.bad-chunk` | Error | A KenCode chunk reads more than 8 bits per output byte, or a match reaches before its start | Keeps what was decoded; the rest reads as zeros | −8819 for the overread; reads stray memory for the other |

## 7. Verification

- `tests/ClassicMac.Files.Tests/NdifTests.cs`, `KenCode_streams_decode`: the example of §2; a match before any output;
  more bits than eight per output byte.
- `Disk_Copy_KenCode_image_matches_its_checksum`: with `CLASSICMAC_CORPUS` set, Disk Copy 6.3.3's "Smaller (KC)" image
  `F800 KC.img` decodes to its stored CRC; `Version_2_test_images_match_their_checksums`: the version 2 image
  `v2 kc.img` that Disk Copy 6.1.2 mounted ([ndif.md §7](../disk-images/ndif.md#7-verification)). Not committed.

## 8. Not covered

- Writing KenCode.
- By Apple's release history, KenCode was the only compression of Disk Image Mounter and Disk Copy 6.0.1; neither was
  checked.

## 9. References

1. Disk Copy 6.3.3 (Apple), its KenCode codec, traced in disassembly; Disk Copy 6.1.2 and 6.5b13 and the self-mounting
   image code compared.
2. Mac OS 9.0 System file, `'dcmp'` 3, traced in disassembly.
