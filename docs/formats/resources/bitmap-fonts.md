# Bitmap fonts (NFNT, FONT, fctb)

The Font Manager's bitmap font strikes, `'NFNT'` and `'FONT'`, and the font colour tables (`'fctb'`) that go with
colour strikes. A strike is one size and style of a font: a header, every glyph's image side by side in one bitmap,
and per-character tables. Strikes live in the System file, font suitcases and applications; a family record
([font-families.md](font-families.md)) ties them together. ClassicMac reads strikes and colour tables, draws text with
them ([quickdraw.md §2.22](../graphics/quickdraw.md#222-drawing-text)), and exports a strike as a glyph sheet, a BDF font and JSON.

| | |
| --- | --- |
| Identified by | Resource types `'NFNT'` and `'FONT'` (the same layout); `'fctb'` of the same ID as a deeper `'NFNT'` |
| ClassicMac | Reads; `ClassicMac.Graphics.Fonts.BitmapFont`, `FontColorTable`; exports through `ClassicMac.Resources.Decoders.Fonts` (`font.bitmap`, `font.colors`) |
| Verified against | Nothing yet |
| Sources | *Inside Macintosh: Text*, Font Manager; the Mac OS 9.0 Font Manager, FontObjects and QuickDraw text drawing, and the 68k ROM's Font Manager, traced in disassembly; Adobe's BDF 2.1 specification (the output) |

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

### 1.1 Strike header

`'NFNT'` and `'FONT'` have the same layout [Doc: *Text*, "The Bitmapped Font Resource"]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | fontType | `u16`, §1.3 |
| +$02 | 2 | firstChar | `i16`: the first character code |
| +$04 | 2 | lastChar | `i16`: the last character code |
| +$06 | 2 | widMax | `i16`: the widest advance |
| +$08 | 2 | kernMax | `i16`: the largest leftward kern, 0 or negative |
| +$0A | 2 | nDescent | `i16`: the negated descent; when positive, the high word of owTLoc [Code] |
| +$0C | 2 | fRectWidth | `i16`: the font rectangle's width |
| +$0E | 2 | fRectHeight | `i16`: its height, ascent + descent; the strike's height |
| +$10 | 2 | owTLoc | `u16`: the offset in words, counted from this field, to the offset/width table |
| +$12 | 2 | ascent | `i16` |
| +$14 | 2 | descent | `i16` |
| +$16 | 2 | leading | `i16` |
| +$18 | 2 | rowWords | `u16`: the strike's row length in words of 1-bit pixels; Mac OS 9 ignores the top bit [Code] |
| +$1A | n | strike | `rowWords × 2 × depth × fRectHeight` bytes, rows top down, pixels most significant bit first |

[Doc] except where tagged.

### 1.2 Tables

Each table has *n* = lastChar − firstChar + 3 entries: one per character, the missing symbol, and one more [Doc].

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| before the offset/width table | 2 × *n* | location table | `u16`: glyph *k*'s image is columns `loc[k]` to `loc[k+1]` of the strike, counted in pixels whatever the depth |
| +$10 + 2 × owTLoc | 2 × *n* | offset/width table | `i16`: per glyph `offset << 8 \| advance`, or −1 when the font lacks the character |
| follows | 2 × *n* | glyph-width table | fontType bit 1 only. `u16`: the advance in 8.8 fixed-point pixels |
| follows | 2 × *n* | image-height table | fontType bit 0 only. `u16`: `top << 8 \| rows`, the rows that hold ink |

[Doc]. In a well-formed font the location table follows the strike and ends where the offset/width table starts; which
of the two places a reader takes is §2 step 4.

### 1.3 fontType

| Bit | Meaning | Tested by [Code] |
| --- | --- | --- |
| 0 | The font has an image-height table | ROM |
| 1 | The font has a glyph-width table | ROM, Mac OS 9 |
| 2–4 | Depth code: 0 1-bit, 1 2-bit, 2 4-bit, 3 8-bit (4 and 5, 16 and 32 bits, only in synthetic strikes) | ROM bits 2–4, Mac OS 9 bits 2–3 |
| 7 | The font has a font colour table (`'fctb'`, §1.4) | ROM |
| 8 | A synthetic strike (set only in memory) | ROM |
| 9 | The font has colours other than black | ROM |
| 12, 15 | Reserved, set (`$9000` for a proportional font) | — |
| 13 | A fixed-width font (`$B000`) | nothing traced |
| 14 | Not to be expanded to the screen's depth (international) | ROM |

Meanings [Doc]; which code tests each bit [Code].

### 1.4 Font colour table (fctb)

A colour table with the layout of a `'clut'` ([palettes.md §2](palettes.md#2-colour-tables-clut)), of the same ID as
the deeper `'NFNT'` it colours, indexed by pixel value [Doc] [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | ctSeed | |
| +$04 | 2 | ctFlags | |
| +$06 | 2 | ctSize | `i16`: entries − 1 |
| +$08 | 8 × entries | entries | Each `i16` pixel value, then `u16` red, green, blue |

## 2. Reading

### 2.1 A strike

As Mac OS 9 reads it [Code], the layout from [Doc]:

1. Read the header (§1.1). Character *c* uses slot *c* − firstChar.
2. The depth is 2 to the power of the depth code, fontType bits 2–3. rowWords is the low 15 bits of +$18.
3. The strike is `rowWords × 2 × depth × fRectHeight` bytes from +$1A. A deeper strike's rows are depth × rowWords × 2
   bytes, but the location table still counts pixels.
4. owTLoc is the `u16` at +$10, with nDescent as its high word when nDescent is positive. The offset/width table is at
   +$10 + 2 × owTLoc; the location table is the 2*n* bytes just before it.
5. The glyph-width table, when fontType bit 1 is set, follows the offset/width table; the image-height table, when bit
   0 is set, follows the last of those two.
6. Slot lastChar − firstChar + 1 is the missing symbol, drawn for characters outside the range and for those whose
   offset/width entry is −1 [Doc].

### 2.2 Placing a glyph

The pen advances by the glyph's advance; the image's left edge is at pen + kernMax + offset; its top row is the font's
ascent above the baseline [Doc]. How QuickDraw draws the glyph is [quickdraw.md §2.22](../graphics/quickdraw.md#222-drawing-text).

### 2.3 A colour strike's colours

Mac OS 9 draws any strike deeper than 1 bit with the `'fctb'` of its ID, whatever fontType bits 7 and 9 say, or else
with the standard colour table of its depth (`'clut'` 64 + depth, the system's, with the highlight colour) [Code].

## 3. Writing

None.

## 4. Variants

- `'FONT'` is the older type; `'NFNT'` has the same layout and is found first ([font-families.md §2.2](font-families.md#22-finding-a-familys-strikes)) [Doc].
- **The 68k ROM** reads a strike by other rules [Code: 68k ROM]:
  - nDescent is the high word of owTLoc when 0 or more;
  - the depth code is fontType bits 2–4;
  - rowWords' top bit is not masked: a strike with it set is unusable;
  - the location table is right after the strike;
  - the `'fctb'` is used only when fontType bit 7 is set; otherwise the colours are a ramp from white (0) to black (the
    highest value).
- Depth codes 4 and 5 (16 and 32 bits) occur only in synthetic strikes the Font Manager builds in memory (bit 8) [Doc].

## 5. ClassicMac

- A strike under 26 bytes, or whose characters do not run within 0–255 in order, or whose strike would be over 1 GiB,
  is refused (`font.undecodable`). Tables that run past the data leave the glyphs there out or blank and are reported
  (`font.short`); the renderer reads such words as 0 (−1 for an offset/width entry). [ClassicMac]
- `BitmapFont.Read` reads as Mac OS 9 does (§2.1). The renderer, in `MacRom` mode, reads by the ROM's rules (§4) and
  treats a strike with rowWords' top bit set as missing. [ClassicMac]
- A location table not right after the strike is read just before the offset/width table, as Mac OS 9 reads it, and
  reported (`font.location-table`). [ClassicMac]
- A `'fctb'` is read sequentially; one under 8 bytes has no entries, and entries stop at the end of the data.
  [ClassicMac]
- The exports, all decoder version 1 [ClassicMac]:

  | Type | Decoder | Files |
  | --- | --- | --- |
  | `NFNT`, `FONT` | `font.bitmap` | `.png` the glyph sheet; `.bdf` a BDF 2.1 font; `.json` the metrics |
  | `NFNT`, `FONT` of no data | `font.bitmap` | `.json`: `familyName` (the resource name) and `family` (the ID ÷ 128) |
  | `fctb` | `font.colors` | `.json`: `entries[]` of `value`, `red`, `green`, `blue` (16-bit) and `hex` (`#rrggbb`, the high bytes) |

  The family exports are [font-families.md §5](font-families.md#5-classicmac), the outline font's
  [outline-fonts.md §5](outline-fonts.md#5-classicmac).
- **The family, size and style of a strike** come from the first `'FOND'` in the same fork whose association table
  lists the strike's ID with a size above 0; for a `'FONT'` with none, from its ID (family × 128 + size, style 0, the
  name from `'FONT'` family × 128). Otherwise the family is unknown and the size 0. [ClassicMac]
- **The glyph sheet**: every glyph in character order, then the missing symbol, 16 to a row, each at its pen position
  in a cell as wide as the widest glyph plus 2 pixels, black ink on white. A deeper strike's pixel values take its
  `'fctb'`'s colours (the high bytes), and values the table lacks the ROM's ramp: grey `255 − value × 255 / max`.
  Pixel value 0 is not drawn. The viewer shows the sheet. [ClassicMac]
- **BDF**: [ClassicMac]
  - `FONT -ClassicMac-<family>-Medium-R-Normal--<size>-<size × 10>-72-72-<M or P>-<widMax × 10>-Apple-Roman`, the
    family's spaces as `_`, or `NFNT<id>` when the family is unknown; `SIZE` the point size (the font's height when
    unknown) at 72 dpi; `FONTBOUNDINGBOX` fRectWidth (at least 1), fRectHeight, kernMax, −descent;
  - properties `FONT_ASCENT`, `FONT_DESCENT`, `CHARSET_REGISTRY "Apple"`, `CHARSET_ENCODING "Roman"`, `FAMILY_NAME`;
  - one character per glyph, `STARTCHAR c<hex code>` at its Mac OS Roman code, the missing symbol `STARTCHAR missing`
    at `ENCODING -1`; `SWIDTH` advance × 1000 / size, `DWIDTH` the advance; `BBX` the image's width and the font's
    height at x = kernMax + offset, y = −descent; a blank glyph `BBX 0 0 0 0` with no rows;
  - a colour font's non-zero pixels are ink; LF line ends.
- **JSON** for a strike: `family`, `size`, `style`, `fontType`, `depth`, `fixedWidth` (bit 13), `firstChar`,
  `lastChar`, `ascent`, `descent`, `leading`, `maxWidth`, `maxKern`, `rectWidth`, `rectHeight`, `glyphs[]`
  (`character`, −1 for the missing symbol; `text` for the others; `advance`; `left` = kernMax + offset; `width`;
  `fractionalAdvance` with a width table; `top` and `rows` with a height table). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `font.location-table` | Info | The location table is not right after the strike | Reads it just before the offset/width table | Mac OS 9 the same; the ROM reads it after the strike [Code] |
| `font.short` | Warning | A strike's tables run past the data | Leaves the glyphs there out or blank | Not traced |
| `font.undecodable` | Warning | A strike under 26 bytes, characters not within 0–255 in order, or a strike over 1 GiB | Exports the resource raw | Not traced |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/Fonts/FontTests.cs`, on strikes built by `FontBuilder`:
  - `Strikes_give_their_metrics_and_glyphs`: the header, glyph placement, the missing symbol for a −1 entry and for
    characters outside the range;
  - `Width_and_height_tables_are_read`: the 8.8 advances and `top << 8 | rows`;
  - `Short_strikes_are_reported_and_tiny_ones_refused`: `font.short`, and a 20-byte strike refused;
  - `The_ROM_reads_a_strike_by_its_own_rules`: a gap before the location table (`font.location-table`, Mac OS 9 and
    ROM places), fontType bit 4, rowWords' top bit;
  - `Font_color_table_reads_sequentially_with_the_big_endian_reader`: the `'fctb'` layout.
- Golden outputs in `tests/ClassicMac.Resources.Decoders.Tests/Golden` (`GoldenFixtures`, made in code): `FONT-1033`
  and `NFNT-1036` (`.json`, `.bdf`, and the sheet's hash; 1036 with width and height tables), `FONT-1024` (a family
  name only), `fctb-1036`.
- The renderer's use of strikes is verified as [quickdraw.md §2.22](../graphics/quickdraw.md#222-drawing-text) records.

## 8. Not covered

- Drawing a specimen of a family (the Font Manager's choice of size and style, scaling, synthesized styles) in the
  font exports. The renderer draws a picture's text with these fonts by the ROM's or Mac OS 9's rules, but the font
  decoders write no specimen.
- Writing fonts (BDF or TrueType back into resources).
- What fontType bit 13 changes: no code was found testing it.

## 9. References

1. Apple, *Inside Macintosh: Text* (1993), Font Manager: "The Bitmapped Font Resource", the font colour table.
2. Apple, *Inside Macintosh: Imaging With QuickDraw* (1994), the ColorTable record.
3. Adobe, *Glyph Bitmap Distribution Format (BDF) Specification*, version 2.1. The output format.
