# HTML output

The folder of HTML pages ClassicMac writes for a document: a DOCMaker stand-alone document or a SimpleText document
with pictures ([documents.md](../resources/documents.md)), or a Word document ([word-mac.md](../documents/word-mac.md)),
as UTF-8 pages, one style sheet and the pictures as image files. It is ClassicMac's own format: no Mac software reads or writes it. The pictures are reflowed into the text,
where the Mac draws them over it. The converter is `document.html`, run by `extract`, `convert` and the viewer.

| | |
| --- | --- |
| Identified by | A folder holding `index.html` and `style.css` (`document/` in an export, whose manifest names the converter `document.html`, [export-manifest.md §1.10](export-manifest.md#110-document)) |
| ClassicMac | Writes; `ClassicMac.Resources.Decoders.Documents` (`HtmlDocuments`, `DocumentFlow`, `HtmlDocumentConverter`) |
| Verified against | Nothing yet (a format of ClassicMac's own) |
| Sources | ClassicMac's design; the document model of [documents.md](../resources/documents.md) |

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

### 1.1 Files

| File | Content |
| --- | --- |
| `index.html` | DOCMaker: the title and a list of the chapters, each with its `'cnt#'` entries. SimpleText and Word: the text |
| `chapter-NN.html` | DOCMaker: one page per chapter, `NN` the chapter number with at least two digits |
| `style.css` | The layout and one class per distinct text style (`s0`, `s1`, …) |
| `images/pict-ID.png` | Each picture, drawn once per `'PICT'` ID (`pict-m5` for ID −5) |

The files are listed pages first (`index.html`, the chapters in order), then `style.css`, then the pictures by path.
All text files are UTF-8 with LF line ends, and the same document always gives the same bytes. [ClassicMac]

### 1.2 Pages

- Each page starts `<!DOCTYPE html>`, with a `utf-8` charset, a `width=device-width` viewport, a `generator` of
  `ClassicMac`, a `<title>` and a link to `style.css`.
- A chapter page is titled `<document title>: <chapter title>` (DOCMaker) or the document's title (SimpleText). Its
  `<body>` has the chapter's background colour. Its text is a `<main class="column">` of the chapter's column width in
  pixels (when it has one), aligned left, centred or right as the chapter's justification says.
- The contents page is a `<main class="contents">` with the title as `<h1>` and a list of links to the chapters, each
  with a nested list of its `'cnt#'` entries, linking to their paragraphs (§3.3).
- Each DOCMaker chapter page has a `<nav>` at its top and bottom: the previous chapter (`rel="prev"`), the contents
  (`index.html`) and the next chapter (`rel="next"`); an empty `<span>` stands in at the first and last chapter.

[ClassicMac]

### 1.3 Text in HTML

`&`, `<`, `>` and `"` are written as entities; CR and LF as LF; other control characters except tab are dropped.
Colours are `#rrggbb` from the 8-bit colour. [ClassicMac]

## 2. Reading

None. ClassicMac does not read its HTML back.

## 3. Writing

### 3.1 Text

1. Each line (up to a CR) is a `<p>`, classed with the style of its first character; a run in another style is a
   `<span>` of its class.
2. An empty line, or one of control characters only (which are dropped), is `<p><br></p>`, keeping its height.
3. A Word document's paragraph ([word-mac.md](../documents/word-mac.md)) gives each of its lines an inline style: its
   alignment when it differs from the column's (`justify` for justified), `margin-left` and `margin-right` from its
   indents; its first line also `text-indent` (the first-line indent) and `margin-top` (the space before); its last
   line `margin-bottom` (the space after). Zero values are left out; a point is a pixel.
4. Spaces and tabs are kept (`white-space: pre-wrap`).
5. A style is a CSS declaration, one class per distinct declaration:
   - the font: its Mac name, then a similar font found on other systems (§3.4);
   - the size in pixels (72 dpi: a point is a CSS pixel);
   - the line height from the style run, when it has one;
   - bold, italic, underline;
   - outline: a 1-pixel stroke, hollow; shadow: hollow with a 1-pixel offset shadow;
   - condense and extend: letter spacing −1 and +1 pixel;
   - the colour, when it is not black.

[ClassicMac]

### 3.2 Pictures: reflowed

The Mac's placement ([documents.md §2.3](../resources/documents.md#23-picture-placement)) depends on its
bitmap fonts' line breaks, which a browser does not reproduce. Instead of drawing over the text, each picture goes in
the text flow at its anchor [ClassicMac]:

1. **Rows.** Anchors on one line, with only spaces between them (space, tab or no-break space), make one row. In a
   row, each picture goes to its alignment: left pictures at the column's left edge, centred ones in the middle, right
   ones at the right edge (a three-column grid).
2. **Before the row.** The text on the anchor's line before the first anchor stays above the row, without its
   trailing spaces; when that is only spaces, it is dropped.
3. **After the row.** The rest of the anchor's line, when it is only spaces, and the blank lines after it (the room
   the author left for the picture) are dropped, so the text continues right under the picture. Text after the
   anchors on their own line continues without its leading spaces. Dropping stops at the next anchor.
4. **Size.** The frame's size, scaled proportionally to the column width when it is wider, unless the picture's
   no-scale flag is set.
5. **Missing or undrawable pictures** are an empty dashed box of the frame's size, titled `PICT <id> (not drawn)`.
6. A `$CA` with no picture stays in the text as a no-break space.

### 3.3 Links

A picture with an action is wrapped in a link, or gets a tooltip [ClassicMac]:

| Action | HTML |
| --- | --- |
| 1 | A link to the chapter's page, and `#pN` for paragraph *N* when it is 0 or more; no link when the chapter does not exist |
| 3 | `javascript:print()` |
| 10 | `javascript:history.back()` |
| 11 | `index.html` |
| 14, 15 | The next or previous chapter's page; no link at the last or first chapter |
| 8 | No link; the note as the picture's tooltip |
| 5 | No link; the tooltip "Opens …" with the action's text |
| 7 | No link; the tooltip "Plays the movie …" |
| 13, 16 | No link; the tooltip "Runs a script" |
| Others | Nothing |

A paragraph that a link or a contents entry goes to gets an empty `<a id="pN">` before it, placed at the next text
when the paragraph was dropped with a picture's whitespace. Paragraphs are counted from 0 by CR.

### 3.4 Fonts

| Mac font | CSS `font-family` |
| --- | --- |
| Chicago, Charcoal | `Chicago,Charcoal,system-ui,sans-serif` |
| Geneva | `Geneva,Verdana,sans-serif` |
| New York | `"New York","Times New Roman",serif` |
| Monaco | `Monaco,Consolas,monospace` |
| Times | `Times,"Times New Roman",serif` |
| Helvetica | `Helvetica,Arial,sans-serif` |
| Helvetica Narrow | `"Helvetica Narrow","Arial Narrow",sans-serif` |
| Courier | `Courier,"Courier New",monospace` |
| Palatino | `Palatino,"Palatino Linotype","Book Antiqua",serif` |
| Bookman | `Bookman,"Bookman Old Style",serif` |
| Avant Garde | `"Avant Garde","Century Gothic",sans-serif` |
| New Century Schoolbook | `"New Century Schoolbook","Century Schoolbook",serif` |
| Zapf Chancery | `"Zapf Chancery","Monotype Corsiva",cursive` |
| Any other | `"<name>",Geneva,Verdana,sans-serif` (quotes removed from the name) |

[ClassicMac]

## 4. Variants

- A DOCMaker document gives `index.html` (the contents) and a page per chapter; a SimpleText document gives one page,
  `index.html`, with its text and no navigation. [ClassicMac]

## 5. ClassicMac

- The converter `document.html` is version 1, raised when its output changes. It reads the data fork only for a
  `TEXT` or `ttro` file (a SimpleText document's text). [ClassicMac]
- Pictures are drawn through `DecodeOptions.ImageEncoder` (PNG by default; the extension follows the encoder) at
  `DecodeOptions.ScreenDepth`; a frame over `DecodeOptions.MaxImagePixels` is not drawn
  ([export-manifest.md §5.2](export-manifest.md#52-decodeoptions)). [ClassicMac]

## 6. Diagnostics

The codes the HTML converter raises. The reader's codes (`document.*`) are in
[documents.md](../resources/documents.md); a converter that throws is `export.converter-failed`
([export-manifest.md §6](export-manifest.md#6-diagnostics)).

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `document.undrawable-picture` | Warning | A picture cannot be drawn: damaged, unsupported or its frame over the pixel limit | Writes an empty dashed box | Not applicable |
| `image.decoder-fault` | Error | The decoder failed on the data with an index or arithmetic exception: a ClassicMac bug (found by mutation testing), not damaged data | Writes an empty dashed box | Not applicable |
| `document.unreadable-text` | Warning | A SimpleText document's data fork cannot be read (over the size limit, or a read error) | Writes no document | Not applicable |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/DocumentTests.cs`: `DOCMaker_documents_convert_to_their_golden_HTML`
  (two pictures sharing a row with the blank line after them dropped; a wide picture scaled to the 260-pixel column; a
  link to a missing chapter left out) and `SimpleText_documents_convert_to_their_golden_HTML`, compared with
  `Golden/Documents/DocMaker` and `Golden/Documents/SimpleText` (pages and `style.css` as files, pictures as SHA-256
  in `images.txt`; `CLASSICMAC_UPDATE_GOLDEN=1` rewrites them).
- `tests/ClassicMac.Cli.Tests/ConvertTests.cs`: `convert` writes every document on a disk in a folder each,
  one document straight into the output folder; `extract` adds the document beside the resources.

## 8. Not covered

- The Mac's own placement of pictures over the text (§3.2).
- Bitmap fonts: the pages name fonts and leave the drawing to the browser.

## 9. References

1. [documents.md](../resources/documents.md): the documents and the model the converter writes.
