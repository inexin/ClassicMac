# Text resources — an implementer's specification

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
3. [Strings (STR)](#3-strings-str)
4. [String lists (STR#)](#4-string-lists-str)
5. [Plain text (TEXT)](#5-plain-text-text)
6. [Style runs (styl)](#6-style-runs-styl)
7. [SimpleText documents](#7-simpletext-documents)
8. [Versions (vers)](#8-versions-vers)
9. [Text output](#9-text-output)
10. [JSON output](#10-json-output)
11. [RTF output](#11-rtf-output)
12. [Writing text resources](#12-writing-text-resources)
13. [Diagnostics](#13-diagnostics)
14. [Not covered yet](#14-not-covered-yet)

---

## 1. Conventions

The shared conventions of [README.md](README.md) hold: big-endian values, `u8`/`u16`/`u32` and `i8`/`i16`/`i32`,
`OSType` codes in quotes, Pascal strings as a length byte and that many bytes. In addition:

- A **Pascal string** holds 0–255 bytes. Nothing pads it: the next field starts right after its last byte.
- Every text in this document is a **byte string in a single-byte Mac encoding**, Mac OS Roman unless noted (§2). One
  byte is one character, so a character offset (a style run's start) is a byte offset.
- An `RGBColor` is three `u16`: red, green, blue, each 0–65535.
- Characters are named by Unicode code point (`U+20AC`) or by byte (`$DB`).

Tags. Every rule carries a source tag from [README.md](README.md): **[Doc]**, **[Code]**, **[Verified]**,
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
| $CA | U+00A0 | No-break space ("option-space"); SimpleText and DOCMaker use it as a picture anchor (§7) |
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
- How CRs are written in each output is set by the **LineEndings** option (§9).

### 2.3 Encoding

Where ClassicMac writes Mac OS Roman (resource and file names, and later edited text), it uses the reverse of the
table, and additionally accepts U+00A4 for $DB and U+2126 for $BD, the older mappings [Doc] (ROMAN.TXT notes). A
character with no Mac OS Roman byte is an error; nothing is replaced silently [ClassicMac].

### 2.4 Other encodings

Every decoder in this document reads text as Mac OS Roman; it is the only encoding the decode options offer so far.
The other Mac scripts (Central European, Cyrillic, Japanese, …) and how the script is chosen (font family ID range,
`'vers'` region) are planned [ClassicMac]. Each text output records its encoding in the export manifest as
`"macintosh"` (the IANA name for Mac OS Roman); the `'styl'` JSON holds no text and records none [ClassicMac]. See
[EXPORT-MANIFEST.md](EXPORT-MANIFEST.md).

---

## 3. Strings (STR)

A `'STR '` resource (type code with a trailing space) is one Pascal string [Doc] (*Inside Macintosh: Text*, String
Resource).

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | Length *n* (0–255) |
| +$01 | *n* | bytes | The text |

- Bytes after the string are ignored [ClassicMac].
- If the length byte says more than the resource holds, the text is cut to the bytes present and `text.string-short`
  is reported; an empty resource gives an empty text and the same diagnostic [ClassicMac].
- Output: the text as a `.txt` file (§9) [ClassicMac].

Example (golden `'STR '` 128, "Greeting"):

```
0E 43 61 66 8E 20 C4 AA 0D 6C 69 6E 65 20 32        → STR_-128.txt:  Café ƒ™␊line 2
```

(␊ = LF, from the stored CR with the default LineEndings.)

---

## 4. String lists (STR#)

A `'STR#'` resource is a count followed by that many Pascal strings, packed with no padding [Doc]
(*Inside Macintosh: Text*, String List Resource).

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | Count of strings |
| +$02 | … | Pascal strings | The strings, one after another |

- The Toolbox numbers the strings from **1** (`GetIndString`), and returns an empty string for an index past the
  count [Doc]; for index 0 or a missing `'STR#'` too (the count compared unsigned) [Code: Mac OS 9.0 Help Manager's
  copy of the glue]. ClassicMac's JSON array is 0-based: array element *i* is string *i* + 1 [ClassicMac].
- *Inside Macintosh* declares the count as an integer; ClassicMac reads it unsigned, so a count of $8000 or more is
  read as a large count and the data's end stops the list [ClassicMac].
- **Cut lists.** When the data ends before the count is reached, the strings read so far are kept; a string whose
  length byte runs past the data is kept as far as it goes and is the last one; a missing length byte ends the list.
  `text.string-list-short` is reported [ClassicMac].
- A resource shorter than 2 bytes gives no output and no diagnostic [ClassicMac].
- Bytes after the last counted string are ignored [ClassicMac].
- Output: JSON (§10.2) [ClassicMac].

Example (golden `'STR#'` 128):

```
00 03  05 48 75 6D 61 6E  03 83 6C 66  00           → {"strings": ["Human", "Élf", ""]}
```

A cut list: `00 03  03 6F 6E 65  05 74 77` gives `["one", "tw"]` and `text.string-list-short`.

---

## 5. Plain text (TEXT)

A `'TEXT'` resource is text with no header and no length field: its length is the resource's size [Doc]
(*Inside Macintosh: Text*). TextEdit keeps at most 32,767 characters in one record [Doc], but a `'TEXT'` resource
itself has no limit, and ClassicMac reads it whole [ClassicMac].

- Output: the text as a `.txt` file (§9), always [ClassicMac].
- When the same resource fork holds a `'styl'` resource **with the same ID**, the text is styled by it (§6) and is also
  written as `.rtf` (§11) [ClassicMac]. Pairing by ID is how styled text is stored: the Help Manager's styled-text
  items name one ID for both resources [Doc] (*More Macintosh Toolbox*), and DOCMaker's reader loads chapter *k*'s
  `'TEXT'` and `'styl'` from the same ID and passes both to TextEdit [Code] (DOCMaker 4.8.4 stand-alone reader).
- A compressed `'styl'` is decompressed like any resource (see [RESOURCE-FORK.md](RESOURCE-FORK.md)) [ClassicMac].

The Help Manager's styled items [Code: Mac OS 9.0 `'PACK'` 14]:

- It loads the `'TEXT'` and then the `'styl'` of the **same ID**, with `GetResource` (any open file). No `'TEXT'`: no
  balloon.
- It sets the whole text, then applies the `'styl'` with `TEUseStyleScrap` (§6.4) over characters 0 to $7FF only; the
  text after that is not restyled.
- With no `'styl'` on a system whose script is not Roman, it sets the script's system font and size (the exact
  condition was not worked out).
- A string item (`'STR#'`) that `GetIndString` returns empty is an error to the Help Manager: no balloon (§4).

Example (golden `'TEXT'` 128, 26 bytes):

```
54 69 74 6C 65 0D 42 6F 64 79 20 74 65 78 74 2C 20 D2 71 75 6F 74 65 64 D3 2E
                                                    → TEXT-128.txt:  Title␊Body text, “quoted”.
```

---

## 6. Style runs (styl)

### 6.1 Layout

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
| +$08 | 2 | i16 | `scrpFont`: font family ID (§6.3) |
| +$0A | 1 | u8 | `scrpFace`: style bits (§6.2) |
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

### 6.2 Face bits

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

### 6.3 Fonts by ID

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

### 6.4 How runs map onto the text

A `'styl'` reaches TextEdit through `TEUseStyleScrap` (`SetStylScrap`), which applies it over a range of the text;
SimpleText (§7), the Help Manager (§5) and DOCMaker all use it. It checks nothing and returns no error
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

## 7. SimpleText documents

SimpleText (and TeachText before it) keeps a document's text in the **data fork** of a file of type `'TEXT'` (creator
`'ttxt'`; read-only documents are type `'ttro'`, read by the same code), and its styles in `'styl'` resource **128**
of the same file's resource fork. SimpleText 1.4 (Mac OS 9.0) [Code: SimpleText 1.4]:

- **Text.** A data fork over **$7C00** (31,744) bytes is refused (error 200). Otherwise the whole data fork is the
  text: plain Mac text (§2), no header.
- **Styles.** `'styl'` 128 from the document's own resource fork (`Get1Resource`) is applied over characters 0 to
  $7FFF with `TEUseStyleScrap`, so the rules of §6.4 hold. Saving removes the old `'styl'` 128 (and `'snd '` 10000,
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

- Its viewer shows such files as styled text (§6.4), with the `'styl'` 128 when there is one and one default run when
  there is not. It reads data forks up to 4 MB for this, past SimpleText's limit.
- `extract` writes the `'styl'` 128 like any resource (JSON); `unpack` writes the data fork unchanged.
- The document with its pictures converts to HTML: [DOCUMENTS.md](DOCUMENTS.md).

---

## 8. Versions (vers)

### 8.1 Layout

A `'vers'` resource describes the version of a file (ID 1) or of the product it belongs to (ID 2) [Doc]
(*Inside Macintosh: Macintosh Toolbox Essentials*, Finder Interface). The Finder shows only the strings
[Code: Finder 9.0]:

- `'vers'` 1's **long** string is Get Info's "Version:" field. With no `'vers'` 1 the Finder uses the string of the
  file's owner resource (the creator code, ID 0), and when that is empty, "n/a".
- `'vers'` 1's **short** string is the list view's Version column (and AppleScript's `version` property).
- `'vers'` 2's **long** string is shown at the top of the Get Info window.
- The numbers are never shown; the Finder only compares the first word of `'vers'` 1 as a number in places.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | Major version, binary-coded decimal |
| +$01 | 1 | u8 | Minor version (high nibble) and bug-fix version (low nibble) |
| +$02 | 1 | u8 | Release stage (below) |
| +$03 | 1 | u8 | Non-release revision number, for a stage before final |
| +$04 | 2 | i16 | Region code (country code in older documentation): 0 = United States |
| +$06 | 1 + *n* | Pascal string | Short version string, e.g. `1.2` |
| … | 1 + *m* | Pascal string | Long version string: version and copyright, e.g. `1.2, © 1996` |

The first four bytes are the `NumVersion` record [Doc]. Stages [Doc]:

| Value | Stage | Letter |
| --- | --- | --- |
| $20 | Development | d |
| $40 | Alpha | a |
| $60 | Beta | b |
| $80 | Final (release) | f |

### 8.2 Reading

- The major version is read as BCD: `(high nibble × 10) + low nibble` [Doc]. Nibbles above 9 are not rejected; they
  simply add (`$1A` → 20) [ClassicMac].
- Minor and bug-fix are the two nibbles of byte 1 [Doc].
- The non-release byte has no Apple reading: no Mac OS 9.0 code interprets it, since the Finder copies only the
  strings [Code: Finder 9.0]. *Inside Macintosh* does not say whether it is BCD, and Apple's Rez template marks the
  two bytes before it "in BCD" but this one only as a hex byte [Doc] (`SysTypes.r`), which suggests binary. Apple's
  own files store BCD, though (Disk Copy 6.5b13: $13 for "6.5b13"), and some other developers' binary ($0F for
  15). ClassicMac reads it as BCD when both nibbles are 0–9 (`$12` → 12), and as binary otherwise (`$0F` → 15)
  [Fitted: Apple's and others' files].
- The region code is a signed integer, kept as a number [Doc].
- Strings are Mac OS Roman (§2). Long version strings may hold a CR: Disk Copy writes the image checksum on a second
  line (`…image␍CRC: $…`) [Fitted].
- Fewer than 7 bytes (the 6-byte header and a length byte): no output, `text.vers-short` [ClassicMac].
- A short string whose length runs past the data is cut; the long string is then empty; `text.vers-short` [ClassicMac].
  A long string that runs past the data is cut, with the same diagnostic. No long string at all (the data ends after
  the short one) gives an empty long string, silently [ClassicMac].
- Bytes after the long string are ignored [ClassicMac].

### 8.3 The display string

ClassicMac shows the numeric version as one string [ClassicMac]:

```
major "." minor [ "." bugFix  if bugFix > 0 ] [ letter nonRelease  if stage ≠ $80 or nonRelease > 0 ]
```

An unknown stage byte uses the letter `?` and the stage name `unknown ($XX)` (two upper-case hex digits)
[ClassicMac]. The Finder shows the strings, not this number (§8.1).

| Bytes 0–3 | Display | Stage |
| --- | --- | --- |
| `04 84 80 00` | 4.8.4 | final |
| `01 00 60 03` | 1.0b3 | beta |
| `06 50 60 13` | 6.5b13 | beta |
| `03 00 60 0F` | 3.0b15 | beta |
| `10 25 20 12` | 10.2.5d12 | development |
| `02 10 40 01` | 2.1a1 | alpha |
| `01 20 80 00` | 1.2 | final |
| `01 00 80 02` | 1.0f2 | final |

Output: JSON (§10.4) [ClassicMac].

---

## 9. Text output

`'STR '` and `'TEXT'` are written as `.txt` files [ClassicMac]:

- **UTF-8, no byte-order mark**, the decoded text only: no trailing line break is added [ClassicMac].
- **LineEndings** (a decode option) sets how line breaks are written [ClassicMac]:
  - `Lf` (the default): every CR becomes LF. An LF already in the text stays, so a stored CR LF becomes LF LF.
  - `AsStored`: CRs are kept.
  There is no CR LF choice. The CLI and the app use the default.
- LineEndings also applies to the strings of the `'STR#'` JSON and to the long version string of the `'vers'` JSON
  (as JSON escapes, `\n` or `\r`), but not to the short version string, and never to RTF, which writes every line
  break as `\par` (§11) [ClassicMac].

---

## 10. JSON output

### 10.1 Common form

- UTF-8, no byte-order mark, one object per file, indented two spaces [ClassicMac].
- Non-ASCII characters are written as themselves, not escaped; `"` and `\` are escaped, CR and LF as `\r` and `\n`,
  other control characters as `\u00XX` [ClassicMac].
- Lines end with LF on every platform, and the file ends with one LF, so outputs and their hashes are the same
  everywhere [ClassicMac].

### 10.2 STR#

```json
{
  "strings": [
    "Human",
    "Élf",
    ""
  ]
}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `strings` | array of string | The strings in stored order (element 0 is `GetIndString` index 1), as read (§4) |

[ClassicMac]

### 10.3 styl

One object per stored element, in **stored order** (not sorted, negative starts kept), with the stored values
unchanged [ClassicMac]:

| Field | Type | Meaning |
| --- | --- | --- |
| `start` | integer | `scrpStartChar` |
| `height` | integer | `scrpHeight` |
| `ascent` | integer | `scrpAscent` |
| `font` | integer | `scrpFont` |
| `fontName` | string | The font's name (§6.3) |
| `face` | integer | `scrpFace`, 0–255 (§6.2) |
| `size` | integer | `scrpSize`, 0 kept as 0 |
| `color` | array of 3 integers | Red, green, blue, 0–65535 |

The array is `runs`. The golden `'styl'` 128 gives (first run shown):

```json
{
  "runs": [
    {
      "start": 0,
      "height": 22,
      "ascent": 18,
      "font": 20,
      "fontName": "Times",
      "face": 1,
      "size": 18,
      "color": [
        0,
        0,
        0
      ]
    },
    …
  ]
}
```

A `'styl'` is written as JSON whether or not a `'TEXT'` of the same ID exists [ClassicMac].

### 10.4 vers

| Field | Type | Meaning |
| --- | --- | --- |
| `display` | string | The display string (§8.3) |
| `major` | integer | Major version (BCD decoded) |
| `minor` | integer | Minor version (0–15) |
| `bugFix` | integer | Bug-fix version (0–15) |
| `stage` | string | `development`, `alpha`, `beta`, `final` or `unknown ($XX)` |
| `nonRelease` | integer | Non-release number (BCD, or binary when not BCD; §8.2) |
| `region` | integer | Region code |
| `shortVersion` | string | Short version string |
| `longVersion` | string | Long version string, with LineEndings applied |

[ClassicMac]. The golden `'vers'` 1:

```
01 20 80 00 00 00 03 31 2E 32 0B 31 2E 32 2C 20 A9 20 31 39 39 36
```

```json
{
  "display": "1.2",
  "major": 1,
  "minor": 2,
  "bugFix": 0,
  "stage": "final",
  "nonRelease": 0,
  "region": 0,
  "shortVersion": "1.2",
  "longVersion": "1.2, © 1996"
}
```

---

## 11. RTF output

A `'TEXT'` with a `'styl'` of the same ID is also written as RTF, built from the runs of §6.4 [ClassicMac]. The
meaning of each control word is the RTF specification's [Author]; the choice of control words for each Mac style is
ClassicMac's [ClassicMac].

### 11.1 Structure

The file is ASCII only (all other characters are escaped), with LF line breaks [ClassicMac]:

```
{\rtf1\ansi\ansicpg1252\deff0\uc1
{\fonttbl{\f0 <name>;}{\f1 <name>;}…}
{\colortbl;\red<r>\green<g>\blue<b>;…}
<run><run>…}
```

- **Header**: RTF version 1, ANSI character set, code page 1252, default font `\f0`, one fallback character after each
  `\u` escape [Author]. The code page never matters, since no byte above $7E is written raw [ClassicMac].
- **Font table**: one entry per distinct font ID, in order of first use by the runs, named as in §6.3; entry *i* is
  `\fi` [ClassicMac]. No font family or character set is given, so readers pick a substitute for fonts they lack
  [ClassicMac]. Two IDs with the same name (1 and 3, both Geneva) get two entries [ClassicMac].
- **Colour table**: an empty first entry (`;`, the reader's automatic colour [Author]), then one entry per distinct
  8-bit colour, in order of first use; the colour of table position *j* (1-based) is `\cfj` [ClassicMac].
- The file ends with `}` and LF. There is no paragraph formatting (`\pard`, alignment, tab stops), since `'styl'`
  stores none [ClassicMac].

### 11.2 Runs

Each run is written as `\plain` (reset character formatting [Author]), its font, size and colour, its face control
words, one space, then its escaped text [ClassicMac]:

| Mac style | RTF | Meaning in RTF [Author] |
| --- | --- | --- |
| Font ID | `\fN` | Font table entry *N* |
| Size *s* points | `\fs(2s)` | Size in half-points |
| Colour | `\cfN` | Colour table entry *N* |
| Bold ($01) | `\b` | Bold |
| Italic ($02) | `\i` | Italic |
| Underline ($04) | `\ul` | Continuous underline |
| Outline ($08) | `\outl` | Outline |
| Shadow ($10) | `\shad` | Shadow |
| Condense ($20) | `\expnd-2\expndtw-10` | Character spacing −0.5 pt (quarter-points, then twips) |
| Extend ($40) | `\expnd2\expndtw10` | Character spacing +0.5 pt |

Face words come in the order of the table [ClassicMac]. Every run repeats its font, size and colour even when they
equal the previous run's [ClassicMac].

Differences from the Mac, accepted for now [ClassicMac]:

- QuickDraw's condense and extend change each character's width by one pixel, 1 point at 72 dpi (§6.2), not 0.5 pt.
- QuickDraw's underline breaks around descenders; RTF's `\ul` is continuous.

### 11.3 Text escaping

| Character | Written as |
| --- | --- |
| `\`, `{`, `}` | `\\`, `\{`, `\}` [Author] |
| CR or LF | `\par` and an LF (the LF is only for readability) [Author] |
| Tab | `\tab ` [Author] |
| Other control characters (below U+0020) | Dropped [ClassicMac] |
| U+0020–U+007E | Themselves |
| U+007F and above | `\uN?`, with *N* the code point as a **signed** 16-bit number, and `?` the fallback [Author] |

For example `é` (U+00E9) is `\u233?`, `“` (U+201C) `舠?`, and the Apple logo U+F8FF `\u-1793?` [Author].

### 11.4 Example

The golden `'TEXT'` 128 with `'styl'` 128 (§5, §6.4) gives:

```
{\rtf1\ansi\ansicpg1252\deff0\uc1
{\fonttbl{\f0 Times;}{\f1 Monaco;}{\f2 Geneva;}}
{\colortbl;\red0\green0\blue0;\red255\green0\blue0;\red0\green128\blue0;}
\plain\f0\fs36\cf1\b Title\par
\plain\f1\fs20\cf2\i Body \plain\f2\fs24\cf3\ul text, 舠?quoted舡?.}
```

---

## 12. Writing text resources

ClassicMac's editor (`TextResources`, `VersionResource`) writes these resources back [ClassicMac, following the formats
above]:

- **Text** is Mac OS Roman (section 2); a line break is written as a carriage return. Text that Mac OS Roman cannot hold
  is refused, not approximated.
- **`STR `:** one Pascal string, at most 255 bytes; nothing after it.
- **`STR#`:** the count (at most 65535), then the strings as Pascal strings.
- **`vers`:** the fixed part with the major version in BCD (0–99), minor and bug fix as nibbles (0–15), the stage byte,
  the non-release number in BCD (0–99, as Apple's own files store it) and the region code, then the short and long
  version strings as Pascal strings.
- **`TEXT` with a `styl` of its ID:** the style runs follow the edit. Text before and after the change keeps its runs
  (their starts moved by the change's length); text inserted takes the style of the run the change starts in, as
  typing does in TextEdit; a run whose text is all deleted is dropped, and two runs at one position keep the later.
  The runs' other fields (height, ascent, font, face, size, colour) are unchanged. A `TEXT` without a `styl` gets none.

## 13. Diagnostics

All are warnings: the output is still written from what could be read, except where noted [ClassicMac]. On the Mac,
`GetIndString` returns an empty string for an index past a list's count (§4), and TextEdit applies any `'styl'`
without complaint (§6.4); what the Toolbox does with the other damaged cases below has not been traced.

| Code | Severity | Emitted by | Meaning |
| --- | --- | --- | --- |
| `text.string-short` | Warning | `'STR '` | The length byte says more than the resource holds (or the resource is empty); the text is cut |
| `text.string-list-short` | Warning | `'STR#'` | The data ends before the counted strings; the strings read so far (the last one possibly cut) are kept |
| `text.styl-short` | Warning | `'TEXT'` (its `'styl'`), `'styl'` | The data ends inside a style element, or is 1 byte (no count; 0 bytes too for a `'styl'` decoded alone); the complete elements are used |
| `text.vers-short` | Warning | `'vers'` | Under 7 bytes: **no output**. Or a version string runs past the data: it is cut |

Cases with no diagnostic [ClassicMac]: a `'STR#'` under 2 bytes (no output); bytes after the last string; an unknown
`'vers'` stage; a missing long version string; `'styl'` elements out of order, with negative starts or starts past the
text; an empty `'styl'` found for a `'TEXT'` (one default run).

The decoders' names and versions, recorded in the manifest: `text.string`, `text.string-list`, `text.text`,
`text.style`, `text.version`, each version 1 [ClassicMac].

---

## 14. Not covered yet

- **Other Mac encodings** (§2.4).
- **Writing** text resources: planned with the editors.
