# Disk Copy 4.2

Disk Copy 4.2 images hold a floppy disk: a short header, the disk's 512-byte sectors in order, then the 12 tag bytes
each sector carried. Apple's Disk Copy 4.x wrote them, Disk Copy 6 still writes them for floppy sizes, and ShrinkWrap
and DiskDup+ write them as an option. ClassicMac reads an image as one file whose data fork is the disk, for the HFS or
MFS reader ([hfs.md](../file-systems/hfs.md), [mfs.md](../file-systems/mfs.md)) to open next.

| | |
| --- | --- |
| Identified by | Type `'dImg'`, creator `'dCpy'` (Disk Copy) or `'Wrap'` (ShrinkWrap); `$0100` at `+$52` of the data fork |
| ClassicMac | Reads; `ClassicMac.Files.Hfs.DiskCopy42Reader` |
| Verified against | Disk Copy 6.1.2's and 6.3.3's images (SheepShaver, Mac OS 9.0)<br>ShrinkWrap 2.1's images<br>The Disk Copy 4.2 image in DART 1.5.3's sample set |
| Sources | Apple File Type Note $E0/$0005; Disk Copy 6.3.3 (disassembly) |

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

### 1.1 The data fork

The data fork is an 84-byte header, the disk's sectors, then the tag bytes [Doc: File Type Note $E0/$0005]. **Tag
bytes** are the 12 bytes of file-system metadata a Lisa or early Mac floppy kept beside each 512-byte sector; Mac OS
never uses them, and images keep them only for exact copies. The resource fork, if any, is not needed to read the
disk.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `+$00` | 64 | Disk name | `Str63`. Bytes past the name's length are junk: ShrinkWrap 2.1's "DiskCopy Image" option leaves nonzero bytes there [Verified] |
| `+$40` | 4 | Data size | `u32`: the disk's sector bytes [Doc] |
| `+$44` | 4 | Tag size | `u32`: 12 per sector, or 0 [Doc] |
| `+$48` | 4 | Data checksum | `u32` ([§1.2](#12-the-checksum)) [Doc] |
| `+$4C` | 4 | Tag checksum | `u32` ([§1.2](#12-the-checksum)) [Doc] |
| `+$50` | 1 | Disk format | 0 = 400K GCR, 1 = 800K GCR, 2 = 720K MFM, 3 = 1440K MFM [Doc] |
| `+$51` | 1 | Format byte | `$12` = 400K, `$22` = larger Mac disk, `$24` = Apple II 800K [Doc]. Disk Copy 6.1.2 and ShrinkWrap 2.1 write `01 22` for 800K [Verified] |
| `+$52` | 2 | Private | `$0100`, required [Doc] |
| `+$54` | data size | Sectors | The disk's sectors, in order |
| `+$54` + data size | tag size | Tags | 12 per sector, in sector order |

### 1.2 The checksum

Both checksums use one sum [Doc], [Verified: Disk Copy 6.1.2's and ShrinkWrap 2.1's images]:

1. Start with a 32-bit sum of 0.
2. For each big-endian 16-bit word of the bytes covered: add it (modulo 2³²), then rotate the sum right by one bit.
3. A trailing odd byte is ignored.

What each covers:

- The data checksum covers all data bytes [Doc], [Verified].
- The tag checksum **skips the first 12 tag bytes** (sector 0's tags) and covers the rest [Fitted: matched on the Disk
  Copy 4.2 image in DART 1.5.3's sample set, whose header held the sum of the tags from byte 12; not traced in Disk
  Copy's code]. DART's tag checksum, the same sum, does not skip them ([dart.md §1.3](dart.md#13-checksums)).

## 2. Reading

1. Read the 84-byte header. `+$52` must be `$0100` [Doc].
2. The disk is the data size bytes from `+$54`; the tags, when the tag size is not 0, follow it.
3. Optionally compute the two checksums ([§1.2](#12-the-checksum)) and compare them with `+$48` and `+$4C`.

The disk format (`+$50`) and format byte (`+$51`) are not needed: the volume reader finds the file system [Fitted].
What Disk Copy does with a wrong checksum or a short data fork was not traced. Disk Copy chooses its reader by file
type alone ([raw-images.md §2](raw-images.md#2-reading)).

## 3. Writing

None.

## 4. Variants

- Disk Copy 6.3.3 writes Disk Copy 4.2 images only for floppy sizes (the choice is greyed out for a 5 MB volume)
  [Verified], typed `'dImg'`/`'dCpy'`, with a `'vers'` resource giving both checksums [Code: 6.3.3], [Verified].
- ShrinkWrap 2.1 (two of its options) and DiskDup+ 2.9.2 (its "Disk Copy" option) also write this format; their
  options are listed in [raw-images.md §4](raw-images.md#4-variants).

## 5. ClassicMac

- **Recognition** [ClassicMac]: a data fork is taken as Disk Copy 4.2 when it is at least 84 bytes, `+$52` is
  `$0100` [Doc], the name length is at most 63, the data size is a nonzero multiple of 512, and the tag size is 0 or
  12 per sector (the last three checks fitted to real images). File types are not used. Where this reader comes in
  the unwrapper's order is in [unwrapping.md §3.1](../containers/unwrapping.md#31-readers-and-order).
- **Output**: one file named by the header's disk name, whose data fork is the data bytes. Tags are dropped.
- **Short data fork**: when the fork holds fewer than 84 + data size bytes, the whole sectors present are kept and
  `diskcopy.truncated` is reported; neither checksum is checked.
- **Short tags**: when the data are complete but the tags are cut short, the tags are dropped and
  `diskcopy.tags-truncated` is reported; the data checksum is still checked.
- **Checksums**: both are checked whenever the data are complete (no option needed); a mismatch is a warning and the
  disk is read anyway.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `diskcopy.checksum` | Warning | The data or tag checksum does not match | Reads the disk | Not traced |
| `diskcopy.tags-truncated` | Warning | The tag bytes are cut short | Drops the tags | Not traced |
| `diskcopy.truncated` | Error | The data fork holds fewer data bytes than the header says | Reads the whole sectors present; skips the checksums | Not traced |

## 7. Verification

- `tests/ClassicMac.Files.Tests/DiskCopyTests.cs`: images built by `Fixtures.DiskCopy42`
  (`tests/ClassicMac.Files.Tests/Fixtures.cs`) prove the layout, the name, the checksum warning, the truncated read
  and the recognition checks.
- `tests/ClassicMac.Files.Tests/HfsTests.cs`: a MacBinary-wrapped Disk Copy 4.2 image unwraps to its HFS volume.
- `tests/ClassicMac.Files.Tests/DartTests.cs` (`DART_153_files_decode_to_their_source_disks`): with the
  `CLASSICMAC_CORPUS` folders set, the Disk Copy 4.2 image in DART 1.5.3's sample set (CiderPress2's test data,
  Apache-2.0, not committed) is the reference disk; its header is the one the tag-checksum rule was fitted to.
- Images made by Disk Copy 6.1.2, 6.3.3 and ShrinkWrap 2.1 in SheepShaver confirmed the sum and the format bytes; they
  are not in the repository.

## 8. Not covered

- Writing Disk Copy 4.2 images.
- What Disk Copy does with a wrong checksum or a truncated image (not traced).
- The tag checksum's skip of the first 12 bytes was matched on one image, not traced in code [Fitted].

## 9. References

1. Apple Computer, File Type Note $E0/$0005, Disk Copy 4.2 image format.
2. Disk Copy 6.1.2 and 6.3.3, Apple, traced in disassembly and run in SheepShaver.
3. CiderPress2 (Andy McFadden), Apache-2.0: the source of the DART 1.5.3 sample set used as test data; no code used.
