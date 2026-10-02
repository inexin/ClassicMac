# Icons

The QuickDraw icon resources of classic Mac OS: the 1-bit `ICON` and `SICN`, the icon lists `ICN#`, `ics#` and `icm#`
(an icon and its mask), the 4- and 8-bit `icl4`, `icl8`, `ics4`, `ics8`, `icm4` and `icm8` in the system colour
tables, and the colour icon `cicn` with its own colour table and mask. They are not part of PICT, but use its
structures. Applications, the Finder and the System hold them; the Icon Utilities draw the 1-, 4- and 8-bit members of
one ID together as an icon suite ([icon-families.md](icon-families.md)). ClassicMac reads every one into an image,
draws suites, and writes all of them from an image in its editor.

| | |
| --- | --- |
| Identified by | Resource types `ICON`, `ICN#`, `ics#`, `icm#`, `SICN`, `icl4`, `icl8`, `ics4`, `ics8`, `icm4`, `icm8`, `cicn` |
| ClassicMac | Reads and writes; `ClassicMac.Resources.Decoders.Images.QuickDrawResources` (reading), `ImageImport` (writing), the `image.icon` decoder |
| Verified against | Mac OS 9.0's Icon Utilities in SheepShaver (the drawing rules, [icon-families.md §7](icon-families.md#7-verification)) |
| Sources | *Inside Macintosh: Imaging With QuickDraw* (CIcon, PixMap, ColorTable) and *More Macintosh Toolbox* (Icon Utilities); Mac OS 9.0's IconUtils and the 68k ROM's Icon Utilities, traced in disassembly |

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

### 1.1 1-bit icons and icon lists

Rows run top down, `width / 8` bytes a row, most significant bit first; 1 is black [Doc].

| Type | Size | Offset | Size (bytes) | Field |
| --- | --- | --- | --- | --- |
| `ICON` | 32 × 32 | +$00 | 128 | The icon, unmasked |
| `ICN#` | 32 × 32 | +$00 | 128 | The icon |
| | | +$80 | 128 | Its mask |
| `ics#` | 16 × 16 | +$00 | 32 | The icon |
| | | +$20 | 32 | Its mask |
| `icm#` | 16 × 12 | +$00 | 24 | The icon |
| | | +$18 | 24 | Its mask |
| `SICN` | 16 × 16 each | +$00 | 32 × *n* | Any number of icons, unmasked |

[Doc]. Where there is a mask, a 0 mask bit is transparent.

### 1.2 4- and 8-bit icons

| Type | Size | Bytes | Pixels |
| --- | --- | --- | --- |
| `icl4`, `icl8` | 32 × 32 | 512, 1024 | 4 or 8 bits, in the standard colour table of that depth |
| `ics4`, `ics8` | 16 × 16 | 128, 256 | the same |
| `icm4`, `icm8` | 16 × 12 | 96, 192 | the same |

Rows run top down without padding; 4-bit pixels high nibble first [Doc]. There is no mask in the resource (§2.2).

The standard colour tables are the System's `clut` 4 and 8 [Doc] [Code]:

- `clut` 8 is the 6 × 6 × 6 cube without black (215 entries, white first, red slowest), then red, green, blue and grey
  ramps of `EE DD BB AA 88 77 55 44 22 11`, then black at 255.
- Pixels convert through the high byte of each exact 16-bit component, as in PICT.

### 1.3 cicn

