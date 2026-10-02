# Code fragment resources (`'cfrg'`)

The `'cfrg'` resource lists the code fragments a file holds and where each one's container is. An application's is
`'cfrg'` 0: its presence makes a 68k application "fat" (PowerPC code beside the `'CODE'` segments). The Mac OS 9 System
file has twelve, naming the libraries in its data fork. ClassicMac reads version 1: its members, their names, usage
and location, and their extensions, including the search extension.

| | |
| --- | --- |
| Identified by | Resource type `'cfrg'`; version 1 at +$0A |
| ClassicMac | Reads; `ClassicMac.Code.Ppc.Cfrg` |
| Verified against | The Mac OS 9 System file (12 `'cfrg'`, 162 members); Disk Copy 6.1.2 |
| Sources | *Mac OS Runtime Architectures* (Apple, 1997), the code fragment resource; `CodeFragments.h` (`CFragResource`, `CFragResourceMember`, `CFragResourceSearchExtension`) |

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

### 1.1 Header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | reservedA | 0 |
| +$04 | 4 | reservedB | 0 |
| +$08 | 2 | reservedC | 0 |
| +$0A | 2 | version | 1 |
| +$0C | 16 | reservedD | Four longs, 0 |
| +$1C | 2 | reservedE | 0 |
| +$1E | 2 | memberCount | |

The members follow at +$20, each starting where the previous one's memberSize ends [Doc: CodeFragments.h].

### 1.2 Member

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | architecture | `'pwpc'`; `'m68k'` for CFM-68K |
| +$04 | 2 | reservedA | 0 |
| +$06 | 1 | reservedB | 0 |
| +$07 | 1 | updateLevel | 0 (kIsCompleteCFrag): a complete fragment; 1 and up (kFirstCFragUpdate …): an update to one |
| +$08 | 4 | currentVersion | Equals the container's currentVersion ([pef.md §1.1](pef.md#11-container-header)) |
| +$0C | 4 | oldDefVersion | |
| +$10 | 4 | usage1 | An application's stack size (appStackSize) |
| +$14 | 2 | usage2 | An application's subdirectory ID (appSubdirID); a library's flags (libFlags) |
| +$16 | 1 | usage | §1.4 |
| +$17 | 1 | where | §1.4 |
| +$18 | 4 | offset | The container's offset in the fork; 0 its start |
| +$1C | 4 | length | The container's length; 0 the rest of the fork |
| +$20 | 4 | where1 | uWhere1: an address space ID (spaceID) or a fork kind (forkKind) |
| +$24 | 2 | where2 | uWhere2: reserved, or a fork instance (forkInstance) |
| +$26 | 2 | extensionCount | |
| +$28 | 2 | memberSize | The whole member, name and extensions included |
| +$2A | 1 + n | name | Pascal string, Mac OS Roman: the fragment's name |

[Doc: CodeFragments.h] Every member of the samples has updateLevel 0, where1 and where2 0, and its reserved fields 0
[Verified: the Mac OS 9 System file, Disk Copy 6.1.2].

### 1.3 Padding and extensions

- Without extensions, memberSize is 43 + n rounded up to a multiple of 4 [Verified: the Mac OS 9 System file].
- With extensions, the name is padded so the extensions start on a 4-byte boundary from the member's start; the
  extensions follow one after another [Doc: CodeFragments.h] [Verified: the Mac OS 9 System file].

An extension:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | extensionKind | |
| +$02 | 2 | extensionSize | The whole extension, this 4-byte header included |
| +$04 | extensionSize − 4 | data | |

The search extension, kind `$30EE` (kCFragResourceSearchExtensionKind):

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | libKind | The library's kind: `'ndrv'`, `'otan'`, … |
| +$04 | … | qualifiers | Four Pascal strings, one after another |

[Doc: CodeFragments.h] [Verified: the Mac OS 9 System file]

### 1.4 Usage and where

| usage | Name | Meaning |
| --- | --- | --- |
| 0 | kImportLibraryCFrag | A shared library others import from |
| 1 | kApplicationCFrag | An application |
| 2 | kDropInAdditionCFrag | A plug-in its host loads |
| 3 | kStubLibraryCFrag | A stub library, for linking only |
| 4 | kWeakStubLibraryCFrag | A stub library whose imports are weak |

