# TIFF images

The Tag Image File Format, Aldus's and Microsoft's format for scanned and edited images, later Adobe's: a header,
then image file directories (IFDs) of tagged fields that describe an image whose pixels lie in strips or tiles. On
the Mac it was the format of scanners (ThunderScan among the first), Photoshop, PageMaker and QuarkXPress, and
QuickTime imported it. Mac programs wrote it big-endian (`MM`). ClassicMac reads the first image of a file.

| | |
| --- | --- |
| Identified by | File type `TIFF`; extensions `.tif`, `.tiff`; `II` and 42 little-endian, or `MM` and 42 big-endian, at the start |
| ClassicMac | Reads; `ClassicMac.Graphics.TiffFile`; the app's preview |
| Verified against | libtiff's sample images (libtiff-pics): every image both read decodes as ImageSharp's reader decodes it, within 1 level per channel |
| Sources | Adobe Developers Association, *TIFF Revision 6.0* (1992); Adobe, *TIFF Technical Note 2* (Deflate); libtiff, as behaviour |

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

### 1.1 Header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Byte order | `II` little-endian, `MM` big-endian; every later number is in this order [Author] |
| +$02 | 2 | 42 | The version number, never changed [Author] |
| +$04 | 4 | First IFD | Its offset from the start of the file [Author] |

### 1.2 IFD

A count (2 bytes), that many 12-byte entries sorted by tag, then the next IFD's offset (4 bytes, 0 for none)
[Author].

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Tag | |
| +$02 | 2 | Type | 1 BYTE, 2 ASCII, 3 SHORT, 4 LONG, 5 RATIONAL; 6–12 the signed, float and double types (6.0) [Author] |
| +$04 | 4 | Count | Values, not bytes [Author] |
| +$08 | 4 | Value or offset | The values themselves when they fit in 4 bytes (left-justified), else their offset [Author] |

### 1.3 The tags read

