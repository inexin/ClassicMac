# Cursors (CURS, crsr)

The cursor resources of classic Mac OS: `CURS`, a 16 × 16 1-bit cursor with its mask and hotspot, and `crsr`, a colour
cursor that adds a PixMap and its colour table. Applications and the System hold them; SetCursor and SetCCursor draw
them over the screen with a mask and an XOR. ClassicMac reads both into an image and a JSON description of what the
cursor does to the screen, and writes both from an image in its editor.

| | |
| --- | --- |
| Identified by | Resource types `CURS` and `crsr` (crsrType `$8000` or `$8001` at +$00) |
| ClassicMac | Reads and writes; `ClassicMac.Resources.Decoders.Images.QuickDrawResources` (`DecodeCursor`, `DecodeColorCursor`), `ImageImport` (`WriteCursor`, `WriteColorCursor`), the `image.cursor` decoder |
| Verified against | Nothing yet |
| Sources | *Inside Macintosh: Imaging With QuickDraw* (Cursor, CCrsr, PixMap, ColorTable); SetCursor and SetCCursor, traced in disassembly |

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

### 1.1 CURS

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 32 | data | 16 rows of 2 bytes, most significant bit first |
| +$20 | 32 | mask | The same layout |
| +$40 | 4 | hotSpot | A Point (v, h) |

[Doc]

### 1.2 crsr

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | crsrType | `$8000` monochrome, `$8001` colour |
| +$02 | 4 | crsrMap | Offset of the PixMap |
| +$06 | 4 | crsrData | Offset of the pixels |
| +$0A | 4 | crsrXData | Reserved, 0 |
| +$0E | 2 | crsrXValid | Reserved, 0 |
| +$10 | 4 | crsrXHandle | Reserved, 0 |
| +$14 | 32 | crsr1Data | The 1-bit data, as a `CURS`'s |
| +$34 | 32 | crsrMask | The mask |
| +$54 | 4 | crsrHotSpot | A Point (v, h) |
| +$58 | 4 | crsrXTable | Reserved, 0 |
| +$5C | 4 | crsrID | Reserved, 0 |
| crsrMap | 50 | PixMap | A PixMap record ([pict.md §4.5](../graphics/pict.md#45-bitmap-pixmap-and-bitmap-opcode-operands)); its pmTable is the offset of its ColorTable |
| crsrData | rowBytes × height | pixels | |
| pmTable | 8 + 8 × entries | colour table | A ColorTable ([pict.md §4.6](../graphics/pict.md#46-colortable)) |

[Doc]

## 2. Reading

### 2.1 CURS

A cursor is drawn as `screen = (screen AND NOT mask) XOR image` [Doc]:

| Mask bit | Data bit | Result |
| --- | --- | --- |
| 1 | 1 | black |
| 1 | 0 | white |
| 0 | 0 | transparent |
| 0 | 1 | inverts (complements) the screen |

### 2.2 crsr

SetCCursor never reads the 1-bit data of a colour cursor [Code]:

- mask 1: the colour pixel, converted to the screen depth;
- mask 0 on a 16- or 32-bit screen: the screen is XORed with the pixel's complement, so white is transparent, black
  inverts, and other colours XOR their complement;
- mask 0 on a screen of 8 bits or fewer: the screen index is XORed with the pixel's index.

### 2.3 The hotspot

Hotspots are clamped to 0–15 [Code].

## 3. Writing

ClassicMac's editor makes both from an RGBA image [ClassicMac]:

1. The image is fitted to 16 × 16, its mask and 1-bit data made by [icons.md §3](icons.md#3-writing).
2. The hotspot is the centre (8, 8) unless given, clamped to 0–15.
3. **`CURS`**: the data, the mask and the hotspot; 68 bytes.
4. **`crsr`**: type `$8001`; the header (§1.2) with the 1-bit data and mask as for `CURS` and every reserved field 0;
   the PixMap at 96; the pixels at 146 (rowBytes 2 × depth); the colour table after them, its offset in pmTable. The
   colour table is the image's colours with white first (icons.md §3 item 5), so pixels outside the mask are white
   and leave the screen unchanged under a colour cursor. The PixMap's other fields are those of a `cicn`'s
   ([icons.md §3](icons.md#3-writing) item 6).

## 4. Variants

- A `crsr` of type `$8000` is monochrome: only its 1-bit data and mask are used [Doc].
- How SetCCursor draws mask-0 pixels depends on the screen depth (§2.2).

## 5. ClassicMac

- A cursor becomes an image of the pixels it paints and a JSON file of the pixels it inverts or XORs, with the
  hotspot: [export-manifest.md §8.5](../output/export-manifest.md#85-cursors). The XOR values are those of a 32-bit
  screen. [ClassicMac]
- A `crsr` of type `$8000`, or one whose PixMap offset is 0, is read from its 1-bit data and mask as a `CURS`; colour
  pixels outside the PixMap read as black. [ClassicMac]
- A `CURS` under 68 bytes, a `crsr` under 96 bytes or of another type than `$8000`/`$8001`, a PixMap whose pixelSize
  is not a QuickDraw depth, and tables or pixels past the data are refused (`image.undecodable`). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `image.undecodable` | Warning | The resource is shorter than its layout, of an unknown crsrType, or its PixMap or tables are impossible | Exports the resource raw | Not traced |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/QuickDrawResourceTests.cs`: `Cursor_PaintsMaskedBitsInvertsTheRestAndReadsTheHotspot`,
  `ColorCursor_IgnoresThe1BitDataAndXorsUnmaskedPixels`.
- `tests/ClassicMac.Resources.Decoders.Tests/ImageDecoderTests.cs`, `Cursors_come_with_their_hotspot_and_inverted_pixels`:
  the JSON's hotspot and inverted rows.
- `tests/ClassicMac.Resources.Decoders.Tests/ImportTests.cs`: a `crsr` written and read back gives the image's opaque
  pixels exactly, its hotspot, and no inverted pixels; a `CURS` is 68 bytes with its hotspot clamped.
- Golden outputs `CURS-128.json` and `crsr-128.json` in `tests/ClassicMac.Resources.Decoders.Tests/Golden`
  (`GoldenFixtures`).

## 8. Not covered

- How a `crsr`'s mask-0 pixels look on screens of 8 bits or fewer (index XOR, §2.2): the JSON gives the 32-bit XOR
  only.
- Animated cursors (`acur`).

## 9. References

1. Apple, *Inside Macintosh: Imaging With QuickDraw* (1994): the Cursor and CCrsr records, SetCursor and SetCCursor.
