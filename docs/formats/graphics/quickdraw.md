# QuickDraw drawing

How QuickDraw draws, completely enough to reproduce it pixel for pixel: the structures it draws with (ports, regions,
patterns, pixel maps, colour tables), fixed-point arithmetic, how shapes become pixels, patterns and transfer modes,
CopyBits, bitmap text and the Font Manager, and drawing on indexed and 16-bit screens. It is not a file format but the
rule book behind several: pictures ([pict.md](pict.md)) are recordings of these calls, and icons, cursors and patterns
are drawn by them. ClassicMac draws by these rules for pictures and resources, and through `QuickDrawPort` for
callers, as the 68k ROM or Mac OS 9 does.

| | |
| --- | --- |
| Identified by | Not a file format: the drawing behind `PICT` ([pict.md](pict.md)), icons, cursors and patterns ([icons.md](../resources/icons.md), [cursors.md](../resources/cursors.md), [patterns.md](../resources/patterns.md)) and text in bitmap fonts ([bitmap-fonts.md](../resources/bitmap-fonts.md)) |
| ClassicMac | Draws; `ClassicMac.Graphics.QuickDraw` (`QuickDrawPort`, `QuickDrawOptions`, `FontLibrary`) |
| Verified against | Mac OS 9.0 in SheepShaver: the golden pictures, the screen depths (§4.6) and the rules tagged [Verified] |
| Sources | *Inside Macintosh: Imaging With QuickDraw* and *Text*; the 68k ROM's QuickDraw and Font Manager (ROM `$077D`); Mac OS 9.0's native QuickDraw (NQD) and FontManager. Other implementations (behaviour only): Executor |

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

### 1.1 Coordinates and numbers

The README's conventions hold ([README.md](../README.md#conventions)). In addition [Doc: Imaging With QuickDraw]:

- A Rect's right and bottom are exclusive: it covers `right − left` × `bottom − top` pixels, and it is empty when
  `bottom ≤ top` or `right ≤ left`.
- `RGBColor` is three `u16`: red, green, blue. An 8-bit component is the high byte.
- The grid lines lie between pixels: pixel (h, v) is the one below and to the right of grid point (h, v).
- QuickDraw computes coordinates in 16-bit words, and several results below depend on that wrap-around. "(short)"
  means: truncate to 16 bits and sign-extend.
- Mode, opcode and constant values are given in hex (`$xx`) or decimal as noted.

### 1.2 The port

A colour port (CGrafPort); the rules that use each field are in §2 [Doc: Imaging With QuickDraw] [Verified: every
offset, against the field offsets of the published interfaces]. The ROM tells a colour port by the sign of portVersion and
reads grafVars at +$08 [Code: 68k ROM]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | device | |
| +$02 | 4 | portPixMap | Handle to the PixMap drawn into (§1.7) |
| +$06 | 2 | portVersion | Top two bits set in a colour port |
| +$08 | 4 | grafVars | Handle to the GrafVars below |
| +$0C | 2 | chExtra | Character extra, 4.12 per point (§2.24) |
| +$0E | 2 | pnLocHFrac | The pen's horizontal fraction (§2.4) |
| +$10 | 8 | portRect | Rect, local coordinates (SetOrigin, §2.4) |
| +$18 | 4 | visRgn | Region handle |
| +$1C | 4 | clipRgn | Region handle |
| +$20 | 4 | bkPixPat | Background pattern (§1.6) |
| +$24 | 6 | rgbFgColor | Foreground RGBColor |
| +$2A | 6 | rgbBkColor | Background RGBColor |
| +$30 | 4 | pnLoc | Point |
| +$34 | 4 | pnSize | Point |
| +$38 | 2 | pnMode | Transfer mode (§2.9) |
| +$3A | 4 | pnPixPat | Pen pattern |
| +$3E | 4 | fillPixPat | Fill pattern |
| +$42 | 2 | pnVis | Pen visibility; drawing is hidden while negative (§2.4) |
| +$44 | 2 | txFont | Font family |
| +$46 | 1 | txFace | Style; a filler byte follows |
| +$48 | 2 | txMode | Text transfer mode |
| +$4A | 2 | txSize | Text size; 0 is the system font's size |
| +$4C | 4 | spExtra | Fixed: extra width of the space |
| +$50 | 4 | fgColor | Classic colour (§1.9) |
| +$54 | 4 | bkColor | Classic colour |
| +$58 | 2 | colrBit | |
| +$5A | 2 | patStretch | |
| +$5C | 12 | picSave, rgnSave, polySave | Recording handles |
| +$68 | 4 | grafProcs | Bottleneck procedures |

GrafVars (rgbOpColor and rgbHiliteColor [Code: 68k ROM]: the OpColor and HiliteColor opcodes and DefHilite write
them, only in a colour port; the Palette Manager fields [Doc: Imaging With QuickDraw]):

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 6 | rgbOpColor | Weight or pin colour of the arithmetic modes (§2.11) |
| +$06 | 6 | rgbHiliteColor | Highlight colour (§2.12) |
| +$0C | 4 | pmFgColor | Palette Manager |
| +$10 | 2 | pmFgIndex | |
| +$12 | 4 | pmBkColor | |
| +$16 | 2 | pmBkIndex | |
| +$18 | 2 | pmFlags | |

The pattern alignment patAlign (§2.13) and the HiliteMode flag (§2.12) are global, not in the port.

### 1.3 Regions

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | rgnSize | `u16`: bytes, including this word and the bbox. Mask off bit 15 |
| +$02 | 8 | rgnBBox | Rect |
| +$0A | rgnSize − 10 | Scan data | `i16` words (a pad byte follows in a picture when rgnSize is odd) |

[Doc: Imaging With QuickDraw]

- A region of size 10 (no scan data) is its bounding rectangle.
- Otherwise the scan data is a list of rows, then a final `$7FFF`. Each row is its `y`, then x values in increasing
  order, then `$7FFF`.
- The x values are inversion points: at each (x, y) the inside/outside state of every pixel at or right of x and at or
  below y flips. So a pixel is inside when the count of inversion points (x′, y′) with x′ ≤ x and y′ ≤ y is odd. XOR
  the x lists of successive rows to get each row's span boundaries.
- To write a region, emit only the points where a band's spans change against the band above it: the symmetric
  difference of the two span-boundary sets.

[Code: 68k ROM]

### 1.4 Polygons

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | polySize | `u16`: bytes, including this word |
| +$02 | 8 | polyBBox | Rect |
| +$0A | 4 × n | Points | n = `(polySize − 10) / 4` |

Leftover bytes of polySize are skipped. A polygon whose bounding box is empty draws nothing. [Doc: Imaging With
QuickDraw] [Reference: Executor]

### 1.5 Patterns

A 1-bit pattern is 8 bytes, one per row, most significant bit leftmost. A 1 bit draws the foreground colour and a 0
bit the background colour (§2.10); the pattern tiles the plane from the pattern alignment (§2.13). [Doc: Imaging With
QuickDraw]

### 1.6 Pixel patterns

