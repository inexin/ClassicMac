# QuickDraw pictures (PICT)

This document specifies the Macintosh QuickDraw picture format (PICT, versions 1, 2 and extended 2) completely
enough to write a decoder that plays pictures back as a Macintosh does, and an encoder whose output a Macintosh
reads: the container, the opcodes and their operands, and the play state DrawPicture keeps. How the drawing itself
comes out, pixel for pixel, is [QUICKDRAW.md](QUICKDRAW.md); QuickTime images inside pictures are
[QUICKTIME.md](QUICKTIME.md). ClassicMac implements it in `ClassicMac.Graphics.Pict` and the ImageSharp and SkiaSharp
adapters.

There are two reference implementations, and they differ in details:

- **Mac OS 9** replaced most of QuickDraw (DrawPicture, CopyBits, the shape procedures, text and the Font Manager)
  with a native PowerPC rewrite. This is what current Macs-in-emulation (SheepShaver, and anything running Mac OS 9)
  show, and it is the **default** of ClassicMac.
- **The Macintosh ROM** (Mac OS ROM 1.6, `$077D`) holds the last Apple revision of the classic 68k QuickDraw, which
  every Mac before Mac OS 9 used.

Sections 2–9 describe the ROM behaviour, marked **ROM** where it is surprising. Section 10 lists every point where
Mac OS 9 plays a picture differently.

Contents

