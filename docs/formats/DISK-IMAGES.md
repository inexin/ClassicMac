# Disk images — an implementer's specification

This document describes the classic Mac OS disk image formats that wrap a floppy or volume — Disk Copy 4.2, DART,
NDIF (Disk Copy 6, including self-mounting and segmented images), ShrinkWrap's outputs and the early UDIF `.dmg` —
and the codecs inside them (ADC, KenCode, DART RLE and DART LZH), completely enough to write a reader and each
decoder without reading ClassicMac's code. Every reader here yields the disk's sectors as one volume, which the HFS or
MFS reader ([HFS-MFS.md](HFS-MFS.md)) or the partition-map reader opens next.

**References.** Apple published a specification only for Disk Copy 4.2 (File Type Note $E0/$0005). NDIF, DART and
early UDIF have none: the rules below come from the disassembly of Disk Copy 6.1.2, 6.3.3 and 6.5b13 (the `.HDI`
block driver, its `bcem` validator and version converter, the `hdi1`/`hdi2` codec plug-ins, the DART reader), checked
on images the real software made in SheepShaver (Mac OS 9.0), each decoded back to its source sectors with the stored
checksum matching. DART files were checked on DART 1.5.3's own output (CiderPress2's test data). LZHUF is Okumura and
Yoshizaki's public-domain program. dmg2img, libdmg-hfsplus and Aaru (GPL/LGPL) and VileFault were behavioural
references only; what they alone say is marked as such.

Contents

