# TIFF images

The Tag Image File Format, Aldus's and Microsoft's format for scanned and edited images, later Adobe's: a header,
then image file directories (IFDs) of tagged fields that describe an image whose pixels lie in strips or tiles. On
the Mac it was the format of scanners, Photoshop, PageMaker and QuarkXPress, and QuickTime imported it. Mac programs
wrote it big-endian (`MM`). ClassicMac reads the first image of a baseline TIFF 6.0 file.

| | |
| --- | --- |
| Identified by | File type `TIFF`; extensions `.tif`, `.tiff`; `II` and 42 little-endian, or `MM` and 42 big-endian, at the start |
| ClassicMac | Reads; `ClassicMac.Graphics.TiffFile`; the app's preview |
| Verified against | Nothing yet |
| Sources | Adobe Developers Association, *TIFF Revision 6.0* (1992) |

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
| 259 | Compression | 1 | 1 none, 5 LZW, 32773 PackBits [Author] |
| 262 | PhotometricInterpretation | (required) | 0 WhiteIsZero, 1 BlackIsZero, 2 RGB, 3 palette, 5 separated (CMYK) [Author] |
| 273 | StripOffsets | (required) | One per strip [Author] |
| 277 | SamplesPerPixel | 1 | [Author] |
| 278 | RowsPerStrip | 2³² − 1 | So one strip by default [Author] |
| 279 | StripByteCounts | (required) | Compressed bytes per strip [Author] |
| 284 | PlanarConfiguration | 1 | 1 chunky (samples of a pixel together) [Author] |
| 317 | Predictor | 1 | 2 the horizontal predictor [Author] |
| 320 | ColorMap | | 3 × 2^bits SHORTs: all reds, all greens, all blues; 0–65535 [Author] |
| 332 | InkSet | 1 | 1 CMYK [Author] |
| 338 | ExtraSamples | | 0 unspecified, 1 associated alpha (premultiplied), 2 unassociated alpha [Author] |

### 1.4 Rows

A row is its pixels' samples in order, `BitsPerSample` each, packed most significant bit first, and ends on a byte
boundary; strips hold `RowsPerStrip` rows (the last strip fewer) [Author].

## 2. Reading

1. Check the header; read the first IFD.
2. Take the tags of §1.3 with their defaults.
3. For each strip: decompress `StripByteCounts` bytes at `StripOffsets` into its rows; with the predictor, add each
   sample to the one a pixel before it in its row (16-bit samples as numbers in the file's byte order, modulo
   2¹⁶) [Author].
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

## 3. Writing

None.

## 4. Variants

- TIFF 5.0's LZW wrote its codes least significant bit first; such strips start with the bytes `00 01` [Reference:
  libtiff]. They are not read.
- Mac OS 9 and the 68k ROM have no TIFF code; QuickTime's importer read it.

## 5. ClassicMac

- The first image of the file is read; further IFDs (pages, thumbnails) are not. [ClassicMac]
- Samples of 16 bits keep their high byte; fewer than 8 bits are spread over 0–255. [ClassicMac]
- CMYK becomes RGB by subtraction, without colour management: each ink removes its colour and black removes all.
  [ClassicMac]
- Associated alpha is divided out, rounded; RGB and grey take alpha, palette and CMYK do not. [ClassicMac]
- Images over 64 Mi pixels are refused. Rows a short strip lacks stay white. [ClassicMac]
- The app previews a file of type `TIFF`, a file named `.tif` or `.tiff`, or a file with no type that has the header.
  [ClassicMac]
- Damage throws `InvalidDataException`; a valid file laid out in a way not read throws `NotSupportedException` whose
  message names it (compression, tiles, planar samples, the photometric interpretation or sample size). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `tiff.short-strip` | Warning | a strip decompresses to fewer bytes than its rows | leaves the rows it lacks white | no Mac counterpart |

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/TiffTests.cs`: both byte orders, bilevel (WhiteIsZero, BlackIsZero), grey of 8 and
  16 bits, palette, PackBits, LZW past 9-bit codes with the predictor over two strips, alpha (associated and not),
  16-bit RGB, CMYK, a short strip, what is not read, damage, other files.
- `tests/ClassicMac.App.Tests/FilePreviewTests.cs`: the preview by type and by extension.

## 8. Not covered

- Tiles, planar samples (PlanarConfiguration 2), more than one image.
- Compression other than none, PackBits and LZW: CCITT (2, 3, 4), JPEG (6, 7), Deflate (8), TIFF 5.0's LZW.
- CIE L\*a\*b\*, YCbCr and CMYK ink sets other than 1; floating-point samples; colour management (ICC profiles).
- Writing TIFF.

## 9. References

- Adobe Developers Association, *TIFF Revision 6.0, Final*, 3 June 1992.
- libtiff (BSD-style licence), `tif_lzw.c`: the code-width rule of §2.2 and TIFF 5.0's LZW, as behaviour only.