1. [Conventions](#1-conventions)
2. [Container](#2-container)
3. [The opcode stream](#3-the-opcode-stream)
4. [Operand structures](#4-operand-structures)
5. [Pixel data](#5-pixel-data)
6. [Play state and coordinate mapping](#6-play-state-and-coordinate-mapping)
7. [Text opcodes](#7-text-opcodes)
8. [Picture comments](#8-picture-comments)
9. [Writing pictures](#9-writing-pictures)
10. [Mac OS 9 differences](#10-mac-os-9-differences)
11. [Not covered](#11-not-covered)

---

## 1. Conventions

- All multi-byte values are **big-endian**.
- `u8/u16/u32` are unsigned, `i8/i16/i32` signed.
- `Rect` is four `i16`: **top, left, bottom, right**. Right and bottom are exclusive, so a rect covers
  `right − left` × `bottom − top` pixels. A rect is empty when `bottom ≤ top` or `right ≤ left`.
- `Point` is two `i16`: **v, h** (vertical first).
- `Fixed` is a signed 16.16 fixed-point `i32`.
- `RGBColor` is three `u16`: red, green, blue. An 8-bit component is the high byte.
- Coordinates are QuickDraw's: the grid lines lie between pixels. Pixel (h, v) is the one below and to the right of
  grid point (h, v).
- **16-bit arithmetic.** QuickDraw computes coordinates in 16-bit words, and several results below depend on that
  wrap-around. "(short)" means: truncate to 16 bits and sign-extend.
- Mode, opcode and constant values are given in hex (`$xx` or `0xXX`) or decimal as noted.

---

## 2. Container

### 2.1 Files and resources

A picture is stored either as:

- the data of a `PICT` resource: the picture itself; or
- a `.pict` file: **512 bytes of application header**, then the picture.

The file header is normally all zeros, but some creators fill it (MacDraw writes `DRWG…`, MacDraft `pictDF…`). Do
not require zeros. Instead, look for the version opcode (§2.3) at offset 10: if it is not there but is at offset
512 + 10, skip the 512 bytes.

Detection without a file extension:

- **Bare picture:** the picFrame (bytes 2–9) is non-empty, and either
  - the bytes at offset 10 are `11 01` (version 1), or
  - the bytes at offset 10 are `00 11 02 FF 0C 00` (version 2 with its header opcode).
- **`.pict` file:** the same test at offset 512. For version 1, also require an all-zero header, because two bytes
  are a weak signature.

### 2.2 Picture header

| Offset | Type | Field |
|---|---|---|
| 0 | u16 | `picSize`: the low 16 bits of the picture size. Unreliable for version 2; ignore it. |
| 2 | Rect | `picFrame`: the picture's bounding box at 72 dpi. |
| 10 | … | the version opcode |

### 2.3 Version

- **Version 1:** the byte opcode `$11` followed by the byte `$01` (the word `0x1101`). All following opcodes are
  **one byte**, and there is no alignment.
- **Version 2:** the word opcode `0x0011` followed by the word `0x02FF`. All following opcodes are **two bytes**,
  and each opcode starts at an **even offset** from the start of the picture. Skip a pad byte after odd-length
  operands. The `$FF` of `0x02FF` is a filler, which the alignment rule absorbs.

A version-2 picture normally continues with the **header opcode `0x0C00`** and 24 bytes of data:

| Offset | Type | Extended version 2 (version field −2) | Version 2 (version field −1) |
|---|---|---|---|
| 0 | i16 | version = −2 | version = −1 |
| 2 | u16 | reserved | reserved |
| 4 | Fixed | hRes (dpi) | bounding box left (Fixed) |
| 8 | Fixed | vRes (dpi) | bounding box top… (Fixed) |
| 12 | Rect | `srcRect`: the optimal source rect, in hRes/vRes units | …right, bottom (Fixed) |
| 20 | u32 | reserved | reserved |

- If the word after `0x0011 0x02FF` is not `0x0C00` (or fewer than 26 bytes remain), there is no header. That word
  is the first drawing opcode.
- Only version −2 changes anything: its opcodes draw in the coordinate space of `srcRect` (if non-empty), at
  `hRes × vRes`. Version −1 draws in `picFrame` at 72 dpi.
- The mid-stream opcode `0x0011` may switch versions: the next byte gives the new version (1 = byte opcodes,
  2 = word opcodes).

### 2.4 The drawing space and the canvas

- **fromRect**: the rect the picture's coordinates are expressed in. This is `srcRect` for an extended version-2
  picture (when non-empty), else `picFrame`.
- **toRect**: the rect the picture is drawn into. A decoder chooses it:
  - **Native resolution:** a canvas the size of fromRect, with toRect = (0, 0, height, width). Pixels map 1:1 and
    nothing is scaled.
  - **72 dpi:** a canvas the size of `picFrame`. When the sizes differ, every coordinate is scaled (§6), exactly as
    `DrawPicture` scales into a destination rect of another size.

The canvas starts transparent ([QUICKDRAW.md](QUICKDRAW.md) §3.1).

---

## 3. The opcode stream

Opcodes are read in a loop until `0x00FF` (OpEndPic) or the end of the data. In version 1, an opcode is one byte and
its value is the same number (`$FF` ends it).

### 3.1 Operand sizes

Every opcode's length is fixed by the table below, so unknown opcodes can be skipped. The ROM's own skip rules for
reserved ranges are given, and they differ from Inside Macintosh in places (marked **ROM**).

| Opcode | Name | Operands | Meaning (§ reference) |
|---|---|---|---|
| 0000 | NOP | — | |
| 0001 | ClipRgn | Region | Set the clip region (§6.5) |
| 0002 | BkPat | 8 bytes | Background pattern (1-bit) |
| 0003 | TxFont | u16 | Font family number (§7) |
| 0004 | TxFace | u8 | Style bits: 1 bold, 2 italic, 4 underline, 8 outline, 16 shadow, 32 condense, 64 extend |
| 0005 | TxMode | u16 | Text transfer mode |
| 0006 | SpExtra | Fixed | Extra width added to the space character |
| 0007 | PnSize | Point | Pen size (scaled, §6.2) |
| 0008 | PnMode | u16 | Pen transfer mode |
| 0009 | PnPat | 8 bytes | Pen pattern (1-bit) |
| 000A | FillPat | 8 bytes | Fill pattern (1-bit) |
| 000B | OvSize | Point | Round-rect corner oval size (scaled like the pen) |
| 000C | Origin | i16 dh, i16 dv (not a Point: h first) | Shift the drawing space (§6.4) |
| 000D | TxSize | u16 | Text size in points |
| 000E | FgColor | u32 | Classic foreground colour constant (§4.6) |
| 000F | BkColor | u32 | Classic background colour constant |
| 0010 | TxRatio | Point numer, Point denom | Text scale (§7) |
| 0011 | VersionOp | u8 | Mid-stream version change (§2.3) |
| 0012 | BkPixPat | PixPat | Background colour pattern (§4.4) |
| 0013 | PnPixPat | PixPat | Pen colour pattern |
| 0014 | FillPixPat | PixPat | Fill colour pattern |
| 0015 | PnLocHFrac | u16 | Pen fraction for the next text opcode only (§7) |
| 0016 | ChExtra | i16 | Character extra, 4.12 fixed per point of text size (§7) |
| 0017–0019 | reserved | — | |
| 001A | RGBFgCol | RGBColor | Foreground colour |
| 001B | RGBBkCol | RGBColor | Background colour |
| 001C | HiliteMode | — | The next drawing's XOR (or srcXor) becomes hilite ([QUICKDRAW.md](QUICKDRAW.md) §5.5) |
| 001D | HiliteColor | RGBColor | Highlight colour |
| 001E | DefHilite | — | Highlight colour back to the default |
| 001F | OpColor | RGBColor | Weight/pin colour for arithmetic modes ([QUICKDRAW.md](QUICKDRAW.md) §5.4) |
| 0020 | Line | Point from, Point to | |
| 0021 | LineFrom | Point to | Line from the pen location |
| 0022 | ShortLine | Point from, i8 dh, i8 dv | |
| 0023 | ShortLineFrom | i8 dh, i8 dv | |
| 0024–0027 | reserved | u16 length + data | |
| 0028 | LongText | Point, u8 count, text | Text at a location |
| 0029 | DHText | u8 dh, u8 count, text | Text, h moved by dh (**unsigned**) |
| 002A | DVText | u8 dv, u8 count, text | Text, v moved by dv (unsigned) |
| 002B | DHDVText | u8 dh, u8 dv, u8 count, text | |
| 002C | fontName | u16 length, u16 old font id, u8 name length, name | Font name mapping (§7) |
| 002D | LineJustify | u16 length, Fixed interCharSpacing, Fixed textExtra | (§7) |
| 002E | glyphState | u16 length, u8 outlinePreferred, u8 preserveGlyph, u8 fractionalWidths, u8 scalingDisabled | (§7) |
| 002F | reserved | u16 length + data | |
| 0030–0034 | frameRect … fillRect | Rect | Verbs: +0 frame, +1 paint, +2 erase, +3 invert, +4 fill |
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
| 0060–0064 | …Arc | Rect, i16 startAngle, i16 arcAngle | |
| 0065–0067 | reserved | 12 bytes | |
| 0068–006C | …SameArc | i16 startAngle, i16 arcAngle | The last rect, new angles |
| 006D–006F | reserved | 4 bytes | |
| 0070–0074 | …Poly | Polygon | |
| 0075–0077 | reserved | Polygon-like: u16 size (including itself) + data | |
| 0078–007C | …SamePoly | — | |
| 007D–007F | reserved | — | |
| 0080–0084 | …Rgn | Region | |
| 0085–0087 | reserved | Region-like: u16 size + data | |
| 0088–008C | …SameRgn | — | |
| 008D–008F | reserved | — | |
| 0090 | BitsRect | bitmap (§4.5) | CopyBits of a BitMap/PixMap |
| 0091 | BitsRgn | bitmap + mask Region | |
| 0092, 0093 | **ROM:** DirectBitsRect, DirectBitsRgn | as 009A, 009B | IM calls these reserved |
| 0094–0097 | reserved | u16 length + data | |
| 0098 | PackBitsRect | bitmap | |
| 0099 | PackBitsRgn | bitmap + mask Region | |
| 009A | DirectBitsRect | direct bitmap (§4.5) | |
| 009B | DirectBitsRgn | direct bitmap + mask Region | |
| 009C–009F | reserved | u16 length + data | |
| 00A0 | ShortComment | u16 kind | (§8) |
| 00A1 | LongComment | u16 kind, u16 size, data | |
| 00A2–00AF | reserved | u16 length + data | |
| 00B0–00CF | reserved | — | |
| 00D0–00DF | reserved | **ROM: u16** length + data (IM says u32) | |
| 00E0–00FE | reserved | u32 length + data | |
| 00FF | OpEndPic | — | End of picture |
| 0100–7FFF | reserved | `2 × (opcode >> 8)` bytes | |
| 02FF | Version | u16 | (only as part of the version-2 header) |
| 0C00 | HeaderOp | 24 bytes | (§2.3) |
| 8000–80FF | reserved | — | |
| 8100–FFFF | reserved | u32 length + data | |
| 8200 | CompressedQuickTime | u32 length + data | ([QUICKTIME.md](QUICKTIME.md)) |
| 8201 | UncompressedQuickTime | u32 length + data | ([QUICKTIME.md](QUICKTIME.md) §3) |

Notes:

- **Bit 3 of the bitmap opcodes is ignored (ROM).** `0x90`/`0x91` are read exactly like `0x98`/`0x99`: the pixel
  data is PackBits whenever rowBytes ≥ 8 (§5). `0x92`/`0x93` are DirectBits.
- **Version-2 drawing opcodes in version-1 pictures** use the same numbers as bytes (`$30` frameRect, and so on).
  PnLocHFrac, ChExtra, LineJustify, glyphState and anything above `$FF` do not occur in version 1.
- **Text in DHText/DVText:** dh and dv are unsigned bytes, so text only moves right and down.

### 3.2 Opcodes that are read but draw nothing

- 0x0017–0x0019, every reserved opcode, and ShortComment/LongComment are consumed and otherwise ignored.
- The only exception is comment 224, which carries the ICC profile (§8).

---

## 4. Operand structures

### 4.1 Region

| Type | Field |
|---|---|
| u16 | `rgnSize`: bytes including this word and the bbox. Mask off bit 15. |
| Rect | `rgnBBox` |
| i16[] | `(rgnSize − 10) / 2` words of scan data (a pad byte follows if rgnSize is odd) |

- A region of size 10 (no scan data) is its bounding rectangle.
- Otherwise the scan data is a list of rows, then a final `0x7FFF`. Each row is:
  - `y`;
  - then x values, in increasing order;
  - then `0x7FFF`.
- The x values are **inversion points**. At each (x, y), the inside/outside state of every pixel at or right of x
  and at or below y is flipped.
- Equivalently, a pixel is inside when the count of inversion points (x′, y′) with x′ ≤ x and y′ ≤ y is odd. XOR
  the x lists of successive rows to get each row's set of span boundaries.

To write a region, emit only the points where a band's spans change against the band above it: the symmetric
difference of the two span-boundary sets.

### 4.2 Polygon

| Type | Field |
|---|---|
| u16 | `polySize` in bytes, including this word |
| Rect | bounding box |
| Point[] | `(polySize − 10) / 4` points |

Skip any leftover bytes of polySize. A polygon with an **empty bounding box** draws nothing.

### 4.3 Pattern (1-bit)

- 8 bytes, one per row, with the most significant bit leftmost.
- A 1 bit draws the foreground colour and a 0 bit the background colour ([QUICKDRAW.md](QUICKDRAW.md) §5).
- The pattern tiles the plane from the **drawing origin** (§6.4).

### 4.4 PixPat (BkPixPat / PnPixPat / FillPixPat)

| Type | Field |
|---|---|
| u16 | `patType` |
| 8 bytes | a 1-bit pattern (the fallback for 1-bit ports) |
| … | if `patType == 2` (dither pattern): an **RGBColor** (6 bytes; some readers wrongly skip 5) |
| … | otherwise: a PixMap **without baseAddr** (rowBytes, bounds, the PixMap fields of §4.5), a ColorTable, then its pixel data (§5) |

- A type-2 pattern draws as that solid colour.
- A type-1 pattern tiles its pixel map. Pixel (x, y) of the pattern is `((x + patAlign.h) mod w, y mod h)`. **ROM:**
  only the horizontal origin shift applies to pixel patterns (see §6.4).

### 4.5 BitMap, PixMap and bitmap opcode operands

**Bitmap opcodes 0x90, 0x91, 0x98, 0x99:**

| Type | Field |
|---|---|
| u16 | `rowBytes`: bit 15 set → a PixMap follows; the low 14 bits are the row length |
| Rect | `bounds` |
| — | if PixMap: the PixMap fields below, then a ColorTable |
| Rect | srcRect |
| Rect | dstRect |
| u16 | transfer mode |
| Region | mask region (0x91, 0x99 only) |
| — | pixel data (§5) |

A plain BitMap (bit 15 clear) is 1 bit per pixel with the palette {0: white, 1: black}.

**Direct opcodes 0x9A, 0x9B (and 0x92, 0x93):** `u32 baseAddr` (always `0x000000FF`; ignore it), then the same
layout as above with a PixMap and **no ColorTable**.

**PixMap fields** (after rowBytes and bounds):

| Type | Field |
|---|---|
| u16 | pmVersion |
| u16 | `packType` |
| u32 | packSize |
| Fixed | hRes |
| Fixed | vRes |
| u16 | `pixelType`: 0 = indexed, 16 = direct (RGBDirect) |
| u16 | `pixelSize`: 1, 2, 4, 8, 16 or 32 (anything else is not a QuickDraw depth) |
| u16 | `cmpCount`: 1 indexed, 3 RGB, 4 with alpha |
| u16 | cmpSize |
| u32 | planeBytes |
| u32 | pmTable |
| u32 | pmReserved |

**In-memory layout** (after unpacking):

- Rows of `rowBytes` bytes.
- Indexed pixels are packed most significant first.
- 16-bit pixels are big-endian `xRRRRRGGGGGBBBBB`. Expand a 5-bit component to 8 bits as `(c << 3) | (c >> 2)`.
- 32-bit pixels are four bytes: **alpha/pad, R, G, B**.

### 4.6 ColorTable

| Type | Field |
|---|---|
| u32 | ctSeed |
| u16 | ctFlags: bit 15 set → a device table |
| u16 | ctSize: entry count − 1 |
| entries | u16 value, u16 r, u16 g, u16 b |

- In a device table each entry is the pixel value of its **position**. Otherwise each entry's `value` field is its
  pixel value.
- Pixel values the table does not list are black.

### 4.7 Classic colour constants (FgColor / BkColor)

| Constant | Colour | RGB (16-bit) |
|---|---|---|
| 30 | whiteColor | FFFF FFFF FFFF |
| 33 | blackColor | 0000 0000 0000 |
| 69 | yellowColor | FC00 F37D 052F |
| 137 | magentaColor | F2D7 0856 84EC |
| 205 | redColor | DD6B 08C2 06A2 |
| 273 | cyanColor | 0241 AB54 EAFF |
| 341 | greenColor | 0000 8000 11B0 |
| 409 | blueColor | 0000 0000 D400 |

Any other value gives black as a foreground colour and white as a background colour. On a colour port, ForeColor and
BackColor set these RGB colours from the QDColors table (`clut` 127 in the ROM).

---

## 5. Pixel data

### 5.1 PackBits

A packed scan line is:

- a **byte count** (a u8, or a **u16 when rowBytes > 250**);
- then that many bytes of runs, each starting with a flag byte `n` (i8):
  - `n ≥ 0`: copy the next `n + 1` units;
  - `n < 0` and `n ≠ −128`: repeat the next unit `1 − n` times;
  - `n = −128`: no-op.

A unit is a byte, except for **word packing** (packType 3), where it is two bytes. Stop at the row length; ignore
extra bytes inside the counted block.

### 5.2 Which rows are packed (ROM)

The ROM's rules are simpler and stranger than Inside Macintosh's. Apply them in this order:

1. **rowBytes < 8:** the rows are stored unpacked (`rowBytes × height` bytes), whatever the opcode or packType.
2. **Direct PixMap** (`pixelType == 16`), dispatched on **packType only**. pixelSize is never checked, so a 16-bit
   map with packType 0, 2 or 4 is decoded as nonsense exactly as on a Macintosh.
   - packType **1**: unpacked.
   - packType **0 or 2**: `(rowBytes / 4) × height × 3` bytes with no row counts: R, G, B per pixel, stored as
     0RGB.
   - packType **3**: PackBits over 16-bit words, per row.
   - packType **4**: per row, one PackBits line holding `planes = clamp(cmpCount, 1, 4)` component planes, each
     `rowBytes / 4` bytes. The planes are written to pixel bytes `4 − planes … 3`, so with 3 planes alpha stays 0
     and the planes are R, G, B; with 4 they are A, R, G, B.
   - packType **≥ 5**: each row's packed bytes are read and discarded, and the pixels stay zero.
3. **Everything else** (1-bit BitMaps and indexed PixMaps, whatever the packType and whichever bitmap opcode): one
   PackBits line per row. **packType is ignored** for indexed data, even packType 1.

### 5.3 Alpha

In a 32-bit map with cmpCount 4, the first byte of each pixel is alpha. QuickDraw ignores alpha when drawing. A
decoder may keep it for srcCopy transfers ([QUICKDRAW.md](QUICKDRAW.md) §6.6).

---

## 6. Play state and coordinate mapping

### 6.1 Initial state

`DrawPicture` starts with:

- **Pen:** location (0, 0), mode patCopy, size ScalePt((1, 1)) from picFrame (not an extended header's srcRect) to the
  destination: 1×1 unless the picture is scaled [Code]. Visibility is kept.
- **Patterns:** pen and fill black, background white.
- **Colours:** foreground black, background white, OpColor black. Highlight colour is the system default: `$9999/$CCCC/$CCCC`
  in the ROM, lavender `$CCCC/$CCCC/$FFFF` on Mac OS 9 (Appearance default). DefHilite restores it. A decoder should
  let the caller choose it.
- **Text:** font 0, face 0, mode srcOr, size 0, space extra 0, character extra 0, pen fraction ½ (`$8000`).
- **Text ratio:** numer = toRect size, denom = fromRect size (§7).
- **Clip:** empty until the ClipRgn opcode (§6.5).
- **Pattern origin:** (0, 0).
- **No font-name mappings.**

### 6.2 MapPt, MapRect, ScaleSize (ROM)

Every point is mapped from fromRect to toRect, per axis. For a coordinate `c` on an axis where fromRect spans
`[fromLo, fromHi)` and toRect `[toLo, toHi)`:

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

- The rounding is **half up on the magnitude**, so negative offsets round symmetrically with positive ones.
- MapRect maps top-left and bottom-right as points.
- **Pen and oval sizes (ScaleSize):**
  - if the sizes are equal, the value is unchanged;
  - a size ≤ 0 becomes 0;
  - otherwise the result is `((u16)size × (u16)toSize + fromSize/2) / fromSize`, and at least 1.

### 6.3 MapRgn and MapPoly

- **Regions:** map every inversion point with MapPt, then rebuild the region from the mapped points. If the sizes
  are equal, just offset it.
- **ROM:** the wide-open region, a rectangle of (−32767, −32767, 32767, 32767) with no scan data, is **not mapped**.
- **Polygons:** map every vertex.

### 6.4 Origin

The Origin opcode's operands are `dh` then `dv` (two `i16`, horizontal first: not a Point), as Mac OS 9.0's
DrawPicture reads them. It adds dh to fromRect's left and right and dv to its top and bottom, cumulatively
(each Origin adds to the previous shift until the next DrawPicture). All later coordinates therefore land dh, dv further
up and left. It also:

- adds (dh, dv) to the **pattern alignment** (patAlign; the ROM only, [QUICKDRAW.md](QUICKDRAW.md) §5.6);
- re-maps the current clip (kept in picture coordinates).

### 6.5 Clip

- DrawPicture replaces the port's clip with an **empty** region until the picture's ClipRgn opcode, so a picture without
  one draws nothing, and one arriving mid-picture limits only what follows; from then on the clip is the mapped picture
  clip intersected with the caller's clip [Code] [Verified: Mac OS 9].
  ClassicMac's decoder (a fresh canvas, no caller) draws such a picture anyway [ClassicMac]; `DrawPicture` onto a
  `QuickDrawPort` follows the Mac.
- The clip region is stored in picture coordinates and mapped (§6.3), then intersected with the caller's clip, whenever
  it or fromRect changes (the Origin opcode re-maps it).
- Every drawing operation is limited to that clip and the canvas.

### 6.6 Saving and restoring the port

DrawPicture copies the whole port record at entry and restores it at exit, so the pen (location, fraction, size,
mode, visibility), the patterns, the text font, face, size and mode, the space and character extras, the colours and
the clip come back unchanged, and the pen does not move ([Verified: Mac OS 9] for the pen and its fraction). patAlign, the picture's LineJustify spacing, FractEnable and
FScaleDisable are saved and restored too. The ROM writes OpColor (black) and the highlight colour, and the `$1D`–`$1F`
opcodes, into the real port's colour state and does not restore them; Mac OS 9 plays those into a copy but still
leaves OpColor black [Code]. pnVis is not reset, so a hidden pen hides the picture.

### 6.7 MapFixPt (text positions only, ROM)

Version-2 text positions are mapped with fixed-point precision. For each axis, on a Fixed coordinate `c`:

```
fromSize = (fromHi << 16) - (fromLo << 16)     // 32-bit
toSize   = (toHi << 16) - (toLo << 16)
d = c - (fromLo << 16)
if fromSize != toSize: d = (int)((long)d * toSize / fromSize)   // signed 64/32 divide, truncating toward zero
result = d + (toLo << 16)
```

---

## 7. Text opcodes

- **LongText, DHText, DVText, DHDVText:**
  - Set or move the text location (picture coordinates). The location persists between text opcodes.
  - The pen goes to the mapped location:
    - **version 2:** MapFixPt (§6.7) of `(v + ½, h + pendingFrac)`. The pen is the integer parts, and the pen
      fraction becomes h's fraction.
    - **version 1:** MapPt. The pen fraction keeps whatever the previous text left.
  - pendingFrac resets to ½ at every text opcode.
- **PnLocHFrac:** sets pendingFrac for the **next** text opcode only.
- **ChExtra:** stored as it is (4.12 per point; colour ports only), not through CharExtra [Code]. **LineJustify:** `interCharSpacing` (Fixed per point) is stored.
  The character extra of [QUICKDRAW.md](QUICKDRAW.md) §7.5 is `(ChExtra << 4) + interCharSpacing`, scaled there.
- **SpExtra:** the space extra (Fixed), scaled by the Font Manager ([QUICKDRAW.md](QUICKDRAW.md) §7.4).
- **glyphState:** the third byte turns fractional widths on or off, and the fourth turns scaling off or on ([QUICKDRAW.md](QUICKDRAW.md) §7.3,
  [QUICKDRAW.md](QUICKDRAW.md) §7.4).
- **TxRatio:**

  ```
  numer.v' = numer.v × toRect.height;  denom.v' = denom.v × fromRect.height     (u16 × u16)
  while ((numer.v' | denom.v') & 0xFFFF8000) != 0: numer.v' >>= 1; denom.v' >>= 1
  (the same for h with widths)
  ```

  This replaces the text ratio. Initially the ratio is numer = toRect size, denom = fromRect size.
- **fontName:**
  - Look up the family with that name, as GetFNum does.
  - If it exists, is not family 0, and differs from the picture's number, then **later** TxFont opcodes with the
    picture's number select the named family. The first mapping for a number wins.
  - Recorders write fontName **before** its TxFont.
- **TxMode / TxSize / TxFace / TxFont** are stored as they are. TxSize writes txSize directly, not through TextSize, so
  it does not clear the character extra: ChExtra then TxSize keeps the extra [Code] [Verified: Mac OS 9].

---

## 8. Picture comments

- **ShortComment** carries a u16 kind; **LongComment** a u16 kind, a u16 size and data.
- They do not draw. A decoder should expose them.
- **Kind 224: ICC profile.**
  - Data is a u32 selector followed by payload:
    - 0 = the first chunk;
    - 1 = a continuation;
    - 2 = end (no payload).
  - Concatenate the payloads from selector 0 through the selector-2 comment.

---

## 9. Writing pictures

A picture that every PICT reader, and the ROM, decodes identically:

1. **File header:** 512 zero bytes, for a `.pict` file only.
2. **Picture header:**
   - `u16 0` (picSize);
   - picFrame `(0, 0, H72, W72)`, the image size at 72 dpi: `round(pixels × 72 / dpi)`, at least 1;
   - `0011 02FF`, then `0C00`;
   - `FFFE 0000` (extended version 2);
   - hRes, vRes (Fixed dpi);
   - srcRect `(0, 0, height, width)`;
   - `u32 0`.
3. **Clip:** `0001`, size 10, the rect `(0, 0, height, width)`.
4. **Optional ICC profile:** LongComments of kind 224:
   - selector 0 with the first ≤ 32000 bytes;
   - selector 1 for each further chunk;
   - then selector 2 with no data.
   - Each comment starts word-aligned.
5. **The image** as one bitmap opcode per vertical strip, each word-aligned:
   - **Indexed (1, 2, 4, 8 bits):**
     - opcode `0098` (or `0090` when rowBytes < 8);
     - rowBytes = the even number of bytes holding the strip's pixels.
     - A 1-bit image whose palette is exactly {white, black} is written as a plain **BitMap** (rowBytes without bit
       15, no PixMap fields).
     - Otherwise write a PixMap (`rowBytes | $8000`, bounds, pmVersion 0, packType 0, packSize 0, hRes, vRes,
       pixelType 0, pixelSize, cmpCount 1, cmpSize = pixelSize, three zero longs), then a ColorTable (ctSeed 0,
       ctFlags 0, ctSize n−1, entries `value = i`, RGB × 257).
     - Rows: PackBits.
   - **16-bit:**
     - opcode `009A`, baseAddr `$000000FF`, rowBytes = width × 2 `| $8000`;
     - pixelType 16, pixelSize 16, cmpCount 3, cmpSize 5, **packType 3**;
     - rows PackBits over **words**. Two equal words are already worth a run.
   - **32-bit:**
     - `009A`, rowBytes = width × 4 `| $8000`;
     - pixelType 16, pixelSize 32, cmpSize 8, **packType 4**;
     - cmpCount 3 (planes R, G, B) or 4 (planes A, R, G, B);
     - each row is the planes back to back, each `width` bytes, PackBits-compressed as one line.
     - With rowBytes < 8, write unpacked `A/0, R, G, B` pixels instead.
     - **Mac OS 9 compatibility:** if any row of a strip packs into more than `(n + (n >> 7) + 3) & ~3` bytes (n =
       planes × width), Mac OS 9 misreads it (§10). Write that strip with **packType 1** (unpacked `A/0, R, G, B`
       rows, no byte counts) instead. Incompressible rows always exceed the limit.
   - srcRect = dstRect = the strip's bounds; mode 0 (srcCopy).
   - Each packed row is preceded by its byte count: a u8, or a u16 when rowBytes > 250.
6. **Strips:** rowBytes has only 14 bits, so a strip is at most `$3FFE` bytes wide:
   - indexed: `($3FFE × 8 / bits) & ~15` pixels;
   - 16-bit: `$3FFE / 2`;
   - 32-bit: `$3FFE / 4`.
   - Wider images are split into strips side by side, each with bounds `(0, left, height, left + w)`.
7. Word-align, then `00FF`.

**PackBits encoding:**

- A run of **3 or more** equal units, or 2 or more when packing words, is written as `(1 − count, unit)`.
- Anything else is written in literal blocks `(count − 1, units…)`. A literal block ends where a run of 3 begins.
- At most 128 units per run or block.

---

## 10. Mac OS 9 differences

Mac OS 9's native QuickDraw is a rewrite, not a port. Where it differs from sections 2–9, a picture shown on Mac OS 9
follows the rules below. It still uses the ROM's MapPt, MapRect, ScalePt, FixMul and FixRatio.

- **LineJustify (`$2D`)** is skipped. The inter-character spacing stays 0 for the whole picture.
- **Text positions (MapFixPt):** each axis multiplies by the truncated ratio `(toSize << 16) / fromSize`, rounding
  half up and saturating: `(d × ratio + $8000) >> 16`. The ROM divides exactly. For example, with a 3 → 1 scale, h = 3.0
  maps to 0.99998, so the pen is 0 with fraction `$FFFF`, not 1.0.
- **Origin** does not shift the pattern alignment, so patterns stay fixed to the canvas [Code] [Verified]. The opcode
  `$0200` (QDSetPatternOrigin) sets the port's pattern origin, which only pixel patterns use; 1-bit patterns
  are unchanged [Verified]. The ROM skips it.
- **`$92`/`$93`** are reserved (a word length and data). Only `$90`, `$91` and `$98`–`$9B` draw.
- **Colour tables** are read whenever pixelSize < 9, whatever the opcode, including for a direct opcode and for
  pixel patterns.
- **PixPat types:** 1 and 3 are pixel maps; every other type is an RGB colour.
- **Direct pixel data, packType 0:** 32-bit maps take the 3-byte path, and 16-bit maps are word PackBits (the ROM bug
  is fixed).
- **PackBits:** a flag of `$80` (−128) is a run of 129 copies of the next unit. It is never a no-op.
- **Round rects:** the corner size is clamped to `[0, rect size]` on each axis, and a zero width or height draws a
  plain rect.
- **Component planes (packType 4)** are read through Mac OS 9's own buffers:
  - Always 3 planes, landing on pixel bytes 1–3, unless cmpCount is 4 (then 4 planes, bytes 0–3).
  - Unpacked length per row: `n = rowBytes − rowBytes/4`, or `rowBytes` with 4 planes.
  - The packed-row buffer holds only `P = (n + (n >> 7) + 3) & ~3` bytes. The unpack buffer follows it directly in
    one block that is kept for the whole image.
  - Model it as one array `mem` of `P + n` bytes. Copy each row's packed bytes to `mem[0…]`; a long row spills past
    P. Then unpack, reading `mem[src++]` and writing `mem[P + dst++]` until n bytes are out (`$80` = run of 129).
  - A row packed into more than P bytes therefore reads spilled bytes that its own output has already overwritten.
    For example, a 97-byte row of 96 literals ends with its first output byte, so pixel 31's blue takes pixel 0's
    red. The ROM reads such rows correctly.

---

## 11. Not covered

What ClassicMac does not reproduce in playing pictures; the drawing's own gaps are in [QUICKDRAW.md](QUICKDRAW.md)
section 10, QuickTime's in [QUICKTIME.md](QUICKTIME.md).
