# Raw disk images (ShrinkWrap, DiskDup+ and others)

A raw image is a volume's or device's 512-byte sectors in order, with no header, tags or compression: the plainest
disk image, written by ShrinkWrap 2.1, DiskDup+, Disk Copy 6.5 (device images) and many other tools. This document also
holds what is shared by every Mac disk image: how Disk Copy chooses a format by file type. ClassicMac has no reader of
its own for raw images: the volume and partition-map readers open the data fork directly.

| | |
| --- | --- |
| Identified by | Types `'hdrv'` and `'DDim'` (any creator); `'APPL'` or `'adrp'` with creator `'sImg'`; `'APPL'`/`'iImg'`; any type Disk Copy does not know ([§2.1](#21-how-disk-copy-chooses-a-format)). No signature: the file system's own (`BD` at byte 1024 for HFS) or the partition map's |
| ClassicMac | Reads, through the volume readers (`ClassicMac.Files.Hfs`: `PartitionMapReader`, `HfsReader`, `MfsReader`, and the others in the unwrapper's order) |
| Verified against | ShrinkWrap 2.1 (floppy and 5 MB volume)<br>DiskDup+ 2.9.2 (an 800K HFS floppy)<br>Disk Copy 6.1.2, 6.3.3 and 6.5b13 (SheepShaver, Mac OS 9.0) |
| Sources | Disk Copy 6.1.2, 6.3.3 and 6.5b13 (disassembly: the type tables and the `.HDI` driver) |

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

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$0` | 512 × *n* | Sectors | The volume's (or device's) sectors 0 to *n* − 1, in order, byte for byte [Verified: ShrinkWrap 2.1, DiskDup+ 2.9.2] |

The resource fork is not needed to read the disk. A self-mounting image keeps the mounter's code there, and
ShrinkWrap's self-mounting floppy also a `'CKSM'` 1 holding the data checksum (the Disk Copy 4.2 sum,
[diskcopy42.md §1.2](diskcopy42.md#12-the-checksum)) [Verified: ShrinkWrap 2.1].

## 2. Reading

### 2.1 How Disk Copy chooses a format

Disk Copy picks the format **by file type (and creator) only**: its driver never looks at the data to decide
[Code: Disk Copy 6.1.2 `typ#` 128, 6.3.3's type table]. An unknown type is mounted as a raw volume if its data fork is
a nonzero multiple of 512 bytes, and is otherwise refused with −8816 [Code: Disk Copy 6.3.3].

| Type | Creator | Format | Source |
| --- | --- | --- | --- |
| `'dImg'` | `'dCpy'` (Disk Copy), `'Wrap'` (ShrinkWrap) | Disk Copy 4.2 ([diskcopy42.md](diskcopy42.md)) | [Code: 6.3.3], [Verified] |
| `'DMd1'`–`'DMd7'`, `'DMdf'` | `'DART'` | DART ([dart.md](dart.md)) | [Code: 6.1.2, 6.3.3] |
| `'dimg'` | `'ddsk'` | NDIF, read/write ([ndif.md](ndif.md)) | [Code: 6.3.3], [Verified] |
| `'rohd'` | `'ddsk'` | NDIF, read-only and read-only compressed (Disk Copy 6.1 on) | [Verified] |
| `'hdro'` | `'ddsk'` | NDIF, the older read-only type | [Code: 6.1.2 `kind` 128] |
| `'hdc '`, `'hdcm'` | | NDIF, DiskSet | [Code: 6.1.2] |
| `'dseg'` | `'ddsk'` | A later part of a segmented NDIF image | [Code: 6.3.3], [Verified] |
| `'APPL'` | `'oneb'` | Disk Copy self-mounting image (`.smi`), NDIF | [Code: 6.3.3], [Verified] |
| `'APPL'`, `'adrp'` | `'sImg'` | ShrinkWrap self-mounting floppy, raw | [Code: 6.1.2], [Verified] |
| `'APPL'` | `'iImg'` | ShrinkWrap self-mounting volume, raw | [Verified] |
| `'hdrv'`, `'DDim'` | any | Raw volume | [Code: 6.1.2], [Verified] |
| `'devi'`, `'devs'` | `'ddsk'` | UDIF device image, part of one ([udif.md](udif.md)) | [Code: 6.5b13 `kind` 128], [Verified] |
| `'devr'` | `'ddsk'` | Raw device image (UDIF read/write) | [Code: 6.5b13], [Verified] |
| `'GImg'`, `'PImg'` | | Toast and other device images | [Code: 6.5b13] |

Other Disk Copy results that apply to every format:

- −8812, "cannot be mounted from the disk it is presently on": Disk Copy 6.1.2 and 6.5b13 give it for an image on
  SheepShaver's shared host volume [Verified].
- Disk Copy 6.1.2 mounts nothing while File Exchange 3.0.3 is active, because that extension installs its own, newer
  `.HDI` driver, which 6.1.2 then uses [Verified].

### 2.2 Reading a raw image

1. Take the data fork as the volume or device, sector 0 first.
2. Open it by its own structures: a partition map ([partition-map.md](../file-systems/partition-map.md)), or a volume
   (`BD` at byte 1024 for HFS, [hfs.md](../file-systems/hfs.md); MFS, [mfs.md](../file-systems/mfs.md)) [Verified].

## 3. Writing

None.

## 4. Variants

ShrinkWrap 2.1 (1996) writes no format of its own [Verified: ShrinkWrap 2.1 in SheepShaver, floppy and 5 MB volume]:

| Option | Type/creator | Data fork | Resource fork |
| --- | --- | --- | --- |
| ShrinkWrap Image (floppy) | `'dImg'`/`'Wrap'` | Disk Copy 4.2, byte for byte; tag size 0 | None |
| DiskCopy Image (floppy) | `'dImg'`/`'dCpy'` | Disk Copy 4.2, with junk after the name ([diskcopy42.md §1.1](diskcopy42.md#11-the-data-fork)) | `'dCpy'` 0: the checksums as text |
| ShrinkWrap Image (volume) | `'hdrv'`/`'Wrap'` | The raw volume | None |
| Drive Container | `'hdrv'`/`'D:\>'` | The raw volume | None |
| Self-Mounting (floppy) | `'APPL'`/`'sImg'` | The raw volume | Mounter code; `'CKSM'` 1 = data checksum |
| Self-Mounting (volume) | `'APPL'`/`'iImg'` | The raw volume | Mounter code; no checksum |

The three volume forms have byte-identical data forks [Verified].

DiskDup+ 2.9.2 (creator `'DDp+'`) saves a "DiskDup+" image as type `'DDim'`: the raw disk, byte-identical to the
source volume [Verified: an 800K HFS floppy saved by DiskDup+ 2.9.2 in SheepShaver]. Its "Disk Copy" option writes
Disk Copy 4.2 ([diskcopy42.md](diskcopy42.md)).

Disk Copy 6.5b13's read/write device images (`'devr'`) and CD-R master images are raw devices; they are described with
UDIF ([udif.md §4](udif.md#4-variants)).

## 5. ClassicMac

- ClassicMac does not use file types to choose a reader, because images copied through other systems lose them. The
  readers are tried in the unwrapper's order ([unwrapping.md §2.1](../containers/unwrapping.md#21-reader-order)):
  UDIF, the partition map, Disk Copy 4.2, NDIF and DART come before the volume readers, so a raw image is whatever
  reaches the volume readers with a file system they recognise [ClassicMac].
- A raw image's resource fork (a self-mounting image's mounter, `'CKSM'` 1) is not read or checked.

## 6. Diagnostics

None. Problems in a raw image are reported by the volume or partition-map reader that opens it.

## 7. Verification

- No raw-image fixture is in the repository. The HFS, MFS and partition-map tests
  (`tests/ClassicMac.Files.Tests/HfsTests.cs`, `MfsTests.cs`, `PartitionMapTests.cs`) read bare volumes and devices,
  which is what these images are.
- ShrinkWrap 2.1's outputs and DiskDup+ 2.9.2's `'DDim'` image were made in SheepShaver and opened by ClassicMac's
  volume readers [Verified]; they are not in the repository.

## 8. Not covered

- Disk Copy 6.0 of 1994 (creator `'dCpy'`, a floppy duplicator unrelated to NDIF) writes its own RLE-compressed and
  self-extracting `'dImg'` variants; they are not specified or read [Code: its disassembly shows no NDIF code].
- Disk Copy's type table is read only for Disk Copy 6.1.2, 6.3.3 and 6.5b13.
- Writing raw images.

## 9. References

1. Disk Copy 6.1.2, 6.3.3 and 6.5b13, Apple, traced in disassembly and run in SheepShaver.
2. ShrinkWrap 2.1 and DiskDup+ 2.9.2, run in SheepShaver.
