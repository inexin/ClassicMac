# Disk Copy 4.2

Contents

1. [Layout](#1-layout)
2. [Checksums](#2-checksums)
3. [Recognition](#3-recognition)
4. [Diagnostics](#4-diagnostics)
5. [Open questions](#5-open-questions)

---

## 1. Layout

The data fork is an 84-byte header, the disk's sectors, then 12 tag bytes per sector [Doc: File Type Note $E0/$0005].
The resource fork, if any, is not needed to read the disk.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| `+$00` | 64 | `Str63` | Disk name. Bytes past the name's length are junk: ShrinkWrap 2.1's "DiskCopy Image" option leaves nonzero bytes there [Verified] |
| `+$40` | 4 | `u32` | Data size: the disk's sector bytes [Doc] |
| `+$44` | 4 | `u32` | Tag size: 12 per sector, or 0 [Doc] |
| `+$48` | 4 | `u32` | Data checksum ([§2](#2-checksums)) [Doc] |
| `+$4C` | 4 | `u32` | Tag checksum ([§2](#2-checksums)) [Doc] |
| `+$50` | 1 | `u8` | Disk format: 0 = 400K GCR, 1 = 800K GCR, 2 = 720K MFM, 3 = 1440K MFM [Doc] |
| `+$51` | 1 | `u8` | Format byte: `$12` = 400K, `$22` = larger Mac disk, `$24` = Apple II 800K [Doc]. Disk Copy 6.1.2 and ShrinkWrap 2.1 wrote `01 22` for 800K [Verified] |
| `+$52` | 2 | `u16` | `$0100`, required [Doc] |
| `+$54` | data size | | The sectors, in order |
| … | tag size | | The tag bytes, 12 per sector, in sector order |

ClassicMac reads neither `+$50` nor `+$51`: the volume reader finds the file system [Fitted].

---

## 2. Checksums

Both checksums use the same sum: start at 0; for each big-endian 16-bit word, add it (modulo 2³²), then rotate the
32-bit sum right by one bit [Doc], [Verified: Disk Copy 6.1.2's and ShrinkWrap 2.1's images]. A trailing odd byte is
ignored.

- The **data checksum** covers all data bytes [Doc], [Verified].
- The **tag checksum skips the first 12 tag bytes** (sector 0's tags) and covers the rest [Fitted: matched on the
  Disk Copy 4.2 image in DART 1.5.3's sample set, whose header held the sum of the tags from byte 12; not traced in
  Disk Copy's code]. DART's own tag checksum does not skip them ([dart.md §4](dart.md#4-checksums)).

ClassicMac checks both when the image is complete and reports a mismatch as a warning; the disk is read anyway. What
Disk Copy does with a wrong Disk Copy 4.2 checksum was not traced.

---

## 3. Recognition

ClassicMac takes a data fork as Disk Copy 4.2 when it is at least 84 bytes, `+$52` is `$0100` [Doc], the name length
is at most 63, the data size is a nonzero multiple of 512, and the tag size is 0 or 12 per sector [Fitted].

- A data fork shorter than 84 + data size keeps its whole sectors, is reported (`diskcopy.truncated`) and is not
  checksummed.
- Tags cut short are dropped and reported (`diskcopy.tags-truncated`); the data checksum is still checked.

Disk Copy 6.3.3 writes Disk Copy 4.2 images only for floppy sizes (the choice is greyed out for a 5 MB volume)
[Verified], typed `dImg`/`dCpy`, with a `vers` resource giving both checksums [Code: 6.3.3], [Verified].

---

## 4. Diagnostics

Every problem is reported with a code and a severity ([README.md](../README.md#diagnostics)). The codecs emit none of
their own: their failures surface as `dart.bad-block`, `ndif.bad-chunk` or `udif.bad-run`. There are no `adc.`,
`kencode.` or `bzip2.` codes.

| Code | Severity | Meaning | ClassicMac | Disk Copy |
| --- | --- | --- | --- | --- |
| `diskcopy.truncated` | Error | The data fork holds fewer data bytes than the header says | reads the whole sectors present; skips the checksums | not traced |
| `diskcopy.tags-truncated` | Warning | The tag bytes are cut short | drops the tags | not traced |
| `diskcopy.checksum` | Warning | The data or tag checksum does not match | reads the disk | not traced |

---

## 5. Open questions

- **Disk Copy 4.2's tag checksum** skipping the first 12 bytes was matched on one image, not traced in code [Fitted].
