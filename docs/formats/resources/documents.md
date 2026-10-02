# Documents — an implementer's specification

This document describes two kinds of classic Mac OS document that are styled TextEdit text with pictures anchored in
it: **DOCMaker** stand-alone documents (chapters, pictures with click actions, a table of contents) and **SimpleText**
documents with pictures. It describes them completely enough to write a reader without reading ClassicMac's code, and
it specifies the HTML that ClassicMac converts them to.

The text, its style runs and the SimpleText document's own rules are in [styled-text.md](styled-text.md) ([styled-text.md §4](styled-text.md#4-style-runs-styl) style runs,
[styled-text.md §5](styled-text.md#5-simpletext-documents) SimpleText); this document adds the pictures, DOCMaker's resources and the conversion.

References:

- DOCMaker 4.8.4's stand-alone reader (Green Mountain Software), disassembly: every DOCMaker rule below. DOCMaker's
  resources have no published description.
- SimpleText 1.4 (Mac OS 9.0), disassembly: SimpleText's pictures ([styled-text.md §5](styled-text.md#5-simpletext-documents)).
- *Inside Macintosh: Text* (1993): TextEdit, `TEUseStyleScrap`, `TEGetPoint`.
- *Inside Macintosh: Imaging With QuickDraw* (1994): pictures (`'PICT'`) and colour tables (`'clut'`).

Contents

1. [Conventions](#1-conventions)
2. [DOCMaker documents](#2-docmaker-documents)
3. [SimpleText documents](#3-simpletext-documents)
4. [Picture placement on the Mac](#4-picture-placement-on-the-mac)
5. [The document model](#5-the-document-model)
6. [Where documents are converted](#6-where-documents-are-converted)
7. [Diagnostics](#7-diagnostics)
8. [Not covered yet](#8-not-covered-yet)

---

## 1. Conventions

The shared conventions of [README.md](../README.md) hold. Text is a byte string in a single-byte Mac encoding (Mac OS
Roman unless chosen otherwise), so a character offset is a byte offset. **$CA** is the option-space (U+00A0 in
Unicode), the picture anchor of both formats.

Tags are those of [README.md](../README.md). **[ClassicMac]** marks ClassicMac's own choices: the model, the HTML output,
and how damaged input is presented.

---

## 2. DOCMaker documents

A DOCMaker stand-alone document is an application (type `'APPL'`, creator `'Dk@P'`): a copy of the reader's code with
the document in its resource fork. Each document carries its own reader, which never checks a version [Code]. The
rules below are DOCMaker 4.8.4's reader [Code: DOCMaker 4.8.4]; ClassicMac recognises the document by its resources
(a `'Wndo'` and `'TEXT'` 128), not by type and creator [ClassicMac].

### 2.1 Chapters

- **Count:** the number of `'Wndo'` resources (`Count1Resources`); below 1, the reader quits.
- Chapter *k* (from 1) is **`'TEXT'`, `'styl'` and `'Wndo'` 127 + *k***, in the order of *k*. There is no index
  resource.
- **Title:** `'STR '` 2000 + *k*. When it is missing: "Chapter *k*", the word being item 16 of `'STR#'` 128.
  Titles appear in the Contents menu (one item per chapter, ⌘1–⌘9 on chapters 1–9), the chapter selector's popup and
  the table-of-contents window.
- **Text:** `TEStyleInsert` of the `'TEXT'`, then `TEUseStyleScrap` over all of it with the `'styl'`
  ([styled-text.md §4.4](styled-text.md#44-how-runs-map-onto-the-text)). No tab stops and no TextEdit hooks. Special bytes: **$CA** anchors a picture (§2.3),
  **$00** is a page break when printing, **CR** ends a paragraph (and counts paragraphs for action 1).

### 2.2 `'Wndo'`: the chapter's window (20 bytes)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `i16` | Top margin: stored, unused on screen |
| +$02 | 2 | `i16` | Left margin: the text's left edge is the window's left + this |
| +$04 | 2 | `i16` | Bottom margin: the view's bottom is the window's bottom − 15 − this |
| +$06 | 2 | `i16` | Right margin: the text's right edge is the window's right − 15 (the scroll bar) − this |
| +$08 | 2 | `i16` | Print margin, top |
| +$0A | 2 | `i16` | Print margin, left |
| +$0C | 2 | `i16` | Print margin, bottom (66 pixels more when the footer is on) |
| +$0E | 2 | `i16` | Print margin, right |
| +$10 | 1 | `u8` | 1: no footer on this chapter |
| +$11 | 1 | `u8` | Unused |
| +$12 | 2 | `i16` | Justification of the whole chapter (`TESetJust`): 0 left, 1 centre, −1 right |

**Background colour:** entry *k* − 1 of `'clut'` 128 (`RGBBackColor`), or entry 0 when there is no entry *k* − 1. The
Divinity manual's `'clut'` 128 stores the number of entries (24) where a `ColorTable` stores it less one, so a
colour table reader finds one entry short (`color.short`, [palettes.md](palettes.md)); the chapters' entries are all
there.

**Window width:** from `'sTwD'` 128. Word 0 is a mode: 1, the screen's full height and the width in word 1; 2, the
position in word 1, the height in word 2 and the **width in word 3**, clamped to the screen. When bytes 8 and 9 are both
set, the window has a zoom box. The **text column** is the window's width − 15 − the left margin − the right margin
(425 pixels for a 512-pixel window with margins 36 and 36). ClassicMac takes 480 pixels when `'sTwD'` 128 is missing
[ClassicMac].

### 2.3 `'pInf'`: pictures

Chapter *k*'s pictures are **`'pInf'` 100*k* + 100 + *j*** for *j* = 1, 2, … up to the first one missing, at most 60.
The *j*-th **$CA** byte in the chapter's text (only single-byte characters are checked, with `CharByte`) anchors the
*j*-th `'pInf'`. Any $CA past the last `'pInf'` is a plain space.

| Field | Type | Meaning |
| --- | --- | --- |
| PICT | `i16` | The `'PICT'` resource ID |
| Alignment | `i16` | 1 centre, 2 left, 3 right, within the text column |
| No-scale | `i16` | 1: never scaled. Otherwise a picture wider than the column is scaled down to it, proportionally |
| Action | `i16` | 0 none; above 0 clickable, the picture inverted while the mouse is held on it; below 0 an invisible button (no feedback). The action is the absolute value |
| Action data | varies | By action, below |
| Print | `i16` | 1: the picture is printed |

| Action | Meaning | Data |
| --- | --- | --- |
| 1 | Go to a chapter, then scroll to a paragraph (from 0) when it is 0 or more | `i16` chapter, `i16` paragraph |
| 2 | About box | — |
| 3 | Print | — |
| 4 | Quit | — |
| 5 | Open a file (full path) | Pascal string |
| 6 | (Code the stand-alone reader leaves out) | `i16`, `i16` |
| 7 | Play a QuickTime movie in the picture's rectangle | Pascal string |
| 8 | Show a note | Pascal string |
| 9 | (Code the stand-alone reader leaves out) | — |
| 10 | Back (a history of 100) | — |
| 11 | Table of contents | — |
| 12 | Find | — |
| 13 | Send an Apple event | 3 `OSType`, Pascal string |
| 14 | Next chapter | — |
| 15 | Previous chapter | — |
| 16 | Run a script | Pascal string |

There are no web links, and no links in the text: only pictures are clickable. A link to a chapter the document does
not have does nothing (the Divinity manual's "click parchment" button links to chapter 35 of 24).

### 2.4 Other resources

| Resource | Meaning |
| --- | --- |
| `'cnt#'` 128 | Table-of-contents entries under the chapter titles: `i16` count, then per entry `i16` chapter, `i16` selection start, `i16` selection end, Pascal string title. Clicking one opens the chapter and selects, scrolls to and flashes the selection |
| `'cntp'` 128 | The table of contents' font, size and bullet character |
| `'xtr2'` 128 | One byte of feature flags, bit 0 the most significant: 0 footer, 1 custom About box, 2 table of contents, 3 Find, 4 Page Setup and Print, 5 Output Text, 6 Transfer menu item, 7 selection and Copy allowed |
| `'foot'` 128 | The printed footer: six `i16` slots (3 time, 4 long date, 5 "Page *n*", 6 document name, 7 chapter title, 8 the text of `'STR '` 1000), byte +$0C 1 when page numbers restart each chapter, a font and size (Geneva 9 by default) |
| `'conp'` 5000–5011, 5051–5056 | Pictures for the chapter bar and its buttons |
| `'rQDF'` 128 | The fonts the document uses (names and sizes), for a missing-font warning |
| `'DLOG'`/`'DLGX'`/`'dctb'`/`'ictb'` 3000, `'STR '` 2999/3000 | The custom About box |
| `'STR#'` 128, 129 | The reader's interface and menu strings |

ClassicMac reads `'cnt#'` into the model; it does not read the others [ClassicMac].

---

## 3. SimpleText documents

A `'TEXT'` or `'ttro'` file: text in the data fork, styles in `'styl'` 128, and, when the resource fork has any
`'PICT'`, the *k*-th $CA (from 0) showing `'PICT'` 1000 + *k*, centred on the view. [styled-text.md §5](styled-text.md#5-simpletext-documents) has the
rules [Code: SimpleText 1.4]. ClassicMac reads a file as a SimpleText document when it is of type `'TEXT'` or `'ttro'`
and has a `'styl'` 128 or a `'PICT'` numbered 1000 or more [ClassicMac].

---

## 4. Picture placement on the Mac

Both readers draw a picture **over the text**, after `TEUpdate`, with no wrap [Code: DOCMaker 4.8.4; SimpleText 1.4]:

- **Top:** the top of the anchor's line (DOCMaker: `TEGetPoint` of the anchor less the line's height; SimpleText: less
  the **first** line's height).
- **Left:** by the alignment within the text column (DOCMaker) or centred on the view (SimpleText).
- **Size:** the picture's frame. DOCMaker scales a picture wider than the column down to it unless no-scale is set;
  SimpleText scales an extended version 2 frame to 72 dpi.

Authors leave blank lines below an anchor to make room, and put several anchors on one line for pictures side by side
[Verified: DOCMaker 4.8.4 in SheepShaver, Mac OS 9.0, the Divinity manual's chapters 1 and 10].

---

## 5. The document model

ClassicMac reads both kinds into one model, `StyledDocument` in `ClassicMac.Resources.Decoders.Documents`
[ClassicMac]: its kind, title (the file's name), chapters, and the contents entries. A chapter has its number, title,
styled text ([styled-text.md §4.4](styled-text.md#44-how-runs-map-onto-the-text), $CA read as U+00A0), justification, background colour, text column width (0 for
SimpleText: no fixed width) and pictures. A picture has its anchor (a character offset), `'PICT'` ID and data (null
when missing), frame width and height, alignment, no-scale flag and action (code, whether it highlights, and its
chapter, paragraph or text). SimpleText's pictures are centred, not scaled, with no action.

---

## 6. Where documents are converted

The converter is `document.html`, version 1 (`ResourceDecoders.CreateDocumentConverters`, an `IDocumentConverter`)
[ClassicMac]. It reads the data fork only for a `'TEXT'` or `'ttro'` file, up to the `--max-resource-size` limit.

- **`extract`** writes a document's folder as `document/` in the file's export folder, and records it in the
  manifest's `document` field (format 1.2, [export-manifest.md §1.10](../output/export-manifest.md#110-document)). `--no-documents`, `--raw`
  and `-t` leave it out.
- **`convert`** writes only the documents:

  ```
  classicmac convert <input> [-o <dir>] [--overwrite] [--screen-depth <n>]
  ```

  The input is opened and unwrapped as by `extract`, and every file with a resource fork is offered to the converter.
  One document is written straight into the output folder (default `<input name without extension> documents` next
  to the input); several get a folder each, placed as `extract` places forks ([export-manifest.md §3.2](../output/export-manifest.md#32-several-forks)). An existing non-empty output folder is refused unless `--overwrite` is given. It prints one line per document
  (`<Mac path>: <entry page>`), then `<n> documents, to <folder>`, or `No documents in <input>.`; diagnostics and exit
  codes are those of `extract`.
- **The viewer** previews a DOCMaker document, or a SimpleText document with pictures, a chapter at a time with a
  chapter menu, laid out by the same flow as the HTML (`DocumentFlow`), at 72 dpi (a point is a pixel) and on the
  chapter's background. A picture whose action goes to a chapter (1), the next or previous one (14, 15) or back (10)
  does so when clicked; the others show what they would do as a tooltip. A SimpleText document without pictures is
  shown as styled text. *Convert Documents* writes the HTML, and *Export Resources* and *Extract All Resources*
  include `document/` ([export-manifest.md §5.5](../output/export-manifest.md#55-folders-chosen-by-the-cli-and-the-viewer)).

---

## 7. Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `document.missing-part` | Error | A DOCMaker chapter has no `'TEXT'` or `'Wndo'` (under 20 bytes counts as none); the chapter is left out |
| `document.bad-picture` | Error | A `'pInf'` under 8 bytes; the picture is left out |
| `document.unanchored-picture` | Warning | A `'pInf'` with no $CA left to anchor it; left out, as the reader cannot place it |
| `document.unreadable-text` | Warning | A SimpleText document's data fork cannot be read (over the size limit, or a read error); no document |
| `document.missing-picture` | Warning | A `'pInf'` names a `'PICT'` the document does not have; the HTML shows an empty box |
| `document.undrawable-picture` | Warning | A picture cannot be drawn (damaged, unsupported or over the pixel limit); the HTML shows an empty box |
| `document.bad-link` | Info | Action 1 goes to a chapter the document does not have; the reader ignores the click, and the HTML has no link |
| `document.unknown-action` | Info | An action DOCMaker 4.8 does not have; kept in the model, no link |

---

## 8. Not covered yet

- DOCMaker's footer, custom About box, `'cntp'` font and feature flags (§2.4): read by the reader, not by ClassicMac.
- DOCMaker versions other than 4.8.4: nothing shows what 5.x and 6.x changed.
- SimpleText's printing page breaks (`'form'` resources) and voice annotations.
- Multi-byte encodings: the anchors are byte offsets, which the model uses as character offsets.
- The viewer's preview has no table of contents page and scrolls to a chapter's top, not to action 1's paragraph.
