# Compact Pro

The archive format of Bill Goodman's Compact Pro, extension `.cpt`. An 8-byte header points to a directory at the end
of the archive; each file's forks are coded with RLE or with LZH followed by RLE. Compact Pro can cut an archive into
segments and save it as a self-extracting application. ClassicMac reads archives, folders, both fork codings, comments
and segment sets.

| | |
| --- | --- |
| Identified by | `$01` at +$00 and a directory, at the offset in +$04, whose CRC matches. Files of type `PACT`, creator `CPCT`; extension `.cpt` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.CompactProReader` |
| Verified against | Compact Pro 1.52 on Mac OS 9.0 (an archive, its `.sea` and a segment set) |
| Sources | Compact Pro's *User's Guide*. Other readers (behaviour only): a Compact Pro format description (docs.rs `compact-pro`), XADMaster, psx-spx's notes, pmarreck/compact_pro, munbox |

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

### 1.1 Archive header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Marker | `$01` |
| +$01 | 1 | Segment number | 1 in an unsegmented archive |
| +$02 | 2 | Archive id | Differs between two saves of the same files; in a segment, the set's id |
| +$04 | 4 | Directory offset | From the start of this file; 0 in every segment but the last |

[Reference: compact-pro description, XADMaster] [Verified: Compact Pro 1.52]

The forks come first, from +$08, and the directory follows them at the end of the archive [Verified: Compact Pro
1.52].

### 1.2 Directory

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | CRC | The CRC-32 register (reflected polynomial `$EDB88320`, initial `$FFFFFFFF`, no final inversion) over everything after it to the end of the entries |
| +$04 | 2 | Entry count | Every entry at every level |
| +$06 | 1 | Comment length | |
| +$07 | n | Comment | Mac OS Roman |
| … | | Entries | In order, each folder followed by its descendants |

Each entry starts with a name:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Name length and kind | Bits 0–6: the length (1–127). Bit 7: a folder |
| +$01 | n | Name | Mac OS Roman |

A folder continues with a `u16`: the number of all its descendant entries. A file continues with 45 bytes:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Volume | 1 in an archive and in the segments made from it (§4) |
| +$01 | 4 | Fork offset | From the start of the whole archive |
| +$05 | 4 | File type | |
| +$09 | 4 | Creator | |
| +$0D | 4 | Creation date | Mac date |
| +$11 | 4 | Modification date | Mac date |
| +$15 | 2 | Finder flags | |
| +$17 | 4 | Fork checksum | The CRC-32 register over the resource fork then the data fork, without the final inversion (`$FFFFFFFF` for an empty file) |
| +$1B | 2 | Flags | Bit 0: encrypted. Bit 1: the resource fork is LZH + RLE. Bit 2: the data fork is LZH + RLE |
| +$1D | 4 | Resource fork length | Expanded |
| +$21 | 4 | Data fork length | Expanded |
| +$25 | 4 | Resource fork compressed length | |
| +$29 | 4 | Data fork compressed length | |

At the fork offset are the compressed resource fork, then the compressed data fork. A fork without its LZH flag is
RLE alone.

[Reference: compact-pro description, XADMaster] [Verified: Compact Pro 1.52]

## 2. Reading

### 2.1 The archive

1. Read the header; at the directory offset, read the directory and check its CRC.
2. Walk the entries in order. A folder opens a scope over its descendant count; a file is in every scope still open.
3. For each file, decode the resource fork, then the data fork, from the fork offset; check the fork checksum.
4. A fork may lie before or after the directory, but not across it or the header.

[Reference: compact-pro description, XADMaster] [Verified: Compact Pro 1.52]

### 2.2 Segments

Compact Pro's Misc > Segment… cuts the whole archive into pieces and puts an 8-byte header on each: `$01`, the
segment number (1, 2, 3 …), a 16-bit id shared by the set (not the archive's own id), and the directory offset: 0 in
every segment but the last, where it counts from the start of that segment file [Verified: Compact Pro 1.52].

1. Open the last segment: a header whose segment number is above 1 with a nonzero directory offset is the last of
   that many segments [Author: Compact Pro *User's Guide*, "Working With Segmented Archives"].
2. The others are the files beside it with the same set id, a lower number and a zero directory offset.
3. Join them: segment 1 whole, then each later segment without its 8-byte header. The directory is not rewritten:
   its fork offsets (and volume bytes, 1) are those of the unsegmented archive, so an offset is into the joined
   archive and a fork may run across segments.
4. The directory is the last segment's, at its offset in that segment.

[Verified: Compact Pro 1.52]

### 2.3 RLE

The RLE stage, alone or after LZH, uses `$81` as the escape [Reference: pmarreck/compact_pro; munbox's sample]:

1. A byte other than `$81` is output.
2. `$81 $82 $00` outputs `$81 $82`.
3. `$81 $82 n`, n ≥ 1, outputs n − 1 more copies of the last byte (`$81 $82 $01` adds none).
4. `$81 x`, for any other x, outputs `$81` and then handles x as a new byte. So after `$81 $81` the second `$81` is
   output and is itself an escape for the next byte ("half-escaped"): `81 81 82 05` gives five `$81`,
   `81 81 81 82 05` six, and `81 81 41` is `81 81 41`.
5. The output must reach the expanded length; a run with no byte before it is an error.

### 2.4 LZH

The LZH stage's output goes through the RLE stage (§2.3) [Reference: psx-spx, compact-pro description]
[Verified: Compact Pro 1.52]:

1. An 8 KiB history window. Bits are read most significant first.
2. Each block starts with three code tables: literals (up to 256 symbols), lengths (up to 64) and the upper bits of
   displacements (up to 128). Each table is an 8-bit count c, then 2c 4-bit code lengths; the codes are canonical.
3. A 1 bit is followed by a literal code. A 0 bit is followed by a length code, a displacement code (the upper bits)
   and 6 raw bits (the lower bits); a displacement of 0 means 8192. Copy `length` bytes from that far back in the
   window; a copy may overlap the bytes it produces.
4. Count 2 for each literal and 3 for each match. When the count reaches `$1FFF0`, the block ends: go to the next
   byte boundary, skip 2 bytes, and 1 more if that leaves an odd byte offset; the next block's tables follow.

## 3. Writing

None.

## 4. Variants

- A self-extracting archive is the archive in an application's data fork ([sea.md](sea.md)).
- Compact Pro 1.52 uses LZH + RLE for the forks it can shrink and RLE alone for the others, 120,000 bytes of noise
  included [Verified: Compact Pro 1.52].
- A set written across floppies while saving, rather than with Segment…, may use the volume byte otherwise; not
  checked.

## 5. ClassicMac

- An archive is recognised only when its directory's CRC matches; read directly, a mismatch is reported and the
  archive read. [ClassicMac]
- The comment is reported as an `archive.comment` diagnostic, as Mac OS Roman text. [ClassicMac]
- Only the last segment is opened; earlier segments are not recognised on their own. Siblings come from the host
  folder through the default pipeline. Two siblings with the same number are an error; siblings of another set are
  ignored. [ClassicMac]
- With a segment missing, the set is reported with the missing numbers; the entries whose forks lie in the segments
  before the first missing one are still read, and each other entry is reported. [ClassicMac]
- Encrypted entries are reported and skipped. Flag bits other than 0–2 are reported and the entry read.
  [ClassicMac]
- A fork checksum mismatch is reported and the decoded forks kept. [ClassicMac]
- `ContainerReadOptions.MaxVolumeEntries` limits the entries; `MaxExpandedBytesPerInput` the input, the segments
  together and the total of the expanded forks. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.comment` | Info | The directory has a comment | Reports its text | Shows the comment |
