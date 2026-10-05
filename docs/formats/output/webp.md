# Lossless WebP

The image files ClassicMac writes in place of PNG when asked: WebP's lossless form (VP8L), whose pixels are exact as
PNG's and whose files are usually smaller. WebP is Google's format, specified in RFC 9649; no Mac software of the time
reads it. ClassicMac writes it; it does not read it.

| | |
| --- | --- |
| Identified by | `.webp` files from the image decoders and document converters, when `DecodeOptions.ImageEncoder` is `WebPEncoder` (the CLI's `--image-format webp`); `RIFF` … `WEBP` `VP8L` at the start |
| ClassicMac | Writes; `ClassicMac.Resources.Decoders.Images.WebPEncoder` (the VP8L stream in `Images/WebP/`) |
| Verified against | ImageSharp's and libwebp's decoders (the latter through SkiaSharp): every test image read back pixel for pixel |
| Sources | RFC 9649, *WebP Image Format* (Google): the RIFF container and the VP8L bit stream |

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

The file is a RIFF container of one chunk [Author: RFC 9649 §2]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | `RIFF` | |
| +$04 | 4 | File size | `u32` little-endian: the bytes after this field |
| +$08 | 4 | `WEBP` | |
| +$0C | 4 | `VP8L` | The chunk's tag |
| +$10 | 4 | Chunk size | `u32` little-endian: the VP8L data's length, without the pad byte |
| +$14 | n | VP8L data | §1.1; a zero byte follows when n is odd |

### 1.1 The VP8L bit stream

Bits are read least significant first [Author: RFC 9649 §3]:

| Bits | Field | Notes |
| --- | --- | --- |
| 8 | Signature | $2F |
| 14 | Width − 1 | Sides of 1 to 16,384 pixels |
| 14 | Height − 1 | |
| 1 | Alpha used | A hint only |
| 3 | Version | 0 |
| … | Transforms | Each: 1 (present), its 2-bit type and its data; then 0 |
| … | The image | An entropy-coded image of the pixels after the transforms (§3.2) |

## 2. Reading

None: ClassicMac does not read WebP.

## 3. Writing

### 3.1 Transforms

[Author: RFC 9649 §4], the choices [ClassicMac]:

1. **Subtract green** (type 2): red and blue each less green, modulo 256.
2. **Predictor** (type 0), size bits 4 (16 × 16 blocks): for each block the mode (0–13) whose residuals are smallest
   (the sum over its pixels and channels of each residual's distance from 0, wrapping); the modes as a sub-image, one
   pixel per block, the mode in green (alpha $FF); every pixel's residual against its prediction from its neighbours
   (the top-left pixel against opaque black, the top row against the left pixel, the left column against the top one,
   the last column's top-right being the row's first pixel), each channel modulo 256.

### 3.2 Entropy-coded images

The predictor's sub-image and the main image are each written as [Author: RFC 9649 §5]: the colour cache flag (and its
size in bits), for the main image the meta prefix code flag (0: one group of codes), the five prefix codes (green with
the 24 length codes and the cache's, red, blue, alpha, the 40 distance codes), then the pixels as tokens:

- a **backward reference**: the longest match of at least 3 pixels among earlier positions with the same pixel pair
  (48 tried at most, up to 4,096 pixels long, within $FFF88 pixels), its length and distance each a prefix symbol and
  extra bits; a distance of one row or one pixel takes the neighbourhood codes 1 and 2, any other its distance + 120;
- else a **colour cache hit**: the pixel at its hash's place in the cache (the hash $1E35A7BD × ARGB, shifted);
- else a **literal**: green, red, blue and alpha, each through its code.

Every pixel decoded goes into the cache, as the decoder puts it there. [ClassicMac] for the matching rules and limits.

### 3.3 Prefix codes

Each alphabet's Huffman code lengths come from its symbol counts, at most 15 bits: when longer, built again with every
count raised to a minimum that doubles (as libwebp does) [ClassicMac]. Codes are canonical, by length then symbol, and
written bit-reversed [Author: RFC 9649 §3.7.2]. A code of one or two symbols under 256 is written simple; an unused
alphabet as a simple code of the one symbol 0; any other as its code lengths, run-length coded (16 repeats the previous
non-zero length 3–6 times, 17 and 18 runs of zeros) through a code-length code of at most 7 bits, written in its fixed
order and then every symbol's length. A code with one symbol takes no bits per symbol.

### 3.4 The colour cache size

The stream is made with no cache, a 6-bit and a 10-bit cache, and the smallest kept [ClassicMac].

## 4. Variants

ClassicMac writes only the lossless form, with the subtract-green and predictor transforms; not lossy VP8, the extended
`VP8X` container, animation, or the colour-transform and colour-indexing transforms.

## 5. ClassicMac

- `WebPEncoder.Instance` is an `IImageEncoder` (`Name` `webp`, `Extension` `.webp`) for `DecodeOptions.ImageEncoder`,
  PNG being the default. The CLI's `extract` and `convert` take `--image-format png|webp`; the app's exports follow
  Export ▸ Images as WebP (kept between sessions; previews are drawn as before). [ClassicMac]
- A side over 16,384 pixels, which VP8L cannot hold, is refused (`ArgumentOutOfRangeException`). [ClassicMac]

## 6. Diagnostics

None: the encoder throws on a size it cannot hold.

## 7. Verification

- `tests/ClassicMac.Graphics.Tests/WebPEncoderTests.cs`: noise, gradients, flat areas, transparency, repeated
  patterns (smaller than the PNG), and sides of 1 and odd sizes, each decoded by ImageSharp and by libwebp (SkiaSharp)
  back to the same pixels; the sizes refused.
- `tests/ClassicMac.Cli.Tests/ExtractTests.cs`, `Image_format_webp_writes_lossless_WebP`; the app's
  `ExportFormatTests` (the menu choice kept, Extract All writing WebP).
- Mac OS 9's Geneva suitcase's bitmap strikes: 36,046 bytes as WebP, 46,031 as ClassicMac's PNG.

## 8. Not covered

- The colour-transform and colour-indexing (palette) transforms, meta prefix codes, and a cache size chosen per image
  beyond the three tried.
- Reading WebP.

## 9. References

1. J. Zern, P. Massimino, J. Alakuijala, RFC 9649, *WebP Image Format*, 2024.
