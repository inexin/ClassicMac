# StuffIt split files

A file, usually a StuffIt archive, cut into segments to fit on floppy disks, by SegmentIt or by StuffIt 1.5.1's own
Other > Segment… command. Each segment is a 100-byte header and the next part of the file: its resource fork, then its
data fork. ClassicMac reassembles the set from any one segment with the others beside it, and the reassembled file
is then unwrapped like any other.

| | |
| --- | --- |
| Identified by | `$B0 $56` (SegmentIt) or `$41 $A7` (StuffIt 1.5.1) at +$00, `$00` at +$02, a segment number from 1 at +$03. StuffIt 1.5.1's segments are type `SegM`, creator `SIT!` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.StuffItSplitReader` |
| Verified against | StuffIt 1.5.1 on Mac OS 9.0 (`$41A7`) |
| Sources | Other readers (behaviour only): XADMaster (`$B056`) |

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

Each segment starts with a 100-byte header [Reference: XADMaster] [Fitted: StuffIt 1.5.1's segments]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Magic | `$B056` (SegmentIt) or `$41A7` (StuffIt 1.5.1) |
| +$02 | 2 | Segment number | From 1; the high byte is 0 |
| +$04 | 64 | File name | A Pascal string, 1–63 bytes, Mac OS Roman, no NUL byte; the bytes after it are not read |
| +$44 | 4 | File type | Of the file that was split |
| +$48 | 4 | Creator | |
| +$4C | 2 | Finder flags | |
| +$4E | 4 | Creation date | Mac date |
| +$52 | 4 | Modification date | Mac date |
| +$56 | 4 | Resource fork length | |
| +$5A | 4 | Data fork length | |
| +$5E | 6 | Reserved | Not read |

The payload follows the header: up to the segment size less 100 bytes of the file, in segment order. Joined in
segment order, the payloads are the resource fork followed by the data fork. Every segment of a set repeats the magic,
the file name and bytes +$44–+$5D.

## 2. Reading

1. Read the header of the segment given; check the magic, byte +$02 = 0, a segment number above 0 and a name of 1–63
   bytes.
2. Find the other segments: files beside it whose headers have the same magic, the same name bytes and the same
   bytes +$44–+$5D.
3. Every segment from 1 to the highest number found must be present.
4. Join the payloads in segment order, up to the resource length plus the data length; split them into the
   resource fork, then the data fork.
5. The file takes the header's name, type, creator, Finder flags and dates.

[Reference: XADMaster] [Fitted: StuffIt 1.5.1's segments]

## 3. Writing

None.

## 4. Variants

StuffIt 1.5.1's Segment command writes the SegmentIt layout with the magic `$41A7` in place of `$B056`. Its headers
leave the bytes after the file name and bytes +$5E–+$63 uninitialised (they differ between segments of one set),
and its Pascal file name may be shorter than the file's name: the sample set records `non` for `fx151_non.sit`
[Fitted: StuffIt 1.5.1's segments].

## 5. ClassicMac

- Siblings are the files in the same folder (or the same container) whose names start with the given segment's host
  name less its extension, case-insensitively, and whose headers match (§2 step 2). [ClassicMac]
- A missing segment, or segments that end before the forks' declared length, is reported and nothing is returned
  rather than truncated forks. Two siblings with the same segment number are an error. [ClassicMac]
- The reassembled file takes the header's name as it is, even when it is shorter than the original name. [ClassicMac]
- All segments together count against `ContainerReadOptions.MaxExpandedBytesPerInput`. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.missing-volume` | Warning | A segment of the set is not found, or the segments end before the declared forks | Returns nothing | Not traced |

## 7. Verification

- `TestData/StuffIt151/fx151_non.seg1`–`seg5` (StuffIt 1.5.1 on Mac OS 9.0; `StuffIt151OriginalTests`): five segments
  of the 41,974-byte `fx151_non.sit`. Opened from segment 1, 3 or 5 with the others beside it, they rebuild
  `fx151_non.sit` byte for byte with type and creator `SIT!`, and it expands; without segment 3 the set is reported
  as `archive.missing-volume`; the header is not recognised with another magic.
- Hand-built `$B056` volumes in `StuffItFeatureTests`: the probe, a set opened from its last volume with both forks and
  metadata, the host name given in the context, the default unwrapper, a missing volume, volumes ending before the
  declared forks, two volumes with the same number, the input-size limit.

## 8. Not covered

- A volume set made by the SegmentIt application itself.
- The meaning of the uninitialised bytes.

## 9. References

1. XADMaster (The Unarchiver), `XADStuffItSplitParser.m`. LGPL-2.1; reference only.
