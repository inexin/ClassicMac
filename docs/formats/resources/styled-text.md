# Styled text (TEXT, styl)

Classic Mac OS keeps text as bytes in Mac OS Roman, a single-byte encoding with CR line breaks. A `'TEXT'` resource is
such text with no header; a `'styl'` resource of the same ID holds its style runs (font, size, face and colour), the
style scrap TextEdit applies with `TEUseStyleScrap`. SimpleText keeps a document's text in its data fork and its styles
in `'styl'` 128. The Help Manager, SimpleText and DOCMaker show styled text this way. This document also holds Mac OS
Roman for the other text resources ([strings.md](strings.md), [version.md](version.md)). ClassicMac decodes `'TEXT'`
to text and, with its `'styl'`, to RTF; a `'styl'` alone to JSON; it edits and writes both back. The outputs are
specified in [text-output.md](../output/text-output.md); documents with pictures in [documents.md](documents.md).

| | |
| --- | --- |
| Identified by | Resource types `'TEXT'` and `'styl'`, paired by ID. SimpleText documents: files of type `'TEXT'` (creator `'ttxt'`) or `'ttro'` (read-only) |
| ClassicMac | Reads and writes: `ClassicMac.Resources.Decoders.Text` (`TextDecoder`, `StyleDecoder`, `StyledText`, `TextResources`); `ClassicMac.Core.MacRoman` |
| Verified against | DOCMaker 4.8.4's stand-alone reader in SheepShaver, Mac OS 9.0 (its `'styl'` runs shown as stored) |
| Sources | *Inside Macintosh: Text*, *More Macintosh Toolbox*, *Imaging With QuickDraw*; Unicode's `ROMAN.TXT`; Mac OS 9.0's `TEUseStyleScrap` (ROM and System), Help Manager (`'PACK'` 14), SimpleText 1.4, DOCMaker 4.8.4, the System 7.1-era Font Manager and the 68k ROM (disassembly) |

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

