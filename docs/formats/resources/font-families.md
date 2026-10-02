# Font families (FOND)

The Font Manager's font family record, `'FOND'`: a family's name (its resource name), its metrics per point, the
strikes and outline fonts that make it up, and optional width, kerning, style-mapping and bounding-box tables. Families
live in the System file, font suitcases and applications, beside the `'NFNT'`/`'FONT'` strikes
([bitmap-fonts.md](bitmap-fonts.md)) and `'sfnt'` outline fonts ([outline-fonts.md](outline-fonts.md)) they list.
ClassicMac reads families, finds their strikes for the renderer, and exports a family as JSON.

| | |
| --- | --- |
| Identified by | Resource type `'FOND'`; the resource ID is the family ID |
| ClassicMac | Reads; `ClassicMac.Graphics.Fonts.FontFamily`; exports through `ClassicMac.Resources.Decoders.Fonts` (`font.family`) |
| Verified against | Nothing yet |
| Sources | *Inside Macintosh: Text*, Font Manager; the Mac OS 9.0 Font Manager and ScriptUtils and the 68k ROM's Font Manager, traced in disassembly |

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

A **4.12** value is an `i16` fixed-point number with 12 fraction bits, in ems (per point of size). Offsets are from
the start of the resource unless a table says otherwise.

### 1.1 Family record

[Doc: *Text*, "The Font Family Resource"] except where tagged:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | ffFlags | `u16`. Bit 1: the family has width tables. Bit 12: use the style extra widths even without fractional widths; bit 13: never use them [Code: 68k ROM]. Bit 14: ignore the family width tables. Bit 15: a fixed-width family |
| +$02 | 2 | ffFamID | `u16`: the family ID (the `'FOND'` ID) |
| +$04 | 2 | ffFirstChar | `i16` |
| +$06 | 2 | ffLastChar | `i16` |
| +$08 | 2 | ffAscent | 4.12, for one point |
| +$0A | 2 | ffDescent | 4.12, negative |
| +$0C | 2 | ffLeading | 4.12 |
| +$0E | 2 | ffWidMax | 4.12 |
| +$10 | 4 | ffWTabOff | `i32`: offset of the width tables (§1.3), 0 for none |
| +$14 | 4 | ffKernOff | `i32`: offset of the kerning tables (§1.4) |
| +$18 | 4 | ffStylOff | `i32`: offset of the style-mapping table (§1.5) |
| +$1C | 18 | ffProperty | Nine 4.12 style extra widths: plain, bold, italic, underline, outline, shadow, condense, extend, unused. Words $8000–$8FFF are sign-magnitude negatives, −(*w* & $0FFF) [Code: 68k ROM]. In a version 4 or later family the last word (+$2C) is the language (§2.1) |
| +$2E | 4 | ffIntl | Two `u16`, reserved for international use |
| +$32 | 2 | ffVersion | `u16` |
| +$34 | 2 | association count | `i16`: the number of fonts less one |
| +$36 | 6 × *n* | association table | §1.2 |

### 1.2 Association table

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | size | `i16`: the point size; 0 an `'sfnt'`, and in Mac OS 9 −1 an `'afnt'` (a Type 1 font through ATM) [Code] |
| +$02 | 2 | style | `u16`: the QuickDraw style in the low byte; bits 8–10 the strike's depth code [Code] |
| +$04 | 2 | resource ID | `i16`: the `'NFNT'`/`'FONT'`, `'sfnt'` or `'afnt'` |

[Doc] except where tagged. Entries are stored by size, then style.

### 1.3 Width tables

At ffWTabOff [Doc] [Code: 68k ROM]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | count | `i16`: tables less one |
| +$02 | 2 + 2 × *n* | tables | Each a `u16` style, then a 4.12 width for each of *n* = ffLastChar − ffFirstChar + 3 characters (from ffFirstChar, the missing symbol and one more) |

The tables are stepped by the family's own range. A family whose ffLastChar is 0 has none [Code: 68k ROM].

### 1.4 Kerning tables

