# DART RLE

DART's "fast" compression: a run-length code over big-endian 16-bit words, each count followed by literal words or by
one word to repeat. DART files use it for their blocks, and NDIF names it as chunk type `$81` for the map Disk Copy
builds for DART files. ClassicMac decodes it.

| | |
| --- | --- |
| Used by | [dart.md](../disk-images/dart.md) (compression 0), [ndif.md](../disk-images/ndif.md) (chunk type `$81`) |
| ClassicMac | Reads; `ClassicMac.Files.Compression.DartRle` |
| Verified against | DART 1.5.3's "fast" files |
| Sources | Disk Copy 6.3.3's codec, traced |

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

The input is a sequence of runs [Code: Disk Copy 6.3.3] [Verified: DART 1.5.3]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Count *n* | `i16` |
| +$02 | 2 × *n* | Literal words | When *n* ≥ 0 |
| +$02 | 2 | Repeated word | When *n* < 0 |

A DART header counts an RLE block's stored length in words ([dart.md §1.2](../disk-images/dart.md#12-blocks))
[Code: Disk Copy 6.3.3].

## 2. Reading

[Code: Disk Copy 6.3.3] [Verified: DART 1.5.3]

1. Read a count *n*.
2. If *n* ≥ 0, copy the next *n* words to the output.
3. If *n* < 0, write the next word −*n* times.
4. Repeat until the output is full. A DART block is always exactly 10,480 words (20,960 bytes).

Disk Copy refuses a count with |*n*| ≥ 10,481, with −50 [Code: Disk Copy 6.3.3].

## 3. Writing

None.

## 4. Variants

None.

## 5. ClassicMac

- A run that would pass the end of the output, or input that runs out before the output is full, is a damaged block;
  the bytes decoded before it are kept and the rest reads as zeros. [ClassicMac]

## 6. Diagnostics

The decoder reports only whether it filled the output; the image readers raise the diagnostic.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `dart.bad-block` | Error | An RLE block in a DART file does not decode to 20,960 bytes | The rest of the block reads as zeros | −50 for a count of 10,481 or more |
| `ndif.bad-chunk` | Error | A `$81` chunk in an NDIF image does not fill its output | The rest of the chunk reads as zeros | −50 for a count of 10,481 or more |

## 7. Verification

- `tests/ClassicMac.Files.Tests/NdifTests.cs`, `DART_RLE_chunks_decode`: literal words and a repeated word fill 512
  bytes; truncated input fails.
- `tests/ClassicMac.Files.Tests/DartTests.cs`: `DART_images_read_as_their_disk` (synthetic RLE blocks as literal runs)
  and `DART_153_files_decode_to_their_source_disks` (DART 1.5.3's own "fast" files decode to their source disks;
  [dart.md §7](../disk-images/dart.md#7-verification); not committed).

## 8. Not covered

- Writing DART RLE.
- No NDIF file with `$81` chunks has been seen ([ndif.md §1.4](../disk-images/ndif.md#14-chunk-types)).

## 9. References

1. Disk Copy 6.3.3 (Apple), its RLE codec, traced in disassembly.
