# Cursors

| Type | Size | Layout |
|---|---|---|
| `CURS` | 16×16 | 32 bytes data, 32 bytes mask, then the hotspot as a Point (v, h) |

**Cursors:**

| Mask bit | Data bit | Result |
|---|---|---|
| 1 | 1 | black |
| 1 | 0 | white |
| 0 | 0 | transparent |
| 0 | 1 | **inverts** the screen |

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

## 1. Writing icons and cursors (import)

ClassicMac's editor makes these resources from an RGBA image, a PNG for example (`ImageImport`), in the layouts
above. No Mac OS code imports an image, so the conversion rules are ClassicMac's [ClassicMac]:

- **`CURS`, `crsr` hotspot:** the centre (8, 8) unless given; clamped to 0–15.