At ffKernOff [Doc] [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | count | `i16`: tables less one |
| +$02 | | tables | Each a `u16` style, an `i16` number of pairs (not a length), then 4-byte pairs: first character, second character, 4.12 kern |

A kern below −$2000 is sign and magnitude, −(*w* & $7FFF) [Code].

### 1.5 Style-mapping table

At ffStylOff [Doc] [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | fontClass | `i16` |
| +$02 | 4 | offset | `i32`: of the glyph-encoding subtable, from this table's start; often odd |
| +$06 | 4 | reserved | |
| +$0A | 48 | indexes | Per style combination, a 1-based index into the strings |
| +$3A | 2 | string count | `i16` |
| +$3C | | strings | Pascal strings: the first the base PostScript name, the others suffixes or lists of suffix indexes |

The glyph-encoding subtable: an `i16` count, then per entry a character code byte and a Pascal glyph name [Doc].

### 1.6 Offset and bounding-box tables

In a version 1 or later family, right after the association table [Code]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | offset count | `i16`: offsets less one |
| +$02 | 4 × *n* | offsets | `i32` from the offset table's start; in practice one, 6: the bounding-box table follows |
| +$06 | 2 | box count | `i16`: boxes less one |
| +$08 | 10 × *n* | boxes | Each a `u16` style, then left, bottom, right, top in 4.12 |

## 2. Reading

### 2.1 The family

1. The resource name is the family's name [Doc].
2. Read the record (§1.1) and the association table (§1.2).
3. Read the width, kerning and style-mapping tables at their offsets when those are not 0, and the bounding-box table
   in a version 1 or later family.
4. In a version 4 or later family, the word at +$2C (ffProperty[8]) is the family's language, −128 (none) when 0 or
   less [Code].

What Mac OS 9 and the ROM use [Code]:

- QuickDraw never kerns bitmap text with the kerning tables; Mac OS 9 only turns them into a TrueType `kern` table for
  Type 1 fonts.
- Only the Script Manager's justification reads the style-mapping table, and only the font class's bit 10 ($0400, no
  extra space between characters).
- Nothing in Mac OS 9 or the ROM reads the offset and bounding-box tables.
- The Font Manager's use of the width tables and style extras is [quickdraw.md §7](../graphics/quickdraw.md#7-text).

### 2.2 Finding a family's strikes

1. For an association of size *s* > 0, the strike is the `'NFNT'` of its resource ID, else the `'FONT'` of that ID
   [Doc] [Code].
2. Old-style fonts with no family record are `'FONT'` resources numbered family × 128 + size; `'FONT'` family × 128 + 0
   is empty and carries the family's name as its resource name [Doc] [Code]. Mac OS 9 makes a family record for them
   in memory [Code].
3. IDs are not range-checked. The Script Manager maps family IDs $4000–$BFFF to scripts, ((ID − $4000) >> 9) + 1, 512
   IDs each [Code].

### 2.3 The system and application fonts

Font number 0 is the family in `SysFontFam` (low memory $BA6), 1 the one in `ApFontID` ($984) [Code]. Mac OS 9.0 sets
them from `'itlb'` 0: Charcoal (family 2002, 12 point) and Geneva (3) [Code]. The Mac OS 9.0 System file holds the
families Geneva (3), Monaco (4), .Keyboard (98), .Last Resort (99) and Chicago (16383), and no family 0 [Code].

## 3. Writing

None.

## 4. Variants

- **Versions**: the offset and bounding-box tables (§1.6) are in version 1 and later; the language word (§2.1) in
  version 4 and later [Code].
- **The 68k ROM** [Code: 68k ROM]:
  - treats a `'FONT'` under $24 bytes as missing (Mac OS 9 does not check);
  - finds old-style `'FONT'` resources only for families under $200;
  - prefers Chicago (16383) as the system font;
  - has no `'afnt'` associations.
- **Mac OS 9.2.2** [Code: 9.2.2]: the language word is kept only when ffVersion ≥ 4
  ([README.md](../README.md#reference-builds)); the System's bitmap fonts were renumbered (Geneva 9 and 12 are `NFNT`
  1025 and 1026, Monaco 9 is 1027, the bitmaps unchanged).

## 5. ClassicMac

- A family under 54 bytes is refused (`font.undecodable`). Tables that run past the data are read as far as they go
  and reported (`font.short`). [ClassicMac]
- The style extra widths are the first eight ffProperty words; the ninth is read only as the language. [ClassicMac]
- The sign-and-magnitude rule for kerns (§1.4) is applied to the bounding boxes too. [ClassicMac]
- The renderer reads the width tables as the Font Manager steps them, words past the resource as 0; the exported
  tables stop at the last whole table. [ClassicMac]
- `FontFamily.Strike` finds a strike through a lookup (type and ID to bytes), so any source of resources can supply
  the fonts: the `'NFNT'`, else the `'FONT'`, an empty one counting as none; no size check; null for an outline or
  Type 1 font. ClassicMac makes no family record for old-style fonts; the exports name their strikes by ID
  ([bitmap-fonts.md §5](bitmap-fonts.md#5-classicmac)). [ClassicMac]
- **JSON** (`font.family`, decoder version 1; strings in the export's text encoding): `name`, `familyId`, `flags`,
  `version`, `language` (version 4 and later), `firstChar`, `lastChar`, `ascent`, `descent`, `leading`, `maxWidth`
  (ems), `styleExtras` (eight), `fonts[]` (`size`, `style`, `face`, `depth`, `id`, `resource`: `NFNT`, `FONT`, `sfnt`,
  `afnt`, or null when missing from the fork), `bounds[]` (`style`, `left`, `bottom`, `right`, `top`),
  `widthTables[]` (`style`, `widths`), `kerningTables[]` (`style`, `pairs[]` of `first`, `second`, `kern`),
  `styleMapping` (`fontClass`, `encodingOffset`, `indexes`, `names`, `glyphEncoding[]` of `code` and `name`; or null).
  [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `font.short` | Warning | The association, width, kerning, style-mapping or bounding-box tables run past the data | Reads them as far as they go | Not traced |
| `font.undecodable` | Warning | The family is under 54 bytes | Exports the resource raw | Not traced |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/Fonts/FontTests.cs`, on a family built by `FontBuilder`:
  - `Families_give_their_fonts_and_tables`: the metrics, a sign-magnitude style extra ($8100), the language of a
    version 4 family, the associations, a width table, a sign-magnitude kern ($812F), the style-mapping strings and an
    odd glyph-encoding offset, a bounding box;
  - `A_familys_strikes_are_found_as_NFNT_then_FONT`: an outline association, a strike from `'FONT'`, one from
    `'NFNT'`, a missing one.
- Golden output `FOND-1024.json` in `tests/ClassicMac.Resources.Decoders.Tests/Golden` (`GoldenFixtures`).

## 8. Not covered

- Writing families.
- The README's Mac OS 9.2.2 note (§4) implies Mac OS 9.0 keeps the language word in families before version 4, while
  §2.1 gives version 4 and later for 9.0; which version test 9.0 makes is not settled.

## 9. References

1. Apple, *Inside Macintosh: Text* (1993), Font Manager: "The Font Family Resource" and its tables.
