# Styled text (TEXT, styl)

This document describes the classic Mac OS text resources — `'STR '`, `'STR#'`, `'TEXT'` with its `'styl'` style
runs, and `'vers'` — and the SimpleText documents that store their text in the data fork, completely enough to write a
reader without reading ClassicMac's code. It also specifies what ClassicMac writes for them: UTF-8 text, RTF for
styled text, and JSON for string lists, style runs and versions. The formats come from Apple's documentation; the
output formats are ClassicMac's own and are marked so.

References:

- *Inside Macintosh: Text* (1993): the string and string-list resources, `'TEXT'`, TextEdit's style scrap
  (`StScrpRec`), the standard font family numbers.
- *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Finder Interface chapter: the version resource.
- *Inside Macintosh: More Macintosh Toolbox* (1993), Help Manager: styled text kept as a `'TEXT'`/`'styl'` pair.
- Apple's Rez template for `'vers'` (`SysTypes.r`, MPW).
- Mac OS 9.0, disassembly: TextEdit's `TEUseStyleScrap` (the ROM and its copy in the System file), the Help Manager
  (`'PACK'` 14), SimpleText 1.4 and Finder 9.0; and the System 7.1-era Font Manager for default sizes.
- Unicode's Apple mapping file `VENDORS/APPLE/ROMAN.TXT` (version c02, 2005): Mac OS Roman to Unicode.
- Microsoft's *Rich Text Format (RTF) Specification*, version 1.9.1 (2008): the RTF output.

Contents