1. [Conventions](#1-conventions)
2. [Recognising an image](#2-recognising-an-image)
3. [Disk Copy 4.2](#3-disk-copy-42)
4. [DART](#4-dart)
5. [NDIF (Disk Copy 6)](#5-ndif-disk-copy-6)
6. [ShrinkWrap 2.1 and other raw images](#6-shrinkwrap-21-and-other-raw-images)
7. [ADC](#7-adc)
8. [KenCode](#8-kencode)
9. [DART RLE](#9-dart-rle)
10. [DART LZH (LZHUF)](#10-dart-lzh-lzhuf)
11. [UDIF (not built in ClassicMac yet)](#11-udif-not-built-in-classicmac-yet)
12. [Diagnostics](#12-diagnostics)
13. [Open questions](#13-open-questions)

---

## 1. Conventions

The shared conventions and source tags of [README.md](README.md) apply: big-endian values, offsets in hex, sizes in
decimal, 512-byte sectors (also called blocks here), and one tag per rule — **[Doc]**, **[Code]** (with the software
and version), **[Verified]**, **[Author]**, **[Fitted]**. **[Fitted?]** marks a rule that is inferred or not yet
settled, listed again in [§13](#13-open-questions).

Also in this document:

- A **chunk** is a run of whole sectors stored one way (zeros, raw, or compressed) in an NDIF image; a **block** of a
  DART image is 40 sectors plus their tags.
- **Tag bytes** are the 12 bytes of file-system metadata a Lisa or early Mac floppy kept beside each 512-byte sector.
  Mac OS never uses them; images keep them only for exact copies.
- `start<<8|type` is a `u32` whose high 24 bits are a sector number and whose low 8 bits are a type code.
- "Disk Copy refuses" means the mount fails with the error named; ClassicMac's own behaviour is given with it.

## 2. Recognising an image

Disk Copy picks the format **by file type (and creator) only**: its driver never looks at the data to decide
[Code: Disk Copy 6.1.2 `typ#` 128, 6.3.3's type table]. An unknown type is mounted as a raw volume if its data fork is
a nonzero multiple of 512 bytes, and is otherwise refused with −8816 [Code: Disk Copy 6.3.3].

| Type | Creator | Format | Source |
| --- | --- | --- | --- |
| `dImg` | `dCpy` (Disk Copy), `Wrap` (ShrinkWrap) | Disk Copy 4.2 | [Code: 6.3.3], [Verified] |
| `DMd1`–`DMd7`, `DMdf` | `DART` | DART | [Code: 6.1.2, 6.3.3] |
| `dimg` | `ddsk` | NDIF, read/write | [Code: 6.3.3], [Verified] |
| `rohd` | `ddsk` | NDIF, read-only and read-only compressed (Disk Copy 6.1 on) | [Verified] |
| `hdro` | `ddsk` | NDIF, the older read-only type (Disk Image Mounter era) | [Code: 6.1.2 `kind` 128]; era [Fitted?] |
| `hdc `, `hdcm` | | NDIF, DiskSet | [Code: 6.1.2] |
| `dseg` | `ddsk` | a later part of a segmented NDIF image | [Code: 6.3.3], [Verified] |
| `APPL` | `oneb` | Disk Copy self-mounting image (`.smi`), NDIF | [Code: 6.3.3], [Verified] |
| `APPL`, `adrp` | `sImg` | ShrinkWrap self-mounting floppy, raw | [Code: 6.1.2], [Verified] |
| `APPL` | `iImg` | ShrinkWrap self-mounting volume, raw | [Verified] |
| `hdrv`, `DDim` | any | raw volume | [Code: 6.1.2], [Verified] |
| `devi`, `devs` | `ddsk` | UDIF device image, part of one | [Code: 6.5b13 `kind` 128], [Verified] |
| `devr` | `ddsk` | raw device image (UDIF read/write) | [Code: 6.5b13], [Verified] |
| `GImg`, `PImg` | | Toast and other device images | [Code: 6.5b13] |

ClassicMac does not rely on types, because images copied through other systems lose them. After the partition-map
reader it tries, in order: a Disk Copy 4.2 header ([§3.3](#33-recognition)), an NDIF map in the resource fork
(any `bcem` 128 of at least `$58` bytes), a DART header ([§4.1](#41-header)), then the volume readers on the data
fork itself, which catch every raw image. Early UDIF and encrypted images are not read yet
([§11](#11-udif-not-built-in-classicmac-yet)).

## 3. Disk Copy 4.2

### 3.1 Layout

The data fork is an 84-byte header, the disk's sectors, then 12 tag bytes per sector [Doc: File Type Note $E0/$0005].
The resource fork, if any, is not needed to read the disk.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 64 | `Str63` | Disk name. Bytes past the name's length are junk: ShrinkWrap 2.1's "DiskCopy Image" option leaves nonzero bytes there [Verified] |
| `+$40` | 4 | `u32` | Data size: the disk's sector bytes [Doc] |
| `+$44` | 4 | `u32` | Tag size: 12 per sector, or 0 [Doc] |
| `+$48` | 4 | `u32` | Data checksum ([§3.2](#32-checksums)) [Doc] |
| `+$4C` | 4 | `u32` | Tag checksum ([§3.2](#32-checksums)) [Doc] |
| `+$50` | 1 | `u8` | Disk format: 0 = 400K GCR, 1 = 800K GCR, 2 = 720K MFM, 3 = 1440K MFM [Doc] |
| `+$51` | 1 | `u8` | Format byte: `$12` = 400K, `$22` = larger Mac disk, `$24` = Apple II 800K [Doc]. Disk Copy 6.1.2 and ShrinkWrap 2.1 wrote `01 22` for 800K [Verified] |
| `+$52` | 2 | `u16` | `$0100`, required [Doc] |
| `+$54` | data size | | The sectors, in order |
| … | tag size | | The tag bytes, 12 per sector, in sector order |

ClassicMac reads neither `+$50` nor `+$51`: the volume reader finds the file system [Fitted].

### 3.2 Checksums

Both checksums use the same sum: start at 0; for each big-endian 16-bit word, add it (modulo 2³²), then rotate the
32-bit sum right by one bit [Doc], [Verified: Disk Copy 6.1.2's and ShrinkWrap 2.1's images]. A trailing odd byte is
ignored.

- The **data checksum** covers all data bytes [Doc], [Verified].
- The **tag checksum skips the first 12 tag bytes** (sector 0's tags) and covers the rest [Fitted: matched on the
  Disk Copy 4.2 image in DART 1.5.3's sample set, whose header held the sum of the tags from byte 12; not traced in
  Disk Copy's code]. DART's own tag checksum does not skip them ([§4.4](#44-checksums)).

ClassicMac checks both when the image is complete and reports a mismatch as a warning; the disk is read anyway. What
Disk Copy does with a wrong Disk Copy 4.2 checksum was not traced.

### 3.3 Recognition

ClassicMac takes a data fork as Disk Copy 4.2 when it is at least 84 bytes, `+$52` is `$0100` [Doc], the name length
is at most 63, the data size is a nonzero multiple of 512, and the tag size is 0 or 12 per sector [Fitted].

- A data fork shorter than 84 + data size keeps its whole sectors, is reported (`diskcopy.truncated`) and is not
  checksummed.
- Tags cut short are dropped and reported (`diskcopy.tags-truncated`); the data checksum is still checked.

Disk Copy 6.3.3 writes Disk Copy 4.2 images only for floppy sizes (the choice is greyed out for a 5 MB volume)
[Verified], typed `dImg`/`dCpy`, with a `vers` resource giving both checksums [Code: 6.3.3], [Verified].

## 4. DART

Apple's Disk Archive/Retrieval Tool compressed floppies for Apple's internal software distribution. Disk Copy 6 reads
DART images (read-only) by mapping them onto an NDIF chunk map in memory: a version 10 map with `+$48` = 41, one
40-sector chunk per block, typed `$81` (RLE), `$82` (LZH) or `$02` (stored), the tags skipped [Code: Disk Copy 6.3.3].

### 4.1 Header

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

### 4.2 Blocks

The blocks follow the header back to back, in disk order. Each decodes to **20,960 bytes: 20,480 data bytes (40
sectors) then 480 tag bytes (40 × 12)** [Code: 6.3.3], [Verified: DART 1.5.3].

- Length −1, or compression 2: the block is stored, 20,960 bytes.
- RLE: the block takes length × 2 bytes and is decoded as in [§9](#9-dart-rle).
- LZH: the block takes length bytes and is decoded as in [§10](#10-dart-lzh-lzhuf). The LZH window's tail carries
  from one block to the next, so blocks are decoded in order with one decoder state.

Disk Copy refuses (−8819) a block length over 20,960 (compared as stored, so in words for RLE), a length of 0, and
blocks that run past the end of the data fork [Code: 6.3.3]; a data fork longer than the blocks is accepted. The
output is the data bytes of every block in order; tags are never used by Disk Copy [Code: 6.3.3], and ClassicMac
drops them too.

**A block may end one byte short.** DART's LZH encoder can leave the last token of a block unwritten; Disk Copy's
decoder stops when its input runs out, and the lost byte is the block's last tag byte, so the data are unaffected
[Code: 6.3.3], [Verified: block 28 of a DART 1.5.3 "best" file decodes to 20,959 bytes]. A reader fills the rest of
a short block with zeros.

### 4.3 Recognition

ClassicMac takes a data fork as DART when compression is 0–2, the size is one of the four, every used length is −1 or
1–20,960, and the blocks end exactly at the end of the data fork. With Finder info showing creator `DART` or a
`DMd…` type, the blocks may also end before the fork does [Fitted, on Disk Copy's rule above].

### 4.4 Checksums

DART keeps two checksums in `CKSM` resources in the resource fork, both computed with the Disk Copy 4.2 sum
([§3.2](#32-checksums)) [Verified: DART 1.5.3, all four sample files]:

| Resource | Covers |
| --- | --- |
| `CKSM` 2 | All data bytes of the disk |
| `CKSM` 1 | **All** tag bytes, including sector 0's (unlike a Disk Copy 4.2 header's tag checksum) |

A `DART` 0 resource repeats both as text. Disk Copy never computes the tag checksum and checks the data checksum only
when verifying (its "Verify checksum" setting, or Verify Image) [Code: 6.3.3]. ClassicMac checks both whenever the
resource fork is present and reports a mismatch as a warning.

## 5. NDIF (Disk Copy 6)

The New Disk Image Format stores the disk in the data fork as chunks and maps them in a `bcem` 128 resource. It was
written by Disk Copy 6.0.1 through 6.5 (and read by Disk Image Mounter), in read/write, read-only, read-only
compressed, self-mounting and segmented forms.

### 5.1 Files and resources

| Resource | Meaning |
| --- | --- |
| `bcem` 128 | The chunk map ([§5.2](#52-the-bcem-header), [§5.3](#53-chunk-entries)); its name is the volume name [Verified] |
| `bcm#` 128 | A segmented image's part record ([§5.8](#58-segmented-images)) |
| `vers` 1 | Text: the file system, the size and the checksum ("CRC: $…"; Disk Copy 6.5b13 writes "CRC28: $…") [Verified] |
| `STR ` −16396 | The writer: "Disk Copy" (6.3.3), "Disk Copy 6.1" (6.1.2), none from 6.5b13 [Verified] |

Disk Copy's driver reads back only `bcem` and `bcm#` [Code: 6.3.3]. `cSum`, `plst`, `size` and similar resources are
not used by Disk Copy 6.3.3 [Code: 6.3.3]. Without the resource fork the data fork cannot be decoded.

### 5.2 The `bcem` header

The map is a 128-byte header then 12-byte entries (version 2 differs: [§5.7](#57-version-2)). Field meanings are
from Disk Copy 6.3.3's field-name strings and validator [Code: 6.3.3], values from real images [Verified].

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 2 | `u16` | Version: 10, 11 or 12 (2 is the old layout) |
| `+$02` | 2 | `u16` | File-system ID; 0 in every image seen; `$FFFF` draws a warning |
| `+$04` | 64 | `Str63` | Volume name |
| `+$44` | 4 | `u32` | Disk size in sectors |
| `+$48` | 4 | `u32` | Buffer size in sectors: the largest compressed chunk plus room for the codec's overrun ([§5.10](#510-what-disk-copy-writes)); 0 when nothing is compressed |
| `+$4C` | 4 | `u32` | Data start: added to every entry's offset (0 in every image seen) |
| `+$50` | 4 | `u32` | CRC-32 of the disk ([§5.6](#56-the-checksum-crc28)); 0 = none |
| `+$54` | 4 | `u32` | Segmented flag: nonzero in part 1 of a segmented image, which needs version 12 |
| `+$58` | 4 | `u32` | Performance field, not checked |
| `+$5C` | 4 | `u32` | Performance field, not checked |
| `+$60` | 28 | `u32[7]` | Reserved. Aaru calls `+$74` an encryption flag and `+$78` a password hash (ShrinkWrap 3?); never seen [Fitted?] |
| `+$7C` | 4 | `u32` | Entry count, the end entry included |
| `+$80` | 12 × count | | The entries |

Versions and their writers:

| Version | Written by | Source |
| --- | --- | --- |
| 2 | Disk Image Mounter 1.0.1 and Disk Copy 6.0.x (1995–96); no image seen | [Fitted?] |
| 10 | Disk Copy 6.1–6.3.3: read/write, read-only, KenCode | [Verified: 6.1.2, 6.3.3] |
| 11 | Disk Copy 6.1–6.3.3: ADC (required for chunk type `$83`) | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3] |
| 12 | Disk Copy 6.3.3 for segmented images; Disk Copy 6.5b13 for all images | [Code: 6.3.3], [Verified: 6.3.3; 6.5b13 read-only compressed] |

Disk Copy 6.3.3 accepts 10, 11 and 12, and 2 with an "obsolete" warning; it refuses a version above 12 with −8818
("cannot be used with the currently installed version of the disk image driver") and any other with −8819 (damaged)
[Code: 6.3.3]. Disk Copy 6.1.2 accepts 2, 10 and 11, and refuses above 11 with −9 (too new) and others with −10
(damaged) [Code: 6.1.2]. ClassicMac accepts 2, 10, 11 and 12 and refuses the rest.

### 5.3 Chunk entries

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$0` | 4 | `start<<8\|type` | First sector of the chunk, and its type ([§5.4](#54-chunk-types)) |
| `+$4` | 4 | `u32` | Offset of the stored bytes in the data fork, from the data start (`+$4C`) |
| `+$8` | 4 | `u32` | Stored length in bytes |

[Code: 6.3.3], [Verified]

- A chunk covers the sectors from its start to the next entry's start: **its decoded size is (next start − start) ×
  512** [Code: 6.3.3]. The chunk size is not stored anywhere else; it is the user's choice
  ([§5.10](#510-what-disk-copy-writes)).
- The last entry has type `$FF` and start = the disk size. Disk Copy 6.3.3 gives it the data's end as offset and 0
  as length; Disk Copy 6.5b13 gives offset 0 [Verified]. Readers ignore both.
- Zero chunks store nothing: offset and length 0 [Verified: 6.3.3].
- Stored bytes follow one another in entry order [Verified], but a reader uses the offsets.

### 5.4 Chunk types

| Type | Meaning | Source |
| --- | --- | --- |
| `$00` | Zeros; nothing is read | [Code: 6.3.3], [Verified] |
| `$02` | Raw: the sectors as they are | [Code: 6.3.3], [Verified] |
| `$80` | KenCode ([§8](#8-kencode)), Disk Copy's "Smaller (KC)" | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3] |
| `$81` | DART RLE ([§9](#9-dart-rle)) | [Code: 6.3.3] |
| `$82` | DART LZH ([§10](#10-dart-lzh-lzhuf)) | [Code: 6.3.3] |
| `$83` | ADC ([§7](#7-adc)), "Faster (ADC)"; map version 11 or later | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3, 6.5b13] |
| `$F0` | Unknown; Aaru names it ShrinkWrap 3's compression. Never seen | behavioural reference only |
| `$FF` | End of the map | [Code: 6.3.3], [Verified] |

- Disk Copy 6.3.3 knows no other type: any other value (`$F0` included) is refused with −8820 ("format not
  recognized"), both when mounting and when read [Code: 6.3.3]. ClassicMac reports it and reads the chunk as zeros.
- Types `$81` and `$82` exist for the in-memory DART mapping ([§4](#4-dart)); no NDIF file with them has been seen.
- Codecs are plug-ins, `hdi1` (68k) and `hdi2` (PowerPC) resources 128–131 for types `$80`–`$83`; the PowerPC set is
  used when the machine has one [Code: 6.3.3]. No codec keeps state from one chunk to the next, except LZH's window
  tail, which Disk Copy's driver carries over from the chunk it decoded last [Code: 6.3.3].
- A compressed image stores any chunk that would not shrink as raw (`$02`), so types mix within one image
  [Verified: 6.1.2, 6.3.3].

### 5.5 Validation

When mounting, Disk Copy 6.3.3's validator checks the map; errors refuse the mount with −8819 unless stated, warnings
are only logged [Code: 6.3.3]:

| Check | Disk Copy 6.3.3 | ClassicMac |
| --- | --- | --- |
| Version ([§5.2](#52-the-bcem-header)) | −8818 / −8819 | refuses |
| Name length over 63 | error | reports, cuts to 63 |
| Name empty, or over 27 bytes | warning | — |
| Disk size ≥ `$400000` sectors | error | refuses (also 0) |
| Data start past the data fork | error | reports |
| CRC `$FFFFFFFF` ("uninitialized") | warning | reports |
| CRC 0 in a read-only image | warning | — |
| Segmented flag with version below 12 | error | refuses |
| Reserved longs (only `+$64` is tested, a bug; the outcome was not traced) | tested | — |
| Count below 2 | error | refuses |
| Map size ≠ `$80` + 12 × count | error; warning ("created with Disk Copy 6.0") when version 10 has exactly one entry more than its count | reports (error; warning for version 10 one off) |
| A type not listed in [§5.4](#54-chunk-types) | −8820 | reports, zeros |
| `$83` in a map below version 11 | error | reports |
| Zero chunk with a nonzero length | warning | reports |
| Raw chunk storing fewer than size bytes | error (more is a warning) | reports, zero-fills |
| Compressed chunk covering more than `+$48` sectors (as stored, no doubling; equal passes) | error, in every version [Code] (the stored length is not checked) | reports as an error, decodes the chunk |
| Starts not increasing, or past the disk | error | reports, reads the map up to that entry |
| Chunk data past the end of the data fork | error | reports, reads what is there |
| Last start ≠ disk size | error | reports |
| No `$FF` entry | warning | reports; the last chunk runs to the disk's end |
| First chunk not starting at sector 0 | not checked (those sectors read garbage) | reports; they read as zeros |
| Read/write mount with zero or compressed chunks | error | (read-only) |

Disk Copy 6.1.2 makes the same checks for versions 2, 10 and 11, except the reserved and segment fields, and returns
−10 for every error, an unknown type included [Code: 6.1.2].

The `+$48` check applies to types `$80`–`$83`, with `+$48` as stored (not doubled for version 10). Disk Copy 6.3.3
logs it as "compressed block count exceeds max chunk block count"; 6.5b13 refuses it both in its driver and in its
Check Image command (−8819) [Code: 6.1.2, 6.3.3, 6.5b13]. No version checks a chunk's stored length; a chunk that
decodes to the wrong size fails only when read (the codec's error, −8819 or −10) [Code: 6.3.3, 6.5b13, 6.1.2].
Disk Copy 6.5b13 turns the NDIF map into UDIF runs in memory: `+$48` becomes the buffers-needed value (doubled for
version 10) and types `$80`–`$83` become `$80000001`–`$80000004` [Code: 6.5b13].

### 5.6 The checksum (CRC28)

`+$50` holds a CRC-32 of the whole disk [Code: 6.3.3], [Verified: every sample]:

- **Table:** for each byte value `i`, `c = i`, then eight times `c = (c & 1) ? (c >> 1) ^ $04C11DB7 : c >> 1`. The
  *normal* polynomial is used in a right-shifting loop, so this is **not** zlib's CRC-32.
- **Update:** `crc = table[(crc ^ byte) & $FF] ^ (crc >> 8)`, starting from `$FFFFFFFF`, **with no final xor**.
- **Coverage:** every sector of the disk in order (disk size × 512 bytes), zero chunks as zeros — so the read-only and
  compressed images of one volume carry the same value.
- **Test values:** 819,200 zero bytes give `$0321EDEE` [Verified: reported by Disk Copy 6.3.3]; the ASCII string
  `123456789` gives `$03B0D416` (computed from the rule).
- Apple later called it "CRC28": Disk Copy 6.5b13's `vers` text says so [Verified: 6.5b13], as does Mac OS X's
  `hdiutil`.
- 0 means none: read/write images store 0, and Disk Copy's driver clears the field and rewrites the map when it
  unmounts one [Code: 6.3.3], [Verified].

**It is checked only on request.** The driver never checks it. Disk Copy checks it when its "Verify checksum" setting
is on: a mismatch stops the mount with an alert (result 130, "The checksum of … is INVALID") [Code: 6.3.3],
[Verified: 6.3.3]. With the setting off, a wrong CRC mounts, and damaged compressed data are served as they decode,
without an error [Verified: 6.3.3]. ClassicMac checks it only when asked (`VerifyChecksums`, the CLI's `--verify`),
reporting `ndif.bad-checksum`.

### 5.7 Version 2

The old map layout, as **Disk Copy 6.1.2's driver** reads it [Code: 6.1.2], [Verified: 6.1.2 mounted hand-built
version 2 images, raw and KenCode, with the checksum valid, and refused ADC in them with −10]:

- The header is the version 10 header up to `+$54` (name, disk size, `+$48`, data start, CRC).
- `+$54` is the entry count, **the end entry included** — not a segmented flag.
- The entries are **8 bytes**, `start<<8|type` then offset, from `+$58`; the map is exactly `$58` + 8 × count bytes
  (a longer map is a warning, a shorter one or another count an error).
- A chunk's stored length is the next entry's offset minus its own; the end entry's length is 0. The stored bytes are
  therefore contiguous in entry order, and a zero chunk's offset is the next chunk's.
- Types `$00`, `$02`, `$80`–`$82` and `$FF`; `$83` is refused; segmented images are not allowed. `+$48` is the largest
  compressed chunk in sectors.
- 6.1.2 converts the map to version 11 in memory and continues as for version 11.

The later readers are broken for version 2, so they are not the reference [Code], [Verified]:

- **Disk Copy 6.3.3** converts to version 12 but then clears ten longs from `+$60`, wiping the count it stored at
  `+$7C`: the table comes out empty and the disk reads as zeros (with "Verify checksum" on, it reports the CRC of
  zeros, `$0321EDEE`). 6.1.2's converter clears from `+$54` instead, and the count survives.
- **Disk Copy 6.5b13** computes the end entry's length from the long past the table and refuses the map (−8819,
  "chunk ends beyond end of data fork").
- **ShrinkWrap 2.1** ignores the version and always reads the count at `+$7C` and 12-byte entries at `+$80`, typed
  `dimg`/`hdro` with creator `ddsk`, reading the data fork sequentially and decoding `$80` through the System's
  `dcmp` 3. That contradicts the layout above; which one Disk Image Mounter's images really use is open [Fitted?].

ClassicMac reads version 2 as 6.1.2 does and reports `ndif.version-2` (Info) with the file's type and creator, the
long at `+$54` and the map size, asking for the image, because no real version 2 image has been seen.

### 5.8 Segmented images

Disk Copy 6.3.3 splits an image into parts (AppleScript only, at most 128 parts) [Code: 6.3.3],
[Verified: 6.3.3]:

- The data fork of the whole image is cut raw into parts of ceil(sectors / parts) sectors; **the map's offsets run
  across the parts' data forks placed back to back** [Verified].
- **Only part 1 has a `bcem`**: version 12, `+$54` = 1. Part 1 keeps its type; the others are typed `dseg`; all keep
  the creator [Verified].
- Every part has a `bcm#` 128 [Code: 6.3.3], [Verified]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 2 | `u16` | Part number, from 1 |
| `+$02` | 2 | `u16` | Part count |
| `+$04` | 16 | | Image ID, the same in every part (made of the date, the tick count, a random number and a CRC-32) |
| `+$14` | 4 | `u32` | CRC-32 ([§5.6](#56-the-checksum-crc28)) of this part's data fork; 0 if the source had no CRC. Never verified |

- Parts are named "*name* *N*of*M*", *N* zero-padded to the width of *M*, at most 31 characters; later parts carry a
  `vers` 1 "Part *n*/*M* of a disk image" [Verified].
- **Disk Copy finds parts by `bcm#`, never by name**: it looks in part 1's folder for files typed `dseg` whose
  `bcm#` has the same image ID and count, and takes each number once. Part 1 may be `dimg`, `rohd` or `APPL`/`oneb`.
  Renamed or reordered parts mount [Code: 6.3.3], [Verified: 6.3.3].
- A missing part, or one from another image, refuses the mount with −8821 ("Not all parts of disk image … could be
  found. Part *n* is missing or corrupted.") [Verified: 6.3.3]. A part other than the last whose length is not a
  multiple of 512 gives −39 [Code: 6.3.3].

ClassicMac does the same with the files in the image's folder, reporting a missing part (`ndif.missing-segment`) and
reading the disk up to it.

### 5.9 Reading the disk

For each sector: find the chunk that covers it, decode the whole chunk to (next start − start) × 512 bytes, and take
the sector. Disk Copy's driver reads a compressed chunk into the end of its buffer, decodes it in place, and keeps
the last decoded chunk [Code: 6.3.3]; its buffer is `+$48` × 512 bytes, doubled for version 10 (KenCode's workspace)
[Code: 6.1.2, 6.3.3]. A read that reaches the `$FF` entry fails with −39 [Code: 6.3.3].

A self-mounting image (`.smi`, `APPL`/`oneb`) is an ordinary NDIF image whose resource fork adds the mounting
application; the data fork is unchanged [Verified: 6.3.3].

### 5.10 What Disk Copy writes

This is not needed to read images, but explains what readers meet [Verified: 6.1.2, 6.3.3, 6.5b13]:

| Format | Type/creator | Version | Chunks | `+$48` | CRC |
| --- | --- | --- | --- | --- | --- |
| Read/Write | `dimg`/`ddsk` | 10 | one raw chunk | 0 | 0 |
| Read-Only | `rohd`/`ddsk` | 10 (12 from 6.5b13) | a raw chunk per used area, zero chunks for free space | 0 | set |
| Read-Only Compressed | `rohd`/`ddsk` | 11 ADC, 10 KenCode (12 from 6.5b13) | fixed-size compressed chunks, raw where they do not shrink | chunk size + ceil(largest overrun / 512) | set |

- On an HFS volume the compressed layouts keep sectors 0–3 as a raw chunk (unless 6.3.3's "Compress Volume Header"
  is on), then chunks up to the last used allocation block, a zero chunk, the alternate MDB (second-last sector) raw,
  and the last sector as zeros. Free space is never stored, so deleted files do not survive.
- The chunk size is 32 sectors in Disk Copy 6.1.2 and 512 by default in 6.3.3 and 6.5b13; 6.3.3's hidden dialog
  allows others (20 and 64 were tried) [Verified].
- **KenCode is chosen in a hidden dialog.** Holding Control while clicking Save in a Read-Only Compressed save shows
  "Override Image Parameters": Smaller (KC) or Faster (ADC, the default), Match Len (67), and in 6.3.3 Chunk Size
  (512) and Compress Volume Header [Verified: 6.1.2, 6.3.3]. 6.5b13 has no such dialog and writes only ADC.
- Disk Copy's encoders, for byte-identical output [Code: 6.1.2, 6.3.3], [Verified: every compressed chunk of the
  samples re-encodes exactly]: ADC with a 65,536-byte window and matches up to 67 bytes; KenCode with a `$2800`-byte
  window and matches up to 64 bytes in 6.3.3 (which ignores Match Len) or up to Match Len in 6.1.2.

## 6. ShrinkWrap 2.1 and other raw images

ShrinkWrap 2.1 (1996) writes no format of its own [Verified: ShrinkWrap 2.1 in SheepShaver, floppy and 5 MB volume]:

| Option | Type/creator | Data fork | Resource fork |
| --- | --- | --- | --- |
| ShrinkWrap Image (floppy) | `dImg`/`Wrap` | Disk Copy 4.2, byte for byte; tag size 0 | none |
| DiskCopy Image (floppy) | `dImg`/`dCpy` | Disk Copy 4.2 (junk after the name, [§3.1](#31-layout)) | `dCpy` 0: the checksums as text |
| ShrinkWrap Image (volume) | `hdrv`/`Wrap` | the raw volume, no header | none |
| Drive Container | `hdrv`/`D:\>` | the raw volume, no header | none |
| Self-Mounting (floppy) | `APPL`/`sImg` | the raw volume | mounter code; `CKSM` 1 = data checksum (Disk Copy 4.2 sum) |
| Self-Mounting (volume) | `APPL`/`iImg` | the raw volume | mounter code; no checksum |

The three volume forms have byte-identical data forks. A raw image is recognised by its file system (for HFS, `BD`
at offset 1024), so ClassicMac's volume readers open these directly [Verified].

Disk Copy 6.0 of 1994 (creator `dCpy`, a floppy duplicator unrelated to NDIF) writes its own RLE-compressed and
self-extracting `dImg` variants; they are not specified here [Code: its disassembly shows no NDIF code].

## 7. ADC

Apple Data Compression, NDIF chunk type `$83` and UDIF run type `$80000004` [Code: Disk Copy 6.3.3 `hdi2` codec],
[Verified: 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF]. A byte-oriented LZ77 code; each token starts with an
opcode byte:

| Opcode bits | Token | Length | Distance |
| --- | --- | --- | --- |
| `1LLLLLLL` | Literal run: *length* bytes follow and are copied | `L` + 1 (1–128) | — |
| `00LLLLDD DDDDDDDD` | Short match | `L` + 3 (3–18) | `D` (10 bits) + 1 (1–1024) |
| `01LLLLLL DDDDDDDD DDDDDDDD` | Long match | `L` + 4 (4–67) | `D` (16 bits) + 1 (1–65,536) |

- A match copies *length* bytes from *distance* bytes back in the output, **one byte at a time**, so a match may
  overlap what it writes (distance 1 repeats the last byte).
- Decoding stops when the output (the chunk's decoded size) is full.
- **A token that would pass the end of the output is an error**, checked before any of it is written; Disk Copy
  reports −8819 (damaged) at read time.
- Disk Copy checks nothing else: a match reaching before the output's start reads the memory before its buffer, and
  input is read past the stored length. ClassicMac reports both instead (`ndif.bad-chunk`).
- No state carries from one chunk to the next.

## 8. KenCode

KenCode is NDIF chunk type `$80`, Disk Copy's "Smaller (KC)" compression [Code: Disk Copy 6.3.3 codec],
[Verified: images made by Disk Copy 6.1.2 and 6.3.3 decode to their CRC, with 20-, 32- and 512-sector chunks]; by
the release history it was the only compression of Disk Image Mounter and Disk Copy 6.0.1 [Fitted?]. It is the
**System's `dcmp` 3 codec** ([RESOURCE-FORK.md](RESOURCE-FORK.md) §15), with the distance classes capped for large
outputs: the length, literal and distance codes are identical [Code: Mac OS 9.0 System, Disk Copy 6.3.3], and a model
of `dcmp` 3 verified in emulation decodes real KenCode chunks of up to 33 sectors byte for byte [Verified]. The two
choose the same distance class up to `$5400` bytes of output; from `$5401` `dcmp` 3 moves on to classes 11–14 while
KenCode stays at 10 ([§8.4](#84-distance-class)): every Disk Copy KenCode decoder (6.1.2, 6.3.3, 6.5b13, and the
self-mounting `oneb` code) also tests a window value set to `$2800`, so class 11 is never reached [Code]. A `dcmp` 3
decoder goes wrong on real KenCode images at the first match past `$5400` [Verified]. ClassicMac keeps its own KenCode
decoder.

### 8.1 Bits

The stream is read **most significant bit first**, with no stored tables: every code is a fixed prefix code. Past the
end of the input, bits read as 0. **The decoder may not read more than 8 × the output size bits**: a read that would
pass that limit is an error (−8819 in Disk Copy) [Code: 6.3.3]. The unary runs of 1 bits below are not counted
against the limit [Code: 6.3.3].

### 8.2 Tokens

`total` counts the bytes produced so far. A flag *afterRun* starts false. Until `total` reaches the output size:

1. Read a **length code** *v* ([§8.3](#83-length-code)).
2. If *v* > 0, or *afterRun* is set: a **match**. Its length is *v* + 2, or *v* + 3 when *afterRun* is set (so it may
   be 3 with *v* = 0); clear *afterRun*. Read a distance ([§8.5](#85-distance)) for the class of `total`
   ([§8.4](#84-distance-class)) and copy *length* bytes from *distance* back, one at a time.
3. Otherwise (*v* = 0, *afterRun* clear): a **literal run**. Read a count ([§8.6](#86-literal-count)), then that many
   8-bit bytes. Set *afterRun* when the count is below 63: a short run must be followed by a match.

A token may run past the output's end; the extra bytes are dropped and decoding ends [Code: 6.3.3]. A match reaching
before the output's start is not checked by Disk Copy (it reads memory before its buffer); ClassicMac reports it.

### 8.3 Length code

Count 1 bits, up to ten; the 0 that ends a shorter run is consumed. Then:

| 1 bits | Then | *v* |
| --- | --- | --- |
| 0 | 1 bit *b* | *b* (0–1) |
| 1 | bit *a* = 0 | 2 |
| 1 | bit *a* = 1, 1 bit *b* | 3 + *b* (3–4) |
| 2 | bit *a* = 0, 1 bit *b* | 5 + *b* (5–6) |
| 2 | bit *a* = 1, 2 bits *b* | 7 + *b* (7–10) |
| 3 | 3 bits | 11–18 |
| 4 | 3 bits | 19–26 |
| 5 | 5 bits | 27–58 |
| 6 | 6 bits | 59–122 |
| 7 | 7 bits | 123–250 |
| 8 | 8 bits | 251–506 |
| 9 | 9 bits | 507–1018 |
| 10 | 10 bits | 1019–2042 |

[Code: 6.3.3], [Verified]

### 8.4 Distance class

The distance code's width class *k* grows with `total` [Code: 6.3.3]:

| `total` below | `$B` | `$15` | `$29` | `$51` | `$A1` | `$2A1` | `$3E9` | `$A81` | `$1501` | `$2A01` | otherwise |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| *k* | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |

Disk Copy's table goes on to classes 11–14 (from `$5401`, as in `dcmp` 3), but each of those needs a window of
`$4001` bytes or more, and the decoder's workspace is set up with a `$2800`-byte window, so KenCode stays at class 10
for all output from `$2A01` on [Code: 6.3.3], [Verified: 512-sector chunks, 256 KB each, decode to the stored CRC].

### 8.5 Distance

| Prefix | Then | Distance |
| --- | --- | --- |
| `0` | *k* bits *x* | *x* + 1 |
| `10` | *k* + 2 bits *x* | 2^*k* + *x* + 1 |
| `11` | *w* bits *x* | 5 × 2^*k* + *x* + 1 |

The width *w* after `11` depends on `total`, with *base* = 5 × 2^*k* [Code: 6.3.3]:

- *w* = 1 if `total` ≤ *base* + 2; else *w* = 2 if `total` ≤ *base* + 4;
- else start with *threshold* = *base* + 4, *step* = 4, *w* = 3 and repeat: add *step* to *threshold*; stop if
  `total` ≤ *threshold* (a threshold of `$680` is compared as `$66C`, a quirk of Disk Copy's code) or *w* = *k* + 4;
  otherwise double *step* and add 1 to *w*.

### 8.6 Literal count

| Bits | Count |
| --- | --- |
| `0` | 1 |
| `1 00` | 2 |
| `1 01` | 3 |
| `1 10` + 2 bits *z* | 4 + *z* (4–7) |
| `1 11` + 4 bits *y*, *y* ≤ 7 | *y* + 8 (8–15) |
| `1 11` + 4 bits *y*, 8 ≤ *y* ≤ 11, + 2 bits *l* | 4*y* + *l* − 16 (16–31) |
| `1 11` + 4 bits *y*, *y* ≥ 12, + 3 bits *l* | 8*y* + *l* − 64 (32–63) |

[Code: 6.3.3], [Verified]

**Example.** `00 101 01100001 01100010 01100011 00 1001` decodes to `abcabc`: length code 0 with *afterRun* clear
starts a literal run of 3 (`abc`); *afterRun* is then set, so length code 0 is a 3-byte match; at `total` 3 the class
is 0, and `10` + 2 bits `01` gives distance 1 + 1 + 1 = 3.

## 9. DART RLE

DART's "fast" compression, and NDIF chunk type `$81` [Code: Disk Copy 6.3.3], [Verified: DART 1.5.3]. The input is
big-endian 16-bit words:

- Read a signed count *n*. If *n* ≥ 0, copy the next *n* words. If *n* < 0, repeat the next word −*n* times.
- Repeat until the output is full. A DART block is always exactly 10,480 words (20,960 bytes).
- Disk Copy refuses |*n*| ≥ 10,481 (−50) [Code: 6.3.3]. ClassicMac reports any run passing the end of the output,
  or input running out, as a damaged block.

A DART header counts an RLE block's length in words.

## 10. DART LZH (LZHUF)

DART's "best" compression, and NDIF chunk type `$82`, is Okumura and Yoshizaki's LZHUF: LZSS over a 4,096-byte ring
buffer with matches of 3–60 bytes, coded with an adaptive Huffman tree for characters and lengths and fixed tables
for the upper bits of positions [Author: `lzhuf.c`, 1988]. Disk Copy 6.3.3's codec follows it with the differences
marked **DART** below [Code: 6.3.3], [Verified: DART 1.5.3's files decode to their source disks].

### 10.1 Constants and tables

`N` = 4096, `F` = 60, `THRESHOLD` = 2, `N_CHAR` = 256 − `THRESHOLD` + `F` = 314 (256 literals and 58 lengths),
`T` = 2 × `N_CHAR` − 1 = 627 (tree nodes), `R` = `T` − 1 = 626 (root), `MAX_FREQ` = `$8000` [Author].

The position tables map the first byte read for a position to its upper 6 bits (`d_code`) and to the total number of
bits in its code (`d_len`) [Author]:

| First byte | `d_code` | `d_len` |
| --- | --- | --- |
| `$00`–`$1F` | 0 | 3 |
| `$20`–`$4F` | 1–3, 16 bytes each | 4 |
| `$50`–`$8F` | 4–11, 8 bytes each | 5 |
| `$90`–`$BF` | 12–23, 4 bytes each | 6 |
| `$C0`–`$EF` | 24–47, 2 bytes each | 7 |
| `$F0`–`$FF` | 48–63, 1 byte each | 8 |

### 10.2 Bit input

Bits are taken most significant first from a 16-bit buffer refilled a byte at a time whenever it holds 8 bits or
fewer [Author]. **DART:** past the end of the input, a refill made while reading a bit supplies zeros and one made
while reading a byte supplies ones [Code: 6.3.3].

### 10.3 The adaptive tree

Arrays: `freq[T + 1]` (16-bit), `prnt[T + N_CHAR]`, `son[T]` [Author].

- **Start:** for each `i` < `N_CHAR`: `freq[i] = 1`, `son[i] = i + T`, `prnt[i + T] = i`. Then for `j` from `N_CHAR`
  to `R`, with `i` = 0, 2, 4, …: `freq[j] = freq[i] + freq[i + 1]`, `son[j] = i`, `prnt[i] = prnt[i + 1] = j`.
  Finally `freq[T] = $FFFF`, `prnt[R] = 0`. **DART:** the tree is rebuilt at the start of every block [Code: 6.3.3].
- **Decode a character:** `c = son[R]`; while `c < T`, `c = son[c + bit]`; the symbol is `c − T`; update it.
- **Update** symbol `c`: if `freq[R]` = `MAX_FREQ`, reconstruct first. Then `c = prnt[c + T]` and repeat until `c`
  is 0: `k = ++freq[c]`; if `k > freq[c + 1]`, set `l = c + 1` and advance `l` while `k > freq[l + 1]`; then swap
  the nodes: `freq[c] = freq[l]`, `freq[l] = k`; `i = son[c]`,
  `prnt[i] = l` (and `prnt[i + 1] = l` if `i < T`); `j = son[l]`, `son[l] = i`; `prnt[j] = c` (and `prnt[j + 1] = c`
  if `j < T`); `son[c] = j`; `c = l`. Then `c = prnt[c]`. **DART:** `k` is compared as a signed 16-bit number with
  the unsigned counts, so a count of `$8000` never moves [Code: 6.3.3].
- **Reconstruct:** gather the leaves (`son[i] ≥ T`) to the front in order, halving each count rounded up
  (`(freq + 1) / 2`). Then for `j` from `N_CHAR` to `T − 1`, with `i` = 0, 2, 4, …: `f = freq[i] + freq[i + 1]`;
  `k = j − 1`, decrease `k` while `f < freq[k]`, then add 1; shift `freq` and `son` from `k` to `j − 1` up by one
  place; `freq[k] = f`, `son[k] = i`. Finally, for every `i` < `T`: `k = son[i]`, `prnt[k] = i`,
  and `prnt[k + 1] = i` if `k < T` [Author].

### 10.4 Decoding a block

- **Window:** the ring buffer `text[N]` persists for the whole file. **DART:** at the start of every block,
  `text[0 … 4035]` is filled with **zeros** (lzhuf.c uses spaces), while `text[4036 … 4095]` keeps what the previous
  block left there (zeros before the first block); `r` = 4036 (`N − F`) [Code: 6.3.3], [Verified].
- **Loop:** decode a character `c`.
  - `c` < 256: output it, `text[r] = c`, `r = (r + 1) mod N`.
  - Otherwise: read a position — take a byte `i`, keep `d_code[i]` as the upper 6 bits, read `d_len[i] − 2` more
    bits into `i` (`i = (i << 1) + bit`), and the lower 6 bits are `i & $3F`. The source is
    `(r − position − 1) mod N` and the length `c − 253` (3–60). Copy byte by byte from the ring, writing each byte to
    the output and to `text[r]`, advancing `r`.
- **End:** stop when the block's 20,960 bytes are written, or **after any token once the bit reader has fetched a
  byte past the end of the input** (DART's own files can end a block one byte short, [§4.2](#42-blocks))
  [Code: 6.3.3], [Verified]. A token that would write past the block's end is an error in Disk Copy (−50); ClassicMac
  stops at the end.

In an NDIF image, Disk Copy 6.3.3 and 6.5b13 allocate the LZH workspace once per driver and clear only ring bytes
0–`$FC3` for each `$82` chunk, so the 60-byte tail is left from the last chunk decoded, in read order; 6.1.2 keeps its
state on the stack, so its tail is stack garbage [Code]. The tail makes no difference in practice: every block of
DART's own files decodes identically with a random tail and in reverse order [Verified]. ClassicMac clears the whole
ring for each chunk. No Disk Copy writes `$82` chunks (none has the LZH encoder); the type exists only in the map
the driver builds in memory for DART files [Code].

## 11. UDIF (not built in ClassicMac yet)

The Universal Disk Image Format replaced NDIF from Disk Copy 6.4 and 6.5 (2000–2002) and is Mac OS X's `.dmg`.
ClassicMac does not read it yet. What follows was verified on **early UDIF images made by Disk Copy 6.5b13** (Mac OS
9.0 in SheepShaver, patched to run below 9.1): read-only compressed (ADC), read-only and read/write device images of
an Apple partition map with an 800K HFS volume, plus "entire device" and CD-R master images of the same device, each
decoded back to the source device exactly. Facts only dmg2img (GPL, behavioural reference) gives are marked so.

Disk Copy 6.5b13 offers UDIF formats only when saving **device images** (types `devi`, `devr`, `devs`, `GImg`,
`PImg`); its menu lists read/write, read-only, read-only compressed, read-only (entire device), CD-R master and two
"OBSOLETE (6.4d50)" formats [Code: 6.5b13], [Verified]. Disk Copy 6.3.3 has no UDIF code at all [Code: 6.3.3].

### 11.1 The `koly` trailer

The last 512 bytes of the data fork [Verified: 6.5b13; the layout agrees with dmg2img]:

| Offset | Size | Type | Meaning | 6.5b13 value |
| --- | --- | --- | --- | --- |
| `+$000` | 4 | `OSType` | `'koly'` | |
| `+$004` | 4 | `u32` | Version | 4 |
| `+$008` | 4 | `u32` | Header size | 512 |
| `+$00C` | 4 | `u32` | Flags: bit 0 = the resource fork is flattened into the data fork; bit 1 = every data run is raw ([§11.4](#114-checksums)) | 1; 3 for "entire device" |
| `+$010` | 8 | `u64` | Running data fork offset | 0 |
| `+$018` | 8 | `u64` | Data fork offset: where the runs' data start | 0 |
| `+$020` | 8 | `u64` | Data fork length | up to the embedded resource fork |
| `+$028` | 8 | `u64` | Resource fork offset | the data's end |
| `+$030` | 8 | `u64` | Resource fork length | 4,509–4,589 |
| `+$038` | 4 | `u32` | Segment number | 1 |
| `+$03C` | 4 | `u32` | Segment count | 1 |
| `+$040` | 16 | | Segment ID | |
| `+$050` | 4 | `u32` | Data checksum type (2 = CRC-32) | 2 |
| `+$054` | 4 | `u32` | Data checksum size in bits | 32 |
| `+$058` | 128 | | Data checksum, left-aligned | |
| `+$0D8` | 8 | `u64` | XML property list offset | **0** |
| `+$0E0` | 8 | `u64` | XML property list length | **0** |
| `+$0E8` | 120 | | Reserved | zeros |
| `+$160` | 4 | `u32` | Master checksum type | 2 |
| `+$164` | 4 | `u32` | Master checksum size in bits | 32 |
| `+$168` | 128 | | Master checksum, left-aligned | |
| `+$1E8` | 4 | `u32` | Image variant | 1 |
| `+$1EC` | 8 | `u64` | Sector count of the device | |
| `+$1F4` | 12 | | Reserved | zeros |

The flags [Code: 6.5b13]:

- **Bit 0**: the resource fork is stored in the data fork, at the resource fork offset (§11.2). Disk Copy's flatten
  routine sets it, its unflatten routine clears it, and opening an image tests it.
- **Bit 1**: every data run is raw, so the data fork is a sector-for-sector copy of the device. The image builder
  starts with it set and clears it at the first run that is not raw; when it is set, Disk Copy skips the run tables
  and reads the data fork directly. (The name is ClassicMac's; the code only tests the bit.)

### 11.2 Where the block tables are

- **Early images (Disk Copy 6.4/6.5):** XML offset and length are 0. The resource fork offset and length point to a
  **complete classic resource fork stored inside the data fork** ([RESOURCE-FORK.md](RESOURCE-FORK.md)). It holds one
  `blkx` resource per partition (−1 "DDM", 0 the partition map, 1 the HFS partition, named after the partition), and
  `plst` 0, `srci` 0, `vers` 1 and `rem ` 0 and 1 [Verified: 6.5b13]. The file's real resource fork holds only a
  `vers` 1 [Verified]. A reader parses the embedded fork's map and takes the `blkx` resources; dmg2img instead walks
  the resource data from a fixed offset, which only works for its layout.
- **Later images** keep the same `mish` tables base64-encoded in an XML property list at the XML offset
  [dmg2img, behavioural reference only].

### 11.3 `mish` block tables

Each `blkx` resource's data is a `mish` table [Verified: 6.5b13]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 4 | `OSType` | `'mish'` |
| `+$04` | 4 | `u32` | Version: 1 |
| `+$08` | 8 | `u64` | First sector of the partition on the device |
| `+$10` | 8 | `u64` | Sector count |
| `+$18` | 8 | `u64` | Data offset: the runs' offsets count from here |
| `+$20` | 4 | `u32` | Buffers needed, in sectors (513 for ADC, 0 uncompressed) |
| `+$24` | 4 | `i32` | Block descriptor: the `blkx` resource ID |
| `+$28` | 24 | | Reserved |
| `+$40` | 4 | `u32` | Checksum type (2 = CRC-32) |
| `+$44` | 4 | `u32` | Checksum size in bits (32) |
| `+$48` | 128 | | Checksum, left-aligned |
| `+$C8` | 4 | `u32` | Run count |
| `+$CC` | `$28` × count | | Runs |

A run:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 4 | `u32` | Type (below) |
| `+$04` | 4 | `u32` | Comment / reserved (0) |
| `+$08` | 8 | `u64` | First sector, relative to the table's first sector |
| `+$10` | 8 | `u64` | Sector count |
| `+$18` | 8 | `u64` | Stored offset, relative to the table's data offset |
| `+$20` | 8 | `u64` | Stored length |

| Type | Meaning | Source |
| --- | --- | --- |
| `$00000000` | Zeros | dmg2img only |
| `$00000001` | Raw | [Verified: 6.5b13] |
| `$00000002` | Free space ("ignore"): not stored, reads as zeros; also used with a count of 0 as a marker | [Verified: 6.5b13] |
| `$80000004` | ADC ([§7](#7-adc)), the NDIF codec | [Verified: 6.5b13] |
| `$80000005` | zlib | dmg2img only |
| `$80000006` | bzip2 | dmg2img only |
| `$80000007` | LZFSE | dmg2img only |
| `$7FFFFFFE` | Comment | dmg2img only |
| `$FFFFFFFF` | End of the table (its first sector = the sector count) | [Verified: 6.5b13] |

Disk Copy 6.5b13's HFS layout mirrors NDIF's: sectors 0–3, the used data, free space, the alternate MDB, free space
[Verified]. The compressed image packs the partitions' data tightly (data offsets 0, 35, 1,559); the read-only one
aligns them (0, 512, 32,768) [Verified].

### 11.4 Checksums

Each checksum field says its algorithm: type 2 is the standard (zlib) CRC-32 (32 bits), **not** NDIF's CRC28; type 4
is MD5 (128 bits) [Verified: 6.5b13]. The read-only, read-only compressed and read/write images use CRC-32; the
read-only "entire device" image (`devi`) uses MD5 in every field. Verifying is optional: decoding never depends on it.

With CRC-32:

- each `mish` checksum covers **only the sectors its runs store**: free runs are skipped, so for HFS it is not the CRC
  of the whole partition;
- the `koly` data checksum covers the data fork up to the data fork length (the embedded resource fork excluded);
- the `koly` master checksum is the CRC-32 of the `mish` checksums' big-endian 4-byte values in `blkx` order
  (−1, 0, 1); the `vers` text repeats it as "CRC32 $…".

With MD5 (the "entire device" image, which stores every sector, free space included, as raw runs):

- each `mish` checksum is the MD5 of its partition's sectors;
- the `koly` data checksum is the MD5 of the data fork, which is the device;
- the `koly` master checksum is the MD5 of the partitions' 16-byte MD5s concatenated in `blkx` order (−1, 0, 1); the
  `vers` text shows it as "MD5 $…".

Its `koly` flags are 3 where the other images have 1 [Verified: 6.5b13]: bit 1 says every run is raw
([§11.1](#111-the-koly-trailer)) [Code: 6.5b13].

### 11.5 Other UDIF files

- **Read/write device images** (`devr`) are the raw device, with no `koly`; the `blkx`, `plst` and other resources
  are in the real resource fork [Verified: 6.5b13]. Read them as raw.
- **CD-R master images** (`GImg`/`CDr3`, the Toast type and creator) are the raw device too, with no `koly` or
  `blkx`; the resource fork holds only `vers` [Verified: 6.5b13]. Read them as raw.
- **Encrypted images** are recognised and refused, not parsed: `encrcdsa` at offset 0 is encryption header version
  2, `cdsaencr` in the last bytes of the file is version 1 [VileFault, behavioural reference; Mac OS X 10.2 and
  later].

## 12. Diagnostics

Every problem is reported with a code and a severity ([README.md](README.md#diagnostics)). The codecs emit none of
their own: their failures surface as `dart.bad-block` or `ndif.bad-chunk`. There are no `adc.`, `kencode.` or `udif.`
codes.

| Code | Severity | Meaning | ClassicMac | Disk Copy |
| --- | --- | --- | --- | --- |
| `diskcopy.truncated` | Error | The data fork holds fewer data bytes than the header says | reads the whole sectors present; skips the checksums | not traced |
| `diskcopy.tags-truncated` | Warning | The tag bytes are cut short | drops the tags | not traced |
| `diskcopy.checksum` | Warning | The data or tag checksum does not match | reads the disk | not traced |
| `dart.bad-block` | Error | An RLE block does not decode to 20,960 bytes, or an LZH block to its 20,480 data bytes | the rest of the block reads as zeros | −8819 or −50 |
| `dart.checksum` | Warning | `CKSM` 2 or 1 does not match | reads the disk | data: INVALID alert when verifying; tags: never checked |
| `ndif.version-2` | Info | A version 2 map, read as Disk Copy 6.1.2 reads it; asks for the image | reads it | 6.1.2: "obsolete" warning; 6.3.3: blank disk; 6.5b13: −8819 |
| `ndif.map-size` | Warning | Version 10 map one entry off its count ("Disk Copy 6.0") | reads the entries present | warning (count one short only) |
| `ndif.map-size` | Error | The map's length does not match its count | reads the entries present | −8819 (6.1.2: −10) |
| `ndif.bad-name` | Error | Volume name over 63 bytes | cuts it | −8819 |
| `ndif.crc-uninitialized` | Warning | CRC is `$FFFFFFFF` | — | warning |
| `ndif.bad-map` | Error | Data start past the data fork; last start ≠ disk size; a start out of order or past the disk; ADC in a map below version 11 | reads up to the bad entry; ADC is still decoded | −8819 (6.1.2: −10) |
| `ndif.no-end` | Warning | No `$FF` entry | the last chunk runs to the disk's end | warning |
| `ndif.gap` | Warning | The first chunk does not start at sector 0 | those sectors read as zeros | not checked; reads garbage |
| `ndif.zero-length` | Warning | A zero chunk stores bytes | ignores them | warning |
| `ndif.short` | Error | A raw chunk stores less than its size, or a chunk's data run past the data fork | reads what is there, then zeros | −8819 |
| `ndif.chunk-size` | Error | A compressed chunk covers more sectors than `+$48` (as stored; equal passes) | decodes it | damaged: −8819 (6.3.3, 6.5b13), −10 (6.1.2) [Code] |
| `ndif.unknown-chunk` | Error | A chunk type Disk Copy does not know (`$F0` named as ShrinkWrap's) | reads it as zeros | −8820 |
| `ndif.missing-segment` | Error | Segmented, but no `bcm#` 128, or a part not found in the folder | reads the disk up to the missing part | −8821 |
| `ndif.bad-segment` | Error | A part other than the last is not a whole number of sectors | reads it as it is | −39 |
| `ndif.bad-checksum` | Error | The disk's CRC28 differs from `+$50` (only with `VerifyChecksums` / `--verify`) | reads the disk | INVALID alert (130) with "Verify checksum" on; nothing otherwise |
| `ndif.bad-chunk` | Error | A compressed chunk fails to decode: ADC overrun, truncated input or match before the start; KenCode overread or match before the start; RLE or LZH short | the rest of the chunk reads as zeros; reported once per chunk | overrun and overread: −8819 when read; before-start and truncated input: not checked, garbage |

Maps ClassicMac cannot read at all throw instead of reporting: a version above 12 (Disk Copy −8818) or other than 2,
10, 11 and 12 (−8819), a map shorter than its header, fewer than two entries, a disk of 0 or `$400000` sectors or
more, the segmented flag below version 12 (all −8819 in Disk Copy), a disk larger than the configured expansion
limit, and an NDIF data fork without its resource fork.

Other Disk Copy results worth knowing: −8816, an unknown file type whose data fork is not a multiple of 512; −8812,
"cannot be mounted from the disk it is presently on" (Disk Copy 6.1.2 and 6.5b13 on SheepShaver's shared host
volume) [Verified]; −20, a write to a zero or compressed chunk [Code: 6.3.3]. Disk Copy 6.1.2 mounted nothing while
File Exchange 3.0.3 was active, because that extension installs its own, newer `.HDI` driver, which 6.1.2 then uses
[Verified].

## 13. Open questions

- **Version 2's real layout.** No image from Disk Image Mounter or Disk Copy 6.0.x has been seen. Disk Copy 6.1.2's
  8-byte layout ([§5.7](#57-version-2)) was verified only on hand-built images; ShrinkWrap 2.1 reads such files with
  the 12-byte layout. That version 2 was written by those two programs is inferred from history and from 6.1.2 not
  writing it [Fitted?].
- **`hdro`** as the Disk Image Mounter era's read-only type is inferred from Disk Copy 6.1.2 naming it beside `rohd`
  [Fitted?].
- **Chunk type `$F0` and `+$74`/`+$78`** come only from Aaru; no image with them has been seen [Fitted?].
- **Disk Copy 4.2's tag checksum** skipping the first 12 bytes was matched on one image, not traced in code [Fitted].
- **The `+$48` check in an emulator.** The rule of [§5.5](#55-validation) (a compressed chunk covering more sectors
  than `+$48` is damaged; equal passes) is traced in all three Disk Copy versions but not yet run: a KenCode image
  whose `+$48` is one below its largest chunk should give −8819 in 6.3.3, −10 in 6.1.2 and message 50 from 6.5b13's
  Check Image, and mount at equal.
- **UDIF:** zlib, bzip2, LZFSE, zero and comment runs, the XML property list and later `koly` versions are known only
  from dmg2img.