| `archive.encrypted` | Warning | A file's flag bit 0 is set | Skips the file | Asks for the password |
| `archive.flags-unknown` | Warning | A file has flag bits other than 0–2 | Reads the file | Not traced |
| `archive.fork-crc` | Error | A file's fork checksum does not match | Keeps the decoded forks | Not traced |
| `archive.header-crc` | Error | The directory's CRC does not match (when read directly) | Reads the archive | Not traced |
| `archive.missing-volume` | Error | Segments of the set are missing | Reads the entries in the segments before the first missing one | Asks for the segment ([User's Guide](#9-references)) |
| `archive.missing-volume` | Warning | An entry's forks lie in or after a missing segment | Skips the entry | Not traced |

## 7. Verification

- `TestData/CompactPro152` (Compact Pro 1.52 on Mac OS 9.0, the synthetic file set of `TestData/StuffIt151` plus
  `Noise.bin`; `CompactPro152OriginalTests`):
  - `cp152` (`PACT`/`CPCT`): every file with both forks and Finder information; the forks before the directory;
    `Folder` with one child `Inner`; the fork checksum; LZH + RLE for `Big.txt`'s data and `ReadMe`'s resource fork,
    RLE for the others.
  - `cp152.sea` (`APPL`/`EXTR`): the data fork is `cp152` except header bytes +$02–+$03; the 13,057-byte extractor
    is not committed and a stand-in resource fork is used.
  - `cpnoise.#1`–`#3` (Misc > Segment…, 40 K): set id `$4287`; the last segment's directory offset (38,105) counts
    from its own start; `Noise.bin`'s 120,001-byte RLE data fork from offset 8 spans all three segments. The set is
    read from its last segment; earlier segments are not archives on their own; a missing segment is reported and its
    entries skipped; duplicate numbers are rejected; another set's siblings are ignored; the segments share the
    input-size limit.
- `TestData/CompactProMunbox/testfile.compact_pro_152.cpt` (from munbox, MIT; said to be made by Compact Pro 1.52, not
  confirmed; `CompactProSampleTests`): 27 files in two nested folders; every data fork matches munbox's MD5. Its LZH
  + RLE forks use the half-escape chain (`81 81 81`, `81 81 82`), its RLE-only forks do not, and no fork has
  `81 82 01`. Its directory ends the archive.
- Hand-built archives in `CompactProFeatureTests`: RLE forks with Finder information, the comment, nested folders,
  a `.sea`, a fork read from an earlier segment, forks overlapping the directory, malformed directory records, LZH
  literals in either fork, overlapping matches, truncated tables, the block boundary.

## 8. Not covered

- Encrypted entries.
- Sets written across floppies while saving, and what their volume byte means.

## 9. References

1. Bill Goodman, *Compact Pro User's Guide*, "Working With Segmented Archives",
   <https://oldapplestuff.com/download/Macintosh/Macintosh_Garden/manuals/Compact-Pro-Users-Guide.pdf>. The author's
   documentation.
2. Compact Pro format description, <https://docs.rs/crate/compact-pro/latest>. Licence not recorded; reference only.
3. XADMaster (The Unarchiver), `XADCompactProParser.m`. LGPL-2.1; reference only.
4. psx-spx, Compact Pro notes, <https://psx-spx.consoledev.net/ps1/cdr/cdromfileformats/compression/>.
   Documentation; licence not recorded.
5. pmarreck/compact_pro, its RLE rule as fixed against real archives. Licence not recorded; reference only.
6. munbox, <https://github.com/dafo123/munbox>, its Compact Pro sample archive. MIT; the sample is committed with
   notice (`THIRD-PARTY-NOTICES.md`), no code used.
