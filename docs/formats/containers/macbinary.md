# MacBinary I, II and III

Contents

1. [Layout](#1-layout)
2. [Header](#2-header)
3. [Versions and detection](#3-versions-and-detection)
4. [Reading](#4-reading)
5. [Writing MacBinary III](#5-writing-macbinary-iii)
6. [Diagnostics](#6-diagnostics)
7. [Not covered](#7-not-covered)

---

## 1. Layout

A MacBinary file is a 128-byte header, then the data fork, then the resource fork. Each fork is padded with zero bytes
to a multiple of 128; a fork of length 0 takes no space. [Author]

MacBinary II adds an optional secondary header between the header and the data fork, its length at `+$78`, padded to
a multiple of 128; and an optional Finder comment after the resource fork, its length at `+$63`. [Author]

```
+0                      128-byte header
+128                    secondary header, pad(secondaryLength) bytes        (II and III only)
+128+pad(sec)           data fork, pad(dataLength) bytes
  …                     resource fork, pad(resourceLength) bytes
  …                     Get Info comment, commentLength bytes                (II and III, optional)
pad(n) = n rounded up to a multiple of 128
```

The last fork need not be padded: files whose final fork ends the file are common. [Fitted]

---

## 2. Header

| Offset | Size | Type | Meaning | Versions |
| --- | --- | --- | --- | --- |
| +$00 (0) | 1 | `u8` | old version number; must be 0 [Author] | all |
| +$01 (1) | 1 | `u8` | name length, 1–63 [Author] | all |
| +$02 (2) | 63 | bytes | file name, Mac OS Roman; only the first *length* bytes count [Author] | all |
| +$41 (65) | 4 | `OSType` | file type [Author] | all |
| +$45 (69) | 4 | `OSType` | creator [Author] | all |
| +$49 (73) | 1 | `u8` | Finder flags, high byte (bits 8–15) [Author] | all |
| +$4A (74) | 1 | `u8` | zero fill; must be 0 [Author] | all |
| +$4B (75) | 2 | `i16` | icon position, vertical [Author] | all |
| +$4D (77) | 2 | `i16` | icon position, horizontal [Author] | all |
| +$4F (79) | 2 | `i16` | window or folder ID (`fdFldr`) [Author] | all |
| +$51 (81) | 1 | `u8` | "protected" flag in bit 0 [Author] | all |
| +$52 (82) | 1 | `u8` | zero fill; must be 0 [Author] | all |
| +$53 (83) | 4 | `u32` | data fork length [Author] | all |
| +$57 (87) | 4 | `u32` | resource fork length [Author] | all |
| +$5B (91) | 4 | `u32` | creation date (Mac date) [Author] | all |
| +$5F (95) | 4 | `u32` | modification date (Mac date) [Author] | all |
| +$63 (99) | 2 | `u16` | length of the Get Info comment that follows the resource fork [Author] | II, III |
| +$65 (101) | 1 | `u8` | Finder flags, low byte (bits 0–7) [Author] | II, III |
| +$66 (102) | 4 | `OSType` | signature `'mBIN'` [Author] | III |
| +$6A (106) | 1 | `i8` | `fdScript` (`FXInfo` +8) [Author] | III |
| +$6B (107) | 1 | `u8` | `fdXFlags` (`FXInfo` +9) [Author] | III |
| +$6C (108) | 8 | bytes | reserved, zero [Author] | II, III |
| +$74 (116) | 4 | `u32` | total length of the files when unpacked (for on-the-fly compressors) [Author] | II, III |
| +$78 (120) | 2 | `u16` | secondary header length [Author] | II, III |
| +$7A (122) | 1 | `u8` | version of MacBinary the writer follows: 129 for II, 130 for III [Author] | II, III |
| +$7B (123) | 1 | `u8` | minimum version needed to read the file: 129 [Author] | II, III |
| +$7C (124) | 2 | `u16` | CRC-16 ([unwrapping.md §2.3](unwrapping.md#23-crc-16)) of bytes 0–123 [Author] | II, III |
| +$7E (126) | 2 | bytes | reserved for a computer type and OS ID; zero for the Mac [Author] | II, III |

In MacBinary I, bytes 99–127 are unused and zero, and there are no low Finder flags. [Author]

---

## 3. Versions and detection

Only MacBinary III has a signature. II is told from I by its header CRC, and I has nothing but its zero bytes, so
detection must be strict or ordinary data files starting with a zero byte pass as MacBinary I. [Author] ClassicMac
tries III, then II, then I; each version is a separate reader with its own test, and a header matches at most one.

Checks common to all versions:

1. The input has at least 128 bytes.
2. Bytes 0, 74 and 82 are zero. [Author]
3. The name length (byte 1) is 1–63. [Author]
4. The name contains neither `:` (the HFS path separator, which no Mac name contains) nor a NUL byte. [Fitted]
5. Both fork lengths are at most `$7FFFFF` (8 MiB − 1). [Author] for MacBinary I; ClassicMac applies the limit to II
   and III as well. No Apple code on the Mac OS 9.0 disk reads or writes MacBinary, so there is no Apple limit to
   match [Code: Mac OS 9.0 disk]: the limit is ClassicMac's choice, taken from the MacBinary authors'.

Then:

| Test | Result |
| --- | --- |
| CRC-16 of bytes 0–123 equals the `u16` at +124, and bytes 102–105 are `'mBIN'` | MacBinary III [Author] |
| CRC matches, no `'mBIN'` | MacBinary II [Author] |
| CRC does not match, and bytes 99–125 are all zero | MacBinary I candidate [Fitted] |
| anything else | not MacBinary |

A MacBinary I candidate is accepted only if the input's length is exactly what the header implies: either
`128 + pad(dataLength) + pad(resourceLength)` (both forks padded), or the same with the last non-empty fork unpadded:
`128 + pad(dataLength) + resourceLength` when there is a resource fork, `128 + dataLength` when there is not. [Fitted]
Longer and shorter inputs are rejected. For example, a 10-byte data fork and a 1-byte resource fork give 384 or 257
bytes.

What this rejects, and why:

- A MacBinary II or III file whose header CRC is damaged: its bytes 99–125 are not zero, so it is not MacBinary I
  either. It is treated as a plain file, with no diagnostic. [Fitted]
- Raw resource forks: byte 1 is the second byte of the data offset, normally 0, so the name length is 0.
- Data files that start with zeros but hold NUL bytes in the "name", or whose "fork lengths" do not add up to the file
  length. [Fitted]

ClassicMac does not check bytes 122 and 123 (the writer's version and the minimum version), nor the reserved bytes
108–119 and 126–127 for II and III. Neither does it check the total length of a II or III file, so trailing data
(such as a Get Info comment) is ignored.

---

## 4. Reading

- **Name**: bytes 2 to 2 + length, kept as bytes (Mac OS Roman). [Author]
- **Finder info**: type, creator, location (v at 75, h at 77) and folder (79) into `FInfo`. `fdFlags` is byte 73 as
  the high byte and, for II and III, byte 101 as the low byte; MacBinary I files have a zero low byte. [Author]
  For III, byte 106 goes to `FXInfo` +8 and byte 107 to `FXInfo` +9; the rest of `FXInfo` is zero. [Author] The
  protected flag (byte 81) is not part of the Finder info; ClassicMac ignores it.
- **Dates**: creation at 91, modification at 95, as Mac dates. ClassicMac takes 0 as "no date".
- **Forks**: the data fork starts at `128 + pad(secondaryLength)` (secondary length 0 for MacBinary I), and the
  resource fork at the data fork's start plus `pad(dataLength)`. [Author]
- **Comment**: the Get Info comment (length at 99, after the resource fork) is not read by ClassicMac.

ClassicMac hands out the forks as slices of the input without copying. If a fork runs past the end of the input, it
reports `macbinary.fork-truncated` (Error) and keeps the bytes that are there; a resource fork that starts past the
end is empty. Since MacBinary I is only detected at its exact length, this happens only for II and III.

---

## 5. Writing MacBinary III

`MacBinaryWriter` writes MacBinary III [Author], with ClassicMac's choices marked:

- The header of §2: name (cut to 63 bytes; an empty name is written as `?` [ClassicMac]), type, creator,
  Finder flags (high byte at 73, low byte at 101), location, folder, both fork lengths, creation and modification dates
  (0 when unknown), `'mBIN'`, `fdScript` and `fdXFlags` from `FXInfo` +8 and +9, writer version 130, reader version 129,
  and the CRC-16 of bytes 0–123. Everything else is zero: no protected flag, secondary header, comment or unpacked
  length.
- The data fork, then the resource fork, each padded with zeros to a multiple of 128, the last one too.
- A fork over `$7FFFFF` bytes is refused (an argument error, nothing written) [ClassicMac, as the reader's limit].

---

## 6. Diagnostics

Codes these readers and the unwrapper emit. Offsets, where given, are in the container's input. None of these formats
is handled by Mac OS ([applesingle-appledouble.md](applesingle-appledouble.md)), so the "original tools" column describes the format authors' intent where it is
stated; the behaviour of MacBinary, BinHex and StuffIt programs was not traced.

| Code | Severity | Meaning | ClassicMac | Original tools |
| --- | --- | --- | --- | --- |
| `macbinary.fork-truncated` | Error | a fork's length runs past the end of the input | keeps the bytes present | not traced |

A MacBinary II or III file with a damaged header CRC, and a MacBinary I file whose length does not match its header,
produce no diagnostic: they are not recognised, and stay plain files (§3).

---

## 7. Not covered

- Writing AppleDouble or AppleSingle version 1, MacBinary I or II, and MacBinary's secondary header and comment.
- The MacBinary Get Info comment, the protected flag and the unpacked-length field (bytes 99, 81, 116): not read.