The shared conventions of [README.md](../README.md#conventions) hold. Every text here is a byte string in a
single-byte Mac encoding, Mac OS Roman unless noted, so one byte is one character and a character offset (a style
run's start) is a byte offset. An `RGBColor` is three `u16`: red, green, blue, each 0–65535. Characters are named by
Unicode code point (`U+20AC`) or by byte (`$DB`).

### 1.1 Mac OS Roman

Mac OS Roman maps every byte to exactly one Unicode code point, so decoding never fails and decoding then encoding
gives back the same bytes [Doc: ROMAN.TXT].

- $00–$7F are ASCII, control characters included [Doc: ROMAN.TXT].
- $80–$FF map as in the table below, row the high nibble, column the low nibble [Doc: ROMAN.TXT, version c02].

|    | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | A | B | C | D | E | F |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 8 | 00C4 | 00C5 | 00C7 | 00C9 | 00D1 | 00D6 | 00DC | 00E1 | 00E0 | 00E2 | 00E4 | 00E3 | 00E5 | 00E7 | 00E9 | 00E8 |
| 9 | 00EA | 00EB | 00ED | 00EC | 00EE | 00EF | 00F1 | 00F3 | 00F2 | 00F4 | 00F6 | 00F5 | 00FA | 00F9 | 00FB | 00FC |
| A | 2020 | 00B0 | 00A2 | 00A3 | 00A7 | 2022 | 00B6 | 00DF | 00AE | 00A9 | 2122 | 00B4 | 00A8 | 2260 | 00C6 | 00D8 |
| B | 221E | 00B1 | 2264 | 2265 | 00A5 | 00B5 | 2202 | 2211 | 220F | 03C0 | 222B | 00AA | 00BA | 03A9 | 00E6 | 00F8 |
| C | 00BF | 00A1 | 00AC | 221A | 0192 | 2248 | 2206 | 00AB | 00BB | 2026 | 00A0 | 00C0 | 00C3 | 00D5 | 0152 | 0153 |
| D | 2013 | 2014 | 201C | 201D | 2018 | 2019 | 00F7 | 25CA | 00FF | 0178 | 2044 | 20AC | 2039 | 203A | FB01 | FB02 |
| E | 2021 | 00B7 | 201A | 201E | 2030 | 00C2 | 00CA | 00C1 | 00CB | 00C8 | 00CD | 00CE | 00CF | 00CC | 00D3 | 00D4 |
| F | F8FF | 00D2 | 00DA | 00DB | 00D9 | 0131 | 02C6 | 02DC | 00AF | 02D8 | 02D9 | 02DA | 00B8 | 02DD | 02DB | 02C7 |

Entries worth knowing [Doc: ROMAN.TXT notes]:

| Byte | Code point | Note |
| --- | --- | --- |
| $CA | U+00A0 | No-break space ("option-space"); SimpleText and DOCMaker use it as a picture anchor (§2.4, [documents.md](documents.md#1-layout)) |
| $DB | U+20AC | Euro sign (§4) |
| $BD | U+03A9 | Greek capital omega; older tables gave the ohm sign U+2126 |
| $C6 | U+2206 | Increment (∆), not the Greek capital delta |
| $F0 | U+F8FF | The Apple logo, a private-use code point |

- A line break is CR ($0D) [Doc: Inside Macintosh: Text]. LF ($0A) has no meaning in Mac text.
- The Chicago font draws glyphs for some control bytes: $11 command key, $12 check mark, $13 diamond, $14 Apple logo
  [Doc: ROMAN.TXT notes]. They are still control characters in Mac OS Roman.

### 1.2 Plain text (TEXT)

A `'TEXT'` resource is text with no header and no length field: its length is the resource's size [Doc: Inside
Macintosh: Text]. TextEdit keeps at most 32,767 characters in one record [Doc: Inside Macintosh: Text]; the resource
itself has no limit.

A `'TEXT'` is styled by the `'styl'` resource of the same ID: the Help Manager's styled-text items name one ID for
both resources [Doc: More Macintosh Toolbox, Help Manager], and DOCMaker's reader loads chapter *k*'s `'TEXT'` and
`'styl'` from the same ID and passes both to TextEdit [Code: DOCMaker 4.8.4 stand-alone reader].

### 1.3 Style runs (styl)

A `'styl'` resource is TextEdit's style scrap record, `StScrpRec`: a count, then that many 20-byte style elements,
`ScrpSTElement` [Doc: Inside Macintosh: Text, TextEdit].

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `scrpNStyles` | `i16`: the number of elements |
| +$02 | 20 × *n* | Elements | Below |

Each element:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `scrpStartChar` | `i32`: offset of the run's first character in the text |
| +$04 | 2 | `scrpHeight` | `i16`: line height, in pixels |
| +$06 | 2 | `scrpAscent` | `i16`: font ascent, in pixels |
| +$08 | 2 | `scrpFont` | `i16`: font family ID (§1.5) |
| +$0A | 1 | `scrpFace` | Style bits (§1.4) |
| +$0B | 1 | Filler | The Pascal `Style` set is one byte, padded to a word |
| +$0C | 2 | `scrpSize` | `i16`: point size; 0 is the default size (§2.3) |
| +$0E | 6 | `scrpColor` | `RGBColor`: the text colour |

- Runs are stored in order of start, and the first starts at 0. Each run extends to the next run's start, the last to
  the end of the text [Doc: Inside Macintosh: Text].
- Height and ascent are TextEdit's line metrics for the run; `TEUseStyleScrap` ignores them and TextEdit computes its
  own [Code: Mac OS 9.0 ROM].

### 1.4 Face bits

`scrpFace` is QuickDraw's `Style` [Doc: Inside Macintosh: Text; Imaging With QuickDraw]:

| Bit | Value | Style |
| --- | --- | --- |
| 0 | $01 | Bold |
| 1 | $02 | Italic |
| 2 | $04 | Underline |
| 3 | $08 | Outline |
| 4 | $10 | Shadow |
| 5 | $20 | Condense |
| 6 | $40 | Extend |
| 7 | $80 | Unused |

QuickDraw's default style table narrows each condensed character by one pixel and widens each extended one by one
pixel [Code: 68k ROM, Mac OS ROM 1.6; the same table in Mac OS 9's native Font Manager].

### 1.5 Fonts by ID

A style names its font by family ID. The standard IDs [Doc: Inside Macintosh: Text, font family numbers]:

| ID | Font | ID | Font | ID | Font |
| --- | --- | --- | --- | --- | --- |
| 0 | The system font (Chicago) | 6 | London | 20 | Times |
| 1 | The application font | 7 | Athens | 21 | Helvetica |
| 2 | New York | 8 | San Francisco | 22 | Courier |
| 3 | Geneva | 9 | Toronto | 23 | Symbol |
| 4 | Monaco | 11 | Cairo | 24 | Mobile |
| 5 | Venice | 12 | Los Angeles | | |

- IDs 0 and 1 are aliases that the running system resolves: the system font and the application font (Geneva on US
  systems) [Doc: Inside Macintosh: Text].
- Any other ID is assigned by the system that installed the font (its `'FOND'` resource) and has no fixed name
  [Doc: Inside Macintosh: Text].

### 1.6 SimpleText documents

SimpleText (and TeachText) keeps a document as a file of type `'TEXT'` (creator `'ttxt'`) or `'ttro'` (read-only,
read by the same code) [Code: SimpleText 1.4]:

| Fork | Resource | Contents |
| --- | --- | --- |
| Data | | The text (§1.1), no header |
| Resource | `'styl'` 128 | The styles (§1.3) |
| Resource | `'PICT'` 1000 + *k* | The picture shown at the *k*-th $CA, from 0 (§2.4) |
| Resource | `'form'` $726D + *k* | Printing: bit 0 of its first long set ends the page after the *k*-th $CA's line |
| Resource | `'snd '` 10000 | The document's voice annotation |

## 2. Reading

### 2.1 Decoding Mac OS Roman

1. Map each byte by §1.1: $00–$7F to the same code point, $80–$FF by the table.
2. Leave CR as the line break; nothing else marks a line.

### 2.2 Pairing TEXT and styl

The Help Manager's styled items [Code: Mac OS 9.0 `'PACK'` 14]:

1. Load the `'TEXT'` and then the `'styl'` of the same ID, with `GetResource` (any open file). No `'TEXT'`: no
   balloon.
2. Set the whole text, then apply the `'styl'` with `TEUseStyleScrap` (§2.3) over characters 0 to $7FF only; the text
   after that is not restyled.
3. With no `'styl'` on a system whose script is not Roman, set the script's system font and size (§4).

A string item (`'STR#'`) that `GetIndString` returns empty is an error to the Help Manager: no balloon
([strings.md §2.2](strings.md#22-string-lists-str)).

DOCMaker's reader hands the `'styl'` to `TEUseStyleScrap` unchanged, and the runs' fonts, sizes, faces and colours
show as stored [Code: DOCMaker 4.8.4] [Verified: its reader in SheepShaver].

### 2.3 How runs map onto the text

A `'styl'` reaches TextEdit through `TEUseStyleScrap` (`SetStylScrap`), which applies it over a range of the text;
SimpleText (§2.4), the Help Manager (§2.2) and DOCMaker all use it. It checks nothing and returns no error
[Code: Mac OS 9.0 ROM; the System file's copy is byte-identical]:

1. Sort the range's two ends and pin each to the text length *L*: `rs`, `re`. Set `pos = rs`.
2. For each element *i* from 0 to *n* − 1:
   1. If `pos >= re` (unsigned), stop.
   2. `end` is `re` for the last element; otherwise `min(rs + start[i + 1], re)` (a 32-bit add, an unsigned compare).
   3. Style `[min(pos, end), max(pos, end))` with element *i*'s font, face, size and colour; an empty range is
      skipped.
   4. `pos = end`.

So:

- An element's own `scrpStartChar` is never read: run 0 starts at the range start, and each start only ends the run
  before it.
- Unsorted starts: a run is applied backwards over `[end, pos)`, and later runs restyle text already styled; the last
  write wins.
- Equal starts give an empty run, which is skipped: of duplicates, the later one wins.
- Past the text: ends are pinned to the range end, and once a run reaches it the later elements are ignored. The
  compare is unsigned, so a negative start counts as past the end.
- `TESetStyleHandle` installs TextEdit's internal style record, not a `'styl'`, and `TEStyleNew` never reads one.

A size of 0 is stored as 0, and the Font Manager picks the size when drawing: the system font size for font 0 when
that is not 0; else its default size (`FMDefaultSize`, 12 unless changed); else 12 [Code: System 7.1-era Font
Manager].

### 2.4 SimpleText documents

SimpleText 1.4 opens a document so [Code: SimpleText 1.4]:

1. A data fork over $7C00 (31,744) bytes is refused (error 200). Otherwise the whole data fork is the text.
2. `'styl'` 128 from the document's own resource fork (`Get1Resource`) is applied over characters 0 to $7FFF with
   `TEUseStyleScrap` (§2.3).
3. Pictures are looked for only when the document's resource fork holds at least one `'PICT'`. Then every $CA in the
   text counts, not only one at a line start (the search string is `'STR#'` 600 item 7): the *k*-th, counting from 0
   in text order, shows `'PICT'` 1000 + *k* from the document's own fork. *k* advances even when that picture is
   missing, so a missing one leaves a gap in the numbering.
4. The picture's frame comes from its header, scaled to 72 dpi by its horizontal and vertical resolution for an
   extended version 2 header (the word at +$0A is $0011 and the word at +$10 is −2).
5. The frame is centred on the view's width, (right − left)/2 − width/2, with the view's left edge not added. Its top
   is the vertical position `TEGetPoint` gives for the $CA (the bottom of its line) less the height of the first line.
   It is drawn over the text, after `TEUpdate`, with no wrap, clipped to the view
   ([documents.md §2.3](documents.md#23-picture-placement)).
6. When printing, a `'form'` $726D + *k* whose first long has bit 0 set ends the page after the *k*-th $CA's line.

## 3. Writing

What a writer must produce [Doc: Inside Macintosh: Text]:

- `'TEXT'`: the text in Mac OS Roman, a line break as CR, no header.
- `'styl'`: the count, then the elements of §1.3, starts in order with the first at 0; the filler byte 0.
- Encoding Mac OS Roman is the reverse of §1.1. U+00A4 (the currency sign) is also written as $DB and U+2126 (the ohm
  sign) as $BD, the older mappings [Doc: ROMAN.TXT notes].

When SimpleText saves, it removes the old `'styl'` 128 and `'snd '` 10000 and writes the current styles back as
`'styl'` 128 [Code: SimpleText 1.4].

How ClassicMac's editor moves runs when the text changes is in §5.4.

## 4. Variants

- $DB is the euro sign from Mac OS 8.5; earlier systems drew the currency sign ¤ (U+00A4) there [Doc: ROMAN.TXT notes].
- `TEUseStyleScrap` is the same in the ROM and in the Mac OS 9.0 System file (§2.3).
- On a system whose script is not Roman, the Help Manager sets the script's system font and size for a `'TEXT'` with no
  `'styl'` (§2.2); the exact condition was not worked out [Code: Mac OS 9.0 `'PACK'` 14].
- The other Mac scripts (Central European, Cyrillic, Japanese, …) use other encodings; which one a text is in follows
  from its font family ID range or a `'vers'` region (§8).

## 5. ClassicMac

### 5.1 Decoding and encoding

- Every decoder reads text as Mac OS Roman, the only encoding the decode options offer. Each text output records its
  encoding in the export manifest as `"macintosh"` (the IANA name for Mac OS Roman); the `'styl'` JSON holds no text
  and records none ([export-manifest.md](../output/export-manifest.md)). [ClassicMac]
- The control bytes $11–$14 decode to the control code points U+0011–U+0014, not to Chicago's glyphs. [ClassicMac]
- How CRs are written in each output is set by the LineEndings option
  ([text-output.md §1.1](../output/text-output.md#11-text-files)). [ClassicMac]
- Encoding (resource and file names, edited text): a character with no Mac OS Roman byte is an error; nothing is
  replaced silently. [ClassicMac]
- Text in the Symbol font is decoded as Mac OS Roman, so its Greek and mathematical glyphs come out as Roman letters.
  [ClassicMac]

### 5.2 TEXT and styl

- A `'TEXT'` is read whole, whatever its length, and always written as `.txt`
  ([text-output.md §1.1](../output/text-output.md#11-text-files)). When the fork holds a `'styl'` of the same ID, the
  text is styled by it and also written as `.rtf` ([text-output.md §1.6](../output/text-output.md#16-rtf-files)). A
  `'styl'` is written as JSON whether or not a `'TEXT'` pairs with it
  ([text-output.md §1.4](../output/text-output.md#14-styl-json)). [ClassicMac]
- A compressed `'styl'` is decompressed like any resource ([resource-fork.md](resource-fork.md#2-reading)).
  [ClassicMac]
- The count is read unsigned. When the data ends inside an element, the complete elements before it are used and
  `text.styl-short` is reported. A 1-byte `'styl'` has no count and is reported the same way. An empty one is
  reported when decoded on its own, but styles its `'TEXT'` with the default run silently. [ClassicMac]
- Styled output (RTF, the app's preview) applies the elements exactly as §2.3 does, over the range 0 to *L*. With no
  element (no `'styl'`, or an empty one), one default run covers the text: font 3 (Geneva), size 12, face 0, black.
  [ClassicMac]
- Then adjacent characters styled by the same element form one run. Colours become 8-bit by taking each component's
  high byte (`$8000` → 128, `$FFFF` → 255). A size of 0 or less becomes 12 in styled output; the JSON keeps the stored
  value. Height and ascent are kept in the JSON and not used for RTF. Face bit 7 is kept in the JSON and ignored in RTF.
  A sorted `'styl'` whose starts lie within the text gives TextEdit's result. [ClassicMac]
- No diagnostic for `'styl'` elements out of order, with negative starts or with starts past the text, or for an empty
  `'styl'` found for a `'TEXT'`. [ClassicMac]

### 5.3 Font names

ClassicMac names fonts for its outputs (`StyleRuns.FontName`) [ClassicMac]:

- The IDs of §1.5 by the table, ID 0 as Chicago and ID 1 as Geneva.
- The LaserWriter fonts' numbers 13–34: 13 Zapf Dingbats, 14 Bookman, 15 Helvetica Narrow, 16 Palatino, 18 Zapf Chancery, 33 Avant
  Garde, 34 New Century Schoolbook.
- Any other ID as `Font n`, for example `Font 1024`.

### 5.4 Writing

The editor (`TextResources`) writes `'TEXT'` and its `'styl'` back [ClassicMac]:

1. The text is encoded as Mac OS Roman (§3); a line break (`\n` or `\r\n`) is written as CR. Text that Mac OS Roman
   cannot hold is refused, not approximated.
2. A `'TEXT'` without a `'styl'` gets none.
3. With a `'styl'`: the text the old and new versions share at the start and at the end is found. Runs before the change
   are kept as they were; the run the change starts in also styles the inserted text, as typing does in TextEdit. Text
   after the change keeps its run, the starts moved by the change's length.
4. Of two runs at one position the later is kept; a run whose text is all deleted (a start past the new end, other
   than the first run) is dropped.
5. The runs' other fields (height, ascent, font, face, size, colour) are unchanged.

`TextResources.ReadText` decodes a `'TEXT'` with CR as `\n`.

### 5.5 SimpleText documents

- The viewer shows a file of type `'TEXT'` whose data fork is 1 byte to 4 MB (past SimpleText's limit) as styled text
  (§5.2), with its `'styl'` 128 when there is one and one default run when there is not. A document with pictures is previewed as a document
  ([documents.md §5](documents.md#5-classicmac)). [ClassicMac]
- `extract` writes the `'styl'` 128 like any resource (JSON); `unpack` writes the data fork unchanged. [ClassicMac]
- The document with its pictures converts to HTML ([documents.md](documents.md#5-classicmac)). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `text.styl-short` | Warning | A `'styl'` ends inside an element, or is 1 byte (no count); also 0 bytes for a `'styl'` decoded on its own | Uses the complete elements; still writes the output | `TEUseStyleScrap` checks nothing (§2.3); not traced further |

## 7. Verification

- Golden fixtures (`tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs`, outputs in `Golden/`,
  `GoldenTests`):
  - `'TEXT'` 128, 26 bytes, gives `TEXT-128.txt` (the CR as LF) and `TEXT-128.rtf`:

    ```
    54 69 74 6C 65 0D 42 6F 64 79 20 74 65 78 74 2C 20 D2 71 75 6F 74 65 64 D3 2E
                                                    → Title␊Body text, “quoted”.
    ```

  - `'styl'` 128 styles it: three runs, `Title␍` (0–6), `Body ` (6–11) and `text, “quoted”.` (11–26):

    ```
    00 03
    00000000 0016 0012 0014 01 00 0012 0000 0000 0000   start 0,  Times (20) 18, bold,      black
    00000006 000C 000A 0004 02 00 000A FFFF 0000 0000   start 6,  Monaco (4) 10, italic,    red
    0000000B 000F 000C 0003 04 00 000C 0000 8000 0000   start 11, Geneva (3) 12, underline, green $8000
    ```

  - `'TEXT'` 129 has no `'styl'` 129: text only. `'styl'` 130 has no `'TEXT'`: JSON only.
  - `'STR '` 128 (in [strings.md §7](strings.md#7-verification)) proves the Mac OS Roman decoding: `43 61 66 8E 20 C4 AA`
    is `Café ƒ™`.
- `TextDecoderTests`: styled text to text and RTF; runs over the whole text and the default Geneva 12 run; runs applied
  as `TEUseStyleScrap` applies them (out of order, duplicate starts, a start past the text and a negative one stopping
  the rest); `'TEXT'` without `'styl'` and `'styl'` alone.
- `TextResourcesTests.Style_runs_follow_the_text`: insertion, a replacement across a run boundary, a deleted run, no
  `'styl'`.
- `MacRomanTests` (`tests/ClassicMac.Core.Tests/CoreTypeTests.cs`): every byte round-trips; U+00A4 and U+2126 encode as
  $DB and $BD; unmappable text is refused.
- `tests/ClassicMac.App.Tests/PreviewTests.cs`: a `'TEXT'` with its `'styl'` previews as styled text.
- DOCMaker 4.8.4's reader in SheepShaver, Mac OS 9.0, shows `'styl'` runs as stored [Verified].

## 8. Not covered

- Other Mac encodings: the other scripts and how the script is chosen (font family ID range, `'vers'` region); a
  Symbol font table.
- The Help Manager's exact condition for a non-Roman script (§4).
- The source of the LaserWriter font numbers 13–34 that ClassicMac names (§5.3) is not recorded.
- SimpleText's printing page breaks (`'form'`) and voice annotations (`'snd '` 10000) are not read.

## 9. References

1. Apple, *Inside Macintosh: Text* (1993): `'TEXT'`, TextEdit's style scrap (`StScrpRec`), the standard font family
   numbers.
2. Apple, *Inside Macintosh: More Macintosh Toolbox* (1993), Help Manager: styled text kept as a `'TEXT'`/`'styl'`
   pair.
3. Apple, *Inside Macintosh: Imaging With QuickDraw* (1994): the `Style` set.
4. Unicode, `VENDORS/APPLE/ROMAN.TXT` (version c02, 2005): Mac OS Roman to Unicode. Mapping data.
5. Microsoft, *Rich Text Format (RTF) Specification*, version 1.9.1 (2008): the RTF output
   ([text-output.md §1.6](../output/text-output.md#16-rtf-files)).
6. Code traced: Mac OS 9.0's TextEdit `TEUseStyleScrap` (the ROM and its copy in the System file), Help Manager
   (`'PACK'` 14) and SimpleText 1.4; DOCMaker 4.8.4's stand-alone reader; the System 7.1-era Font
   Manager (default sizes); the 68k ROM and Mac OS ROM 1.6 (the style table).
