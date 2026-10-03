# Finder resources

The resources an application gives the Finder and the Process Manager: its bundle (`'BNDL'`), which ties its creator
code to its icons and the file types it handles; its file references (`'FREF'`); its kind strings (`'kind'`), the
names the Finder shows for its documents' kinds; and its size resource (`'SIZE'`), the memory partition and the events
it handles. ClassicMac reads all four, writes each as JSON, and names a document's kind from them (§2.3). Version
resources (`'vers'`) are in [version.md](version.md), icons in [icons.md](icons.md).

| | |
| --- | --- |
| Identified by | Resource types `'BNDL'`, `'FREF'`, `'kind'`, `'SIZE'` |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Finder.FinderResources`, the decoders `finder.bundle`, `finder.file-reference`, `finder.kind`, `finder.size`; `FinderKindResolver` |
| Verified against | The 20 `'kind'` resources on a Mac OS 9.0 startup disk, read whole (§1.4); the kinds the Finder shows were not compared |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials* (Finder Interface), *Inside Macintosh: Processes* (Process Manager), Apple's Rez templates (`Types.r`, MPW); Mac OS 9.0's own `'kind'` resources for the kind layout |

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

### 1.4 Kind strings (kind)

[Fitted: the 20 `'kind'` resources of a Mac OS 9.0 startup disk, every one read whole by this layout]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `OSType`; the creator code of the application whose documents these are; `'istd'` in the System's kinds of standard types (§2.3) |
| +$04 | 2 | Region | `i16`; the region code of the strings (0 in every one seen, the United States) |
| +$06 | 2 | Reserved | 0 in every one seen |
| +$08 | 2 | Count | `u16`; the number of entries (not less one) |
| +$0A | … | Entries | One per kind |

Each entry, padded with a zero byte to an even length:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | File type | `OSType`; `'apnm'` gives the application's own name instead of a kind |
| +$04 | 1 + n | Kind | Pascal string, Mac OS Roman |

- One file may hold several, for different signatures (Location Manager's `'walk'` and `'fall'`), and a signature need
  not be the file's own creator (ColorSync Extension, creator `'Sync'`, names `'sync'` files). [Fitted]
- The System's kinds of standard types are a `'kind'` signed `'istd'` (System Resources' −16550, named "industry
  standards", on Mac OS 9.0). [Fitted]

## 2. Reading

### 2.1 A file type's icon

The local IDs connect the bundle's maps [Doc: Macintosh Toolbox Essentials]:

1. In the bundle's `'FREF'` map, each mapping's resource ID is a `'FREF'` resource.
2. That `'FREF'`'s local icon ID is looked up among the local IDs of the `'ICN#'` map.
3. The mapping's resource ID is the ID of the icon family: the `'ICN#'`, `'icl4'`, `'icl8'`, `'ics#'`, … of that ID.

### 2.2 Which size resource

The Process Manager reads `'SIZE'` −1, as the developer set it, or `'SIZE'` 0 when the user changed the memory
requirements in the Finder's Get Info window [Doc: Processes].

### 2.3 A document's kind

How the Finder chooses the kind of a document of type *T* and creator *C*. The order below is fitted to the
resources on the disk and to what the Finder's strings allow; the Finder's code that chooses was not traced, and its
results were not compared on a running Mac. [Fitted]

1. The application whose signature is *C* is found: the Finder asks its desktop database; ClassicMac looks for a file
   on the volume with creator *C* that is an application (`'APPL'`, `'APPC'`, `'APPD'`, `'appe'`) or has the
   hasBundle flag.
2. If it is found and one of its `'kind'` resources signed *C* names *T* (resources in ID order, entries in order), that
   string is the kind.
3. If it is found but names no kind for *T*, the kind is "*name* document", *name* being its `'apnm'` entry, else its
   file name. The System keeps the pattern as `'STR#'` −16552 item 2, "^0 document", next to "document" (item 1).
4. Otherwise the System's `'kind'` signed `'istd'` (System Resources' −16550 on Mac OS 9.0) names standard types
   whatever their creator.
5. Otherwise the Finder shows "document"; ClassicMac's callers first try their own table of known types.

Folders, disks, applications and system files have kinds of the Finder's own, by type, with no `'kind'` resource:
the strings are in the Finder (`'STR#'` 1419: "folder", "System Folder", "Control Panels folder"…, "hard disk",
"application"; `'STR#'` 5100: "control panel", "system extension", "Chooser extension"…), but which type takes which
string is in the Finder's code, not traced.

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
| `kind` | `finder.kind` | `signature`, `region`, `kinds[]` of `type` and `kind` (the `'apnm'` entry included, as stored) |
| `SIZE` | `finder.size` | `flags`, `flagNames` (the names of the bits set, from bit 15 down; reserved bits have none), `preferredSize`, `minimumSize` |

- Data that ends early is reported, and the JSON holds what was read: a bundle under 8 bytes keeps only its
  signature (when 4 bytes are there), a longer one the maps and mappings read whole; a `'FREF'` under 6 bytes keeps only its file type (when 4 bytes are there), local icon ID 0 and an empty name; a
  `'SIZE'` under 10 bytes keeps only its flags (when 2 bytes are there), with sizes 0. A `'FREF'` name that runs past
  the end is empty. [ClassicMac]
- Each `'SIZE'` resource is listed on its own; ClassicMac does not choose between −1 and 0. [ClassicMac]
- A `'kind'` under 10 bytes keeps only its signature (when 4 bytes are there); a longer one keeps the entries read
  whole. Kind strings are read as Mac OS Roman. [ClassicMac]
- `FinderKindResolver` follows §2.3 steps 1–4 over the applications it is given (`FinderApplicationSource`: the
  signature, the file name, and a reader of the resource fork) and the System's forks, and returns the kind with its
  source (`FinderKindSource`: the application's `'kind'` and its ID, the application's name, the System's `'istd'`
  kind) and the application's name; null when none applies, for the caller's table. Each application's fork is read
  once, the first time one of its documents is asked about, and its kinds are kept; the first application given for a
  signature is used. A fork that cannot be read counts as one with no `'kind'`, a damaged `'kind'` as absent.
  `FinderKindResolver.IsApplicationType` says which file types are applications. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `finder.short` | Warning | The data ends before the resource's fields do | Writes the JSON of what was read | Not traced |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs`, compared with `Golden/BNDL-128.json`,
  `FREF-128.json`, `FREF-129.json` and `SIZE--1.json` (`GoldenTests`), made in code with no Apple data: a bundle with
  `'ICN#'` and `'FREF'` maps, where `'FREF'` 128 (`APPL`, local icon 0) finds `'ICN#'` 128 and `'FREF'` 129 (`TEXT`,
  local icon 7) finds no icon; a `'SIZE'` −1 with four flags; and `Golden/kind-128.json`, a `'kind'` with an
  `'apnm'` entry and two kinds, one of them padded.
- `tests/ClassicMac.Resources.Decoders.Tests/FinderKindTests.cs`: the layout (padding, a short resource) and the
  resolver's steps, its laziness (one read per application, none for other creators) and damaged input, on forks made
  in code.
- The 20 `'kind'` resources on a Mac OS 9.0 startup disk (SimpleText, Disk Copy, Keychain Access, Sherlock 2, Script
  Editor, Netscape Communicator, System Resources, control panels and extensions) all read whole with §1.4, checked
  locally; no Apple data is kept.

The other resources are not checked against those of real applications.

## 8. Not covered

- The Finder's desktop database and how it caches bundles.
- The other Finder resources: `'open'`, `'mstr'`, `'hfdr'`.
- The Finder's own kinds of folders, disks, applications and system files (§2.3), and a document's kind when its
  application's signature differs from its creator code.
- `'kind'` resources for other regions: the region code is read but not chosen by.
- Writing these resources.

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Finder Interface: bundles, file references, icon
   families.
2. Apple, *Inside Macintosh: Processes* (1992), Process Manager: the size resource.
3. Apple, MPW Rez templates (`Types.r`): field order and the `'SIZE'` flag names.
