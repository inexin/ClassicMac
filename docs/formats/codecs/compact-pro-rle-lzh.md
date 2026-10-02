# Compact Pro RLE and LZH

The two fork codings of Bill Goodman's Compact Pro: a run-length code with `$81` as its escape, and an LZH stage (an
8 KiB window with canonical Huffman codes in blocks) whose output always goes through the RLE stage. Compact Pro
archives code each fork with RLE alone or with LZH followed by RLE, and DiskDoubler's method 8 (DiskDoubler B) uses
the same two codings. ClassicMac decodes both.

| | |
| --- | --- |
| Used by | [compact-pro.md](../archives/compact-pro.md) (entry flag bits 1 and 2), [diskdoubler.md §2.9](../archives/diskdoubler.md#29-method-8-compact-pro) (method 8) |
| ClassicMac | Reads; `ClassicMac.Files.Archives` (`CompactProLzhDecoder`, `CompactProReader.DecodeRle8182`) |
| Verified against | Compact Pro 1.52 on Mac OS 9.0<br>DiskDoubler 3.7.7 (method 8) |
| Sources | Other readers (behaviour only): a Compact Pro format description (docs.rs `compact-pro`), XADMaster, psx-spx's notes, pmarreck/compact_pro, munbox |

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

### 1.1 RLE

A byte stream; `$81` is the escape [Reference: pmarreck/compact_pro; munbox's sample]:

| Bytes | Meaning |
| --- | --- |
| *x* ≠ `$81` | The byte *x* |
| `$81 $82 $00` | The bytes `$81 $82` |
| `$81 $82` *n*, *n* ≥ 1 | *n* − 1 more copies of the last byte output |
| `$81` *x*, *x* ≠ `$82` | The byte `$81`, then *x* read as a new byte (§2.1) |

### 1.2 LZH blocks

Bits are read most significant first [Reference: psx-spx, compact-pro description] [Verified: Compact Pro 1.52].
Each block is:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 8 bits + 2*c* × 4 bits | Literal code | Up to 256 symbols |
| … | 8 bits + 2*c* × 4 bits | Length code | Up to 64 symbols |
| … | 8 bits + 2*c* × 4 bits | Displacement code | The upper bits of displacements; up to 128 symbols |
| … | … | Tokens | §2.2, up to the block's end |
| … | 0–7 bits, 2 or 3 bytes | Padding | §2.2 step 4 |

A code is an 8-bit count *c*, then 2*c* 4-bit code lengths, one per symbol from 0; the codes are canonical
[Reference: psx-spx, compact-pro description].

A token is:

| Bits | Token |
| --- | --- |
| `1` + a literal code | A literal byte |
| `0` + a length code + a displacement code + 6 bits | A match: the displacement is the code's value (upper bits) × 64 + the 6 bits; a displacement of 0 means 8192 |

## 2. Reading

### 2.1 RLE

1. A byte other than `$81` is output.
2. `$81 $82 $00` outputs `$81 $82`.
3. `$81 $82 n`, n ≥ 1, outputs n − 1 more copies of the last byte (`$81 $82 $01` adds none).
4. `$81 x`, for any other x, outputs `$81` and then handles x as a new byte. So after `$81 $81` the second `$81` is
   output and is itself an escape for the next byte ("half-escaped"): `81 81 82 05` gives five `$81`,
   `81 81 81 82 05` six, and `81 81 41` is `81 81 41`.
5. The output must reach the expanded length; a run with no byte before it is an error.

[Reference: pmarreck/compact_pro; munbox's sample]

### 2.2 LZH

The LZH stage's output goes through the RLE stage (§2.1) [Reference: psx-spx, compact-pro description]
[Verified: Compact Pro 1.52]:

1. Keep an 8 KiB history window.
2. Read the block's three codes (§1.2).
3. Read tokens. A literal is output and put in the window. A match copies `length` bytes from that far back in the
   window; a copy may overlap the bytes it produces.
4. Count 2 for each literal and 3 for each match. When the count reaches `$1FFF0`, the block ends: go to the next
   byte boundary, skip 2 bytes, and 1 more if that leaves an odd byte offset; the next block's codes follow.
5. Stop when the RLE stage's output reaches the expanded length.

## 3. Writing

None.

## 4. Variants

- DiskDoubler's method 8 puts a 16-byte header before the stream; its sum says whether LZH is used
  ([diskdoubler.md §2.9](../archives/diskdoubler.md#29-method-8-compact-pro)).

## 5. ClassicMac

- The window starts as zeros, so a match reaching before the first byte copies zeros. [ClassicMac]
- A code that is oversubscribed, has duplicate or prefix codes, or describes more symbols than its table holds; a code
  that is not defined; input that ends inside a code or token, or before a block's padding; RLE output past the
  expanded length; and a stream ending short of it are errors: the archive read fails. [ClassicMac]
- RLE input left over once the expanded length is reached is ignored. [ClassicMac]

## 6. Diagnostics

None. The decoders report nothing themselves; a damaged stream fails the read, and a fork checksum mismatch is
reported by the archive readers as `archive.fork-crc` ([compact-pro.md §6](../archives/compact-pro.md#6-diagnostics),
[diskdoubler.md §6](../archives/diskdoubler.md#6-diagnostics)).

## 7. Verification

- `TestData/CompactPro152` (Compact Pro 1.52 on Mac OS 9.0): forks of both codings, a long RLE fork across segments
  ([compact-pro.md §7](../archives/compact-pro.md#7-verification)).
- `TestData/CompactProMunbox/testfile.compact_pro_152.cpt` (from munbox, MIT; `CompactProSampleTests`): its LZH + RLE
  forks use the half-escape chain (`81 81 81`, `81 81 82`), its RLE-only forks do not, and no fork has `81 82 01`;
  every data fork matches munbox's MD5. `RleEscapesFollowTheHalfEscapeRule`: the chains of §2.1.
- Hand-built streams in `CompactProFeatureTests`: LZH literals in either fork, a match overlapping the bytes it
  produces, truncated code data, the block boundary.
- `DiskDoublerFeatureTests`: method 8 with LZH and RLE in hand-built records and in DiskDoubler 3.7.7's own
  `DiskDoubler377DdbTestFile.dd` ([diskdoubler.md §7](../archives/diskdoubler.md#7-verification)).

## 8. Not covered

- Writing either coding.

## 9. References

1. Compact Pro format description, <https://docs.rs/crate/compact-pro/latest>. Licence not recorded; reference only.
2. XADMaster (The Unarchiver), `XADCompactProParser.m`, `XADCompactProLZHHandle.m`, `XADCompactProRLEHandle.m`.
   LGPL-2.1; reference only.
3. psx-spx, Compact Pro notes, <https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/>.
   Documentation; licence not recorded.
4. pmarreck/compact_pro, its RLE rule as fixed against real archives. Licence not recorded; reference only.
5. munbox, <https://github.com/dafo123/munbox>, its Compact Pro sample archive. MIT; the sample is committed with
   notice (`THIRD-PARTY-NOTICES.md`), no code used.
