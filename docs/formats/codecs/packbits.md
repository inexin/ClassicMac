# PackBits

Apple's run-length code for bitmap rows: a flag byte announces either a literal block of units or one unit repeated.
QuickDraw pictures pack their scan lines with it, MacPaint documents their rows, and QuickTime's Planar RGB codec
its planes. ClassicMac decodes it in all three and encodes it when it writes pictures.

| | |
| --- | --- |
| Used by | [pict.md](../graphics/pict.md) (packed scan lines), [macpaint.md](../graphics/macpaint.md) (the image rows), [quicktime.md](../graphics/quicktime.md) (`8BPS` planes, the `PNTG` codec), StuffIt's method 6 ([stuffit.md](../archives/stuffit.md)) |
| ClassicMac | Reads and writes; `ClassicMac.Core.PackBits` (`Unpack`, `Pack`), used by `ClassicMac.Graphics` (`PixMap`, `PictWriter`, `MacPaintFile`, QuickTime) and `StuffItReader` |
| Verified against | Pictures drawn on Mac OS 9.0 ([pict.md](../graphics/pict.md)) |
| Sources | Technical Note 1023, *Understanding PackBits*; *Inside Macintosh: Imaging With QuickDraw*; Mac OS 9.0's native QuickDraw, traced |

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

### 1.1 Runs

Packed data is a sequence of runs [Doc: Technical Note 1023]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Flag *n* | `i8` |
| +$01 | (*n* + 1) units | Literal units | When *n* ≥ 0 |
| +$01 | 1 unit | Repeated unit | When −127 ≤ *n* ≤ −1: written 1 − *n* times (2–128) |

A flag of −128 (`$80`) has no operand (§4.1).

A unit is a byte, except for word packing (a PICT PixMap's packType 3, 16-bit pixels), where it is two bytes
[Doc: Inside Macintosh: Imaging With QuickDraw].

### 1.2 A packed scan line in a picture

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 or 2 | Byte count | A `u8`, or a `u16` when rowBytes > 250 |
| … | byte count | Runs | §1.1 |

[Doc: Inside Macintosh: Imaging With QuickDraw]. Which rows of a picture are packed, and how, is
[pict.md §2.4](../graphics/pict.md#24-pixel-data). MacPaint's rows have no byte count: the runs of all 720 rows follow
one another ([macpaint.md](../graphics/macpaint.md)). QuickTime's `8BPS` gives a `u16` byte count for every row of
every plane before the rows ([quicktime.md](../graphics/quicktime.md)).

## 2. Reading

For one scan line [Doc: Technical Note 1023]:

1. Read the byte count, where there is one, and take that many bytes.
2. Read a flag *n*.
3. *n* ≥ 0: copy the next *n* + 1 units.
4. *n* < 0 and *n* ≠ −128: write the next unit 1 − *n* times.
5. *n* = −128: as §4.1 says.
6. Repeat from step 2. Stop at the row length; bytes left inside the counted block are ignored.

## 3. Writing

What ClassicMac's picture writer produces [ClassicMac]:

1. A run of 3 or more equal units, or of 2 or more when packing words, is written as (1 − count, unit).
2. Everything else is written in literal blocks (count − 1, units …). A literal block ends where a run of 3 equal
   units begins.
3. At most 128 units go in one run or one block.
4. A flag of −128 is never written.

The byte count before each row is [pict.md §3](../graphics/pict.md#3-writing)'s.

## 4. Variants

### 4.1 The `$80` flag

- The 68k ROM's QuickDraw skips a flag of −128 (`$80`): it is a no-op [Doc: Technical Note 1023].
- Mac OS 9's native QuickDraw reads it as a run: 129 copies of the next unit [Code: Mac OS 9.0 NQD].

## 5. ClassicMac

- One decoder and one packer, `ClassicMac.Core.PackBits`: `Unpack(source, destination, options)` takes the unit size
  (1, or 2 for word packing) and whether the flag −128 is a run (§4.1), and reports the bytes read and written and why
  it stopped: the input used up, the output full, or a run that the input or the output cannot complete
  (`PackBitsEnd`). Each format decides what a short stop means: pictures, MacPaint and QuickTime keep what was
  written; StuffIt's method 6 makes the fork unreadable. A repeat writes whole units only, so a word that does not fit
  at a row's end is left out. `Pack(data, unitSize)` is §3's packer. [ClassicMac]
- Mac OS 9's component-plane reader (32-bit pictures, packType 4) keeps its own loop, as it reproduces Mac OS 9's
  overlapping buffers ([pict.md](../graphics/pict.md)). [ClassicMac]
- Picture scan lines follow `PictDecodeOptions.QuickDraw` for the `$80` flag (§4.1): a no-op for `MacRom`, a run of
  129 for `MacOS9`. [ClassicMac]
- MacPaint rows and `8BPS` planes always treat `$80` as a no-op. [ClassicMac]
- Input that ends inside a run stops the line; the units not written stay zero. Units beyond the row length are
  dropped. [ClassicMac]

## 6. Diagnostics

None. A short or malformed run leaves the rest of the row zero; it is not reported.

## 7. Verification

- `tests/ClassicMac.Core.Tests/PackBitsTests.cs`: Technical Note 1023's example, the `$80` flag both ways, word units,
  each way of stopping, and packing (runs, literal blocks, the 128-unit cap, no `$80` flag, round trips).
- `tests/ClassicMac.Graphics.Tests/PictParserTests.cs`, pictures built with `PictBuilder`:
  `PackBits_OnMacOS9_FlagMinus128_IsARunOf129` (§4.1), `IndexedPixMap_PackType1_IsStillPacked`,
  `DirectBits16_PackType0_OnMacOS9_IsWordPackBits` (word packing), `DirectBits32_PackType4_OnMacOS9_OverflowingRowRepeatsItsFirstByte`
  (component planes) and packed pixel-pattern data.
- `PictReaderTests`: pictures written by `PictWriter` read back. `QuickTimeTests`: an `8BPS` image.
- The golden pictures of `tests/golden/pict` against Mac OS 9.0 screenshots ([pict.md](../graphics/pict.md);
  `tests/golden/README.md`).

## 8. Not covered

- Whether `$80` is a run anywhere else on Mac OS 9 (MacPaint documents and QuickTime planes are not decoded by
  QuickDraw's scan-line reader).

## 9. References

1. Apple, Technical Note 1023, *Understanding PackBits*.
2. Apple, *Inside Macintosh: Imaging With QuickDraw*, "Pixel maps" and the picture opcodes.
3. Mac OS 9.0's native QuickDraw (NQD), traced in disassembly.