As a picture stores one (BkPixPat, PnPixPat, FillPixPat; the `ppat` resource is [patterns.md](../resources/patterns.md))
[Doc: Imaging With QuickDraw]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | patType | `u16`: 1 a pixel map, 2 an RGB colour |
| +$02 | 8 | pat1Data | The 1-bit pattern, for 1-bit ports |
| +$0A | 6 | RGBColor | Type 2 only. Six bytes (some readers skip 5) |
| +$0A | | Pixel map | Other types: a PixMap without baseAddr (rowBytes, bounds, the fields of §1.7), a ColorTable (§1.8), then its pixel data ([pict.md §2.4](pict.md#24-pixel-data)) |

- A type 2 pattern draws as that solid colour (on indexed screens through PatDither, §4.9).
- A type 1 pattern tiles its pixel map: pattern pixel (x, y) is `((x + patAlign.h) mod w, y mod h)`; only the
  horizontal alignment applies in the ROM (§2.13). [Code: 68k ROM]

### 1.7 BitMaps and PixMaps

A BitMap is baseAddr, rowBytes and bounds; 1 bit per pixel, 0 white and 1 black. A PixMap adds, after rowBytes and
bounds [Doc: Imaging With QuickDraw]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | pmVersion | `u16` |
| +$02 | 2 | packType | `u16` ([pict.md §2.4](pict.md#24-pixel-data)) |
| +$04 | 4 | packSize | `u32` |
| +$08 | 4 | hRes | Fixed |
| +$0C | 4 | vRes | Fixed |
| +$10 | 2 | pixelType | `u16`: 0 indexed, 16 direct (RGBDirect) |
| +$12 | 2 | pixelSize | `u16`: 1, 2, 4, 8, 16 or 32; anything else is not a QuickDraw depth |
| +$14 | 2 | cmpCount | `u16`: 1 indexed, 3 RGB, 4 with alpha |
| +$16 | 2 | cmpSize | `u16` |
| +$18 | 4 | planeBytes | `u32` |
| +$1C | 4 | pmTable | `u32`: the colour table's handle in memory |
| +$20 | 4 | pmReserved | `u32` |

rowBytes's bit 15 marks a PixMap and its low 14 bits are the row length. In memory (after unpacking):

- rows of rowBytes bytes;
- indexed pixels packed most significant first;
- 16-bit pixels big-endian `xRRRRRGGGGGBBBBB`; a 5-bit component expands to 8 bits as `(c << 3) | (c >> 2)`;
- 32-bit pixels four bytes: alpha (or pad), R, G, B.

### 1.8 Colour tables

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | ctSeed | `u32` |
| +$04 | 2 | ctFlags | `u16`: bit 15 set, a device table |
| +$06 | 2 | ctSize | `u16`: entries − 1 |
| +$08 | 8 × n | Entries | `u16` value, `u16` red, `u16` green, `u16` blue |

In a device table each entry is the pixel value of its position; otherwise each entry's value field is its pixel
value. Pixel values the table does not list are black. [Doc: Imaging With QuickDraw] The `clut` resource is
[palettes.md](../resources/palettes.md).

### 1.9 Classic colours

The classic colour constants of ForeColor and BackColor (and a picture's FgColor and BkColor). On a colour port they
set these RGB colours, from the QDColors table (`clut` 127 in the ROM) [Doc: Imaging With QuickDraw] [Code: 68k ROM]:

| Constant | Colour | RGB (16-bit) |
| --- | --- | --- |
| 30 | whiteColor | FFFF FFFF FFFF |
| 33 | blackColor | 0000 0000 0000 |
| 69 | yellowColor | FC00 F37D 052F |
| 137 | magentaColor | F2D7 0856 84EC |
| 205 | redColor | DD6B 08C2 06A2 |
| 273 | cyanColor | 0241 AB54 EAFF |
| 341 | greenColor | 0000 8000 11B0 |
| 409 | blueColor | 0000 0000 D400 |

Any other value is black as a foreground colour and white as a background colour.

### 1.10 Fonts

Text in a picture or port is a font number, size, style and mode, then characters in Mac OS Roman; drawing it exactly
needs the Mac's bitmap fonts. QuickDraw reads the strikes (`NFNT`, `FONT`) of [bitmap-fonts.md](../resources/bitmap-fonts.md)
and the family records (`FOND`) of [font-families.md](../resources/font-families.md), found in resource forks
([resource-fork.md](../resources/resource-fork.md)). Beyond those documents:

- The ROM and Mac OS 9 read three strike fields differently: the depth bits of fontType (2–4 in the ROM, 2–3 on Mac
  OS 9), nDescent as the high word of owTLoc (the ROM when 0 or more), and where the location table is (the ROM right
  after the strike, Mac OS 9 just before the offset/width table). [Code]
- rowWords: Mac OS 9 ignores the top bit; with it set, the ROM's strike is unusable. [Code]
- A family association whose style has a non-zero high byte is a colour (depth) variant; 1-bit text ignores it.
  [Code: 68k ROM]
- Character c uses table slot `c − firstChar`; characters outside [firstChar, lastChar], or with an offset/width of
  −1, use the missing symbol, slot `lastChar − firstChar + 1`. [Doc: Text]

## 2. Reading

§2 gives the 68k ROM's QuickDraw and Font Manager [Code: 68k ROM], marked where surprising. §4.1–§4.5 list every point
where Mac OS 9 differs, and §4.6–§4.11 drawing on screens of fewer than 32 bits.

### 2.1 Fixed-point arithmetic

These must be exact: they decide pixel positions.

- **FixMul(a, b):**
  1. If b = 1.0 return a; if a = 1.0 return b.
  2. Otherwise take `p = (long)a × b`.
  3. If p ≥ 2⁴⁷ or p < −2⁴⁷, saturate to `$7FFFFFFF` or `$80000000` by the operands' signs.
  4. Otherwise `r = p >> 16` (floor). Add 1 when bit 15 of p is set and either r ≥ 0 or the low 15 bits are non-zero.
     This rounds half away from zero.
- **FixRatio(n, d)** (`i16` operands): d = 0 gives `$7FFFFFFF`, or `$80000001` when n < 0; n = d gives `$10000`;
  n = −32768 and d = −1 give `$80000000`; otherwise `(n << 16) / d`, truncated toward zero.
- **FixRound(x):** x ≥ `$7FFF8000` gives 32767; otherwise `(x + (x ≥ 0 ? $8000 : $7FFF)) >> 16` as an `i16`. This
  rounds halves away from zero.
- **SlopeFromAngle(a):** reduce a (`i16`) modulo 180 into [0, 180); return `-Tangent[a]` for a ≤ 90, else
  `Tangent[180 − a]`. `Tangent[k]` is QuickDraw's table of tan(k°) as Fixed, not exactly rounded tangents. Use exactly
  these 91 values:

```
00000000 00000478 000008F1 00000D6B 000011E7 00001666 00001AE8 00001F6F 000023FA 0000288C
00002D24 000031C3 0000366A 00003B1A 00003FD4 00004498 00004968 00004E44 0000532E 00005826
00005D2D 00006245 0000676E 00006CAA 000071FB 00007760 00007CDC 00008270 0000881E 00008DE7
000093CD 000099D2 00009FF7 0000A640 0000ACAD 0000B341 0000B9FF 0000C0E9 0000C802 0000CF4E
0000D6CF 0000DE8A 0000E681 0000EEB9 0000F737 00010000 00010919 00011287 00011C51 0001267F
00013117 00013C22 000147AA 000153B9 0001605B 00016D9B 00017B89 00018A35 000199AF 0001AA0E
0001BB68 0001CDD6 0001E177 0001F66E 00020CE1 000224FE 00023EFC 00025B19 0002799F 00029AE7
0002BF5B 0002E77A 000313E3 00034556 00037CC7 0003BB68 000402C2 000454DB 0004B462 00052501
0005ABD9 00065051 00071D88 000824F3 000983AD 000B6E17 000E4CF5 001314BD 001CA2D7 00394A30
7FFFFFFF
```

### 2.2 The drawing pipeline

These rules draw into a 32-bit direct destination; colours are 8 bits per component, the high byte of each 16-bit
QuickDraw component. Every shape is converted to the exact set of pixels QuickDraw touches, a region (§2.5–§2.8),
which is then:

1. intersected with the destination and the clip;
2. painted through a pattern and a transfer mode (§2.9–§2.13).

Bitmaps go through CopyBits (§2.14) and text through the character generator (§2.22), which itself ends in CopyBits.

### 2.3 Verbs

| Verb | Region | Pattern | Mode |
| --- | --- | --- | --- |
| frame (+0) | The shape's outline, drawn with the pen | Pen pattern | Pen mode |
| paint (+1) | The shape | Pen pattern | Pen mode |
| erase (+2) | The shape | Background pattern | patCopy |
| invert (+3) | The shape | Black | patXor, or hilite when pending (§2.12) |
| fill (+4) | The shape | Fill pattern | patCopy |

- **"Same" opcodes** in a picture reuse the last rect (shared by rects, round rects, ovals and arcs), the last polygon
  or the last region, each kept in picture coordinates.
- **Rect-based shapes** draw nothing when the rect, mapped, is empty.
- **Polygons with fewer than two points** draw nothing.
- **Framing a polygon** draws a line (§2.7) between each pair of consecutive vertices and does not close it.
- **Pen modes on ovals, round rects and arcs** (DrawArc `$FFC93E8C`; Mac OS 9 the same): frame and paint force bit 3
  of the pen mode and draw only modes 8–15, 40–47 and 58 (hilite). Any other mode draws nothing: 16–31, 48–57
  (grayishTextOr included), 59–63, and 64 and up (ditherCopy). Elsewhere ditherCopy is treated as copy.
- **HiliteMode** applies to the next drawing operation only. Every drawing operation clears it, whether or not it used
  it.

### 2.4 The port: origin, pen visibility, pen fraction

- **SetOrigin(h, v)** offsets portRect, the pixel map's bounds and visRgn, so the destination's top-left pixel gets
  local coordinates (h, v). The clip region, patAlign and the pen are untouched: they keep their local coordinates and
  so move on the screen. Nothing happens when the origin is unchanged [Code].
- **pnVis** (HidePen decrements it, ShowPen increments it): while negative, lines, every frame, paint, erase, invert
  and fill verb, text pixels, CopyBits into the port's own pixels and ScrollRect draw nothing. The pen still moves:
  LineTo sets pnLoc, and DrawString advances it. CopyBits into other bitmaps, CopyMask and CopyDeepMask, region and
  polygon recording and picture recording (unless pnVis < −1) are not suppressed [Code]. The ROM and Mac OS 9 agree.
- **pnLocHFrac** (colour ports) is reset to `$8000` by MoveTo, Move, the line path (LineTo, Line, FramePoly, also with
  the pen hidden) and InitCPort/OpenCPort; not by SetOrigin, SetPort, the pen-state calls, PenNormal, HidePen, ShowPen
  or PortChanged. DrawString adds the fraction of the text's width to it, the carry going into pnLoc.h [Code].

### 2.5 Rects and regions

- **Rect:** its pixels.
- **Frame of a rect or region:** the shape minus the shape inset by the pen. Inset a region by (penH, penV):
  1. shrink every span by penH at both ends, dropping spans that vanish;
  2. erode the result vertically by penV: a pixel stays only if the pixels penV above and below it are also inside.

[Reference: Executor]

### 2.6 Ovals, round rects and arcs

One scan converter, DrawArc, handles all three: an ellipse of `ovalWidth × ovalHeight` (the whole rect for ovals and
arcs, the corner oval for round rects: OvSize, not halved), stepped one scan line at a time from the rect's top, its
left and right edges kept in 16.16 and moved in half-pixel steps. The edges are exact only with this incremental
method; no closed form reproduces them.

**Ellipse setup** for rect (top, left, bottom, right):

```
w = clamp(ovalWidth, 0, (short)(right - left))
h = clamp(ovalHeight, 0, (short)(bottom - top))
L = (left << 16) + w * 0x8000
R = (right << 16) - w * 0x8000 + 0x8000
ratio = FixRatio(h, w)                      // truncating
term  = (long)ratio * ratio                  // 64-bit, 32.32
step  = 2 * term
sum   = 0
oddY  = (short)(1 - h)
target = 2*h - 1
```

**Per scan line** (only rows inside [top, bottom)):

```
y = oddY;  oddY = (short)(oddY + 2)
while (int)(sum >> 32) < target:  R += 0x8000; L -= 0x8000; sum += term; term += step
while (int)(sum >> 32) > target:  R -= 0x8000; L += 0x8000; term -= step; sum -= term
target -= 4 * (short)(y + 1)
row's run = [(short)(L >> 16), (short)(R >> 16))
```

Examples: 4 × 4 → `[1,3) [0,4) [0,4) [1,3)`; 7 × 7 → `[2,5) [1,6) [0,7) [0,7) [0,7) [1,6) [2,5)`; 16 × 16 →
`[5,11) [3,13) [2,14) [1,15) [1,15) [0,16)×6 [1,15) [1,15) [2,14) [3,13) [5,11)`. Flat ovals are vertically
asymmetric: 16 × 3 → `[2,14) [0,16) [1,15)`. Runs may extend past the rect and are clipped to it.

**Round rects:**

- Rows in `[holdTop, holdBottom)` do not step the ellipse, so the sides stay straight, with
  `holdTop = top + ((short)ovalHeight >> 1)` and `holdBottom = holdTop + (bottom − top) − ovalHeight` (not clamped).
- With ovalHeight ≤ 0 and ovalWidth > 0, every row is `[left + ⌊ow/2⌋, right − ⌊ow/2⌋)`.

**Frames:**

- The inner ellipse uses the rect inset by the pen, `(top+penV, left+penH, bottom−penV, right−penH)`, and an oval
  size of `outer − 2 × pen` on each axis.
- If the inset rect is empty, the shape is painted solid.
- On rows inside the inner rect, draw `[outerL, innerL)` and `[innerR, outerR)`; on other rows the whole outer run.

**Arcs:**

- Angles are in degrees, 0 at 12 o'clock, clockwise. A negative arcAngle becomes `start += arc; arc = −arc`. arcAngle
  0 draws nothing; arcAngle ≥ 360 draws the full oval.
- Two rays bound the wedge:

  ```
  start = (short)start mod 360 (made non-negative);  stop = (start + arc) mod 360
  midRow = (short)(top + bottom) >> 1;  midCol = (short)(left + right) >> 1
  aspect = FixRatio((short)(right-left), (short)(bottom-top))
  slope1 = FixMul(SlopeFromAngle(start), aspect);  slope2 = FixMul(SlopeFromAngle(stop), aspect)
  halfH  = (u16)(bottom - top) >> 1
  ray_i  = (midCol << 16) - Times(slope_i, halfH)
  flag1  = start < 180 ? start - 90 : 270 - start
  flag2  = stop  < 180 ? stop  - 90 : 270 - stop
  ```

- **Times(s, n)**, the 68000's multiply of a Fixed by a small integer: `low = (u16)s × n` (unsigned 32-bit),
  `high = (short)((short)(s >> 16) × n)`; the result's high word is `(u16)((low >> 16) + (u16)high)`, its low word
  `low & $FFFF`. The high word wraps, so steep angles in huge ovals wrap as on the Mac (89–91° in a 1200 × 1200 oval).
- **Hidden top half:** with arc < 180 the top half is hidden when `(short)(flag1 | flag2) ≥ 0`; with arc = 180, when
  start = 90.
- For each row from top to bottom, first step the outer and inner ellipses, then:
  1. **At midRow:** negate both flags and un-hide. If arc < 180 and `(short)(flag1 | flag2) ≥ 0`, stop: the arc lay in
     the top half. If arc = 180 and start = 270, stop. Otherwise swap the two rays (position, slope and flag).
  2. **Otherwise, if not hidden:** emit the row's runs, cutting the left side at ray 1 while `flag1 < 0` and the right
     side at ray 2 while `flag2 < 0`. Ray positions are `(short)(ray >> 16)`, at the row's top.
     - `cutLeft = clip1 && outerL < ray1 ? ray1 : outerL`; `cutRight = clip2 && outerR > ray2 ? ray2 : outerR`.
     - **Hollow rows:** `innerLeft = clip2 && innerL > ray2 ? ray2 : innerL`,
       `innerRight = clip1 && innerR < ray1 ? ray1 : innerR`. If `cutLeft < cutRight`, emit `[cutLeft, innerLeft)` and
       `[innerRight, cutRight)`. Otherwise, for a reflex wedge (`(short)(flag1 & flag2) < 0` and arc > 180): if
       `innerLeft == cutRight` emit `[cutLeft, innerL)`, else if `cutLeft == innerRight` emit `[innerR, cutRight)`;
       then emit `[outerL, innerLeft)` and `[innerRight, outerR)`.
     - **Solid rows:** if `cutLeft < cutRight`, emit `[cutLeft, cutRight)`. Otherwise, for a reflex wedge, emit
       `[outerL, cutRight)` and `[cutLeft, outerR)`.
  3. Advance both rays by their slopes.
- All runs are clipped to [left, right).

**Filling through DrawArc:** ovals, round rects and arcs have no solid-pattern shortcut, so hilite always works per
pixel wherever the pattern pixel is not the background colour (contrast §2.12). Source modes act as the corresponding
pattern modes; only modes 8–15, 40–47 and 58 draw once bit 3 is forced (§2.3).

### 2.7 Lines

The pen is a `penH × penV` rectangle hanging below and right of the path, swept from (h1, v1) to (h2, v2) inclusive:

- **Bounds:** `top = min(v1, v2)`, `bottom = max(v1, v2) + penV`, `left = min(h1, h2)`, `right = max(h1, h2) + penH`.
  If this rect is empty, the line draws nothing.
- **Horizontal or vertical lines:** that rectangle.
- **Otherwise, a pen dimension ≤ 0** draws nothing.
- **Otherwise:** order the ends so v1 < v2, and emit one run per row:

```
slope = FixRatio((short)(h2 - h1), (short)(v2 - v1))
rise  = FixMul(penV << 16, slope)
runL  = (h1 << 16) + 0x8000 + (slope >> 1)
runR  = runL + (penH << 16)
if slope >= 0:  runL -= rise;  if slope < 0x10000: runL += slope  else: runR -= 0x10000
else:           runR -= rise;  if slope < -0x10000: runL += 0x10000 else: runR += slope
for row in [top, bottom):  emit [(short)(runL >> 16), (short)(runR >> 16)) clipped to [left, right); runL += slope; runR += slope
```

A Boolean pen mode m < 32 becomes the pattern mode `(m mod 64) | 8`; other modes are used as they are.

### 2.8 Polygons

A filled polygon is the region QuickDraw records for its edges, closed from the last point to the first:

- **A horizontal edge** adds the inversion-point pair (h1, v) and (h2, v).
- **A vertical edge** adds nothing.
- **Any other edge** (ordered so v1 < v2):

```
slope = FixRatio((short)(h2-h1), (short)(v2-v1))
h = (h1 << 16) + 0x8000 + (slope >> 1)
if slope >= 0: { if slope < 0x10000: h += slope }
else if slope < -0x10000: h += 0x10000
prev = h1; v = v1
do { cur = (short)(h >> 16); if cur != prev: toggle points (prev, v) and (cur, v); prev = cur; v++; h += slope } while v != v2
if prev != h2: toggle (prev, v) and (h2, v)
```

Build the region from all the points; a point toggled twice cancels.

### 2.9 Transfer modes

| Modes | Names |
| --- | --- |
| 0–7 | srcCopy, srcOr, srcXor, srcBic, notSrcCopy, notSrcOr, notSrcXor, notSrcBic |
| 8–15 | patCopy, patOr, patXor, patBic, notPatCopy, notPatOr, notPatXor, notPatBic |
| 32–39 | blend, addPin, addOver, subPin, transparent, addMax, subOver, adMin |
| 40–47 | The arithmetic modes of 32–39 with bit 3 set |
| 49 | grayishTextOr (text only, §2.23) |
| 50 | hilite (58 as a pattern mode) |
| 64 | ditherCopy (a bit) |

Normalization [Doc: Imaging With QuickDraw] [Code: 68k ROM]:

1. Clear bit 6.
2. Modes ≥ 50 are hilite.
3. 49 is srcOr.
4. 32–47 become `mode & ~8`.
5. Otherwise take `mode & 7`; pattern and source variants are the same operation for 1-bit data.
6. With HiliteMode pending, srcXor and patXor become hilite.

### 2.10 Boolean modes

**1-bit sources and 1-bit patterns:** a pixel (1 "on", black) is colorized with the foreground colour F and background
colour B. For the not modes, first swap on and off:

| Mode & 3 | Result |
| --- | --- |
| copy | On → F, off → B |
| or | On → F, off → unchanged |
| xor | On → invert the destination (255 − each component), off → unchanged |
| bic | On → B, off → unchanged |

The arithmetic, transparent and hilite modes use the colorized pixel (F for on, B for off) as a colour source (§2.11).

**Colour sources** (a pixel s of a deep bitmap or colour pattern, destination d) work bitwise on 24-bit RGB values. The
ROM's direct loops work on inverted values, which gives:

| Mode | Result |
| --- | --- |
| srcCopy | `(s & B) \| (~s & F)` |
| srcOr | `(~s & F) \| (s & d)` |
| srcXor | `d ^ ~s` |
| srcBic | `(~s & B) \| (s & d)` |
| notSrcCopy | `(~s & B) \| (s & F)` |
| notSrcOr | `(s & F) \| (~s & d)` |
| notSrcXor | `d ^ s` |
| notSrcBic | `(s & B) \| (~s & d)` |

With the default black foreground and white background, srcCopy copies, srcOr is an AND, and so on.

**Pixel patterns** (types 1 and 2) in Boolean modes instead treat the pattern pixel P as a value with fore all ones and
back 0 (the not modes invert P first):

| Mode & 3 | Result |
| --- | --- |
| copy | P |
| or | `d \| P` |
| xor | `d ^ P` |
| bic | `d & ~P` |

### 2.11 Arithmetic modes

Per 8-bit component, with s the source and d the destination. The weight or pin comes from the OpColor component `w`
(16-bit); a zero component counts as 1, and `pin = w >> 8`.

| Mode | Component |
| --- | --- |
| blend | `(s·w + d·(65536 − w)) >> 16`, truncating. When all three weights are equal and are `$7FFF` or `$8000`, the exact average `(s + d) >> 1` |
| addPin | `r = s + d`; `r > 255 or r > pin ? pin : r` |
| addOver | `(s + d) & 255` |
| subPin | `r = d − s`; `r < 0 or r < pin ? pin : r` |
| subOver | `(d − s) & 255` |
| addMax | `max(s, d)` |
| adMin | `min(s, d)` |
| transparent | s, except that pixels whose RGB equals the background colour are not drawn |

### 2.12 Hilite

- A source pixel equal to the background colour is not drawn.
- Otherwise a destination pixel equal to the background colour becomes the highlight colour, one equal to the
  highlight colour becomes the background colour, and any other is unchanged.

[Reference: Executor] [Code: 68k ROM]

**Rects, polygons and regions filled through RgnBlt:** when the area is not a single rectangle and the pattern is a
1-bit pattern whose rows are all `$00` or `$FF`, with at least one `$FF` row, hilite fills the whole area as if the
pattern were solid black. BitBlt (a single rectangle) and DrawArc (ovals, round rects, arcs) do not. [Code: 68k ROM]

### 2.13 Pattern placement

A pattern's phase is the port's local coordinates plus patAlign, for every verb (rects, regions, ovals, arcs,
polygons, lines; PatExpand) [Code]. For pixel (x, y) in local coordinates:

- **1-bit patterns:** row `(y + patAlign.v) & 7`, bit `(x + patAlign.h) & 7`.
- **Pixel patterns:** `((x + patAlign.h) mod w, y mod h)`; patAlign.v is not applied.

So SetOrigin (§2.4) shifts patterns on the screen. patAlign changes only in InitGraf, DrawPicture (saved, zeroed and
restored) and the Origin opcode of a picture ([pict.md §2.9](pict.md#29-origin)); PortChanged does nothing [Code].

### 2.14 CopyBits

CopyBits (and a picture's bitmap opcodes) transfer srcRect of a pixel map to the (mapped) dstRect, through the mask
region (the picture's Rgn variants) intersected with the clip, in the given mode; bit 6 (ditherCopy) is ignored on a
32-bit destination. Nothing is drawn if either rect is empty.

When srcRect and dstRect are the same size, each destination pixel takes the source pixel at the same offset. Source
pixels outside the bitmap's bounds are skipped (the destination is left unchanged); unscaled copies read only pixels
inside the bitmap.

### 2.15 Vertical scaling

For each destination row, a group of source rows is merged:

```
start = (srcH > dstH && srcH % dstH == 0) ? srcH - 1 : (u16)srcH >> 1     // exact integer shrinks start at srcH-1
error = -start;  row = srcTop (relative to the bitmap's top);  k = 0
while row < bitmapHeight:
    group = [row++]; error += dstH
    while error <= 0 and row < bitmapHeight: group += row++; error += dstH
    do: dstRow[k++] = group; if k == dstH: done; error -= srcH  while error >= 0
```

Destination rows left without a group, because the source ran out, are not drawn. The start of `srcH − 1` for exact
integer shrinks decides which rows merge: 6 → 2 merges {0,1,2} {3,4,5}, not {0,1} {2,3,4} (§4.12).

### 2.16 Horizontal scaling

**1-bit sources** use QuickDraw's special row scalers. Destination column j takes source column(s):

| dst/src | Rule |
| --- | --- |
| ×2, ×4, ×8, ×16 | `j / n` |
| ×1.5 | Per source pair a, b: a b b (abcd → abbcdd), `2·(j/3) + (j mod 3 ≠ 0)` |
| ×3, ×6 | `j / n` |
| Any whole multiple of 8 | `j / n` |
| Other enlargements | `((f >> 1) + j·f) >> 16` with `f = FixRatio(srcW, dstW) & $FFFF` |
| ×½, ×¼ | OR of source columns `2j, 2j+1` / `4j … 4j+3` |
| ×¾ | Per 4 source columns a b c d: a, b\|c, d |
| Other shrinks | Source column i lands on `((f >> 1) + i·f) >> 16` with `f = FixRatio(dstW, srcW) & $FFFF`; the columns landing on one destination column are ORed |

- The ratio is identified by `FixRatio(srcW, dstW) & $FFFF` for enlargements: `$8000`, `$4000`, `$2000`, `$1000`,
  `$AAAA`, `$5555`, `$2AAA`; for shrinks by `FixRatio(dstW, srcW) & $FFFF`: `$8000`, `$4000`, `$C000`.
- Merged 1-bit pixels are ORed: black wins, over both merged rows and merged columns.
- Scaled 1-bit copies read source memory as StretchBits' row buffer does: each source row is read linearly from
  srcRect's left for `32 × ceil(srcW / 32)` bits; bits past srcRect's right edge (up to the long boundary) come from
  the row's bytes, and a row that runs past the end of the data reads as 0.

**Deeper sources** use the plain fraction stepper for every ratio, with no special cases, so ×1.5 is not abbcdd:

- enlarging: destination j copies source `((f >> 1) + j·f) >> 16`, `f = FixRatio(srcW, dstW) & $FFFF`;
- shrinking: source i lands on `((f >> 1) + i·f) >> 16`, `f = FixRatio(dstW, srcW) & $FFFF`.

### 2.17 Merging deep pixels

Merging happens at the source depth, before colour conversion: rows first (per column), then columns.

- **2–8 bits:** the largest pixel index in the group, not an average.
- **16 and 32 bits:** a truncated per-component average: each column's rows `sum / n`, then the columns `sum >> 1` for
  two columns, else `sum / columns`. 16-bit components are averaged as 5-bit values, then expanded
  `(c << 3) | (c >> 2)`.

### 2.18 Colour conversion

- 1-bit sources with the white/black palette are colorized with F and B (§2.10).
- Other indexed pixels take their colour table's colour.
- 16-bit pixels expand 5 → 8 bits.
- 32-bit pixels use their R, G, B.

The colour is then transferred by §2.10 (Boolean modes) or §2.11–§2.12 (arithmetic, transparent, hilite).

### 2.19 Alpha

QuickDraw ignores alpha. A 32-bit, cmpCount 4 source copied with srcCopy carries its alpha byte into the destination's
pad byte only for unmerged pixels: a pixel formed by averaging rows or columns gets alpha 0.

### 2.20 Choosing a font

The Font Manager's inputs: the family (TxFont, after font-name mapping, [pict.md §2.13](pict.md#213-text-opcodes)),
the size (TxSize; 0 means 12), the face (TxFace), the text ratio numer/denom, the space extra, and the
fractional-widths and scaling-disabled flags (glyphState). The screen is 80 × 80 dpi to the Font Manager, a ratio
of 1.

1. **The size to search for:** `searchSize = FixRound(FixMul(FixRatio(numer.h, denom.h), size << 16))`. Only the
   horizontal ratio is used.
2. **The families to try, in order:** the family itself (family 0 is the system font; family 1 the application font,
   normally 3, Geneva); then for families ≥ `$4000` the system font, then the application font; otherwise the
   application font, Geneva (3), then the system font.
3. **A family with a FOND**, by its association entries:
   1. **Exact:** entries of searchSize. Choose the style variant (below) and load its resource, `NFNT` of that id
      first, then `FONT`. If it loads, done; if it is missing, go on.
   2. **Outline:** if the table has any size-0 entry, the family is TrueType: stop, the text goes to the outline
      fallback. Double and half sizes are never tried.
   3. **Double**, then **half** (only if searchSize is even), as for exact. Skipped when scaling is disabled.
   4. **Nearest:** scan the entries in table order, skipping a leading size-0 entry, and keep the entry whose distance
      `|searchSize − size|` is ≤ the best so far, so later entries win ties (in an ascending table, the larger size).
      Sizes are taken from the table whether or not their resource exists. With scaling disabled, stop at the first
      larger size once a smaller one has been kept: the result is the nearest smaller size, and a larger size only
      when no smaller one exists. If the winner's resource is missing, the FOND search fails (step 4); the next
      nearest size is not tried.
4. **Old-style fonts** (the FOND search failed or there is no FOND; only families < `$200`):
   `s = (searchSize == 0 ? 1 : searchSize) & $7F`; try `FONT` id `family × 128 + t` for t = s, then 2s (if s < 64),
   then s/2 (if s is even), then s+1 … 127, then s−1 … 1. With scaling disabled the downward scan comes first:
   s−1 … 1, then s+1 … 127. A `FONT` shorter than `$24` bytes counts as missing. The remaining style is the whole face.
5. If nothing is found in any family, the text goes to the outline fallback.

**Style variant within a size:** the entry whose style equals face wins. Otherwise score each entry: one with any
style bit not in face scores −1; a subset scores the sum of bold 4, italic 8, underline 1, outline 3, shadow 3,
condense 2, extend 1. The strictly highest score wins; among equal scores, the first. The remaining style, which the
Font Manager must synthesize, is `face & ~entryStyle`.

### 2.21 Font Manager output

**Style effects**, from the remaining style, added as bytes:

| Bit | boldPixels | italic | shadow | extra |
| --- | --- | --- | --- | --- |
| bold | +1 | | | +1 |
| italic | | +8 | | |
| outline | | | +1 | +1 |
| shadow | | | +2 | +2 |
| condense | | | | −1 |
| extend | | | | +1 |

Underline sets `ulThick = 1`. Outline and shadow together give shadow 3.

**Remaining stretch**, per axis, with `size` the requested TxSize (0 → 12) and `actualSize` the chosen strike's:

```
ratio  = FixMul(FixRatio(numer.x, denom.x), FixRatio(size, actualSize & $7F))
FOutNumer.x = (ratio + $80) >> 8          // 8.8, rounded half up
FOutDenom   = ($100, $100)
```

The strike is drawn unscaled, then the whole text image is stretched by FOutNumer/FOutDenom (§2.22).

**Scaling disabled** (glyphState byte 4): each axis's FOutNumer n is cut, and the rest becomes a factor:

```
numer = $100
while n >= $200: numer <<= 1; n >>= 1          // 16-bit words
while n <  $C0:  numer >>= 1; n <<= 1
if n < $100:     numer = (short)(numer * 3) >> 2; n = (n << 2) / 3     // 3/4 of a power of two
FOutNumer = numer;  factor = n << 8            // Fixed, 1.0 ≤ factor < 2.0
```

- The ROM never returns when n is 0 or ≥ `$8000`: the loops cannot end.
- The horizontal factor multiplies every non-zero width after its style extra, `FixMul(width, hFactor)`, skipped when
  the factor is exactly 1.0. The space extra is added afterwards and is not scaled.
- Ascent, descent and leading (vertical factor) and widMax (horizontal) become
  `FixRound(FixMul(sign-extended byte << 16, factor))`, stored as bytes.

**Family style extra:** for fonts found through a FOND when ffFlags bit 13 is clear and either bit 12 is set or
fractional widths are on. `sum = ffProperty[0] + Σ ffProperty[bit + 1]` over the remaining style bits, leaving out
bits the chosen family width table already covers; `extraFixed = FixMul(sum << 4, actualSize << 16)`. With fractional
widths off, `extra = FixRound(extraFixed)` and widths add `extra << 16`; with them on, widths add extraFixed and the
extra byte stays as in the style table.

**Width table** (Fixed, 256 entries):

- **Fractional widths off:** the offset/width entry's low byte; missing characters (outside the range or −1) take the
  missing symbol's width.
- **Fractional widths on, a strike with a glyph-width table:** those 8.8 widths (`<< 8`); a word of `$FFFF` is missing
  and takes the missing symbol's word.
- **Fractional widths on, a family width table** (FOND flags bit 14 clear, no strike widths): the table whose style
  equals face, else the best subset, scoring bold 1, italic 3, underline 5, outline 4, shadow 4, condense 2, extend 2.
  The table's words are read from its start for the strike's range, word k → character `strikeFirst + k`, so when the
  strike's first character differs from the family's the widths are shifted. The missing symbol's width is word
  `strikeLast − strikeFirst + 1`, and a word of `$FFFF` is missing. Width = `(u16 word × actualSize) << 4`. Characters
  outside the strike's range take the missing width.

Then, for all three: every non-zero width gets the style extra (zero widths none; the missing symbol's width gets it
too); `widths[32] += FixMul(FixMul(FixRatio(numer.h, denom.h), FixRatio(FOutDenom.h, FOutNumer.h)), spaceExtra)`;
`widths[13]` (return) = 0.

**Metrics** (GetFontInfo, used by grayishTextOr): the strike's ascent and descent (bytes), scaled as above when scaling
is disabled; with shadow, +1 ascent and + shadow descent; if FOutNumer ≠ FOutDenom, each is scaled
`(v · numer.v + denom.v / 2) / denom.v`.

### 2.22 Drawing text

DrText's input: the pen at (h, v) with a 16-bit fraction `frac`, the text, the Font Manager output, the mode, and the
character extra `cx` (Fixed, [pict.md §2.13](pict.md#213-text-opcodes)).

1. **Character extra in strike pixels** (if non-zero):
   `cx = FixMul(FixMul(cx, size << 16), FixMul(FixRatio(numer.h, denom.h), FixRatio(FOutDenom.h, FOutNumer.h)))`.
2. **Width:** `width = Σ widths[c] + (c ≠ 32 ? cx : 0)`, as Fixed. Code 32 is always a space: its glyph is never drawn
   and it never gets cx.
3. **Mode:** `mode = txMode & ~8` (the pattern bit only; there is no `& 7`). If bit 6 is set, the text image is also
   the transfer mask, so only glyph pixels are touched, and bit 6 is cleared. Arithmetic and hilite modes reach the
   transfer as they are.
4. **textRect:**
   - left = `h + kern`. kern is 0 unless kernMax < 0; then look up the offset/width entry of the first character by its
     raw character code, not `code − firstChar` (a ROM bug), entry 0 instead if that entry is −1, and
     `kern = min(0, (entry >> 8 & $FF) + kernMax)`, applied only when that sum ≤ 0.
   - right = the high word of `((h << 16) | frac) + width`, with no extra slop: ink past the final pen position is
     clipped.
   - If the remaining style is not 0: `lean = (short)((u16)(sign-extended italic byte) × (u16)(ascent − 1)) >> 4`;
     `slop = (sbyte)(lean + bold − extra)`, summed in a byte, so it wraps above 127;
     `right += slop + (shadow == 0 ? 0 : shadow < 4 ? shadow + 1 : 4)`;
     `left −= (short)((u8)italic × (u16)descent) >> 4`.
   - top = `v − ascent`; bottom = `top + fRectHeight`, the strike's ascent and fRectHeight.
5. **Stretch:** when FOutNumer ≠ FOutDenom the pen advances by `width × numer.h / denom.h` (unsigned, truncated), and
   the image goes to `MapRect(textRect, (v, h, v + denom.v, h + denom.h), (v, h, v + numer.v, h + numer.h))`. The new
   pen fraction is `(frac + advance) & $FFFF`.
6. **Buffer:** a 1-bit off-screen image with its left edge at `bufLeft = left & ~31` (the long-aligned pixel at or left
   of it), `rowLongs = ((u16)(right − bufLeft) >> 5) + 2` longs wide and fRectHeight rows tall, cleared to 0, with one
   extra long after it. The image is one bit stream, rows back to back: glyph writes and style passes that cross a row
   edge flow into the neighbouring row.
7. **Characters:** `charLoc = ((h + kernMax − bufLeft) << 16) | frac`, starting at the pen's own fraction. For each
   byte:
   - a space (32): `charLoc += widths[32]`;
   - otherwise `step = widths[c] + cx`; the glyph is slot `(u16)(c − firstChar)`, or the missing symbol if that is past
     the range or −1; if the missing symbol is also −1, skip the character without advancing;
   - draw at `x = (entry >> 8 & $FF) + (short)(charLoc >> 16)`, then `charLoc += step`: the glyph's strike columns
     `[loc[k], loc[k+1])` (none if empty) are ORed into the image at x, for rows `[top, top + rows)` of the height
     table (default all rows).
8. **Styles,** in this order:
   1. **Bold:** boldPixels passes; each ORs every bit of the stream into the next bit (right by one; the last bit of a
      row carries into the next row), over the image plus the extra long.
   2. **Italic:** working upward from the second-to-last row, keep an accumulator (`+= italic` per row, not wrapped);
      each row takes the bits `acc >> 4` positions to its left in the stream, bits before the image reading 0. The
      bottom row does not move.
   3. **Underline** (ulThick ≠ 0 and descent ≥ 2): `ink = row B | row B+1 | row B+2`, with B = ascent the baseline row
      and B+2 := B when descent = 2; spread ink one pixel left and right; row `B+1 |= ~spread` across the whole image
      row, with no right trim: it runs to textRect's right edge, slop included.
   4. **Shadow and outline** (shadow ≠ 0; a colour port): buf2 = the image followed by 4 zero rows. Smear buf2 right
      `(shadow & 3) + 1` times over the image's bits plus one long (bits can spill into the first extra row), then down
      the same number of times over all rows (each row |= the row above, from the bottom up). XOR the image one bit
      later and one row lower into buf2: `buf2[W + j] ^= image[j − 1]` for j over the image plus one long. Transfer
      buf2 from `(top, left, bottom + 4, right)` to that rect offset by (−1, −1) (mapped if stretched). Nothing else is
      drawn: the letters' inside is left untouched, so outline text shows the background through it (a black-and-white
      port would refill the inside for srcOr and srcBic).
9. **Transfer:** without shadow, transfer the image's textRect to the stretched rect with the mode. The image is a 1-bit
   bitmap with bounds `(top, bufLeft, bottom, right)`, so the scaling rules of §2.15–§2.16 apply.

### 2.23 grayishTextOr

Mode 49. Pictures never record it, since it is drawn outside picture recording, but a hand-built picture may use it.
On a colour port:

1. Take the foreground and background as 16-bit colours `c·257`.
2. `mid = (fg + bk) >> 1` per component, `+2` if `< $8000`.
3. `gray` = the high byte of mid.
4. If `dist(gray·257, mid)` is less than half of `dist(gray·257, bk)` and less than half of `dist(gray·257, fg)`, where
   dist is the largest component difference, draw the text srcOr in gray.
5. Otherwise draw it srcOr in the foreground colour, then paint the rect `(v − ascent, h, v + descent, h + width)` with
   the gray pattern (`AA 55 AA 55 …`) in patBic, with the metrics of §2.21 and the unscaled integer width of §2.22
   step 2.

### 2.24 Character extra, measuring and font metrics

- **CharExtra(extra: Fixed)** (colour ports; old ports ignore it) stores the extra per point:
  `chExtra = (short)(FixDiv(extra, size << 16) >> 4)`, a 4.12 value, where size is txSize or, when that is 0, the
  system font size (SysFontSize, then FMDefaultSize, then 12). FixDiv rounds. TextSize always clears chExtra. Drawing
  and measuring use `(short)chExtra << 4` (plus a picture's LineJustify spacing) × the strike size × the text scale ×
  FOutDenom / FOutNumer, after every character but spaces (§2.22) [Code].
- **TextWidth, StringWidth, CharWidth:** `trunc(trunc(sum) × FOutNumer.h / FOutDenom.h)`, where sum is StdTxMeas's Fixed
  width (§2.21: the strike's widths with the style extra, space extra, character extra and the FScaleDisable factor),
  with an unsigned multiply and divide. There is no text scaling (numer = denom = 1). Always truncated, never rounded
  [Code].
- **GetFontInfo** takes FMOutput's metrics, not the strike's. widMax gets FOutExtra (bold +1, outline +1, shadow +2,
  condense −1, extend +1, or the family's style extra; italic 0); shadow and outline add 1 to the ascent and the shadow
  count (outline 1, shadow 2, both 3) to the descent. Then each is stretched, `(value × numer + denom / 2) / denom`,
  vertically for ascent, descent and leading and horizontally for widMax [Code].

## 3. Writing

None. QuickDraw draws; recording drawing as a picture is [pict.md §3](pict.md#3-writing).

## 4. Variants

### 4.1 Mac OS 9: bitmaps

Mac OS 9's native QuickDraw is a rewrite, not a port. Where it differs from §2 (and from the picture rules of
[pict.md §4.2](pict.md#42-mac-os-9)), Mac OS 9 follows §4.1–§4.5. It still uses the ROM's MapPt, MapRect, ScalePt,
FixMul and FixRatio. [Code: Mac OS 9.0 NQD]

- **Scaling:** one DDA for rows and columns, at every depth, replacing the ROM's row DDA, column stepper and 1-bit
  special cases:
  - enlarging: destination k takes source `ceil(s(2k + 1) / 2d) − 1`;
  - reducing: destination k merges the sources between `b(k−1)` and `b(k)`, with `b(k) = floor((k + 1) s / d + ½)` and
    `b(−1) = 0`.
  - Exact integer reductions agree with the ROM; other ratios do not: 3 → 4 gives 0, 1, 1, 2 (the ROM 0, 0, 1, 2), a
    1-bit ×1.5 gives aabccd, and 3 → 269 maps column 179 to source 2.
- **Clipped scaling:** the DDA starts at the first visible row or column n without stepping to it. Where its error is
  exactly 0 there, that one row or column is off: enlarging, when `s(2n + 1)` is a multiple of `2d`, it takes source
  `s(2n + 1) / 2d`, one more than unclipped; reducing, when `2ns` is an odd multiple of `d`, its group starts one source
  earlier. Later rows and columns are unchanged.
- **Merging:** 1–8 bits take the largest index (1 bit: OR). 16 and 32 bits widen 16-bit components to 8 bits, then
  average rows and then columns, rounded: `(sum + n/2) / n`.
- **Colorizing** applies to copy modes when the foreground isn't black or the background isn't white, to or modes when
  the foreground isn't black, to bic modes when the background isn't white, and never to xor modes.
- **A colorized pixel:** 1-bit sources and indexed srcCopy and notSrcCopy work as in the ROM (bitwise). srcOr, srcBic,
  notSrcOr and notSrcBic, and colorizing copies of direct sources, blend each channel. With s the source channel
  (8-bit), C the fore colour (or modes) or back colour (bic modes), and `s := 255 − s` for the not modes:
  - copy: `((256 − s)·F + (s + 1)·B) >> 8`
  - notCopy: `((256 − s)·B + (s + 1)·F) >> 8`
  - or, bic: `((256 − s)·C + (s + 1)·d) >> 8`
- **Blend:** `(s·w + d·(65536 − w) + $8000) >> 16`; the exact average `(s + d) >> 1` when all three weights lie in
  `$7F80…$807F`; a zero weight counts as 1.
- **Pattern hilite** follows the pattern everywhere, without the ROM's whole-area quirk (§2.12).
- **Pixel patterns** use the vertical alignment too, which is always 0, since Origin does not move it.
- **Scaled CopyMask onto a 1-bit screen:** with a 1-bit source and mask, a 1-bit destination and the general stretch
  path (source and destination sizes differ, a deeper mask, or ditherCopy), Mac OS 9 applies its pixel-value
  colorizing op to RGB values and maps them back. Under the mask, srcCopy with any colours but black on white paints
  every pixel black; srcOr and notSrcOr set pixels black; srcBic and notSrcBic also set pixels black (not white);
  srcXor inverts the clear pixels; notSrcCopy paints black. Unscaled CopyMask, CopyBits (with or without a mask
  region) and deeper sources or destinations are not affected [Code] [Verified: 9.0]. A bug, possibly fixed after 9.0.

### 4.2 Mac OS 9: shapes

- **Arc slopes** use a half-up fixed multiply, `(a·b + $8000) >> 16`, which rarely moves an arc edge by one pixel.
  Every other arc computation is the ROM's.
- **Pen modes:** bit 6 is dropped first (64–79 draw as 0–15), then the ROM's acceptance rule applies (§2.3).
- **Frames of ovals, round rects and arcs** have no emptiness or pen-size test:
  - The inner shape spans rows `[top + penV, bottom − penV)`, with an oval of `max(0, ovalW − 2penH)` by
    `max(0, ovalH − 2penV)`.
  - At zero inner width (always when the pen is wider than half the shape), its edges stay at `iL = left + penH` and
    `iR = right − penH`.
  - Inner rows draw two slabs, `[oL, iL)` and then `[iR, oR)`, clamped to the rect. When `iL > iR` the slabs overlap
    and the overlap is painted twice: copy modes are unchanged, XOR cancels, arithmetic modes apply twice. Narrow rows
    can paint outside the oval.
  - A zero pen height makes every row an inner row: a 2 × 8 oval framed with pen (v 0, h 2) paints `[0, 2)` on all 8
    rows.
  - Rows outside the inner span are solid, as in the ROM.
- **Round rects:** the corner size clamp and zero sizes are [pict.md §4.2](pict.md#42-mac-os-9).

[Code: Mac OS 9.0 NQD]

### 4.3 Mac OS 9: text and the Font Manager

[Code: Mac OS 9.0 NQD, FontManager]

- **Size folding:**

  ```
  if any numer/denom component is 0: numer = denom = (1, 1)
  take absolute values; if numer.v == denom.v: both 1; if numer.h == denom.h: both 1, no fold
  r = (numer.h << 16) / denom.h;  p = r·size;  if (p >> 16) < 4: no fold
  newSize = (p + $8000) >> 16;  f = FixRatio(size, newSize)
  numer = ((FixMul(f, r) + $80) >> 8, (FixMul(f, (numer.v << 16) / denom.v) + $80) >> 8);  denom = (256, 256)
  ```

  The search uses newSize (or the original size, without folding); size choice is otherwise §2.20. The stretch is
  `D6 = (size << 16) / strikeSize`, then per axis `H = ratio × D6`, a half-up multiply skipped when D6 = 1.0, and
  `FOutNumer = H ≤ $7FFF7F ? (H + $80) >> 8 : $7FFF`. Examples: 9 pt at 4/3 → 12 pt, numer (256, 192); 12 pt at 3/2 →
  18, (256, 171); 9 pt at 5/4 → 11, (262, 209).
- **Font fallback order:** the application font, the lowest-numbered font family, the system font, then Geneva.
- **Style variants** are matched against `face & $9B`, with the ROM's scores. The synthesized remaining style is the
  unmasked face minus the entry's style, so underline, condense and extend are always synthesized.
- **Family width tables** are walked with the family's own character range: the strike's character c takes word
  `c − ffFirstChar`, and the missing symbol word `ffLastChar − ffFirstChar + 1`.
- **Text modes:** transparent (36) and ditherCopy (64) draw as srcOr; grayishTextOr (49) draws srcOr in the gray
  GetGray finds (§2.23).
- **Glyph placement ignores the pen fraction.** With S the running Fixed advance from 0, a glyph goes at
  `pen.h + ((S + $8000) >> 16) + its offset + kernMax`. The pen still ends at `(pen.h << 16) + fraction + advance`, so
  PnLocHFrac only moves the end pen. The pen is capped at 32752.0.
- **Italic** pivots on the row just below the baseline (y = pen.v), with `it` the italic pixels (default 8): rows above
  shift right by `((pen.v − y) × it) / 16`; that row and the rows below shift left by `((y − pen.v + 1) × it) / 16`;
  both divides truncate.
- **Text rect** (ink past the final pen position is not clipped), relative to the pen, from the union U of the glyph
  images and the spaces' origin points:
  - `W = (advance + $8000) >> 16`;
  - `right = U.right`; if the mode is 0, the mode is above 3, or the text is underlined, `right = max(right, W)`;
  - `right += bold + (italic ? it × (ascent − 1) / 16 − extra : 0) + (shadow ? min(shadow + 1, 4) : 0)`;
  - if `extra > 0`: `right = max(right − extra, W)`;
  - `left = min(U.left − (italic ? it × (descent − 1) / 16 : 0), 0)`.
- **Character extra** is added only to characters that are not spaces and have a width above 0 (carriage returns and
  zero-width characters get none), and is scaled with half-up multiplies.
- **A lone carriage return** (a text of exactly one byte 13) draws nothing and leaves the pen. A carriage return inside
  longer text draws its glyph with advance 0.
- **Stretched text advances the pen** by `FixMul(width, numer/denom)`, rounded half up.
- **Stretched text** has no routine of its own: the 1-bit text buffer is stretched from the text rect to the mapped
  rect, the same for every mode:
  - the destination rect: each pen-relative edge o maps to `sign(o) × floor((|o| × N + 128) / 256)`, N the 8.8 stretch
    of that axis (MapRect about the pen);
  - rows and columns: the ordinary DDA (§4.1) on the rect sizes S → D, from the rect's own top-left: enlarging
    `ceil(S(2k+1)/2D) − 1`; reducing the OR of `[end(k−1), end(k))`, `end(k) = floor((k+1)S/D + ½)`;
  - the mode changes only the source rect: srcOr, srcXor and srcBic use the union of the glyph images (vertically the
    rows that hold ink, raised to at least the baseline; spaces count as points on the baseline); srcCopy, modes above
    3 and underlined text extend it to −ascent..descent and the rounded width. Geneva 9 text spans rows −7..2 (ink) or
    −10..2 (extended).
  - Banding (a buffer over 32 KB) and run splits restart the rounding per band; outline and shadow shrink the rect by 1
    first.
- **Colour bitmap fonts** (an NFNT with a depth code in fontType, strike rows `rowWords × 2 × depth` bytes; in a FOND
  the association's style high byte is the depth code):
  - At a chosen size and style, the last association of the same size and low style byte with depth code 1–4 is used
    instead of the plain strike.
  - Colours: the `fctb` with the NFNT's id (a colour table, §1.8), else the standard `clut` of the strike's depth.
  - Each glyph is drawn on its own with opaque srcCopy, whatever the text mode and style: its whole box (image width ×
    font rect height, top at `pen.v − ascent`) is copied from the strike through the colours, clipped.
  - Glyph x = `HiWord(penFixed + scaled) + kernMax + offset`, where penFixed = `(pen.h << 16) | fraction`, scaled is
    the unscaled Fixed advance so far (widths plus chExtra, as in §2.22) times `numer.h / denom.h` when stretched, and
    the glyph offset is not scaled.
  - Stretched width = `(2 × bits × numer.h + denom.h) / (2 × denom.h)` (half up); the rows scale with MapRect about
    the pen. Glyphs of width 0 draw nothing.
  - The pen advances as for 1-bit text.

### 4.4 Mac OS 9: the port and measuring

[Code: Mac OS 9.0 NQD]

- **Pixel patterns** also apply patAlign.v, and a port can have its own pattern origin for them (QDSetPatternOrigin,
  grafVars flag `$4000`; the picture opcode `$0200`).
- **SetOrigin** does nothing without a port and clears QDErr.
- **CharExtra:** the divide truncates and the result is clamped to ±`$7FFF`; TextSize clears chExtra only when the size
  changes; the extra is added only to characters with a width, and is 0 for non-native scripts.
- **TextWidth:** a signed divide. Strings under 32 characters are cached, and with a stretched font a cache hit can be 1
  more (`floor(FixTxWid × n / d)` rather than `trunc(w) × n / d`).
- **GetFontInfo:** the FScaleDisable factor is applied twice, and a negative leading works.

### 4.5 Mac OS 9: quirks not reproduced

Known from Mac OS 9's code but not exactly specified or verified [Code: Mac OS 9.0 NQD]:

- italic rows that need shifts of 32 bits or more (from about 64 rows at the default slant) are corrupted;
- a clipped reduction's right-edge span is one column short;
- a destination rect past the pixel map's bounds picks its scaling routine from truncated widths.

Old black-and-white ports and the destination's alpha byte do not apply.

### 4.6 Screen depths

A picture drawn on a 1, 2, 4, 8 or 16-bit screen looks different: at indexed depths QuickDraw works on colour-table
indices, and ditherCopy dithers. §4.6–§4.11 were verified pixel for pixel against Mac OS 9 [Verified: Mac OS 9].

- **The screen:** a GWorld of that depth with its default colour table: 1 bit white, black; 2 bits greys FFFF, ACAC,
  5555, 0000; 4 bits `clut` 4; 8 bits `clut` 8; 16 bits 5-5-5 pixels, bit 15 zero.
- **Each pixel's colour** is its entry's colour; 16-bit pixels get each 5-bit field replicated to 8 bits,
  `(c << 3) | (c >> 2)`.

### 4.7 Screen depths: RGB to index

**The inverse table** (MakeITable, resolution 4):

1. Take a cube of 18³ cells: a 1-cell border and 16³ interior cells.
2. Seed the entries in this order: entry 0, the last entry, then entries 1 … n−1. Each seed goes to cell
   `(R8 >> 4, G8 >> 4, B8 >> 4)` + 1 on each axis.
3. The first entry to reach a cell owns it. A later entry is a hidden colour, chained to the owner.
4. Fill breadth-first from the seeds, in neighbour order +B, −B, +G, −G, +R, −R. An empty neighbour takes the colour
   and joins the queue.
5. The table is the 4096 interior cells, red most significant.

The standard `clut` 4 and 8 have no hidden colours at resolution 4.

**Grey tables** (`clut` 1 and 2) map luminance to an entry:

1. Put each entry at its red byte (the lowest index wins on equal reds).
2. Fill the gaps by alternating left-to-right and right-to-left passes, each copying a neighbour, until none is left.
   This gives the nearest entry, exact ties going to the darker one.

**The lookups:**

| Where | Colour screens | Grey screens | 16 bits |
| --- | --- | --- | --- |
| Direct pixels copied (srcCopy, and Boolean results of direct sources) | `table[(R>>4)<<8 \| (G>>4)<<4 \| B>>4]` | `links[(5R+9G+2B) >> 4]` | `c >> 3` |
| Arithmetic and colorized results | The same | `links[(5(R&$F0) + 9(G&$F0) + 2(B&$F0)) >> 4]` (the cell's corner) | `c >> 3` |
| Color2Index (fore, back, hilite, indexed sources) | The table, then the hidden-colour chain (Mac OS 9: nearest by Manhattan distance on 16-bit components) | Mac OS 9 `links[(5R+9G+2B) >> 12]` on 16-bit components; the ROM `links[((((R+G)/2 + B)/2 + R)/2 + G)/2 >> 8]` | `c16 >> 11` |

### 4.8 Screen depths: the port's indices

- **Fore and back:** fgI = Color2Index(fore) and bkI = Color2Index(back), from the exact 16-bit colours; the classic
  FgColor and BkColor constants map through QDColors (§1.9). On 1- and 2-bit screens, if fgI = bkI while the colours
  differ, fgI = Color2Index(the complement of fore): yellow on white draws black on a 1-bit screen.
- **Hilite:** hiI = Color2Index(hilite); if that equals bkI, Color2Index of the complement instead.

### 4.9 Screen depths: transfer modes on indices

Let M = 2^depth − 1 (on 16 bits, M = `$7FFF`). The not modes invert the source bit or source index first.

**1-bit sources and 1-bit patterns:**

| Mode | Result |
| --- | --- |
| copy | On → fgI, off → bkI |
| or | On → fgI |
| bic | On → bkI |
| xor | On → d ⊕ M |

**Indexed sources:**

- copy: `Color2Index((rgb & back) | (~rgb & fore))` on 16-bit components; notSrcCopy complements rgb first. When the
  §4.8 collision rule replaced fgI, the fore colour here is fgI's colour.
- or, bic, xor: convert the source with Color2Index to s, then or `(s & fgI) | (~s & d)`, bic `(s & bkI) | (~s & d)`,
  xor `d ^ s`.

**Direct sources:** the 32-bit rules (§2.10–§2.12) on the screen colour of d, then the table (Mac OS 9's colorizing
included). On 16 bits Mac OS 9 colorizes on 5-bit fields: copy `((32 − s)·F + (s + 1)·B) >> 5`; or and bic
`((32 − s)·C + (s + 1)·d) >> 5`.

**Pixel patterns:** each colour is converted to a value p (Color2Index for indexed patterns, the table for direct
ones), then fore = all ones and back = 0: copy p, or `d | p`, xor `d ^ p`, bic `d & ~p`.

**RGB patterns (type 2):** PatDither builds a 2 × 2 cell; pixel (x, y) takes cell `[(y & 1) · 2 + (x & 1)]`, with the
pattern alignment applied:

```
0 1
2 3
```

1. Fill the slots in the order 0, 1, 3, 2.
2. Keep running 16-bit totals, starting at 0. For each slot: add the colour to the totals;
   `i = Color2Index(clamp(total, 0, $FFFF))`; subtract Index2Color(i) from the totals.
3. Index2Color on 16 bits replicates each field: `(c << 11) | (c << 6) | (c << 1) | (c >> 4)`.

**Arithmetic modes:** an indexed source is first converted to its screen index, and its colour is that entry's colour.
Apply the §2.11 operation (Mac OS 9's rounding, §4.1) to the source colour and d's colour, and convert the result with
the arithmetic lookup (§4.7). On 16 bits, on 5-bit fields: blend `(s·w + d·(65536 − w) + $8000) >> 16`, the average
`(s + d) >> 1` only when all three weights are `$7F80`…`$807F`; pins at the OpColor's top 5 bits; addOver and subOver
modulo 32.

**transparent, hilite, invert, 1-bit screens:**

- transparent writes s when s ≠ bkI;
- hilite: where s ≠ bkI, d = bkI becomes hiI and d = hiI becomes bkI;
- invert: `d ^ M`;
- on 1-bit screens the arithmetic modes 32–39 become srcCopy, srcBic, srcXor, srcOr, srcOr, srcBic, srcXor, srcOr, and
  hilite becomes xor.

### 4.10 Screen depths: ditherCopy

Only CopyBits of a direct source in srcCopy with the ditherCopy flag (64) dithers. Every other depth reduction
truncates.

**Mac OS 9:**

- Scope: the rows of the visible bounds, from the top, serpentine with the first row left to right. The error buffer
  and the carry start at 0 for each call.
- Per pixel: `v = clamp(c + carry + buf[x], 0, 255)` per component; choose the entry with the table;
  `e = v − (the entry's 8-bit colour)`; `carry = e >> 1` (floor); `buf[x] = e − (e >> 1)`, stored as a signed byte, so
  +128 wraps.
- Clipped pixels reset `buf[x]` and the carry.
- 16 bits: the error is v's low 3 bits.
- Grey screens: dither the luminance `(5R+9G+2B) >> 4` against the entry's blue byte.
- 1 bit: black when v < 128, with error v or v − 255.

**The ROM:**

- Scope: rows from the first visible one, always across the destination rect's full width; clipped pixels are
  converted but not drawn.
- Error split: `buf[x] = e >> 1` (floor) and `carry = (e >> 1) + (e & 1)` (ceil), with no wrapping.
- Grey screens: the luminance `((R + G + 2B)/4 + R + 2G)/4`, against the entry's red byte.
- 16 bits: ordered, `min(c + D[row & 3][x & 3], 255) >> 3`, rows counted from the first visible one and x from the
  rect's left:

  ```
  D = 0 5 1 4
      6 3 7 2
      1 4 0 5
      7 2 6 3
  ```

### 4.11 Screen depths: text

- **Mac OS 9:** 1-bit text on indexed screens follows §4.9.
- **The ROM's synthetic strikes (2–8 bits):** glyph bits map to Color2Index(black) and to Color2Index(white), which is
  0. On the standard tables this gives the same pixels as expanding the 1-bit strike.
- The ROM's arithmetic-mode text on indexed screens (colorizing stripped, ink the black index) is not verified (§8).

### 4.12 Apple's 1984 QuickDraw source

Apple's published 1984 QuickDraw source differs from the ROM `$077D` in two places [Code: QuickDraw 1984 source]:

- vertical scaling always starts the error at `srcH / 2`, so exact integer shrinks merge other rows (§2.15);
- the character generator always starts charLoc's fraction at ½, not at the pen's fraction (§2.22).

## 5. ClassicMac

- **The port:** `QuickDrawPort` draws on an `RgbaBitmap` with QuickDraw's names (pen, patterns, colours, clip,
  SetOrigin, the frame/paint/erase/invert/fill verbs, CopyBits, CopyMask, text, TextWidth, GetFontInfo, DrawPicture).
  `QuickDrawOptions.Version` selects `MacOS9` (the default) or `MacRom`, `ScreenDepth` 1, 2, 4, 8, 16 or 32 (the
  default; any other throws `ArgumentOutOfRangeException`), `Fonts` the bitmap fonts, `TextFallback` the outline
  fallback, `HiliteColor` the system highlight colour (by default `$CCCC $CCCC $FFFF` for Mac OS 9, `$9999 $CCCC $CCCC`
  for the ROM), `PreserveAlpha` §2.19 (off by default). [ClassicMac]
- **The canvas** is 32-bit RGBA and starts fully transparent. For every transfer mode a transparent pixel reads as
  white, as the port behind a picture is white. Every pixel a drawing operation writes becomes opaque, except that
  bitmaps may carry alpha (§2.19, with `PreserveAlpha`); pixels never written stay transparent, so callers can
  composite the result. At screen depths below 32 the canvas is still RGBA, each pixel the colour it has on that
  screen. [ClassicMac]
- **Fonts:** `FontLibrary` holds the `FOND`, `NFNT`, `FONT` and `fctb` resources the caller supplies (from resource
  forks); they are not part of any picture. A strike whose characters do not run within 0–255 (lastChar before
  firstChar included), or whose rowWords has the top bit set in the ROM mode, counts as missing. [ClassicMac]
- **The outline fallback:** text with no usable bitmap strike (no fonts supplied, the family missing, or TrueType) goes
  to `ITextFallback`: drawn at the mapped pen position with the baseline at v, at TxSize with TxFace, through the text
  mode as a 1-bit mask; a picture's fontName, when known, helps choose a similar font. It cannot match a Mac exactly.
  The ImageSharp and SkiaSharp adapters supply fallbacks. [ClassicMac]
- **FScaleDisable** where the ROM would never return (n 0 or ≥ `$8000`, §2.21): the stretch is left uncut.
  [ClassicMac]
- **Mac OS 9 mode, not modelled:** the pattern origin of §4.4; the TextWidth cache; GetFontInfo applies the
  FScaleDisable factor once. The quirks of §4.5 are drawn the ROM way in both modes. The scaled CopyMask bug of §4.1 is
  reproduced for srcCopy, the mode CopyMask uses, as ClassicMac reproduces Mac OS 9.0's other quirks. [ClassicMac]
- The adapters and decoders reach the renderer through `ClassicMac.Graphics` internals; the engine lives in
  `QuickDraw/Engine`, `Regions` and `Text`. [ClassicMac]

## 6. Diagnostics

None. The renderer reports no diagnostics; the font readers' codes (`font.short`, `font.location-table`) are in
[bitmap-fonts.md](../resources/bitmap-fonts.md), and pictures' in [pict.md §6](pict.md#6-diagnostics).

## 7. Verification

- **Golden pictures** (`tests/golden/pict`, written by `tools/GoldenPictures`; `GoldenTests`): shapes with every verb
  and pen size, the pattern pen modes, line slopes, 1-bit CopyBits in every Boolean mode with StretchBits ratios and a
  mask region, 8-, 16- and 32-bit CopyBits, fore and back colours with colorizing, arithmetic modes and hilite, text in
  Chicago, Geneva, New York and Monaco with every style, a 144 dpi picture, Origin and clip. The emulator screenshots
  and the Apple fonts are not committed (`tests/golden/README.md`); the test skips without them.
- **Mac OS 9.0 in SheepShaver:** the screen depths (§4.6–§4.11) pixel for pixel, the scaled CopyMask bug, and the rules
  tagged [Verified].
- **Unit tests** (`tests/ClassicMac.Graphics.Tests`, small fonts from `TestFont`):
  - `RegionTests`: region data, insets, ovals (4 × 4, 5 × 5, 3 × 3, symmetry), round-rect corners, frames, polygons,
    arcs (a quarter, a negative sweep), lines and pen size 0.
  - `DrawingTests`: the verbs, pattern alignment, flat ovals, zero-height round rects (both modes), ditherCopy and pen
    modes 16–31 on ovals, the Mac OS 9 slab frame, pen modes, the clip, hilite, pixel patterns, the arithmetic modes
    (blend truncating in the ROM, rounding on Mac OS 9).
  - `CopyBitsTests`: stretching and shrinking 1-bit and deep sources, colorizing, the Boolean modes, transparent,
    alpha, the ×1.5 and ×¾ column groups, the exact-shrink row start, Mac OS 9's DDA, rounding and channel blend.
  - `PortTests`: SetOrigin, a hidden pen, text measuring, DrawPicture, CopyMask on a 1-bit screen.
  - `TextTests`: strikes, placement, space extra, the missing symbol, bold, italic (both modes), underline, outline,
    ink past the pen (both modes), the lone return, ChExtra, PnLocHFrac, TxRatio, Mac OS 9's stretched srcOr rect and
    size folding, the Font Manager's size and style choice, fallbacks, width tables, FScaleDisable, colour fonts,
    FixRound, grayishTextOr.
  - `ScreenDepthTests`: the 8-bit inverse table, 16-bit truncation, 1-bit luminance, ditherCopy error diffusion, the
    ROM's ordered 16-bit dither, invert, the allowed depths.
  - `LayeringTests`: the layers of `ClassicMac.Graphics` use only the layers below them.

## 8. Not covered

- **TrueType (`sfnt`) text** goes to the outline fallback (§5). On Mac OS 9 this includes stretched text whose folded
  size has no bitmap strike (§4.3).
- **Stretched text on Mac OS 9:** banding (a text buffer over 32 KB) and run splits restarting the rounding per band;
  outline and shadow shrinking the rect by 1 first (§4.3).
- **The ROM's arithmetic-mode text on indexed screens** is not verified (§4.11).
- **Mac OS 9's quirks of §4.5**, its pattern origin and its TextWidth cache (§4.4).
- **Custom screen colour tables and search procs.**
- **QuickTime images on indexed screens**, which the Image Compression Manager's codecs dither themselves
  ([quicktime.md §8](quicktime.md#8-not-covered)).

## 9. References

1. Apple Computer, *Inside Macintosh: Imaging With QuickDraw* (1994). Apple's documentation.
2. Apple Computer, *Inside Macintosh: Text* (1993), the Font Manager and the font resources. Apple's documentation.
3. Apple Computer, QuickDraw source code (1984), released through the Computer History Museum. Apple's licence;
   reference only, never committed.
4. Executor (ARDI; Wolfgang Thaller's revision), <https://github.com/autc04/executor>, `qIMVxfer.cpp` (transfer modes,
   hilite) and the region inset. MIT; parts ported with notice (`THIRD-PARTY-NOTICES.md`).
