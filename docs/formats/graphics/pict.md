# QuickDraw pictures (PICT)

A QuickDraw picture is a recording of QuickDraw calls: a header, then a stream of opcodes that DrawPicture plays back
into a port. Pictures are the Mac's clipboard and document image format, stored as `PICT` resources and as `.pict`
files, in versions 1 (the original QuickDraw), 2 (Color QuickDraw) and extended 2 (with a resolution). This document
gives the container, the opcodes and their operands, the play state DrawPicture keeps, and what a writer must produce;
how the drawing comes out, pixel for pixel, is [quickdraw.md](quickdraw.md), and QuickTime images inside pictures are
[quicktime.md](quicktime.md). ClassicMac decodes pictures as the 68k ROM or Mac OS 9 draws them, and writes them.

| | |
| --- | --- |
| Identified by | `PICT` resources; files of type `PICT`, extensions `.pict`, `.pct`, `.pic`. A non-empty picFrame at +$02 and the version opcode at +$0A: `11 01` (version 1) or `00 11 02 FF 0C 00` (version 2); in a file, the same at +$200 (§2.1) |
| ClassicMac | Reads and writes; `ClassicMac.Graphics.Pict` (`PictReader`, `PictHeader`, `PictWriter`), the ImageSharp and SkiaSharp adapters |
| Verified against | Mac OS 9.0 in SheepShaver (the golden pictures, and the rules tagged [Verified]) |
| Sources | *Inside Macintosh: Imaging With QuickDraw*, Appendix A; the 68k ROM's DrawPicture (ROM `$077D`); Mac OS 9.0's native QuickDraw. Other readers (behaviour only): Executor |

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

