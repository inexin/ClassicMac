# QuickDraw drawing

This document specifies how QuickDraw draws, completely enough to reproduce it pixel for pixel: fixed-point
arithmetic, how shapes become pixels, patterns and transfer modes, CopyBits, bitmap text and the Font Manager, and
drawing on indexed and 16-bit screens. Pictures ([PICT.md](PICT.md)) are recordings of these calls, and
ClassicMac's `QuickDrawPort` ([QUICKDRAW-API.md](../QUICKDRAW-API.md)) makes them directly; both draw by these
rules, implemented in `ClassicMac.Graphics.QuickDraw`.

There are two reference implementations, and they differ in details:

- **Mac OS 9** replaced most of QuickDraw (DrawPicture, CopyBits, the shape procedures, text and the Font Manager)
  with a native PowerPC rewrite. This is what current Macs-in-emulation (SheepShaver, and anything running Mac OS 9)
  show, and it is the **default** of ClassicMac.
- **The Macintosh ROM** (Mac OS ROM 1.6, `$077D`) holds the last Apple revision of the classic 68k QuickDraw, which
  every Mac before Mac OS 9 used.

Sections 2–8 describe the ROM behaviour, marked **ROM** where it is surprising. Section 9 lists every point where
Mac OS 9 differs.

Contents