| Tag | Name | Default | Notes |
| --- | --- | --- | --- |
| 256 | ImageWidth | (required) | Pixels per row [Author] |
| 257 | ImageLength | (required) | Rows [Author] |
| 258 | BitsPerSample | 1 | One value per sample [Author] |
| 259 | Compression | 1 | 1 none, 2 CCITT modified Huffman, 3 CCITT Group 3, 4 CCITT Group 4, 5 LZW, 8 and 32946 Deflate, 32771 word-aligned modified Huffman, 32773 PackBits, 32809 ThunderScan [Author] |
| 262 | PhotometricInterpretation | (required) | 0 WhiteIsZero, 1 BlackIsZero, 2 RGB, 3 palette, 5 separated (CMYK), 6 YCbCr, 8 CIE L\*a\*b\*, 9 ICC L\*a\*b\* [Author] |
| 266 | FillOrder | 1 | 1 most significant bit first; 2 least significant first [Author] |
| 273 | StripOffsets | (required for strips) | One per strip, per plane when planar [Author] |
| 277 | SamplesPerPixel | 1 | [Author] |
| 278 | RowsPerStrip | 2³² − 1 | So one strip by default [Author] |
| 279 | StripByteCounts | (required for strips) | Compressed bytes per strip [Author] |
| 284 | PlanarConfiguration | 1 | 1 chunky (a pixel's samples together), 2 planar (a plane per sample) [Author] |
| 292 | T4Options | 0 | Bit 0 two-dimensional coding; bit 2 fill bits before each EOL [Author] |
| 317 | Predictor | 1 | 2 the horizontal predictor; 3 the floating-point predictor [Author] |
| 320 | ColorMap | | 3 × 2^bits SHORTs: all reds, all greens, all blues; 0–65535 [Author] |
| 322, 323 | TileWidth, TileLength | | Present only in a tiled image [Author] |
| 324, 325 | TileOffsets, TileByteCounts | | One per tile, per plane when planar, tiles left to right, top to bottom [Author] |
| 332 | InkSet | 1 | 1 CMYK [Author] |
| 338 | ExtraSamples | | 0 unspecified, 1 associated alpha (premultiplied), 2 unassociated alpha [Author] |
| 339 | SampleFormat | 1 | 1 unsigned, 2 signed integer, 3 IEEE floating point [Author] |
| 529 | YCbCrCoefficients | 299/1000, 587/1000, 114/1000 | LumaRed, LumaGreen, LumaBlue (RATIONALs) [Author] |
| 530 | YCbCrSubSampling | 2, 2 | Horizontal and vertical: 1, 2 or 4 each [Author] |
| 532 | ReferenceBlackWhite | 0, 255, 128, 255, 128, 255 | Footroom and headroom of Y, Cb, Cr (RATIONALs) [Author] |

### 1.4 Rows

A row is its pixels' samples in order (one sample per pixel in a plane of a planar image), packed most significant
bit first, and ends on a byte boundary. A strip holds `RowsPerStrip` rows (the last fewer); a tile is always
TileWidth × TileLength pixels, padded past the image's right and bottom edges [Author]. Samples of 16, 24 and 32 bits
are numbers in the file's byte order; other depths are a bit stream [Reference: libtiff].

## 2. Reading

1. Check the header; read the first IFD.
2. Take the tags of §1.3 with their defaults.
3. For each strip or tile: decompress its byte count at its offset into its rows; with the predictor, add each sample
   to the one a pixel before it in its row, modulo the sample's size [Author].
4. Turn each pixel into a colour: WhiteIsZero 0 is white; BlackIsZero 0 is black; a palette pixel looks up the
   ColorMap; RGB as it is; alpha from the first extra sample [Author].

### 2.1 PackBits

As Apple's: [packbits.md](../codecs/packbits.md) [Author].

### 2.2 LZW

1. Codes are read most significant bit first, 9 bits at first [Author].
2. Code 256 clears the table (the next code is 258, the width 9); 257 ends the strip [Author].
3. A code in the table outputs its string; the code equal to the next one outputs the previous string and its own
   first byte; either way the previous string plus this output's first byte becomes the next entry [Author].
4. The width grows to 10, 11 and 12 bits when the next code is 511, 1023 and 2047: one code earlier than TIFF 6.0's
   text describes, as every writer does it [Reference: libtiff].

### 2.3 Deflate

A zlib stream (RFC 1950 and 1951) per strip or tile [Author: TIFF Technical Note 2].

### 2.4 ThunderScan

Thunderware's 4-bit compression for its Macintosh scanner. Each row is coded on its own and starts from pixel 0; a
byte's top two bits say what it is [Reference: libtiff]:

| Top bits | Meaning |
| --- | --- |
| 00 | The last pixel again, as many times as the low six bits say |
| 01 | Three 2-bit deltas (bits 5–4, 3–2, 1–0): 0 → 0, 1 → +1, 2 → none, 3 → −1 |
| 10 | Two 3-bit deltas (bits 5–3, 2–0): 0–3 → 0 to +3, 4 → none, 5–7 → −3 to −1 |
| 11 | A pixel: the low four bits |

Each pixel written is kept to four bits and becomes the last pixel.

### 2.5 CCITT

Bilevel images only. A white run is 0 bits and a black run 1 bits; PhotometricInterpretation then says which is
white [Author]. With FillOrder 2 each byte's bits are reversed first [Author].

1. **Runs** (T.4 §4.1): a run is any makeup codes (64 to 2560) and then one terminating code (0 to 63), from the white
   or the black table of T.4 Tables 2 and 3; the codes from 1792 up are shared [Author: ITU-T T.4].
2. **Modified Huffman** (2): each row is runs from white, alternating, until the width is filled; rows start on a
   byte boundary, without EOLs. Compression 32771 starts them on a 16-bit word [Author].
3. **Group 3** (3): each row is preceded by an EOL (eleven zeros and a one, after any fill zeros); with T4Options
   bit 0, a bit after the EOL says the row is one-dimensional (1) or two-dimensional (0) [Author: ITU-T T.4].
4. **Two-dimensional rows** (Group 3 with bit 0, and every Group 4 row): changes are coded against the row above,
   which is white above the first row. From a0 (−1 at the start, the colour white), b1 is the first change on the row
   above past a0 that turns to the other colour and b2 the change after it; the modes are pass (`0001`: a0 moves to
   b2), horizontal (`001`, then a run of the current colour and one of the other) and vertical (`1`, `011`/`010`,
   `000011`/`000010`, `0000011`/`0000010`: a1 is b1 + 0, ±1, ±2, ±3, and the colour flips) [Author: ITU-T T.4 §4.2,
   T.6].
5. **Group 4** (4): two-dimensional rows without EOLs or alignment [Author: ITU-T T.6].

### 2.6 YCbCr

Without subsampling (1, 1) a pixel's samples are Y, Cb, Cr. Subsampled, the data comes in units of
YCbCrSubSampling[0] × YCbCrSubSampling[1] Y samples (row by row within the unit) followed by one Cb and one Cr,
the units left to right, then down; units past the image's right or bottom edge are padded [Author: TIFF 6.0 §21].
Each component is first scaled by ReferenceBlackWhite, Y to 0–255 and Cb and Cr to ±127, then
R = Y + Cr × (2 − 2 × LumaRed), B = Y + Cb × (2 − 2 × LumaBlue), G = (Y − LumaBlue × B − LumaRed × R) / LumaGreen
[Author: TIFF 6.0 §21].

### 2.7 CIE L\*a\*b\*

L runs over the sample's range for 0–100; a\* and b\* are two's complement numbers (CIELab, 8) or numbers about the
middle of their range (ICCLab, 9), in units of 1 at 8 bits and 1/256 at 16 [Author: TIFF 6.0 §23; Adobe TIFF
Technical Note: ICCLab].

### 2.8 Sample formats and the floating-point predictor

SampleFormat 2 samples are two's complement; 3 are IEEE 754 numbers of 16, 32 or 64 bits [Author]. The floating-point
predictor (3) splits each row's samples into byte planes, most significant byte first, then codes each byte as the
difference from the one a pixel (as many bytes as there are samples) before it [Author: Adobe Photoshop TIFF
Technical Note 3].

## 3. Writing

None.

## 4. Variants

- TIFF 5.0's LZW wrote its codes least significant bit first and grew the width when the next code reached 512,
  1024 and 2048, without the early change; such strips start with Clear written that way, `00` then a byte whose low
  bit is 1 [Reference: libtiff].
- Some writers put a tiled image's TileOffsets and TileByteCounts under the strip tags (273, 279); libtiff takes them
  as the same fields [Reference: libtiff].
- Mac OS 9 and the 68k ROM have no TIFF code; QuickTime's importer read it.

## 5. ClassicMac

- The first image of the file is read; further IFDs (pages, thumbnails) are not. [ClassicMac]
- Samples deeper than 8 bits keep their high 8 bits; shallower ones are spread over 0–255; a palette index uses all
  its bits. [ClassicMac]
- Signed samples are moved up by half their range. Floating-point samples between 0 and 1 are levels; an image
  whose colour samples reach outside 0–1 is stretched linearly from its smallest to its largest value (a
  satellite or scientific image has no white). [ClassicMac]
- YCbCr is converted with the image's coefficients and reference black and white, without filtering the
  subsampled chroma; CIE L\*a\*b\* becomes sRGB through XYZ with the D65 white. [ClassicMac]
- CMYK becomes RGB without colour management, as libtiff's RGBA interface converts it: R = (255 − C) × (255 − K) / 255,
  likewise G and B. [Reference: libtiff]
- Associated alpha is divided out, rounded; RGB and grey take alpha, palette and CMYK do not. [ClassicMac]
- Images over 64 Mi pixels are refused. What a short strip or tile lacks stays white. [ClassicMac]
- A tiled image's offsets are taken from the strip tags when the tile tags are missing (§4). [ClassicMac]
- The app previews a file of type `TIFF`, a file named `.tif` or `.tiff`, or a file with no type that has the header.
  [ClassicMac]
- Damage throws `InvalidDataException`; a valid file laid out in a way not read throws `NotSupportedException` whose
  message names it (the compression, the photometric interpretation, floating-point samples). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `tiff.short-strip` | Warning | strips or tiles decompress to fewer bytes than their rows (one diagnostic for the image, with the count) | leaves what they lack white | no Mac counterpart |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/TiffTests.cs`: both byte orders; bilevel, grey of 8, 12, 16 and 32 bits; 2-bit
  RGB; palettes of 4 and 16 bits; PackBits; LZW past 9-bit codes with the predictor over two strips; TIFF 5.0 LZW;
  Deflate; ThunderScan; planar samples; tiles cut at the edge; alpha, associated and not; 16-bit RGB; CMYK; a short
  strip; what is not read; damage; other files.
- `tests/ClassicMac.Graphics.Tests/TiffColorTests.cs`: YCbCr with and without subsampling, its coefficients and
  reference black and white; CIELab and ICCLab; signed samples; 16-, 32- and 64-bit floats, in and outside 0–1;
  the floating-point predictor.
- `tests/ClassicMac.Graphics.Tests/TiffFaxTests.cs`: modified Huffman, word-aligned rows, makeup and extended codes,
  Group 3 one- and two-dimensional, fill bits before EOLs, Group 4 vertical and pass modes, FillOrder 2, BlackIsZero,
  damaged data.
- `tests/ClassicMac.App.Tests/FilePreviewTests.cs`: the preview by type and by extension.
- libtiff's sample images (libtiff-pics, not in the repository) were decoded and compared with ImageSharp's reader:
  `cramps.tif` (big-endian PackBits), `cramps-tile.tif` and `quad-tile.tif` (tiles), `jello.tif`, `strike.tif` (LZW
  palette, RGBA), `ladoga.tif` (16-bit Deflate), `oxford.tif` (planar LZW), `pc260001.tif`, the `jim___*` halftones
  and the `depth/flower-*` set (grey, palette, RGB chunky and planar, CMYK, 2 to 32 bits), and `fax2d.tif` and
  `g3test.tif` (Group 3 two- and one-dimensional), and `ycbcr-cat.tif` (subsampled YCbCr), all match within 1 level.
  `caspian.tif` (64-bit floats with Deflate), which ImageSharp draws as stripes, shows the satellite image;
  `dscf0013.tif` (a camera's YCbCr) and the 16-bit and planar CMYK images, which ImageSharp does not read, were
  checked by eye.
  `quad-lzw.tif` (TIFF 5.0 LZW) and `text.tif` (ThunderScan, a scanned Mac printout), which ImageSharp does not read,
  were checked by eye; `text.tif`'s last strip holds 36 of its 39 rows.

## 8. Not covered

- Compression: JPEG (6, 7), SGILog (34676, 34677), NeXT (32766) and others; CCITT's uncompressed mode (T.4 extensions).
- Photometric interpretations: ITU L\*a\*b\* (10), LogL and LogLuv; CMYK ink sets other than 1; subsampled YCbCr
  that is planar, not 8-bit or with a predictor.
- 24-bit floating-point samples.
- More than one image; colour management (ICC profiles); writing TIFF.

## 9. References

- Adobe Developers Association, *TIFF Revision 6.0, Final*, 3 June 1992.
- Adobe, *TIFF Technical Note 2*, 17 March 2002: Deflate compression.
- ITU-T Recommendation T.4 (Group 3 facsimile) and T.6 (Group 4 facsimile).
- libtiff (BSD-style licence): `tif_lzw.c` (the code-width rule of §2.2 and TIFF 5.0's LZW), `tif_thunder.c` (§2.4),
  `tif_getimage.c` (CMYK), `tif_dirread.c` (the tile and strip tags), as behaviour only.
- libtiff-pics, the libtiff test images (<https://gitlab.com/libtiff/libtiff-pics>).
