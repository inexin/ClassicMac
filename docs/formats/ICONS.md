# Icon, cursor and pattern resources

Decoded by `ClassicMac.Resources.Decoders` (`QuickDrawResources`); what the exports contain is in
[EXPORT-MANIFEST.md](EXPORT-MANIFEST.md). Colour tables and pixel maps are as in [PICT.md](PICT.md); scaling and screen depths
as in [QUICKDRAW.md](QUICKDRAW.md).

These are the QuickDraw image resources of classic Mac OS resource forks. They are not part of PICT, but they use
the same structures. All are big-endian.

- **1-bit images:** 1 = black. Where there is a mask, a 0 mask bit is transparent.
- **Indexed images** are converted with the high byte of each 16-bit colour component, as in PICT.

| Type | Size | Layout |
|---|---|---|
| `ICON` | 32×32 | 128 bytes, 1 bit per pixel, unmasked |
| `ICN#` | 32×32 | 128 bytes icon, then 128 bytes mask |
| `ics#` | 16×16 | 32 bytes icon, then 32 bytes mask |
| `icm#` | 16×12 | 24 bytes icon, then 24 bytes mask |
| `SICN` | 16×16 each | any number of 32-byte 1-bit icons, unmasked |
| `icl4`, `icl8` | 32×32 | 4- or 8-bit pixels in the standard colour table |
| `ics4`, `ics8` | 16×16 | 4- or 8-bit pixels in the standard colour table |
| `icm4`, `icm8` | 16×12 | 4- or 8-bit pixels in the standard colour table |
| `PAT ` | 8×8 | 8 bytes |
| `PAT#` | 8×8 each | u16 count, then 8 bytes each |
| `CURS` | 16×16 | 32 bytes data, 32 bytes mask, then the hotspot as a Point (v, h) |

**4- and 8-bit icons:**

- The standard colour tables are the Mac `clut` 4 and 8. Take their exact 16-bit values' high bytes.
- `clut` 8 is the 6×6×6 cube **without black** (215 entries, white first, red slowest), then red, green, blue and
  gray ramps of `EE DD BB AA 88 77 55 44 22 11`, then **black at 255**.
- There is no mask in the resource. The mask of the 1-bit icon list with the same id and size (`ICN#`, `ics#`,
  `icm#`) applies. Without an icon list the Icon Utilities draw nothing (noMaskFoundErr, −1000). An all-zero mask
  draws nothing.
- **An icon list without its mask half** (the resource is only the icon) gets a computed mask, CalcMask:
  - flood-fill the white pixels 4-connected to the edges;
  - the mask is every pixel the flood did not reach, which is the icon's silhouette including enclosed holes.
- **Which icon is drawn:** see "Drawing an icon suite" below.
- **`cicn`** (PlotCIcon):
  - fore/back are forced to black/white;
  - the 1-bit BitMap is drawn if it exists and the screen depth is at most 2, else the PixMap;
  - the mask is the icon mask.

**Cursors:**

| Mask bit | Data bit | Result |
|---|---|---|
| 1 | 1 | black |
| 1 | 0 | white |
| 0 | 0 | transparent |
| 0 | 1 | **inverts** the screen |

**`cicn`** (colour icon):

- A header, then variable-length data:
  - a 50-byte PixMap (baseAddr, rowBytes & `$3FFF`, bounds, pmVersion, packType, packSize, hRes, vRes, pixelType,
    pixelSize, cmpCount, cmpSize, planeBytes, pmTable, pmReserved);
  - a 14-byte mask BitMap (baseAddr, rowBytes, bounds);
  - a 14-byte 1-bit BitMap (rowBytes 0 when absent);
  - a 4-byte iconData;
  - the mask bits, then the 1-bit bits;
  - a ColorTable ([PICT.md](PICT.md) §4.6);
  - the pixels, unpacked, `rowBytes × height` bytes.
- The mask masks the colour pixels. Rows beyond the mask's data are unmasked.

**`crsr`** (colour cursor):

