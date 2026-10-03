# Microsoft Word 4 and 5 for the Macintosh

Microsoft Word 4.0 (1989) and 5.0/5.1 (1991–1992) for the Macintosh saved documents of type `'WDBN'`, creator
`'MSWD'`, in a format of their own, not the Word for Windows formats: big-endian, with a header that maps the text, the
formatting tables and the style sheet, everything in the data fork. Microsoft never published it. ClassicMac reads the
text with its character and paragraph formatting into a styled document, which the viewer shows and `convert` and
`extract` write as HTML ([html.md](../output/html.md)). A document saved with Fast Save is reported, not read.

| | |
| --- | --- |
| Identified by | Type `'WDBN'`; the data fork starts `$FE37` and the version word `$001C` (Word 4) or `$0023` (Word 5) |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Documents` (`MacWordDocuments`) |
| Verified against | One Word 5 document from a CD-ROM (§7), fast saved; nothing made by Word 4 |
| Sources | Fitted to that document; libmwaw for names and for what the document does not show (behaviour only) |

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

The data fork is a header, the text, then formatting pages and tables. All values are big-endian. A file position is
an *FC*; FCs of the text run from the header's text start. Pages are 512 bytes, page *n* at FC *n* × 512.

### 1.1 Header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Signature | `$FE37` for Word 4 and 5 [Fitted]; `$FE34` Word 3, `$FE32` Word 1 [Reference: libmwaw] |
| +$02 | 2 | Version | `$0023` Word 5 [Fitted]; `$001C` Word 4 [Reference: libmwaw] |
| +$04 | 6 | Reserved | Zero [Fitted] |
| +$0A | 1 | Flags | `$04` set in every Word 5.1a document, fast saved or not; `$08` when the document holds a picture [Verified: Word 5.1a and 4.0 documents, §7]; bits 4–7 the number of fast saves since the last full save [Fitted] |
| +$0B | 9 | Reserved | [Fitted] |
| +$14 | 4 | Text start | FC of the first character, `$100` [Fitted] |
| +$18 | 4 | Text end | FC after the last character of all the text (main text, then footnotes and headers) [Fitted] |
| +$1C | 4 | Data end | FC after the last byte used; the fork is rounded up to a page [Fitted] |
| +$20 | 4 | Reserved | [Fitted] |
| +$24 | 4 | Main text length | Characters of the main text [Fitted] |
| +$28 | 24 | Other text lengths | Zero in a document with no footnotes or headers [Fitted] |
| +$40 | 120 | Zones 0–19 | 6 bytes each (§1.2) [Fitted] |
| +$B8 | 2 | Character pages | How many pages the character bin table lists [Fitted] |
| +$BA | 2 | Paragraph pages | How many pages the paragraph bin table lists [Fitted] |
| +$BC | 6 each | Zones 20 and up | As zones 0–19, up to the text [Fitted] |

### 1.2 Zones

Each zone entry is a `u32` FC and a `u16` length in bytes; an empty zone has length 0 [Fitted]. The zones ClassicMac
uses, and the others' names [Reference: libmwaw]:

| Zone | Holds | Read |
| --- | --- | --- |
| 0 | The style sheet (§1.5) [Fitted] | Yes |
| 1 | The same FC and length as zone 0 in the document seen [Fitted] | No |
| 2, 3 | Footnote positions and footnotes | No |
| 4 | Sections | No |
| 5 | Page breaks | No |
| 6, 7 | Field names and positions | No |
| 8 | Headers and footers | No |
| 9 | The character bin table (§1.3) [Fitted] | Yes |
| 10 | The paragraph bin table (§1.3) [Fitted] | Yes |
| 12 | The font family IDs used, a `u16` each [Fitted] | No |
| 13 | The print record (`TPrint`, 120 bytes) [Fitted] | No |
| 18 | The piece table of a fast-saved document [Fitted] | Only its length |
| 21 | The font names (§1.7) [Fitted] | Yes |
| 24 | The document summary: Pascal strings (title, subject, author, version, keywords) [Fitted] | No |

### 1.3 Bin tables and FKP pages

A bin table maps FCs to the pages that format them: *n* + 1 `u32` FCs, then *n* `u16` page numbers; *n* is
(length − 4) / 6 and equals the header's page count [Fitted]. Each page is a formatted disk page (FKP) [Fitted]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 4 × (*crun* + 1) | FCs | Run *i* covers FC *i* up to FC *i* + 1 |
| +4 × (*crun* + 1) | *crun* | Offsets | Run *i*'s property block at twice this byte from the page start; 0 for none (the style's properties) |
| … | | Property blocks | Placed from the end of the page down, each at an even offset |
| +$1FF | 1 | *crun* | The number of runs |

In a character page a block is a length byte and that many bytes of CHP (§1.4). In a paragraph page it is a length
byte counting *words*, then that many words: the style number, 6 bytes of line-height data, then the sprms (§1.6),
padded with a zero byte to a whole word [Fitted]. Paragraph runs end at paragraph marks [Fitted].

### 1.4 Character properties (CHP)

A block applies on top of its paragraph style's CHP. Bytes past the block's length are the style's [Fitted].

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 1 | Flags | Toggled against the style: `$80` bold, `$40` italic [Fitted]; `$20` strike-through, `$10` outline, `$08` shadow, `$04` small caps, `$02` all caps, `$01` hidden [Reference: libmwaw] |
| +1 | 1 | Which fields | `$08` size and `$04` underline [Fitted]; `$10` font, `$02` raised or lowered, `$01` spacing, `$20` colour, `$40` start from the style's CHP [Reference: libmwaw] |
| +2 | 2 | Font | The font family ID (§1.7) [Fitted] |
| +4 | 1 | Size | In half points (`$14` is 10 point) [Fitted] |
| +5 | 1 | Raised or lowered | Signed [Reference: libmwaw] |
| +6 | 1 | Spacing | Signed [Reference: libmwaw] |
| +7 | 1 | Colour and underline | Bits 4–7 the colour; bits 1–3 the underline: 0 none, 1 single [Fitted], 2 word, 3 double, 4 dotted [Reference: libmwaw] |
| +8 | | More | Up to 2 more bytes in the document seen, repeating the size [Fitted]; not read |

### 1.5 Style sheet

| Part | Layout |
| --- | --- |
| Head | A `u16` (0 in the document seen) [Fitted] |
| Names | A `u16` length counting itself, then a Pascal string per style; a zero length is a standard style with no name [Fitted] |
| Characters | A `u16` length counting itself, then per style a length byte and a CHP block (§1.4), or `$FF` for none [Fitted] |
| Paragraphs | A `u16` length counting itself, then per style a length byte (in bytes) and a block: the style number, 6 bytes of line-height data, the sprms (§1.6); `$FF` for none [Fitted] |
| Links | A `u16` count, then per style its next style and the style it is based on, a byte each [Fitted] |

A paragraph's style number is its index in these lists [Fitted: the document seen numbers its three styles from 0].
A style with no character block has New York 12 [Reference: libmwaw].

### 1.6 Paragraph sprms

A sprm is a code byte and its argument. Lengths are in twips (1/20 point) [Fitted]:

| Sprm | Argument | Property |
| --- | --- | --- |
| `$02` | 1 | The style number [Reference: libmwaw] |
| `$05` | 1 | Alignment: 0 left, 1 centred, 2 right [Fitted], 3 justified [Reference: libmwaw] |
| `$07`, `$08`, `$09` | 1 | Keep lines together, keep with next, page break before [Reference: libmwaw] |
| `$0A`, `$0B` | 1 | Border style, border sides [Reference: libmwaw] |
| `$0F`, `$17` | 1 + *n* | Tabs: a length byte, then *n* bytes [Reference: libmwaw] |
| `$10` | 2 | Right indent, `i16` [Reference: libmwaw] |
| `$11` | 2 | Left indent, `i16` [Fitted] |
| `$13` | 2 | First-line indent from the left indent, `i16` (negative: hanging) [Reference: libmwaw] |
| `$14` | 2 | Line spacing [Reference: libmwaw] |
| `$15`, `$16` | 2 | Space before, space after [Reference: libmwaw] |
| `$18` | 1 | In a table: 1 [Fitted] |
| `$19` | 1 | The table row's end mark: 1 [Fitted] |
| `$1E`–`$22` | 2 | Borders: top, left, bottom, right, between [Reference: libmwaw] |
| `$94`, `$99` | 2 | Table properties [Fitted] |
| `$98` | 2 + *n* | The row's cell definitions: a `u16` length, then *n* bytes [Fitted] |
| `$00` | | Padding: ends the list [Fitted] |

### 1.7 Font names

A `u16` count, then per font a reserved `u16` (zero), the `i16` font family ID and a Pascal string with its name
[Fitted]. The family ID is the Mac's (0 Chicago, 2 New York, 3 Geneva, 20 Times), and a document's font names hold
on another Mac where the numbers differ [Fitted].

### 1.8 Characters

The text is Mac OS Roman [Fitted]. These bytes are not characters:

| Byte | Meaning |
| --- | --- |
| `$0D` | Paragraph end [Fitted] |
| `$09` | Tab [Fitted] |
| `$07` | End of a table cell; with sprm `$19`, the end of the row [Fitted] |
| `$0C` | Page or section break, ending its paragraph [Fitted] |
| `$0B` | Line break within a paragraph [Reference: libmwaw] |
| `$1E`, `$1F` | Non-breaking hyphen, optional hyphen [Reference: libmwaw] |
| `$01`, `$02`, other controls | A picture, a footnote reference and other special characters [Reference: libmwaw] |

## 2. Reading

1. Check the signature and version (§1.1); a data fork shorter than `$100` bytes is no Word document.
2. When zone 18 (the piece table) is not empty, the document was fast saved and the text is in pieces listed by the piece table: report it
   and stop (§5).
3. The main text is the main text length's characters from the text start; the text end and the data end must lie in
   the fork.
4. Read the font names (§1.7) and the style sheet (§1.5).
5. Read the character and paragraph runs from their bin tables' pages (§1.3).
6. For each character: its paragraph's style gives the base CHP and paragraph properties; the paragraph run's sprms
   apply on top (§1.6); the character run's CHP block applies to the style's CHP (§1.4).

[Fitted]

## 3. Writing

None.

## 4. Variants

| Version | Signature, version | Notes |
| --- | --- | --- |
| Word 1 | `$FE32` | A different header [Reference: libmwaw]; not read |
| Word 3 | `$FE34`, `$0000` | A `$30`-byte header, zones from `+$1E` [Reference: libmwaw]; not read |
| Word 4 | `$FE37`, `$001C` | As Word 5 [Reference: libmwaw] |
| Word 5 | `$FE37`, `$0023` | This document [Fitted] |

Word 6 for the Macintosh (`'W6BN'`) and Word 98 (`'W8BN'`) saved the Word for Windows formats instead.

## 5. ClassicMac

- One chapter, titled with the file's name; the text's paragraphs carry their alignment, indents and spacing in
  points. [ClassicMac]
- Paragraph ends, line breaks and page breaks become CR; a cell's end becomes a tab and a row's end a CR, so a table
  reads as tab-separated lines. [ClassicMac]
- Non-breaking and optional hyphens become U+2011 and U+00AD; pictures, footnote references and other control
  characters are left out. [ClassicMac]
- Hidden text is left out, as Word shows and prints it by default; all-caps text is upper-cased. [ClassicMac]
- The face shows bold, italic, underline (any kind), outline and shadow; strike-through, small caps, colour,
  raised and lowered text are not shown. [ClassicMac]
- A fast-saved document is not read: its piece table's layout is not known from any source, and guessing it could put
  the text together wrongly. The diagnostic says how to get a readable document. [ClassicMac]
- A zone outside the file, or a page past its end, is left out and reported once; the text still reads. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `word.bad-header` | Error | The text's start or end is not in the file | Reads nothing | Not traced |
| `word.bad-styles` | Warning | The style sheet ends early | Reads without styles | Not traced |
| `word.bad-zone` | Warning | A zone or a formatting page lies past the end of the file | Leaves it out | Not traced |
| `word.fast-saved` | Error | The document was fast saved (§2 step 2) | Reads nothing; says to save it again with Fast Save off | Word reads it |
| `word.not-shown` | Info | The text has pictures, footnote references or other special characters | Leaves them out | Word shows them |
| `word.unknown-sprm` | Warning | A paragraph sprm not in §1.6 | Stops reading that paragraph's sprms | Not traced |
| `word.unsupported-version` | Error | A Word for the Macintosh signature with a version other than Word 4 or 5 | Reads nothing | Not traced |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/MacWordTests.cs` builds documents byte by byte
  (`MacWordFixtures.cs`): character flags, sizes, fonts by name, underline, a style's toggles, hidden and all-caps
  text, paragraph alignment, indents and spacing, a style's alignment, line breaks and table rows, special
  characters, fast-saved documents (by the flag and by the piece table), other versions, a bad zone, an unknown sprm,
  and the HTML and converter output.
- `tests/ClassicMac.Resources.Decoders.Tests/WordCorpusTests.cs`, with `CLASSICMAC_WORD_CORPUS` set: a 132,608-byte
  Word 5 document from a game's CD-ROM, fast saved twice, reads as version 5 with a main text of 25,562 characters,
  39 fonts (Chicago, New York, Geneva and Times among them), 3 styles, 1,347 character runs (218 of them bold) and
  4,443 paragraph runs, and is reported as fast saved. Every [Fitted] rule above was read from this document; it is
  not in the repository.
- Nothing was checked against Word itself.

## 8. Not covered

- Fast-saved documents (the piece table, zone 18).
- Word 1 and Word 3 documents.
- Footnotes, headers and footers, sections, page layout, tabs, borders, line spacing and table column widths.
- Pictures (inline `$01` characters and their data).
- Styles based on other styles: a style's own blocks apply to the defaults, not to its parent's.
- Character colour, strike-through, small caps and raised or lowered text.
- The document summary (zone 24).

## 9. References

1. libmwaw, the `MsWrd` parser (MPL 2.0 or LGPL 2.1 or later): the zone names, the meaning of CHP and paragraph fields
   the document seen does not show, Word 1 and 3's signatures. Behaviour only; no code is taken from it.