Numbers, rects, points and colours are as in [quickdraw.md §1.1](quickdraw.md#11-coordinates-and-numbers); the
structures pictures share with QuickDraw (regions, polygons, patterns, pixel maps, colour tables) are in
[quickdraw.md §1](quickdraw.md#1-layout).

### 1.1 Files and resources

A picture is stored either as the data of a `PICT` resource, the picture itself, or as a `.pict` file:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 512 | Application header | Normally zeros; some creators fill it (MacDraw writes `DRWG…`, MacDraft `pictDF…`) |
| +$200 | | Picture | §1.2 |

[Doc: Imaging With QuickDraw]

### 1.2 Picture header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | picSize | `u16`: the low 16 bits of the picture's size. Unreliable in version 2 |
| +$02 | 8 | picFrame | Rect: the picture's bounding box at 72 dpi |
| +$0A | | Version opcode | §1.3 |

[Doc: Imaging With QuickDraw]

### 1.3 Version and header opcodes

- **Version 1:** the byte opcode `$11` and the byte `$01` (the word `$1101`). Every later opcode is one byte, with no
  alignment.
- **Version 2:** the word opcode `$0011` and the word `$02FF`. Every later opcode is two bytes and starts at an even
  offset from the picture's start; a pad byte follows odd-length operands. The `$FF` of `$02FF` is a filler the
  alignment absorbs.

[Doc: Imaging With QuickDraw]

A version 2 picture normally continues with the header opcode `$0C00` and 24 bytes [Doc: Imaging With QuickDraw,
Listings A-5 and A-6]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Version | `i16`: −2 for extended version 2 (OpenCPicture), −1 for version 2 (OpenPicture) |
| +$02 | 2 | Reserved | |
| +$04 | 4 | hRes | Version −2: Fixed dpi. Version −1: the bounding box's left, Fixed |
| +$08 | 4 | vRes | Version −2: Fixed dpi. Version −1: the bounding box's top, Fixed |
| +$0C | 8 | srcRect | Version −2: the optimal source rect, in hRes × vRes units. Version −1: the bounding box's right and bottom, Fixed |
| +$14 | 4 | Reserved | |

### 1.4 Opcodes

Every opcode's operand size is fixed by this table, so an unknown opcode can be skipped. The reserved ranges are
skipped by the ROM's rules, which differ from *Inside Macintosh* where marked (§4.1). [Doc: Imaging With QuickDraw,
Table A-2] [Code: 68k ROM]

| Opcode | Name | Operands | Meaning |
| --- | --- | --- | --- |
| 0000 | NOP | — | |
| 0001 | ClipRgn | Region | The clip region (§2.10) |
| 0002 | BkPat | 8 bytes | Background pattern, 1-bit |
| 0003 | TxFont | `u16` | Font family number (§2.13) |
| 0004 | TxFace | `u8` | Style bits: 1 bold, 2 italic, 4 underline, 8 outline, 16 shadow, 32 condense, 64 extend |
| 0005 | TxMode | `u16` | Text transfer mode |
| 0006 | SpExtra | Fixed | Extra width for the space character |
| 0007 | PnSize | Point | Pen size (scaled, §2.7) |
| 0008 | PnMode | `u16` | Pen transfer mode |
| 0009 | PnPat | 8 bytes | Pen pattern, 1-bit |
| 000A | FillPat | 8 bytes | Fill pattern, 1-bit |
| 000B | OvSize | Point | Round-rect corner oval size (scaled like the pen) |
| 000C | Origin | `i16` dh, `i16` dv | Not a Point: h first. Shifts the drawing space (§2.9) |
| 000D | TxSize | `u16` | Text size in points |
| 000E | FgColor | `u32` | Classic foreground colour ([quickdraw.md §1.9](quickdraw.md#19-classic-colours)) |
| 000F | BkColor | `u32` | Classic background colour |
| 0010 | TxRatio | Point numer, Point denom | Text scale (§2.13) |
| 0011 | VersionOp | `u8` | A version change mid-stream (§2.3) |
| 0012 | BkPixPat | PixPat | Background pixel pattern ([quickdraw.md §1.6](quickdraw.md#16-pixel-patterns)) |
| 0013 | PnPixPat | PixPat | Pen pixel pattern |
| 0014 | FillPixPat | PixPat | Fill pixel pattern |
| 0015 | PnLocHFrac | `u16` | Pen fraction for the next text opcode only (§2.13) |
| 0016 | ChExtra | `i16` | Character extra, 4.12 per point of text size (§2.13) |
| 0017–0019 | reserved | — | |
| 001A | RGBFgCol | RGBColor | Foreground colour |
| 001B | RGBBkCol | RGBColor | Background colour |
| 001C | HiliteMode | — | The next drawing's XOR (or srcXor) becomes hilite ([quickdraw.md §2.12](quickdraw.md#212-hilite)) |
| 001D | HiliteColor | RGBColor | Highlight colour |
| 001E | DefHilite | — | Highlight colour back to the default |
| 001F | OpColor | RGBColor | Weight or pin colour of the arithmetic modes ([quickdraw.md §2.11](quickdraw.md#211-arithmetic-modes)) |
| 0020 | Line | Point from, Point to | |
| 0021 | LineFrom | Point to | From the pen location |
| 0022 | ShortLine | Point from, `i8` dh, `i8` dv | |
| 0023 | ShortLineFrom | `i8` dh, `i8` dv | |
| 0024–0027 | reserved | `u16` length + data | |
| 0028 | LongText | Point, `u8` count, text | Text at a location |
| 0029 | DHText | `u8` dh, `u8` count, text | Text, h moved by dh (unsigned) |
| 002A | DVText | `u8` dv, `u8` count, text | Text, v moved by dv (unsigned) |
| 002B | DHDVText | `u8` dh, `u8` dv, `u8` count, text | |
| 002C | fontName | `u16` length, `u16` old font id, `u8` name length, name | Font-name mapping (§2.13) |
| 002D | LineJustify | `u16` length, Fixed interCharSpacing, Fixed textExtra | (§2.13) |
| 002E | glyphState | `u16` length, `u8` outlinePreferred, `u8` preserveGlyph, `u8` fractionalWidths, `u8` scalingDisabled | (§2.13) |
| 002F | reserved | `u16` length + data | |
| 0030–0034 | frameRect … fillRect | Rect | Verbs: +0 frame, +1 paint, +2 erase, +3 invert, +4 fill ([quickdraw.md §2.3](quickdraw.md#23-verbs)) |
| 0035–0037 | reserved | 8 bytes | |
| 0038–003C | frameSameRect … | — | The last rect again |
| 003D–003F | reserved | — | |
| 0040–0044 | …RRect | Rect | Round rect with the OvSize corner |
| 0045–0047 | reserved | 8 bytes | |
| 0048–004C | …SameRRect | — | |
| 004D–004F | reserved | — | |
| 0050–0054 | …Oval | Rect | |
| 0055–0057 | reserved | 8 bytes | |
| 0058–005C | …SameOval | — | |
| 005D–005F | reserved | — | |
| 0060–0064 | …Arc | Rect, `i16` startAngle, `i16` arcAngle | |
| 0065–0067 | reserved | 12 bytes | |
| 0068–006C | …SameArc | `i16` startAngle, `i16` arcAngle | The last rect, new angles |
| 006D–006F | reserved | 4 bytes | |
| 0070–0074 | …Poly | Polygon | |
| 0075–0077 | reserved | `u16` size (including itself) + data | Like a polygon |
| 0078–007C | …SamePoly | — | |
| 007D–007F | reserved | — | |
| 0080–0084 | …Rgn | Region | |
| 0085–0087 | reserved | `u16` size + data | Like a region |
| 0088–008C | …SameRgn | — | |
| 008D–008F | reserved | — | |
| 0090 | BitsRect | Bitmap (§1.5) | CopyBits of a BitMap or PixMap |
| 0091 | BitsRgn | Bitmap + mask Region | |
| 0092, 0093 | DirectBitsRect, DirectBitsRgn in the ROM | As 009A, 009B | *Inside Macintosh*: reserved (§4.1) |
| 0094–0097 | reserved | `u16` length + data | |
| 0098 | PackBitsRect | Bitmap | |
| 0099 | PackBitsRgn | Bitmap + mask Region | |
| 009A | DirectBitsRect | Direct bitmap (§1.5) | |
| 009B | DirectBitsRgn | Direct bitmap + mask Region | |
| 009C–009F | reserved | `u16` length + data | |
| 00A0 | ShortComment | `u16` kind | (§1.6) |
| 00A1 | LongComment | `u16` kind, `u16` size, data | |
| 00A2–00AF | reserved | `u16` length + data | |
| 00B0–00CF | reserved | — | |
| 00D0–00DF | reserved | `u16` length + data in the ROM | *Inside Macintosh*: `u32` (§4.1) |
| 00E0–00FE | reserved | `u32` length + data | |
| 00FF | OpEndPic | — | End of the picture |
| 0100–7FFF | reserved | `2 × (opcode >> 8)` bytes | |
| 02FF | Version | `u16` | Only in the version 2 header |
| 0C00 | HeaderOp | 24 bytes | (§1.3) |
| 8000–80FF | reserved | — | |
| 8100–FFFF | reserved | `u32` length + data | |
| 8200 | CompressedQuickTime | `u32` length + data | ([quicktime.md §1.1](quicktime.md#11-compressedquicktime-8200)) |
| 8201 | UncompressedQuickTime | `u32` length + data | ([quicktime.md §1.3](quicktime.md#13-uncompressedquicktime-8201)) |

- **Bit 3 of the bitmap opcodes is ignored** [Code: 68k ROM]: `$90`/`$91` are read exactly like `$98`/`$99`, the pixel
  data PackBits whenever rowBytes ≥ 8 (§2.4). `$92`/`$93` are DirectBits.
- **Version 1** pictures use the same numbers as bytes (`$30` frameRect, and so on). PnLocHFrac, ChExtra, LineJustify,
  glyphState and anything above `$FF` do not occur in version 1. [Doc: Imaging With QuickDraw]
- **DHText and DVText:** dh and dv are unsigned bytes, so text only moves right and down. [Doc: Imaging With QuickDraw]

### 1.5 Bitmap opcode operands

`$90`, `$91`, `$98`, `$99` [Doc: Imaging With QuickDraw]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | rowBytes | Bit 15 set: a PixMap. The low 14 bits are the row length |
| +$02 | 8 | bounds | Rect |
| +$0A | 36 | PixMap fields | Only for a PixMap: pmVersion to pmReserved ([quickdraw.md §1.7](quickdraw.md#17-bitmaps-and-pixmaps)) |
| … | | ColorTable | Only for a PixMap ([quickdraw.md §1.8](quickdraw.md#18-colour-tables)) |
| … | 8 | srcRect | Rect |
| … | 8 | dstRect | Rect |
| … | 2 | Mode | `u16`: the transfer mode |
| … | | maskRgn | Region; `$91` and `$99` only |
| … | | Pixel data | §2.4 |

A plain BitMap (bit 15 clear) is 1 bit per pixel, 0 white and 1 black.

`$9A`, `$9B` (and `$92`, `$93` in the ROM): a `u32` baseAddr (always `$000000FF`; ignored), then the same layout with
a PixMap and no ColorTable [Doc: Imaging With QuickDraw].

### 1.6 Picture comments

ShortComment carries a `u16` kind; LongComment a `u16` kind, a `u16` size and that many bytes. They draw nothing.
Kind 224 carries an ICC profile [Doc: ColorSync]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Selector | `u32`: 0 the first chunk, 1 a continuation, 2 the end (no payload) |
| +$04 | | Payload | Profile bytes |

## 2. Reading

§2 gives the 68k ROM's DrawPicture [Code: 68k ROM]; §4.2 lists every point where Mac OS 9 plays a picture
differently.

### 2.1 Recognising a picture

1. **A bare picture:** picFrame (+$02) is not empty, and the bytes at +$0A are `11 01` (version 1) or
   `00 11 02 FF 0C 00` (version 2 with its header opcode).
2. **A `.pict` file:** the same test at +$200. For version 1, also require an all-zero application header, because two
   bytes are a weak signature.
3. To read: if the version opcode is not at +$0A but is at +$20A, skip the 512-byte application header, whatever it
   holds.

[ClassicMac]

### 2.2 The drawing space and the canvas

- **fromRect**, the rect the picture's coordinates are expressed in: srcRect for an extended version 2 picture whose
  srcRect is not empty, else picFrame. Version −1 draws in picFrame at 72 dpi. [Doc: Imaging With QuickDraw]
- **toRect**, the rect the picture is drawn into: DrawPicture's destination. Every coordinate is mapped from fromRect to
  toRect (§2.7), scaling when their sizes differ.

### 2.3 The opcode loop

1. Read the header (§1.2, §1.3). In version 2, if the word after `$0011 $02FF` is not `$0C00` (or fewer than 26 bytes
   remain), there is no header opcode: that word is the first drawing opcode.
2. In version 2, align to an even offset; read the opcode (a byte in version 1, a word in version 2).
3. `$00FF` ends the picture (`$FF` in version 1); so does the end of the data.
4. `$0011` mid-stream switches versions: the next byte is the new version, 1 for byte opcodes, 2 for word opcodes.
5. Otherwise play the opcode, or skip its operands by §1.4. Go to step 2.

The reserved opcodes, `$0017`–`$0019`, ShortComment and LongComment are read and draw nothing; only comment 224
(§2.14) has a meaning.

[Doc: Imaging With QuickDraw] [Code: 68k ROM]

### 2.4 Pixel data

Packed rows are PackBits ([packbits.md](../codecs/packbits.md)), each preceded by its byte count: a `u8`, or a `u16`
when rowBytes > 250 [Doc: Imaging With QuickDraw]. Which rows are packed follows the ROM, in this order
[Code: 68k ROM]:

1. **rowBytes < 8:** the rows are stored unpacked, `rowBytes × height` bytes, whatever the opcode or packType.
2. **Direct PixMap** (pixelType 16), by packType alone; pixelSize is never checked, so a 16-bit map with packType 0, 2
   or 4 decodes as nonsense, as on a Mac:
   - packType **1**: unpacked;
   - packType **0 or 2**: `(rowBytes / 4) × height × 3` bytes with no row counts: R, G, B per pixel, stored as 0RGB;
   - packType **3**: PackBits over 16-bit words, per row;
   - packType **4**: per row, one PackBits line holding `planes = clamp(cmpCount, 1, 4)` component planes of
     `rowBytes / 4` bytes each, written to pixel bytes `4 − planes … 3`: with 3 planes alpha stays 0 and the planes are
     R, G, B; with 4 they are A, R, G, B;
   - packType **5 or more**: each row's packed bytes are read and discarded; the pixels stay 0.
3. **Everything else** (1-bit BitMaps and indexed PixMaps, whatever the packType and the opcode): one PackBits line per
   row. packType is ignored for indexed data, packType 1 included.

### 2.5 Alpha

In a 32-bit map with cmpCount 4 the first byte of each pixel is alpha. QuickDraw ignores it when drawing
([quickdraw.md §2.19](quickdraw.md#219-alpha)). [Doc: Imaging With QuickDraw]

### 2.6 Initial state

DrawPicture starts with [Code: 68k ROM]:

- **Pen:** location (0, 0), mode patCopy, size ScalePt((1, 1)) from picFrame (not an extended header's srcRect) to the
  destination: 1 × 1 unless the picture is scaled [Code]. Visibility is kept.
- **Patterns:** pen and fill black, background white.
- **Colours:** foreground black, background white, OpColor black. The highlight colour is the system's:
  `$9999 $CCCC $CCCC` in the ROM; DefHilite restores it.
- **Text:** font 0, face 0, mode srcOr, size 0, space extra 0, character extra 0, pen fraction ½ (`$8000`).
- **Text ratio:** numer = toRect's size, denom = fromRect's size (§2.13).
- **Clip:** empty until the ClipRgn opcode (§2.10).
- **Pattern alignment:** (0, 0).
- **No font-name mappings.**

### 2.7 MapPt, MapRect, ScaleSize

Every point is mapped from fromRect to toRect, per axis. For a coordinate `c` on an axis where fromRect spans
`[fromLo, fromHi)` and toRect `[toLo, toHi)` [Code: 68k ROM]:

```
fromSize = (short)(fromHi - fromLo);  toSize = (short)(toHi - toLo)
d = (short)(c - fromLo)
if fromSize != toSize:
    negative = d < 0;  if negative: d = (short)-d
    p = (u16)d * (u16)toSize + ((u16)fromSize >> 1)        // unsigned 32-bit
    q = p / (u16)fromSize
    d = q > 0xFFFF ? (short)p : (short)q                     // an overflowing divide leaves the product
    if negative: d = (short)-d
result = (short)(d + toLo)
```

- The rounding is half up on the magnitude, so negative offsets round like positive ones.
- MapRect maps the top-left and bottom-right corners as points.
- **ScaleSize** (pen and oval sizes): equal sizes leave the value unchanged; a size ≤ 0 becomes 0; otherwise
  `((u16)size × (u16)toSize + fromSize / 2) / fromSize`, at least 1.

### 2.8 MapRgn and MapPoly

- **Regions:** map every inversion point with MapPt and rebuild the region from the mapped points; with equal sizes,
  only offset it. The wide-open region, (−32767, −32767, 32767, 32767) with no scan data, is not mapped.
- **Polygons:** map every vertex.

[Code: 68k ROM]

### 2.9 Origin

The Origin opcode's operands are dh then dv (two `i16`, h first: not a Point), as Mac OS 9.0's DrawPicture reads them
[Code]. It adds dh to fromRect's left and right and dv to its top and bottom, cumulatively (each Origin adds to the
shift until the next DrawPicture), so later coordinates land dh, dv further up and left. It also:

- adds (dh, dv) to the pattern alignment, patAlign ([quickdraw.md §2.13](quickdraw.md#213-pattern-placement));
- re-maps the current clip, which is kept in picture coordinates.

[Code: 68k ROM]

### 2.10 Clip

- DrawPicture replaces the port's clip with an empty region until the picture's ClipRgn opcode, so a picture without
  one draws nothing, and one arriving mid-picture limits only what follows; from then on the clip is the mapped picture
  clip intersected with the caller's clip [Code] [Verified: Mac OS 9].
- The clip region is kept in picture coordinates and mapped (§2.8), then intersected with the caller's clip, whenever
  it or fromRect changes (the Origin opcode re-maps it).
- Every drawing operation is limited to that clip and the destination.

### 2.11 Saving and restoring the port

DrawPicture copies the whole port record at entry and restores it at exit, so the pen (location, fraction, size,
mode, visibility), the patterns, the text font, face, size and mode, the space and character extras, the colours and
the clip come back unchanged, and the pen does not move ([Verified: Mac OS 9] for the pen and its fraction). patAlign,
the picture's LineJustify spacing, FractEnable and FScaleDisable are saved and restored too. The ROM writes OpColor
(black) and the highlight colour, and the `$1D`–`$1F` opcodes, into the real port's colour state and does not restore
them [Code]. pnVis is not reset, so a hidden pen hides the picture.

### 2.12 MapFixPt

Version 2 text positions are mapped with fixed-point precision. Per axis, on a Fixed coordinate `c`
[Code: 68k ROM]:

```
fromSize = (fromHi << 16) - (fromLo << 16)     // 32-bit
toSize   = (toHi << 16) - (toLo << 16)
d = c - (fromLo << 16)
if fromSize != toSize: d = (int)((long)d * toSize / fromSize)   // signed 64/32 divide, truncating toward zero
result = d + (toLo << 16)
```

### 2.13 Text opcodes

- **LongText, DHText, DVText, DHDVText** set or move the text location, in picture coordinates; it persists between
  text opcodes. The pen goes to the mapped location:
  - version 2: MapFixPt (§2.12) of `(v + ½, h + pendingFrac)`; the pen is the integer parts, and the pen fraction
    becomes h's fraction;
  - version 1: MapPt; the pen fraction keeps what the previous text left.
  - pendingFrac goes back to ½ at every text opcode.
- **PnLocHFrac** sets pendingFrac for the next text opcode only.
- **ChExtra** is stored as it is (4.12 per point; colour ports only), not through CharExtra [Code]. **LineJustify**
  stores interCharSpacing (Fixed per point). The character extra of
  [quickdraw.md §2.22](quickdraw.md#222-drawing-text) is `(ChExtra << 4) + interCharSpacing`, scaled there.
- **SpExtra** is the space extra (Fixed), scaled by the Font Manager
  ([quickdraw.md §2.21](quickdraw.md#221-font-manager-output)).
- **glyphState:** the third byte turns fractional widths on or off, the fourth scaling off or on
  ([quickdraw.md §2.20](quickdraw.md#220-choosing-a-font), [quickdraw.md §2.21](quickdraw.md#221-font-manager-output)).
- **TxRatio** replaces the text ratio:

  ```
  numer.v' = numer.v × toRect.height;  denom.v' = denom.v × fromRect.height     (u16 × u16)
  while ((numer.v' | denom.v') & 0xFFFF8000) != 0: numer.v' >>= 1; denom.v' >>= 1
  (the same for h with widths)
  ```

- **fontName:** look up the family of that name, as GetFNum does. If it exists, is not family 0 and differs from the
  picture's number, later TxFont opcodes with the picture's number select the named family. The first mapping for a
  number wins. Recorders write fontName before its TxFont.
- **TxMode, TxSize, TxFace, TxFont** are stored as they are. TxSize writes txSize directly, not through TextSize, so it
  does not clear the character extra: ChExtra then TxSize keeps the extra [Code] [Verified: Mac OS 9].

[Code: 68k ROM]

### 2.14 Picture comments

Comments are read and draw nothing. For kind 224, concatenate the payloads from selector 0 through the selector 2
comment: that is the picture's ICC profile. [Doc: ColorSync]

### 2.15 QuickTime images

`$8200` and `$8201` are [quicktime.md](quicktime.md). A drawn QuickTime image is followed by drawing for Macs without
QuickTime, which a reader that drew the image skips ([quicktime.md §2.3](quicktime.md#23-the-fallback-drawing)).

## 3. Writing

### 3.1 The picture

A picture that every PICT reader, the ROM and Mac OS 9 decode alike:

1. **File header:** 512 zero bytes, for a `.pict` file only.
2. **Picture header:** `u16 0` (picSize); picFrame `(0, 0, H72, W72)`, the image's size at 72 dpi,
   `round(pixels × 72 / dpi)`, at least 1; `0011 02FF`; `0C00`; `FFFE 0000` (extended version 2); hRes and vRes
   (Fixed dpi); srcRect `(0, 0, height, width)`; `u32 0`.
3. **Clip:** `0001`, size 10, the rect `(0, 0, height, width)`.
4. **ICC profile** (optional): LongComments of kind 224, selector 0 with the first ≤ 32,000 bytes, selector 1 for each
   further chunk, then selector 2 with no data; each word-aligned.
5. **The image**, one bitmap opcode per vertical strip, each word-aligned; srcRect = dstRect = the strip's bounds, mode
   0 (srcCopy); each packed row preceded by its byte count (§2.4):
   - **Indexed (1, 2, 4, 8 bits):** opcode `0098` (`0090` when rowBytes < 8); rowBytes the even number of bytes
     holding the strip's pixels. A 1-bit image whose palette is exactly {white, black} is a plain BitMap (rowBytes
     without bit 15, no PixMap fields). Otherwise a PixMap (`rowBytes | $8000`, bounds, pmVersion 0, packType 0,
     packSize 0, hRes, vRes, pixelType 0, pixelSize, cmpCount 1, cmpSize = pixelSize, three zero longs) and a
     ColorTable (ctSeed 0, ctFlags 0, ctSize n − 1, entries `value = i`, RGB × 257). Rows PackBits.
   - **16-bit:** opcode `009A`, baseAddr `$000000FF`, rowBytes = width × 2 `| $8000`; pixelType 16, pixelSize 16,
     cmpCount 3, cmpSize 5, packType 3; rows PackBits over words.
   - **32-bit:** `009A`, rowBytes = width × 4 `| $8000`; pixelType 16, pixelSize 32, cmpSize 8, packType 4; cmpCount
     3 (planes R, G, B) or 4 (planes A, R, G, B); each row the planes back to back, each `width` bytes, packed as one
     line. With rowBytes < 8, unpacked `A/0, R, G, B` pixels instead. If any row of a strip packs into more than
     `(n + (n >> 7) + 3) & ~3` bytes (n = planes × width), Mac OS 9 misreads it (§4.2): write that strip with packType
     1 (unpacked `A/0, R, G, B` rows, no byte counts). Incompressible rows always exceed the limit.
6. **Strips:** rowBytes has 14 bits, so a strip is at most `$3FFE` bytes wide: indexed `($3FFE × 8 / bits) & ~15`
   pixels, 16-bit `$3FFE / 2`, 32-bit `$3FFE / 4`. A wider image is split into strips side by side, each with bounds
   `(0, left, height, left + w)`.
7. Word-align, then `00FF`.

[Doc: Imaging With QuickDraw] [Code: Mac OS 9.0 NQD] for the 32-bit limit.

### 3.2 PackBits encoding

Rows are packed as [packbits.md §3](../codecs/packbits.md#3-writing) describes.

### 3.3 Recording a picture

OpenPicture, OpenCPicture and ClosePicture record what is drawn into a port, as both QuickDraws do it
[Code: 68k ROM $077D] [Code: Mac OS 9.0 QuickDraw]; the differences are §4.3.

1. **Opening.** OpenPicture(frame) in a colour port makes version 2 with the version −1 header (§1.3):
   `0011 02FF 0C00 FFFF FFFF`, the frame as Fixed left, top, right, bottom, `u32 0`; picFrame is the frame.
   OpenCPicture(srcRect, hRes, vRes) makes the extended header: `0011 02FF 0C00 FFFE 0000`, hRes, vRes, srcRect,
   `u32 0`, and picFrame `(0, 0, height × 72 / vRes, width × 72 / hRes)` (an unsigned truncating divide). Pictures do
   not nest. Opening calls HidePen, so nothing is drawn while recording unless the caller shows the pen again.
2. **The saved state** starts as: background pattern white, pen and fill patterns black, pen (1, 1) patCopy at (0, 0),
   text font 0, face 0, srcOr, size 0, no space or character extra, ratio 1/1, text at (0, 0), the rect and oval size
   0, the origin the port's, foreground black, background white, OpColor black, the highlight colour **black**, glyph
   state $80808080, and the clip an **empty** region; font 0 is known.
3. **State is lazy.** Setting a pen, colour, font or clip writes nothing; each drawing call writes what it needs where
   it differs from the saved state, which it then takes. Before every drawing call (CheckPic), in this order:
   OpColor `001F`; the highlight colour, as DefHilite `001E` when it is the system's (the low-memory HiliteRGB), else
   HiliteColor `001D`; HiliteMode `001C` when the next drawing highlights; RGBFgCol `001A`; RGBBkCol `001B`; Origin
   `000C` (dh, dv); the clip `0001` with its region (a wide-open clip is the rect (−32767, −32767, 32767, 32767)).
4. **The verb's state** (PutPicVerb): frame writes PnSize `0007` (v, h), PnMode `0008` and the pen pattern; paint the
   pen mode and pattern; erase the background pattern; fill the fill pattern; invert nothing. Patterns (UpdatePat):
   - an old pattern (patType 0) is always PnPat `0009`, BkPat `0002` or FillPat `000A` and its 8 bytes, compared with
     the saved 8 bytes only while the saved pattern is old (else written);
   - any other pattern is PnPixPat `0013`, BkPixPat `0012` or FillPixPat `0014`, written unless the saved pattern is a
     pixel pattern equal to it (EqualPat): patType, the 8-byte pat1Data, then for patType 2 the RGB colour (6 bytes);
     otherwise the pixel map record without baseAddr, its colour table only when it has one (no minimal table), and
     its rows as CopyBits writes them (a direct pattern packed as packType 3 or 4);
   - the saved patterns start as old ones: a black pen, a white background, a black fill.
5. **Lines** take the frame state, then: when dh and dv both fit in −128…127, ShortLineFrom `0023 dh dv` if the pen is
   where the picture last left it, else ShortLine `0022 v h dh dv`; otherwise LineFrom `0021` (the end) or Line `0020`
   (the start and the end). MoveTo writes nothing.
6. **Rects, round rects, ovals and arcs** write the noun ($30, $40, $50, $60) plus the verb and the rect; the same rect
   as the last one of any noun writes the noun + 8 + verb and no rect. A round rect first writes OvSize `000B`
   (height, width) when it changed; an arc ends with its start and arc angles. **Polygons** `0070`+verb and
   **regions** `0080`+verb always write their whole data.
7. **Text**, in pieces of at most 255 bytes: first the state where it changed, in this order: FontName `002C`
   (length + 3, the font number, its name as a Pascal string; once per font, and not when the name is empty), TxFont
   `0003`, TxFace `0004` (a byte), TxMode `0005`, TxSize `000D`, SpExtra `0006`, GlyphState `002E` (`0004`, then
   outline preferred, preserve glyph, FractEnable and FScaleDisable as $FF or 0), TxRatio `0010`, PnLocHFrac `0015`
   (whenever the fraction is not $8000), ChExtra `0016`. Then, with dh, dv the pen less where the last text was: both
   in 0…255, DHText `0029 dh` when dv is 0, DVText `002A dv` when dh is 0, else DHDVText `002B dh dv`; otherwise
   LongText `0028 v h`; then the count and the bytes.
8. **CopyBits** writes only CheckPic's state. The source is trimmed to srcRect's rows and to whole bytes around its
   columns, rowBytes even. BitsRect `0090` (BitsRgn `0091` with a mask region), + 8 for PackBitsRect and PackBitsRgn
   when rowBytes ≥ 8; a direct pixel map DirectBitsRect `009A` (`009B` with a mask) and baseAddr $000000FF. A BitMap
   writes rowBytes and bounds; a pixel map its record without baseAddr (packType 1 when rowBytes < 8, else 0 indexed,
   3 for 16-bit, 4 for 32-bit) and, when indexed, its colour table (none: `00000000 0000 0000 4B4F 0000 0000 0000`).
   Then srcRect, dstRect, the mode, the mask region and the rows: raw under 8 bytes, else each packed by `_PackBits`
   ([packbits.md §4.2](../codecs/packbits.md#42-quickdraws-_packbits)) with its length before it, a byte when
   rowBytes ≤ 250, else a word.
9. **PicComment** writes ShortComment `00A0 kind`, or LongComment `00A1 kind size` and the data; it writes no state and
   is recorded even when nothing else is.
10. **Recording stops** when the pen is hidden twice (an open region or polygon, or one more HidePen): nothing but
    comments is recorded until it is shown again.
11. **Padding and the end:** every opcode starts on an even offset (a zero byte before it); data after an opcode is
    not padded. ClosePicture writes `00FF`, sets picSize to the low word of the length and calls ShowPen.

## 4. Variants

### 4.1 Inside Macintosh and the ROM

Where the ROM's DrawPicture differs from *Inside Macintosh* [Code: 68k ROM]:

- `$0092` and `$0093` are DirectBitsRect and DirectBitsRgn; *Inside Macintosh* calls them reserved.
- `$00D0`–`$00DF` carry a `u16` length; *Inside Macintosh* says `u32`.
- Bit 3 of the bitmap opcodes is ignored, and packType is ignored for indexed data (§2.4).
- Pixel patterns shift by the horizontal pattern alignment only
  ([quickdraw.md §2.13](quickdraw.md#213-pattern-placement)).

### 4.2 Mac OS 9

Mac OS 9's native QuickDraw is a rewrite, not a port. A picture shown on Mac OS 9 follows these rules where they
differ from §2; it still uses the ROM's MapPt, MapRect, ScalePt, FixMul and FixRatio. [Code: Mac OS 9.0 NQD]

- **Highlight colour:** lavender `$CCCC $CCCC $FFFF` (the Appearance default). The `$1D`–`$1F` opcodes play into a copy
  of the port's colour state, but OpColor is still left black.
- **LineJustify** (`$2D`) is skipped: the inter-character spacing stays 0 for the whole picture.
- **MapFixPt** multiplies each axis by the truncated ratio `(toSize << 16) / fromSize`, rounding half up and
  saturating: `(d × ratio + $8000) >> 16`. With a 3 → 1 scale, h = 3.0 maps to 0.99998, so the pen is 0 with fraction
  `$FFFF`, not 1.0.
- **Origin** does not shift the pattern alignment, so patterns stay fixed to the canvas [Code] [Verified]. The opcode
  `$0200` (QDSetPatternOrigin) sets the port's pattern origin, which only pixel patterns use; 1-bit patterns are
  unchanged [Verified]. The ROM skips it.
- **`$92`/`$93`** are reserved (a word length and data). Only `$90`, `$91` and `$98`–`$9B` draw.
- **Colour tables** are read whenever pixelSize < 9, whatever the opcode: for a direct opcode and for pixel patterns
  too.
- **PixPat types:** 1 and 3 are pixel maps; every other type is an RGB colour.
- **Direct pixel data, packType 0:** 32-bit maps take the 3-byte path, and 16-bit maps are word PackBits (the ROM's
  nonsense decoding does not happen).
- **PackBits:** a flag of `$80` (−128) is a run of 129 copies of the next unit, never a no-op.
- **Round rects:** the corner size is clamped to `[0, rect size]` on each axis, and a zero width or height draws a
  plain rect.
- **Component planes (packType 4)** are read through Mac OS 9's own buffers:
  1. Always 3 planes, landing on pixel bytes 1–3, unless cmpCount is 4 (then 4 planes, bytes 0–3).
  2. The unpacked length per row is `n = rowBytes − rowBytes / 4`, or rowBytes with 4 planes.
  3. The packed-row buffer holds only `P = (n + (n >> 7) + 3) & ~3` bytes; the unpack buffer follows it directly, in
     one block kept for the whole image.
  4. Model it as one array `mem` of `P + n` bytes. Copy each row's packed bytes to `mem[0…]`; a long row spills past
     P. Then unpack, reading `mem[src++]` and writing `mem[P + dst++]` until n bytes are out (`$80` a run of 129).
  5. A row packed into more than P bytes therefore reads spilled bytes its own output has already overwritten: a
     97-byte row of 96 literals ends with its first output byte, so pixel 31's blue takes pixel 0's red. The ROM reads
     such rows correctly.

### 4.3 Recording: the ROM and Mac OS 9

Picture recording (§3.3) differs [Code: 68k ROM $077D] [Code: Mac OS 9.0 QuickDraw]:

- **OpenCPicture's frame:** the ROM scales srcRect only when both resolutions are set, else keeps srcRect; Mac OS 9
  always scales.
- **CopyBits of a BitMap:** Mac OS 9 records the mode without ditherCopy and with the arithmetic modes as Boolean ones
  (blend → srcCopy, addPin → srcBic, addOver → srcXor, subPin → srcOr, transparent → srcCopy, by `index & 3`); the ROM
  records the mode as given. A pixel map's mode is recorded as given in both.
- **An empty CopyBits** (no width once trimmed): the ROM records nothing; Mac OS 9 records it.
- **ScrollRect:** the ROM stops recording while it runs; Mac OS 9 does not, so it records its CopyBits and erase (not
  verified live).
- **GlyphState:** Mac OS 9 writes FractEnable as $FF when it is set; the ROM writes the byte as stored.
- **The pattern origin:** Mac OS 9 writes `0200` (v, h) when QDSetPatternOrigin changed it.
- **A pixel pattern's pmVersion:** Mac OS 9 records 0; the ROM records the pixel map's own.
- **Packed rows:** the two `_PackBits` differences of [packbits.md §4.2](../codecs/packbits.md#42-quickdraws-_packbits).
- **Version 1 pictures** (an old GrafPort): the ROM also writes FontName, LineLayout and GlyphState (every ROM version 1
  picture with text gets `2E 0004 xxxxxxxx`); Mac OS 9 writes them only in version 2.

## 5. ClassicMac

- **Decoding:** `PictReader.Decode` returns the `RgbaBitmap`; `PictReader.Read` adds the `PictInfo` (version, extended
  version 2, picFrame, bounds, resolution, every comment in order, the ICC profile). Both take bytes or a stream (read
  from its position to its end and left open) and a cancellation token checked between opcodes. `PictHeader.IsPicture`,
  `IsPictFile` (§2.1) and `ReadInfo` (the header without decoding) recognise pictures. [ClassicMac]
- **The canvas:** a fresh RGBA canvas that starts transparent; pixels the picture never draws stay transparent
  ([quickdraw.md §5](quickdraw.md#5-classicmac)). Its size is `PictDecodeOptions.Resolution`: `Native` (the default)
  maps fromRect 1:1 to a canvas its size; `PictureFrame` draws into a canvas the size of picFrame at 72 dpi, scaling as
  DrawPicture does. [ClassicMac]
- **The clip:** on a fresh canvas a picture without a ClipRgn opcode still draws (unclipped until one arrives);
  `QuickDrawPort.DrawPicture` into a caller's port follows the Mac (§2.10). [ClassicMac]
- **`PictDecodeOptions`:** `QuickDraw` selects `MacOS9` (the default) or `MacRom`; `ScreenDepth` 1, 2, 4, 8, 16 or 32
  (the default) ([quickdraw.md §4.6](quickdraw.md#46-screen-depths)); `HiliteColor` the system highlight colour (by
  default the chosen QuickDraw's); `PreserveAlpha` keeps alpha for srcCopy; `Fonts` and `TextFallback` draw text
  ([quickdraw.md §5](quickdraw.md#5-classicmac)); `ImageCodec` decodes QuickTime codecs the core lacks. [ClassicMac]
- **Not modelled:** Mac OS 9's opcode `$0200` is skipped by its size in both modes; the pattern origin it sets is not
  modelled (§8). [ClassicMac]
- **Errors:** an unexpected version opcode is refused with `NotSupportedException`, truncated data with
  `EndOfStreamException`; a length that runs past the data is truncation. An unsupported pixel format throws.
  [ClassicMac]
- **Damaged pictures:** scaling from a rectangle of zero width or height (DrawPicture's MapPt and ScalePt divide by
  it) is refused with `InvalidDataException`, since the 68k's `DIVU` traps on a zero divisor (a "zero divide" system
  error). A BitMap or PixMap whose rowBytes is too small for its width is drawn with its rows overlapping as QuickDraw
  reads them; what the last row would read past the pixel data is zeros, where the Mac reads whatever memory follows.
  A pixel pattern whose pixel map has empty bounds has no pixel data but still tiles one pixel, read the same way:
  zeros, so pixel value 0. Both modes. [ClassicMac]
- **Size limit:** a picture whose canvas (§2.2) has more than `PictDecodeOptions.MaxPixels` pixels (64 Mi by
  default) is refused with `InvalidDataException` before anything is allocated; the Mac has no such limit, as
  DrawPicture draws into the port, clipped to it. `ClassicMac.Resources.Decoders` passes `DecodeOptions.MaxImagePixels`.
  [ClassicMac]
- **Writing:** `PictWriter` writes §3: `PictWriteOptions.Format` (`Indexed1`, `Indexed2`, `Indexed4`, `Indexed8`,
  `Rgb555`, `Rgb888` (the default), `Argb8888`), `Palette` (else the bitmap's own colours, which must fit), the
  resolutions (72 dpi by default), `IccProfile`, and `FileHeader` (true by default; false writes a bare picture for a
  `PICT` resource). Width and height must be 1–32767. With a palette each pixel takes its nearest palette colour.
  [ClassicMac]
- **Importing an image** (`ImageImport.WritePicture`, the editor's Import Image): the image is composited over white
  (a picture has no transparency), then written as §3 without the file header: indexed at the smallest depth (1, 2, 4
  or 8 bits) that holds its colours, else 32-bit direct (`Rgb888`). [ClassicMac]
- **Adapters:** the ImageSharp adapter registers the format (`PictFormat`: extensions `pict`, `pct`, `pic`; MIME
  `image/x-pict`, `image/pict`) with a decoder, an encoder and two detectors (one past the 512-byte header, one for
  bare pictures), and reports the picture's resolution and ICC profile as metadata; unreadable data becomes
  `InvalidImageContentException`. The SkiaSharp adapter's `PictSkia` decodes to an unpremultiplied `SKBitmap` or an
  `SKImage`, `DecodeAny` tries QTIF, PICT and MacPaint, and `Encode` writes §3. [ClassicMac]

- **Recording** (§3.3, §4.3): `PictureRecorder.OpenPicture`/`OpenCPicture` and `ClosePicture` record a
  `QuickDrawPort`'s drawing, and `QuickDrawPort.PicComment` adds comments. A port is a colour port, so pictures are
  version 2. ClassicMac records neither ScrollRect, nor CopyMask, nor a picture drawn into the port, writes no pattern
  origin and no LineLayout, has no memory limit (so no dead picture), and takes font names from the port's
  `FontLibrary`. A pixel pattern's pmVersion is 0 in both modes; OpenCPicture with a zero resolution keeps srcRect in both
  modes. [ClassicMac]

## 6. Diagnostics

`ClassicMac.Graphics` reports no diagnostics: it throws (§5). When `ClassicMac.Resources.Decoders` converts a `PICT`
resource ([export-manifest.md](../output/export-manifest.md)), it reports:

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `image.too-large` | Warning | picFrame covers more than `DecodeOptions.MaxImagePixels` | Does not draw the picture; writes the resource raw | No such limit |
| `image.undecodable` | Warning | The library refused the data (truncated, a bad structure, an unsupported variant, a canvas over `MaxImagePixels` once the header's resolution is applied) | Writes the resource raw | Not traced |
| `image.decoder-fault` | Error | The decoder failed on the data with an index or arithmetic exception: a ClassicMac bug (found by mutation testing), not damaged data | Writes the resource raw | Not applicable |

## 7. Verification

- **Golden pictures:** `tools/GoldenPictures` writes the feature pictures in `tests/golden/pict` (shapes, pen modes,
  lines, 1-bit and colour CopyBits, colours and arithmetic modes, text, a 144 dpi extended version 2 picture, Origin and
  clip); `GoldenTests` compares screenshots of them, taken in an emulator, with the decoder pixel for pixel
  (`tests/golden/README.md`). The screenshots and the Mac fonts are not committed, so the test skips without them.
- **Rules tagged [Verified]** were checked on Mac OS 9.0 in SheepShaver: the clip before ClipRgn, the restored pen and
  fraction, ChExtra kept by TxSize, Origin and `$0200` on Mac OS 9.
- **Hand-built pictures** (`tests/ClassicMac.Graphics.Tests`, built opcode by opcode with `PictBuilder`):
  - `PictParserTests`: operand sizes of every legal opcode; a mid-stream version change; the extended version 2 and
    version −1 headers; a version 2 picture without the header opcode; pixel patterns without desync; ICC comments;
    `$92` as DirectBitsRect in the ROM and reserved on Mac OS 9; every packType of §2.4 and §4.2, including the Mac OS
    9 plane-buffer overflow and PackBits `$80`; a `.pict` file with a non-zero application header; `ReadInfo`.
  - `PictReaderTests`: a written picture reads back pixel-identical; a PNG is not a picture; a version 1 BitsRect;
    shapes in canvas space; text style reaches the fallback.
  - `CopyBitsTests` and `DrawingTests`: MapPt, ScaleSize, MapRgn and the wide-open region; `PictureFrame` resolution;
    the Origin opcode (cumulative shift, clip re-mapping, pattern alignment in the ROM and not on Mac OS 9).
  - `PortTests`: DrawPicture into a port draws nothing before a clip, stays inside the port's clip, scales and
    restores the port.
  - `TextTests`: PnLocHFrac, TxRatio, fontName and TxSize keeping the character extra.
  - `PictureRecordingTests`: the traced code's example recording byte for byte in both modes (derived from the code,
    not dumped live), lazy state and the same rect, a pen hidden twice, the line and text opcodes, a BitMap's mode in
    each QuickDraw, OpenCPicture's frame, recorded drawing and 4-, 8-, 16- and 32-bit pixel maps playing back
    pixel-identical.
  - `WriterTests`: every format round-trips; incompressible 32-bit rows stay readable by Mac OS 9; the plain BitMap;
    palettes; resolution and picFrame; ICC across several comments; strips; a bare picture.
  - `StreamDecodingTests`, `ImageSharpPluginTests`, `SkiaSharpPluginTests`: streams, the adapters' detection,
    metadata, truncated data and encoding.

## 8. Not covered

- The pattern origin of Mac OS 9's opcode `$0200` (QDSetPatternOrigin), read and written.
- Recording version 1 pictures (an old GrafPort), ScrollRect, CopyMask and DrawPicture into a recording port.
- Drawing gaps are in [quickdraw.md §8](quickdraw.md#8-not-covered), QuickTime's in
  [quicktime.md §8](quicktime.md#8-not-covered).

## 9. References

1. Apple Computer, *Inside Macintosh: Imaging With QuickDraw* (1994), Appendix A, "Picture Opcodes" (Table A-2,
   Listings A-5 and A-6). Apple's documentation.
2. Apple Computer, *ColorSync* documentation, the picture comment for embedded profiles (kind 224). Apple's
   documentation.
3. Executor (ARDI; Wolfgang Thaller's revision), <https://github.com/autc04/executor>, `qPicstuff.cpp` (operand sizes,
   pixel-data layouts) and `C_StdPoly`. MIT; parts ported with notice (`THIRD-PARTY-NOTICES.md`).
