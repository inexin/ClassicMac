# Bitmap fonts (NFNT, FONT, fctb)

This document describes the classic Mac OS Font Manager's resources: bitmap font strikes (`'NFNT'`, `'FONT'`), font
families (`'FOND'`), font colour tables (`'fctb'`) and outline fonts (`'sfnt'`), completely enough to write a reader
without reading ClassicMac's code. It also specifies what ClassicMac writes for them: a glyph sheet image, a BDF font
and JSON for strikes, JSON for families and colours, and the TrueType file for outline fonts.

The reader is `ClassicMac.Graphics.Fonts` (in the `ClassicMac.Graphics` package, on `ClassicMac.Core` only); the decoders are in
`ClassicMac.Resources.Decoders.Fonts`.

References:

- *Inside Macintosh: Text* (1993), Font Manager: the bitmapped font resource, the font family resource and its tables.
- The 68k ROM's Font Manager, disassembly (as traced for [quickdraw.md §7](../graphics/quickdraw.md#7-text)): the offset of the
  offset/width table, the style extra widths, the width tables' stepping.
- Mac OS 9.0, disassembly: the native Font Manager, FontObjects, QuickDraw's text drawing and ScriptUtils in the System
  file's data fork: which fields and tables are read, and how. **[Code]** below is Mac OS 9.0 unless it names the ROM.
- Apple's *TrueType Reference Manual*: the `sfnt` offset table, table directory and `name` table.
- Adobe's *Glyph Bitmap Distribution Format (BDF) Specification*, version 2.1: the BDF output.

Contents

