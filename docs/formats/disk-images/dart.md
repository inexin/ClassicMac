# DART

Apple's Disk Archive/Retrieval Tool compressed floppies for Apple's internal software distribution. Disk Copy 6 reads
DART images (read-only) by mapping them onto an NDIF chunk map in memory: a version 10 map with `+$48` = 41, one
40-sector chunk per block, typed `$81` (RLE), `$82` (LZH) or `$02` (stored), the tags skipped [Code: Disk Copy 6.3.3].

Contents

1. [Header](#1-header)
2. [Blocks](#2-blocks)
3. [Recognition](#3-recognition)
4. [Checksums](#4-checksums)
5. [Diagnostics](#5-diagnostics)

---

## 1. Header

The data fork starts with the header [Code: Disk Copy 6.3.3], [Verified: DART 1.5.3].

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 1 | `u8` | Compression: 0 = RLE ("fast"), 1 = LZH ("best"), 2 = none |
| `+$01` | 1 | `u8` | Source disk type (table below) |
| `+$02` | 2 | `u16` | Disk size in KB: 400, 720, 800 or 1440 only |
| `+$04` | 2 × 40, or 2 × 72 for 1440K | `i16[]` | Block lengths: RLE in 16-bit words, LZH in bytes, −1 = stored |

The header is `$54` bytes, or `$94` for 1440K. The disk has size × 2 / 40 blocks (20, 36, 40 or 72); length slots
past that count are ignored [Code: 6.3.3].

| File type | Source type | Size | Disk | Source |
| --- | --- | --- | --- | --- |
| `DMd1` | 1 | 400K | Mac 400K | [Code: 6.3.3] |
| `DMd2` | 2 | 400K | Lisa | [Code: 6.3.3] |
| `DMd3` | 1 | 800K | Mac 800K | [Code: 6.3.3], [Verified: DART 1.5.3] |
| `DMd4` | 3 | 800K | Apple II 800K | [Code: 6.3.3] |
| `DMd5` | `$11` | 720K | MS-DOS 720K | [Code: 6.3.3] |
| `DMd6` | `$10` | 1440K | Mac 1440K | [Code: 6.3.3] |
| `DMd7` | `$12` | 1440K | MS-DOS 1440K | [Code: 6.3.3] |

A file type that disagrees with the header only draws a warning from Disk Copy [Code: 6.3.3]. ClassicMac ignores the
type and the source type byte.

---

## 2. Blocks

The blocks follow the header back to back, in disk order. Each decodes to **20,960 bytes: 20,480 data bytes (40
sectors) then 480 tag bytes (40 × 12)** [Code: 6.3.3], [Verified: DART 1.5.3].

- Length −1, or compression 2: the block is stored, 20,960 bytes.
- RLE: the block takes length × 2 bytes and is decoded as in [dart-rle.md](../codecs/dart-rle.md).
- LZH: the block takes length bytes and is decoded as in [lzhuf.md](../codecs/lzhuf.md). The LZH window's tail carries
  from one block to the next, so blocks are decoded in order with one decoder state.

Disk Copy refuses (−8819) a block length over 20,960 (compared as stored, so in words for RLE), a length of 0, and
blocks that run past the end of the data fork [Code: 6.3.3]; a data fork longer than the blocks is accepted. The
output is the data bytes of every block in order; tags are never used by Disk Copy [Code: 6.3.3], and ClassicMac
drops them too.

**A block may end one byte short.** DART's LZH encoder can leave the last token of a block unwritten; Disk Copy's
decoder stops when its input runs out, and the lost byte is the block's last tag byte, so the data are unaffected
[Code: 6.3.3], [Verified: block 28 of a DART 1.5.3 "best" file decodes to 20,959 bytes]. A reader fills the rest of
a short block with zeros.

---

## 3. Recognition

ClassicMac takes a data fork as DART when compression is 0–2, the size is one of the four, every used length is −1 or
1–20,960, and the blocks end exactly at the end of the data fork. With Finder info showing creator `DART` or a
`DMd…` type, the blocks may also end before the fork does [Fitted, on Disk Copy's rule above].

---

## 4. Checksums

DART keeps two checksums in `CKSM` resources in the resource fork, both computed with the Disk Copy 4.2 sum
([diskcopy42.md §2](diskcopy42.md#2-checksums)) [Verified: DART 1.5.3, all four sample files]:

| Resource | Covers |
| --- | --- |
| `CKSM` 2 | All data bytes of the disk |
| `CKSM` 1 | **All** tag bytes, including sector 0's (unlike a Disk Copy 4.2 header's tag checksum) |

A `DART` 0 resource repeats both as text. Disk Copy never computes the tag checksum and checks the data checksum only
when verifying (its "Verify checksum" setting, or Verify Image) [Code: 6.3.3]. ClassicMac checks both whenever the
resource fork is present and reports a mismatch as a warning.

---

## 5. Diagnostics

Every problem is reported with a code and a severity ([README.md](../README.md#diagnostics)). The codecs emit none of
their own: their failures surface as `dart.bad-block`, `ndif.bad-chunk` or `udif.bad-run`. There are no `adc.`,
`kencode.` or `bzip2.` codes.

| Code | Severity | Meaning | ClassicMac | Disk Copy |
| --- | --- | --- | --- | --- |
| `dart.bad-block` | Error | An RLE block does not decode to 20,960 bytes, or an LZH block to its 20,480 data bytes | the rest of the block reads as zeros | −8819 or −50 |
| `dart.checksum` | Warning | `CKSM` 2 or 1 does not match | reads the disk | data: INVALID alert when verifying; tags: never checked |
