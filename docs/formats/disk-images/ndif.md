# NDIF (Disk Copy 6)

The New Disk Image Format stores a disk in the data fork as chunks (zeros, raw or compressed sectors) and maps them in
a `'bcem'` 128 resource. Disk Copy 6.0.1 through 6.5 wrote it (and Disk Image Mounter read it), in read/write,
read-only, read-only compressed, self-mounting (`.smi`) and segmented forms. Apple published no specification. ClassicMac
reads an image as one file whose data fork is the disk, for the HFS or MFS reader ([hfs.md](../file-systems/hfs.md))
or the partition-map reader to open next.

| | |
| --- | --- |
| Identified by | Types `'dimg'`, `'rohd'`, `'hdro'` (creator `'ddsk'`), `'hdc '`, `'hdcm'`; `'APPL'`/`'oneb'` (self-mounting); `'dseg'`/`'ddsk'` for later parts ([raw-images.md §2.1](raw-images.md#21-how-disk-copy-chooses-a-format)). No signature in the data fork: a `'bcem'` 128 resource |
| ClassicMac | Reads; `ClassicMac.Files.Hfs.NdifReader`. Writes: `NdifWriter` rewrites an image around a changed disk, makes a new one of a disk and cuts one into the parts of a segmented image |
| Verified against | Disk Copy 6.1.2, 6.3.3 and 6.5b13 images made in SheepShaver (Mac OS 9.0), each decoded back to its source sectors with the stored checksum matching<br>Hand-built version 2 images mounted by Disk Copy 6.1.2<br>Damaged and segmented images as Disk Copy 6.3.3 handled them<br>New Read-Only, ADC and segmented images byte for byte against Disk Copy 6.3.3's of the same volumes |
| Sources | Disk Copy 6.1.2, 6.3.3 and 6.5b13 (disassembly: the `.HDI` block driver, its `'bcem'` validator and version converter, the `'hdi1'`/`'hdi2'` codec plug-ins); Aaru and ShrinkWrap 2.1 for behaviour only |

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

A **chunk** is a run of whole 512-byte sectors stored one way (zeros, raw, or compressed). `start<<8|type` is a `u32`
whose high 24 bits are a sector number and whose low 8 bits are a type code.

### 1.1 Files and resources

| Resource | Meaning |
| --- | --- |
| `'bcem'` 128 | The chunk map ([§1.2](#12-the-bcem-header), [§1.3](#13-chunk-entries)); its name is the volume name [Verified] |
| `'bcm#'` 128 | A segmented image's part record ([§1.6](#16-segmented-images)) |
| `'vers'` 1 | Text: the file system, the size and the checksum ("CRC: $…"; Disk Copy 6.5b13 writes "CRC28: $…") [Verified] |
| `'STR '` −16396 | The writer: "Disk Copy" (6.3.3), "Disk Copy 6.1" (6.1.2), none from 6.5b13 [Verified] |

Disk Copy's driver reads back only `'bcem'` and `'bcm#'`; `'cSum'`, `'plst'`, `'size'` and similar resources are not
used by Disk Copy 6.3.3 [Code: 6.3.3]. Without the resource fork the data fork cannot be decoded. A self-mounting image
(`'APPL'`/`'oneb'`) is an ordinary NDIF image whose resource fork adds the mounting application; the data fork is
unchanged [Verified: 6.3.3].

### 1.2 The `bcem` header

The map is a 128-byte header then 12-byte entries (version 2 differs: [§4.2](#42-version-2)). Field meanings are from
Disk Copy 6.3.3's field-name strings and validator [Code: 6.3.3], values from real images [Verified].

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$00` | 2 | Version | `u16`: 10, 11 or 12 (2 is the old layout); [§4.1](#41-map-versions) |
| `+$02` | 2 | File-system ID | `u16`: 0 in every image seen; `$FFFF` draws a warning from Disk Copy |
| `+$04` | 64 | Volume name | `Str63` |
| `+$44` | 4 | Disk size | `u32`, in sectors |
| `+$48` | 4 | Buffer size | `u32`, in sectors: the largest compressed chunk plus room for the codec's overrun ([§4.3](#43-what-disk-copy-writes)); 0 when nothing is compressed |
| `+$4C` | 4 | Data start | `u32`: added to every entry's offset (0 in every image seen) |
| `+$50` | 4 | CRC | `u32`: CRC28 of the disk ([§1.5](#15-the-checksum-crc28)); 0 = none |
| `+$54` | 4 | Segmented flag | `u32`: nonzero in part 1 of a segmented image, which needs version 12 |
| `+$58` | 4 | Performance | `u32`, not checked |
| `+$5C` | 4 | Performance | `u32`, not checked |
| `+$60` | 28 | Reserved | `u32[7]`. Aaru calls `+$74` an encryption flag and `+$78` a password hash (ShrinkWrap 3?); never seen [Reference: Aaru] |
| `+$7C` | 4 | Entry count | `u32`, the end entry included |
| `+$80` | 12 × count | Entries | [§1.3](#13-chunk-entries) |

### 1.3 Chunk entries

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$0` | 4 | Start and type | `start<<8\|type`: the chunk's first sector, and its type ([§1.4](#14-chunk-types)) |
| `+$4` | 4 | Offset | `u32`: the stored bytes' offset in the data fork, from the data start (`+$4C`) |
| `+$8` | 4 | Stored length | `u32`, in bytes |

[Code: 6.3.3], [Verified]

- A chunk covers the sectors from its start to the next entry's start: **its decoded size is (next start − start) ×
  512** [Code: 6.3.3]. The chunk size is not stored anywhere else; it is the user's choice
  ([§4.3](#43-what-disk-copy-writes)).
- The last entry has type `$FF` and start = the disk size. Disk Copy 6.3.3 gives it the data's end as offset and 0 as
  length; Disk Copy 6.5b13 gives offset 0 [Verified]. Readers ignore both.
- Zero chunks store nothing: offset and length 0 [Verified: 6.3.3].
- Stored bytes follow one another in entry order [Verified], but a reader uses the offsets.

### 1.4 Chunk types

| Type | Meaning | Source |
| --- | --- | --- |
| `$00` | Zeros; nothing is read | [Code: 6.3.3], [Verified] |
| `$02` | Raw: the sectors as they are | [Code: 6.3.3], [Verified] |
| `$80` | KenCode ([kencode.md](../codecs/kencode.md)), Disk Copy's "Smaller (KC)" | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3] |
| `$81` | DART RLE ([dart-rle.md](../codecs/dart-rle.md)) | [Code: 6.3.3] |
| `$82` | DART LZH ([lzhuf.md](../codecs/lzhuf.md)) | [Code: 6.3.3] |
| `$83` | ADC ([adc.md](../codecs/adc.md)), "Faster (ADC)"; map version 11 or later | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3, 6.5b13] |
| `$F0` | Named as ShrinkWrap 3's compression; never seen | [Reference: Aaru] |
| `$FF` | End of the map | [Code: 6.3.3], [Verified] |

- Disk Copy 6.3.3 knows no other type: any other value (`$F0` included) is refused with −8820 ("format not
  recognized"), both when mounting and when read [Code: 6.3.3].
- Types `$81` and `$82` exist for the map Disk Copy builds in memory for DART files
  ([dart.md §2](dart.md#2-reading)); no NDIF file with them has been seen.
- The codecs are plug-ins, `'hdi1'` (68k) and `'hdi2'` (PowerPC) resources 128–131 for types `$80`–`$83`; the PowerPC
  set is used when the machine has one [Code: 6.3.3]. No codec keeps state from one chunk to the next, except LZH's
  window tail ([lzhuf.md §2.3](../codecs/lzhuf.md#23-decoding-a-block)) [Code: 6.3.3].
- A compressed image stores any chunk that would not shrink as raw (`$02`), so types mix within one image
  [Verified: 6.1.2, 6.3.3].

### 1.5 The checksum (CRC28)

`+$50` holds a CRC-32 of the whole disk [Code: 6.3.3], [Verified: every sample]. It is **not** zlib's CRC-32: the
*normal* polynomial is used in a right-shifting loop, and there is no final xor.

1. Build the table: for each byte value `i`, `c = i`, then eight times
   `c = (c & 1) ? (c >> 1) ^ $04C11DB7 : c >> 1`.
2. Start with `crc = $FFFFFFFF`.
3. For every byte of the disk, every sector in order (disk size × 512 bytes, zero chunks as zeros):
   `crc = table[(crc ^ byte) & $FF] ^ (crc >> 8)`.
4. The result is `crc`, with no final xor.

- Because it covers the decoded disk, the read-only and compressed images of one volume carry the same value.
- Test values: 819,200 zero bytes give `$0321EDEE` [Verified: reported by Disk Copy 6.3.3]; the ASCII string
  `123456789` gives `$03B0D416` (computed from the rule).
- Apple later called it "CRC28": Disk Copy 6.5b13's `'vers'` text says so [Verified: 6.5b13], as does Mac OS X's
  `hdiutil`.
- 0 means none: read/write images store 0, and Disk Copy's driver clears the field and rewrites the map when it
  unmounts one [Code: 6.3.3], [Verified].

### 1.6 Segmented images

Disk Copy 6.3.3 splits an image into parts (from AppleScript only, at most 128 parts) [Code: 6.3.3],
[Verified: 6.3.3]:

- The data fork of the whole image is cut raw into parts of ceil(sectors / parts) sectors; **the map's offsets run
  across the parts' data forks placed back to back** [Verified].
- **Only part 1 has a `'bcem'`**: version 12, `+$54` = 1. Part 1 keeps its type; the others are typed `'dseg'`; all
  keep the creator [Verified].
- Parts are named "*name* *N*of*M*", *N* zero-padded to the width of *M*, at most 31 characters; later parts carry a
  `'vers'` 1 "Part *n*/*M* of a disk image" [Verified].

Every part has a `'bcm#'` 128 [Code: 6.3.3], [Verified]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$00` | 2 | Part number | `u16`, from 1 |
| `+$02` | 2 | Part count | `u16` |
| `+$04` | 16 | Image ID | The same in every part (made of the date, the tick count, a random number and a CRC-32) |
| `+$14` | 4 | Part CRC | `u32`: CRC28 ([§1.5](#15-the-checksum-crc28)) of this part's data fork; 0 if the source had no CRC. Never verified |

## 2. Reading

Disk Copy chooses the NDIF reader by file type ([raw-images.md §2.1](raw-images.md#21-how-disk-copy-chooses-a-format)).
"Disk Copy refuses" below means the mount fails with the result named.

### 2.1 Validation

When mounting, Disk Copy 6.3.3's validator checks the map; errors refuse the mount with −8819 unless stated, warnings
are only logged [Code: 6.3.3]:

| Check | Disk Copy 6.3.3 |
| --- | --- |
| Version ([§4.1](#41-map-versions)) | −8818 / −8819 |
| Name length over 63 | Error |
| Name empty, or over 27 bytes | Warning |
| Disk size ≥ `$400000` sectors | Error |
| Data start past the data fork | Error |
| CRC `$FFFFFFFF` ("uninitialized") | Warning |
| CRC 0 in a read-only image | Warning |
| Segmented flag with version below 12 | Error |
| Reserved longs (only `+$64` is tested, a bug; the outcome was not traced) | Tested |
| Count below 2 | Error |
| Map size ≠ `$80` + 12 × count | Error; a warning ("created with Disk Copy 6.0") when version 10 has exactly one entry more than its count |
| A type not listed in [§1.4](#14-chunk-types) | −8820 |
| `$83` in a map below version 11 | Error |
| Zero chunk with a nonzero length | Warning |
| Raw chunk storing fewer than its size in bytes | Error (more is a warning) |
| Compressed chunk covering more than `+$48` sectors (as stored, no doubling; equal passes) | Error, per chunk ([§2.3](#23-the-48-check)); the stored length is not checked |
| Starts not increasing, or past the disk | Error |
| Chunk data past the end of the data fork | Error |
| Last start ≠ disk size | Error |
| No `$FF` entry | Warning |
| First chunk not starting at sector 0 | Not checked: those sectors read garbage |
| Read/write mount with zero or compressed chunks | Error |

Disk Copy 6.1.2 makes the same checks for versions 2, 10 and 11, except the reserved and segment fields, keeps every
error, and returns −10 for each, an unknown type included [Code: 6.1.2].

### 2.2 Per-chunk errors count only on the last entry

The 6.3.3 and 6.5b13 validators clear the error at the start of every table entry, so an error found in a chunk's
entry (a count over `+$48`, an unknown type, `$83` below version 11, a raw chunk storing too little, a start out of
order or past the disk, data past the end of the data fork) refuses the mount only when it is found in the last entry,
normally the `$FF` one [Code: 6.3.3, 6.5b13]. The checks on the header and on the map as a whole are not affected.
6.1.2 refuses on any entry ([§2.1](#21-validation)).

### 2.3 The `+$48` check

The check applies to types `$80`–`$83`, with `+$48` as stored (not doubled for version 10). Disk Copy 6.3.3 logs it as
"compressed block count exceeds max chunk block count" [Code: 6.3.3]. What each version does with a chunk over `+$48`
[Code: 6.1.2, 6.3.3, 6.5b13]:

- **Disk Copy 6.1.2** refuses the image as damaged (−10) [Verified: 6.1.2 refuses a KenCode image whose `+$48` is 511
  with 512-sector chunks with "The Mount Image operation did not complete (−10) … is damaged", and mounts the same
  image with `+$48` = 512, checksum valid].
- **Disk Copy 6.3.3 and 6.5b13** forget the error at the next entry ([§2.2](#22-per-chunk-errors-count-only-on-the-last-entry)),
  so the chunk fails only when it does not fit the decode buffer, `+$48` × 512 bytes doubled for version 10
  ([§2.4](#24-reading-the-disk)), at the first read of it. A version 10 (KenCode) image with `+$48` = 511 and
  512-sector chunks mounts, checksum valid; the same with version 11 (ADC) fails with −27 (possibly −37: the digits were
  hard to read) [Verified: 6.5b13; 6.3.3 not run]. 6.5b13's Check Image command gave −50 on both images
  [Verified: 6.5b13].

No version checks a chunk's stored length; a chunk that decodes to the wrong size fails only when read (the codec's
error, −8819 or −10) [Code: 6.3.3, 6.5b13, 6.1.2].

### 2.4 Reading the disk

1. For a sector, find the chunk that covers it.
2. Zero chunk: the sector is zeros. Raw chunk: read it from data start + offset.
3. Compressed chunk: decode the whole chunk to (next start − start) × 512 bytes with its codec ([§1.4](#14-chunk-types))
   and take the sector.

Disk Copy's driver reads a compressed chunk into the end of its buffer, decodes it in place, and keeps the last
decoded chunk [Code: 6.3.3]; its buffer is `+$48` × 512 bytes, doubled for version 10 (KenCode's workspace)
[Code: 6.1.2, 6.3.3]. A read that reaches the `$FF` entry fails with −39 [Code: 6.3.3]. A write to a zero or
compressed chunk fails with −20 [Code: 6.3.3].

### 2.5 Checking the checksum

The driver never checks `+$50`. Disk Copy checks it when its "Verify checksum" setting is on: a mismatch stops the mount
with an alert (result 130, "The checksum of … is INVALID") [Code: 6.3.3], [Verified: 6.3.3]. With the setting off, a
wrong CRC mounts, and damaged compressed data are served as they decode, without an error [Verified: 6.3.3].

### 2.6 Finding a segmented image's parts

**Disk Copy finds parts by `'bcm#'`, never by name** [Code: 6.3.3], [Verified: 6.3.3]:

1. Part 1 may be `'dimg'`, `'rohd'` or `'APPL'`/`'oneb'`; its `'bcm#'` gives the image ID and the count.
2. Look in part 1's folder for files typed `'dseg'` whose `'bcm#'` has the same image ID and count; take each part
   number once. Renamed or reordered parts mount.
3. Join the parts' data forks in part order; the map's offsets run across them ([§1.6](#16-segmented-images)).

A missing part, or one from another image, refuses the mount with −8821 ("Not all parts of disk image … could be found.
Part *n* is missing or corrupted.") [Verified: 6.3.3]. A part other than the last whose length is not a multiple of 512
gives −39 [Code: 6.3.3].

## 3. Writing

### 3.1 Rewriting an image

An image is made again around a changed disk of the same size, keeping what Disk Copy reads (§2.1) valid:

1. The map keeps its version and header; only map versions 10 to 12 that are not segmented are rewritten.
2. Chunk boundaries stay. A chunk whose decoded sectors are unchanged keeps its type and stored bytes. A changed chunk
   is stored raw (`$02`, its size in bytes), as Disk Copy stores a chunk that would not shrink (§1.4); raw chunks are
   valid in every version and do not count against `+$48`. In a compressed image a changed run is compressed again
   with the image's codec, as Disk Copy 6.3.3 encodes it: ADC (`$83`, [adc.md §3](../codecs/adc.md#3-writing)) when
   the map is version 11 or later and holds ADC chunks, otherwise KenCode (`$80`,
   [kencode.md §3](../codecs/kencode.md#3-writing)) when it holds KenCode chunks; stored so unless that is longer than
   the sectors, and `+$48` grows when the run and its margin need more. An edited ADC
   image of an 800 KB volume is 148,295 bytes (153,375 before the edit), mounts in Disk Copy 6.3.3 with the checksum
   valid and passes Disk First Aid 8.5 [Verified]; so do edited KenCode images with 512- and 20-sector chunks, given
   as Basilisk II entries (216,620 and 216,667 bytes; 224,622 and 219,336 before) [Verified]. Its runs of
   all-zero sectors become zero
   chunks of their own (offset and length 0, §1.3), so an edit does not store empty space; the map gains their entries
   and its count (`+$7C`) and size follow (§2.1). An image whose chunks are all raw (a read/write image, §4.3) stays
   all raw, since Disk Copy refuses a read/write mount with zero chunks (§2.1). Edited ADC and read-only raw images
   mount in Disk Copy 6.3.3 with the checksum valid and pass Disk First Aid 8.5 [Verified].
3. Stored bytes are written in entry order from the data start; each entry's offset and length are made again; the end
   entry's offset is the data's end and its length 0, as Disk Copy 6.3.3 writes it (§1.3).
4. The CRC (`+$50`) is computed again over the new disk (§1.5) when the image had one, and stays 0 when it had none. The
   `'vers'` 1 text's "CRC: $…" or "CRC28: $…" gets the new value (same length).
5. Every other resource, the name and the Finder info stay.

[ClassicMac], following §1–§2. The edit session names the sectors it changed, so a chunk is found unchanged without
the old disk being decoded.

### 3.2 A new image

A disk becomes an image laid out as Disk Copy 6.3.3 lays it out ([§4.3](#43-what-disk-copy-writes)):

1. **Read/Write** (`'dimg'`, version 10): the whole disk as one raw chunk; CRC 0.
2. **Read-Only** (`'rohd'`, version 10) and **Read-Only Compressed** (`'rohd'`; ADC version 11, KenCode version 10), on a
   plain HFS disk:
   - sectors 0 to `drAlBlSt` − 1 (boot blocks, MDB, bitmap) as one raw chunk;
   - the **used area**, from `drAlBlSt` to the end of the last allocation block the bitmap marks used: read-only, one
     raw chunk; compressed, chunks of the chunk size (512 sectors by default) from `drAlBlSt`, the last one shorter,
     each compressed with the image's codec as Disk Copy's encoder does ([adc.md §3](../codecs/adc.md#3-writing),
     [kencode.md §3](../codecs/kencode.md#3-writing)) or stored raw when that does not shrink it. Free blocks inside
     the used area keep their bytes;
   - from there to the second-last sector, a zero chunk (none when the used area reaches it);
   - the second-last sector (the alternate MDB) raw, and the last sector as a zero chunk.
3. Stored bytes follow one another in entry order from data start 0; the end entry's offset is the data's end and its
   length 0.
4. `+$48` is the largest of each compressed chunk's sectors plus its decoder's overrun in whole sectors; 0 when nothing
   is compressed. The CRC (§1.5) is of the disk as the image stores it (what lies past the used area as zeros).
5. The map's name, and `'bcem'` 128's resource name, is the volume name (`drVN`).

[Verified: the Read-Only and Read-Only Compressed (ADC) images of an 800 KB and a 5 MB volume made this way from their
Read/Write images have the same data fork and the same `'bcem'` as Disk Copy 6.3.3's, byte for byte]. ClassicMac's
own choices [ClassicMac]:

- A disk that is not a plain HFS volume (no `BD` MDB, or one whose allocation area does not fit the disk) has no used
  area to find: all of it is stored, read-only as one raw chunk, compressed in chunks from sector 0.
- No `'STR '` −16396, which names Disk Copy as the writer (6.5b13 writes none either). `'vers'` 1 is version 1.0
  final with "ClassicMac" as the short string and Disk Copy's text as the long one: "Mac™ OS HFS *n*K image", or
  "*n*K disk image" for another disk, then "CRC: $*xxxxxxxx*" on its own line when there is a CRC, so §3.1 can renew it.
- Type and creator are set (`'ddsk'`); the Finder flags are left 0.
- The new image is read back, with its checksum verified, before it is returned.

### 3.3 A segmented image

An image is cut into parts as Disk Copy 6.3.3 cuts it ([§1.6](#16-segmented-images)):

1. Its data fork is cut raw into parts of ceil(sectors / parts) sectors (sectors of the data fork, rounded up); the
   last part takes the rest. 2 to 128 parts, none empty.
2. Part 1 keeps the image's Finder info and its resources, the `'bcem'` made version 12 with `+$54` = 1 and its
   resource name dropped. The other parts are typed `'dseg'` (the creator kept) and hold only `'bcm#'` 128 and a
   `'vers'` 1 whose long string is "Part *n*/*M* of a disk image" (its numbers and short string taken from the
   image's `'vers'` 1).
3. Every part's `'bcm#'` 128 holds its number, the count, the image ID and the CRC28 of its own data fork (0 when the
   image has no CRC).
4. Parts are named "*name* *N*of*M*", *N* zero-padded to the width of *M*, the name cut so the whole fits 31 characters.

[Verified: the four parts of Disk Copy 6.3.3's segmented 5 MB image, made again from its whole image, have the same
names, types, data forks, `'bcem'` and `'bcm#'` numbers and CRCs]. The image ID is the date and 12 random bytes
[ClassicMac]: Disk Copy's mixes the date, the tick count, a random number and a CRC-32, and readers only compare it.

### 3.4 Editing a segmented image

A segmented image is edited through part 1 [ClassicMac], following §3.1 and §3.3:

1. The parts are found as Disk Copy finds them (§2.6), in part 1's folder and in its layout (AppleDouble or Basilisk
   II); a backup (`.orig`) is not a part. With a part missing the image is read only.
2. They are joined into the image they were cut from: their data forks back to back, part 1's resources without its
   `'bcm#'`, the map unflagged (version 12 stays). That image is made again around the edited disk (§3.1).
3. It is cut again into as many parts (§3.3), each keeping its name, Finder info, its other resources and the image ID;
   each `'bcm#'` gets its part's new CRC28. When the image has grown smaller than a sector per part, its data fork is
   padded with zeros to equal parts: chunks are found by their offsets, so the padding is never read.
4. Saved in place, every part is written over itself, each original kept as `.orig` the first time. Saved as a new
   file, part 1 is written where asked and the others beside it, named by its name with its " 1of*M*" ending (as
   wide as *M*) replaced by their numbers, or, without that ending, with " *N*of*M*" added.

[Verified: Disk Copy 6.3.3's own four-part image, edited and saved in place, reads back with the change; not yet
mounted in Disk Copy after the edit.]

## 4. Variants

### 4.1 Map versions

| Version | Written by | Source |
| --- | --- | --- |
| 2 | Disk Image Mounter 1.0.1 and Disk Copy 6.0.x (1995–96); no image seen | Inferred ([§8](#8-not-covered)) |
| 10 | Disk Copy 6.1–6.3.3: read/write, read-only, KenCode | [Verified: 6.1.2, 6.3.3] |
| 11 | Disk Copy 6.1–6.3.3: ADC (required for chunk type `$83`) | [Code: 6.3.3], [Verified: 6.1.2, 6.3.3] |
| 12 | Disk Copy 6.3.3 for segmented images; Disk Copy 6.5b13 for all images | [Code: 6.3.3], [Verified: 6.3.3; 6.5b13 read-only compressed] |

- Disk Copy 6.3.3 accepts 10, 11 and 12, and 2 with an "obsolete" warning; it refuses a version above 12 with −8818
  ("cannot be used with the currently installed version of the disk image driver") and any other with −8819 (damaged)
  [Code: 6.3.3].
- Disk Copy 6.1.2 accepts 2, 10 and 11, and refuses a version above 11 with −9 (too new) and others with −10 (damaged)
  [Code: 6.1.2].
- Disk Copy 6.5b13 turns the NDIF map into UDIF runs in memory ([udif.md §1](udif.md#1-layout)): `+$48` becomes the
  buffers-needed value (doubled for version 10) and types `$80`–`$83` become `$80000001`–`$80000004` [Code: 6.5b13].

### 4.2 Version 2

The old map layout, as **Disk Copy 6.1.2's driver** reads it [Code: 6.1.2], [Verified: 6.1.2 mounted hand-built
version 2 images, raw and KenCode, with the checksum valid, and refused ADC in them with −10]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$00` | `$54` | Header | As version 10 up to `+$54`: version, file-system ID, name, disk size, `+$48`, data start, CRC ([§1.2](#12-the-bcem-header)) |
| `+$54` | 4 | Entry count | `u32`, **the end entry included**; not a segmented flag |
| `+$58` | 8 × count | Entries | `start<<8\|type` (4 bytes), then the offset (`u32`) |

- The map is exactly `$58` + 8 × count bytes: a longer map is a warning, a shorter one or another count an error.
- A chunk's stored length is the next entry's offset minus its own; the end entry's length is 0. The stored bytes are
  therefore contiguous in entry order, and a zero chunk's offset is the next chunk's.
- Types `$00`, `$02`, `$80`–`$82` and `$FF`; `$83` is refused; segmented images are not allowed. `+$48` is the largest
  compressed chunk in sectors.
- 6.1.2 converts the map to version 11 in memory (its converter clears from `+$54`, so the count survives) and
  continues as for version 11.

The later readers are broken for version 2, so they are not the reference [Code], [Verified]:

- **Disk Copy 6.3.3** converts to version 12 but then clears ten longs from `+$60`, wiping the count it stored at
  `+$7C`: the table comes out empty and the disk reads as zeros (with "Verify checksum" on, it reports the CRC of
  zeros, `$0321EDEE`).
- **Disk Copy 6.5b13** computes the end entry's length from the long past the table and refuses the map (−8819,
  "chunk ends beyond end of data fork").
- **ShrinkWrap 2.1** ignores the version and always reads the count at `+$7C` and 12-byte entries at `+$80`, for files
  typed `'dimg'`/`'hdro'` with creator `'ddsk'`, reading the data fork sequentially and decoding `$80` through the
  System's `'dcmp'` 3. That contradicts the layout above ([§8](#8-not-covered)).

### 4.3 What Disk Copy writes

[Verified: 6.1.2, 6.3.3, 6.5b13]:

| Format | Type/creator | Version | Chunks | `+$48` | CRC |
| --- | --- | --- | --- | --- | --- |
| Read/Write | `'dimg'`/`'ddsk'` | 10 | One raw chunk | 0 | 0 |
| Read-Only | `'rohd'`/`'ddsk'` | 10 (12 from 6.5b13) | A raw chunk per used area, zero chunks for free space | 0 | Set |
| Read-Only Compressed | `'rohd'`/`'ddsk'` | 11 ADC, 10 KenCode (12 from 6.5b13) | Fixed-size compressed chunks, raw where they do not shrink | Chunk size + ceil(largest overrun / 512) | Set |

- On an HFS volume the compressed layouts keep sectors 0–3 as a raw chunk (unless 6.3.3's "Compress Volume Header" is
  on), then chunks up to the last used allocation block, a zero chunk, the alternate MDB (second-last sector) raw, and
  the last sector as zeros. Free space past the last used block is never stored.
- The chunk size is 32 sectors in Disk Copy 6.1.2 and 512 by default in 6.3.3 and 6.5b13; 6.3.3's hidden dialog
  allows others (20 and 64 were tried).
- The compressed layouts' used area ends with the last allocation block in use, and every block before it is stored:
  free blocks inside it keep their bytes (deleted files there survive), and only what lies past it is left out
  [Verified: 5 MB volumes before and after deleting files, 6.3.3]. Read-Only lays out the same chunks, but stores the
  used area as one raw chunk.
- **KenCode is chosen in a hidden dialog.** Holding Control while clicking Save in a Read-Only Compressed save shows
  "Override Image Parameters": Smaller (KC) or Faster (ADC, the default), Match Len (67), and in 6.3.3 Chunk Size (512)
  and Compress Volume Header [Verified: 6.1.2, 6.3.3]. 6.5b13 has no such dialog and writes only ADC.
- Disk Copy's encoders, for byte-identical output [Code: 6.1.2, 6.3.3], [Verified: every compressed chunk of the
  samples re-encodes exactly]: ADC with a 65,536-byte window and matches up to 67 bytes; KenCode with a `$2800`-byte
  window and matches up to 64 bytes in 6.3.3 (which ignores Match Len) or up to Match Len in 6.1.2.

### 4.4 Self-mounting and segmented images

A self-mounting image is NDIF with a mounting application in its resource fork ([§1.1](#11-files-and-resources)); a
segmented image is NDIF version 12 cut into parts ([§1.6](#16-segmented-images), [§2.6](#26-finding-a-segmented-images-parts)).

## 5. ClassicMac

- **New images**: `NdifWriter.Create` makes an image of a disk (§3.2) with `NdifCreateOptions` (`Format`:
  `ReadWrite`, `ReadOnly`, `Adc` (the default) or `KenCode`; `ChunkSectors`, 512 by default), and `NdifWriter.Split`
  cuts one into the parts of a segmented image (§3.3); the CLI's `ndif` writes them.
- **Writing**: an image of a plain HFS disk, given as an AppleDouble pair, a Basilisk II entry, or in MacBinary,
  AppleSingle or BinHex, is writable: the edit session (`InputEditSession`) edits the decoded disk as a volume
  ([hfs.md §3](../file-systems/hfs.md#3-writing)) and saves the image made again (§3.1) in the input's own layout, as a new
  file or in place (each file written kept as `.orig` the first time); a segmented image through part 1, every part
  written (§3.4). Its disk is not resized. The CLI's `check` runs the writer's checks on the disk.

- **Recognition** [ClassicMac]: a file whose resource fork (256 bytes or more) parses and holds a `'bcem'` 128 of at
  least `$58` bytes, whatever its version or file type. The data fork alone never says it is NDIF; reading a data fork
  without its resource fork throws. Where this reader comes in the unwrapper's order is in
  [unwrapping.md §2.1](../containers/unwrapping.md#21-reader-order).
- **Maps that are not read** (the read throws): a version above 12 (Disk Copy −8818) or other than 2, 10, 11 and 12
  (−8819); a map shorter than its header; fewer than two entries, by the count or by what the map holds; a disk of 0 or
  `$400000` sectors or more; the segmented flag below version 12 (all −8819 in Disk Copy); a disk larger than the
  expanded-bytes limit ([unwrapping.md §5](../containers/unwrapping.md#5-classicmac)).
- **Version 2** is read as Disk Copy 6.1.2 reads it ([§4.2](#42-version-2)) and reported as `ndif.version-2` (Info)
  with the file's type and creator, the long at `+$54` and the map size, asking for the image, because no real version
  2 image has been seen.
- **Map size**: a length that does not match the count is an error, except a version 10 map one entry off its count
  either way, which is a warning (Disk Copy warns only when the map has one entry more). The entries the map holds are
  read, up to the count.
- **Header**: a name over 63 bytes is reported and cut to 63; an empty name gives the output the file's own name. A
  CRC of `$FFFFFFFF` is reported. The name's 27-byte warning, a CRC of 0 in a read-only image, the file-system ID and
  the reserved longs are not checked. A data start past the data fork is reported (`ndif.bad-map`).
- **Entries**: ClassicMac reports every entry's problems wherever the entry is, rather than refusing the image
  ([§2.2](#22-per-chunk-errors-count-only-on-the-last-entry)):
  - the first `$FF` entry ends the map; with none, the last chunk runs to the disk's end (`ndif.no-end`); an end
    entry whose start is not the disk size is `ndif.bad-map`;
  - a first chunk starting after sector 0 is `ndif.gap`, and the sectors before it read as zeros;
  - a chunk ends at the next entry's start, or the disk's end if that is sooner; a start not after the previous one, or
    past the disk, is `ndif.bad-map`, and the map is read up to that entry;
  - a zero chunk that stores bytes is `ndif.zero-length`; the bytes are ignored;
  - a raw chunk storing fewer than its size is `ndif.short`, and the rest reads as zeros (one storing more is not
    reported); a chunk whose data run past the data fork is `ndif.short`, and what is there is read;
  - `$83` below version 11 is `ndif.bad-map`, and the chunk is still decoded;
  - a compressed chunk over `+$48` sectors is `ndif.chunk-size`: a warning when it fits the decode buffer (`+$48`,
    doubled for version 10), since 6.1.2 refuses the image and later versions read it, and an error when it does not,
    which no version can read. It is decoded either way;
  - `$F0` and any other unknown type are `ndif.unknown-chunk` and read as zeros.
- **Reading**: the disk is read on demand, one decoded chunk kept, as Disk Copy's driver does. A compressed chunk that
  fails to decode (ADC overrun, truncated input or match before the start; KenCode overread or match before the start;
  RLE or LZH short) is reported once as `ndif.bad-chunk`, and the rest of it reads as zeros. Each LZH chunk starts
  with a clear window ([lzhuf.md §2.3](../codecs/lzhuf.md#23-decoding-a-block)), and an LZH chunk one byte short is not
  reported.
- **Checksum**: checked only when asked (`ContainerReadOptions.VerifyChecksums`, the CLI's `--verify`), and only when
  `+$50` is not 0; a mismatch is `ndif.bad-checksum` and the disk is read anyway.
- **Segmented images**: part 1's `'bcm#'` 128 (at least 20 bytes) gives the ID, the count and its own number; without
  it, `ndif.missing-segment` is reported and only part 1's data fork is read. The other parts are found among the files
  beside the image (`ContainerContext.Siblings`) as Disk Copy finds them ([§2.6](#26-finding-a-segmented-images-parts)).
  The first part not found is reported (`ndif.missing-segment`) and the disk is read up to it; a part other than the
  last that is not a whole number of sectors is `ndif.bad-segment` and is read as it is.
- **Output**: one file named by the volume name, whose data fork is the disk.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `ndif.bad-checksum` | Error | The disk's CRC28 differs from `+$50` (only with `VerifyChecksums` / `--verify`) | Reads the disk | INVALID alert (130) with "Verify checksum" on; nothing otherwise |
| `ndif.bad-chunk` | Error | A compressed chunk fails to decode: ADC overrun, truncated input or match before the start; KenCode overread or match before the start; RLE or LZH short | The rest of the chunk reads as zeros; reported once per chunk | Overrun and overread: −8819 when read; before-start and truncated input: not checked, garbage |
| `ndif.bad-map` | Error | Data start past the data fork; last start ≠ disk size; a start out of order or past the disk; ADC in a map below version 11 | Reads up to the bad entry; ADC is still decoded | −8819 (6.1.2: −10); per-entry cases only on the last entry in 6.3.3 and 6.5b13 |
| `ndif.bad-name` | Error | Volume name over 63 bytes | Cuts it to 63 | −8819 |
| `ndif.bad-segment` | Error | A part other than the last is not a whole number of sectors | Reads it as it is | −39 |
| `ndif.chunk-size` | Warning | A compressed chunk covers more sectors than `+$48` (as stored; equal passes) but fits the decode buffer (`+$48`, doubled for version 10) | Decodes it | 6.1.2: damaged, −10 [Code] [Verified]; 6.3.3, 6.5b13: reads it [Code] [Verified: 6.5b13] |
| `ndif.chunk-size` | Error | A compressed chunk covers more sectors than the decode buffer | Decodes it | 6.1.2: −10 [Code]; 6.3.3, 6.5b13: fails at the first read of the chunk [Code] (−27 or −37 in 6.5b13 [Verified]) |
| `ndif.crc-uninitialized` | Warning | CRC is `$FFFFFFFF` | Nothing more | Warning |
| `ndif.gap` | Warning | The first chunk does not start at sector 0 | Those sectors read as zeros | Not checked; reads garbage |
| `ndif.map-size` | Warning | Version 10 map one entry off its count ("Disk Copy 6.0") | Reads the entries present | Warning (map one entry longer only) |
| `ndif.map-size` | Error | The map's length does not match its count | Reads the entries present | −8819 (6.1.2: −10) |
| `ndif.missing-segment` | Error | Segmented, but no `'bcm#'` 128, or a part not found in the folder | Reads the disk up to the missing part | −8821 |
| `ndif.no-end` | Warning | No `$FF` entry | The last chunk runs to the disk's end | Warning |
| `ndif.short` | Error | A raw chunk stores less than its size, or a chunk's data run past the data fork | Reads what is there, then zeros | −8819; only on the last entry in 6.3.3 and 6.5b13 |
| `ndif.unknown-chunk` | Error | A chunk type Disk Copy does not know (`$F0` named as ShrinkWrap's) | Reads it as zeros | −8820; only on the last entry in 6.3.3 and 6.5b13 |
| `ndif.version-2` | Info | A version 2 map, read as Disk Copy 6.1.2 reads it; asks for the image | Reads it | 6.1.2: "obsolete" warning; 6.3.3: blank disk; 6.5b13: −8819 |
| `ndif.zero-length` | Warning | A zero chunk stores bytes | Ignores them | Warning |

The codecs have no codes of their own (no `adc.` or `kencode.` codes): their failures surface as `ndif.bad-chunk`.

## 7. Verification

`NdifWriterTests.cs` covers §3: a changed chunk stored raw (bytes ADC cannot shrink) while the others keep their bytes,
a changed chunk compressed again in an ADC image and in a KenCode image, an image without compressed chunks keeping
changed ones raw, the CRC made again, an
unchanged disk giving the same data and map, a changed chunk's zero runs stored as zero chunks, a read/write image
staying raw with no CRC, and the refusals.
`NdifSessionTests.cs` edits images given as an AppleDouble pair (saved as a new pair and in place) and in MacBinary,
and reads the disk of every kind of image, also one whose data fork starts with the disk's raw boot blocks and MDB.
`NdifSegmentedTests.cs` covers §3.4: a segmented image made again in as many parts (read-only and ADC, the parts
given in any order), parts of another image or a missing part refused, an image grown smaller than its parts padded,
the session saving every part as new files and in place, an image missing a part read only, and, with the corpus,
Disk Copy's own four-part image edited in place.
`NdifCreateTests.cs` covers §3.2 and §3.3: the read-only layout, free space past the used area read as zeros, ADC and
KenCode chunks of a chosen size, a read/write image, a disk that is not HFS, the `'vers'` text, a split into parts
read back through its siblings, part names, and the refusals; and, with the corpus, `New_images_match_Disk_Copy_s_own`
(below).

Synthetic images, `tests/ClassicMac.Files.Tests/NdifTests.cs` (built by `NdifBuilder.cs`):

- `Chunks_decode_to_the_disk`: raw, zero and compressed chunks decode to the volume.
- `Compressed_chunks_over_the_buffer_size_are_reported`: the `+$48` warning and the decode-buffer error, version 10
  doubling, equal passing, and ADC below version 11.
- `Images_nest_through_the_unwrapper_to_the_volume`: a MacBinary-wrapped image unwraps to its HFS volume.
- `Segments_are_found_by_their_part_resource_not_their_names`: renamed, reordered parts among another image's parts.
- `The_checksum_is_verified_only_when_asked`, `Maps_Disk_Copy_refuses_are_not_read`,
  `Version_2_maps_are_read_as_Disk_Copy_6_1_2_reads_them`, `Unknown_chunks_read_as_zeros_and_are_reported`,
  `Damaged_data_is_reported`, `Files_without_a_chunk_map_are_not_NDIF`, and the codec tests `DART_RLE_chunks_decode`,
  `KenCode_streams_decode`, `ADC_opcodes_decode`.

Images made by Disk Copy in SheepShaver, read from the `CLASSICMAC_CORPUS` folders by file name (not committed):

- `S800 RW.img`, `S800 RO.img`, `S800 ADC.img`, `S800 ADC.smi`, `S5M RW.img`, `S5M RO.img`, `S5M ADC.img`,
  `S5M RO del.img`, `S5M ADC del.img` and the four-part `seg/S5M RO seg 1of4` (Disk Copy 6.3.3): each decodes to the
  read/write image of the same volume with its CRC28 matching (`Disk_Copy_images_decode_to_their_volumes`).
- `F800 KC.img` (Disk Copy 6.3.3, "Smaller (KC)"): KenCode chunks decode to the stored CRC
  (`Disk_Copy_KenCode_image_matches_its_checksum`).
- `uc.img` (Disk Copy 6.5b13, version 12, end entry offset 0): decodes to its volume
  (`Disk_Copy_6_5_image_decodes_to_its_volume`).
- `v2 raw.img`, `v2 kc.img`, `v2 kc badcrc.img`, `v2 adc.img`: hand-built version 2 images that Disk Copy 6.1.2
  mounted with a valid checksum (the ADC one refused with −10); read with their checksums, the bad CRC and ADC in
  version 2 reported (`Version_2_test_images_match_their_checksums`).
- The `ndiftest` folder: `control.img`, `badcrc.img`, `adc_flip.img`, `adc_garb.img`, and the segmented cases
  `seg_ok`, `seg_renamed`, `seg_missing3` and `seg_foreign2`, with what Disk Copy 6.3.3 did with each: a bad CRC or
  damaged ADC refused only with "Verify checksum" on, a missing or foreign part −8821, renamed parts mounted
  (`Damaged_images_are_reported_as_Disk_Copy_refuses_them`).
- `S800 RW.img` and `S5M RW.img` made into Read-Only and ADC images match `S800 RO.img`, `S800 ADC.img`, `S5M RO.img`
  and `S5M ADC.img` in data fork and map; `seg/S5M RO.img` split into four matches `seg/S5M RO seg 1of4` to `4of4`
  (`New_images_match_Disk_Copy_s_own`).

## 8. Not covered

- Rewriting version 2 images; a new image's KenCode chunks compared with Disk Copy's own (only ADC was); an edited
  segmented image mounted in Disk Copy.
- Checking rewritten images against Disk Copy itself (mounting one in SheepShaver with "Verify checksum" on).
- **Version 2's real layout.** No image from Disk Image Mounter or Disk Copy 6.0.x has been seen. Disk Copy 6.1.2's
  8-byte layout ([§4.2](#42-version-2)) was verified only on hand-built images; ShrinkWrap 2.1 reads such files with the
  12-byte layout. That version 2 was written by those two programs is inferred from the release history and from 6.1.2
  not writing it.
- **`'hdro'`** as the Disk Image Mounter era's read-only type is inferred from Disk Copy 6.1.2 naming it beside
  `'rohd'`.
- **Chunk type `$F0` and `+$74`/`+$78`** come only from Aaru; no image with them has been seen.
- **The `+$48` check in Disk Copy 6.3.3 and 6.5b13** ([§2.3](#23-the-48-check)). 6.1.2's refusal (−10) is run; 6.5b13
  mounting a version 10 image whose chunks pass `+$48` but fit the doubled buffer is run, and matches the traced rule
  that the validator's per-entry error is cleared. Not yet run: 6.3.3 (the emulator hung); the exact code 6.5b13 gives
  for the ADC image (−27 or −37); why its Check Image gives −50; and the equal case in Check Image, which hung the
  application.
- DiskSet images (`'hdc '`, `'hdcm'`) are named in Disk Copy 6.1.2's type table only; none has been seen.

## 9. References

1. Disk Copy 6.1.2, 6.3.3 and 6.5b13, Apple, traced in disassembly and run in SheepShaver.
2. Aaru (Natalia Portillo), GPL-3 (some files LGPL): NDIF chunk type `$F0` and the `+$74`/`+$78` fields; behaviour
   only.
3. ShrinkWrap 2.1: how it reads `'dimg'`/`'hdro'` files, from its disassembly; behaviour only.
4. Codecs: [adc.md](../codecs/adc.md), [kencode.md](../codecs/kencode.md), [dart-rle.md](../codecs/dart-rle.md),
   [lzhuf.md](../codecs/lzhuf.md).