A colour icon (`CIcon`) [Doc: *Imaging With QuickDraw*]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 50 | iconPMap | A PixMap record ([pict.md §4.5](../graphics/pict.md#45-bitmap-pixmap-and-bitmap-opcode-operands)): baseAddr, rowBytes (the low 14 bits), bounds, pmVersion, packType, packSize, hRes, vRes, pixelType, pixelSize, cmpCount, cmpSize, planeBytes, pmTable, pmReserved |
| +$32 | 14 | iconMask | A BitMap: baseAddr, rowBytes, bounds |
| +$40 | 14 | iconBMap | A BitMap; rowBytes 0 when there is no 1-bit icon |
| +$4E | 4 | iconData | A handle, 0 in the resource |
| +$52 | iconMask rowBytes × height | mask bits | |
| follows | iconBMap rowBytes × height | 1-bit icon bits | |
| follows | 8 + 8 × entries | colour table | A ColorTable ([pict.md §4.6](../graphics/pict.md#46-colortable)) |
| follows | iconPMap rowBytes × height | pixels | Unpacked |

## 2. Reading

### 2.1 1-bit icons

1 is black; 0 is white, or transparent where the mask bit is 0 [Doc].

### 2.2 Masks

1. A 4- or 8-bit icon takes the mask of the icon list of the same ID and size (`ICN#` for `icl4`/`icl8`, `ics#` for
   `ics4`/`ics8`, `icm#` for `icm4`/`icm8`) [Doc] [Code].
2. An icon list without its mask half (the resource is only the icon) gets a computed mask, CalcMask [Code]:
   1. flood-fill the white pixels 4-connected to the edges;
   2. the mask is every pixel the flood did not reach: the icon's silhouette, enclosed holes included.
3. Without an icon list the Icon Utilities draw nothing (noMaskFoundErr, −1000) [Code].
4. An all-zero mask draws nothing [Code].

Which member of a suite is drawn, and how, is [icon-families.md §2.1](icon-families.md#21-drawing-an-icon-suite).

### 2.3 cicn

1. Read the PixMap, the two BitMaps and the handle (82 bytes), then the mask bits, the 1-bit bits, the colour table
   and the pixels in that order [Doc].
2. PlotCIcon forces the fore- and background colours to black and white [Code].
3. It draws the 1-bit BitMap when there is one and the screen depth is at most 2, else the PixMap [Code].
4. The mask masks either [Doc] [Code].

## 3. Writing

ClassicMac's editor makes these resources from an RGBA image (a PNG, for example) in the layouts of §1. No Mac OS
code imports an image, so every conversion rule here is ClassicMac's [ClassicMac]:

1. **Size:** an image of another size is scaled to fit the resource's, by the area average of premultiplied colour,
   its aspect kept and centred on a transparent field. A `cicn` keeps the image's size up to 256 × 256; a larger image
   is fitted into the smaller of its size and 256 each way.
2. **Mask:** a pixel is in the mask when its alpha is at least 128.
3. **1-bit data** (`ICON`, the icon lists, a `cicn`'s BitMap, a cursor's data): black where the pixel is in the mask
   and its luminance `(299 R + 587 G + 114 B) / 1000` is below 128.
4. **Standard-table icons** (`icl4`, `icl8`, `ics4`, …): each pixel the nearest entry of the standard 4- or 8-bit table
   by RGB distance (the lowest index on a tie), no dithering; pixels outside the mask take the entry nearest white
   (index 0).
5. **A colour table of the image's colours** (`cicn`, and `crsr` in [cursors.md §3](cursors.md#3-writing)): exactly
   the opaque pixels' colours in the order met, at the smallest depth of 1, 2, 4 or 8 bits that holds them; beyond 256
   colours the standard 8-bit table, nearest colours. `ctSeed` and `ctFlags` are 0, each entry's value is its index,
   and each 8-bit component is repeated into 16 bits. Pixels outside the mask take the entry nearest white.
6. **`cicn`**: the PixMap (rowBytes even, with bit 15 set; bounds 0, 0, height, width; packType 0; 72 dpi; pixelType 0;
   pixelSize and cmpSize the depth; cmpCount 1; pmTable 0), the mask and icon BitMaps (rowBytes even), a zero
   iconData, the mask, the 1-bit icon, the colour table and the pixels.

An icon family (the six members of one ID) is [icon-families.md §3](icon-families.md#3-writing).

## 4. Variants

The resources have one layout each. Where Mac OS 9.0, the 68k ROM and Mac OS 9.2.2 draw them differently is
[icon-families.md §4](icon-families.md#4-variants).

## 5. ClassicMac

- Each icon becomes an RGBA image, 1-bit icons black on white, masked pixels transparent. What `extract` writes for
  each type, `SICN` lists and the masks included, is [export-manifest.md §8.3](../output/export-manifest.md#83-icons-and-their-masks).
  [ClassicMac]
- A 4- or 8-bit icon without an icon list is decoded opaque and reported (`image.no-mask`), where the Icon Utilities
  draw nothing: ClassicMac prefers a visible image. [ClassicMac]
- An icon list whose second half is too short for a mask gets CalcMask, as on the Mac (§2.2). A `SICN` gives one image
  per whole 32 bytes. [ClassicMac]
- A `cicn` under 82 bytes, one whose PixMap pixelSize is not 1, 2, 4, 8, 16 or 32, or whose tables or pixels run past
  the data is refused (`image.undecodable`). A mask of rowBytes 0 means no mask; rows or columns past the mask's data
  are unmasked. [ClassicMac]
- The decoders take one resource at a time; `IconSuite` draws suites ([icon-families.md §5](icon-families.md#5-classicmac)).
  [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `image.no-mask` | Info | A 4- or 8-bit icon has no icon list of its ID and size | Decodes it opaque | Draws nothing (noMaskFoundErr, −1000) [Code] |
| `image.undecodable` | Warning | The resource is shorter than its layout, or a `cicn`'s PixMap or tables are impossible | Exports the resource raw | Not traced |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/QuickDrawResourceTests.cs`: `IconList_MasksTheIcon`,
  `ColorIcon8_UsesTheStandardTableAndTheIconListMask`, `IconList_WithoutItsMaskHalf_UsesCalcMask`,
  `Cicn_DrawsItsPixelsThroughItsColorTableAndMask`, `PatternList_AndSmallIcons_DecodeEveryEntry` (the `SICN` list).
- `tests/ClassicMac.Resources.Decoders.Tests/ImageDecoderTests.cs`: `ICON` black on white, icon lists' masks, colour
  icons masked by their list (and `image.no-mask` without one), `SICN` as numbered images, damaged resources left raw.
- `tests/ClassicMac.Resources.Decoders.Tests/ImportTests.cs`: `Colour_icons_and_cursors_keep_exact_colours_and_the_mask`
  (a `cicn` written and read back gives the image's opaque pixels exactly), `Standard_table_icons_use_the_nearest_colour`,
  `Cursors_and_plain_icons`.
- Golden fixtures for every icon type, made in code (`GoldenFixtures`; image hashes in
  `tests/ClassicMac.Resources.Decoders.Tests/Golden/golden.json`).

## 8. Not covered

- PlotCIcon's 1-bit BitMap at depths 1 and 2 (§2.3): the `cicn` export always shows the PixMap.

## 9. References

1. Apple, *Inside Macintosh: Imaging With QuickDraw* (1994): the PixMap, BitMap, ColorTable and CIcon records.
2. Apple, *Inside Macintosh: More Macintosh Toolbox* (1993), Icon Utilities: icon families, the icon resources,
   CalcMask's role.
