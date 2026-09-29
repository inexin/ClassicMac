# Finder resources — an implementer's specification

This document describes the resources an application gives the Finder and the Process Manager: its bundle
(`'BNDL'`), file references (`'FREF'`) and size resource (`'SIZE'`), completely enough to write a reader without
reading ClassicMac's code, and it specifies the JSON ClassicMac writes for them. Version resources (`'vers'`) are in
[TEXT.md](TEXT.md) §8; icons in [PICT-FORMAT.md](PICT-FORMAT.md) §18.

References:

- *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Finder Interface: bundles, file references, icon families.
- *Inside Macintosh: Processes* (1992), Process Manager: the size resource.
- Apple's Rez templates (`Types.r`, MPW): field order and the `'SIZE'` flag names.

Contents

1. [Conventions](#1-conventions)
2. [Bundles (BNDL)](#2-bundles-bndl)
3. [File references (FREF)](#3-file-references-fref)
4. [Size resources (SIZE)](#4-size-resources-size)
5. [JSON output](#5-json-output)
6. [Diagnostics](#6-diagnostics)

---

## 1. Conventions

The shared conventions of [README.md](README.md) hold. Tags are those of [README.md](README.md); **[ClassicMac]**
marks ClassicMac's own choices.

---

## 2. Bundles (BNDL)

A bundle ties an application's creator code to its icons and the file types it handles [Doc]:

| Size | Type | Meaning |
| --- | --- | --- |
| 4 | `OSType` | Signature: the application's creator code |
| 2 | `i16` | The signature resource's ID (a resource of the creator's type, usually a version string; 0 by convention) |
| 2 | `i16` | Number of types less one |
| … | | Per type: `OSType` type (`'ICN#'`, `'FREF'`), `i16` number of mappings less one, then per mapping an `i16` local ID and an `i16` resource ID |

The local IDs connect the maps: a `'FREF'`'s local icon ID is looked up in the `'ICN#'` map to find the resource ID of
the icon family (`'ICN#'`, `'icl4'`, `'icl8'`, `'ics#'`, … of that ID) [Doc].

---

## 3. File references (FREF)

| Size | Type | Meaning |
| --- | --- | --- |
| 4 | `OSType` | A file type the application creates or opens (`'APPL'` for the application itself) |
| 2 | `i16` | The local ID of its icon, through the bundle's `'ICN#'` map |
| 1 + *n* | Pascal string | A file name: unused by the Finder, usually empty |

---

## 4. Size resources (SIZE)

The Process Manager reads `'SIZE'` −1 (as the developer set it), or `'SIZE'` 0 when the user changed the memory
requirements in the Finder's Get Info window [Doc] (*Processes*):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `u16` | Flags |
| +$02 | 4 | `u32` | Preferred partition size, in bytes |
| +$06 | 4 | `u32` | Minimum partition size, in bytes |

Flags, from bit 15 [Doc] (Rez `Types.r`): 15 reserved, 14 `acceptSuspendResumeEvents`, 13 reserved, 12
`canBackground`, 11 `doesActivateOnFGSwitch`, 10 `onlyBackground`, 9 `getFrontClicks`, 8 `acceptAppDiedEvents`, 7
`is32BitCompatible`, 6 `isHighLevelEventAware`, 5 `localAndRemoteHLEvents`, 4 `isStationeryAware`, 3
`useTextEditServices`, 2 `isDisplayManagerAware`, 1–0 reserved.

---

## 5. JSON output

One `.json` file per resource, UTF-8, indented by two spaces, LF line ends [ClassicMac]:

| Type | Decoder | Fields |
| --- | --- | --- |
| `BNDL` | `finder.bundle` | `signature`, `signatureId`, `maps[]` (`type`, `ids[]` of `local` and `resource`), and `fileTypes[]`: for each `'FREF'` in the bundle that exists, `fileType`, `fref` (its ID) and `icon` (the icon family's resource ID, or null when the `'ICN#'` map has no such local ID) |
| `FREF` | `finder.file-reference` | `fileType`, `localIconId`, `fileName` |
| `SIZE` | `finder.size` | `flags`, `flagNames`, `preferredSize`, `minimumSize` |

All three are version 1 and record the encoding (`macintosh`).

---

## 6. Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| `finder.short` | Warning | The data ends before the resource's fields do; the JSON holds what was read |
