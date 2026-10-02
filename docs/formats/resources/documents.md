# Documents (DOCMaker, SimpleText)

Two kinds of classic Mac OS document are styled TextEdit text with pictures anchored in it. A DOCMaker stand-alone
document (Green Mountain Software) is an application carrying its own reader, with chapters, pictures with click
actions and a table of contents in its resource fork. A SimpleText document keeps its text in the data fork and may
show pictures at option-spaces. ClassicMac reads both into one document model, converts them to HTML
([html.md](../output/html.md)) and previews them. The text, its style runs and the SimpleText document's own rules
are in [styled-text.md](styled-text.md) (style runs [§1.3](styled-text.md#13-style-runs-styl), SimpleText
[§1.6](styled-text.md#16-simpletext-documents)); this document adds the pictures, DOCMaker's resources and the
conversion.

| | |
| --- | --- |
| Identified by | DOCMaker: type `'APPL'`, creator `'Dk@P'`, with `'Wndo'` and `'TEXT'` 128 resources. SimpleText: type `'TEXT'` or `'ttro'` with a `'styl'` 128 or a `'PICT'` numbered 1000 or more |
| ClassicMac | Reads and converts to HTML; `ClassicMac.Resources.Decoders.Documents` (`StyledDocuments`, `StyledDocument`, the `document.html` converter) |
| Verified against | DOCMaker 4.8.4 in SheepShaver, Mac OS 9.0: the Divinity manual |
| Sources | DOCMaker 4.8.4's stand-alone reader and SimpleText 1.4 (Mac OS 9.0), disassembly; *Inside Macintosh: Text*; *Inside Macintosh: Imaging With QuickDraw* |

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

Text is a byte string in a single-byte Mac encoding, so a character offset is a byte offset. $CA is the option-space
(U+00A0 in Unicode), the picture anchor of both formats. DOCMaker's resources have no published description; every
DOCMaker rule here is DOCMaker 4.8.4's stand-alone reader [Code: DOCMaker 4.8.4].

### 1.1 A DOCMaker document

A copy of the reader's code (type `'APPL'`, creator `'Dk@P'`) with the document in its resource fork. Each document
carries its own reader, which never checks a version [Code: DOCMaker 4.8.4].

| Resource | Contents |
| --- | --- |
| `'TEXT'` 127 + k | Chapter k's text (k from 1); special bytes in §2.1 |
| `'styl'` 127 + k | Its style runs ([styled-text.md §1.3](styled-text.md#13-style-runs-styl)) |
| `'Wndo'` 127 + k | Its window (§1.2) |
| `'STR '` 2000 + k | Its title |
| `'clut'` 128 | Background colours: entry k − 1 for chapter k |
| `'sTwD'` 128 | The window's size (§1.3) |
| `'pInf'` 100k + 100 + j | Chapter k's j-th picture (§1.4) |
| `'cnt#'` 128 | Table-of-contents entries (§1.6) |
| `'cntp'` 128, `'xtr2'` 128, `'foot'` 128, others | §1.6 |

There is no index resource.

### 1.2 `'Wndo'`: a chapter's window

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Top margin | `i16`; stored, unused on screen |
| +$02 | 2 | Left margin | `i16`; the text's left edge is the window's left + this |
| +$04 | 2 | Bottom margin | `i16`; the view's bottom is the window's bottom − 15 − this |
| +$06 | 2 | Right margin | `i16`; the text's right edge is the window's right − 15 (the scroll bar) − this |
| +$08 | 2 | Print margin, top | `i16` |
| +$0A | 2 | Print margin, left | `i16` |
| +$0C | 2 | Print margin, bottom | `i16`; 66 pixels more when the footer is on |
| +$0E | 2 | Print margin, right | `i16` |
| +$10 | 1 | No footer | 1: no footer on this chapter |
| +$11 | 1 | Unused | |
| +$12 | 2 | Justification | `i16`, of the whole chapter (`TESetJust`): 0 left, 1 centre, −1 right |

[Code: DOCMaker 4.8.4]

### 1.3 `'sTwD'` 128: the window's size

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Mode | 1: the screen's full height, the width in word 1. 2: the position in word 1, the height in word 2, the width in word 3, clamped to the screen |
| +$02 | 2 | Word 1 | By mode |
| +$04 | 2 | Word 2 | By mode |
| +$06 | 2 | Word 3 | By mode |
| +$08 | 2 | Zoom box | When bytes +$08 and +$09 are both set, the window has a zoom box |

[Code: DOCMaker 4.8.4]

### 1.4 `'pInf'`: a picture

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | PICT | `i16`: the `'PICT'` resource ID |
| +$02 | 2 | Alignment | `i16`: 1 centre, 2 left, 3 right, within the text column |
| +$04 | 2 | No-scale | `i16`: 1, never scaled. Otherwise a picture wider than the column is scaled down to it, proportionally |
| +$06 | 2 | Action | `i16`: 0 none; above 0 clickable, the picture inverted while the mouse is held on it; below 0 an invisible button (no feedback). The action is the absolute value (§1.5) |
| +$08 | varies | Action data | By action (§1.5) |
| … | 2 | Print | `i16`: 1, the picture is printed |

[Code: DOCMaker 4.8.4]

### 1.5 Actions

| Action | Meaning | Data |
| --- | --- | --- |
| 1 | Go to a chapter, then scroll to a paragraph (from 0) when it is 0 or more | `i16` chapter, `i16` paragraph |
| 2 | About box | — |
| 3 | Print | — |
| 4 | Quit | — |
| 5 | Open a file (full path) | Pascal string |
| 6 | Code the stand-alone reader leaves out | `i16`, `i16` |
| 7 | Play a QuickTime movie in the picture's rectangle | Pascal string |
| 8 | Show a note | Pascal string |
| 9 | Code the stand-alone reader leaves out | — |
| 10 | Back (a history of 100) | — |
| 11 | Table of contents | — |
| 12 | Find | — |
| 13 | Send an Apple event | 3 `OSType`, Pascal string |
| 14 | Next chapter | — |
| 15 | Previous chapter | — |
| 16 | Run a script | Pascal string |

[Code: DOCMaker 4.8.4] There are no web links, and no links in the text: only pictures are clickable.

### 1.6 Other DOCMaker resources

| Resource | Meaning |
| --- | --- |
| `'cnt#'` 128 | Table-of-contents entries under the chapter titles: `i16` count, then per entry `i16` chapter, `i16` selection start, `i16` selection end, Pascal string title. Clicking one opens the chapter and selects, scrolls to and flashes the selection |
| `'cntp'` 128 | The table of contents' font, size and bullet character |
| `'xtr2'` 128 | One byte of feature flags, bit 0 the most significant: 0 footer, 1 custom About box, 2 table of contents, 3 Find, 4 Page Setup and Print, 5 Output Text, 6 Transfer menu item, 7 selection and Copy allowed |
| `'foot'` 128 | The printed footer: six `i16` slots (3 time, 4 long date, 5 "Page n", 6 document name, 7 chapter title, 8 the text of `'STR '` 1000), byte +$0C 1 when page numbers restart each chapter, a font and size (Geneva 9 by default) |
| `'conp'` 5000–5011, 5051–5056 | Pictures for the chapter bar and its buttons |
| `'rQDF'` 128 | The fonts the document uses (names and sizes), for a missing-font warning |
| `'DLOG'`/`'DLGX'`/`'dctb'`/`'ictb'` 3000, `'STR '` 2999/3000 | The custom About box |
| `'STR#'` 128, 129 | The reader's interface and menu strings |

[Code: DOCMaker 4.8.4]

### 1.7 A SimpleText document

Text in the data fork, styles in `'styl'` 128, and, when the resource fork has any `'PICT'`, the k-th $CA (from 0)
showing `'PICT'` 1000 + k: [styled-text.md §1.6](styled-text.md#16-simpletext-documents) and
[§2.4](styled-text.md#24-simpletext-documents) have the rules [Code: SimpleText 1.4].

## 2. Reading

### 2.1 DOCMaker chapters

[Code: DOCMaker 4.8.4]

1. The number of chapters is the number of `'Wndo'` resources (`Count1Resources`); below 1, the reader quits.
2. Chapter k (from 1) is `'TEXT'`, `'styl'` and `'Wndo'` 127 + k, in the order of k.
3. Its title is `'STR '` 2000 + k. When that is missing: "Chapter k", the word being item 16 of `'STR#'` 128. Titles
   appear in the Contents menu (one item per chapter, ⌘1–⌘9 on chapters 1–9), the chapter selector's pop-up and the
   table-of-contents window.
4. The text: `TEStyleInsert` of the `'TEXT'`, then `TEUseStyleScrap` over all of it with the `'styl'`
   ([styled-text.md §2.3](styled-text.md#23-how-runs-map-onto-the-text)). No tab stops and no TextEdit hooks. $CA
   anchors a picture (§2.2), $00 is a page break when printing, CR ends a paragraph (and counts paragraphs for
   action 1).
5. The background colour is entry k − 1 of `'clut'` 128 (`RGBBackColor`), or entry 0 when there is no entry k − 1.
6. The text column is the window's width (§1.3) − 15 − the left margin − the right margin (425 pixels for a 512-pixel
   window with margins 36 and 36).

### 2.2 DOCMaker pictures

[Code: DOCMaker 4.8.4]

1. Chapter k's pictures are `'pInf'` 100k + 100 + j for j = 1, 2, … up to the first one missing, at most 60.
2. The j-th $CA byte in the chapter's text anchors the j-th `'pInf'`; only single-byte characters are checked, with
   `CharByte`. Any $CA past the last `'pInf'` is a plain space.
3. A click runs the picture's action (§1.5). A link to a chapter the document does not have does nothing.

### 2.3 Picture placement

Both readers draw a picture over the text, after `TEUpdate`, with no wrap [Code: DOCMaker 4.8.4] [Code: SimpleText
1.4]:

- Top: the top of the anchor's line. DOCMaker: `TEGetPoint` of the anchor less the line's height; SimpleText: less the
  first line's height.
- Left: by the alignment within the text column (DOCMaker), or centred on the view (SimpleText).
- Size: the picture's frame. DOCMaker scales a picture wider than the column down to it unless no-scale is set;
  SimpleText scales an extended version 2 frame to 72 dpi.

Authors leave blank lines below an anchor to make room, and put several anchors on one line for pictures side by side
[Verified: DOCMaker 4.8.4 in SheepShaver, Mac OS 9.0, the Divinity manual's chapters 1 and 10].

## 3. Writing

None.

## 4. Variants

- DOCMaker versions other than 4.8.4 were not traced; nothing shows what 5.x and 6.x changed.
- The Divinity manual's `'clut'` 128 stores the number of entries (24) where a `ColorTable` stores it less one, so a
  colour table reader finds one entry short ([palettes.md](palettes.md#2-reading)); the chapters' entries are all
  there. Its "click parchment" button links to chapter 35 of 24, which does nothing
  (§2.2).

## 5. ClassicMac

- Recognition: a DOCMaker document by its resources (a `'Wndo'` and `'TEXT'` 128), not by type and creator; a
  SimpleText document when the file is of type `'TEXT'` or `'ttro'` and has a `'styl'` 128 or a `'PICT'` numbered
  1000 or more. [ClassicMac]
- The window width is 480 pixels when `'sTwD'` 128 is missing. [ClassicMac]
- A chapter with no `'TEXT'` or `'Wndo'` (a `'Wndo'` under 20 bytes counts as none) is left out. A `'pInf'` under 8
  bytes, or one with no $CA left to anchor it, is left out. [ClassicMac]
- `'cnt#'` is read into the model; `'cntp'`, `'xtr2'`, `'foot'`, `'conp'`, `'rQDF'` and the About box are not.
  [ClassicMac]
- The model, `StyledDocument`: its kind, title (the file's name), chapters, and the contents entries. A chapter has its
  number, title, styled text ($CA read as U+00A0), justification, background colour, text column width (0 for
  SimpleText: no fixed width) and pictures. A picture has its anchor (a character offset), `'PICT'` ID and data (null
  when missing), frame width and height, alignment, no-scale flag and action (code, whether it highlights, and its
  chapter, paragraph or text). SimpleText's pictures are centred, not scaled, with no action. [ClassicMac]
- The converter is `document.html`, version 1 (`ResourceDecoders.CreateDocumentConverters`, an `IDocumentConverter`);
  its output is [html.md](../output/html.md). It reads the data fork only for a `'TEXT'` or `'ttro'` file, up to the
  `--max-resource-size` limit. [ClassicMac]
- `extract` writes a document's folder as `document/` in the file's export folder and records it in the manifest's
  `document` field (format 1.2, [export-manifest.md §6.10](../output/export-manifest.md#610-document)).
  `--no-documents`, `--raw` and `-t` leave it out. [ClassicMac]
- `convert` writes only the documents [ClassicMac]:

  ```
  classicmac convert <input> [-o <dir>] [--overwrite] [--screen-depth <n>]
  ```

  The input is opened and unwrapped as by `extract`, and every file with a resource fork is offered to the converter.
  One document is written straight into the output folder (default `<input name without extension> documents` next
  to the input); several get a folder each, placed as `extract` places forks
  ([export-manifest.md §3.2](../output/export-manifest.md#32-several-forks)). An existing non-empty output folder is
  refused unless `--overwrite` is given. It prints one line per document (`<Mac path>: <entry page>`), then
  `<n> documents, to <folder>`, or `No documents in <input>.`; diagnostics and exit codes are those of `extract`.
- The viewer previews a DOCMaker document, or a SimpleText document with pictures, a chapter at a time with a chapter
  menu, laid out by the same flow as the HTML (`DocumentFlow`), at 72 dpi (a point is a pixel) and on the chapter's
  background. A picture whose action goes to a chapter (1), the next or previous one (14, 15) or back (10) does so
  when clicked; the others show what they would do as a tooltip. A SimpleText document without pictures is shown as
  styled text. Convert Documents writes the HTML, and Export Resources and Extract All Resources include `document/`
  ([export-manifest.md §3.3](../output/export-manifest.md#33-folders-chosen-by-the-cli-and-the-viewer)). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `document.bad-link` | Info | Action 1 goes to a chapter the document does not have | Keeps the picture; the HTML has no link | Ignores the click |
| `document.bad-picture` | Error | A `'pInf'` is under 8 bytes | Leaves the picture out | Not traced |
| `document.missing-part` | Error | A DOCMaker chapter has no `'TEXT'` or `'Wndo'` (under 20 bytes counts as none) | Leaves the chapter out | Not traced |
| `document.missing-picture` | Warning | A `'pInf'` names a `'PICT'` the document does not have | The HTML shows an empty box | Not traced |
| `document.unanchored-picture` | Warning | A `'pInf'` has no $CA left to anchor it | Leaves the picture out | Does not show it (§2.2) |
| `document.undrawable-picture` | Warning | A picture cannot be drawn (damaged, unsupported or over the pixel limit) | The HTML shows an empty box | Not traced |
| `document.unknown-action` | Info | An action DOCMaker 4.8 does not have | Keeps it in the model, no link | Not traced |
| `document.unreadable-text` | Warning | A SimpleText document's data fork cannot be read (over the size limit, or a read error) | No document | Not traced |

## 7. Verification

- DOCMaker 4.8.4 in SheepShaver, Mac OS 9.0, showing the Divinity manual (not in the repository): picture placement
  (§2.3), its `'clut'` 128 and its link to a missing chapter (§4) [Verified].
- `tests/ClassicMac.Resources.Decoders.Tests/DocumentFixtures.cs`: small DOCMaker and SimpleText documents made in
  code, laid out as DOCMaker 4.8.4 and SimpleText 1.4 read them. `DocumentTests`:
  - `DOCMaker_documents_read_as_DOCMaker_4_8_reads_them`: titles (a missing `'STR '` gives "Chapter 2"), the column
    width (window width − scroll bar − margins), the background from `'clut'` entry k − 1, three `'pInf'` for four
    option-spaces (the last a plain space), an invisible button, the anchor, `document.bad-link` for a link to
    chapter 9;
  - `SimpleText_documents_show_PICT_1000_plus_k_at_the_k_th_option_space`: a missing `'PICT'` 1001 leaves a gap;
  - `Other_files_are_not_documents`: a `'TEXT'` with no `'styl'` and no pictures;
  - `DOCMaker_documents_convert_to_their_golden_HTML` and `SimpleText_documents_convert_to_their_golden_HTML`:
    compared with `Golden/Documents/`.
- `tests/ClassicMac.App.Tests/DocumentTests.cs`: the chapter-at-a-time preview with its pictures; SimpleText files
  previewed as documents only with pictures; Convert Documents and extract.
- `tests/ClassicMac.Resources.Cli.Tests/ConvertTests.cs`: `convert` on a disk with several documents, one document
  straight into the output folder, `extract` adding the document beside the resources.

## 8. Not covered

- DOCMaker's footer, custom About box, `'cntp'` font and feature flags (§1.6): read by the reader, not by ClassicMac.
- DOCMaker versions other than 4.8.4 (§4).
- SimpleText's printing page breaks (`'form'` resources) and voice annotations.
- Multi-byte encodings: the anchors are byte offsets, which the model uses as character offsets.
- The viewer's preview has no table of contents page and scrolls to a chapter's top, not to action 1's paragraph.

## 9. References

1. Green Mountain Software, DOCMaker 4.8.4: its stand-alone reader, traced in disassembly. Commercial; no published
   description of its resources.
2. Apple, SimpleText 1.4 (Mac OS 9.0), traced in disassembly.
3. Apple, *Inside Macintosh: Text* (1993): TextEdit, `TEUseStyleScrap`, `TEGetPoint`.
4. Apple, *Inside Macintosh: Imaging With QuickDraw* (1994): pictures (`'PICT'`) and colour tables (`'clut'`).
