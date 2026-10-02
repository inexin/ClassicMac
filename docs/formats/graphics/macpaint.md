# MacPaint documents

The document format of MacPaint and of the many programs that read and wrote it: one 576 × 720 page at 1 bit per
pixel, a 512-byte header with the document's fill patterns, then 720 PackBits-compressed rows. The same rows, without
the header, are QuickTime's `PNTG` codec ([quicktime.md §2.14](quicktime.md#214-the-pntg-codec)). ClassicMac reads
the documents, bare or MacBinary-wrapped, and draws the page black on white.

| | |
| --- | --- |
| Identified by | File type `PNTG`; extensions `.pntg`, `.pnt`, `.mac`. No signature: a version of 0, 2 or 3 at +$00, zero padding at +$134–+$1FF and a first row that unpacks to exactly 72 bytes (§2.1) |
| ClassicMac | Reads; `ClassicMac.Graphics.MacPaintFile` |
| Verified against | Nothing yet |
| Sources | Apple's Macintosh Technical Note #86, *MacPaint Document Format* |

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

### 1.1 The document

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 4 | Version | `u32`: 0, 2 or 3. Version 0 has no patterns; 2 and 3 carry them |
| +$004 | 304 | Patterns | 38 fill patterns of 8 bytes, one byte per row, most significant bit leftmost (versions 2 and 3) |
| +$134 | 204 | Padding | Zeros |
| +$200 | | Rows | 720 rows of 72 bytes (576 pixels), each PackBits-compressed on its own ([packbits.md](../codecs/packbits.md)) |

A pixel is a bit, most significant bit leftmost; 1 is black, 0 white. [Doc: TN #86]

### 1.2 The MacBinary wrapper

A document copied off a Mac often carries a 128-byte MacBinary header before the data fork
([macbinary.md](../containers/macbinary.md)). The fields a reader of MacPaint documents needs:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Old version | 0 |
| +$01 | 1 | Name length | 1–63; the name follows |
| +$41 | 4 | File type | `PNTG` |
| +$4A | 1 | Zero | 0 |
| +$52 | 1 | Zero | 0 |
| +$53 | 4 | Data fork length | `u32` |
| +$80 | | Data fork | The document (§1.1) |

## 2. Reading

### 2.1 Recognising a document

1. If the data is at least 128 bytes and its first 128 bytes are a MacBinary header naming file type `PNTG` (§1.2),
   it is a document.
2. Otherwise it must be at least 514 bytes, with a version of 0, 2 or 3 and the 204 padding bytes all zero.
3. The first row at +$200 must unpack to exactly 72 bytes: a run or literal block never straddles two rows.

MacPaint has no magic number, so a bare header is only a hint; the file type or the extension is the stronger
evidence. [ClassicMac]

### 2.2 Decoding

1. If the data is MacBinary-wrapped, take the data fork: the bytes from +$80, as many as the data fork length says
   (no more than the data holds).
2. Skip the 512-byte header.
3. Unpack PackBits rows back to back until 720 rows of 72 bytes are out or the data ends.
4. Draw each bit: 1 black, 0 white.

[Doc: TN #86]

## 3. Writing

None.

## 4. Variants

- Version 0 documents have no patterns; the 304 bytes are zero or unused. [Doc: TN #86]
- QuickTime's `PNTG` codec is the rows of §1.1 without the header, in a QuickTime image
  ([quicktime.md §2.14](quicktime.md#214-the-pntg-codec)).

## 5. ClassicMac

- `MacPaintFile.IsMacPaintFile` applies §2.1; `MacPaintFile.Decode` takes bytes or a stream (read from its position to
  its end and left open) and returns an opaque 576 × 720 `RgbaBitmap`. [ClassicMac]
- The patterns are not read. [ClassicMac]
- Rows missing from a truncated document stay white. A document of 512 bytes or fewer after any MacBinary header, or
  whose first row does not unpack, is refused with `NotSupportedException`. [ClassicMac]
- The ImageSharp adapter registers the format (`MacPaintFormat`: extensions `pntg`, `pnt`, `mac`; MIME
  `image/x-macpaint`) and reports 72 dpi; its detector looks at the first 592 bytes. The SkiaSharp adapter's
  `PictSkia.DecodeAny` tries the format after QTIF and PICT. [ClassicMac]

## 6. Diagnostics

None. `ClassicMac.Graphics` reports no diagnostics for MacPaint documents; it refuses unreadable ones with an
exception (§5).

## 7. Verification

Hand-built documents only; no document made by MacPaint is in the repository.

- `MacImageFileTests` (`tests/ClassicMac.Graphics.Tests`): a document decodes black on white; a MacBinary wrapper is
  skipped; the ImageSharp adapter loads a document and does not take PICT data for one.
- `StreamDecodingTests`: a document read from a stream matches the bytes; short documents fail alike from bytes and
  streams.
- The QuickTime `PNTG` codec has no test of its own.

## 8. Not covered

- The fill patterns of versions 2 and 3.
- Writing MacPaint documents.

## 9. References

1. Apple Computer, Macintosh Technical Note #86, *MacPaint Document Format*. Apple's documentation.
