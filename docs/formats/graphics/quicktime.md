# QuickTime still images

QuickTime still images as QuickDraw pictures carry them (the CompressedQuickTime and UncompressedQuickTime opcodes),
the codecs that compress them, and QuickTime image files (QTIF), their standalone form. Applications that save
pictures through QuickTime write the opcodes, with an ordinary QuickDraw drawing after the image for Macs without
QuickTime. ClassicMac decodes
the built-in codecs, hands others to a codec the caller supplies, and draws the image through CopyBits
([quickdraw.md §2.14](quickdraw.md#214-copybits)).

| | |
| --- | --- |
| Identified by | Picture opcodes `$8200` and `$8201` ([pict.md §1.4](pict.md#14-opcodes)). QTIF files: type `qtif`; extensions `.qtif`, `.qti`, `.qif`; a first atom of type `idsc`, `idat` or `iicc` (§1.4) |
| ClassicMac | Reads; `ClassicMac.Graphics.QuickTime` (codecs, `QuickTimeImageFile`), `ClassicMac.Graphics.Pict` (the opcodes) |
| Verified against | The `raw `, `rle `, `rpza`, `smc ` and `cvid` decoders against FFmpeg's, pixel for pixel (§7) |
| Sources | *Inside Macintosh: QuickTime*; Apple's QuickTime File Format; *Inside Macintosh: Imaging With QuickDraw* (the standard colour tables); the Truevision TGA specification. Other decoders (behaviour only): FFmpeg |

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

Numbers, rects and points are as in [quickdraw.md §1.1](quickdraw.md#11-coordinates-and-numbers).

### 1.1 CompressedQuickTime ($8200)

The opcode is followed by a `u32` length and that many bytes:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Version | `u16` |
| +$02 | 36 | Matrix | Nine `i32`, `a b u c d v h vOff w`: a, b, c, d, h and vOff are 16.16 fixed; u, v and w 2.30 |
| +$26 | 4 | matteSize | `u32` |
| +$2A | 8 | matteRect | Rect |
| +$32 | 2 | Transfer mode | `u16` |
| +$34 | 8 | srcRect | Rect, in the image's pixels |
| +$3C | 4 | Accuracy | `u32` |
| +$40 | 4 | maskSize | `u32` |
| +$44 | matteSize | Matte | An image description and its data |
| … | maskSize | Mask | A region ([quickdraw.md §1.3](quickdraw.md#13-regions)), when maskSize is not 0 |
| … | | Image | An image description (§1.2), then the compressed data to the end of the opcode |

[Doc: Inside Macintosh: QuickTime]

### 1.2 Image description

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | idSize | `u32`: the whole description, with any colour table and atoms after it; at least 86 |
| +$04 | 4 | cType | The codec, a four-character code (§2.6–§2.15) |
| +$08 | 8 | Reserved | |
| +$10 | 2 | Version | `u16` |
| +$12 | 2 | Revision | `u16` |
| +$14 | 4 | Vendor | OSType |
| +$18 | 4 | Temporal quality | `u32` |
| +$1C | 4 | Spatial quality | `u32` |
| +$20 | 2 | Width | `u16` |
| +$22 | 2 | Height | `u16` |
| +$24 | 4 | hRes | Fixed, dpi |
| +$28 | 4 | vRes | Fixed, dpi |
| +$2C | 4 | dataSize | `u32` |
| +$30 | 2 | frameCount | `u16` |
| +$32 | 32 | Name | Pascal string: the compressor's name |
| +$52 | 2 | Depth | `i16`: 1–32; 33–40 are grey at 1–8 bits |
| +$54 | 2 | clutID | `i16`: −1 none; 0 a colour table follows at +$56 ([quickdraw.md §1.8](quickdraw.md#18-colour-tables)) |

The compressed data starts at idSize from the description's start. [Doc: Inside Macintosh: QuickTime]

### 1.3 UncompressedQuickTime ($8201)

The opcode is followed by a `u32` length and that many bytes:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Version | `u16` |
| +$02 | 36 | Matrix | As in §1.1 |
| +$26 | 4 | matteSize | `u32` |
| +$2A | 8 | matteRect | Rect |
| +$32 | matteSize | Matte | Then a pad byte to an even offset |
| … | 2 | Bitmap opcode | `$0098`, `$0099`, `$009A` or `$009B` (`$0090`–`$0093` are read the same way) |
| … | | Operands | That opcode's operands ([pict.md §1.5](pict.md#15-bitmap-opcode-operands)) and pixel data ([pict.md §2.4](pict.md#24-pixel-data)) |

[Doc: Inside Macintosh: QuickTime]

### 1.4 QuickTime image files (QTIF)

A QTIF file is a sequence of atoms:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Size | `u32`, including this header. 0: the atom runs to the end of the file. 1: a `u64` size follows the type |
| +$04 | 4 | Type | Four characters |
| +$08 | 8 | Extended size | `u64`, only when the size is 1 |
| +$08 or +$10 | | Content | |

The atoms [Doc: QuickTime File Format]:

| Type | Content |
| --- | --- |
| `idsc` | An image description (§1.2), with any colour table |
| `idat` | The compressed image |
| `iicc` | An ICC profile (optional) |
| others (`meta`, …) | Ignored |

## 2. Reading

### 2.1 Colour tables

An indexed image's colours are [Doc: Inside Macintosh: QuickTime]:

1. clutID 0: the table after the description (its entries' RGB high bytes);
2. otherwise the standard table of that clutID;
3. otherwise the standard table for the depth.

The standard tables [Doc: Inside Macintosh: Imaging With QuickDraw]:

- ids 1, 2, 4 and 8: the Macintosh default 1-, 2-, 4- and 8-bit colour tables;
- ids 33–40: grey ramps of 2ⁿ levels (n = id − 32), white first;
- the 8-bit table is the 6 × 6 × 6 cube of `$FF`…`$00` in steps of `$33` (red slowest, white first), its first 215
  colours; then 10-level red, green, blue and grey ramps (`EE DD BB AA 88 77 55 44 22 11`); then black.

### 2.2 Placing a compressed image

1. Decode the image (§2.6–§2.15) to the description's width × height.
2. Map the corners of srcRect through the matrix: `h' = x·a + y·c + h`, `v' = x·b + y·d + vOff`, each rounded as
   `(value + $8000) >> 16`.
3. Draw the image into the mapped corners' bounding box, then map that box to the canvas like any picture rect
   ([pict.md §2.7](pict.md#27-mappt-maprect-scalesize)).
4. Use the opcode's transfer mode, and its mask region intersected with the clip.

[Doc: Inside Macintosh: QuickTime]

### 2.3 The fallback drawing

A picture with a QuickTime image carries drawing for Macs without QuickTime: usually the text "QuickTime and a …
decompressor are needed to see this picture", sometimes a bitmap. When the image was drawn, skip it [Fitted]:

1. If the next opcode is PnSize with v = `$00AE`, its h is a byte count: skip that many bytes after the opcode.
2. Skip a bitmap opcode that immediately follows into the same destination rect (Photoshop's placeholder).

### 2.4 Drawing an uncompressed image

1. Read the matrix, skip the matte and the pad byte.
2. Read the bitmap opcode and its operands as [pict.md §1.5](pict.md#15-bitmap-opcode-operands) gives them.
3. With an identity matrix, draw the bitmap to the opcode's dstRect, with its own mode and mask region.
4. Otherwise place it as §2.2 places a compressed image: the bounding box of the opcode's srcRect mapped through the
   matrix.
5. Skip the fallback drawing (§2.3).

Steps 1–3 [Doc: Inside Macintosh: QuickTime]; step 4 follows the compressed case [ClassicMac].

### 2.5 Reading a QTIF file

1. Walk the atoms from the start of the file. The first atom of each type is used.
2. Read the image description from `idsc`; it must lie within its atom.
3. Decode `idat` with the description's codec.

An atom running past the end of the file is cut there; an atom whose size is less than its header ends the walk.
[ClassicMac]

### 2.6 The raw codec

`raw `, uncompressed [Reference: FFmpeg]:

- Rows at the description's depth (1–32; for 33–40, depth − 32 bits of index).
- The row length is the data length divided by the height when that is the minimum or up to 3 bytes more; otherwise
  the minimum rounded up to even.
- 16-bit pixels are RGB 555, 24-bit RGB, 32-bit ARGB.

### 2.7 The rle codec

`rle `, Animation [Reference: FFmpeg]:

1. A `u32` chunk size, then `u16` flags. If flags bit 3 is set: a `u16` starting line, 2 bytes, a `u16` line count,
   2 bytes.
2. Per line, a skip byte: 0 ends the frame; otherwise skip `skip − 1` units. Then codes until −1:
   - `0`: another skip byte follows;
   - `n > 0`: n literal units follow;
   - `n < −1`: the next unit is repeated −n times.

| Depth | Unit |
| --- | --- |
| 1 | 16 pixels in 2 bytes |
| 2 | 16 pixels in 4 bytes |
| 4 | 8 pixels in 4 bytes |
| 8 | 4 pixels in 4 bytes |
| 16 | One RGB 555 pixel (2 bytes) |
| 24 | One RGB pixel (3 bytes) |
| 32 | One ARGB pixel (4 bytes) |

### 2.8 The rpza codec

`rpza`, Road Pizza (Apple Video) [Reference: FFmpeg]:

1. A byte `$E1`, a `u24` length, then opcodes over 4 × 4 blocks in raster order. Colours are RGB 555 (mask `$7FFF`).
2. Opcodes:
   - `$80 + n − 1`: skip n blocks;
   - `$A0 + n − 1`, a colour: n solid blocks;
   - `$C0 + n − 1`, colour A, colour B: n four-colour blocks, each followed by 4 index bytes;
   - a byte with bit 7 clear starts a single block and, with the next byte, forms colour A. If the byte after that has
     bit 7 set, it starts colour B of a four-colour block; otherwise 15 more colours follow, a 16-colour block.
3. A four-colour block's colours, by 2-bit index, are `{B, (11A + 21B) >> 5, (21A + 11B) >> 5, A}`, per 5-bit
   component. One index byte per row, most significant pair first.

### 2.9 The smc codec

`smc `, Graphics [Reference: FFmpeg]:

1. A flags byte and a `u24` length, then opcodes over 4 × 4 blocks of 8-bit indices, with three 256-entry circular
   caches of 2-, 4- and 8-colour sets.
2. The high nibble is the operation and the low nibble `n − 1`; the second of each pair takes the count from the next
   byte + 1:

| Opcode | Operation |
| --- | --- |
| 0x/1x | Skip |
| 2x/3x | Repeat the previous block |
| 4x/5x | Repeat the previous two blocks |
| 6x/7x | One colour byte: solid blocks |
| 8x/9x | Two-colour blocks: 8x reads a new pair into the cache, 9x a cache index; then `u16` flags per block, bit 15 pixel 0 |
| Ax/Bx | Four-colour blocks: `u32` flags, 2 bits per pixel, top first |
| Cx/Dx | Eight-colour blocks: 6 bytes per block, nibbles n0…nB |
| Ex | 16 raw indices per block |

3. An eight-colour block's pixels 0–7 take 3-bit indices from `n0 n1 n2 n4 n5 n6`, and pixels 8–15 from
   `n8 n9 nA n3 n7 nB`, each read as a 24-bit value, most significant first.

### 2.10 The cvid codec

`cvid`, Cinepak [Reference: FFmpeg]:

1. Frame header: flags, `u24` length, `u16` width, `u16` height, `u16` strip count.
2. Strips: `u16` id, `u16` size, then y1, x1, y2, x2. A strip's height is `y2 − y1`, or the rest of the frame when
   that is ≤ 0.
3. Chunks: `u16` id, `u16` size including the header.
4. Codebook chunks (`$20xx`): `$0200` selects the V1 book (else V4); `$0400` means 4-byte entries (luma only: grey or
   palette index), else 6 bytes, Y0 Y1 Y2 Y3, `i8` U, `i8` V; `$0100` is a partial update, a `u32` bit mask (most
   significant bit first) before each 32 entries.
5. Vector chunks cover the strip's 4 × 4 blocks in raster order, reading a `u32` mask stream most significant bit
   first:
   - `$3000`: one bit per block; 1 is four V4 indices (2 × 2 each), 0 one V1 index (its 4 lumas each fill a 2 × 2);
   - `$3100`: first a "changed" bit (0 skips the block), then the V4/V1 bit;
   - `$3200`: V1 only, with no bits.
6. Colour: `R = Y + 2V`, `G = Y − U/2 − V` (U/2 truncated toward zero), `B = Y + 2U`, clamped. At depths ≤ 8 the Y
   value is a palette index; at 33–40 it is grey.

### 2.11 The 8BPS codec

`8BPS`, Planar RGB [Reference: FFmpeg]: `u16` packed byte counts for each row of each plane, then the planes'
PackBits rows ([packbits.md](../codecs/packbits.md)). The planes are R, G, B, then alpha at depth 32; at depth 8 there
is one plane of palette indices.

### 2.12 The yuv2 and YVU9 codecs

- `yuv2`: per pixel pair, `Y0 U Y1 V`, U and V signed bytes [Reference: FFmpeg].
- `YVU9`: a full Y plane, then V and U planes subsampled 4 × 4, unsigned and centred on 128 [Reference: FFmpeg].
- Both convert full-range, as JFIF does [Doc: JFIF]: `R = Y + 1.402 V`, `G = Y − 0.344136 U − 0.714136 V`,
  `B = Y + 1.772 U`.

### 2.13 The tga codec

`tga `, a complete Targa file [Author: Truevision TGA specification]: an 18-byte header, then the image ID, the colour
map and the pixels; types 1, 2 and 3, raw or RLE (+8); 8, 15, 16, 24 or 32 bits; bottom-up unless descriptor bit 5 is
set.

### 2.14 The PNTG codec

`PNTG`: a MacPaint page without its header, 576 × 720 at 1 bit per pixel, 72-byte rows PackBits-compressed back to
back, 1 black ([macpaint.md §1.1](macpaint.md#11-the-document)).

### 2.15 Other codecs

`jpeg`, `png `, `gif `, `tiff` and others hold a complete file in that format, for a general image decoder. An image
that cannot be decoded draws nothing, and the picture's fallback drawing (§2.3) is shown, as on a Mac without that
decompressor.

## 3. Writing

None.

## 4. Variants

- A Mac without QuickTime skips both opcodes by their `u32` length and shows the fallback drawing instead
  ([pict.md §1.4](pict.md#14-opcodes)). [Doc: Inside Macintosh: Imaging With QuickDraw]

## 5. ClassicMac

- The built-in codecs are `raw `, `rle `, `rpza`, `smc `, `cvid`, `8BPS`, `yuv2`, `YVU9`, `tga ` and `PNTG`. Any
  other goes to the caller's `IPictImageCodec` (`PictDecodeOptions.ImageCodec`, or the codec passed to
  `QuickTimeImageFile.Decode`). A codec returns null for data it cannot decode, and corrupt data in a built-in codec
  counts as undecodable. [ClassicMac]
- The ImageSharp adapter's `ImageSharpImageCodec` decodes `jpeg`, `png `, `gif `, `tiff`, `webp` and `WRLE` (Windows
  BMP without its file header); the SkiaSharp adapter's `SkiaImageCodec` decodes what Skia reads (PNG, JPEG and
  others) and leaves TIFF to the picture's fallback. [ClassicMac]
- The matte is skipped and not applied; rotation and skew are not modelled: the image is drawn into its bounding box.
  [ClassicMac]
- An empty srcRect in `$8200` means the whole image. A mask region that cannot be read is ignored. A block too short
  for its fields, or whose description does not fit, is not drawn (and the fallback is not skipped). [ClassicMac]
- The `PNTG` codec always decodes a 576 × 720 page, whatever the description's size. [ClassicMac]
- A stored colour table is read only when idSize is over 86; its entries are taken by position, and at most 256.
  [ClassicMac]
- In `$8201`, opcodes other than `$0090`–`$0093` and `$0098`–`$009B` are not drawn; in the Mac OS 9 mode `$0092` and
  `$0093` are not drawn either ([pict.md §4.2](pict.md#42-mac-os-9)). [ClassicMac]
- QTIF: `QuickTimeImageFile.IsQuickTimeImageFile` needs 8 bytes and a first atom of size 1 or at least 8 whose type is
  `idsc`, `idat` or `iicc`. `ReadDescription`, `ReadIccProfile`, `Decode` and `Read` (the image, description and ICC
  profile) take bytes or a stream, read from its position to its end and left open. A file without `idsc` or `idat`,
  or whose codec nothing decodes, is refused with `NotSupportedException`. [ClassicMac]
- The ImageSharp adapter registers QTIF (`QuickTimeImageFormat`: extensions `qtif`, `qti`, `qif`; MIME
  `image/x-quicktime`, `image/qtif`), with the resolution and ICC profile as metadata; `PictSkia.DecodeAny` tries QTIF
  first. [ClassicMac]

## 6. Diagnostics

None. `ClassicMac.Graphics` reports no diagnostics for QuickTime images: an image it cannot decode in a picture is left
to the fallback drawing, and an unreadable QTIF file is refused with an exception (§5).

## 7. Verification

Hand-built images only; no picture or QTIF file made by QuickTime is in the repository.

- During development the `raw `, `rle ` (16, 24 and 32 bits), `rpza`, `smc ` and `cvid` decoders matched FFmpeg's
  decoders pixel for pixel on sample files and FFmpeg-encoded frames; the samples are not committed.
- `QuickTimeTests` (`tests/ClassicMac.Graphics.Tests`): `$8201` draws its bitmap opcode, skips the fallback and is
  placed by a scaling matrix; a 32-bit `raw ` image in `$8200` is drawn where the matrix puts it; the PnSize marker and
  a bitmap into the same rect are skipped; an unknown codec goes to the caller's codec, then to the fallback; the
  8-bit standard table; `raw ` 8-bit; `rpza` solid and four-colour blocks; `smc ` two-colour blocks and repeats; `rle `
  24-bit skips, literals and repeats; `8BPS` planes; bottom-up true-colour Targa; PNG through the ImageSharp codec.
- `MacImageFileTests`: a QTIF file decodes with the built-in codecs, an unsupported codec throws, and ImageSharp loads
  one with an embedded PNG.
- `StreamDecodingTests`: QTIF files read from a stream match the bytes; files without a description or an image fail
  alike; ImageSharp identifies a QTIF file from its atoms.
- `PictParserTests`: a QuickTime opcode without codec support is skipped by its length.
- `SkiaSharpPluginTests`: the Skia codec decodes PNG and leaves TIFF to the fallback.

## 8. Not covered

- Mattes, rotation and skew.
- The placement of `$8201` under a matrix other than the identity was not checked against QuickTime.
- On indexed screens the Image Compression Manager's codecs dither to the screen themselves; ClassicMac draws the
  decoded image through CopyBits instead ([quickdraw.md §4.6](quickdraw.md#46-screen-depths)).
- Codecs beyond the built-in ones, without a caller's codec.

## 9. References

1. Apple Computer, *Inside Macintosh: QuickTime* (1993), the Image Compression Manager and the QuickTime picture
   opcodes. Apple's documentation.
2. Apple Computer, *QuickTime File Format* (2001), image descriptions and QTIF atoms. Apple's documentation.
3. Apple Computer, *Inside Macintosh: Imaging With QuickDraw* (1994), Appendix A and the default colour tables.
   Apple's documentation.
4. Truevision, *Truevision TGA File Format Specification*, version 2.0. The format author's documentation.
5. JPEG File Interchange Format, version 1.02 (the YCbCr conversion). A standard.
6. FFmpeg, its `rpza`, `smc`, `qtrle`, `cinepak`, `8bps` and raw video decoders. LGPL-2.1 or later; behaviour
   reference only, no code used.
