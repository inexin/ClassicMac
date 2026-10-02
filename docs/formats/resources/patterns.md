# Patterns (PAT, PAT#, ppat, ppt#)

The QuickDraw pattern resources: `PAT `, an 8 × 8 1-bit pattern; `PAT#`, a list of them; `ppat`, a pixel pattern with
its own PixMap and colour table (or an RGB colour QuickDraw dithers); and `ppt#`, a list of pixel patterns that
ResEdit reads itself. The System, the Control Panel and applications such as paint programs hold them. ClassicMac reads
every one into an image.

| | |
| --- | --- |
| Identified by | Resource types `PAT `, `PAT#`, `ppat`, `ppt#` |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Images.QuickDrawResources` (`DecodePattern`, `DecodePatternList`, `DecodePixelPattern`, `DecodePixelPatternList`), the `image.pattern` decoder |
| Verified against | ResEdit 2.1.3's `ppat` 1731 and `ppt#` 1751 and 3100, in SheepShaver, Mac OS 9.0 |
| Sources | *Inside Macintosh: Imaging With QuickDraw* (Pattern, PixPat, PixMap, ColorTable); Mac OS 9.0's GetPixPat, traced in disassembly |

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

### 1.1 PAT and PAT#

| Type | Offset | Size | Field | Notes |
| --- | --- | --- | --- | --- |
| `PAT ` | +$00 | 8 | pattern | 8 rows of one byte, most significant bit first; 1 is black |
| `PAT#` | +$00 | 2 | count | `u16` |
| | +$02 | 8 × count | patterns | Each as a `PAT ` |

[Doc]

### 1.2 ppat

A flattened PixPat [Doc: *Imaging With QuickDraw*]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | patType | 0 a 1-bit pattern, 1 a pixel pattern, 2 an RGB pattern [Doc]; 3 is drawn as 1 [Code] |
| +$02 | 4 | patMap | Offset of the PixMap |
| +$06 | 4 | patData | Offset of the pixel data |
| +$0A | 4 | patXData | Reserved, 0 |
| +$0E | 2 | patXValid | Reserved |
| +$10 | 4 | patXMap | Reserved, 0 |
| +$14 | 8 | pat1Data | The 1-bit pattern for 1-bit screens |
| patMap | 50 | PixMap | A PixMap record ([quickdraw.md §1.7](../graphics/quickdraw.md#17-bitmaps-and-pixmaps)); pmTable is the offset of its colour table |
| patData | to pmTable | pixel data | |
| pmTable | 8 + 8 × (ctSize + 1) | colour table | A ColorTable ([quickdraw.md §1.8](../graphics/quickdraw.md#18-colour-tables)) |

### 1.3 ppt#

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | count | `u16` |
| +$02 | 4 × count | offsets | `u32`, from the resource start |
| offsets | | elements | Each a complete flattened `ppat`, its own offsets relative to the element's start; element *i* ends where element *i* + 1 begins |

[Verified: ResEdit 2.1.3]

## 2. Reading

### 2.1 ppat

GetPixPat [Code]:

1. The pixel data runs from patData to pmTable. A table before the pixel data makes GetPixPat fail, so the resource
   does not load.
2. The ColorTable at pmTable is read unless the PixMap is RGB direct (pixelType 16).
3. The table's ctSize (entries − 1) is signed: $FFFF is an empty 8-byte table. GetPixPat copies (ctSize + 1) × 8 + 8
   bytes, not bounded by the resource. A 1-bit pattern with an empty table draws 0 white and 1 black, whatever the
   port's colours [Code] [Verified].
4. By patType:
   - 0: the pattern is the first 8 bytes of the pixel data (at patData), not pat1Data.
   - 1 and 3: the PixMap.
   - 2 (RGB): the colour is the ColorTable's entry 4 (table + $2A); the resource's pixels are ignored. A 32-bit screen
     draws it solid; other depths draw PatDither's 2 × 2 cell ([quickdraw.md §4.6](../graphics/quickdraw.md#46-screen-depths)).
   - Above 3: Mac OS 9 fails to load it.

### 2.2 ppt#

No Toolbox routine loads a `ppt#`; ResEdit reads it itself [Code]. Each element is read as a `ppat` (§2.1).

## 3. Writing

None.

## 4. Variants

The four pattern types (§2.1 step 4). The ROM's GetPixPat was not traced.

## 5. ClassicMac

- `PAT ` gives an 8 × 8 image, black on white; `PAT#` and `ppt#` one image per pattern, numbered
  ([export-manifest.md §8.6](../output/export-manifest.md#86-patterns)); a `ppat` its pattern at its own size.
  [ClassicMac]
- A type 2 `ppat` is drawn as a 32-bit screen draws it: an 8 × 8 solid of the colour. [ClassicMac]
- `PAT#` reads the patterns that fit; a `ppat` under 28 bytes (or an element running past the data), of a type above
  3, with its table before its pixel data or pixel data shorter than its PixMap, a PixMap of a pixelSize that is not a
  QuickDraw depth, or tables past the data is refused (`image.undecodable`). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `image.undecodable` | Warning | The resource is shorter than its layout, a `ppat` type is above 3, its table precedes its pixel data, or its PixMap is impossible | Exports the resource raw | GetPixPat fails for a type above 3 or a table before the data [Code]; otherwise not traced |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/QuickDrawResourceTests.cs`: `PixelPattern_ReadsItsPixMapAndColorTable`,
  `PixelPattern_CtSizeMinusOne_IsAnEmptyTable` (the layout of ResEdit 2.1.3's `ppat` 1731),
  `PixelPattern_Type0_UsesTheFirstBytesOfThePixelData`, `PixelPattern_Type2_IsSolidInTheColorOfTableEntry4`,
  `PatternList_AndSmallIcons_DecodeEveryEntry`.
- `tests/ClassicMac.Resources.Decoders.Tests/ImageDecoderTests.cs`, `Patterns_are_eight_by_eight`.
- ResEdit 2.1.3's `ppat` 1731 and `ppt#` 1751/3100 elements end with an empty table and load in Mac OS 9.0 [Verified].
- Golden fixtures `PAT ` 128, `PAT#` 128, `ppat` 128, `ppt#` 128 (`GoldenFixtures`; hashes in
  `tests/ClassicMac.Resources.Decoders.Tests/Golden/golden.json`).

## 8. Not covered

- Writing patterns.
- A type 2 `ppat` at depths below 32 bits (PatDither) in the export; the renderer draws it ([quickdraw.md §4.6](../graphics/quickdraw.md#46-screen-depths)).

## 9. References

1. Apple, *Inside Macintosh: Imaging With QuickDraw* (1994): the Pattern and PixPat records, GetPixPat.