| where | Name | The container is |
| --- | --- | --- |
| 0 | kMemoryCFragLocator | Already in memory |
| 1 | kDataForkCFragLocator | In the file's data fork, at offset, length bytes |
| 2 | kResourceCFragLocator | In a resource of the file |
| 3 | kByteStreamCFragLocator | A byte stream |
| 4 | kNamedFragmentCFragLocator | Another fragment, by name |

[Doc: CodeFragments.h] Only usage 0 and 1 and where 1 occur in the samples [Verified: the Mac OS 9 System file,
Disk Copy 6.1.2].

## 2. Reading

1. Check the version (+$0A) is 1; read memberCount (+$1E).
2. From +$20, for each member: read the 42 fixed bytes and the name.
3. With extensions, start at the member's start plus the name's end rounded up to 4; read extensionCount extensions,
   each extensionSize long.
4. The next member starts memberSize bytes after this one.
5. For a member with `where` 1, the container is the data fork from offset, length bytes (to the end when 0); it
   starts with `Joy!peff` ([pef.md](pef.md)).

[Doc: CodeFragments.h] Every member of the System file's twelve resources lands on a PEF container in its data fork,
of the member's currentVersion [Verified: the Mac OS 9 System file].

## 3. Writing

None.

## 4. Variants

- A fat application has `'cfrg'` 0 with one application member in the data fork, offset 0, length 0 (Disk Copy 6.1.2:
  `DiskCopy.PPC`) [Verified: Disk Copy 6.1.2]. Its 68k code is [code-segments.md](code-segments.md).
- A library file lists several import-library members at their data-fork offsets; members of a driver or Open
  Transport module carry a search extension [Verified: the Mac OS 9 System file].

## 5. ClassicMac

- A resource shorter than its 32-byte header, or of another version, throws. [ClassicMac]
- Usage and where values outside the tables are kept as read. [ClassicMac]
- A member that runs past the resource or whose memberSize cannot hold its name stops the reading; the members before
  it are kept. An extension cut short stops that member's extensions; those before it are kept. [ClassicMac]
- A search extension too short for its four strings, or for its kind, is kept as an extension but gives no search
  information. [ClassicMac]
- Slicing the data fork and reading the container is the caller's ([pef.md](pef.md)). [ClassicMac]

A `'cfrg'` is exported as JSON and its data-fork fragments listed as [disassembly.md](../output/disassembly.md) describes.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `cfrg.extension-truncated` | Error | A member's extensions run past it, or an extension's size is under 4 or past the member | Keeps the extensions before it | Not traced |
| `cfrg.member-size` | Error | A memberSize is smaller than its fixed bytes and name, or runs past the resource | Stops reading members | Not traced |
| `cfrg.member-truncated` | Error | A member's fixed bytes run past the resource | Stops reading members | Not traced |

## 7. Verification

- `tests/ClassicMac.Code.Tests/CfrgTests.cs` (hand-built resources): every member field; the next member found
  memberSize bytes on, for the stored sizes 43 + the name padded to 4 and with slack; extensions after the padded
  name and after a name that needs no padding, ending at memberSize or leaving a gap; the search extension and the
  short ones that give none; no members; reserved fields ignored; a resource locator's fields kept as read; unknown
  usage and where values; the header and version checks; each damage case.
- Gated on `CLASSICMAC_CODE_CORPUS` (`CorpusTests`; skipped without it):
  - the Mac OS 9 System file: its twelve `'cfrg'` (IDs 0, 1, 4, 16, 25, 34, 49, 52, 55, 61, 64, 70) read without
    diagnostics with the expected member and extension counts, 162 members in all; every member is a `'pwpc'` import
    library in the data fork whose container reads, relocates and finds its exports
    ([pef.md §7](pef.md#7-verification)) and has the member's currentVersion; every search extension reads;
    `'cfrg'` 1's first member is `Resources` at `$85E10`, `$CAC0` bytes;
  - Disk Copy 6.1.2: one member, `DiskCopy.PPC`, an application in the data fork at offset 0, length 0.

## 8. Not covered

- Writing `'cfrg'`.
- Versions other than 1.
- What `libFlags`, the update level and the qualifiers mean to the Code Fragment Manager's search.
- Locators other than the data fork were never seen; ClassicMac reads their fields but does not follow them. Which
  fields name a kResourceCFragLocator's resource is not settled.

## 9. References

1. Apple Computer, *Mac OS Runtime Architectures* (1997), the code fragment resource.
2. Apple Computer, Universal Interfaces 3.4: `CodeFragments.h`.
