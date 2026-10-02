# Colour tables and palettes

The colour table resource (`'clut'`) of Color QuickDraw and the palette resource (`'pltt'`) of the Palette Manager.
A colour table maps pixel values to colours; a palette lists the colours a window wants, each with a usage and a
tolerance. ClassicMac reads both and writes each as JSON with the exact colours and as an Adobe colour table
(`.act`).

| | |
| --- | --- |
| Identified by | Resource types `'clut'` and `'pltt'` |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Colors.Palettes`, the decoders `color.table` and `color.palette` |
| Verified against | Nothing yet |
| Sources | *Inside Macintosh: Imaging With QuickDraw* (Color QuickDraw), *Inside Macintosh: Advanced Color Imaging* (Palette Manager), `Palettes.h`; Adobe's *Photoshop File Formats Specification* for `.act` |

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

An `RGBColor` is three `u16`: red, green, blue, each 0–65535 [Doc: Imaging With QuickDraw].

### 1.1 Colour tables (clut)

A `ColorTable` [Doc: Imaging With QuickDraw]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | ctSeed | `i32`; identifies the table's contents for colour matching |
| +$04 | 2 | ctFlags | `u16`; bit 15 set for a graphics device's table |
| +$06 | 2 | ctSize | `i16`; the number of entries less one (−1: none) |
| +$08 | 8 × n | ctTable | The entries, each a `ColorSpec` |

A `ColorSpec` [Doc: Imaging With QuickDraw]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | value | `i16`; see below |
| +$02 | 6 | rgb | `RGBColor` |

In a pixel map's table (bit 15 clear) each entry's value is the pixel value it stands for. In a device's table (bit 15
set) the entries are in pixel-value order, and the value field holds other information [Doc: Imaging With
QuickDraw].

The same structure appears inside pictures, colour icons and pixel patterns ([pict.md](../graphics/pict.md),
[icons.md](icons.md)), and as the window, dialog and control colour tables of
[windows-dialogs.md](windows-dialogs.md#1-layout), where the value is a part code.

### 1.2 Palettes (pltt)

A `Palette` as stored [Doc: Advanced Color Imaging]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | pmEntries | `i16`; the number of entries |
| +$02 | 14 | Reserved | The Palette Manager's private fields |
| +$10 | 16 × n | pmInfo | The entries, each a `ColorInfo` |

A `ColorInfo` [Doc: Advanced Color Imaging]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 6 | ciRGB | `RGBColor` |
| +$06 | 2 | ciUsage | `u16`; the usage bits below |
| +$08 | 2 | ciTolerance | `u16`; how far the matched colour may be, 0–65535 per component; 0 is exact |
| +$0A | 6 | Reserved | Private |

The usage values [Doc: `Palettes.h`]:

| Value | Name |
| --- | --- |
| 0 | `pmCourteous` |
| $0001 | `pmDithered` |
| $0002 | `pmTolerant` |
| $0004 | `pmAnimated` |
| $0008 | `pmExplicit` |
| $0010 | `pmWhite` |
| $0020 | `pmBlack` |
| $0100 | `pmInhibitG2` |
| $0200 | `pmInhibitC2` |
| $0400 | `pmInhibitG4` |
| $0800 | `pmInhibitC4` |
| $1000 | `pmInhibitG8` |
| $2000 | `pmInhibitC8` |

## 2. Reading

### 2.1 A colour table

1. Read the 8-byte header; the entry count is ctSize + 1, so −1 gives none [Doc: Imaging With QuickDraw].
2. Read the entries, 8 bytes each, from +$08.
3. With bit 15 of ctFlags clear, an entry's pixel value is its value field; with it set, its position in the table
   [Doc: Imaging With QuickDraw].

### 2.2 A palette

1. Read pmEntries; skip the 14 reserved bytes.
2. Read the entries, 16 bytes each, from +$10. An entry's index is its position.

What the Mac does with a table or palette shorter than its count is not traced.

## 3. Writing

None.

## 4. Variants

None.

## 5. ClassicMac

- Data shorter than the header (8 bytes for a `'clut'`, 16 for a `'pltt'`) gives no entries; data that ends before the
  counted entries gives the whole entries before the end. Both are reported. Bytes after the counted entries are
  ignored. A negative count gives no entries. [ClassicMac]
- Each resource is written as two files [ClassicMac]:
  - `.json` (the main file): for a `'clut'`, `seed`, `flags` and `device` (bit 15); then `entries[]`, each with
    `index` (the pixel value: the value field, or the position in a device's table), `value` (a `'clut'`'s stored
    value), `red`, `green`, `blue` (0–65535), `hex` (`#rrggbb`, from the high bytes), and for a `'pltt'` `usage`,
    `usageNames` (the names of §1.2 whose bits are set; none for `pmCourteous`) and `tolerance`. UTF-8, indented by
    two spaces, LF line ends.
  - `.act`: 256 RGB triplets of one byte each (the high bytes), unused ones black, each entry at its index (an index
    outside 0–255 is left out), then two big-endian words: the number of colours (the highest index used plus one)
    and the transparent index ($FFFF, none). 772 bytes in all [Doc: Photoshop File Formats Specification, Color
    Table].
- The decoders are `color.table` (`'clut'`) and `color.palette` (`'pltt'`), version 1. [ClassicMac]
- The viewer shows a table or palette as a grid of 16 × 16-pixel swatches, 16 to a row, in entry order, with a white
  gap between swatches, captioned with the number of colours. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `color.short` | Warning | The data ends before the header or before the entries it counts | Writes the entries read | Not traced |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs`, compared with `Golden/clut-128.json`,
  `clut-129.json` and `pltt-128.json` (`GoldenTests`), made in code with no Apple data: a pixel map's table whose values
  are the indices, with two stray bytes after it; a device table, indexed by position; a palette with usages and
  tolerances.
- `tests/ClassicMac.App.Tests/PalettePreviewTests.cs`: an 18-entry table previews as two rows of swatches, 256 × 32,
  captioned "18 colours".

Nothing is checked against colour tables or palettes from real software.

## 8. Not covered

- The Palette Manager's matching (tolerance, animation, inhibit bits) and the Color Manager's inverse tables: the
  resources are listed, not applied.
- Writing `'clut'` or `'pltt'` resources.

## 9. References

1. Apple, *Inside Macintosh: Imaging With QuickDraw* (1994), Color QuickDraw: `ColorTable`, `ColorSpec`.
2. Apple, *Inside Macintosh: Advanced Color Imaging* (1994), Palette Manager: the palette resource and the usage
   constants; `Palettes.h` (Universal Interfaces).
3. Adobe, *Photoshop File Formats Specification*, Color Table (`.act`).
