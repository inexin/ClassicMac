# UDIF

The Universal Disk Image Format replaced NDIF ([ndif.md](ndif.md)) from Disk Copy 6.4 and 6.5 (2000–2002) and is Mac OS
X's `.dmg`. A 512-byte `koly` trailer ends the data fork; `mish` block tables, one per partition, map the device's
sectors onto runs stored in the data fork (zeros, raw, or compressed). Apple published no specification. ClassicMac
reads an image as one file whose data fork is the device, for the partition-map reader
([partition-map.md](../file-systems/partition-map.md)) or a volume reader to open next.

| | |
| --- | --- |
| Identified by | `'koly'` at the start of the last 512 bytes of the data fork; types `'devi'`, `'devs'` (creator `'ddsk'`) in Disk Copy 6.5 ([raw-images.md §2.1](raw-images.md#21-how-disk-copy-chooses-a-format)); extension `.dmg`. Encrypted images: `encrcdsa` at offset 0 or `cdsaencr` at the end |
| ClassicMac | Reads; `ClassicMac.Files.Hfs.UdifReader` |
| Verified against | Disk Copy 6.5b13 (Mac OS 9.0 in SheepShaver, patched to run below 9.1): read-only compressed (ADC), read-only and read/write device images of an Apple partition map with an 800K HFS volume, and "entire device" and CD-R master images of the same device, each decoded back to the source device exactly<br>No image made by Mac OS X |
| Sources | Disk Copy 6.5b13 (disassembly); dmg2img and VileFault for Mac OS X's additions, behaviour only |

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

### 1.1 The `koly` trailer

The last 512 bytes of the data fork [Verified: 6.5b13; the layout agrees with dmg2img]. A checksum field is a `u32`
type, a `u32` size in bits, then 128 bytes holding the value left-aligned ([§1.5](#15-checksums)).

| Offset | Size | Field | Notes (the value Disk Copy 6.5b13 writes) |
| --- | --- | --- | --- |
| `+$000` | 4 | Signature | `'koly'` |
| `+$004` | 4 | Version | `u32`: 4 |
| `+$008` | 4 | Header size | `u32`: 512 |
| `+$00C` | 4 | Flags | `u32`: 1; 3 for "entire device" (below) |
| `+$010` | 8 | Running data fork offset | `u64`: 0 |
| `+$018` | 8 | Data fork offset | `u64`: where the runs' data start; 0 |
| `+$020` | 8 | Data fork length | `u64`: up to the embedded resource fork |
| `+$028` | 8 | Resource fork offset | `u64`: the data's end |
| `+$030` | 8 | Resource fork length | `u64`: 4,509–4,589 |
| `+$038` | 4 | Segment number | `u32`: 1 |
| `+$03C` | 4 | Segment count | `u32`: 1 |
| `+$040` | 16 | Segment ID | |
| `+$050` | 4 | Data checksum type | `u32`: 2 = CRC-32 |
| `+$054` | 4 | Data checksum size | `u32`, in bits: 32 |
| `+$058` | 128 | Data checksum | Left-aligned |
| `+$0D8` | 8 | XML property list offset | `u64`: **0** |
| `+$0E0` | 8 | XML property list length | `u64`: **0** |
| `+$0E8` | 120 | Reserved | Zeros |
| `+$160` | 4 | Master checksum type | `u32`: 2 |
| `+$164` | 4 | Master checksum size | `u32`, in bits: 32 |
| `+$168` | 128 | Master checksum | Left-aligned |
| `+$1E8` | 4 | Image variant | `u32`: 1 |
| `+$1EC` | 8 | Sector count | `u64`: the device's |
| `+$1F4` | 12 | Reserved | Zeros |

The flags [Code: 6.5b13]:

- **Bit 0**: the resource fork is stored in the data fork, at the resource fork offset
  ([§1.2](#12-where-the-block-tables-are)). Disk Copy's flatten routine sets it, its unflatten routine clears it, and
  opening an image tests it.
- **Bit 1**: every data run is raw, so the data fork is a sector-for-sector copy of the device. The image builder starts
  with it set and clears it at the first run that is not raw; when it is set, Disk Copy skips the run tables and reads
  the data fork directly. (The name is ClassicMac's; the code only tests the bit.)

### 1.2 Where the block tables are

- **Early images (Disk Copy 6.4/6.5):** the XML offset and length are 0. The resource fork offset and length point to a
  **complete classic resource fork stored inside the data fork** ([resource-fork.md](../resources/resource-fork.md)).
  It holds one `'blkx'` resource per partition (−1 "DDM", 0 the partition map, 1 the HFS partition, named after the
  partition), and `'plst'` 0, `'srci'` 0, `'vers'` 1 and `'rem '` 0 and 1 [Verified: 6.5b13]. The file's real
  resource fork holds only a `'vers'` 1 [Verified]. A reader parses the embedded fork's map and takes the `'blkx'`
  resources; dmg2img instead walks the resource data from a fixed offset, which works only for its layout.
- **Later images** keep the same `mish` tables base64-encoded in an XML property list at the XML offset: the top
  `dict`'s `resource-fork` key, its `blkx` array, each entry's `Data` (base64) and `ID` [Reference: dmg2img].

### 1.3 `mish` block tables

Each `'blkx'` resource's data is a `mish` table [Verified: 6.5b13]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$00` | 4 | Signature | `'mish'` |
| `+$04` | 4 | Version | `u32`: 1 |
| `+$08` | 8 | First sector | `u64`: the partition's first sector on the device |
| `+$10` | 8 | Sector count | `u64` |
| `+$18` | 8 | Data offset | `u64`: the runs' offsets count from here |
| `+$20` | 4 | Buffers needed | `u32`, in sectors: 513 for ADC, 0 uncompressed |
| `+$24` | 4 | Block descriptor | `i32`: the `'blkx'` resource ID |
| `+$28` | 24 | Reserved | |
| `+$40` | 4 | Checksum type | `u32`: 2 = CRC-32 |
| `+$44` | 4 | Checksum size | `u32`, in bits: 32 |
| `+$48` | 128 | Checksum | Left-aligned |
| `+$C8` | 4 | Run count | `u32` |
| `+$CC` | `$28` × count | Runs | [§1.4](#14-runs) |

### 1.4 Runs

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$00` | 4 | Type | `u32` (below) |
| `+$04` | 4 | Comment | `u32`, reserved (0) |
| `+$08` | 8 | First sector | `u64`, relative to the table's first sector |
| `+$10` | 8 | Sector count | `u64` |
| `+$18` | 8 | Stored offset | `u64`, relative to the table's data offset |
| `+$20` | 8 | Stored length | `u64` |

| Type | Meaning | Source |
| --- | --- | --- |
| `$00000000` | Zeros | [Reference: dmg2img] |
| `$00000001` | Raw | [Verified: 6.5b13] |
| `$00000002` | Free space ("ignore"): not stored, reads as zeros; also used with a count of 0 as a marker | [Verified: 6.5b13] |
| `$80000004` | ADC ([adc.md](../codecs/adc.md)), the NDIF codec | [Verified: 6.5b13] |
| `$80000005` | zlib | [Reference: dmg2img] |
| `$80000006` | bzip2 ([bzip2.md](../codecs/bzip2.md)) | [Reference: dmg2img] |
| `$80000007` | LZFSE | [Reference: dmg2img] |
| `$7FFFFFFE` | Comment | [Reference: dmg2img] |
| `$FFFFFFFF` | End of the table (its first sector = the sector count) | [Verified: 6.5b13] |

### 1.5 Checksums

Each checksum field names its algorithm [Verified: 6.5b13]: type 2 is the standard (zlib) CRC-32, 32 bits, stored
big-endian, **not** NDIF's CRC28 ([ndif.md §1.5](ndif.md#15-the-checksum-crc28)); type 4 is MD5, 128 bits. The
read-only, read-only compressed and read/write images use CRC-32; the read-only "entire device" image (`'devi'`) uses
MD5 in every field.

| Field | With CRC-32 | With MD5 ("entire device", every sector stored as raw runs) |
| --- | --- | --- |
| Each `mish` checksum | Covers **only the sectors its runs store**, in run order: free runs are skipped, so for HFS it is not the CRC of the whole partition | The partition's sectors |
| `koly` data checksum | The data fork up to the data fork length (the embedded resource fork excluded) | The data fork, which is the device |
| `koly` master checksum | The CRC-32 of the `mish` checksums' big-endian 4-byte values in `'blkx'` order (−1, 0, 1); the `'vers'` text repeats it as "CRC32 $…" | The MD5 of the partitions' 16-byte MD5s concatenated in `'blkx'` order; the `'vers'` text shows it as "MD5 $…" |

Verifying is optional: decoding never depends on it.

## 2. Reading

1. Read the `koly` trailer from the last 512 bytes ([§1.1](#11-the-koly-trailer)).
2. Find the block tables: the XML property list when its length is not 0, otherwise the embedded resource fork's
   `'blkx'` resources ([§1.2](#12-where-the-block-tables-are)). Take them in ID order, the order the master checksum
   uses.
3. For each run of each table ([§1.4](#14-runs)): it covers the device sectors from (table first sector + run first
   sector) for its sector count; its stored bytes are at **`koly` data fork offset + table data offset + run offset**
   in the data fork, for its stored length. The end run stops the table.
4. Zero and free runs read as zeros; raw runs are copied; compressed runs are decoded, each whole run to its sector
   count × 512 bytes.
5. The device is the `koly` sector count × 512 bytes.

When flag bit 1 is set, Disk Copy skips the tables and reads the data fork as the device ([§1.1](#11-the-koly-trailer))
[Code: 6.5b13]. Disk Copy 6.5b13 also reads NDIF images by turning their maps into runs
([ndif.md §4.1](ndif.md#41-map-versions)).

## 3. Writing

None.

## 4. Variants

- **Disk Copy 6.5b13** offers UDIF formats only when saving **device images** (types `'devi'`, `'devr'`, `'devs'`,
  `'GImg'`, `'PImg'`); its menu lists read/write, read-only, read-only compressed, read-only (entire device), CD-R master
  and two "OBSOLETE (6.4d50)" formats [Code: 6.5b13], [Verified]. Disk Copy 6.3.3 has no UDIF code at all
  [Code: 6.3.3].
- Its HFS layout mirrors NDIF's ([ndif.md §4.3](ndif.md#43-what-disk-copy-writes)): sectors 0–3, the used data, free
  space, the alternate MDB, free space [Verified]. The compressed image packs the partitions' data tightly (data offsets
  0, 35, 1,559); the read-only one aligns them (0, 512, 32,768) [Verified].
- The "entire device" image uses MD5 everywhere ([§1.5](#15-checksums)) and has `koly` flags 3 where the others have
  1: bit 1 says every run is raw [Verified: 6.5b13], [Code: 6.5b13].
- **Read/write device images** (`'devr'`) are the raw device, with no `koly`; the `'blkx'`, `'plst'` and other
  resources are in the real resource fork [Verified: 6.5b13]. Read them as raw ([raw-images.md](raw-images.md)).
- **CD-R master images** (`'GImg'`/`'CDr3'`, the Toast type and creator) are the raw device too, with no `koly` or
  `'blkx'`; the resource fork holds only `'vers'` [Verified: 6.5b13]. Read them as raw.
- **Mac OS X images** keep the tables in the XML property list and add zero, zlib, bzip2, LZFSE and comment runs
  [Reference: dmg2img].
- **Encrypted images** (Mac OS X 10.2 and later): `encrcdsa` at offset 0 is encryption header version 2, `cdsaencr` in
  the last bytes of the file is version 1 [Reference: VileFault].

## 5. ClassicMac

- **Recognition** [ClassicMac]: a data fork of at least 512 bytes whose last 512 bytes start with `'koly'`, or an
  encrypted image (`encrcdsa` in the first 8 bytes, `cdsaencr` in the last 8). UDIF is tried before the partition-map
  reader ([unwrapping.md §3.1](../containers/unwrapping.md#31-readers-and-order)), because an image whose runs are all
  raw starts with the device's driver descriptor, which the partition-map reader would otherwise take. The `koly`
  version and flags are not checked or used; the runs are always read through the tables.
- **Refused** (the read throws): encrypted images; a segment count above 1 (segmented UDIF); no `'blkx'` tables; a
  property list or embedded resource fork lying outside the file; a property list over 64 MiB; XML or base64 that does
  not parse; a device over the expanded-bytes limit ([unwrapping.md §3.3](../containers/unwrapping.md#33-expanded-bytes-limit)).
- **Tables**: from the property list (whitespace in the base64 ignored; an entry without `ID` numbered by its place),
  or from the embedded resource fork, in ID order. A table shorter than `$CC` bytes or not starting `'mish'` is skipped
  (`udif.bad-table`). A run count larger than the table holds is cut to what it holds (`udif.bad-table`). A table
  with no end run is a warning (`udif.bad-table`).
- **Runs**: comment runs and runs of 0 sectors are skipped. Runs from every table are sorted by start; an overlap is
  reported (`udif.bad-table`) and the run starting later wins from its start. Zero and free runs read as zeros; raw
  runs straight from the file; ADC ([adc.md](../codecs/adc.md)), zlib (.NET's `ZLibStream`) and bzip2
  ([bzip2.md](../codecs/bzip2.md)) runs are decoded one whole run at a time, the last one kept. A compressed run over
  2 GiB decoded reads as zeros (`udif.bad-run`).
- **Damage**: stored bytes past the end of the file are reported (`udif.bad-run`) and read as far as they go; a run that
  does not decode to exactly its sector count × 512 bytes is reported once (`udif.bad-run`) and the rest of it reads as
  zeros; sectors no run covers read as zeros.
- **LZFSE** (`$80000007`) and unknown run types are reported (`udif.unsupported-run`) and read as zeros.
- **Device length**: the `koly` sector count × 512, or, when that is 0, the end of the furthest table.
- **Output**: one file whose data fork is the device, named after the host file without its extension, else
  "Disk image".
- **Checksums** are verified only on request (`ContainerReadOptions.VerifyChecksums`, the CLI's `--verify`), each with
  the algorithm its field names ([§1.5](#15-checksums)); any mismatch is `udif.bad-checksum`:
  - each table's over the sectors its raw and compressed runs store, in run order (zero, free, LZFSE and unknown runs
    skipped); a table whose checksum type is neither 2 nor 4 stops the whole verification;
  - the master over the tables' computed checksums in ID order;
  - the data over the data fork from its offset for its length (skipped when the length is 0).

  A master or data checksum of another type is not checked.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `udif.bad-checksum` | Error | A table's, the master or the data checksum differs (only with `VerifyChecksums` / `--verify`) | Reads the disk | Not traced |
| `udif.bad-run` | Error | A run's stored bytes pass the end of the file; a compressed run over 2 GiB; a run that fails to decode (ADC, zlib or bzip2 damage, or a size other than its sectors') | Reads what is there; the rest of the run reads as zeros; reported once per run | Not traced |
| `udif.bad-table` | Warning | A table with no end run | Reads its runs | Not traced |
| `udif.bad-table` | Error | A `'blkx'` that is not a `mish` table; a run count larger than the table holds; runs of two tables (or one) overlapping | Skips the table; reads the runs it holds; the later run wins | Not traced |
| `udif.unsupported-run` | Error | An LZFSE run, or a run type ClassicMac does not know | Reads it as zeros | Not traced |

The codecs have no codes of their own (no `adc.` or `bzip2.` codes): their failures surface as `udif.bad-run`.

## 7. Verification

- `tests/ClassicMac.Files.Tests/UdifTests.cs`, images built by `UdifBuilder.cs`:
  - `Every_run_type_decodes_and_the_checksums_match`: zero, free, raw, ADC, zlib and bzip2 runs, with the tables in an
    embedded resource fork (CRC-32 and MD5) and in an XML property list.
  - `Damaged_data_fails_its_checksums`: one flipped byte fails the table, master and data checksums, and only when
    verifying.
  - `LZFSE_runs_read_as_zeros_with_an_error`, `Encrypted_images_are_recognised_and_refused`.
  - `Images_unwrap_to_their_volumes`: a `.dmg` of a partitioned disk unwraps through the partition map to its HFS file.
- `Disk_Copy_6_5_images_decode_to_their_device`: with `CLASSICMAC_CORPUS` set, Disk Copy 6.5b13's read-only compressed
  (`uco`), read-only (`uro`) and "entire device" (`ued`) images decode to the device `dev800_ref.bin` with every
  checksum matching. Not committed.
- The Mac OS X parts (the XML property list; zero, zlib, bzip2 and comment runs) are tested on synthetic images only.

## 8. Not covered

- Writing UDIF images.
- **UDIF from Mac OS X.** The XML property list and the zlib, bzip2, zero and comment runs are known only from dmg2img
  and tested only on synthetic images; no `.dmg` made by Mac OS X is in the corpus. LZFSE runs are not decoded.
- Later `koly` versions and segmented UDIF images (segment count above 1, refused) have not been seen.
- Encrypted images are recognised, not decrypted.
- What Disk Copy does with damaged UDIF images was not traced.

## 9. References

1. Disk Copy 6.3.3 and 6.5b13, Apple, traced in disassembly and run in SheepShaver.
2. dmg2img (Lekensteyn), GPL: the XML property list and the zero, zlib, bzip2, LZFSE and comment runs; behaviour only.
3. libdmg-hfsplus (planetbeing), GPL: a behavioural reference for UDIF; no rule here rests on it alone.
4. VileFault (vfdecrypt), licence not recorded: the two encryption signatures; behaviour only.
