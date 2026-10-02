# Finder resources

The resources an application gives the Finder and the Process Manager: its bundle (`'BNDL'`), which ties its creator
code to its icons and the file types it handles; its file references (`'FREF'`); and its size resource (`'SIZE'`),
the memory partition and the events it handles. ClassicMac reads all three and writes each as JSON. Version
resources (`'vers'`) are in [version.md](version.md), icons in [icons.md](icons.md).

| | |
| --- | --- |
| Identified by | Resource types `'BNDL'`, `'FREF'`, `'SIZE'` |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Finder.FinderResources`, the decoders `finder.bundle`, `finder.file-reference`, `finder.size` |
| Verified against | Nothing yet |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials* (Finder Interface), *Inside Macintosh: Processes* (Process Manager), Apple's Rez templates (`Types.r`, MPW) |

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

### 1.1 Bundles (BNDL)

[Doc: Macintosh Toolbox Essentials] [Doc: `Types.r`]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `OSType`; the application's creator code |
| +$04 | 2 | Signature ID | `i16`; the ID of the signature resource, a resource of the creator's type (usually a version string; 0 by convention) |
| +$06 | 2 | Type count | `i16`; the number of types less one |
| +$08 | … | Type maps | One per type, in order |

Each type map:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Type | `OSType`: `'ICN#'`, `'FREF'`, … |
| +$04 | 2 | Mapping count | `i16`; the number of mappings less one |
| +$06 | 4 × n | Mappings | Each an `i16` local ID, then an `i16` resource ID |

### 1.2 File references (FREF)

[Doc: Macintosh Toolbox Essentials]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | File type | `OSType`; a type the application creates or opens (`'APPL'` for the application itself) |
| +$04 | 2 | Local icon ID | `i16`; an icon's local ID, through the bundle's `'ICN#'` map |
| +$06 | 1 + n | File name | Pascal string; unused by the Finder, usually empty |

### 1.3 Size resources (SIZE)

[Doc: Processes]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Flags | `u16`; below |
| +$02 | 4 | Preferred size | `u32`; the preferred partition size, in bytes |
| +$06 | 4 | Minimum size | `u32`; the minimum partition size, in bytes |

The flags, by their Rez names [Doc: `Types.r`]:

| Bit | Name |
| --- | --- |
| 15 | Reserved |
| 14 | `acceptSuspendResumeEvents` |
| 13 | Reserved |
| 12 | `canBackground` |
| 11 | `doesActivateOnFGSwitch` |
| 10 | `onlyBackground` |
| 9 | `getFrontClicks` |
| 8 | `acceptAppDiedEvents` |
| 7 | `is32BitCompatible` |
| 6 | `isHighLevelEventAware` |
| 5 | `localAndRemoteHLEvents` |
| 4 | `isStationeryAware` |
| 3 | `useTextEditServices` |
| 2 | `isDisplayManagerAware` |
| 1–0 | Reserved |

## 2. Reading

### 2.1 A file type's icon

The local IDs connect the bundle's maps [Doc: Macintosh Toolbox Essentials]:

1. In the bundle's `'FREF'` map, each mapping's resource ID is a `'FREF'` resource.
2. That `'FREF'`'s local icon ID is looked up among the local IDs of the `'ICN#'` map.
3. The mapping's resource ID is the ID of the icon family: the `'ICN#'`, `'icl4'`, `'icl8'`, `'ics#'`, … of that ID.

### 2.2 Which size resource

The Process Manager reads `'SIZE'` −1, as the developer set it, or `'SIZE'` 0 when the user changed the memory
requirements in the Finder's Get Info window [Doc: Processes].

## 3. Writing

None.

## 4. Variants

None.

## 5. ClassicMac

Each resource is written as one `.json` file, UTF-8, indented by two spaces, LF line ends, recording its text
encoding (`macintosh` by default). The decoders are version 1. [ClassicMac]

| Type | Decoder | Fields |
| --- | --- | --- |
| `BNDL` | `finder.bundle` | `signature`, `signatureId`, `maps[]` (`type`, `ids[]` of `local` and `resource`), and `fileTypes[]`: for each mapping of the `'FREF'` map whose `'FREF'` exists in the fork, `fileType`, `fref` (its ID) and `icon` (the icon family's resource ID by §2.1, or null when the `'ICN#'` map has no such local ID) |
| `FREF` | `finder.file-reference` | `fileType`, `localIconId`, `fileName` |
| `SIZE` | `finder.size` | `flags`, `flagNames` (the names of the bits set, from bit 15 down; reserved bits have none), `preferredSize`, `minimumSize` |

- Data that ends early is reported, and the JSON holds what was read: a bundle under 8 bytes keeps only its
  signature (when 4 bytes are there), a longer one the maps and mappings read whole; a `'FREF'` under 6 bytes keeps only its file type (when 4 bytes are there), local icon ID 0 and an empty name; a
  `'SIZE'` under 10 bytes keeps only its flags (when 2 bytes are there), with sizes 0. A `'FREF'` name that runs past
  the end is empty. [ClassicMac]
- Each `'SIZE'` resource is listed on its own; ClassicMac does not choose between −1 and 0. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `finder.short` | Warning | The data ends before the resource's fields do | Writes the JSON of what was read | Not traced |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs`, compared with `Golden/BNDL-128.json`,
  `FREF-128.json`, `FREF-129.json` and `SIZE--1.json` (`GoldenTests`), made in code with no Apple data: a bundle with
  `'ICN#'` and `'FREF'` maps, where `'FREF'` 128 (`APPL`, local icon 0) finds `'ICN#'` 128 and `'FREF'` 129 (`TEXT`,
  local icon 7) finds no icon; a `'SIZE'` −1 with four flags.

Nothing is checked against the resources of real applications.

## 8. Not covered

- The Finder's desktop database and how it caches bundles.
- The other Finder resources: `'open'`, `'kind'`, `'mstr'`, `'hfdr'`.
- Writing these resources.

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Finder Interface: bundles, file references, icon
   families.
2. Apple, *Inside Macintosh: Processes* (1992), Process Manager: the size resource.
3. Apple, MPW Rez templates (`Types.r`): field order and the `'SIZE'` flag names.