1. [Conventions](#1-conventions)
2. [Bitmap strikes (NFNT, FONT)](#2-bitmap-strikes-nfnt-font)
3. [Font colour tables (fctb)](#3-font-colour-tables-fctb)
4. [Output](#4-output)
5. [Diagnostics](#5-diagnostics)
6. [Not covered yet](#6-not-covered-yet)

---

## 1. Conventions

The shared conventions of [README.md](../README.md) hold. A **4.12** value is an `i16` fixed-point number with 12
fraction bits, in ems (per point of size). Tags are those of [README.md](../README.md); **[ClassicMac]** marks
ClassicMac's own choices.

---

## 2. Bitmap strikes (NFNT, FONT)

`'NFNT'` and `'FONT'` have the same layout [Doc] (*Text*, "The Bitmapped Font Resource"): a header, then the strike
(every glyph's image side by side in one bitmap), then tables of one entry per character, the missing symbol, and one
more.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `u16` | fontType (below) |
| +$02 | 2 | `i16` | firstChar: the first character code |
| +$04 | 2 | `i16` | lastChar: the last character code |
| +$06 | 2 | `i16` | widMax: the widest advance |
| +$08 | 2 | `i16` | kernMax: the largest leftward kern (0 or negative) |
| +$0A | 2 | `i16` | nDescent: the negated descent; **when positive, the high word of owTLoc** [Code] (the ROM: when 0 or more) |
| +$0C | 2 | `i16` | fRectWidth: the font rectangle's width |
| +$0E | 2 | `i16` | fRectHeight: its height, ascent + descent: the strike's height |
| +$10 | 2 | `u16` | owTLoc: the offset **in words, from this field**, to the offset/width table |
| +$12 | 2 | `i16` | ascent |
| +$14 | 2 | `i16` | descent |
| +$16 | 2 | `i16` | leading |
| +$18 | 2 | `u16` | rowWords: the strike's row length in words, in pixels (1-bit units); Mac OS 9 ignores the top bit [Code] |
| +$1A | | | strike: `rowWords × 2 × depth × fRectHeight` bytes, rows top down, pixels most significant bit first |
| | 2 × *n* | `u16`[] | location table: glyph *k*'s image is columns `loc[k]` to `loc[k+1]` of the strike. Mac OS 9 finds it just before the offset/width table (at its offset − 2*n*) [Code]; the ROM right after the strike [Code: 68k ROM]; the same place in a well-formed font |
| +$10 + 2·owTLoc | 2 × *n* | `i16`[] | offset/width table: per glyph `offset << 8 | advance`, or −1 when the font lacks the character |
| | 2 × *n* | `u16`[] | glyph-width table (fontType bit 1): the advance in 8.8 fixed-point pixels |
| | 2 × *n* | `u16`[] | image-height table (fontType bit 0): `top << 8 | rows`, the rows that hold ink |

*n* = lastChar − firstChar + 3. Character *c* uses slot *c* − firstChar; slot lastChar − firstChar + 1 is the
**missing symbol**, drawn for characters outside the range or marked −1 [Doc].

**Placing a glyph** [Doc]: the pen advances by the glyph's advance; the image's left edge is at pen + kernMax + offset;
its top row is the font's ascent above the baseline.

fontType bits [Doc], and which code tests them [Code]:

| Bit | Meaning | Tested by |
| --- | --- | --- |
| 0 | The font has an image-height table | ROM |
| 1 | The font has a glyph-width table | ROM, Mac OS 9 |
| 2–4 | Depth code: 0 1-bit, 1 2-bit, 2 4-bit, 3 8-bit (4 and 5, 16 and 32 bits, only in synthetic strikes) | ROM bits 2–4, Mac OS 9 bits 2–3 |
| 7 | The font has a font colour table (`'fctb'`, §3) | ROM |
| 8 | A synthetic strike (set only in memory) | ROM |
| 9 | The font has colours other than black | ROM |
| 12, 15 | Reserved, set (`$9000` for a proportional font) | — |
| 13 | A fixed-width font (`$B000`) | nothing traced |
| 14 | Not to be expanded to the screen's depth (international) | ROM |

A deeper strike's rows are depth × rowWords × 2 bytes; the location table still counts pixels.

ClassicMac refuses a strike under 26 bytes, or whose characters are not within 0–255 in order; tables that run past the
data leave the glyphs there out and are reported (§5) [ClassicMac].

---

## 3. Font colour tables (fctb)

A colour table (as `'clut'`, [palettes.md §2](palettes.md#2-colour-tables-clut)) of the same ID as a deeper `'NFNT'`, indexed by pixel value
[Doc] [Code]. Mac OS 9 draws any strike deeper than 1 bit with its `'fctb'`, whatever bits 7 and 9 say, or else with
the standard colour table of its depth (`'clut'` 64 + depth, the system's with the highlight colour) [Code]. The ROM
uses the `'fctb'` only when bit 7 is set, and otherwise a ramp from white (0) to black (the highest value) [Code: 68k
ROM]. ClassicMac draws a deeper strike's glyph sheet with its `'fctb'`, else the ROM's ramp [ClassicMac].

---

## 4. Output

| Type | Decoder | Files [ClassicMac] |
| --- | --- | --- |
| `NFNT`, `FONT` | `font.bitmap` | `.png` a glyph sheet: every glyph in character order, then the missing symbol, 16 to a row, each at its pen position in a cell as wide as the widest glyph, black ink on white (a colour font's pixels in its `'fctb'`'s colours, else greys); `.bdf` a BDF 2.1 font; `.json` the metrics |
| `NFNT`, `FONT` of no data | `font.bitmap` | `.json`: `familyName` (the resource name) and `family` (ID ÷ 128) |
| `FOND` | `font.family` | `.json` |
| `sfnt` | `font.outline` | `.ttf` the data unchanged (`.sfnt` when it is not TrueType); `.json` its version, names and tables |
| `fctb` | `font.colors` | `.json` |

- **The family and size of a strike** come from a `'FOND'` in the same fork whose association table lists the strike's
  ID; for a `'FONT'` with none, from its ID (family × 128 + size, the name from `'FONT'` family × 128) [ClassicMac].
- **BDF**: one character per glyph at its Mac OS Roman code (the missing symbol at `ENCODING -1`), `DWIDTH` the
  advance, `BBX` the image's width and the font's height at x = kernMax + offset, y = −descent; a blank glyph `BBX 0
  0 0 0` with no rows; `SIZE` the point size (the font's height when unknown) at 72 dpi; properties `FONT_ASCENT`,
  `FONT_DESCENT`, `CHARSET_REGISTRY "Apple"`, `CHARSET_ENCODING "Roman"`, `FAMILY_NAME`. A colour font's non-zero
  pixels are ink. LF line ends.
- **JSON** for a strike: `family`, `size`, `style`, `fontType`, `depth`, `fixedWidth`, `firstChar`, `lastChar`,
  `ascent`, `descent`, `leading`, `maxWidth`, `maxKern`, `rectWidth`, `rectHeight`, `glyphs[]` (`character`, `text`,
  `advance`, `left` = kernMax + offset, `width`, `fractionalAdvance` with a width table, `top` and `rows` with a height
  table). For a family: `name`, `familyId`, `flags`, `version`, `firstChar`, `lastChar`, `ascent`, `descent`,
  `leading`, `maxWidth` (ems), `language` (version 4 and later), `styleExtras`, `fonts[]` (`size`, `style`, `face`,
  `depth`, `id`, `resource`: `NFNT`, `FONT`, `sfnt`, `afnt` or null when missing), `bounds[]` (`style`, `left`,
  `bottom`, `right`, `top`), `widthTables[]`, `kerningTables[]`, `styleMapping` (`fontClass`, `encodingOffset`,
  `indexes`, `names`, `glyphEncoding[]` of `code` and `name`; or null).

All decoders are version 1. The viewer shows a strike's glyph sheet.

---

## 5. Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `font.short` | Warning | A strike's or family's tables, or an `sfnt`'s directory, run past the data; read as far as they go |
| `font.location-table` | Info | A strike's location table is not right after its strike; it is read just before the offset/width table, as Mac OS 9 reads it |
| `font.undecodable` | Warning | A strike or family too short for its header, or a strike's character range impossible; exported raw |

---

## 6. Not covered yet

- Drawing text with a family (the Font Manager's choice of size and style, scaling, synthesized styles) in the font
  exports. The renderer (`ClassicMac.Graphics.QuickDraw`) draws a picture's text with the fonts read here, by the
  ROM's or Mac OS 9's rules ([quickdraw.md §7](../graphics/quickdraw.md#7-text)), but the font decoders write no specimen.
- Writing fonts (BDF or TrueType back into resources).
