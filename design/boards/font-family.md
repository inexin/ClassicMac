# Font family ('FOND') preview

Item: P6 (new). Today a `'FOND'` previews as raw JSON. Show it as a property view ([property-view.md](property-view.md)) with a sample drawn from the family's own strikes. Read only; there is no FOND form. Data comes from `ClassicMac.Graphics.Fonts.FontFamily` (docs/formats/resources/font-families.md). The example values below are illustrative.

## Header

- 48 px tile showing "Aa" drawn from the family's 24 pt strike (or the largest bitmap strike), else a generic font icon.
- Name: the family name (the resource name), e.g. `3 “Geneva”`; kind "Font family in System".
- Facts: Type `'FOND'` · Family ID `3` · Version `2` · Strikes `7 bitmap, 1 TrueType` · Fixed width `No`.
- Tab strip right side: segmented **Properties | JSON** (JSON is today's view).

## Sample (top, full width)

- Size chips, one per bitmap size in the association table (9 10 12 14 18 20 24), plus a **TrueType** chip when an `'sfnt'` entry exists. Chosen chip = CmSegmentOn. Default: 12, else the nearest size.
- A style select: Plain, Bold, Italic, Bold Italic, and any other styles that have their own strike. Styles without a strike are listed as "Bold (QuickDraw)", synthesised as the Mac would, with a muted note.
- The sample line drawn from the strike through the existing font renderer, black on white on the checkerboard frame, at integer zoom (Zoom applies). Default text: "The quick brown fox jumps over the lazy dog 0123456789"; an inline text field edits it (Mac OS Roman only; other characters show as the strike's missing-symbol glyph).
- Under the line, muted 11: "From 'NFNT' 393 · 12 pt plain · 1-bit", linking to that resource. TrueType: "From 'sfnt' 3", drawn at the chosen size if the outline renderer supports it, otherwise "No preview for outline fonts yet".
- No strike at all: "This family has no strikes in the open files" + the association rows still listed below.

## Association table (card)

A matrix, not a list: rows = point sizes ascending, columns = Plain, Bold, Italic, Bold Italic, then one column per other style combination present (e.g. "Bold Condensed"). Cells:

- A resource link in mono: `NFNT 393` (or `FONT 393`). Clicking selects that resource in the tree. A missing resource (not in any open file) shows the ID muted with a warning icon and tooltip "Not found in the open files".
- A depth badge when bits 8–10 of the style word are set: `4-bit`, `8-bit` (CmSegmentTrack chip, 10 px).
- Empty cell: "—" muted.
- The `size 0` row is labelled **TrueType** and spans all style columns it covers (`sfnt 3`). A `size −1` row is labelled **Type 1 (ATM)** with its `afnt` ID.

## Metrics card

Label / value rows, values in mono: Ascent, Descent, Leading, Max width, each as the stored 4.12 fraction of the point size and, after it, in pixels at the sample's chosen size (`0.920 em · 11 px at 12 pt`). Then First and last character (`$20–$FF`), Flags as chips (Has width tables, Fixed width, Ignore width tables, Use style extra widths) with the raw value `0x6000`.

## Style card

- **Style extra widths:** a compact row of seven values: Plain, Bold, Italic, Underline, Outline, Shadow, Condense, Extend (`+0.031` em etc.), zeros muted.
- **Width tables:** "2 tables: Plain, Bold" or "None".
- **Style mapping:** font class, then the PostScript base name and the derived names per style ("Geneva", "Geneva-Bold"), as a two-column list. "None" when ffStylOff is 0.

## Kerning card

- Summary: "2 tables · Plain 120 pairs · Bold 98 pairs", or "No kerning".
- A small sortable table of the pairs for the chosen style: Pair (the two characters, mono, plus codes on hover) · Kern in em and in pixels at the chosen size. Strongest kerns first, 8 rows, "Show all 120" expands.
- Selecting a pair redraws the sample with that pair highlighted (CmMatchSoft behind both glyphs).

## Diagnostics

`font.short` and `font.undecodable` show in the panel as today; a card whose table was cut short says "Read as far as the data goes" in muted text.
