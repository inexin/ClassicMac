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

None.

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

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `font.short` | Warning | The table directory runs past the data, or a table does | Reads the entries that fit; the names only from a whole `name` table | Not traced |
| `font.undecodable` | Warning | The data is under 12 bytes | Exports the resource raw | Not traced |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/Fonts/FontTests.cs`, `Outline_fonts_give_their_tables_and_names`: a font built by
  `FontBuilder` gives its tables and names, read alone or from within a longer buffer.
- Golden output `sfnt-1024.json` in `tests/ClassicMac.Resources.Decoders.Tests/Golden` (`GoldenFixtures`).

## 8. Not covered

- Drawing with outline fonts: the renderer approximates text without a bitmap strike
  ([quickdraw.md §2.22](../graphics/quickdraw.md#222-drawing-text)).
- Checking table checksums.

## 9. References

1. Apple, *TrueType Reference Manual*: the offset table, the table directory and the `name` table.
