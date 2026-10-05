# Outline fonts (sfnt)

An `'sfnt'` resource is a TrueType font file's bytes: an offset table, a table directory, then the tables. Mac OS 7
and later keep TrueType fonts this way in the System file and font suitcases; a family record
([font-families.md](font-families.md)) refers to one with an association of size 0. ClassicMac reads the directory and
the names, and exports the data unchanged as a `.ttf` file with a JSON summary.

| | |
| --- | --- |
| Identified by | Resource type `'sfnt'`; the first four bytes $00010000 or `'true'` (TrueType), `'typ1'` (PostScript) |
| ClassicMac | Reads; `ClassicMac.Graphics.Fonts.OutlineFont`; exports through `ClassicMac.Resources.Decoders.Fonts` (`font.outline`) |
| Verified against | Nothing yet |
| Sources | Apple's *TrueType Reference Manual*; the Mac OS 9.0 Font Manager, traced in disassembly |

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

### 1.1 Offset table

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | version | $00010000 or `'true'` for TrueType outlines; `'typ1'` for PostScript Type 1 |
| +$04 | 2 | numTables | `u16` |
| +$06 | 2 | searchRange | `u16` |
| +$08 | 2 | entrySelector | `u16` |
| +$0A | 2 | rangeShift | `u16` |
| +$0C | 16 × numTables | table directory | §1.2 |

[Doc: *TrueType Reference Manual*]

### 1.2 Table directory entry

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | tag | |
| +$04 | 4 | checksum | `u32` |
| +$08 | 4 | offset | `u32`, from the start of the font |
| +$0C | 4 | length | `u32` |

[Doc: *TrueType Reference Manual*]

### 1.3 The name table

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | format | `u16` |
| +$02 | 2 | count | `u16`: name records |
| +$04 | 2 | stringOffset | `u16`: of the strings, from the table's start |
| +$06 | 12 × count | name records | Each `u16` platform, encoding, language, name ID, length, offset (from stringOffset) |

Name IDs 1, 2 and 4 are the family, subfamily and full names [Doc: *TrueType Reference Manual*].

## 2. Reading

1. Mac OS 9 picks the scaler by the first four bytes, $00010000 counting as `'true'` [Code].
2. Its only scaler takes `'true'` fonts and Apple's own `'mor0'`, `$A5kbd` and `$A5lst` fonts (the System file's
   .Keyboard and .Last Resort), a `bhed` table in place of `head`, and bitmap-only fonts [Code].
3. PostScript `'typ1'` fonts need ATM [Code].

## 3. Writing

### 3.1 Making a font loadable

Windows' and other modern loaders require what Mac TrueType fonts often lack. Made loadable, a TrueType font gets it,
from what it has, as the OpenType specification describes each table [Doc: OpenType], every other table kept:

1. **Version**: the sfnt version $00010000 (the Mac's `'true'` is not taken).
2. **`cmap`**: with no Windows Unicode subtable (3, 1 or 3, 10), a (3, 1) record is added: on the font's Unicode
   subtable (platform 0) when it has one, else on a new format 4 subtable made from the Macintosh one (platform 1,
   formats 0, 6 or 4; its encoding ID the Mac script), each code read as its character in that script's encoding (a code
   that is no character, or a control character, left out). The format 4 subtable has a segment for each run of
   consecutive characters with consecutive glyphs (idDelta only), then $FFFF.
3. **`name`**: Windows records (the encoding and language of the Windows ones the font has; else Unicode, US English,
   3, 1, $0409) for each name the font has that they lack, as UTF-16. Missing names are made: style (2) "Regular"; full
   name (4) the family and style ("Family Style", the family alone for Regular); version (5) "Version *x.yyy*" from
   `head`'s fontRevision; unique name (3) "*full name*: *version*"; PostScript name (6) the family and style joined by a
   hyphen, printable ASCII without spaces or `[](){}<>/%`, at most 63 characters.
4. **`OS/2`** (version 1), when missing: xAvgCharWidth the average of the glyphs' non-zero advances (`hmtx`); weight
   700 for a bold `macStyle`, else 400; width 5; fsType 0 (installable); sub- and superscript sizes 0.65 × 0.6 em,
   offsets 0.075 and 0.35 em; strikeout 0.05 em at 0.26 em; panose 0; the Unicode ranges of the characters mapped;
   vendor four spaces; fsSelection italic, bold, or regular from `macStyle`; the first and last characters mapped; typo
   ascender, descender and line gap from `hhea`; Windows ascent and descent covering `head`'s bounds and `hhea`'s; code
   pages Latin 1 (with Basic Latin) and Macintosh.
