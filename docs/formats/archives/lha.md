# LHA and LArc

The archive format of LHarc and LHA (Haruyasu Yoshizaki) and of LArc, common on MS-DOS and Unix, and written on the
Mac by MacLHA. Each entry is a header and one file's compressed bytes; header levels 0 to 3 differ in how the header
is sized and extended. On the Mac, MacLHA stores each file as MacBinary unless told otherwise. ClassicMac reads header
levels 0–3 and the methods `-lh0-` to `-lh7-`, `-lzs-` and `-lz5-`, for entries made on a Mac.

| | |
| --- | --- |
| Identified by | A method string `-lh?-` or `-lz?-` at +$02 with a valid header at offset 0; extensions `.lzh`, `.lha` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.LhaReader` |
| Verified against | MacLHA 2.24 (lhasa's test archives): levels 0, 1 and 2; `-lh0-`, `-lh1-`, `-lh5-` |
| Sources | LHa for UNIX's header description and method table; Kaitai's LHA specification. Other readers (behaviour only): LHa for UNIX, lhasa |

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

All numbers in LHA headers are little-endian. An entry is a header, then the packed data; a header whose first byte
is 0 ends the archive [Reference: LHa for UNIX, Kaitai].

### 1.1 Level-0 and level-1 headers

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Header size | Bytes after the checksum byte: the header is this plus 2 bytes |
| +$01 | 1 | Header checksum | Byte sum, modulo 256, of the header from +$02 |
| +$02 | 5 | Method | `-lh0-` … (§1.5) |
| +$07 | 4 | Packed size | Level 0: the payload. Level 1: the extension headers plus the payload |
| +$0B | 4 | Expanded size | |
| +$0F | 4 | Date and time | DOS format; not read |
| +$13 | 1 | Attribute | DOS attribute; not read |
| +$14 | 1 | Level | 0 or 1 |
| +$15 | 1 | Name length | n |
| +$16 | n | Name | |
| +$16+n | 2 | File CRC | CRC-16/ARC of the expanded file |
| +$18+n | 1 | OS identifier | Level 0: present only when the header size leaves room for it (§4). Level 1: always present; `m` is the Mac |
| … | | Extended area | Level 0: optional |
| end − 2 | 2 | Next extension size | Level 1 only: size of the first extension header, 0 for none |

[Reference: LHa for UNIX, Kaitai] [Verified: MacLHA 2.24]

In level 1 the extension headers follow the base header and come before the payload. Each is [Reference: LHa for
UNIX]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Type | `$00`: header CRC. `$01`: file name. `$02`: directory name. Others are skipped |
| +$01 | n − 3 | Data | |
| n − 2 | 2 | Next extension size | 0 ends the chain |

### 1.2 Level-2 header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Header size | The whole header, extensions included |
| +$02 | 5 | Method | |
| +$07 | 4 | Packed size | The payload only |
| +$0B | 4 | Expanded size | |
| +$0F | 4 | Time | Unix seconds; not read |
| +$13 | 1 | Reserved | Not read |
| +$14 | 1 | Level | 2 |
| +$15 | 2 | File CRC | CRC-16/ARC of the expanded file |
| +$17 | 1 | OS identifier | |
| +$18 | 2 | Next extension size | Extension headers as in §1.1 follow at +$1A |

The header may end with one padding byte after the extensions. A type-`$00` extension, required, holds the header's
CRC-16/ARC computed with its own two bytes as zero. [Reference: LHa for UNIX, Kaitai] [Verified: MacLHA 2.24]

### 1.3 Level-3 header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Word size | 4 |
| +$02 | 5 | Method | |
| +$07 | 4 | Packed size | The payload only |
| +$0B | 4 | Expanded size | |
| +$0F | 4 | Time | Unix seconds; not read |
| +$13 | 1 | Attribute | Not read |
| +$14 | 1 | Level | 3 |
| +$15 | 2 | File CRC | |
| +$17 | 1 | OS identifier | |
| +$18 | 4 | Header size | The whole header |
| +$1C | 4 | Next extension size | Extension headers follow at +$20, each with a 4-byte next size at its end |

There is no padding. The type-`$00` header CRC is required and computed as in level 2. [Reference: LHa for UNIX,
Kaitai]

### 1.4 Paths

The file name comes from the base header (levels 0 and 1) or a type-`$01` extension; the directory from a type-`$02`
extension, its names separated by `$FF` [Reference: LHa for UNIX]. MacLHA writes a "full" path with a leading `$FF`
and the volume name (`$FF Untitled $FF subdir $FF subdir2 $FF`) [Verified: MacLHA 2.24]. Names from a Mac (`m`) entry
are Mac OS Roman.

### 1.5 Methods

| Method | Coding | Window | Matches |
| --- | --- | --- | --- |
| `-lh0-` | Stored | | |
| `-lh1-` | LZHUF: adaptive Huffman (§2.3) | 4 KiB | 3–60 |
| `-lh2-` | Adaptive Huffman, growing position tree (§2.4) | 8 KiB | 3–256 |
| `-lh3-` | Static Huffman, 286 symbols (§2.5) | 8 KiB | 3–256 |
| `-lh4-` | Static Huffman (§2.6) | 4 KiB | 3–256 |
| `-lh5-` | Static Huffman (§2.6) | 8 KiB | 3–256 |
| `-lh6-` | Static Huffman (§2.6) | 32 KiB | 3–256 |
| `-lh7-` | Static Huffman (§2.6) | 64 KiB | 3–256 |
| `-lzs-` | LArc LZSS (§2.7) | 2 KiB | 2–17 |
| `-lz5-` | LArc LZSS (§2.7) | 4 KiB | 3–18 |
| `-lz4-` | LArc, stored | | |
| `-lhd-` | A directory; no payload | | |

[Reference: LHa for UNIX, lhasa]

## 2. Reading

### 2.1 Entries

1. At each entry, a first byte 0 ends the archive.
2. Read the level at +$14 and the header by its level (§1.1–§1.3). Check a level-0 or level-1 header's checksum and a
   level-2 or level-3 header's CRC; a bad one is an error.
3. Level 1: follow the extension chain after the base header, within the packed size; the payload is the packed size
   less the extensions.
4. Build the path from the directory and file names (§1.4); both `/` and `\` separate folders.
5. Decode the payload by the method, to exactly the expanded size; the history window starts as spaces unless the
   method says otherwise. A match may overlap the bytes it is producing.
6. Check the file CRC (CRC-16/ARC) of the expanded file.

[Reference: LHa for UNIX, Kaitai] [Verified: MacLHA 2.24]

### 2.2 MacLHA entries

Except with "non-Mac", each entry's data is a MacBinary file carrying both forks, Finder information and dates
[Verified: MacLHA 2.24]. Unwrapping it is a separate step ([macbinary.md](../containers/macbinary.md)).

### 2.3 `-lh1-`

`-lh1-` is Okumura and Yoshizaki's LZHUF ([lzhuf.md](../codecs/lzhuf.md)) [Author: LZHUF]
[Verified: MacLHA 2.24]:

1. A 314-symbol adaptive tree, rebuilt when the root's count reaches `$8000`; an initial leaf group's leader is its
   left-most (lowest-index) leaf ([Reference: lhasa]).
2. A match's position: the upper 6 bits through LZHUF's fixed `d_code`/`d_len` table, a canonical code of 1, 3, 8, 12,
   24 and 16 codes of 3 to 8 bits (the first read is 3 bits), then 6 raw low bits.
3. A space-filled 4 KiB window. Positions are relative, so LZHUF's start at 4096 − 60 changes nothing.

### 2.4 `-lh2-`

An 8 KiB space-filled window, a dynamic literal/length tree, and a position tree that grows as the output passes each
64-byte boundary; matches are 3–256 bytes [Reference: LHa for UNIX, lhasa].

### 2.5 `-lh3-`

An 8 KiB window; blocks with a 16-bit command count, a 286-symbol literal/length Huffman tree and either a transmitted
or a ready-made position tree; matches are 3–256 bytes [Reference: LHa for UNIX, lhasa].

### 2.6 `-lh4-` to `-lh7-`

1. Each block starts with a 16-bit command count (0 is an error) and three Huffman tables: the code-length table, the
   literal/length table (510 symbols) and the position table. The position table's count field is 4 bits for
   `-lh4-`/`-lh5-` and 5 bits for `-lh6-`/`-lh7-`.
2. Symbols below 256 are literals; the others are match lengths from 3. A position follows each match.
3. When the block's commands are used, read the next block's tables.

[Reference: LHa for UNIX, lhasa]

### 2.7 LArc

- `-lzs-`: a 2 KiB space-filled ring, writing from 2048 − 17. Bits most significant first: a 1 bit is followed by an
  8-bit literal; a 0 bit by an 11-bit ring position and a 4-bit length less 2 (lengths 2–17).
- `-lz5-`: a 4 KiB ring preset with LArc's pattern (13 copies of each byte value 0–255, the values 0–255, then
  255–0, 128 zero bytes, 110 spaces, 18 zero bytes), writing from 4096 − 18. Each flag byte governs eight commands,
  least significant bit first: a 1 bit is a literal byte; a 0 bit is two bytes, the low 8 bits of the ring position,
  then its high 4 bits (upper nibble) and the length less 3 (lower nibble).

[Reference: LHa for UNIX, lhasa]

## 3. Writing

None.

## 4. Variants

- A level-0 header's OS identifier is an optional extension: MacLHA 2.24 writes level-0 headers without one, ending
  with the CRC (24 + name-length bytes in all) [Verified: MacLHA 2.24].
- MacLHA's "full" paths start with the volume name (§1.4) [Verified: MacLHA 2.24].
- MacLHA with "non-Mac" stores the plain data fork [Verified: MacLHA 2.24].

## 5. ClassicMac

- Only entries with OS identifier `m`, or a level-0 entry without one, are read; others are skipped and reported,
  because their names' encoding is not known. Names are kept as Mac OS Roman bytes. [ClassicMac]
- A full path's volume name is kept as the top folder, as lhasa does, so nothing is lost and the path stays relative.
  [ClassicMac]
- The entry is returned as stored; the default pipeline unwraps a MacBinary entry one level down, the LHA entry's
  folders staying on the LHA node and placing the Mac file on unpacking. [ClassicMac]
- `-lz4-` and any unknown method are reported and skipped by their packed size. [ClassicMac]
- A file CRC mismatch is reported and the decoded data kept. A bad header checksum or header CRC, a truncated
  bitstream, an invalid table or a match past the expanded size is an error. [ClassicMac]
- Dates and DOS attributes are not read. [ClassicMac]
- `ContainerReadOptions.MaxExpandedBytesPerInput` limits the archive and the total expanded data;
  `MaxVolumeEntries` counts every valid header, directories and skipped entries included. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.encoding-unsupported` | Warning | An entry's OS identifier is not `m` | Skips it | Not traced |
