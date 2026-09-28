# Colour tables and palettes — an implementer's specification

This document describes the classic Mac OS colour table resource (`'clut'`) and the Palette Manager's palette resource
(`'pltt'`), completely enough to write a reader without reading ClassicMac's code, and it specifies what ClassicMac
writes for them: JSON with the exact colours, and an Adobe colour table (`.act`).

References:

- *Inside Macintosh: Imaging With QuickDraw* (1994), Color QuickDraw: `ColorTable` and `ColorSpec`.
- *Inside Macintosh: Advanced Color Imaging* (1994), Palette Manager: the palette resource and the usage constants.
- Adobe's *Photoshop File Formats Specification*: the Color Table (`.act`) file.

Contents

1. [Conventions](#1-conventions)
2. [Colour tables (clut)](#2-colour-tables-clut)
3. [Palettes (pltt)](#3-palettes-pltt)
4. [Output](#4-output)
5. [Diagnostics](#5-diagnostics)

---

## 1. Conventions

The shared conventions of [README.md](README.md) hold. An `RGBColor` is three `u16` (red, green, blue), each 0–65535.
Tags are those of [README.md](README.md); **[ClassicMac]** marks ClassicMac's own choices.

---

## 2. Colour tables (clut)

A `ColorTable` [Doc] (*Imaging With QuickDraw*):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `i32` | Seed: identifies the table's contents for colour matching |
| +$04 | 2 | `u16` | Flags: bit 15 set for a graphics device's table |
| +$06 | 2 | `i16` | Number of entries less one (−1: none) |
| +$08 | 8 × *n* | `ColorSpec` | Entries: `i16` value, `RGBColor` |

In a pixel map's table (bit 15 clear) each entry's value is the pixel value it stands for. In a device's table (bit 15
set) the entries are in pixel-value order, and the value field holds other information [Doc].

The same structure appears inside pictures, colour icons and pixel patterns ([QuickDraw.Pict's PICT-FORMAT.md](https://github.com/inexin/QuickDraw.Pict)),
and as the window, dialog and control colour tables of [INTERFACE.md](INTERFACE.md) §10, where the value is a part
code.

---

## 3. Palettes (pltt)

A `Palette` as stored [Doc] (*Advanced Color Imaging*):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `i16` | Number of entries |
| +$02 | 14 | | Reserved: the Palette Manager's private fields |
| +$10 | 16 × *n* | `ColorInfo` | Entries |

Each `ColorInfo`: an `RGBColor`, a `u16` usage, a `u16` tolerance (how far the matched colour may be, 0–65535 per
component; 0 exact), and 6 private bytes.

Usage [Doc] (`Palettes.h`): 0 `pmCourteous`; bits $0001 `pmDithered`, $0002 `pmTolerant`, $0004 `pmAnimated`, $0008
`pmExplicit`, $0010 `pmWhite`, $0020 `pmBlack`, $0100 `pmInhibitG2`, $0200 `pmInhibitC2`, $0400 `pmInhibitG4`, $0800
`pmInhibitC4`, $1000 `pmInhibitG8`, $2000 `pmInhibitC8`.

---

## 4. Output

Two files per resource [ClassicMac]:

- **`.json`** (the main file): for a `'clut'`, `seed`, `flags`, `device` (bit 15); then `entries[]`: `index` (the
  pixel value: the value field, or the position in a device's table), `value` (a `'clut'`'s stored value), `red`,
  `green`, `blue` (0–65535), `hex` (`#rrggbb`, the high bytes), and for a `'pltt'` `usage`, `usageNames` and
  `tolerance`. UTF-8, indented by two spaces, LF line ends.
- **`.act`**: 256 RGB triplets of one byte each (the high bytes), unused ones black, each entry at its index (entries
  with an index outside 0–255 are left out), then the number of colours (the highest index used plus one) and the
  transparent index ($FFFF, none) as big-endian words: 772 bytes.

The decoders are `color.table` (`'clut'`) and `color.palette` (`'pltt'`), version 1. The viewer shows a palette as a
grid of swatches, 16 to a row, in entry order.

---

## 5. Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `color.short` | Warning | The data ends before the entries it counts (or before the header); the entries read are written |
