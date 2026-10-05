# MacBinary I, II and III

MacBinary (`.bin`) packs one Mac file into one flat file for modem transfers and foreign file systems: a 128-byte
header with the name, Finder information and dates, then the data fork and the resource fork. It was defined by its
authors in three versions (I in 1985, II in 1987, III in 1996) and has no Apple specification; no Apple code on a
Mac OS 9.0 disk reads or writes it. ClassicMac reads all three versions and writes MacBinary III.

| | |
| --- | --- |
| Identified by | Extension `.bin`; III: `'mBIN'` at +$66; II and III: the header CRC-16 at +$7C; I: zero bytes and an exact file length (§2.1) |
| ClassicMac | Reads and writes (III); `ClassicMac.Files.Containers` (`MacBinaryReader`, `MacBinaryWriter`) |
| Verified against | Nothing yet |
| Sources | The MacBinary I, II and III specifications |

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

## 1. Layout

Offsets are in hex with the decimal offset the specifications use in parentheses: `+$53 (83)`. `pad(n)` is `n`
rounded up to a multiple of 128.

### 1.1 The file

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 128 | header | §1.2 [Author] |
| +$80 (128) | pad(secondaryLength) | secondary header | II and III only, optional; its length at +$78 [Author] |
| +$80 + pad(secondaryLength) | pad(dataLength) | data fork | [Author] |
| (after the data fork) | pad(resourceLength) | resource fork | [Author] |
| (after the resource fork) | commentLength | Get Info comment | II and III only, optional; its length at +$63 [Author] |

Each fork is padded with zero bytes to a multiple of 128; a fork of length 0 takes no space. [Author] The last fork
need not be padded: files whose final fork ends the file are common. [Fitted]

