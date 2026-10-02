# DART

DART images hold a floppy disk compressed in 40-sector blocks. Apple's Disk Archive/Retrieval Tool made them for
Apple's internal software distribution; Disk Copy 6 reads them, read-only. ClassicMac reads an image as one file whose
data fork is the disk, for the HFS or MFS reader ([hfs.md](../file-systems/hfs.md), [mfs.md](../file-systems/mfs.md))
to open next.

| | |
| --- | --- |
| Identified by | Types `'DMd1'`–`'DMd7'` and `'DMdf'`, creator `'DART'` [Code: Disk Copy 6.1.2, 6.3.3]; no signature: a plausible header whose blocks fill the data fork |
| ClassicMac | Reads; `ClassicMac.Files.Hfs.DartReader` |
| Verified against | DART 1.5.3's own files, "fast" (RLE) and "best" (LZH), with their source disks |
| Sources | Disk Copy 6.3.3 (disassembly of its DART reader); the codecs' sources in [dart-rle.md](../codecs/dart-rle.md) and [lzhuf.md](../codecs/lzhuf.md) |

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

---

## 1. Layout

### 1.1 Header

The data fork starts with the header [Code: Disk Copy 6.3.3], [Verified: DART 1.5.3]. It is `$54` bytes, or `$94` for
a 1440K disk.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$00` | 1 | Compression | `u8`: 0 = RLE ("fast"), 1 = LZH ("best"), 2 = none |
| `+$01` | 1 | Source type | `u8`: the source disk (table below) |
| `+$02` | 2 | Size | `u16`: disk size in KB, 400, 720, 800 or 1440 only |
| `+$04` | 2 × 40, or 2 × 72 for 1440K | Block lengths | `i16[]`: RLE in 16-bit words, LZH in bytes, −1 = stored |

The disk has size × 2 / 40 blocks (20, 36, 40 or 72); length slots past that count are ignored [Code: 6.3.3].

| File type | Source type | Size | Disk | Source |
| --- | --- | --- | --- | --- |
| `'DMd1'` | 1 | 400K | Mac 400K | [Code: 6.3.3] |
| `'DMd2'` | 2 | 400K | Lisa | [Code: 6.3.3] |
| `'DMd3'` | 1 | 800K | Mac 800K | [Code: 6.3.3], [Verified: DART 1.5.3] |
| `'DMd4'` | 3 | 800K | Apple II 800K | [Code: 6.3.3] |
| `'DMd5'` | `$11` | 720K | MS-DOS 720K | [Code: 6.3.3] |
| `'DMd6'` | `$10` | 1440K | Mac 1440K | [Code: 6.3.3] |
| `'DMd7'` | `$12` | 1440K | MS-DOS 1440K | [Code: 6.3.3] |
| `'DMdf'` | | | Listed in Disk Copy's type table; its meaning was not traced | [Code: 6.1.2, 6.3.3] |

### 1.2 Blocks

The blocks follow the header back to back, in disk order. Each decodes to **20,960 bytes: 20,480 data bytes (40
sectors) then 480 tag bytes (40 × 12)** [Code: 6.3.3], [Verified: DART 1.5.3]. Tag bytes are defined in
[diskcopy42.md §1.1](diskcopy42.md#11-the-data-fork).

| Block length | Compression | Stored bytes | Decoded by |
| --- | --- | --- | --- |
| −1, or any with compression 2 | none | 20,960 | — |
| *n* > 0 | 0 (RLE) | *n* × 2 | [dart-rle.md](../codecs/dart-rle.md) |
| *n* > 0 | 1 (LZH) | *n* | [lzhuf.md](../codecs/lzhuf.md); the window's tail carries from one block to the next ([lzhuf.md §2.3](../codecs/lzhuf.md#23-decoding-a-block)) |

### 1.3 Checksums

DART keeps two checksums in the resource fork, both the Disk Copy 4.2 sum
([diskcopy42.md §1.2](diskcopy42.md#12-the-checksum)) [Verified: DART 1.5.3, all four sample files]:

| Resource | Size | Covers |
| --- | --- | --- |
| `'CKSM'` 2 | 4 | All data bytes of the disk |
| `'CKSM'` 1 | 4 | **All** tag bytes, including sector 0's (a Disk Copy 4.2 header's tag checksum skips them) |

A `'DART'` 0 resource repeats both as text [Verified: DART 1.5.3].

## 2. Reading

Disk Copy 6.3.3 reads a DART image by building an NDIF chunk map for it in memory
([ndif.md §1](ndif.md#1-layout)): version 10, `+$48` = 41, one 40-sector chunk per block, typed `$81` (RLE), `$82`
(LZH) or `$02` (stored), the tags skipped [Code: Disk Copy 6.3.3]. It chooses the DART reader by file type
([raw-images.md §2](raw-images.md#2-reading)). The steps:

1. Read the header ([§1.1](#11-header)). A file type that disagrees with the header only draws a warning from Disk
   Copy [Code: 6.3.3].
2. Check the used block lengths: Disk Copy refuses the image (−8819) for a length over 20,960 (compared as stored, so
   in words for RLE) or a length of 0, and for blocks that run past the end of the data fork; a data fork longer than
   the blocks is accepted [Code: 6.3.3].
3. Decode the blocks in order ([§1.2](#12-blocks)), with one LZH decoder state for the whole file.
4. The disk is the 20,480 data bytes of every block, in order. Disk Copy never uses the tags [Code: 6.3.3].
5. Optionally check `'CKSM'` 2 against the data. Disk Copy checks only the data checksum, and only when verifying
   (its "Verify checksum" setting, or Verify Image); it never computes the tag checksum [Code: 6.3.3].

**A block may end one byte short.** DART's LZH encoder can leave the last token of a block unwritten; Disk Copy's
decoder stops when its input runs out, and the lost byte is the block's last tag byte, so the data are unaffected
[Code: 6.3.3], [Verified: block 28 of a DART 1.5.3 "best" file decodes to 20,959 bytes]. A reader fills the rest of a
short block with zeros.

## 3. Writing

None.

## 4. Variants

The compression byte selects RLE, LZH or none for the whole file; a block stored because it would not shrink has
length −1 ([§1.2](#12-blocks)). The source type and file type name the floppy ([§1.1](#11-header)). No other versions
are known.

## 5. ClassicMac

- **Recognition** [ClassicMac], built on Disk Copy's checks in [§2](#2-reading): compression 0–2, a size of 400, 720, 800 or 1440, every
  used length −1 or 1–20,960 when the file is compressed (compression 2 ignores the lengths), and the blocks ending
  exactly at the end of the data fork. When the Finder info shows creator `'DART'` or a type starting `'DMd'`, the
  blocks may also end before the fork does. The source type byte and the file type are otherwise ignored. Where this
  reader comes in the unwrapper's order is in [unwrapping.md §2.1](../containers/unwrapping.md#21-reader-order).
- **Output**: one file named after the host file (else the file's own name), whose data fork is the disk; tags are
  dropped. The whole data fork is read into memory, within the expanded-bytes limit
  ([unwrapping.md §5](../containers/unwrapping.md#5-classicmac)).
- **Damaged blocks**: an RLE block that does not decode to 20,960 bytes, or an LZH block that gives fewer than its
  20,480 data bytes, is reported as `dart.bad-block`; the rest of the block reads as zeros. An LZH block short only in
  its tags is not reported.
- **Checksums**: `'CKSM'` 2 and 1 are checked whenever the resource fork is present and readable and the resource
  holds at least 4 bytes; a mismatch is a warning and the disk is read anyway. A resource fork that does not parse is
  ignored.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `dart.bad-block` | Error | An RLE block does not decode to 20,960 bytes, or an LZH block to its 20,480 data bytes | The rest of the block reads as zeros | −8819 or −50 |
| `dart.checksum` | Warning | `'CKSM'` 2 or 1 does not match | Reads the disk | Data: INVALID alert when verifying; tags: never checked |

The codecs have no codes of their own: their failures surface as `dart.bad-block`.

## 7. Verification

- `tests/ClassicMac.Files.Tests/DartTests.cs`, `DART_images_read_as_their_disk`: synthetic 800K images (stored, and
  RLE as literal runs) unwrap through the DART reader to the HFS volume and its file. `Other_data_is_not_DART`: an
  impossible size is refused.
- `DART_153_files_decode_to_their_source_disks`: with `CLASSICMAC_CORPUS` set, DART 1.5.3's own "fast" and "best"
  files (each split into `.data`, `.rsrc` and `.type`, with a `-source.img` beside them; CiderPress2's test data,
  Apache-2.0, not committed) decode to their source disks with no diagnostics, so the block layout, both codecs, the
  short last block and both `'CKSM'` checksums are proved.

## 8. Not covered

- Writing DART images.
- The meaning of type `'DMdf'`.
- Which of −8819 and −50 Disk Copy gives for each kind of damaged block.

## 9. References

1. Disk Copy 6.1.2 and 6.3.3, Apple, traced in disassembly.
2. DART 1.5.3, Apple: the sample files.
3. CiderPress2 (Andy McFadden), Apache-2.0: the source of the DART 1.5.3 sample set; no code used.
