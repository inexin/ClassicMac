# Icons

Decoded by `ClassicMac.Resources.Decoders` (`QuickDrawResources`); what the exports contain is in
[export-manifest.md](../output/export-manifest.md). Colour tables and pixel maps are as in [pict.md](../graphics/pict.md); scaling and screen depths
as in [quickdraw.md](../graphics/quickdraw.md).

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
- **Which icon is drawn:** see [icon-families.md §1](icon-families.md#1-drawing-an-icon-suite-ploticonid-ploticonsuite).
- **`cicn`** (PlotCIcon):
  - fore/back are forced to black/white;
  - the 1-bit BitMap is drawn if it exists and the screen depth is at most 2, else the PixMap;
  - the mask is the icon mask.

**`cicn`** (colour icon):

- A header, then variable-length data:
  - a 50-byte PixMap (baseAddr, rowBytes & `$3FFF`, bounds, pmVersion, packType, packSize, hRes, vRes, pixelType,
    pixelSize, cmpCount, cmpSize, planeBytes, pmTable, pmReserved);
  - a 14-byte mask BitMap (baseAddr, rowBytes, bounds);
  - a 14-byte 1-bit BitMap (rowBytes 0 when absent);
  - a 4-byte iconData;
  - the mask bits, then the 1-bit bits;
  - a ColorTable ([pict.md §4.6](../graphics/pict.md#46-colortable));
  - the pixels, unpacked, `rowBytes × height` bytes.
- The mask masks the colour pixels. Rows beyond the mask's data are unmasked.

## 1. Writing icons and cursors (import)

ClassicMac's editor makes these resources from an RGBA image, a PNG for example (`ImageImport`), in the layouts
above. No Mac OS code imports an image, so the conversion rules are ClassicMac's [ClassicMac]:

- **Size:** an image of another size is scaled to fit the resource's (area average of premultiplied colour), its
  aspect kept and centred on a transparent field. `cicn` keeps the image's size (up to 256 × 256).
- **Mask:** a pixel is in the mask when its alpha is at least 128.
- **1-bit data** (`ICON`, the icon lists, a `cicn`'s BitMap, a cursor's data): black where the pixel is in the mask
  and its luminance `(299 R + 587 G + 114 B) / 1000` is below 128.
- **Standard-table icons** (`icl4`, `icl8`, `ics4`, …): each pixel the nearest entry of the standard 4- or 8-bit
  table by RGB distance (the lowest index on a tie), no dithering; pixels outside the mask are white (index 0).
- **`cicn`, `crsr`:** a colour table of exactly the image's colours (for `crsr`, white first), at the smallest depth
  of 1, 2, 4 or 8 bits that holds them; beyond 256 colours the standard 8-bit table, nearest colours. `ctSeed` and
  `ctFlags` are 0 and each entry's value is its index. Pixels outside the mask are white, which leaves the screen
  under a colour cursor.
  - `cicn`: the PixMap (pmTable 0), mask and icon BitMaps (rowBytes even), a zero icon data handle, the mask, the
    1-bit icon, the colour table and the pixels.
  - `crsr`: type `$8001`, the PixMap at 96, the pixels at 146, the colour table after them (pmTable its offset); the
    extra fields (`crsrXData`, `crsrXValid`, `crsrXHandle`, `crsrXTable`, `crsrID`) are 0.
- Read back by `QuickDrawResources`, a `cicn` or `crsr` gives the image's opaque pixels exactly [Verified:
  ClassicMac's tests].
