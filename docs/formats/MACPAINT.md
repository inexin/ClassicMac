# MacPaint documents

The MacPaint document format, which ClassicMac reads in `ClassicMac.Graphics` (`MacPaintFile`); the same rows are
QuickTime's `PNTG` codec ([QUICKTIME.md](QUICKTIME.md)).

MacPaint documents (`.pntg`, `.pnt`, `.mac`; file type `PNTG`) are 576 × 720 at 1 bit per pixel, with 1 = black:

| Offset | Size | Field |
|---|---|---|
| 0 | u32 | version: 0, 2 or 3 |
| 4 | 304 | 38 fill patterns of 8 bytes (versions 2 and 3) |
| 308 | 204 | padding (zeros) |
| 512 | — | 720 rows of 72 bytes, each PackBits-compressed on its own |

- The same rows, without the 512-byte header, are the data of the QuickTime `PNTG` codec ([QUICKTIME.md](QUICKTIME.md) §2).
- A **MacBinary** wrapper may precede the file:
  - a 128-byte header: byte 0 is 0, a 1–63 character name starts at byte 1, and the file type `PNTG` is at byte 65;
  - the data fork's length is at byte 83;
  - the data fork starts at byte 128.
- MacPaint has no magic number. For detection, check the version, the zero padding, and that the first row unpacks
  to exactly 72 bytes; the file type or extension is a stronger hint.
