# Patterns (PAT, PAT#, ppat, ppt#)

| Type | Size | Layout |
|---|---|---|
| `PAT ` | 8×8 | 8 bytes |
| `PAT#` | 8×8 each | u16 count, then 8 bytes each |

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
- The table's ctSize (entries − 1) is signed: $FFFF is an empty 8-byte table. GetPixPat copies (ctSize + 1) × 8 + 8
  bytes, not bounded by the resource. A 1-bit pattern with an empty table draws 0 white and 1 black, whatever the
  port's colours; ResEdit 2.1.3's `ppat` 1731 and the `ppt#` 1751/3100 elements end with one and load [Code]
  [Verified]. No Toolbox routine loads a `ppt#`; ResEdit reads it itself.
- Types 1 and 3 decode the PixMap.
- Type 0: the pattern is the **first 8 bytes of the pixel data** (at the pixels offset), not the 1-bit pattern at
  offset 20.
- Type 2 (RGB): the colour is the ColorTable's **entry 4** (table + $2A); the resource's pixels are ignored.
  - A 32-bit screen draws it solid.
  - Other depths draw PatDither's 2×2 cell ([quickdraw.md §8](../graphics/quickdraw.md#8-screen-depths)).
- Mac OS 9 fails to load types above 3.
- **`ppt#`:** a u16 count, then that many u32 offsets from the resource start. Each element is a complete flattened
  `ppat`, whose own offsets are relative to the element's start; element i ends where element i+1 begins.