### 1.2 Header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 (0) | 1 | old version number | `u8`, must be 0 [Author]; all versions |
| +$01 (1) | 1 | name length | `u8`, 1–63 [Author]; all |
| +$02 (2) | 63 | file name | Mac OS Roman; only the first *length* bytes count [Author]; all |
| +$41 (65) | 4 | file type | `OSType` [Author]; all |
| +$45 (69) | 4 | creator | `OSType` [Author]; all |
| +$49 (73) | 1 | Finder flags, high byte | bits 8–15 of `fdFlags` [Author]; all |
| +$4A (74) | 1 | zero fill | must be 0 [Author]; all |
| +$4B (75) | 2 | icon position, vertical | `i16` [Author]; all |
| +$4D (77) | 2 | icon position, horizontal | `i16` [Author]; all |
| +$4F (79) | 2 | window or folder ID | `i16`, `fdFldr` [Author]; all |
| +$51 (81) | 1 | protected flag | bit 0 [Author]; all |
| +$52 (82) | 1 | zero fill | must be 0 [Author]; all |
| +$53 (83) | 4 | data fork length | `u32` [Author]; all |
| +$57 (87) | 4 | resource fork length | `u32` [Author]; all |
| +$5B (91) | 4 | creation date | `u32`, Mac date ([unwrapping.md §1.2](unwrapping.md#12-dates)) [Author]; all |
| +$5F (95) | 4 | modification date | `u32`, Mac date [Author]; all |
| +$63 (99) | 2 | Get Info comment length | `u16`; the comment follows the resource fork [Author]; II, III |
| +$65 (101) | 1 | Finder flags, low byte | bits 0–7 of `fdFlags` [Author]; II, III |
| +$66 (102) | 4 | signature | `'mBIN'` [Author]; III |
| +$6A (106) | 1 | `fdScript` | `i8`, `FXInfo` +8 [Author]; III |
| +$6B (107) | 1 | `fdXFlags` | `u8`, `FXInfo` +9 [Author]; III |
| +$6C (108) | 8 | reserved | zero [Author]; II, III |
| +$74 (116) | 4 | unpacked length | `u32`: total length of the files when unpacked, for on-the-fly compressors [Author]; II, III |
| +$78 (120) | 2 | secondary header length | `u16` [Author]; II, III |
| +$7A (122) | 1 | writer's version | `u8`: 129 for II, 130 for III [Author]; II, III |
| +$7B (123) | 1 | minimum reader version | `u8`: 129 [Author]; II, III |
| +$7C (124) | 2 | header CRC | `u16`, the CRC-16 of [unwrapping.md §1.3](unwrapping.md#13-crc-16) over bytes 0–123 [Author]; II, III |
| +$7E (126) | 2 | reserved | for a computer type and OS ID; zero for the Mac [Author]; II, III |

In MacBinary I, bytes 99–127 are unused and zero, and there are no low Finder flags. [Author] The Finder information
fields map to `FInfo` and `FXInfo` as in [unwrapping.md §1.1](unwrapping.md#11-finder-information).

## 2. Reading

### 2.1 Detection

Only MacBinary III has a signature. II is told from I by its header CRC, and I has nothing but its zero bytes, so
detection must be strict or ordinary data files starting with a zero byte pass as MacBinary I. [Author] Each header
matches at most one version:

1. The input has at least 128 bytes.
2. Bytes 0, 74 and 82 are zero. [Author]
3. The name length (byte 1) is 1–63. [Author]
4. The name contains neither `:` (the HFS path separator, which no Mac name contains) nor a NUL byte. [Fitted]
5. Both fork lengths are at most `$7FFFFF` (8 MiB − 1). [Author] for MacBinary I; ClassicMac applies the limit to II
   and III as well (§5).
6. If the CRC-16 of bytes 0–123 equals the `u16` at +124: MacBinary III when bytes 102–105 are `'mBIN'`, else
   MacBinary II. [Author]
7. Otherwise, if bytes 99–125 are not all zero, it is not MacBinary. [Fitted]
8. Otherwise it is MacBinary I only if the input's length is exactly what the header implies: either
   `128 + pad(dataLength) + pad(resourceLength)` (both forks padded), or the same with the last non-empty fork
   unpadded: `128 + pad(dataLength) + resourceLength` when there is a resource fork, `128 + dataLength` when there is
   not. Longer and shorter inputs are rejected. [Fitted] A 10-byte data fork and a 1-byte resource fork give 384 or
   257 bytes.

What this rejects, and why:

- A MacBinary II or III file whose header CRC is damaged: its bytes 99–125 are not zero, so it is not MacBinary I
  either. It is a plain file. [Fitted]
- Raw resource forks: byte 1 is the second byte of the data offset, normally 0, so the name length is 0.
- Data files that start with zeros but hold NUL bytes in the "name", or whose "fork lengths" do not add up to the file
  length. [Fitted]

An input that does not pass its version's test is unusable (`container.unreadable`,
[unwrapping.md §2.2](unwrapping.md#22-unwrapping-a-file)).

### 2.2 The file

1. **Name**: bytes 2 to 2 + length, as bytes (Mac OS Roman). [Author]
2. **Finder information**: type, creator, location (v at 75, h at 77) and folder (79) into `FInfo`. `fdFlags` is
   byte 73 as the high byte and, for II and III, byte 101 as the low byte; MacBinary I files have a zero low byte.
   For III, byte 106 goes to `FXInfo` +8 and byte 107 to `FXInfo` +9; the rest of `FXInfo` is zero. [Author] The
   protected flag (byte 81) is not part of the Finder information.
3. **Dates**: creation at 91, modification at 95, as Mac dates. [Author]
4. **Forks**: the data fork starts at `128 + pad(secondaryLength)` (secondary length 0 for MacBinary I), and the
   resource fork at the data fork's start plus `pad(dataLength)`. [Author]
5. **Comment**: the Get Info comment (length at 99) follows the resource fork. [Author]

A fork that runs past the end of the input keeps the bytes that are there, with `macbinary.fork-truncated` (Error); a
resource fork that starts past the end is empty. Since MacBinary I is only detected at its exact length, this happens
only for II and III.

## 3. Writing

A MacBinary III file [Author]:

1. The header of §1.2: name length and name; type, creator; Finder flags, high byte at 73 and low byte at 101;
   location; folder; both fork lengths; creation and modification dates (0 when unknown); `'mBIN'`; `fdScript` and
   `fdXFlags` from `FXInfo` +8 and +9; writer version 130, reader version 129; the CRC-16 of bytes 0–123.
2. Every other header byte zero: no protected flag, secondary header, comment or unpacked length.
3. The data fork, then the resource fork, each padded with zeros to a multiple of 128, the last one too.

## 4. Variants

| | MacBinary I | MacBinary II | MacBinary III |
| --- | --- | --- | --- |
| Recognised by | zero bytes and exact length [Fitted] | header CRC [Author] | header CRC and `'mBIN'` [Author] |
| Finder flags | high byte only | both bytes | both bytes |
| `fdScript`, `fdXFlags` | no | no | yes |
| Secondary header, comment | no | yes | yes |
| Fork limit | `$7FFFFF` [Author] | ClassicMac applies I's (§5) | ClassicMac applies I's (§5) |

No Apple code on the Mac OS 9.0 disk reads or writes MacBinary, so there is no Apple limit or behaviour to match
[Code: Mac OS 9.0 disk]; the Mac OS components that decode `.bin` files use third-party code
([binhex.md §4.1](binhex.md#41-apples-encoder)).

## 5. ClassicMac

- Each version is a separate reader (`MacBinaryReader.III`, `.II`, `.I`, format names `MacBinary III`, `MacBinary II`,
  `MacBinary I`), tried in that order ([unwrapping.md §2.1](unwrapping.md#21-reader-order)). [ClassicMac]
- The `$7FFFFF` fork limit applies to II and III too: there is no Apple limit to match, so the limit is ClassicMac's
  choice, taken from the MacBinary authors'. [ClassicMac]
- The reader does not check bytes 122 and 123 (the writer's version and the minimum version), nor the reserved bytes
  108–119 and 126–127 of II and III, nor the total length of a II or III file, so trailing data (such as a Get Info
  comment) is ignored. [ClassicMac]
- The Get Info comment, the protected flag and the unpacked-length field (bytes 99, 81, 116) are not read.
  [ClassicMac]
- A date of 0 is "not recorded". [ClassicMac]
- The forks are slices of the input, not copies. [ClassicMac]
- `MacBinaryWriter` writes §3. A name longer than 63 bytes is cut to 63; an empty name is written as `?`. A name with
  `:` or NUL, which the reader takes for another kind of file, and a fork over `$7FFFFF` bytes, as the reader's limit,
  are refused (an argument error, nothing written). [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `macbinary.fork-truncated` | Error | a fork's length runs past the end of the input | keeps the bytes present | no Apple code reads MacBinary [Code: Mac OS 9.0 disk] |

A MacBinary II or III file with a damaged header CRC, and a MacBinary I file whose length does not match its header,
produce no diagnostic: they are not recognised, and stay plain files (§2.1).

## 7. Verification

No file made by a MacBinary application is in the tests; the inputs are built byte by byte by
`tests/ClassicMac.Files.Tests/Fixtures.cs` (`MacBinary`).

- `tests/ClassicMac.Files.Tests/MacBinaryTests.cs`: `Each_version_is_detected_by_its_own_reader` (§2.1),
  `MacBinary_II_carries_name_Finder_info_dates_and_both_forks` (§2.2), `MacBinary_I_has_only_the_high_flag_byte`,
  `MacBinary_III_keeps_script_and_extended_flags`, `A_secondary_header_is_skipped`,
  `Truncated_forks_keep_what_is_there` (`macbinary.fork-truncated`), `MacBinary_I_needs_the_length_its_header_gives`,
  `Data_starting_with_zeros_is_not_MacBinary_I`, `A_bad_CRC_is_not_MacBinary_II`, `Other_files_are_not_MacBinary`.
- `tests/ClassicMac.Files.Tests/ContainerWriterTests.cs`: `MacBinary_III_reads_back` (§3 read back by §2), `MacBinary_refuses_a_name_no_Mac_file_has`.
- `tests/ClassicMac.Files.Tests/ForkSaverTests.cs`: `A_MacBinary_II_file_is_saved_as_MacBinary_III`
  ([writing.md](writing.md)).

## 8. Not covered

- Writing MacBinary I or II, and MacBinary's secondary header and Get Info comment.
- Reading the Get Info comment, the protected flag and the unpacked-length field (bytes 99, 81, 116).

## 9. References

1. The MacBinary I specification (1985).
2. The MacBinary II specification (1987).
3. The MacBinary III specification (1996).
4. Shared structures (Finder information, dates, CRC-16): [unwrapping.md](unwrapping.md).