- Header:

  | Offset | Size | Field |
  |---|---|---|
  | 0 | 2 | crsrType: `$8000` monochrome, `$8001` colour |
  | 2 | 4 | offset of the PixMap |
  | 6 | 4 | offset of the pixels |
  | 10 | 10 | reserved |
  | 20 | 32 | 1-bit data |
  | 52 | 32 | mask |
  | 84 | 4 | hotspot, as a Point (v, h) |
  | 88 | 8 | reserved |

- The PixMap's pmTable is the offset of its ColorTable.
- SetCCursor **never reads the 1-bit data**. The cursor is drawn as `screen = (screen AND NOT mask) XOR image`:
  - Mask 1: the colour pixel, converted to the screen depth.
  - Mask 0 on a 16/32-bit screen: the screen is XORed with the pixel's complement. White is transparent, black
    inverts, other colours XOR their complement.
  - Mask 0 on a screen of 8 bits or fewer: the screen index is XORed with the pixel's index.
- A `CURS`'s data bit 1 under mask 0 inverts (complements) the screen.
- Hotspots are clamped to 0..15.

**`ppat`** (pixel pattern):

- Header:

  | Offset | Size | Field |
  |---|---|---|
  | 0 | 2 | patType |
  | 2 | 4 | offset of the PixMap |
  | 6 | 4 | offset of the pixels |
  | 10 | 10 | reserved |
  | 20 | 8 | the 1-bit pattern |

- The pixel data runs from the pixels offset to pmTable. A table before the pixel data makes GetPixPat fail, so the
  resource does not load. The ColorTable (at pmTable) is read unless the PixMap is RGB direct (pixelType 16).
- Types 1 and 3 decode the PixMap.
- Type 0: the pattern is the **first 8 bytes of the pixel data** (at the pixels offset), not the 1-bit pattern at
  offset 20.
- Type 2 (RGB): the colour is the ColorTable's **entry 4** (table + $2A); the resource's pixels are ignored.
  - A 32-bit screen draws it solid.
  - Other depths draw PatDither's 2×2 cell ([QUICKDRAW.md](QUICKDRAW.md) §8).
- Mac OS 9 fails to load types above 3.
- **`ppt#`:** a u16 count, then that many u32 offsets from the resource start. Each element is a complete flattened
  `ppat`, whose own offsets are relative to the element's start; element i ends where element i+1 begins.

## Drawing an icon suite (PlotIconID, PlotIconSuite)

The Icon Utilities draw a suite (the `ICN#`/`icl4`/`icl8`, `ics#`/`ics4`/`ics8` and `icm#`/`icm4`/`icm8` of one ID;
on Mac OS 9 a suite may also hold the 48 × 48 and 32-bit members) into a port. Mac OS 9's native Icon Utilities and the
ROM's differ where noted [Code]. PlotIconID never reads `ICON` or `SICN`.

1. **The mask group** comes from the rect's size (before alignment); the first 1-bit member in the list is used, and
   the group sets the source size:
   - Mac OS 9: width or height ≥ 48 → `ich#` `ICN#` `ics#` `icm#`; both under 32 and height > 12 → `ics#` `ICN#`
     `icm#` `ich#`; both under 32 → `icm#` `ics#` `ICN#` `ich#`; otherwise `ICN#` `ich#` `ics#` `icm#`.
   - ROM: width or height ≥ 32 → `ICN#` `ics#` `icm#`; height > 12 → `ics#` `icm#` `ICN#`; else `icm#` `ics#` `ICN#`.
   - No 1-bit member → noMaskFoundErr (−1000), nothing drawn. The mask is never taken from colour data.
   - The mask is the member's second half; a member without it gets CalcMask of its image (Mac OS 9 fails the call for
     a short `ich#`). An `icm#` longer than one icon and its mask is treated as SICNs: the rect is re-centred to 16
     rows (`c = (top + bottom) >> 1`, top `c − 8`), the source is 16 tall, and the mask is its second half (or
     CalcMask for one SICN).
   - An all-zero mask draws nothing (noErr).
