# Classic Mac archives

This document records the archive formats implemented by `ClassicMac.Files.Archives`. StuffIt and Compact Pro have no
published vendor specifications in the project references; rules derived from another reader or from test fixtures are
provisional and marked **[Fitted]**. An independently generated fixture proves the documented behavior, not that the
original Mac application writes every detail the same way.

## StuffIt 5 (initial subset)

StuffIt 5 is recognized by the eight-byte `StuffIt ` signature and version byte 5 at offset 82. The 100-byte archive
header stores its total length at 84, the root member count at 92, and the first root member's absolute offset at 94.
An archive member begins with `A5 A5 A5 A5`; the `u16` at +6 gives its header length. The member header has flags at +9,
Mac creation and modification seconds at +10 and +14, the next sibling offset at +22, the name byte count at +30, and
the header CRC at +32. These field positions and encodings are **[Fitted]** by comparison with
[Deark's StuffIt reader](https://github.com/jsummers/deark/blob/master/modules/stuffit.c).

The member flag `$40` denotes a folder and `$20` denotes an encrypted member **[Fitted]**. Folder headers store the
first child offset at +34 and child count at +46. A file header stores logical and compressed data-fork lengths at
+34/+38, data-fork CRC at +42, and compression method at +46. The UTF-8 name follows the fixed fields (and any
password bytes). File-specific Finder information starts after the variable header: resource-fork presence is flag bit
0 of its `flags2`; type, creator and Finder flags follow. When present, resource-fork lengths, CRC and method precede
resource bytes, which precede data-fork bytes. These member details are **[Fitted]** against Deark's independent
parser and the hand-built vectors in `StuffItFeatureTests`; they have not yet been checked against a corpus created by
the original StuffIt application.

ClassicMac currently extracts method 0 (stored) and method 1 (RLE90) for either fork. RLE90 emits ordinary bytes as
literals; `$90 00` emits a literal `$90`; `$90 n` for nonzero `n` repeats the previously decoded byte until the run has
`n` copies **[Fitted]**. Truncated runs, runs without a prior byte, output-length mismatches and extents outside the
archive are rejected. Per-fork CRC mismatches are reported as errors while retaining the decoded file. Encrypted
entries and unsupported methods are reported and omitted; no password is requested. Files and folders are returned
through the normal `ContainerUnwrapper` pipeline, preserving UTF-8 paths, Finder type/creator/flags, dates and both
forks.

The v1–4 record layout, methods 2, 3, 5, 8, 13, 14 and 15, archive-level comments, complete folder metadata and
verification against original-application archives remain unimplemented. See Phase 10 in [the project plan](../PLAN.md).
