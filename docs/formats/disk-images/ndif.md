# NDIF (Disk Copy 6)

This document describes the classic Mac OS disk image formats that wrap a floppy or volume — Disk Copy 4.2, DART,
NDIF (Disk Copy 6, including self-mounting and segmented images), ShrinkWrap's outputs and UDIF (`.dmg`) — and
the codecs inside them (ADC, KenCode, DART RLE, DART LZH and bzip2), completely enough to write a reader and each
decoder without reading ClassicMac's code. Every reader here yields the disk's sectors as one volume, which the HFS or
MFS reader ([hfs.md](../file-systems/hfs.md)) or the partition-map reader opens next.

**References.** Apple published a specification only for Disk Copy 4.2 (File Type Note $E0/$0005). NDIF, DART and
early UDIF have none: the rules below come from the disassembly of Disk Copy 6.1.2, 6.3.3 and 6.5b13 (the `.HDI`
block driver, its `bcem` validator and version converter, the `hdi1`/`hdi2` codec plug-ins, the DART reader), checked
on images the real software made in SheepShaver (Mac OS 9.0), each decoded back to its source sectors with the stored
checksum matching. DART files were checked on DART 1.5.3's own output (CiderPress2's test data). LZHUF is Okumura and
Yoshizaki's public-domain program; bzip2 is Julian Seward's format. dmg2img, libdmg-hfsplus and Aaru (GPL/LGPL) and
VileFault were behavioural references only; what they alone say is marked as such.

Contents