2. **Alignment** never resizes: the member always fills the rect, however unevenly. With an alignment, the mask's
   bounding box is mapped into the rect (MapPt: `(|v − from| × toSize + fromSize / 2) / fromSize`, sign restored; no
   scaling when the sizes are equal), and the rect is moved so the box is centred (`((a + b) − (box a + box b)) >> 1`,
   a floor) or on the chosen edge. An off-centre mask moves even at 1:1.
3. **Colour or 1-bit:** colour data when (depth > 4 and not open) or (depth 4, not selected, none or offline); the
   colour member comes only from the mask's group:
   - Mac OS 9 (by the port's depth): 16/32 bits `il32` `icl8` `icl4` (then the 1-bit member); 8 bits `icl8` `il32`
     `icl4`; 4 bits `icl4`; 1–2 bits none (the small, mini and huge groups likewise with their own members).
   - ROM: `icl8` at 8 bits and more, else `icl4` at 4 bits and more.
   - Otherwise, or with no colour member, the 1-bit path.
4. **Colour data** (PlotDeep): fore- and background forced black and white; the data drawn through the mask with
   CopyMask (both stretched to the rect). Label, selection and dimming are baked into a copy of the system colour
   table, per entry in that order (the ROM changes only the entries its `indl` resources −16392 and −16391 list; Mac OS
   9 every entry):
   - label, 8-bit table: `L = Brighten(label colour)`; entry 0 becomes `(c + L) >> 1`, the others `(c × L) >> 16`;
     4-bit table: entry 15 (black) becomes the raw label colour;
   - selected: Darken (halve every component; unless all three are then equal within $200, halve the smallest, then
     the smaller of the other two unless they are equal);
   - disabled: `(c + $FFFF) >> 1`, and no pattern.
   - 32-bit data is transformed on a copy: label `c × Brighten(L) >> 16` per channel, selected halves each channel,
     disabled `(c + 255) >> 1`.
   - Brighten: white for black; else each component scaled up so the largest is $FFFF, then
     `c × lum′ / $FFFF + ($FFFF − lum′)`, with `lum = (5r + 9g + 2b) >> 4` and `lum′ = (lum >> 1) + (lum >> 3) + $6000`.
   - Offline then adds 25% black dots inside the mask (step 6).
5. **1-bit data** (PlotShallow):
   - At depth 2 and more: the foreground is the raw label colour (depth > 2 and a label) or the port's; the
     background is `$CC2A $CC2A $FF2A` for open, else the port's; disabled, when depth > 4 or there is no label, takes
     GetGray's colour as the foreground and drops the transform if GetGray succeeds; selected Darkens the foreground
     (depth > 4) and always the background (so white becomes $7FFF grey).
   - At depth 1: selected swaps the port's colours; no label, grey or Darken.
   - None: the image through the mask with CopyMask (image 1 = foreground, 0 = background, inside the mask).
6. **Transforms (offline, open, disabled on 1-bit):**
   - **Bitmap path** (Mac OS 9: only a rect of exactly 32 × 32; ROM: any rect up to 32 × 32, the mask first stretched
     to the rect when the sizes differ), patterns aligned to the icon: T = the image (the outline for open: the mask
     less every pixel whose four neighbours are set); disabled: even rows AND $AAAAAAAA, odd rows AND $55555555;
     offline and open: even rows OR `mask & $88888888`, odd rows OR `mask & $22222222`. T goes through the mask with
     CopyMask, or when the mask was stretched: the ROM paints the mask srcBic then T srcOr, Mac OS 9 copies T srcCopy
     through the mask's region. For colour data, T (the dots alone) is ORed on in black.
   - **Region path** (otherwise), patterns aligned to the port: the mask's region (BitMapToRegion, MapRgn'd to the
     rect); disabled: the image drawn, then PaintRgn with gray in patBic; offline: the image (1-bit) drawn, then
     PaintRgn with ltGray in patOr; open: FillRgn black, InsetRgn(1, 1), FillRgn ltGray.
   - The ROM's pattern loop never ends for an odd mask height (a crash); Mac OS 9 fixed it.
