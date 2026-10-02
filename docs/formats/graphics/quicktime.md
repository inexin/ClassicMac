# QuickTime still images

QuickTime images as QuickDraw pictures carry them (the CompressedQuickTime and UncompressedQuickTime opcodes), the
built-in codecs, and QuickTime image files (QTIF). Conventions are those of [PICT.md](PICT.md) section 1; drawing the
decoded image is CopyBits ([QUICKDRAW.md](QUICKDRAW.md) section 6). ClassicMac implements it in
`ClassicMac.Graphics.QuickTime` and `ClassicMac.Graphics.Pict`.

## 1. CompressedQuickTime (0x8200)

After the u32 length:

| Type | Field |
|---|---|
| u16 | version |
| i32[9] | matrix `a b u c d v h vOff w`: a, b, c, d, h, vOff are 16.16; u, v, w are 2.30 |
| u32 | matteSize |
| Rect | matteRect |
| u16 | transfer mode |
| Rect | srcRect |
| u32 | accuracy |
| u32 | maskSize |
| bytes | the matte (matteSize bytes; an image description + data). It may be ignored. |
| bytes | the mask Region (maskSize bytes, if non-zero) |
| — | the image description, then the compressed data up to the end of the opcode |

**Image description** (at least 86 bytes):

| Offset | Type | Field |
|---|---|---|
| 0 | u32 | idSize: total size, including a colour table and atoms |
| 4 | 4 chars | cType: the codec |
| 8 | 8 bytes | reserved |
| 16 | u16, u16 | version, revision |
| 20 | u32 | vendor |
| 24 | u32, u32 | temporal and spatial quality |
| 32 | u16, u16 | width, height |
| 36 | Fixed, Fixed | hRes, vRes |
| 44 | u32 | dataSize |
| 48 | u16 | frameCount |
| 50 | 32 bytes | name (Pascal string) |
| 82 | i16 | depth: 1–32; 33–40 = gray 1–8 bits |
| 84 | i16 | clutID: −1 = none; 0 = a ColorTable follows at offset 86 |

The compressed data starts at `idSize` (at least 86).

**Palette for indexed images:**