5. **`post`** (version 3, no glyph names), when missing: upright, the underline at −0.1 em, 0.05 em thick, fixed pitch
   when every advance is the same.
6. The file again: the tables in tag order, 4-byte aligned, each with its checksum, the offset table's searchRange,
   entrySelector and rangeShift, and `head`'s checkSumAdjustment so the file sums to $B1B0AFBA.

[Verified: Windows' GDI (`AddFontMemResourceEx`) refuses 30 of the 60 TrueType fonts of Mac OS 9.0's Fonts folder as
they are, and loads all 60 made loadable. Its strictest needs are the unique name (3) and the version]. A font that
lacks none of these, or is not TrueType, is left as it is.

## 4. Variants

- TrueType (`'true'`, $00010000) and PostScript Type 1 (`'typ1'`) data share the container (§1) [Doc].
- Apple's own version tags and the `bhed` table, which Mac OS 9's scaler accepts: §2 step 2.

## 5. ClassicMac

- An `'sfnt'` under 12 bytes is refused (`font.undecodable`). Directory entries past the data, and tables that run
  past it, are reported (`font.short`). [ClassicMac]
- It is TrueType when the version is $00010000 or `'true'` and it has a `glyf` table. [ClassicMac]
- The names are read from the `name` table: the Macintosh platform's Roman record (platform 1, encoding 0) first,
  else a Unicode one (platform 0, or 3 with encoding 1, UTF-16 big-endian). [ClassicMac]
- The export (`font.outline`, decoder version 1): `.ttf`, the data unchanged (`.sfnt` when it is not TrueType); `.json`
  with `version`, `trueType`, `familyName`, `subfamilyName`, `fullName`, `tables[]` (`tag`, `offset`, `length`).
  [ClassicMac]
- With `DecodeOptions.LoadableFonts` (off by default; the CLI's `extract --loadable-fonts`, the app's Export ▸ Loadable
  Fonts) a TrueType font's `.ttf` is
  made loadable (§3.1, `LoadableFont.Make`), and the JSON gains `loadable[]`, what was added (`version`,
  `cmap (3,1)`, `name (Windows)`, `OS/2`, `post`). The JSON's tables are the font's own. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `font.short` | Warning | The table directory runs past the data, or a table does | Reads the entries that fit; the names only from a whole `name` table | Not traced |
| `font.undecodable` | Warning | The data is under 12 bytes | Exports the resource raw | Not traced |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/Fonts/FontTests.cs`, `Outline_fonts_give_their_tables_and_names`: a font built by
  `FontBuilder` gives its tables and names, read alone or from within a longer buffer.
- Golden output `sfnt-1024.json` in `tests/ClassicMac.Resources.Decoders.Tests/Golden` (`GoldenFixtures`).
- `tests/ClassicMac.Graphics.Tests/Fonts/LoadableFontTests.cs` (§3.1), on fonts `TrueTypeBuilder` makes: what is
  added and its content, the checksums, a font that lacks nothing kept, names made, Apple's partial Windows names and
  `'true'` version, and GDI refusing the Mac font and loading it made loadable (on Windows).
- `tests/ClassicMac.Resources.Decoders.Tests/LoadableFontDecoderTests.cs`: the option on and off; and, with
  `CLASSICMAC_MAC_FONTS` naming a folder of font suitcases (raw resource forks, never committed), every TrueType font
  loading in GDI made loadable. `tests/ClassicMac.Cli.Tests/ExtractTests.cs`, `Loadable_fonts_adds_what_modern_systems_need`.

## 8. Not covered

- Drawing with outline fonts: the renderer approximates text without a bitmap strike
  ([quickdraw.md §2.22](../graphics/quickdraw.md#222-drawing-text)).
- Checking table checksums when reading.
- Making PostScript Type 1 (`'typ1'`) or bitmap-only (`bhed`) sfnts loadable; double-byte Mac `cmap` subtables (format
  2) as the source of a Windows one.

## 9. References

1. Apple, *TrueType Reference Manual*: the offset table, the table directory and the `name` table.
2. Microsoft, *OpenType specification*: `cmap` format 4, `name` IDs, `OS/2`, `post` and the checksums, as modern loaders
   read them (§3.1).
