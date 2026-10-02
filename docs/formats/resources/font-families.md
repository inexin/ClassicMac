# Font families (FOND)

Contents

1. [Font families (FOND)](#1-font-families-fond)
2. [Finding a family's strikes](#2-finding-a-familys-strikes)

---

## 1. Font families (FOND)

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
- **Kerning tables** (at ffKernOff): an `i16` count less one, then per table a `u16` style, an `i16` **number of
  pairs** (not a length), and 4-byte pairs: first character, second character, 4.12 kern [Doc] [Code]. A kern below
  −$2000 is sign and magnitude, −(*w* & $7FFF) [Code]. QuickDraw never kerns bitmap text with them; Mac OS 9 only turns
  them into a TrueType `kern` table for Type 1 fonts [Code].
- **Style-mapping table** (at ffStylOff): `i16` font class, `i32` offset of the glyph-encoding subtable **from the
  table's start** (often odd), a reserved `i32`, 48 index bytes (per style combination, a 1-based index into the
  strings), then an `i16` count and the strings (Pascal strings; the first is the base PostScript name, the others
  suffixes or lists of suffix indexes) [Doc] [Code]. The glyph-encoding subtable: an `i16` count, then per entry a
  character code byte and a Pascal glyph name. Only the Script Manager's justification reads the table, and only the
  font class's bit 10 ($0400, no extra space between characters) [Code].
- **Offset and bounding-box tables** (version 1 and later), right after the association table: an `i16` count less one
  and `i32` offsets from the offset table's start (in practice one, 6: the bounding-box table follows), then an `i16`
  count less one and 10-byte boxes: `u16` style, then left, bottom, right, top in 4.12 [Code]. Nothing in Mac OS 9 or
  the ROM reads them.
- **Associations**: size 0 is an `'sfnt'`, and in Mac OS 9 size −1 an `'afnt'` (a Type 1 font through ATM) [Code]. The
  style word's low byte is the QuickDraw style, bits 8–10 the strike's depth code [Code].
- **Language**: in a version 4 or later family, the word at +$2C (ffProperty[8]) is the family's language, −128 (none)
  when 0 or less [Code].

---

## 2. Finding a family's strikes

For a font association of size *s* > 0, the strike is the `'NFNT'` of its resource ID, else the `'FONT'` of that ID
[Doc] [Code]. The ROM treats a `'FONT'` under $24 bytes as missing; Mac OS 9 does not check [Code]. Old-style fonts
with no family record are `'FONT'` resources numbered family × 128 + size (the ROM only for families under $200);
`'FONT'` family × 128 + 0 is empty and carries the family's name as its resource name [Doc] [Code]. Mac OS 9 makes a
family record for them in memory [Code]. `ClassicMac.Graphics.Fonts` takes a lookup (type and ID to bytes) for this, so any
source of resources can supply the fonts [ClassicMac].

IDs are not range-checked. The Script Manager maps family IDs $4000–$BFFF to scripts, ((ID − $4000) >> 9) + 1, 512
IDs each [Code].

**The system and application fonts**: font number 0 is the family in `SysFontFam` (low memory $BA6), 1 the one in
`ApFontID` ($984) [Code]. Mac OS 9.0 sets them from `'itlb'` 0: Charcoal (family 2002, 12 point) and Geneva (3); the
ROM prefers Chicago (16383) [Code]. The Mac OS 9.0 System file holds the families Geneva (3), Monaco (4), .Keyboard
(98), .Last Resort (99) and Chicago (16383), and no family 0 [Code].