1. [Conventions](#1-conventions)
2. [Mac OS Roman](#2-mac-os-roman)
3. [Plain text (TEXT)](#3-plain-text-text)
4. [Style runs (styl)](#4-style-runs-styl)
5. [SimpleText documents](#5-simpletext-documents)
6. [Writing text resources](#6-writing-text-resources)
7. [Diagnostics](#7-diagnostics)
8. [Not covered yet](#8-not-covered-yet)

---

## 1. Conventions

The shared conventions of [README.md](../README.md) hold: big-endian values, `u8`/`u16`/`u32` and `i8`/`i16`/`i32`,
`OSType` codes in quotes, Pascal strings as a length byte and that many bytes. In addition:

- A **Pascal string** holds 0–255 bytes. Nothing pads it: the next field starts right after its last byte.
- Every text in this document is a **byte string in a single-byte Mac encoding**, Mac OS Roman unless noted (§2). One
  byte is one character, so a character offset (a style run's start) is a byte offset.
- An `RGBColor` is three `u16`: red, green, blue, each 0–65535.
- Characters are named by Unicode code point (`U+20AC`) or by byte (`$DB`).

Tags. Every rule carries a source tag from [README.md](../README.md): **[Doc]**, **[Code]**, **[Verified]**,
**[Author]** (here: Microsoft's RTF specification, for the meaning of RTF control words) and **[Fitted]**. This
document adds one:

| Tag | Meaning |
| --- | --- |
| **[ClassicMac]** | ClassicMac's own choice for its output: file formats, JSON field names, the RTF mapping, and how odd or damaged input is presented in the output. Apple's software writes none of these outputs, so there is nothing to match; the tag says the rule can change with a new decoder version |

---

## 2. Mac OS Roman

### 2.1 Decoding

Mac OS Roman maps every byte to exactly one Unicode code point, so decoding never fails and decoding then encoding
gives back the same bytes [Doc] (ROMAN.TXT).

- **$00–$7F** are ASCII, control characters included [Doc] (ROMAN.TXT).
- **$80–$FF** map as in the table below [Doc] (ROMAN.TXT, version c02). Row = high nibble, column = low nibble.

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

Entries worth knowing [Doc] (ROMAN.TXT notes):

| Byte | Code point | Note |
| --- | --- | --- |
| $CA | U+00A0 | No-break space ("option-space"); SimpleText and DOCMaker use it as a picture anchor (§5) |
| $DB | U+20AC | Euro sign from Mac OS 8.5; earlier systems drew the currency sign ¤ (U+00A4) here |
| $BD | U+03A9 | Greek capital omega; older tables gave the ohm sign U+2126 |
| $C6 | U+2206 | Increment (∆), not the Greek capital delta |
| $F0 | U+F8FF | The Apple logo, a private-use code point |

Example (the `'STR '` 128 golden output): the bytes `43 61 66 8E 20 C4 AA` decode to `Café ƒ™` [Doc].

### 2.2 Control characters and line breaks

- A line break is **CR ($0D)** [Doc] (*Inside Macintosh: Text*). LF ($0A) has no meaning in Mac text.
- The Chicago font draws glyphs for some control bytes ($11 command key, $12 check mark, $13 diamond, $14 Apple logo)
  [Doc] (ROMAN.TXT notes). They are still control characters in Mac OS Roman, and ClassicMac decodes them as the
  control code points U+0011–U+0014 [ClassicMac].
- How CRs are written in each output is set by the **LineEndings** option ([text-output.md §1](../output/text-output.md#1-text-output)).

### 2.3 Encoding

Where ClassicMac writes Mac OS Roman (resource and file names, and later edited text), it uses the reverse of the
table, and additionally accepts U+00A4 for $DB and U+2126 for $BD, the older mappings [Doc] (ROMAN.TXT notes). A
character with no Mac OS Roman byte is an error; nothing is replaced silently [ClassicMac].

### 2.4 Other encodings

Every decoder in this document reads text as Mac OS Roman; it is the only encoding the decode options offer so far.
The other Mac scripts (Central European, Cyrillic, Japanese, …) and how the script is chosen (font family ID range,
`'vers'` region) are planned [ClassicMac]. Each text output records its encoding in the export manifest as
`"macintosh"` (the IANA name for Mac OS Roman); the `'styl'` JSON holds no text and records none [ClassicMac]. See
[export-manifest.md](../output/export-manifest.md).

---

## 3. Plain text (TEXT)

A `'TEXT'` resource is text with no header and no length field: its length is the resource's size [Doc]
(*Inside Macintosh: Text*). TextEdit keeps at most 32,767 characters in one record [Doc], but a `'TEXT'` resource
itself has no limit, and ClassicMac reads it whole [ClassicMac].

- Output: the text as a `.txt` file ([text-output.md §1](../output/text-output.md#1-text-output)), always [ClassicMac].
- When the same resource fork holds a `'styl'` resource **with the same ID**, the text is styled by it (§4) and is also
  written as `.rtf` ([text-output.md §3](../output/text-output.md#3-rtf-output)) [ClassicMac]. Pairing by ID is how styled text is stored: the Help Manager's styled-text
  items name one ID for both resources [Doc] (*More Macintosh Toolbox*), and DOCMaker's reader loads chapter *k*'s
  `'TEXT'` and `'styl'` from the same ID and passes both to TextEdit [Code] (DOCMaker 4.8.4 stand-alone reader).
- A compressed `'styl'` is decompressed like any resource (see [resource-fork.md](resource-fork.md)) [ClassicMac].

The Help Manager's styled items [Code: Mac OS 9.0 `'PACK'` 14]:

- It loads the `'TEXT'` and then the `'styl'` of the **same ID**, with `GetResource` (any open file). No `'TEXT'`: no
  balloon.
- It sets the whole text, then applies the `'styl'` with `TEUseStyleScrap` (§4.4) over characters 0 to $7FF only; the
  text after that is not restyled.
- With no `'styl'` on a system whose script is not Roman, it sets the script's system font and size (the exact
  condition was not worked out).
- A string item (`'STR#'`) that `GetIndString` returns empty is an error to the Help Manager: no balloon ([strings.md §2](strings.md#2-string-lists-str)).

Example (golden `'TEXT'` 128, 26 bytes):

```
54 69 74 6C 65 0D 42 6F 64 79 20 74 65 78 74 2C 20 D2 71 75 6F 74 65 64 D3 2E
                                                    → TEXT-128.txt:  Title␊Body text, “quoted”.
```

---

## 4. Style runs (styl)

### 4.1 Layout

A `'styl'` resource is TextEdit's style scrap record, `StScrpRec`: a count, then that many 20-byte style elements
(`ScrpSTElement`) [Doc] (*Inside Macintosh: Text*, TextEdit). DOCMaker's reader hands the resource to TextEdit's
`TEUseStyleScrap` unchanged, and the runs' fonts, sizes, faces and colours show as stored [Code] (DOCMaker 4.8.4)
[Verified] (its reader in SheepShaver).

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | i16 | `scrpNStyles`: number of elements |
| +$02 | 20 × *n* | elements | The elements, below |

Each element:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | i32 | `scrpStartChar`: offset of the run's first character in the text |
| +$04 | 2 | i16 | `scrpHeight`: line height, in pixels |
| +$06 | 2 | i16 | `scrpAscent`: font ascent, in pixels |
| +$08 | 2 | i16 | `scrpFont`: font family ID (§4.3) |
| +$0A | 1 | u8 | `scrpFace`: style bits (§4.2) |
| +$0B | 1 | u8 | Filler (the Pascal `Style` set is one byte, padded to a word) |
| +$0C | 2 | i16 | `scrpSize`: point size |
| +$0E | 6 | RGBColor | `scrpColor`: text colour |

- Runs are stored in order of start, and the first starts at 0 [Doc]. Each run extends to the next run's start; the
  last to the end of the text [Doc].
- Height and ascent are TextEdit's line metrics for the run. `TEUseStyleScrap` ignores them and TextEdit computes its
  own [Code: Mac OS 9.0 ROM]. ClassicMac keeps them in the JSON and does not use them for RTF [ClassicMac].
- A size of 0 means the default size [Doc]. TextEdit stores the 0, and the Font Manager picks the size: the system
  font size for font 0 when that is not 0; else its default size (`FMDefaultSize`, 12 unless changed); else 12
  [Code: System 7.1-era Font Manager]. ClassicMac takes a size of 0 (and any negative size) as
  12 points in styled output, and keeps the stored value in the JSON [ClassicMac].
- ClassicMac reads the count unsigned. When the data ends inside an element, the elements before it are used and
  `text.styl-short` is reported. A 1-byte resource has no count and is reported the same way; an empty one is reported
  when decoded on its own, but styles its `'TEXT'` with the default run silently [ClassicMac].

### 4.2 Face bits

`scrpFace` is QuickDraw's `Style` [Doc] (*Inside Macintosh: Text*; *Imaging With QuickDraw*):

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

Bit 7 is ignored in the RTF output and kept in the JSON [ClassicMac]. QuickDraw's default style table narrows each
condensed character by one pixel and widens each extended one by one pixel [Code] (68k ROM, Mac OS ROM 1.6; the same
table in Mac OS 9's native Font Manager).

### 4.3 Fonts by ID

A style names its font by family ID. The standard IDs [Doc] (*Inside Macintosh: Text*, font family numbers) and the
names ClassicMac gives them [ClassicMac]:

| ID | Name | ID | Name | ID | Name |
| --- | --- | --- | --- | --- | --- |
| 0 | Chicago (the system font) | 6 | London | 20 | Times |
| 1 | Geneva (the application font) | 7 | Athens | 21 | Helvetica |
| 2 | New York | 8 | San Francisco | 22 | Courier |
| 3 | Geneva | 9 | Toronto | 23 | Symbol |
| 4 | Monaco | 11 | Cairo | 24 | Mobile |
| 5 | Venice | 12 | Los Angeles | | |

- ID 0 and ID 1 are aliases: the system font and the application font, which the running system resolves (Geneva is
  the application font on US systems) [Doc]. ClassicMac names ID 0 Chicago and ID 1 Geneva [ClassicMac].
- Any other ID is assigned by the system that installed the font (its `'FOND'` resource) and has no fixed name;
  ClassicMac names it `Font n`, for example `Font 1024` [ClassicMac].
- Text in the Symbol font is still decoded as Mac OS Roman, so its Greek and mathematical glyphs come out as Roman
  letters [ClassicMac]; a Symbol table arrives with the other encodings (§2.4).

### 4.4 How runs map onto the text

A `'styl'` reaches TextEdit through `TEUseStyleScrap` (`SetStylScrap`), which applies it over a range of the text;
SimpleText (§5), the Help Manager (§3) and DOCMaker all use it. It checks nothing and returns no error
[Code: Mac OS 9.0 ROM; the System file's copy is byte-identical]:

```
rs, re = the range, sorted, each pinned to the text length L
pos = rs
for i = 0 … n − 1:
    if pos >= re: stop                                        (unsigned)
    end = (i == n − 1) ? re : min(rs + start[i + 1], re)      (32-bit add, unsigned compare)
    style [min(pos, end), max(pos, end)) with element i       (font, face, size, colour; empty: skipped)
    pos = end
```

- An element's own `scrpStartChar` is never read: run 0 starts at the range start, and each start only ends the run
  before it.
- **Unsorted** starts: a run is applied backwards over `[end, pos)`, and later runs restyle text already styled; the
  last write wins.
- **Equal** starts give an empty run, which is skipped: of duplicates, the later one wins.
- **Past the text**: ends are pinned to the range end, and once a run reaches it the later elements are ignored. The
  compare is unsigned, so a negative start counts as past the end.
- `TESetStyleHandle` installs TextEdit's internal style record, not a `'styl'`, and `TEStyleNew` never reads one.

For styled output (RTF, the app's preview) ClassicMac applies the elements exactly this way over the whole text
(the range 0 to *L*) [ClassicMac: the range]. With no element (no `'styl'`, or an empty one), one default run covers
the text: font 3 (Geneva), size 12, face 0, black [ClassicMac].

Then adjacent characters styled by the same element form one run; colours become 8-bit by taking each component's
high byte (`$8000` → 128, `$FFFF` → 255), and a size of 0 or less becomes 12 [ClassicMac]. A sorted `'styl'` whose
starts lie within the text gives TextEdit's result.

Example (golden `'styl'` 128 with `'TEXT'` 128, 26 characters):

```
00 03
00000000 0016 0012 0014 01 00 0012 0000 0000 0000   start 0,  Times (20) 18, bold,      black
00000006 000C 000A 0004 02 00 000A FFFF 0000 0000   start 6,  Monaco (4) 10, italic,    red
0000000B 000F 000C 0003 04 00 000C 0000 8000 0000   start 11, Geneva (3) 12, underline, green $8000
```

gives three runs: `Title␍` (0–6), `Body ` (6–11) and `text, “quoted”.` (11–26).

---

## 5. SimpleText documents

SimpleText (and TeachText before it) keeps a document's text in the **data fork** of a file of type `'TEXT'` (creator
`'ttxt'`; read-only documents are type `'ttro'`, read by the same code), and its styles in `'styl'` resource **128**
of the same file's resource fork. SimpleText 1.4 (Mac OS 9.0) [Code: SimpleText 1.4]:

- **Text.** A data fork over **$7C00** (31,744) bytes is refused (error 200). Otherwise the whole data fork is the
  text: plain Mac text (§2), no header.
- **Styles.** `'styl'` 128 from the document's own resource fork (`Get1Resource`) is applied over characters 0 to
  $7FFF with `TEUseStyleScrap`, so the rules of §4.4 hold. Saving removes the old `'styl'` 128 (and `'snd '` 10000,
  the document's voice annotation) and writes the current styles back as `'styl'` 128.
- **Pictures** are looked for only when the document's resource fork holds at least one `'PICT'`. Then **every** $CA
  (option-space) in the text counts, not only one at a line start (the search string is `'STR#'` 600 item 7): the
  *k*-th, counting from 0 in text order, shows **`'PICT'` 1000 + *k*** from the document's own fork. *k* advances
  even when that picture is missing, so a missing one leaves a gap in the numbering.
- **Placement.** The picture's frame comes from its header, scaled to 72 dpi (by its horizontal and vertical
  resolution) for an extended version 2 header (the word at +$0A is $0011 and the word at +$10 is −2). The frame is
  centred on the view's width, (right − left)/2 − width/2, with the view's left edge not added. Its top is the
  vertical position `TEGetPoint` gives for the $CA (the bottom of its line) less the height of the **first** line.
  It is drawn over the text, with no wrap, clipped to the view.
- **Printing.** When the document has `'form'` resources, `'form'` $726D + *k* belongs to the *k*-th $CA: bit 0 of its
  first long set ends the page after that $CA's line.

What ClassicMac does [ClassicMac]:

- Its viewer shows such files as styled text (§4.4), with the `'styl'` 128 when there is one and one default run when
  there is not. It reads data forks up to 4 MB for this, past SimpleText's limit.
- `extract` writes the `'styl'` 128 like any resource (JSON); `unpack` writes the data fork unchanged.
- The document with its pictures converts to HTML: [documents.md](documents.md).

---

## 6. Writing text resources

ClassicMac's editor (`TextResources`, `VersionResource`) writes these resources back [ClassicMac, following the formats
above]:

- **Text** is Mac OS Roman (§2); a line break is written as a carriage return. Text that Mac OS Roman cannot hold
  is refused, not approximated.
- **`TEXT` with a `styl` of its ID:** the style runs follow the edit. Text before and after the change keeps its runs
  (their starts moved by the change's length); text inserted takes the style of the run the change starts in, as
  typing does in TextEdit; a run whose text is all deleted is dropped, and two runs at one position keep the later.
  The runs' other fields (height, ascent, font, face, size, colour) are unchanged. A `TEXT` without a `styl` gets none.

---

## 7. Diagnostics

All are warnings: the output is still written from what could be read, except where noted [ClassicMac]. On the Mac,
`GetIndString` returns an empty string for an index past a list's count ([strings.md §2](strings.md#2-string-lists-str)), and TextEdit applies any `'styl'`
without complaint (§4.4); what the Toolbox does with the other damaged cases below has not been traced.

| Code | Severity | Emitted by | Meaning |
| --- | --- | --- | --- |
| `text.styl-short` | Warning | `'TEXT'` (its `'styl'`), `'styl'` | The data ends inside a style element, or is 1 byte (no count; 0 bytes too for a `'styl'` decoded alone); the complete elements are used |

Cases with no diagnostic [ClassicMac]: a `'STR#'` under 2 bytes (no output); bytes after the last string; an unknown
`'vers'` stage; a missing long version string; `'styl'` elements out of order, with negative starts or starts past the
text; an empty `'styl'` found for a `'TEXT'` (one default run).

---

## 8. Not covered yet

- **Other Mac encodings** (§2.4).
- **Writing** text resources: planned with the editors.
