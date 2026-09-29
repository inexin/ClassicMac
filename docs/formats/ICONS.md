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
- **Which icon is drawn** (PlotIconID and the icon suites, Mac OS 9; described, not implemented; see Not covered):
  - The mask group depends on the rect size:
    - 48 or more: `ich#`.
    - Under 32 and taller than 12: `ics#`, then `ICN#`, then `icm#`.
    - Under 32 otherwise: `icm#`, `ics#`, `ICN#`.
    - Otherwise: `ICN#` first.
  - The colour data comes only from the mask's group, by screen depth:
    - 16/32 bits: `il32`, `icl8`, `icl4`, `ICN#`;
    - 8 bits: `icl8`, `il32`, `icl4`, `ICN#`;
    - 4 bits: `icl4`, `ICN#`;
    - 1–2 bits: `ICN#`.
  - Plain drawing copies the data through the mask, with no transform.
  - Transforms:
    - selected: Darken halves each component, then halves the smallest, then the smaller of the other two unless they
      are equal (±$200);
    - disabled: `(c + $FFFF) >> 1`.
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

## Not covered

- Icon suites: choosing a member by rect size and screen depth, and the selected, disabled, label, offline and open
  transforms, are described but not implemented. The decoders take one resource at a time.
- The 48 × 48 and 32-bit icon types (`ich#`, `il32`, `l8mk` and relatives) and `icns` are not decoded.