| `archive.end-marker-missing` | Warning | The archive ends without a 0 byte | Keeps the entries read | Not traced |
| `archive.fork-checksum` | Error | A file's CRC-16 does not match | Keeps the decoded data | Not traced |
| `archive.method-unsupported` | Warning | A method ClassicMac does not decode | Skips the entry | Not traced |

## 7. Verification

- `TestData/MacLha224` (MacLHA 2.24, from lhasa's test suite, ISC; `MacLha224Tests`): `l0_lh5.lzh` (level 0 without
  an OS byte), `l0_lh1.lzh`, `l1_lh1.lzh`, `l2_lh1.lzh` (`-lh1-` at levels 0–2, decoding to the same MacBinary file as
  `-lh5-`), `l1_nm_lh5.lzh` ("non-Mac", the plain file), `l1_subdir.lzh` and `l2_full_subdir.lzh` (`$FF`-separated
  directories, the full path's volume name). All 16 of lhasa's MacLHA 2.24 archives were read the same way, among them
  `-lh0-` archives of a MacBinary file holding a gzip file, which unwrap one level further; seven are committed.
  lhasa's `test/compressed/lh1.bin` decodes to its CRC-32 as well.
- Hand-built records in `LhaFeatureTests`: all four header levels, Mac OS Roman names and paths, stored data, extended
  file and directory names, extension-chain payload positions, unknown extensions, header checksums and CRCs, file
  CRCs, `-lh1-` literals and tree updates, `-lh2-` preset-window matches, maximum matches and position-tree growth,
  `-lh3-` literals, matches with both position trees and several blocks, `-lzs-` and `-lz5-` literals, preset-window
  copies and overlapping matches, literals and matches in `-lh4-` to `-lh7-` with several blocks, output-length and
  truncation checks, unsupported methods and encodings, the input and entry limits, unwrapper integration.

## 8. Not covered

- Dates and attributes.
- Entries from other systems than the Mac.
- `-lz4-` and methods not in §1.5.
- `-lh2-`, `-lh3-`, `-lh4-`, `-lh6-`, `-lh7-`, `-lzs-` and `-lz5-` against archives made on a Mac.

## 9. References

1. LHa for UNIX, `header.doc.md` and `src/lha_macro.h`, <https://github.com/jca02266/lha>. The LHa for UNIX licence;
   reference only.
2. Kaitai Struct, LHA specification, <https://formats.kaitai.io/lzh/>. CC0.
3. lhasa (Simon Howard), `lib/lh_new_decoder.c`, `lib/lh1_decoder.c`, and its MacLHA test archives,
   <https://github.com/fragglet/lhasa>. ISC; the `-lh1-` tree follows it with notice (`THIRD-PARTY-NOTICES.md`).
4. Haruhiko Okumura and Haruyasu Yoshizaki, LZHUF (1988): [lzhuf.md](../codecs/lzhuf.md).
