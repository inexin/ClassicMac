# HTML output

`HtmlDocuments.Write` converts a document to a folder [ClassicMac]:

| File | Content |
| --- | --- |
| `index.html` | DOCMaker: the title and a list of the chapters, each with its `'cnt#'` entries. SimpleText: the text |
| `chapter-NN.html` | DOCMaker: one page per chapter, `NN` the chapter number with at least two digits |
| `style.css` | The layout and one class per distinct text style (`s0`, `s1`, …) |
| `images/pict-ID.png` | Each picture, drawn once per `'PICT'` ID (`pict-m5` for ID −5), through the chosen image encoder at the chosen screen depth |

All text files are UTF-8 with LF line ends, and the same document always gives the same bytes.

Contents

1. [Text](#1-text)
2. [Pictures: reflowed](#2-pictures-reflowed)
3. [Links](#3-links)

---

## 1. Text

- The chapter is a `<main class="column">` of its column width in pixels, justified as the chapter says, on a
  `<body>` of its background colour.
- Each line (up to a CR) is a `<p>`, classed with the style of its first character; a run in another style is a
  `<span>`. An empty line (or one of control characters only, which are dropped) is `<p><br></p>`, keeping its height. Spaces and tabs are kept (`white-space: pre-wrap`).
- A style is: the font (its Mac name, then a similar font found on other systems: Palatino → "Palatino Linotype",
  "Book Antiqua", serif; Geneva → Verdana, sans-serif; Chicago → system-ui; Monaco → Consolas, monospace; …), the size
  in pixels (72 dpi: a point is a CSS pixel), the line height from the style run when it has one, bold, italic,
  underline, outline (a 1-pixel stroke, hollow), shadow (hollow with a 1-pixel offset shadow), condense and extend
  (letter spacing −1 and +1 pixel) and colour.

---

## 2. Pictures: reflowed

The Mac's placement ([documents.md §4](../resources/documents.md#4-picture-placement-on-the-mac)) depends on its bitmap fonts' line breaks, which a browser does not reproduce. Instead of
drawing over the text, ClassicMac **puts each picture in the text flow at its anchor**:

- **Rows.** Anchors on one line, with only spaces between them, make one row. In a row, each picture goes to its
  alignment: left pictures at the column's left edge, centred ones in the middle, right ones at the right edge (a
  three-column grid).
- **Before the row:** the text on the anchor's line before the first anchor stays above the row, without its trailing
  spaces; when that is only spaces, it is dropped.
- **After the row:** the rest of the anchor's line, when it is only spaces, and the blank lines after it (the room the
  author left for the picture) are dropped, so the text continues right under the picture. Text after the anchors on
  their own line continues without its leading spaces. Dropping stops at the next anchor.
- **Size:** the frame's size, scaled to the column width (proportionally) when it is wider, unless no-scale is set.
- **Missing or undrawable pictures** are an empty dashed box of the frame's size.
- A $CA with no picture stays in the text as a no-break space.

---

## 3. Links

| Action | HTML |
| --- | --- |
| 1 | A link to the chapter's page, and `#pN` for paragraph *N* when it is 0 or more; no link when the chapter does not exist |
| 3 | `javascript:print()` |
| 10 | `javascript:history.back()` |
| 11 | `index.html` |
| 14, 15 | The next or previous chapter's page; no link at the last or first chapter |
| 8 | No link; the note as the picture's tooltip |
| 5, 7, 13, 16 | No link; a tooltip saying what it would do |
| others | Nothing |

A paragraph that a link or a contents entry goes to gets an empty `<a id="pN">` before it (placed at the next text
when the paragraph was dropped with a picture's whitespace). Each chapter page has links to the previous chapter, the
contents and the next chapter at its top and bottom.
