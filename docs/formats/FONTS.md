# Fonts — an implementer's specification

This document describes the classic Mac OS Font Manager's resources: bitmap font strikes (`'NFNT'`, `'FONT'`), font
families (`'FOND'`), font colour tables (`'fctb'`) and outline fonts (`'sfnt'`), completely enough to write a reader
without reading ClassicMac's code. It also specifies what ClassicMac writes for them: a glyph sheet image, a BDF font
and JSON for strikes, JSON for families and colours, and the TrueType file for outline fonts.

The reader is the `ClassicMac.Fonts` package (on `ClassicMac.Core` only); the decoders are in
`ClassicMac.Resources.Decoders.Fonts`.

References:

- *Inside Macintosh: Text* (1993), Font Manager: the bitmapped font resource, the font family resource and its tables.
- The 68k ROM's Font Manager, disassembly (as traced for QuickDraw.Pict's `PICT-FORMAT.md` §12): the offset of the
  offset/width table, the style extra widths, the width tables' stepping.
- Apple's *TrueType Reference Manual*: the `sfnt` offset table, table directory and `name` table.
- Adobe's *Glyph Bitmap Distribution Format (BDF) Specification*, version 2.1: the BDF output.

Contents

1. [Conventions](#1-conventions)
2. [Bitmap strikes (NFNT, FONT)](#2-bitmap-strikes-nfnt-font)
3. [Font families (FOND)](#3-font-families-fond)
4. [Finding a family's strikes](#4-finding-a-familys-strikes)
5. [Font colour tables (fctb)](#5-font-colour-tables-fctb)
6. [Outline fonts (sfnt)](#6-outline-fonts-sfnt)
7. [Output](#7-output)
8. [Diagnostics](#8-diagnostics)
9. [Not covered yet](#9-not-covered-yet)

---

## 1. Conventions

The shared conventions of [README.md](README.md) hold. A **4.12** value is an `i16` fixed-point number with 12
fraction bits, in ems (per point of size). Tags are those of [README.md](README.md); **[ClassicMac]** marks
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
| +$0A | 2 | `i16` | nDescent: the negated descent; **when 0 or more, the high word of owTLoc** [Code: 68k ROM] |
| +$0C | 2 | `i16` | fRectWidth: the font rectangle's width |
| +$0E | 2 | `i16` | fRectHeight: its height, ascent + descent: the strike's height |
| +$10 | 2 | `u16` | owTLoc: the offset **in words, from this field**, to the offset/width table |
| +$12 | 2 | `i16` | ascent |
| +$14 | 2 | `i16` | descent |
| +$16 | 2 | `i16` | leading |
| +$18 | 2 | `i16` | rowWords: the strike's row length in words (of 1-bit pixels) |
| +$1A | | | strike: `rowWords × 2 × depth × fRectHeight` bytes, rows top down, pixels most significant bit first |
| | 2 × *n* | `u16`[] | location table: glyph *k*'s image is columns `loc[k]` to `loc[k+1]` of the strike |
| +$10 + 2·owTLoc | 2 × *n* | `i16`[] | offset/width table: per glyph `offset << 8 | advance`, or −1 when the font lacks the character |
| | 2 × *n* | `u16`[] | glyph-width table (fontType bit 1): the advance in 8.8 fixed-point pixels |
| | 2 × *n* | `u16`[] | image-height table (fontType bit 0): `top << 8 | rows`, the rows that hold ink |

*n* = lastChar − firstChar + 3. Character *c* uses slot *c* − firstChar; slot lastChar − firstChar + 1 is the
**missing symbol**, drawn for characters outside the range or marked −1 [Doc].

**Placing a glyph** [Doc]: the pen advances by the glyph's advance; the image's left edge is at pen + kernMax + offset;
its top row is the font's ascent above the baseline.

fontType bits [Doc]:

| Bit | Meaning |
| --- | --- |
| 0 | The font has an image-height table |
| 1 | The font has a glyph-width table |
| 2–3 | Depth: 0 1-bit, 1 2-bit, 2 4-bit, 3 8-bit (a colour font) |
| 7 | The font has a font colour table (`'fctb'`, §5) |
| 8 | A synthetic font |
| 9 | The font has colours other than black |
| 12, 15 | Reserved, set (`$9000` for a proportional font) |
| 13 | A fixed-width font (`$B000`) |
| 14 | Not to be expanded to the screen's depth |

ClassicMac refuses a strike under 26 bytes, or whose characters are not within 0–255 in order; tables that run past the
data leave the glyphs there out and are reported (§8) [ClassicMac].

---

## 3. Font families (FOND)

A family record [Doc] (*Text*, "The Font Family Resource"). Its resource name is the family's name.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `u16` | ffFlags: bit 1 the family has width tables; bit 12 use the style extra widths even without fractional widths, bit 13 never use them [Code: 68k ROM]; bit 14 ignore the family width tables; bit 15 a fixed-width family |
| +$02 | 2 | `u16` | ffFamID: the family ID (the `'FOND'` ID) |
| +$04 | 2 | `i16` | ffFirstChar |
| +$06 | 2 | `i16` | ffLastChar |
| +$08 | 2 | 4.12 | ffAscent, for one point |
| +$0A | 2 | 4.12 | ffDescent (negative) |
| +$0C | 2 | 4.12 | ffLeading |
| +$0E | 2 | 4.12 | ffWidMax |
| +$10 | 4 | `i32` | ffWTabOff: offset of the width tables (0 none) |
| +$14 | 4 | `i32` | ffKernOff: offset of the kerning tables |
| +$18 | 4 | `i32` | ffStylOff: offset of the style-mapping table |
| +$1C | 18 | 4.12 × 9 | ffProperty: the style extra widths: plain, bold, italic, underline, outline, shadow, condense, extend, unused. Words $8000–$8FFF are sign-magnitude negatives, −(*w* & $0FFF) [Code: 68k ROM] |
| +$2E | 4 | `u16` × 2 | ffIntl |
| +$32 | 2 | `u16` | ffVersion |
| +$34 | 2 | `i16` | number of fonts less one |
| +$36 | 6 × *n* | | the font association table: `i16` size (0 an outline font), `u16` style, `i16` resource ID |

Offsets are from the start of the resource.

- **Width tables** (at ffWTabOff): an `i16` count less one, then per table a `u16` style and a 4.12 width per
  character from ffFirstChar, the missing symbol and one more (ffLastChar − ffFirstChar + 3 entries). A family whose
  ffLastChar is 0 has none [Code: 68k ROM].
- **Kerning tables** (at ffKernOff): an `i16` count less one, then per table a `u16` style, an `i16` number of pairs,
  and 4-byte pairs: first character, second character, 4.12 kern [Doc].
- **Style-mapping table** (at ffStylOff): `i16` font class, `i32` offset of the glyph-encoding subtable, a reserved
  `i32`, 48 index bytes (per style combination, an index into the name list), then an `i16` count and the style-name
  strings (Pascal strings; the first is the family's base name, the others suffixes or suffix-index lists) [Doc].

---

## 4. Finding a family's strikes

For a font association of size *s* > 0, the strike is the `'NFNT'` of its resource ID, else the `'FONT'` of that ID
[Doc]. Old-style fonts with no family record are `'FONT'` resources numbered family × 128 + size; `'FONT'` family ×
128 + 0 is empty and carries the family's name as its resource name [Doc]. `ClassicMac.Fonts` takes a lookup (type and
ID to bytes) for this, so any source of resources can supply the fonts [ClassicMac].

---

## 5. Font colour tables (fctb)

A colour table (as `'clut'`, [PALETTES.md](PALETTES.md) §2) of the same ID as a colour `'NFNT'`: the colours of its
pixel values [Doc].

---

## 6. Outline fonts (sfnt)

An `'sfnt'` resource is a TrueType font file's bytes [Doc]: an offset table (`u32` version, `u16` number of tables,
search fields), a table directory of 16-byte entries (tag, checksum, offset, length), then the tables. A family refers
to it with an association of size 0. ClassicMac reads the directory and the `name` table's family (ID 1), subfamily
(ID 2) and full (ID 4) names, from the Macintosh Roman records first, else Unicode [ClassicMac].

---

## 7. Output

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
  `leading`, `maxWidth` (ems), `styleExtras`, `fonts[]` (`size`, `style`, `id`, `resource`: `NFNT`, `FONT`, `sfnt` or
  null when missing), `widthTables[]`, `kerningTables[]`, `styleMapping` (or null).

All decoders are version 1. The viewer shows a strike's glyph sheet.

---

## 8. Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `font.short` | Warning | A strike's or family's tables, or an `sfnt`'s directory, run past the data; read as far as they go |
| `font.undecodable` | Warning | A strike or family too short for its header, or a strike's character range impossible; exported raw |

---

## 9. Not covered yet

- The Mac OS 9 Font Manager's own reading of the tables past the header (asked of the disassembly).
- The glyph-encoding subtable of the style-mapping table, and the family's bounding-box table.
- Drawing text with a family (the Font Manager's choice of size and style, scaling, synthesized styles): with the
  renderer, after the QuickDraw.Pict merge.
- Writing fonts (BDF or TrueType back into resources).