7. **Labels:** 0 none to 7, from the transform's bits 8–11, or PlotIconSuite's suite label when the transform has none;
   8–15 make GetLabel fail. The colours are the System's `'rgb '` −16392 + n (Mac OS 9's defaults: 1 $5600 $2C9D $0524
   Project 2, 2 $0000 $64AF $11B0 Project 1, 3 $0000 $0000 $D400 Personal, 4 $0241 $AB54 $EAFF Cool, 5 $F2D7 $0856
   $84EC In Progress, 6 $DD6B $08C2 $06A2 Hot, 7 $FFFF $648A $028C Essential); the Finder's Labels settings change them.
8. **Other routines:** PlotIconHandle draws an `ICON` or `ICN#` as the 32 × 32 member (a 128-byte `ICON` gets
   CalcMask); PlotSICNHandle a `SICN` list as the small or mini member (a list of two or more: the second is the mask);
   PlotIcon copies an `ICON` unmasked with CopyBits srcCopy, stretched, in the port's colours.

ClassicMac draws suites with `IconSuite.Plot` into a `QuickDrawPort` (the port's depth is the screen depth; a port is
never a picture or printer, so CopyMask is always used), for either QuickDraw.

## Icon families (Mac OS 9 Icon Services)

Mac OS 9's Icon Services knows 20 members, and nothing else [Code]:

| Size | 1-bit (image + mask) | 4-bit | 8-bit | 32-bit | 8-bit mask |
|---|---|---|---|---|---|
| 16 × 12 | `icm#` (48) | `icm4` (96) | `icm8` (192) | — | — |
| 16 × 16 | `ics#` (64) | `ics4` (128) | `ics8` (256) | `is32` (1024) | `s8mk` (256) |
| 32 × 32 | `ICN#` (256) | `icl4` (512) | `icl8` (1024) | `il32` (4096) | `l8mk` (1024) |
| 48 × 48 | `ich#` (576) | `ich4` (1152) | `ich8` (2304) | `ih32` (9216) | `h8mk` (2304) |
| 128 × 128 | — | — | — | `it32` (65536) | `t8mk` (16384) |

(Raw sizes in bytes.) Rows run top down without padding: 1-bit members are the image then the mask, `w/8` bytes a
row; 4-bit pixels high nibble first; 4- and 8-bit colours from the system colour tables 4 and 8 [Code].

- **8-bit masks** (`s8mk`, `l8mk`, `h8mk`, `t8mk`): one byte a pixel, drawn as a deep mask through the grey table 40,
  so the value is the alpha: 0 transparent, $FF opaque. Most real ones have partial alpha [Code] [Verified].
- **32-bit members:** a payload of exactly the raw size is raw ARGB (A first; the alpha is not used). Any other size is
  compressed [Code]:
  - three planes, red, green, blue, each one byte per pixel, into bytes 1, 2 and 3 of each pixel (alpha stays 0);
  - a control byte under $80 copies that many plus one literal bytes; $80 and over repeats the next byte
    (control − 125) times (3 to 130);
  - a count stops at the end of its plane (the excess literal bytes are skipped, a run is cut short); nothing
    carries into the next plane, which begins only when the current one is full; bytes after the blue plane are
    ignored, and short data leaves zeros;
  - **`it32` only** starts with a 4-byte compression-format word, which must be 0: otherwise the whole family fails
    (paramErr). Mac OS 9 writes it as 0.
