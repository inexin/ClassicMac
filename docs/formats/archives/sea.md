# Self-extracting archives

A self-extracting archive (`.sea`) is an application (type `APPL`) whose resource fork holds the extractor and whose
data fork is the archive, from offset 0 to the end of the fork. StuffIt, Compact Pro and DiskDoubler all make them.
ClassicMac needs no reader of its own: it finds the archive in the data fork like any other.

| | |
| --- | --- |
| Identified by | Type `APPL` with creator `aust` (StuffIt's classic extractor) or `EXTR` (Compact Pro); the data fork is recognised as an archive |
| ClassicMac | Reads, through the archive readers; `ClassicMac.Files.ContainerUnwrapper` |
| Verified against | StuffIt SEA 3.5 samples<br>StuffIt 5 extractors<br>Compact Pro 1.52 |
| Sources | The StuffIt extractor's code |

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

| Fork | Holds |
| --- | --- |
| Resource fork | The extractor: `CODE` and the other resources of the application |
| Data fork | The archive, from offset 0 to the end of the fork |

## 2. Reading

1. Read the application's data fork as an archive of the kinds in §4.
2. The extractor's resources are an ordinary resource fork.

## 3. Writing

None.

## 4. Variants

- StuffIt's classic extractor (creator `aust`): the data fork is a `SIT!` archive ([stuffit.md](stuffit.md)) whose
  archive length equals the fork's length. The extractor opens its own data fork by name and reads from offset 0
  without checking the signature [Code] [Verified: StuffIt SEA 3.5 samples].
- StuffIt 5 extractors: the data fork is a complete StuffIt 5 archive ([stuffit5.md](stuffit5.md)) whose length field
  equals the fork's length [Verified: StuffIt 5 extractors].
- Compact Pro's extractor (creator `EXTR`): the data fork is the archive ([compact-pro.md](compact-pro.md)), the same
  bytes as the archive saved without Self-Extracting except its id (header bytes +$02–+$03); the extractor is a
  13,057-byte resource fork [Verified: Compact Pro 1.52].
- DiskDoubler's `.sea` and `.prompt.sea` copies hold its archives ([diskdoubler.md](diskdoubler.md))
  [Verified: DiskDoubler Pro 4.1.1].

## 5. ClassicMac

- The unwrapper tries every reader on a file's data fork whatever its type, so a `.sea` lists as the application with
  the archive's files inside it. [ClassicMac]
- The extractor's own resources are often compressed with Aladdin's own `dcmp` (128); they are listed, not
  decompressed. [ClassicMac]

## 6. Diagnostics

None. The archive readers report their own.

## 7. Verification

- `TestData/CompactPro152/cp152.sea` (Compact Pro 1.52 on Mac OS 9.0, `APPL`/`EXTR`; `CompactPro152OriginalTests`):
  the data fork differs from `cp152` only in bytes +$02–+$03; the extractor is not committed and a stand-in resource
  fork is used.
- `SelfExtractingArchiveTests`: `TestData/StuffItLegacy45/StuffItDeluxe45.sit` as the data fork of an `APPL`/`aust`
  file with a stand-in resource fork unwraps as a StuffIt archive.
- `CompactProFeatureTests`: a hand-built Compact Pro `.sea`.
- The DiskDoubler corpus's `.sea` and `.prompt.sea` copies ([diskdoubler.md §7](diskdoubler.md#7-verification)).

## 8. Not covered

- InstallerMaker installers (creator `STi0`, data fork signature `ST65`) and other installer formats.
- Aladdin's `dcmp` 128.

## 9. References

None beyond the archive documents linked in §4.