- clutID 0 → the table that follows (its entries' RGB high bytes);
- else the standard table of that clutID;
- else the standard table for the depth.

Standard tables:

- id 1, 2, 4, 8: the Macintosh default 1-, 2-, 4- and 8-bit colour tables;
- ids 33–40: gray ramps of 2ⁿ levels (n = id − 32), white first.
- The 8-bit table is the 6×6×6 cube of `$FF…$00` in steps of `$33` (red slowest, white first), the first 215 of
  its colours. Then 10-level red, green, blue and gray ramps (`EE DD BB AA 88 77 55 44 22 11`), then black.

**Placement:**

- Map the corners of srcRect through the matrix: `h' = x·a + y·c + h`, `v' = x·b + y·d + vOff`, then
  `(value + $8000) >> 16`.
- Draw the decoded image into their bounding box (rotation and skew are not modelled), then map that box to the
  canvas ([PICT.md](PICT.md) §6).
- Use the opcode's transfer mode, and its mask region intersected with the clip.

**Fallback drawing:**

- QuickTime pictures carry a fallback for systems without QuickTime: usually "QuickTime and a … decompressor are
  needed to see this picture", sometimes a bitmap.
- When the image was decoded:
  - If the next opcode is **PnSize with v = `$00AE`**, its h is a byte count. Skip that many bytes after the opcode.
  - Also skip a bitmap opcode that immediately follows **into the same destination rect** (Photoshop's placeholder).

---

## 2. Codecs

Each codec produces an RGBA image of the description's width × height.

**`raw ` (uncompressed):**

- Rows at the description's depth (1–32; for 33–40 use depth − 32 bits of index).
- Row length: the data length / height, if that is within 3 bytes above the minimum; otherwise the minimum rounded up
  to even.
- 16-bit is RGB 555, 24-bit RGB, 32-bit ARGB.

**`rle ` (Animation):**

- **Header:** u32 chunk size, then u16 flags. If flags bit 3 is set: u16 starting line, 2 bytes, u16 line count,
  2 bytes.
- **Per line:** a skip byte (0 ends the frame; otherwise skip `skip − 1` units), then codes until −1:
  - `0`: another skip byte follows;
  - `n > 0`: n literal units follow;
  - `n < −1`: the next unit is repeated −n times.
- **Unit sizes:**

  | Depth | Unit |
  |---|---|
  | 1 bit | 16 pixels in 2 bytes |
  | 2 bits | 16 pixels in 4 bytes |
  | 4 bits | 8 pixels in 4 bytes |
  | 8 bits | 4 pixels in 4 bytes |
  | 16 | one RGB 555 pixel (2 bytes) |
  | 24 | one RGB pixel (3 bytes) |
  | 32 | one ARGB pixel (4 bytes) |

**`rpza` (Road Pizza / Apple Video):**

- **Framing:** a byte `$E1`, a u24 length, then opcodes over 4×4 blocks in raster order. Colours are RGB 555 (mask
  `$7FFF`).
- **Opcodes:**
  - `$80 + n−1`: skip n blocks.
  - `$A0 + n−1`, colour: n solid blocks.
  - `$C0 + n−1`, colour A, colour B: n four-colour blocks, each followed by 4 index bytes.
  - A byte with bit 7 clear starts a single block: with the next byte it forms colour A.
    - If the byte after that has bit 7 set, it starts colour B of a four-colour block.
    - Otherwise 15 more colours follow: a 16-colour block.
- **Four-colour blocks:**
  - The colours, by 2-bit index, are `{B, (11A + 21B) >> 5, (21A + 11B) >> 5, A}`, per 5-bit component.
  - One index byte per row, most significant pair first.

**`smc ` (Graphics):**

- **Framing:** a flags byte and u24 length, then opcodes over 4×4 blocks of 8-bit indices. There are three 256-entry
  circular caches of 2-, 4- and 8-colour sets.
- **Opcodes** (the high nibble is the operation, the low nibble `n−1`; the second of each pair takes the count from
  the next byte + 1):

  | Opcode | Operation |
  |---|---|
  | 0x/1x | skip |
  | 2x/3x | repeat the previous block |
  | 4x/5x | repeat the previous two blocks |
  | 6x/7x | one colour byte, solid blocks |
  | 8x/9x | two-colour blocks: 8x reads a new pair into the cache, 9x a cache index; then u16 flags per block, bit 15 = pixel 0 |
  | Ax/Bx | four-colour blocks: u32 flags, 2 bits per pixel, top first |
  | Cx/Dx | eight-colour blocks: 6 bytes per block, nibbles n0…nB (see below) |
  | Ex | 16 raw indices per block |

- **Eight-colour block pixel indices:** pixels 0–7 take 3-bit indices from `n0 n1 n2 n4 n5 n6`, and pixels 8–15 from
  `n8 n9 nA n3 n7 nB`, each read as a 24-bit value, most significant first.

**`cvid` (Cinepak):**

- **Frame header:** flags, u24 length, u16 width, u16 height, u16 strip count.
- **Strips:** u16 id, u16 size, then y1, x1, y2, x2. Each strip's height is `y2 − y1`, or the rest of the frame
  when that is ≤ 0.
- **Chunks:** u16 id, u16 size including the header.
- **Codebook chunks (`$20xx`):**
  - `$0200` selects the V1 book (else V4).
  - `$0400` means 4-byte entries (luma only: gray or palette index), else 6 bytes: Y0 Y1 Y2 Y3, i8 U, i8 V.
  - `$0100` is a partial update: a u32 bit mask (MSB first) precedes each 32 entries.
- **Vector chunks** cover the strip's 4×4 blocks in raster order, reading a u32 mask stream MSB first:
  - `$3000`: one bit per block; 1 = four V4 indices (2×2 each), 0 = one V1 index (its 4 lumas each fill a 2×2).
  - `$3100`: first a "changed" bit (0 = skip the block), then the V4/V1 bit.
  - `$3200`: V1 only, with no bits.
- **Colour:** `R = Y + 2V`, `G = Y − U/2 − V` (U/2 truncated toward zero), `B = Y + 2U`, clamped. For depths ≤ 8
  the Y value is a palette index; for 33–40 it is gray.

**`8BPS` (Planar RGB):**

- u16 packed byte counts for each row of each plane, then the planes' PackBits rows.
- Planes are R, G, B, then alpha for depth 32. For depth 8 there is one plane of palette indices.

**`yuv2`:** per pixel pair, `Y0 U Y1 V`, with U and V as signed bytes.

**`YVU9`:** a full Y plane, then V and U planes subsampled 4×4, unsigned and centred on 128. The colour conversion
for YUV codecs is full-range JFIF:

- `R = Y + 1.402 V`
- `G = Y − 0.344136 U − 0.714136 V`
- `B = Y + 1.772 U`

**`tga ` (Targa):** a complete Targa file:

- an 18-byte header, then the image ID, colour map and pixels;
- types 1/2/3, raw or RLE (+8);
- 8/15/16/24/32 bits;
- bottom-up unless descriptor bit 5 is set.

**`PNTG` (MacPaint):** 576 × 720, 1 bit per pixel, 72-byte rows PackBits-compressed back to back, 1 = black.

**Others** (`jpeg`, `png `, `gif `, `tiff`, …) contain a complete file in that format. Hand them to a general image
decoder. An image that cannot be decoded draws nothing, and the fallback drawing is then shown.

---

## 3. UncompressedQuickTime (0x8201)

After the u32 length:

| Type | Field |
|---|---|
| u16 | version |
| i32[9] | matrix, as in 0x8200 |
| u32 | matteSize |
| Rect | matteRect |
| bytes | the matte (matteSize bytes), then a pad byte to an even offset |
| u16 | a bitmap opcode: `0098`, `0099`, `009A` or `009B` (`0090`–`0093` read the same way) |
| — | that opcode's operands ([PICT.md](PICT.md) §4.5) and pixel data ([PICT.md](PICT.md) §5) |

- Draw the bitmap as that opcode would, with its own mode and mask region.
- **Placement:** with an identity matrix, the opcode's dstRect. Otherwise, as for 0x8200, the bounding box of the
  opcode's srcRect mapped through the matrix. This follows the compressed case; no Apple reference was checked.
- Afterwards, skip the fallback drawing as for 0x8200.
- A Macintosh without QuickTime skips the whole opcode (u32 length) and shows the fallback instead.

---

## 4. QuickTime image files (QTIF)

The standalone form of a QuickTime image (`.qtif`, `.qti`, `.qif`) is a sequence of atoms:

| Type | Field |
|---|---|
| u32 | atom size, including this 8-byte header. 0 = to the end of the file; 1 = a u64 size follows the type. |
| 4 chars | atom type |
| bytes | content |

- **`idsc`:** an image description, as in §1, including any colour table. ClassicMac reads it within its atom: a
  description running past the atom's end is unreadable [ClassicMac].
- **`idat`:** the compressed image. Decode it with the description's codec (§2).
- **`iicc`:** an ICC profile (optional).
- Other atoms (`meta`, …) can be ignored.
- The first atom of each type is used. An atom running past the end of the file is cut there, and an atom whose size
  is less than its header ends the file [ClassicMac].
- **Detection:** the first atom's type is `idsc`, `idat` or `iicc`.

---

## 5. Not covered

- Mattes, rotation and skew. Images are placed in their bounding box, and the matte is ignored.