- **Other members** (1-, 4-, 8-bit and the 8-bit masks) must be exactly their raw size; otherwise the member is
  dropped without an error [Code]. So a mask-less `ICN#` counts here as absent (unlike the Icon Utilities' CalcMask).

**`icns`** (IconFamilyResource) [Code]:

- A header: the type (`icns`, or a variant: `tile`, `over`, `drop`, `open`, `odrp`) and a u32 length including the
  header. The length must be at least 9 and equal the resource's size exactly; otherwise the family is empty
  (noIconDataAvailableErr, which callers tolerate).
- Then elements, packed without alignment and in any order: a type, a u32 size including its 8-byte header, the
  data. A size under 1 fails the whole family; an element running past the end is skipped; a known member is stored
  (the last of a type wins); a variant type nests a family; anything else (`TOC `, `info`, `icnV`, `name`, `ic07`, …)
  is ignored.
- Mac OS 9 writes the known members only, in table order (`icm#` … `t8mk`), 32-bit ones always compressed.
- **Which resources make a family:** an `icns` of the ID is used alone. Without one, the classic resources of the ID
  (`icm#`/`4`/`8`, `ics#`/`4`/`8`, `ICN#`, `icl4`, `icl8`) are read, each only at its exact size. 32-bit members, 8-bit
  masks and the 48 × 48 members come only from an `icns`.

**Mask and data choice** (PlotIconRefFast) [Code]:

- The size group comes from the destination rect's height: ≤ 12 mini, ≤ 20 small, ≤ 40 large, ≤ 56 huge, ≥ 57
  thumbnail. The first mask present in the group's list wins:
  - mini: `icm#` `ics#` `ICN#` `ich#` (never an 8-bit mask);
  - small: `s8mk` `l8mk` `h8mk` `ics#` `ICN#` `icm#` `ich#`;
  - large: `l8mk` `s8mk` `h8mk` `ICN#` `ics#` `icm#` `ich#`;
  - huge: `h8mk` `l8mk` `s8mk` `ich#` `ICN#` `ics#` `icm#`;
  - thumbnail: `t8mk` `h8mk` `l8mk` `s8mk` `ich#` `ICN#` `ics#` `icm#`.
- So an 8-bit mask wins over any 1-bit mask, even one of another size, and the two are never combined. The 1-bit
  and 8-bit masks of real families differ by a few pixels; neither is derived from the other [Verified].
- Data by screen depth (large group shown; the other groups start with their own size): 32 bits `il32` `icl8`
  `icl4` `is32` `ics8` `ics4` `ih32` `ich8` `ich4` `icm8` `icm4` `ICN#` …; 8 or 16 bits `icl8` `icl4` `ics8` `ics4`
  `ich8` `ich4` `icm8` `icm4` `ICN#` … (no 32-bit data, so a 16-bit screen gets `icl8`); 4 bits `icl4` `ICN#` …;
  1 bit `ICN#` `ics#` `icm#` `ich#`. The thumbnail list, at any depth: `it32` `ih32` `ich8` `il32` `icl8` `is32`
  `ics8` `icm8` `ich#` `ICN#` `ics#` `icm#`.
- Drawing: a mask the same size as the data → CopyDeepMask with srcCopy (an 8-bit mask blends as alpha); otherwise
  the mask becomes a region (8-bit: a pixel is in when its byte is not 0), MapRgn'd to the rect, and CopyBits
  srcCopy draws through it (a hard edge). A family without any mask falls back to the Icon Utilities.
- **ClassicMac** exports each image member of an `icns` through the mask its own size selects, the 8-bit mask as the
  image's alpha, as [EXPORT-MANIFEST.md](EXPORT-MANIFEST.md) section 8.3 lists.

## Not covered

- The export decoders take one resource at a time and do not draw suites; `IconSuite.Plot` does.
- Not reproduced by `IconSuite.Plot`: the 8-bit masks of an extended suite (Mac OS 9's deep CopyMask), several
  screens (DeviceLoop), PlotIconHandle, PlotSICNHandle and PlotIcon, a gray-scale device's label rule, and the ROM's
  endless pattern loop. CopyMask's single stretch of data and mask is taken to sample both alike [ClassicMac]. Not yet
  compared with the harness.
- `icns` variants (`tile`, `over`, `drop`, `open`, `odrp`) are read but not exported; standalone 32-bit, 48 × 48 and
  8-bit mask resources are not decoded (Icon Services never reads them outside an `icns`).
