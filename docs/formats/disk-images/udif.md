# UDIF

The Universal Disk Image Format replaced NDIF from Disk Copy 6.4 and 6.5 (2000–2002) and is Mac OS X's `.dmg`.
ClassicMac reads it ([§6](#6-how-classicmac-reads-udif)). What follows was verified on **early UDIF images made
by Disk Copy 6.5b13** (Mac OS 9.0 in SheepShaver, patched to run below 9.1): read-only compressed (ADC), read-only and
read/write device images of an Apple partition map with an 800K HFS volume, plus "entire device" and CD-R master
images of the same device, each decoded back to the source device exactly. Facts only dmg2img (GPL, behavioural
reference) gives are marked so: the XML property list and the zero, zlib, bzip2, LZFSE and comment runs of Mac OS X's
images.

Disk Copy 6.5b13 offers UDIF formats only when saving **device images** (types `devi`, `devr`, `devs`, `GImg`,
`PImg`); its menu lists read/write, read-only, read-only compressed, read-only (entire device), CD-R master and two
"OBSOLETE (6.4d50)" formats [Code: 6.5b13], [Verified]. Disk Copy 6.3.3 has no UDIF code at all [Code: 6.3.3].

Contents

1. [The `koly` trailer](#1-the-koly-trailer)
2. [Where the block tables are](#2-where-the-block-tables-are)
3. [`mish` block tables](#3-mish-block-tables)
4. [Checksums](#4-checksums)
5. [Other UDIF files](#5-other-udif-files)
6. [How ClassicMac reads UDIF](#6-how-classicmac-reads-udif)
7. [Diagnostics](#7-diagnostics)
8. [Open questions](#8-open-questions)

---

## 1. The `koly` trailer

The last 512 bytes of the data fork [Verified: 6.5b13; the layout agrees with dmg2img]:

| Offset | Size | Type | Meaning | 6.5b13 value |
| --- | --- | --- | --- | --- |
| `+$000` | 4 | `OSType` | `'koly'` | |
| `+$004` | 4 | `u32` | Version | 4 |
| `+$008` | 4 | `u32` | Header size | 512 |
| `+$00C` | 4 | `u32` | Flags: bit 0 = the resource fork is flattened into the data fork; bit 1 = every data run is raw ([§4](#4-checksums)) | 1; 3 for "entire device" |
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

- **Bit 0**: the resource fork is stored in the data fork, at the resource fork offset
  ([§2](#2-where-the-block-tables-are)). Disk Copy's flatten routine sets it, its unflatten routine clears it,
  and opening an image tests it.
- **Bit 1**: every data run is raw, so the data fork is a sector-for-sector copy of the device. The image builder
  starts with it set and clears it at the first run that is not raw; when it is set, Disk Copy skips the run tables
  and reads the data fork directly. (The name is ClassicMac's; the code only tests the bit.)

---

## 2. Where the block tables are

- **Early images (Disk Copy 6.4/6.5):** XML offset and length are 0. The resource fork offset and length point to a
  **complete classic resource fork stored inside the data fork** ([resource-fork.md](../resources/resource-fork.md)). It holds one
  `blkx` resource per partition (−1 "DDM", 0 the partition map, 1 the HFS partition, named after the partition), and
  `plst` 0, `srci` 0, `vers` 1 and `rem ` 0 and 1 [Verified: 6.5b13]. The file's real resource fork holds only a
  `vers` 1 [Verified]. A reader parses the embedded fork's map and takes the `blkx` resources; dmg2img instead walks
  the resource data from a fixed offset, which only works for its layout.
- **Later images** keep the same `mish` tables base64-encoded in an XML property list at the XML offset
  [dmg2img, behavioural reference only].

---

## 3. `mish` block tables

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
| `$80000004` | ADC ([adc.md](../codecs/adc.md)), the NDIF codec | [Verified: 6.5b13] |
| `$80000005` | zlib | dmg2img only |
| `$80000006` | bzip2 ([bzip2.md](../codecs/bzip2.md)) | dmg2img only |
| `$80000007` | LZFSE | dmg2img only |
| `$7FFFFFFE` | Comment | dmg2img only |
| `$FFFFFFFF` | End of the table (its first sector = the sector count) | [Verified: 6.5b13] |

Disk Copy 6.5b13's HFS layout mirrors NDIF's: sectors 0–3, the used data, free space, the alternate MDB, free space
[Verified]. The compressed image packs the partitions' data tightly (data offsets 0, 35, 1,559); the read-only one
aligns them (0, 512, 32,768) [Verified].

---

## 4. Checksums

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
([§1](#1-the-koly-trailer)) [Code: 6.5b13].

---

## 5. Other UDIF files

- **Read/write device images** (`devr`) are the raw device, with no `koly`; the `blkx`, `plst` and other resources
  are in the real resource fork [Verified: 6.5b13]. Read them as raw.
- **CD-R master images** (`GImg`/`CDr3`, the Toast type and creator) are the raw device too, with no `koly` or
  `blkx`; the resource fork holds only `vers` [Verified: 6.5b13]. Read them as raw.
- **Encrypted images** are recognised and refused, not parsed: `encrcdsa` at offset 0 is encryption header version
  2, `cdsaencr` in the last bytes of the file is version 1 [VileFault, behavioural reference; Mac OS X 10.2 and
  later].

---

## 6. How ClassicMac reads UDIF

Disk Copy 6.5b13's read-only, read-only compressed (ADC) and "entire device" (MD5) images decode to their source
device byte for byte, with every checksum matching [Verified: 6.5b13]. The Mac OS X parts — the XML property list and
zero, zlib, bzip2 and comment runs — follow dmg2img (GPL, behavioural reference only) and are tested on synthetic
images only: no `.dmg` made by Mac OS X is in the corpus.

- **Recognition.** A data fork of at least 512 bytes whose last 512 bytes start with `'koly'`, or an encrypted image
  (`encrcdsa` at offset 0, `cdsaencr` in the last 8 bytes, [§5](#5-other-udif-files)). UDIF is tried right after
  BinHex and **before the partition-map reader**: an image whose runs are all raw starts with the device's driver
  descriptor, which the partition-map reader would otherwise take [ClassicMac]. The `koly` version and flags are not
  checked or used; the runs are always read through the tables [ClassicMac].
- **Refused** (the read throws): encrypted images; a segment count above 1 (segmented UDIF is not read yet); no
  `blkx` tables; a property list or embedded resource fork lying outside the file, a property list over 64 MiB, XML
  or base64 that does not parse; a device over the configured expansion limit.
- **The tables.** When the `koly` XML length is nonzero, from the property list: the top `dict`'s `resource-fork` key,
  its `blkx` array, each entry's `Data` (base64, whitespace ignored) and `ID` [dmg2img]. Otherwise from the embedded
  resource fork's `blkx` resources ([§2](#2-where-the-block-tables-are)). Tables are taken **in ID order**, the
  order the master checksum uses. A table shorter than `$CC` bytes or not starting `'mish'` is skipped
  (`udif.bad-table`).
- **Runs → disk.** A run covers device sectors from (table first sector + run first sector) for its sector count;
  its bytes are at **`koly` data fork offset + table data offset + run offset** in the data fork, for its stored
  length. Runs from every table are sorted by start; an overlap is reported (`udif.bad-table`) and the later run
  wins. The end run (`$FFFFFFFF`) stops a table (missing: `udif.bad-table`, a warning); comment runs (`$7FFFFFFE`) and
  runs of 0 sectors are skipped. A run count larger than the table holds is cut to what it holds.
- **Reading.** The disk is read on demand, through the chunked disk NDIF uses too ([ndif.md §3.9](ndif.md#39-reading-the-disk)):
  zero (`$0`) and free (`$2`) runs read as zeros; raw runs straight from the file; ADC ([adc.md](../codecs/adc.md)), zlib (.NET's
  `ZLibStream`) and bzip2 ([bzip2.md](../codecs/bzip2.md)) runs are decoded one whole run at a time, the last one kept. A run that
  does not decode to exactly its sector count × 512 bytes is reported once (`udif.bad-run`) and the rest of it reads
  as zeros; stored bytes past the end of the file are reported and read as far as they go; sectors no run covers read
  as zeros [ClassicMac].
- **LZFSE** (`$80000007`) and unknown run types are reported (`udif.unsupported-run`) and read as zeros
  [ClassicMac].
- **Device length:** the `koly` sector count × 512, or, when that is 0, the end of the furthest table.
- **Output:** one file whose data fork is the device, for the next readers (usually the partition map). It is named
  after the host file without its extension, else "Disk image" [ClassicMac].
- **Checksums** are verified only on request (`VerifyChecksums`, the CLI's `--verify`), each with the algorithm its
  field names — type 2 CRC-32 (zlib's, stored big-endian), type 4 MD5 ([§4](#4-checksums)); any mismatch is
  `udif.bad-checksum`:
  - each table's over the sectors its runs store: raw and compressed runs, in run order; zero, free, LZFSE and
    unknown runs are skipped;
  - the master over the tables' computed checksums in ID order;
  - the data over the data fork from its offset for its length.

  A table whose checksum type is neither 2 nor 4 stops the verification [ClassicMac].

---

## 7. Diagnostics

Every problem is reported with a code and a severity ([README.md](../README.md#diagnostics)). The codecs emit none of
their own: their failures surface as `dart.bad-block`, `ndif.bad-chunk` or `udif.bad-run`. There are no `adc.`,
`kencode.` or `bzip2.` codes.

| Code | Severity | Meaning | ClassicMac | Disk Copy |
| --- | --- | --- | --- | --- |
| `udif.bad-table` | Error | A `blkx` that is not a `mish` table; a run count larger than the table holds; runs of two tables (or one) overlapping | skips the table; reads the runs it holds; the later run wins | not traced |
| `udif.bad-table` | Warning | A table with no end run | reads its runs | not traced |
| `udif.bad-run` | Error | A run's stored bytes pass the end of the file; a compressed run over 2 GiB; a run that fails to decode (ADC, zlib or bzip2 damage, or a size other than its sectors') | reads what is there; the rest of the run reads as zeros; reported once per run | not traced |
| `udif.unsupported-run` | Error | An LZFSE run, or a run type ClassicMac does not know | reads it as zeros | not traced |
| `udif.bad-checksum` | Error | A table's, the master or the data checksum differs (only with `VerifyChecksums` / `--verify`) | reads the disk | not traced |

---

## 8. Open questions

- **UDIF from Mac OS X.** The XML property list and zlib, bzip2, zero and comment runs are known only from dmg2img
  and tested only on synthetic images; no `.dmg` made by Mac OS X is in the corpus. LZFSE runs are not decoded.
  Later `koly` versions and segmented UDIF images (segment count above 1, refused) have not been seen.