1. [Conventions](#1-conventions)
2. [Fixed-point arithmetic](#2-fixed-point-arithmetic)
3. [The rendering model](#3-the-rendering-model)
4. [Shapes](#4-shapes)
5. [Patterns and transfer modes](#5-patterns-and-transfer-modes)
6. [Bitmaps (CopyBits / StretchBits)](#6-bitmaps-copybits--stretchbits)
7. [Text](#7-text)
8. [Screen depths](#8-screen-depths)
9. [Mac OS 9 differences](#9-mac-os-9-differences)
10. [Not covered](#10-not-covered)

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

## 2. Fixed-point arithmetic

These must be exact: they decide pixel positions.

- **FixMul(a, b):**
  - If b = 1.0 return a; if a = 1.0 return b.
  - Otherwise take `p = (long)a × b`.
  - If p ≥ 2⁴⁷ or p < −2⁴⁷, saturate to `0x7FFFFFFF` or `0x80000000` by the operands' signs.
  - Otherwise `r = p >> 16` (floor). Add 1 when bit 15 of p is set and either r ≥ 0 or the low 15 bits are
    non-zero. This rounds half away from zero.
- **FixRatio(n, d)** (i16 operands):
  - If d = 0: `0x7FFFFFFF`, or `0x80000001` when n < 0.
  - If n = d: `0x10000`.
  - If n = −32768 and d = −1: `0x80000000`.
  - Otherwise `(n << 16) / d`, truncated toward zero.
- **FixRound(x):**
  - If x ≥ `0x7FFF8000`, return 32767.
  - Otherwise `(x + (x ≥ 0 ? 0x8000 : 0x7FFF)) >> 16` as an i16. This rounds halves away from zero.
- **SlopeFromAngle(a):**
  - Reduce a (i16) modulo 180 into [0, 180).
  - Return `-Tangent[a]` for a ≤ 90, else `Tangent[180 − a]`.
  - `Tangent[k]` is QuickDraw's table of tan(k°) as Fixed, **not** exactly rounded tangents. Use exactly these 91
    values:

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

---

## 3. The rendering model

### 3.1 Canvas

- The canvas is 32-bit RGBA and starts **fully transparent**.
- For every transfer mode, a transparent pixel reads as **white**, because the port behind the picture is white.
- Every pixel a drawing operation writes becomes opaque, except that bitmaps may carry alpha (§6.6).
- Pixels a picture never writes stay transparent, so callers can composite the result.

The ROM draws into a 32-bit direct destination with the rules below. Colours are 8 bits per component, taken from
the high byte of each 16-bit QuickDraw component.

### 3.2 The drawing pipeline

Every shape is converted to the exact set of pixels QuickDraw would touch: a **region** (§4). That region is then:

1. intersected with the canvas and the mapped clip;
2. painted through a pattern and a transfer mode (§5).

Bitmaps go through CopyBits (§6) and text through the character generator (§7), which itself ends in CopyBits.

### 3.3 Verbs

| Verb | Region | Pattern | Mode |
|---|---|---|---|
| frame (+0) | the shape's outline, drawn with the pen (§4) | pen pattern | pen mode |
| paint (+1) | the shape | pen pattern | pen mode |
| erase (+2) | the shape | background pattern | patCopy |
| invert (+3) | the shape | black | patXor, or hilite when pending (§5.5) |
| fill (+4) | the shape | fill pattern | patCopy |

Details:

- **"Same" opcodes** reuse the last rect (shared by rects, round rects, ovals and arcs), the last polygon or the last
  region, each kept in picture coordinates.
- **Rect-based shapes** map the rect, then draw nothing if the mapped rect is empty.
- **Polygons with fewer than two points** draw nothing.
- **Framing a polygon** draws a line (§4.5) between each pair of consecutive vertices and **does not close it**.
- **Pen modes on ovals, round rects and arcs** (ROM, DrawArc `$FFC93E8C`; Mac OS 9 the same): frame and paint force
  bit 3 of the pen mode and draw only modes 8–15, 40–47 and 58 (hilite). Any other mode draws **nothing**: 16–31,
  48–57 (including grayishTextOr), 59–63, and 64 and up (ditherCopy). Elsewhere ditherCopy is treated as copy.
- **HiliteMode** applies to the next drawing operation only. Every drawing operation clears it, whether or not it
  used it.

---

### 3.4 The port: origin, pen visibility, pen fraction

- **SetOrigin(h, v)** offsets portRect, the pixel map's bounds and visRgn, so the canvas's top-left pixel gets local
  coordinates (h, v). The clip region, patAlign and the pen are untouched: they keep their local coordinates and so move
  on the screen. Nothing happens when the origin is unchanged [Code].
- **pnVis** (HidePen decrements it, ShowPen increments it): while negative, lines, every frame/paint/erase/invert/fill
  verb, text pixels, CopyBits into the port's own pixels and ScrollRect draw nothing. The pen still moves: LineTo sets
  pnLoc, and DrawString advances it. CopyBits into other bitmaps, CopyMask/CopyDeepMask, region and polygon recording
  and picture recording (unless pnVis < −1) are not suppressed [Code]. The ROM and Mac OS 9 agree.
- **pnLocHFrac** (colour ports) is reset to `$8000` by MoveTo, Move, the line path (LineTo, Line, FramePoly, also when
  the pen is hidden) and InitCPort/OpenCPort; not by SetOrigin, SetPort, the pen-state calls, PenNormal, HidePen,
  ShowPen or PortChanged. DrawString adds the fraction of the text's width to it, the carry going into pnLoc.h [Code].
  Mac OS 9 caps the pen at 32752.0.

## 4. Shapes

All shapes produce regions in canvas coordinates, clipped horizontally to the shape's rect where noted.

### 4.1 Rects and regions

- **Rect:** its pixels.
- **Frame of a rect or region:** the shape minus the shape inset by the pen. Inset a region by (penH, penV) as
  follows:
  - shrink every span by penH at both ends, dropping spans that vanish;
  - then erode the result vertically by penV (a pixel stays only if the pixels penV above and below it are also
    inside).

### 4.2 Ovals, round rects and arcs (ROM, DrawArc)

One scan converter handles all three:

- an **ellipse** of `ovalWidth × ovalHeight` — the whole rect for ovals and arcs, the corner oval (OvSize, **not
  halved**) for round rects;
- stepped one scan line at a time from the rect's top;
- its left and right edges kept in 16.16 and moved in **half-pixel** steps.

Edges are exact only with this incremental method; there is no closed form that reproduces them.

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

**Per scan line** (only for rows inside [top, bottom)):

```
y = oddY;  oddY = (short)(oddY + 2)
while (int)(sum >> 32) < target:  R += 0x8000; L -= 0x8000; sum += term; term += step
while (int)(sum >> 32) > target:  R -= 0x8000; L += 0x8000; term -= step; sum -= term
target -= 4 * (short)(y + 1)
row's run = [(short)(L >> 16), (short)(R >> 16))
```

Examples to test against:

- 4×4 → `[1,3) [0,4) [0,4) [1,3)`.
- 7×7 → `[2,5) [1,6) [0,7) [0,7) [0,7) [1,6) [2,5)`.
- 16×16 → `[5,11) [3,13) [2,14) [1,15) [1,15) [0,16)×6 [1,15) [1,15) [2,14) [3,13) [5,11)`.
- **Quirks:**
  - Flat ovals are vertically asymmetric: 16×3 → `[2,14) [0,16) [1,15)`.
  - Runs may extend past the rect, and are clipped to it.

**Round rects:**

- Rows in `[holdTop, holdBottom)` do not step the ellipse, so the sides stay straight. Here
  `holdTop = top + ((short)ovalHeight >> 1)` and `holdBottom = holdTop + (bottom − top) − ovalHeight` (not clamped).
- With ovalHeight ≤ 0 and ovalWidth > 0, every row is `[left + ⌊ow/2⌋, right − ⌊ow/2⌋)`.

**Frames (hollow shapes):**

- The inner ellipse uses the rect inset by the pen, `(top+penV, left+penH, bottom−penV, right−penH)`, and an oval
  size of `outer − 2 × pen` on each axis.
- If the inset rect is empty, the shape is painted solid.
- On rows inside the inner rect, draw `[outerL, innerL)` and `[innerR, outerR)`. On other rows, draw the whole outer
  run.

**Arcs:**

- Angles are in degrees, 0 = 12 o'clock, clockwise.
- A negative arcAngle is normalized to `start += arc; arc = −arc`.
- arcAngle 0 draws nothing. arcAngle ≥ 360 draws the full oval.

Two rays bound the wedge:

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

- **Times(s, n):** the 68000 way of multiplying a Fixed by a small integer:
  - `low = (u16)s × n` (unsigned 32-bit);
  - `high = (short)((short)(s >> 16) × n)`;
  - result high word = `(u16)((low >> 16) + (u16)high)`, low word = `low & 0xFFFF`.
  - The high word wraps, so steep angles in huge ovals wrap exactly as on the Mac (e.g. 89–91° in a 1200×1200 oval).
- **Hidden top half:** with arc < 180, the top half is hidden when `(short)(flag1 | flag2) ≥ 0`. With arc = 180, it
  is hidden when start = 90.

For each row from top to bottom, first step the ellipses (outer and inner) as above. Then:

- **At midRow:** negate both flags and un-hide. If arc < 180 and `(short)(flag1 | flag2) ≥ 0`, stop, because the arc
  lay in the top half. If arc = 180 and start = 270, stop. Otherwise swap the two rays (position, slope and flag).
- **Otherwise, if not hidden:** emit the row's runs, cutting the left side at ray 1 while `flag1 < 0` and the right
  side at ray 2 while `flag2 < 0`. Ray positions are `(short)(ray >> 16)`, evaluated at the row's top.
  - `cutLeft = clip1 && outerL < ray1 ? ray1 : outerL`
  - `cutRight = clip2 && outerR > ray2 ? ray2 : outerR`
  - **Hollow rows:** `innerLeft = clip2 && innerL > ray2 ? ray2 : innerL` and
    `innerRight = clip1 && innerR < ray1 ? ray1 : innerR`.
    - If `cutLeft < cutRight`, emit `[cutLeft, innerLeft)` and `[innerRight, cutRight)`.
    - Otherwise, for a reflex wedge (`(short)(flag1 & flag2) < 0` and arc > 180):
      - if `innerLeft == cutRight`, emit `[cutLeft, innerL)`;
      - else if `cutLeft == innerRight`, emit `[innerR, cutRight)`;
      - then emit `[outerL, innerLeft)` and `[innerRight, outerR)`.
  - **Solid rows:** if `cutLeft < cutRight`, emit `[cutLeft, cutRight)`. Otherwise, for a reflex wedge, emit
    `[outerL, cutRight)` and `[cutLeft, outerR)`.
- **Then** advance both rays by their slopes.

All runs are clipped to [left, right).

### 4.3 Fill details specific to DrawArc (ROM)

- **Solid-pattern shortcut:** ovals, round rects and arcs have none. Hilite always works per pixel wherever the
  pattern pixel is not the background colour (contrast §5.5).
- **Pen modes:**
  - source modes act as the corresponding pattern modes;
  - only modes 8–15, 40–47 and 58 draw once bit 3 is forced; everything else draws nothing (§3.3).

### 4.4 Lines

The pen is a `penH × penV` rectangle **hanging below and right** of the path, swept from (h1, v1) to (h2, v2)
inclusive:

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

**Line pen mode:** a Boolean pen mode m < 32 becomes the pattern mode `(m mod 64) | 8`; other modes are used as
they are.

### 4.5 Polygons

A filled polygon is the region QuickDraw records for its edges, closed from the last point to the first:

- **Horizontal edge:** add the inversion-point pair (h1, v) and (h2, v).
- **Vertical edge:** adds nothing.
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

Build the region from all points (a point toggled twice cancels).

---

## 5. Patterns and transfer modes

### 5.1 Mode numbers

| Modes | Names |
|---|---|
| 0–7 | srcCopy, srcOr, srcXor, srcBic, notSrcCopy, notSrcOr, notSrcXor, notSrcBic |
| 8–15 | patCopy, patOr, patXor, patBic, notPatCopy, notPatOr, notPatXor, notPatBic |
| 32–39 | blend, addPin, addOver, subPin, transparent, addMax, subOver, adMin |
| 40–47 | the same arithmetic modes as 32–39, with bit 3 set |
| 49 | grayishTextOr (text only, §7.6) |
| 50 | hilite (58 as a pattern mode) |
| 64 | ditherCopy (bit) |

**Normalization:**

1. Clear bit 6.
2. Modes ≥ 50 are **hilite**.
3. 49 is srcOr.
4. 32–47 become `mode & ~8`.
5. Otherwise take `mode & 7`. Pattern and source variants are the same operation for 1-bit data.
6. With **HiliteMode pending**, srcXor/patXor become hilite.

### 5.2 1-bit sources and 1-bit patterns

A 1-bit pixel (1 = "on"/black) is colorized with the foreground colour F and background colour B.

**Boolean modes** (for the `not` modes, first swap on/off):

| Mode & 3 | Result |
|---|---|
| copy | on → F, off → B |
| or | on → F, off → unchanged |
| xor | on → invert the destination (255 − each component), off → unchanged |
| bic | on → B, off → unchanged |

**Arithmetic, transparent and hilite modes** use the colorized pixel (F for on, B for off) as a colour source
(§5.4).

### 5.3 Colour sources in Boolean modes (ROM)

For a colour source pixel s (a pixel of a deep bitmap or a colour pattern) and destination d, work **bitwise on
24-bit RGB values**. The ROM's direct loops work on inverted values, which gives:

| Mode | Result |
|---|---|
| srcCopy | `(s & B) \| (~s & F)` |
| srcOr | `(~s & F) \| (s & d)` |
| srcXor | `d ^ ~s` |
| srcBic | `(~s & B) \| (s & d)` |
| notSrcCopy | `(~s & B) \| (s & F)` |
| notSrcOr | `(s & F) \| (~s & d)` |
| notSrcXor | `d ^ s` |
| notSrcBic | `(s & B) \| (~s & d)` |

With the default black foreground and white background, srcCopy copies, srcOr is an AND, and so on.

**Colour patterns (PixPat types 1 and 2) in Boolean modes** instead treat the pattern pixel P as a value with fore =
all ones and back = 0 (the `not` modes invert P first):

| Mode & 3 | Result |
|---|---|
| copy | P |
| or | `d \| P` |
| xor | `d ^ P` |
| bic | `d & ~P` |

### 5.4 Arithmetic modes (ROM, 32-bit destination)

Computed per 8-bit component, with s = source and d = destination. The weight or pin comes from the OpColor
component `w` (16-bit); a zero component counts as 1, and `pin = w >> 8`.

| Mode | Component |
|---|---|
| blend | `(s·w + d·(65536 − w)) >> 16` truncating. **Exception:** when all three weights are equal and are `$7FFF` or `$8000`, use the exact average `(s + d) >> 1`. |
| addPin | `r = s + d`; `r > 255 or r > pin ? pin : r` |
| addOver | `(s + d) & 255` |
| subPin | `r = d − s`; `r < 0 or r < pin ? pin : r` |
| subOver | `(d − s) & 255` |
| addMax | `max(s, d)` |
| adMin | `min(s, d)` |
| transparent | s, except that pixels whose RGB equals the background colour are not drawn |

### 5.5 Hilite

- A source pixel equal to the background colour is not drawn.
- Otherwise:
  - a destination pixel equal to the background colour becomes the highlight colour;
  - a destination pixel equal to the highlight colour becomes the background colour;
  - any other destination pixel is unchanged.

**ROM quirk (rects, polygons and regions filled through RgnBlt):** when the area is **not a single rectangle** and
the pattern is a 1-bit pattern whose rows are all `$00` or `$FF`, with at least one `$FF` row, hilite fills the
whole area as if the pattern were solid black. BitBlt (a single rectangle) and DrawArc (ovals, round rects, arcs) do
not do this.

### 5.6 Pattern placement

A pattern's phase is the port's **local** coordinates plus patAlign, for every verb (rects, regions, ovals, arcs,
polygons, lines; PatExpand) [Code]. For pixel (x, y) in local coordinates:

- **1-bit patterns:** row `(y + patAlign.v) & 7`, bit `(x + patAlign.h) & 7`.
- **Pixel patterns (ROM):** `((x + patAlign.h) mod w, y mod h)`. patAlign.v is not applied.

So SetOrigin (§3.4) shifts patterns on the screen. patAlign itself changes only in InitGraf, DrawPicture (saved,
zeroed and restored) and the ROM's Origin opcode ([PICT.md](PICT.md) §6.4); PortChanged does nothing [Code].

---

## 6. Bitmaps (CopyBits / StretchBits)

Bitmap opcodes transfer `srcRect` of the pixel map to the **mapped** `dstRect`:

- **Mask:** the mapped mask region (Rgn variants), intersected with the clip.
- **Mode:** the opcode's mode (bit 6, ditherCopy, is ignored).

Nothing is drawn if either rect is empty.

### 6.1 Unscaled copies

If srcRect and dstRect are the same size, each destination pixel takes the source pixel at the same offset. Source
pixels outside the bitmap's bounds are skipped (the destination is left unchanged).

### 6.2 Vertical scaling (ROM)

For each destination row, a group of source rows is merged:

```
start = (srcH > dstH && srcH % dstH == 0) ? srcH - 1 : (u16)srcH >> 1     // ROM: exact integer shrinks start at srcH-1
error = -start;  row = srcTop (relative to the bitmap's top);  k = 0
while row < bitmapHeight:
    group = [row++]; error += dstH
    while error <= 0 and row < bitmapHeight: group += row++; error += dstH
    do: dstRow[k++] = group; if k == dstH: done; error -= srcH  while error >= 0
```

- Destination rows left without a group, because the source ran out, are not drawn.
- The 1984 source always started at `srcH / 2`. The ROM's `srcH − 1` for exact integer shrinks changes which rows
  merge. For example, 6 → 2 merges {0,1,2} {3,4,5}, not {0,1} {2,3,4}.

### 6.3 Horizontal scaling

**1-bit sources** use QuickDraw's special row scalers. Destination column j takes source column(s):

| dst/src | Rule |
|---|---|
| ×2, ×4, ×8, ×16 | `j / n` |
| ×1.5 | per source pair a, b: **a b b** (so abcd → abbcdd), i.e. `2·(j/3) + (j mod 3 ≠ 0)` |
| ×3, ×6 | `j / n` |
| any whole multiple of 8 | `j / n` |
| other enlargements | `((f >> 1) + j·f) >> 16` with `f = FixRatio(srcW, dstW) & 0xFFFF` |
| ×½, ×¼ | OR of source columns `2j, 2j+1` / `4j … 4j+3` |
| ×¾ | per 4 source columns a b c d: **a, b\|c, d** |
| other shrinks | source column i lands on `((f >> 1) + i·f) >> 16` with `f = FixRatio(dstW, srcW) & 0xFFFF`; the columns landing on one destination column are ORed |

- The ratio is identified by `FixRatio(srcW, dstW) & 0xFFFF` for enlargements: `$8000`, `$4000`, `$2000`, `$1000`,
  `$AAAA`, `$5555`, `$2AAA`.
- For shrinks it is `FixRatio(dstW, srcW) & 0xFFFF`: `$8000`, `$4000`, `$C000`.
- **Merged 1-bit pixels are ORed:** black wins, over both merged rows and merged columns.

Scaled 1-bit copies read source memory the way StretchBits' row buffer does. Each source row is read linearly from
srcRect's left for `32 × ceil(srcW / 32)` bits:

- bits past srcRect's right edge (up to the long boundary) come from the row's bytes;
- a row that runs past the end of the data reads as 0.

Unscaled copies read only pixels inside the bitmap.

**Deeper sources (ROM)** use the plain fraction stepper for every ratio. There are no special cases, so ×1.5 is
*not* abbcdd:

- **Enlarging:** destination j copies source `((f >> 1) + j·f) >> 16`, with `f = FixRatio(srcW, dstW) & 0xFFFF`.
- **Shrinking:** source i lands on `((f >> 1) + i·f) >> 16`, with `f = FixRatio(dstW, srcW) & 0xFFFF`.

### 6.4 Merging deep pixels (ROM)

Merging happens **at the source depth, before colour conversion**: rows first (per column), then columns.

- **2–8 bits:** the **largest pixel index** in the group. This is not an average.
- **16 and 32 bits:** a truncated per-component average.
  - First, each column's rows are averaged: `sum / n`.
  - Then the columns: `sum >> 1` for two columns, else `sum / columns`.
  - 16-bit components are averaged as 5-bit values, then expanded `(c << 3) | (c >> 2)`.

### 6.5 Colour conversion and modes

- **1-bit sources with the white/black palette** are colorized with F/B (§5.2).
- **Other indexed pixels** take their ColorTable colour.
- **16-bit pixels** expand 5 → 8 bits.
- **32-bit pixels** use their R, G, B.

The colour is then transferred with §5.3 (Boolean modes) or §5.4/§5.5 (arithmetic, transparent, hilite).

### 6.6 Alpha

- QuickDraw ignores alpha.
- A decoder that wants to keep it can store the source's alpha byte for a 32-bit, cmpCount-4 source copied with
  **srcCopy**.
- **ROM rule:** alpha survives only for **unmerged** pixels. Any pixel formed by averaging rows or columns gets alpha
  0.

---

## 7. Text

Text in a picture is **not** stored as pixels. It is a font number, size, style and mode, then characters in Mac
Roman. Reproducing it exactly needs the Macintosh bitmap fonts the picture used. These are Apple resources that the
caller must supply; they are not part of the format. Text in fonts that are not available can only be approximated,
by rendering an outline font at the same position.

### 7.1 Font resources

**`FONT` / `NFNT`** (a bitmap strike; both have the same layout). The two QuickDraws read three fields differently,
each marked below; [FONTS.md](FONTS.md) specifies the resource in full.

| Offset | Type | Field |
|---|---|---|
| 0 | u16 | fontType. Bit 0: has a glyph height table. Bit 1: has a glyph width table. Bits 2–4 (ROM) or 2–3 (Mac OS 9): log₂ depth (colour fonts). Bit 9: colour. |
| 2 | i16 | firstChar |
| 4 | i16 | lastChar |
| 6 | i16 | widMax |
| 8 | i16 | kernMax (≤ 0 when glyphs kern left) |
| 10 | i16 | nDescent. **When ≥ 0, it is the high word of owTLoc** (ROM). |
| 12 | i16 | fRectWidth |
| 14 | i16 | fRectHeight: the strike's height, ascent + descent |
| 16 | u16 | owTLoc: offset **in words**, from this field, to the offset/width table |
| 18 | i16 | ascent |
| 20 | i16 | descent |
| 22 | i16 | leading |
| 24 | i16 | rowWords: the strike's row length in words. Mac OS 9 ignores the top bit; with it set, the ROM's strike is unusable (ClassicMac treats the font as missing) |
| 26 | — | strike: `rowWords × 2 × depth × fRectHeight` bytes, MSB first |
| — | u16[] | location table: `lastChar − firstChar + 3` entries (the characters, the missing symbol, a sentinel); glyph k occupies strike columns `[loc[k], loc[k+1])`. The ROM reads it right after the strike, Mac OS 9 just before the offset/width table (the same place in a well-formed font) |
| 16 + 2·owTLoc | i16[] | offset/width table, same count. Each entry is `offset << 8 \| width`, or **−1 for a missing character**. `offset` is the image's shift relative to kernMax; `width` is the advance. |
| — | u16[] | optional glyph-width table (fontType bit 1): 8.8 fixed widths |
| — | u16[] | optional height table (fontType bit 0): `top << 8 \| rows` per glyph |

Character c uses table slot `c − firstChar`. Characters outside [firstChar, lastChar], or with an offset/width of
−1, use the **missing symbol**, slot `lastChar − firstChar + 1`. A strike whose characters do not run within 0–255
(lastChar before firstChar included) is treated as missing [ClassicMac].

**`FOND`** (a font family):

| Offset | Type | Field |
|---|---|---|
| 0 | u16 | ffFlags. Bit 12: use the family's style-extra table even without fractional widths. Bit 13: **never** use it. Bit 14: ignore the family width tables. |
| 2 | u16 | ffFamID |
| 4 | i16 | ffFirstChar |
| 6 | i16 | ffLastChar |
| 8–15 | | ascent, descent, leading, widMax |
| 16 | u32 | ffWTabOff: offset of the family width tables (0 = none) |
| 20 | u32 | ffKernOff |
| 24 | u32 | ffStylOff |
| 28 | u16[9] | ffProperty: style extra widths, 4.12 per point. [0] plain, then one per style bit (bold, italic, underline, outline, shadow, condense, extend), [8] unused. Words `$8000–$8FFF` are **sign-magnitude** negatives (−(w & `$0FFF`)). |
| 46 | u16[2] | ffIntl |
| 50 | u16 | ffVersion |
| 52 | u16 | association count − 1 |
| 54 | 6 bytes each | association entries: `u16 size, u16 style, u16 resource id` |

- In an association entry, **size 0** means an outline (TrueType `sfnt`) font.
- A style with a non-zero high byte is a colour/depth variant; ignore it for 1-bit text.
- **Width tables** (at ffWTabOff):
  - u16 count − 1;
  - then per table: a u16 style, then `ffLastChar − ffFirstChar + 3` words (4.12 per point).
  - Tables are stepped with the family's own range. A FOND whose ffLastChar is 0 has none.

### 7.2 Resource forks

To load fonts from a suitcase, System file or application, parse its resource fork.

**Header:**

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | dataOffset |
| 4 | u32 | mapOffset |
| 8 | u32 | data length |
| 12 | u32 | map length |

**Map** (at mapOffset):

- `+24` u16: offset of the type list, relative to the map.
- `+26` u16: offset of the name list, relative to the map.
- **Type list:** u16 count − 1; then per type: a 4-char type, u16 count − 1, and a u16 offset of its reference list
  (relative to the type list).
- **Reference entries** (12 bytes each):
  - i16 id;
  - u16 name offset (`$FFFF` = none), relative to the name list;
  - u8 attributes;
  - u24 data offset, relative to dataOffset;
  - u32 reserved.
- **Data** at the data offset: a u32 length, then the bytes.
- **Names** are Pascal strings.

A FOND resource's **name is the family name**. An old-style `FONT` numbered `family × 128 + 0` holds the family name
of old-style fonts.

### 7.3 Choosing a font (Font Manager, ROM)

**Inputs:**

- family (TxFont, after font-name mapping, [PICT.md](PICT.md) §7);
- size (TxSize; 0 means 12);
- face (TxFace);
- the text ratio numer/denom ([PICT.md](PICT.md) §7);
- the space extra;
- the fractional-widths and scaling-disabled flags (glyphState).

The screen is 80 × 80 dpi to the Font Manager, a ratio of 1.

**1. Size to search for:**

```
searchSize = FixRound(FixMul(FixRatio(numer.h, denom.h), size << 16))
```

Only the horizontal ratio is used.

**2. Families to try, in order:**

- The family itself. Family 0 is the **system font**; family 1 is the **application font** (normally 3, Geneva).
- Then fallbacks:
  - for families ≥ `$4000`: the system font, then the application font;
  - otherwise: the application font, Geneva (3), then the system font.

**3. For a family with a FOND** (its association entries):

1. **Exact:** entries of `searchSize`. Choose the style variant (below) and load its resource: `NFNT` of that id
   first, then `FONT`. If it loads, done. If it is missing, go on.
2. **Outline:** if the table has **any size-0 entry**, the family is TrueType. Stop, and render the text with the
   outline fallback. Double and half sizes are never tried.
3. **Double**, then **half** (only if searchSize is even): same as exact. **Skipped when scaling is disabled.**
4. **Nearest:** scan the entries in table order, skipping a leading size-0 entry, and keep the entry whose distance
   `|searchSize − size|` is **≤** the best so far, so later entries win ties (in an ascending table, ties go to the
   larger size). Sizes are taken from the table **whether or not their resource exists**.
   - **Scaling disabled:** stop at the first larger size once a smaller one has been kept. The result is the nearest
     smaller size, and a larger size only when no smaller one exists.
   - If the winner's resource is missing, the FOND search fails (step 4 below); the next-nearest size is not tried.

**Style variant within a size:**

- The entry whose style equals face wins.
- Otherwise score each entry:
  - an entry with any style bit **not** in face scores −1;
  - a subset scores the sum of **bold 4, italic 8, underline 1, outline 3, shadow 3, condense 2, extend 1**.
- The strictly highest score wins; among equal scores, the first.

The **remaining style**, the part the Font Manager must synthesize, is `face & ~entryStyle`.

**4. Old-style fonts** (the FOND search failed or there is no FOND; only families < `$200`):

- `s = (searchSize == 0 ? 1 : searchSize) & $7F`.
- Try `FONT` id `family × 128 + t` for t = s; then 2s (if s < 64); then s/2 (if s is even); then s+1 … 127; then
  s−1 … 1. **With scaling disabled, the downward scan comes first:** s−1 … 1, then s+1 … 127.
- A `FONT` shorter than `$24` bytes counts as missing.
- The remaining style is the whole face.

If nothing is found in any family, the text goes to the outline fallback.

### 7.4 Font Manager output (ROM)

**Style effects,** from the remaining style, added as bytes:

| Bit | boldPixels | italic | shadow | extra |
|---|---|---|---|---|
| bold | +1 | | | +1 |
| italic | | +8 | | |
| outline | | | +1 | +1 |
| shadow | | | +2 | +2 |
| condense | | | | −1 |
| extend | | | | +1 |

Underline sets `ulThick = 1`. Outline + shadow gives shadow 3.

**Remaining stretch,** per axis:

```
ratio  = FixMul(FixRatio(numer.x, denom.x), FixRatio(size, actualSize & $7F))
FOutNumer.x = (ratio + $80) >> 8          // 8.8, rounded half up
FOutDenom   = ($100, $100)
```

Here `size` is the requested TxSize (0 → 12) and `actualSize` the chosen strike's size. The strike is drawn
unscaled, then the whole text image is stretched by FOutNumer/FOutDenom (§7.5).

**Scaling disabled** (glyphState byte 4). Each axis's FOutNumer n is cut, and the rest becomes a factor (**ROM**):

```
numer = $100
while n >= $200: numer <<= 1; n >>= 1          // 16-bit words
while n <  $C0:  numer >>= 1; n <<= 1
if n < $100:     numer = (short)(numer * 3) >> 2; n = (n << 2) / 3     // 3/4 of a power of two
FOutNumer = numer;  factor = n << 8            // Fixed, 1.0 ≤ factor < 2.0
```

- The ROM never returns when n is 0 or ≥ `$8000` (the loops cannot end). A decoder must stop there; ClassicMac.Graphics
  leaves the stretch uncut.
- The horizontal factor multiplies every non-zero width **after** its style extra: `FixMul(width, hFactor)`, skipped
  when the factor is exactly 1.0. The space extra is added afterwards and is not scaled.
- Metrics: ascent, descent and leading (vertical factor) and widMax (horizontal) become
  `FixRound(FixMul(sign-extended byte << 16, factor))`, stored as bytes.

**Family style extra:**

- Applies to fonts found through a FOND when ffFlags bit 13 is clear and either bit 12 is set or fractional widths
  are on.
- `sum = ffProperty[0] + Σ ffProperty[bit + 1]` over the remaining style bits, leaving out bits the chosen family
  width table already covers. Then `extraFixed = FixMul(sum << 4, actualSize << 16)`.
- Fractional widths **off:** `extra = FixRound(extraFixed)`, and widths add `extra << 16`.
- Fractional widths **on:** widths add `extraFixed`, and the `extra` byte stays as in the style table.

**Width table** (Fixed, 256 entries):

- **Fractional widths off:**
  - `width = offset/width low byte`;
  - missing characters (outside the range or −1) take the missing symbol's width.
- **Fractional widths on, strike with a glyph-width table:** those 8.8 widths (`<< 8`). A word of `$FFFF` means
  missing and takes the missing symbol's word.
- **Fractional widths on, family width table** (FOND flags bit 14 clear, no strike widths):
  - Choose the table whose style equals face exactly. Otherwise take the best subset, scoring **bold 1, italic 3,
    underline 5, outline 4, shadow 4, condense 2, extend 2**.
  - **ROM:** the table's words are read from its start for the **strike's** range, word k → character
    `strikeFirst + k`. When the strike's first character differs from the family's, the widths are shifted.
  - The missing symbol's width is word `strikeLast − strikeFirst + 1`, and a word of `$FFFF` means missing.
  - Width = `(u16 word × actualSize) << 4`.
  - Characters outside the strike's range take the missing width.

Then, for all three sources:

- **Every non-zero width** gets the style extra. Zero widths get none. The missing symbol's width gets it too.
- **Space:**
  `widths[32] += FixMul(FixMul(FixRatio(numer.h, denom.h), FixRatio(FOutDenom.h, FOutNumer.h)), spaceExtra)`.
- `widths[13]` (return) = 0.

**Metrics** (GetFontInfo, used by grayishTextOr):

- ascent/descent are the strike's (bytes), scaled as above when scaling is disabled.
- With shadow: +1 ascent, + shadow descent.
- If FOutNumer ≠ FOutDenom, each is scaled `(v · numer.v + denom.v / 2) / denom.v`.

### 7.5 Drawing text (DrText, ROM)

**Input:** the pen at (h, v) plus a fraction `frac` (16 bits), the text, the Font Manager output, the mode, and the
character extra `cx` (Fixed, [PICT.md](PICT.md) §7).

**1. Character extra in strike pixels** (if non-zero):

```
cx = FixMul(FixMul(cx, size << 16), FixMul(FixRatio(numer.h, denom.h), FixRatio(FOutDenom.h, FOutNumer.h)))
```

**2. Width.**

- `width = Σ widths[c] + (c ≠ 32 ? cx : 0)`, as Fixed.
- **Code 32 is always a space.** Its glyph is never drawn, and it never gets cx.

**3. Mode.**

- `mode = txMode & ~8`: the pattern bit only; there is no `& 7`.
- If bit 6 is set, the text image is also the transfer **mask**, so only glyph pixels are touched, and bit 6 is
  cleared.
- Arithmetic and hilite modes reach the transfer as they are.

**4. textRect.**

- **left** = `h + kern`:
  - `kern` is 0 unless kernMax < 0.
  - When kernMax < 0: look up the offset/width entry of the **first character by its raw character code**, not
    `code − firstChar` (**ROM bug**). If that entry is −1, use entry 0 instead.
  - `kern = min(0, (entry >> 8 & $FF) + kernMax)`, applied only when that sum ≤ 0.
- **right** = the high word of `((h << 16) | frac) + width`, **with no extra slop**. Ink past the final pen
  position is clipped.
- **If the remaining style is non-zero:**
  - `lean = (short)((u16)(sign-extended italic byte) × (u16)(ascent − 1)) >> 4`
  - `slop = (sbyte)(lean + bold − extra)` — summed in a **byte**, so it wraps above 127
  - `right += slop + (shadow == 0 ? 0 : shadow < 4 ? shadow + 1 : 4)`
  - `left −= (short)((u8)italic × (u16)descent) >> 4`
- **top** = `v − ascent`; **bottom** = `top + fRectHeight`. These are the strike's ascent and fRectHeight.

**5. Stretch.**

- When FOutNumer ≠ FOutDenom, the pen advances by `width × numer.h / denom.h` (unsigned, truncated).
- The image goes to `MapRect(textRect, (v, h, v + denom.v, h + denom.h), (v, h, v + numer.v, h + numer.h))`.
- **New pen fraction:** `(frac + advance) & $FFFF`.

**6. Buffer.**

- A 1-bit off-screen image with its left edge at `bufLeft = left & ~31` (the long-aligned pixel at or left of it).
- It is `rowLongs = ((u16)(right − bufLeft) >> 5) + 2` longs wide and fRectHeight rows tall, cleared to 0.
- One extra long follows it.
- Treat the image as **one bit stream**, rows back to back. Glyph writes and style passes that cross a row edge
  flow into the neighbouring row.

**7. Characters.**

- `charLoc = ((h + kernMax − bufLeft) << 16) | frac`. The ROM starts at the pen's own fraction; the 1985 code always
  used ½.
- For each byte:
  - **Space (32):** `charLoc += widths[32]`.
  - **Otherwise:** `step = widths[c] + cx`. Find the glyph: slot `(u16)(c − firstChar)`; if that is past the range
    or −1, use the missing symbol. If the missing symbol is also −1, skip the character **without advancing**.
  - Draw at `x = (entry >> 8 & $FF) + (short)(charLoc >> 16)`, then `charLoc += step`.
  - The glyph's strike columns `[loc[k], loc[k+1])` (skip if empty) are **ORed** into the image at x, for rows
    `[top, top + rows)` of the height table (default: all rows).

**8. Styles,** in this order:

1. **Bold:** `boldPixels` passes. Each pass ORs every bit of the stream into the next bit (right by one; the last bit
   of a row carries into the next row), over the image plus the extra long.
2. **Italic:** working upward from the second-to-last row, keep an accumulator (`+= italic` per row, **not
   wrapped**). Each row takes the bits `acc >> 4` positions to its left in the stream; bits before the image read 0.
   The bottom row does not move.
3. **Underline** (ulThick ≠ 0 and descent ≥ 2):
   - `ink = row B | row B+1 | row B+2`, where B = ascent is the baseline row, and B+2 := B when descent = 2.
   - Spread `ink` one pixel left and right.
   - Row `B+1 |= ~spread` across the **whole** image row. There is no right trim; it runs to textRect's right edge,
     including the slop.
4. **Shadow / outline** (shadow ≠ 0; a **colour port**):
   - `buf2` = the image followed by 4 zero rows.
   - Smear buf2 right `(shadow & 3) + 1` times over the image's bits plus one long (bits can spill into the first
     extra row).
   - Smear it down the same number of times over all rows (each row |= the row above, from the bottom up).
   - Then XOR the image **one bit later and one row lower** into buf2: `buf2[W + j] ^= image[j − 1]` for j over the
     image plus one long.
   - Transfer buf2 from `(top, left, bottom + 4, right)` to that rect offset by (−1, −1) (mapped if stretched).
   - **Nothing else is drawn.** The letters' inside is left untouched, so outline text shows the background through
     it. (A black-and-white port would refill the inside for srcOr/srcBic.)

**9. Transfer.** Without shadow, transfer the image's textRect to the stretched rect with the mode. The image is a
1-bit bitmap with bounds `(top, bufLeft, bottom, right)`, so the scaling rules of §6 apply.

### 7.6 grayishTextOr (mode 49)

Pictures never record it, because it is drawn outside picture recording, but a hand-built picture may use it. On a
colour port:

1. Take the foreground and background as 16-bit colours `c·257`.
2. `mid = (fg + bk) >> 1` per component, `+2` if `< $8000`.
3. `gray` = the high byte of mid.
4. If `dist(gray·257, mid)` is less than half of `dist(gray·257, bk)` and less than half of `dist(gray·257, fg)`,
   where `dist` is the largest component difference, draw the text srcOr in `gray`.
5. Otherwise draw it srcOr in the foreground colour, then paint the rect `(v − ascent, h, v + descent, h + width)`
   with the **gray pattern** (`AA 55 AA 55 …`) in **patBic**:
   - ascent and descent are the metrics of §7.4;
   - width is the unscaled integer width from step 2 of §7.5.

### 7.7 Outline fallback

When no bitmap strike can be used (no fonts supplied, the family is missing, or it is TrueType), render the text with
an outline font:

- at the mapped pen position, with the baseline at v;
- at TxSize, with TxFace;
- through the text mode as a 1-bit mask.

The picture's font name, if known from a fontName opcode, helps choose a similar font. This cannot match a Macintosh
exactly.

---

### 7.8 Character extra, measuring and font metrics

- **CharExtra(extra: Fixed)** (colour ports; old ports ignore it) stores the extra **per point**:
  `chExtra = (short)(FixDiv(extra, size << 16) >> 4)`, a 4.12 value, where size is txSize, or when that is 0 the
  system font size (SysFontSize, then FMDefaultSize, then 12). FixDiv rounds. TextSize always clears chExtra. Drawing
  and measuring use `(short)chExtra << 4` (plus the picture's LineJustify spacing) × the strike size × the text scale ×
  FOutDenom / FOutNumer, after every character but spaces (§7.5) [Code].
- **TextWidth / StringWidth / CharWidth:** `trunc(trunc(sum) × FOutNumer.h / FOutDenom.h)`, where sum is StdTxMeas's
  Fixed width (§7.4: the strike's widths with the style extra, space extra, character extra and the FScaleDisable
  factor), with an unsigned multiply and divide. There is no text scaling (numer = denom = 1). Always truncated, never
  rounded [Code].
- **GetFontInfo** takes FMOutput's metrics, not the strike's. widMax gets FOutExtra (bold +1, outline +1, shadow +2,
  condense −1, extend +1, or the family's style extra; italic 0); shadow and outline add 1 to the ascent and the
  shadow count (outline 1, shadow 2, both 3) to the descent. Then each is stretched, `(value × numer + denom / 2) / denom`,
  vertically for ascent, descent and leading and horizontally for widMax [Code].

## 8. Screen depths

A picture drawn on a 1, 2, 4, 8 or 16-bit screen looks different. For indexed depths, QuickDraw works on colour-table
indices, and ditherCopy dithers.

- **The screen:** a GWorld of that depth with its default colour table:
  - 1 bit: white, black;
  - 2 bits: greys FFFF, ACAC, 5555, 0000;
  - 4 bits: `clut` 4;
  - 8 bits: `clut` 8;
  - 16 bits: 5-5-5 pixels, bit 15 zero.
- **Output:** each pixel is the colour of its entry. 16-bit pixels get each 5-bit field replicated to 8 bits:
  `(c << 3) | (c >> 2)`.
- Everything below is verified pixel for pixel against Mac OS 9.

### 8.1 RGB to index

**The inverse table (MakeITable, resolution 4).**

1. Take a cube of 18³ cells: a 1-cell border and 16³ interior cells.
2. Seed the entries in this order: entry 0, the last entry, then entries 1..n−1. Each seed goes to cell
   `(R8 >> 4, G8 >> 4, B8 >> 4)` + 1 on each axis.
3. The first entry to reach a cell owns it. A later entry is a hidden colour, chained to the owner.
4. Fill breadth-first from the seeds, in neighbour order +B, −B, +G, −G, +R, −R. An empty neighbour takes the
   colour and joins the queue.
5. The table is the 4096 interior cells, red most significant.

The standard `clut` 4 and 8 have no hidden colours at resolution 4.

**Grey tables** (`clut` 1 and 2) map luminance to an entry:
1. Put each entry at its red byte (the lowest index wins on equal reds).
2. Fill the gaps by alternating left-to-right and right-to-left passes, each copying a neighbour, until none is left.
   This gives the nearest entry, with exact ties going to the darker one.

**The lookups:**

| Where | Colour screens | Grey screens | 16 bits |
|---|---|---|---|
| Direct pixels copied (srcCopy, and Boolean results of direct sources) | `table[(R>>4)<<8 \| (G>>4)<<4 \| B>>4]` | `links[(5R+9G+2B) >> 4]` | `c >> 3` |
| Arithmetic and colorized results | the same | `links[(5(R&$F0) + 9(G&$F0) + 2(B&$F0)) >> 4]` (the cell's corner) | `c >> 3` |
| Color2Index (fore, back, hilite, indexed sources) | the table, then the hidden-colour chain (Mac OS 9: nearest by Manhattan distance on 16-bit components) | Mac OS 9 `links[(5R+9G+2B) >> 12]` on 16-bit components; ROM `links[((((R+G)/2 + B)/2 + R)/2 + G)/2 >> 8]` | `c16 >> 11` |

### 8.2 The port's indices

- **Fore and back:**
  - fgI = Color2Index(fore) and bkI = Color2Index(back), from the exact 16-bit colours. The classic `FgColor`/`BkColor`
    constants map to the QDColors table ([PICT.md](PICT.md) §4.7).
  - On 1- and 2-bit screens, if fgI = bkI while the colours differ, fgI = Color2Index(complement of fore). For example,
    yellow on white draws black on a 1-bit screen.
- **Hilite:** hiI = Color2Index(hilite). If that equals bkI, use Color2Index of the complement instead.

### 8.3 Transfer modes on indices

Let M = 2^depth − 1 (on 16 bits, M = $7FFF). The not modes invert the source bit or source index first.

**1-bit sources and 1-bit patterns:**

| Mode | Result |
|---|---|
| copy | on → fgI, off → bkI |
| or | on → fgI |
| bic | on → bkI |
| xor | on → d ⊕ M |

**Indexed sources:**

- **copy:** `Color2Index((rgb & back) | (~rgb & fore))` on 16-bit components; notSrcCopy complements rgb first.
  - When the §8.2 collision rule replaced fgI, the fore colour here is fgI's colour.
- **or / bic / xor:** convert the source with Color2Index to s, then:
  - or: `(s & fgI) | (~s & d)`;
  - bic: `(s & bkI) | (~s & d)`;
  - xor: `d ^ s`.

**Direct sources:**
- The 32-bit rules (§5), applied to the screen colour of d.
- Then the table (Mac OS 9 colorizing included).
- On 16 bits, Mac OS 9 colorizes on 5-bit fields:
  - copy: `((32 − s)·F + (s + 1)·B) >> 5`;
  - or / bic: `((32 − s)·C + (s + 1)·d) >> 5`.

**Pixel patterns:**
- Each colour is converted to a value p: Color2Index for indexed patterns, the table for direct ones.
- Then fore = all ones and back = 0: copy p, or `d | p`, xor `d ^ p`, bic `d & ~p`.

**RGB patterns (type 2):** PatDither builds a 2×2 cell with the layout below. Pixel (x, y) takes cell
`[(y & 1) · 2 + (x & 1)]`, with the pattern alignment applied.

```
0 1
2 3
```

1. Fill the slots in the order 0, 1, 3, 2.
2. Keep running 16-bit totals, starting at 0. For each slot:
   1. Add the colour to the totals.
   2. `i = Color2Index(clamp(total, 0, $FFFF))`.
   3. Subtract Index2Color(i) from the totals.
3. Index2Color on 16 bits replicates each field: `(c << 11) | (c << 6) | (c << 1) | (c >> 4)`.

**Arithmetic modes:**
- An indexed source is first converted to its screen index, and its colour is that entry's colour.
- Apply the §5.4 operation (Mac OS 9 rounding) to the source colour and d's colour.
- Convert the result with the arithmetic lookup (§8.1).
- **16 bits:** 5-bit fields, with these rules:
  - blend: `(s·w + d·(65536 − w) + $8000) >> 16`; the average `(s + d) >> 1` only when all three weights are
    $7F80..$807F;
  - pins at the OpColor's top 5 bits;
  - addOver and subOver are modulo 32.

**transparent, hilite, invert, 1-bit screens:**
- **transparent:** writes s when s ≠ bkI.
- **hilite:** where s ≠ bkI, d = bkI becomes hiI and d = hiI becomes bkI.
- **Invert:** `d ^ M`.
- **1-bit screens:** the arithmetic modes 32–39 become srcCopy, srcBic, srcXor, srcOr, srcOr, srcBic, srcXor, srcOr,
  and hilite becomes xor.

### 8.4 ditherCopy

Only CopyBits of a direct source in srcCopy with the ditherCopy flag (64) dithers. Every other depth reduction
truncates.

**Mac OS 9:**
- **Scope:** rows of the visible bounds, starting at the top, serpentine with the first row left to right. The error
  buffer and the carry start at 0 for each call.
- **Per pixel:**
  - `v = clamp(c + carry + buf[x], 0, 255)` per component.
  - Choose the entry with the table.
  - `e = v − (the entry's 8-bit colour)`.
  - `carry = e >> 1` (floor).
  - `buf[x] = e − (e >> 1)`, stored as a signed byte, so +128 wraps.
- **Clipped pixels** reset `buf[x]` and the carry.
- **16 bits:** the error is v's low 3 bits.
- **Grey screens:** dither the luminance `(5R+9G+2B) >> 4` against the entry's blue byte.
- **1 bit:** black when v < 128, with error v or v − 255.

**ROM:**
- **Scope:** rows from the first visible one, always across the destination rect's full width. Clipped pixels are
  converted but not drawn.
- **Error split:** `buf[x] = e >> 1` (floor) and `carry = (e >> 1) + (e & 1)` (ceil), with no wrapping.
- **Grey screens:** luminance `((R + G + 2B)/4 + R + 2G)/4`, against the entry's red byte.
- **16 bits:** ordered, `min(c + D[row & 3][x & 3], 255) >> 3`, with rows counted from the first visible one and x
  from the rect's left:

  ```
  D = 0 5 1 4
      6 3 7 2
      1 4 0 5
      7 2 6 3
  ```

### 8.5 Text

- **Mac OS 9:** 1-bit text on indexed screens follows §8.3.
- **ROM synthetic strikes (2–8 bits):**
  - Glyph bits map to Color2Index(black) and to Color2Index(white), which is 0.
  - On the standard tables this gives the same pixels as expanding the 1-bit strike, so they are not modelled
    separately.
- **Not yet verified:** the ROM's arithmetic-mode text on indexed screens (colorizing stripped, ink = the black
  index).

### 8.6 Not covered

- **QuickTime images on indexed screens:** the Image Compression Manager's codecs dither to the screen themselves.
- **Custom screen colour tables and search procs.**

---

## 9. Mac OS 9 differences

Mac OS 9's native QuickDraw is a rewrite, not a port. Where it differs from sections 2–8 and the picture rules of [PICT.md](PICT.md), a picture shown on Mac OS 9
follows the rules below. It still uses the ROM's MapPt, MapRect, ScalePt, FixMul and FixRatio.

### 9.1 Bitmaps

- **Scaling:** one DDA for rows **and** columns, at every depth, replacing the ROM's row DDA, column stepper and
  1-bit special cases:
  - Enlarging: destination k takes source `ceil(s(2k + 1) / 2d) − 1`.
  - Reducing: destination k merges the sources between `b(k−1)` and `b(k)`, with `b(k) = floor((k + 1) s / d + ½)`
    and `b(−1) = 0`.
  - Exact integer reductions agree with the ROM; other ratios do not. For example, 3 → 4 gives 0, 1, 1, 2 (ROM
    0, 0, 1, 2), a 1-bit ×1.5 gives aabccd, and 3 → 269 maps column 179 to source 2.
- **Clipped scaling:** the DDA starts at the first visible row or column n (the top or left of the visible area)
  without stepping to it. Where its error is exactly 0 there, that one row or column is off:
  - enlarging, when `s(2n + 1)` is a multiple of `2d`: it takes source `s(2n + 1) / 2d`, one more than unclipped;
  - reducing, when `2ns` is an odd multiple of `d`: its group starts one source earlier.
  - Later rows and columns are unchanged.
- **Merging:** 1–8 bits take the largest index (1 bit: OR). 16/32 bits widen 16-bit components to 8 bits, then
  average rows and then columns **rounded**: `(sum + n/2) / n`.
- **Colorizing** applies:
  - to copy modes when the foreground isn't black or the background isn't white;
  - to or modes when the foreground isn't black;
  - to bic modes when the background isn't white;
  - never to xor modes.
- **How a colorized pixel is computed:**
  - 1-bit sources and indexed srcCopy / notSrcCopy work as in the ROM (bitwise).
  - srcOr, srcBic, notSrcOr and notSrcBic, and colorizing copies of direct sources, **blend each channel**. With s
    the source channel (8-bit), `C` = fore (or modes) or back (bic modes), and `s := 255 − s` for the not modes:
    - copy: `((256 − s)·F + (s + 1)·B) >> 8`
    - notCopy: `((256 − s)·B + (s + 1)·F) >> 8`
    - or / bic: `((256 − s)·C + (s + 1)·d) >> 8`
- **Blend:** `(s·w + d·(65536 − w) + $8000) >> 16`. The exact average `(s + d) >> 1` is used when all three
  weights lie in `$7F80…$807F`; a zero weight counts as 1.
- **Pattern hilite:** follows the pattern everywhere. There is none of the ROM's whole-area quirk.
- **Pixel patterns:** use the vertical alignment too, which is always 0, since Origin does not move it.

### 9.2 Shapes

- **Arc slopes** use a half-up fixed multiply, `(a·b + $8000) >> 16`. This rarely moves an arc edge by one pixel.
  Every other arc computation is the same as the ROM.
- **Pen modes:** bit 6 is dropped first (64–79 draw as 0–15), then the same acceptance rule as the ROM applies
  (§3.3).
- **Frames of ovals, round rects and arcs** have no emptiness or pen-size test:
  - The inner shape spans rows `[top + penV, bottom − penV)`, with an oval of `max(0, ovalW − 2penH)` by
    `max(0, ovalH − 2penV)`.
  - At zero inner width (always when the pen is wider than half the shape), its edges stay fixed at
    `iL = left + penH` and `iR = right − penH`.
  - Inner rows draw two slabs, `[oL, iL)` and then `[iR, oR)`, clamped to the rect. When `iL > iR` the slabs
    overlap and the overlap is painted **twice**: copy modes are unchanged, XOR cancels, and arithmetic modes apply
    twice. Narrow rows can paint outside the oval.
  - A zero pen height makes every row an inner row. For example, a 2×8 oval framed with pen (v 0, h 2) paints
    `[0, 2)` on all 8 rows.
  - Rows outside the inner span are solid, as in the ROM.

### 9.3 Text and the Font Manager

- **Size folding:**

  ```
  if any numer/denom component is 0: numer = denom = (1, 1)
  take absolute values; if numer.v == denom.v: both 1; if numer.h == denom.h: both 1, no fold
  r = (numer.h << 16) / denom.h;  p = r·size;  if (p >> 16) < 4: no fold
  newSize = (p + $8000) >> 16;  f = FixRatio(size, newSize)
  numer = ((FixMul(f, r) + $80) >> 8, (FixMul(f, (numer.v << 16) / denom.v) + $80) >> 8);  denom = (256, 256)
  ```

  - The search uses newSize (or the original size, without folding). Size choice is otherwise as in §7.3.
  - **Stretch:** `D6 = (size << 16) / strikeSize`, then per axis `H = ratio × D6`, a half-up multiply that is
    skipped when D6 = 1.0. `FOutNumer = H ≤ $7FFF7F ? (H + $80) >> 8 : $7FFF`.
  - Examples: 9 pt at 4/3 → 12 pt, numer (256, 192); 12 pt at 3/2 → 18, (256, 171); 9 pt at 5/4 → 11, (262, 209).
- **Font fallback order:** the application font, then the lowest-numbered font family, then the system font, then
  Geneva.
- **Style variants** are matched against `face & $9B`, with the ROM's scores. The synthesized remaining style is
  the unmasked face minus the entry's style, so underline, condense and extend are always synthesized.
- **Family width tables** are walked with the family's own character range. The strike's character c takes word
  `c − ffFirstChar`, and the missing symbol takes word `ffLastChar − ffFirstChar + 1`.
- **Text modes:**
  - transparent (36) and ditherCopy (64) draw as srcOr;
  - grayishTextOr (49) draws srcOr in the gray GetGray finds (§7.6).
- **Glyph placement ignores the pen fraction.** With S the running Fixed advance from 0, a glyph goes at
  `pen.h + ((S + $8000) >> 16) + its offset + kernMax`. The pen still ends at `(pen.h << 16) + fraction + advance`,
  so PnLocHFrac only moves the end pen.
- **Italic** pivots on the row just below the baseline (y = pen.v), with `it` = the italic pixels (default 8):
  - rows above shift right by `((pen.v − y) × it) / 16`;
  - that row and the rows below shift left by `((y − pen.v + 1) × it) / 16`;
  - both divides truncate.
- **Text rect** (ink past the final pen position is not clipped), relative to the pen, starting from the union U of
  the glyph images and the spaces' origin points:
  - `W = (advance + $8000) >> 16`
  - `right = U.right`; if the mode is 0, the mode is above 3, or the text is underlined: `right = max(right, W)`
  - `right += bold + (italic ? it × (ascent − 1) / 16 − extra : 0) + (shadow ? min(shadow + 1, 4) : 0)`
  - if `extra > 0`: `right = max(right − extra, W)`
  - `left = min(U.left − (italic ? it × (descent − 1) / 16 : 0), 0)`
- **Character extra:**
  - It is added only to characters that are not spaces and have a width above 0; carriage returns and zero-width
    characters get none.
  - It is scaled with half-up multiplies.
- **A lone carriage return** (a text of exactly one byte 13) draws nothing and leaves the pen. A carriage return
  inside longer text draws its glyph with advance 0.
- **Stretched text advances the pen** by `FixMul(width, numer/denom)`, rounded half up.
- **Stretched text** has no routine of its own. The 1-bit text buffer is stretched from the text rect to the mapped
  rect, and the mapping is the same for every mode:
  - **The destination rect:** each pen-relative edge o maps to `sign(o) × floor((|o| × N + 128) / 256)`, with N the
    8.8 stretch of that axis. This is MapRect about the pen.
  - **Rows and columns:** the ordinary DDA (§9.1) on the rect sizes S → D, from the rect's own top-left:
    - enlarging, `ceil(S(2k+1)/2D) − 1`;
    - reducing, the OR of `[end(k−1), end(k))`, where `end(k) = floor((k+1)S/D + ½)`.
  - **The mode changes only the source rect.**
    - srcOr, srcXor and srcBic use the union of the glyph images. Vertically, that is the rows that hold ink, raised to
      at least the baseline; spaces count as points on the baseline.
    - srcCopy, modes above 3 and underlined text extend it to −ascent..descent and the rounded width.
    - For example, Geneva 9 text spans rows −7..2 (ink) or −10..2 (extended).
  - **Not covered:** banding (a buffer over 32 KB) and run splits restart the rounding per band. Outline and shadow
    shrink the rect by 1 first.
- **Colour bitmap fonts** (an NFNT with fontType bits 2–4 = log₂ depth > 0, strike rows `rowWords × 2 × depth` bytes;
  in a FOND the association's style high byte is the depth code):
  - At a chosen size and style, the last association of the same size and low style byte with depth code 1–4 is
    used instead of the plain strike.
  - Colours: the `fctb` with the NFNT's id (a ColorTable, [PICT.md](PICT.md) §4.6), else the standard `clut` of the strike's depth.
  - Each glyph is drawn on its own with **opaque srcCopy**, whatever the text mode and style: its whole box
    (image width × font rect height, top at `pen.v − ascent`) is copied from the strike through the colours, clipped.
  - Glyph x = `HiWord(penFixed + scaled) + kernMax + offset`, where penFixed = `(pen.h << 16) | fraction`, `scaled`
    is the unscaled Fixed advance so far (widths plus chExtra, as in §7) times `numer.h / denom.h` when stretched,
    and the glyph offset is **not** scaled.
  - Stretched width = `(2 × bits × numer.h + denom.h) / (2 × denom.h)` (half up); the rows scale with MapRect about
    the pen. Glyphs of width 0 draw nothing.
  - The pen advances as for 1-bit text.

### 9.4 The port and measuring

- **Pixel patterns** also apply patAlign.v, and a port can have its own pattern origin for them (QDSetPatternOrigin,
  grafVars flag `$4000`; the picture opcode `$0200`) [Code]; ClassicMac does not model the pattern origin.
- **SetOrigin** does nothing without a port and clears QDErr [Code].
- **CharExtra:** the divide truncates and the result is clamped to ±$7FFF; TextSize clears chExtra only when the size
  changes; the extra is added only to characters with a width, and is 0 for non-native scripts [Code].
- **TextWidth:** a signed divide. Strings under 32 characters are cached, and with a stretched font a cache hit can be 1
  more (`floor(FixTxWid × n / d)` rather than `trunc(w) × n / d`) [Code]; ClassicMac does not model the cache.
- **GetFontInfo:** the FScaleDisable factor is applied twice, and a negative leading works [Code]; ClassicMac applies it
  once.

### 9.5 Not yet pinned down

These Mac OS 9 differences are known but not yet exactly specified or verified. ClassicMac.Graphics draws them the ROM way
in both modes:

- **Quirks found by reading code but not reproduced:**
  - italic rows that need shifts of 32 bits or more (from about 64 rows at the default slant) are corrupted;
  - a clipped reduction's right-edge span is one column short;
  - a destination rect past the pixel map's bounds picks its scaling routine from truncated widths.

Indexed and 16-bit screens are covered in §8. Old black-and-white ports and the destination's alpha byte do not
apply.

---

## 10. Not covered

Everything this document knows about but ClassicMac.Graphics does not reproduce, in one place.

**Text**
- **TrueType (`sfnt`) text.** It goes to the outline fallback. On Mac OS 9 this includes stretched text whose folded
  size has no bitmap strike (§9.3).
- **Stretched text edge cases** (§9.3): banding (a text buffer over 32 KB) and run splits restart the rounding per
  band; outline and shadow shrink the rect by 1 first.
- **The ROM's arithmetic-mode text on indexed screens** is not verified (§8.5).

**Mac OS 9 quirks found in code but not reproduced** (§9.5)
- Corrupted italic rows that need shifts of 32 bits or more.
- A clipped reduction's right-edge span that is one column short.
- A destination rect past the pixel map's bounds picking its scaling routine from truncated widths.

**Screens** (§8.6)
- Custom screen colour tables and search procs.
- QuickTime images on indexed screens, which the codecs dither themselves.