1. [Conventions](#1-conventions)
2. [Recognising an image](#2-recognising-an-image)
3. [NDIF (Disk Copy 6)](#3-ndif-disk-copy-6)
4. [Diagnostics](#4-diagnostics)
5. [Open questions](#5-open-questions)

---

## 1. Conventions

The shared conventions and source tags of [README.md](../README.md) apply: big-endian values, offsets in hex, sizes in
decimal, 512-byte sectors (also called blocks here), and one tag per rule — **[Doc]**, **[Code]** (with the software
and version), **[Verified]**, **[Author]**, **[Fitted]**. **[Fitted?]** marks a rule that is inferred or not yet
settled, listed again in [§5](#5-open-questions); **[ClassicMac]** marks ClassicMac's own choice where the format
leaves one.

Also in this document:

- A **chunk** is a run of whole sectors stored one way (zeros, raw, or compressed) in an NDIF image; a **block** of a
  DART image is 40 sectors plus their tags.
- **Tag bytes** are the 12 bytes of file-system metadata a Lisa or early Mac floppy kept beside each 512-byte sector.
  Mac OS never uses them; images keep them only for exact copies.
- `start<<8|type` is a `u32` whose high 24 bits are a sector number and whose low 8 bits are a type code.
- "Disk Copy refuses" means the mount fails with the error named; ClassicMac's own behaviour is given with it.

---

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

ClassicMac does not rely on types, because images copied through other systems lose them. It tries UDIF first — a
`koly` trailer in the last 512 bytes, or an encryption signature ([udif.md §6](udif.md#6-how-classicmac-reads-udif)) — before
the partition-map reader, because a UDIF image whose runs are all raw starts with the device's driver descriptor.
After the partition-map reader it tries, in order: a Disk Copy 4.2 header ([diskcopy42.md §3](diskcopy42.md#3-recognition)), an NDIF map in
the resource fork (any `bcem` 128 of at least `$58` bytes), a DART header ([dart.md §1](dart.md#1-header)), then the volume
readers on the data fork itself, which catch every raw image. Encrypted and segmented UDIF images are recognised and
refused.

---

## 3. NDIF (Disk Copy 6)

The New Disk Image Format stores the disk in the data fork as chunks and maps them in a `bcem` 128 resource. It was
written by Disk Copy 6.0.1 through 6.5 (and read by Disk Image Mounter), in read/write, read-only, read-only
compressed, self-mounting and segmented forms.

### 3.1 Files and resources

| Resource | Meaning |
| --- | --- |
| `bcem` 128 | The chunk map ([§3.2](#32-the-bcem-header), [§3.3](#33-chunk-entries)); its name is the volume name [Verified] |
| `bcm#` 128 | A segmented image's part record ([§3.8](#38-segmented-images)) |
| `vers` 1 | Text: the file system, the size and the checksum ("CRC: $…"; Disk Copy 6.5b13 writes "CRC28: $…") [Verified] |
| `STR ` −16396 | The writer: "Disk Copy" (6.3.3), "Disk Copy 6.1" (6.1.2), none from 6.5b13 [Verified] |

Disk Copy's driver reads back only `bcem` and `bcm#` [Code: 6.3.3]. `cSum`, `plst`, `size` and similar resources are
not used by Disk Copy 6.3.3 [Code: 6.3.3]. Without the resource fork the data fork cannot be decoded.

### 3.2 The `bcem` header

The map is a 128-byte header then 12-byte entries (version 2 differs: [§3.7](#37-version-2)). Field meanings are
from Disk Copy 6.3.3's field-name strings and validator [Code: 6.3.3], values from real images [Verified].

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 2 | `u16` | Version: 10, 11 or 12 (2 is the old layout) |
| `+$02` | 2 | `u16` | File-system ID; 0 in every image seen; `$FFFF` draws a warning |
| `+$04` | 64 | `Str63` | Volume name |
| `+$44` | 4 | `u32` | Disk size in sectors |
| `+$48` | 4 | `u32` | Buffer size in sectors: the largest compressed chunk plus room for the codec's overrun ([§3.10](#310-what-disk-copy-writes)); 0 when nothing is compressed |
| `+$4C` | 4 | `u32` | Data start: added to every entry's offset (0 in every image seen) |
| `+$50` | 4 | `u32` | CRC-32 of the disk ([§3.6](#36-the-checksum-crc28)); 0 = none |
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

### 3.3 Chunk entries

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$0` | 4 | `start<<8\|type` | First sector of the chunk, and its type ([§3.4](#34-chunk-types)) |
| `+$4` | 4 | `u32` | Offset of the stored bytes in the data fork, from the data start (`+$4C`) |
| `+$8` | 4 | `u32` | Stored length in bytes |

[Code: 6.3.3], [Verified]

- A chunk covers the sectors from its start to the next entry's start: **its decoded size is (next start − start) ×
  512** [Code: 6.3.3]. The chunk size is not stored anywhere else; it is the user's choice
  ([§3.10](#310-what-disk-copy-writes)).
- The last entry has type `$FF` and start = the disk size. Disk Copy 6.3.3 gives it the data's end as offset and 0
  as length; Disk Copy 6.5b13 gives offset 0 [Verified]. Readers ignore both.
- Zero chunks store nothing: offset and length 0 [Verified: 6.3.3].
- Stored bytes follow one another in entry order [Verified], but a reader uses the offsets.

### 3.4 Chunk types

| Type | Meaning | Source |
| --- | --- | --- |
| `$00` | Zeros; nothing is read | [Code: 6.3.3], [Verified] |
| `$02` | Raw: the sectors as they are | [Code: 6.3.3], [Verified] |
| `$80` | KenCode ([kencode.md](../codecs/kencode.md)), Disk Copy's "Smaller (KC)" | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3] |
| `$81` | DART RLE ([dart-rle.md](../codecs/dart-rle.md)) | [Code: 6.3.3] |
| `$82` | DART LZH ([lzhuf.md](../codecs/lzhuf.md)) | [Code: 6.3.3] |
| `$83` | ADC ([adc.md](../codecs/adc.md)), "Faster (ADC)"; map version 11 or later | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3, 6.5b13] |
| `$F0` | Unknown; Aaru names it ShrinkWrap 3's compression. Never seen | behavioural reference only |
| `$FF` | End of the map | [Code: 6.3.3], [Verified] |

- Disk Copy 6.3.3 knows no other type: any other value (`$F0` included) is refused with −8820 ("format not
  recognized"), both when mounting and when read [Code: 6.3.3]. ClassicMac reports it and reads the chunk as zeros.
- Types `$81` and `$82` exist for the in-memory DART mapping ([dart.md](dart.md)); no NDIF file with them has been seen.
- Codecs are plug-ins, `hdi1` (68k) and `hdi2` (PowerPC) resources 128–131 for types `$80`–`$83`; the PowerPC set is
  used when the machine has one [Code: 6.3.3]. No codec keeps state from one chunk to the next, except LZH's window
  tail, which Disk Copy's driver carries over from the chunk it decoded last [Code: 6.3.3].
- A compressed image stores any chunk that would not shrink as raw (`$02`), so types mix within one image
  [Verified: 6.1.2, 6.3.3].

### 3.5 Validation

When mounting, Disk Copy 6.3.3's validator checks the map; errors refuse the mount with −8819 unless stated, warnings
are only logged [Code: 6.3.3]:

| Check | Disk Copy 6.3.3 | ClassicMac |
| --- | --- | --- |
| Version ([§3.2](#32-the-bcem-header)) | −8818 / −8819 | refuses |
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
| A type not listed in [§3.4](#34-chunk-types) | −8820 | reports, zeros |
| `$83` in a map below version 11 | error | reports |
| Zero chunk with a nonzero length | warning | reports |
| Raw chunk storing fewer than size bytes | error (more is a warning) | reports, zero-fills |
| Compressed chunk covering more than `+$48` sectors (as stored, no doubling; equal passes) | error, per chunk (see below; the stored length is not checked) | reports (a warning; an error past the decode buffer), decodes the chunk |
| Starts not increasing, or past the disk | error | reports, reads the map up to that entry |
| Chunk data past the end of the data fork | error | reports, reads what is there |
| Last start ≠ disk size | error | reports |
| No `$FF` entry | warning | reports; the last chunk runs to the disk's end |
| First chunk not starting at sector 0 | not checked (those sectors read garbage) | reports; they read as zeros |
| Read/write mount with zero or compressed chunks | error | (read-only) |

**Per-chunk checks count only on the last entry.** The 6.3.3 and 6.5b13 validators clear the error at the start of
every table entry, so an error found in a chunk's entry (a count over `+$48`, an unknown type, `$83` below version 11,
a raw chunk storing too little, a start out of order or past the disk, data past the end of the data fork) refuses
the mount only when it is found in the last entry, normally the `$FF` one [Code: 6.3.3, 6.5b13]. The checks on the
header and the map as a whole are not affected.

Disk Copy 6.1.2 makes the same checks for versions 2, 10 and 11, except the reserved and segment fields, keeps every
error, and returns −10 for each, an unknown type included [Code: 6.1.2].

The `+$48` check applies to types `$80`–`$83`, with `+$48` as stored (not doubled for version 10). Disk Copy 6.3.3
logs it as "compressed block count exceeds max chunk block count" [Code: 6.3.3]. What each version does with a chunk
over `+$48` [Code: 6.1.2, 6.3.3, 6.5b13]:

- **Disk Copy 6.1.2** refuses the image as damaged (−10) [Verified: 6.1.2 refuses a KenCode image whose `+$48` is
  511 with 512-sector chunks with "The Mount Image operation did not complete (−10) … is damaged", and mounts the
  same image with `+$48` = 512, checksum valid].
- **Disk Copy 6.3.3 and 6.5b13** forget the error at the next entry (above), so the chunk fails only when it does not
  fit the decode buffer, `+$48` × 512 bytes doubled for version 10 ([§3.9](#39-reading-the-disk)), at the first read
  of it. A version 10 (KenCode) image with `+$48` = 511 and 512-sector chunks mounts, checksum valid; the same with
  version 11 (ADC) fails with −27 (possibly −37: the digits were hard to read) [Verified: 6.5b13; 6.3.3 not run].
  6.5b13's Check Image command gave −50 on both images [Verified: 6.5b13].

No version checks a chunk's stored length; a chunk that decodes to the wrong size fails only when read (the codec's
error, −8819 or −10) [Code: 6.3.3, 6.5b13, 6.1.2]. Disk Copy 6.5b13 turns the NDIF map into UDIF runs in memory:
`+$48` becomes the buffers-needed value (doubled for version 10) and types `$80`–`$83` become
`$80000001`–`$80000004` [Code: 6.5b13].

ClassicMac reports a chunk over `+$48` as `ndif.chunk-size`, a Warning (6.1.2 refuses the image, later versions read
it), and as an Error when the chunk is also larger than the decode buffer, which no version can read. It decodes the
chunk either way.

### 3.6 The checksum (CRC28)

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

### 3.7 Version 2

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

### 3.8 Segmented images

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
| `+$14` | 4 | `u32` | CRC-32 ([§3.6](#36-the-checksum-crc28)) of this part's data fork; 0 if the source had no CRC. Never verified |

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

### 3.9 Reading the disk

For each sector: find the chunk that covers it, decode the whole chunk to (next start − start) × 512 bytes, and take
the sector. Disk Copy's driver reads a compressed chunk into the end of its buffer, decodes it in place, and keeps
the last decoded chunk [Code: 6.3.3]; its buffer is `+$48` × 512 bytes, doubled for version 10 (KenCode's workspace)
[Code: 6.1.2, 6.3.3]. A read that reaches the `$FF` entry fails with −39 [Code: 6.3.3].

A self-mounting image (`.smi`, `APPL`/`oneb`) is an ordinary NDIF image whose resource fork adds the mounting
application; the data fork is unchanged [Verified: 6.3.3].

### 3.10 What Disk Copy writes

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

---

## 4. Diagnostics

Every problem is reported with a code and a severity ([README.md](../README.md#diagnostics)). The codecs emit none of
their own: their failures surface as `dart.bad-block`, `ndif.bad-chunk` or `udif.bad-run`. There are no `adc.`,
`kencode.` or `bzip2.` codes.

| Code | Severity | Meaning | ClassicMac | Disk Copy |
| --- | --- | --- | --- | --- |
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
| `ndif.chunk-size` | Warning | A compressed chunk covers more sectors than `+$48` (as stored; equal passes) but fits the decode buffer (`+$48`, doubled for version 10) | decodes it | 6.1.2: damaged, −10 [Code] [Verified]; 6.3.3, 6.5b13: reads it [Code] [Verified: 6.5b13] |
| `ndif.chunk-size` | Error | A compressed chunk covers more sectors than the decode buffer | decodes it | 6.1.2: −10 [Code]; 6.3.3, 6.5b13: fails at the first read of the chunk [Code] (−27 or −37 in 6.5b13 [Verified]) |
| `ndif.unknown-chunk` | Error | A chunk type Disk Copy does not know (`$F0` named as ShrinkWrap's) | reads it as zeros | −8820 |
| `ndif.missing-segment` | Error | Segmented, but no `bcm#` 128, or a part not found in the folder | reads the disk up to the missing part | −8821 |
| `ndif.bad-segment` | Error | A part other than the last is not a whole number of sectors | reads it as it is | −39 |
| `ndif.bad-checksum` | Error | The disk's CRC28 differs from `+$50` (only with `VerifyChecksums` / `--verify`) | reads the disk | INVALID alert (130) with "Verify checksum" on; nothing otherwise |
| `ndif.bad-chunk` | Error | A compressed chunk fails to decode: ADC overrun, truncated input or match before the start; KenCode overread or match before the start; RLE or LZH short | the rest of the chunk reads as zeros; reported once per chunk | overrun and overread: −8819 when read; before-start and truncated input: not checked, garbage |

Where the Disk Copy column gives a validator error for one chunk's entry (`ndif.short`, `ndif.unknown-chunk`, and the
order, range and ADC cases of `ndif.bad-map`), Disk Copy 6.3.3 and 6.5b13 refuse the mount only when that entry is the
last one; 6.1.2 refuses on any entry ([§3.5](#35-validation)) [Code].

Maps ClassicMac cannot read at all throw instead of reporting: a version above 12 (Disk Copy −8818) or other than 2,
10, 11 and 12 (−8819), a map shorter than its header, fewer than two entries, a disk of 0 or `$400000` sectors or
more, the segmented flag below version 12 (all −8819 in Disk Copy), a disk larger than the configured expansion
limit, and an NDIF data fork without its resource fork. UDIF images throw for the reasons in
[udif.md §6](udif.md#6-how-classicmac-reads-udif) (encrypted, segmented, no tables, tables outside the file or unreadable).

Other Disk Copy results worth knowing: −8816, an unknown file type whose data fork is not a multiple of 512; −8812,
"cannot be mounted from the disk it is presently on" (Disk Copy 6.1.2 and 6.5b13 on SheepShaver's shared host
volume) [Verified]; −20, a write to a zero or compressed chunk [Code: 6.3.3]. Disk Copy 6.1.2 mounted nothing while
File Exchange 3.0.3 was active, because that extension installs its own, newer `.HDI` driver, which 6.1.2 then uses
[Verified].

---

## 5. Open questions

- **Version 2's real layout.** No image from Disk Image Mounter or Disk Copy 6.0.x has been seen. Disk Copy 6.1.2's
  8-byte layout ([§3.7](#37-version-2)) was verified only on hand-built images; ShrinkWrap 2.1 reads such files with
  the 12-byte layout. That version 2 was written by those two programs is inferred from history and from 6.1.2 not
  writing it [Fitted?].
- **`hdro`** as the Disk Image Mounter era's read-only type is inferred from Disk Copy 6.1.2 naming it beside `rohd`
  [Fitted?].
- **Chunk type `$F0` and `+$74`/`+$78`** come only from Aaru; no image with them has been seen [Fitted?].
- **The `+$48` check in Disk Copy 6.3.3 and 6.5b13** ([§3.5](#35-validation)). 6.1.2's refusal (−10) is run;
  6.5b13 mounting a version 10 image whose chunks pass `+$48` but fit the doubled buffer is run, and matches the
  traced rule that the validator's per-entry error is cleared. Not yet run: 6.3.3 (the emulator hung); the exact
  code 6.5b13 gives for the ADC image (−27 or −37); why its Check Image gives −50; and the equal case in Check
  Image, which hung the application.
