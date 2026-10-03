# Finder resources

The resources an application gives the Finder and the Process Manager: its bundle (`'BNDL'`), which ties its creator
code to its icons and the file types it handles; its file references (`'FREF'`); its kind strings (`'kind'`), the
names the Finder shows for its documents' kinds; and its size resource (`'SIZE'`), the memory partition and the events
it handles. ClassicMac reads all four, writes each as JSON, and names a document's kind from them (§2.3). Version
resources (`'vers'`) are in [version.md](version.md), icons in [icons.md](icons.md).

| | |
| --- | --- |
| Identified by | Resource types `'BNDL'`, `'FREF'`, `'kind'`, `'SIZE'` |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Finder.FinderResources`, the decoders `finder.bundle`, `finder.file-reference`, `finder.kind`, `finder.size`; `FinderKindResolver`, `KnownKinds`; `TypeCreatorDatabase` (a spreadsheet the user supplies, §2.6) |
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

[Code: Finder 9.2.2 KindResourceIsValid, InstallKindResource] [Verified: the 20 `'kind'` resources of a Mac OS 9.0
startup disk all read whole]

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | Signature | `OSType`; the creator code whose documents these kinds name; `'istd'` in the System's kinds of standard types (§2.3) |
| +$04 | 2 | Region | `i16`; the region code of the strings; only kinds of the system's own region are used, with no fallback to another |
| +$06 | 2 | Reserved | Must be 0, or the resource is ignored |
| +$08 | 2 | Count | `u16`; the number of entries (not less one) |
| +$0A | … | Entries | One per kind |

Each entry, padded with a zero byte to an even length:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | File type | `OSType`; `'apnm'` gives the application's own name instead of a kind |
| +$04 | 1 + n | Kind | Pascal string, Mac OS Roman; the desktop database keeps at most 63 characters |

- A resource under 8 bytes is ignored. [Code: Finder 9.2.2 KindResourceIsValid]
- The Finder copies kinds into the desktop database when it registers a file with a bundle (the hasBundle flag, or type
  `'APPL'`; also an uninited `'thng'` in the Extensions folder), keyed by (the `'kind'`'s signature, the file type).
  An entry already there is replaced only by a `'kind'` whose signature is the installing file's own creator. [Code:
  Finder 9.2.2 InstallKindResource]
- So one file may hold kinds for several signatures (Location Manager's `'walk'` and `'fall'`), and a signature need
  not be the file's own creator: ColorSync Extension (creator `'Sync'`) names `'sync'` files, and its `'kind'` never
  serves `'Sync'`. Keys are binary `OSType`s, so case counts. [Fitted]
- The System's kinds of standard types are a `'kind'` signed `'istd'` (System Resources' −16550, named "industry
  standards", on Mac OS 9.0).

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

The kind of a document of type *T* and creator *C*, as `GetDocumentKindString` finds it; the first step that gives a
string wins [Code: Translation library, GetDocumentKindString]. A file whose type has a kind of the Finder's own (§2.4)
does not get here.

1. The desktop database's kind for (*C*, *T*), of the system's region.
2. Its kind for (*C*, `'apnm'`) and " document": System Resources' `'STR#'` −16552 item 2, "^0 document", the result at
   most 64 bytes.
3. The file name of *C*'s application (`PBDTGetAPPL`: on the file's volume, then the startup volume, then the others)
   and " document", the same way.
4. The desktop database's kind for (`'istd'`, *T*), from System Resources' `'kind'` −16550.
5. "document", System Resources' `'STR#'` −16552 item 1.

So the application's name comes before the standard kinds: a `'TEXT'` file whose application is found reads
"*application* document", not "Text document". Stationery is not told apart.

Without a desktop database ClassicMac does the same from the volume's files: a `'kind'` matched by its signature,
then the creator's `'apnm'`, then the application's file name, then `'istd'`, then "document". [Fitted: equivalent to
the desktop database's contents after registering every file with a bundle]

### 2.4 The Finder's own kinds

The Finder names some items by their type before asking for a document's kind [Code: Finder 9.2.2]:

| Types | Kind |
| --- | --- |
| `'APPL'`, `'APPC'`, `'APPD'` | "application program" (the Finder's `'STR '` 6902) |
| `'dfil'` | "desk accessory" |
| `'DFIL'` | "desk accessory suitcase" |
| `'FFIL'` | "font suitcase" |
| `'ffil'`, `'tfil'`, `'sfnt'`, `'ttcf'` | "font" |
| `'kfil'` | "keyboard layout" |
| `'sfil'` | "sound" |
| `'ifil'` | "script" |
| `'edtP'`, `'edtp'`, `'edtT'`, `'edts'`, `'edtt'`, `'edtu'`, `'publ'` | "*application* edition" (the creator's application name), else "edition" |
| `'clpp'`, `'clpt'`, `'clps'`, other `'clp?'` | "picture clipping", "text clipping", "sound clipping", "clipping" |
| `'url '`, `'ilht'`, `'ilft'`, `'ilaf'`, `'ilfi'`, `'ilma'`, `'ilnw'`, `'ilat'`, `'ilge'`, `'ilns'` | The Finder's `'fmap'` 11010 into `'STR#'` 11000: "internet location", "web page location", "ftp location", "network location", "file location", "email address", "news location", "AppleTalk zone location", "internet location", "neighborhood location" [Fitted: the method] |
| `'slnk'` | "Mac OS X alias" |
| `'zsys'` | "system file" |
| System files: types in the Finder's `'fmap'` 5111 | Its `'STR#'` 5100 item *n* (below) |

`'fmap'` 5111 holds entries {`OSType` type, `i16` 0, `i16` *n*}, ended by a zero type. On Mac OS 9.0: 1 "Chooser
extension" (`'RDEV'`, `'PRER'`, `'PRES'`, `'pdvr'`); 2 "system extension" (`'INIT'`, `'thng'`, `'sLnk'`, `'adev'`,
`'mdev'`, `'appe'`, `'etpp'`, `'ttpp'`, `'atlk'`, `'scri'`, `'ndrv'`, `'comd'`, `'CLMP'`); 3 (`'fext'`); 4 "database
extension" (`'ddev'`); 5 "communications tool" (`'cbnd'`, `'tbnd'`, `'fbnd'`); 6 "control panel" (`'cdev'`); 7
"PostScript® font" (`'LWFN'`); 8 "printing extension" (`'pext'`); 9 "paper type" (`'drpt'`, `'uspt'`); 10 "control strip
module" (`'sdev'`); 11 "OpenDoc® editor" (`'oded'`); 12 "library" (`'shlb'`, `'libr'`); 13 "Text Encoding Converter
document" (`'utbl'`, `'ecpg'`); 14 "contextual menu plug-in" (`'cmpi'`); 15 "modem script" (`'mlts'`); 16 "scripting
addition" (`'osax'`).

- A file with the hasBundle flag and such a type keeps the Finder's kind ("system extension"); its `'kind'` names its
  documents. An application's own `'kind'` entry for `'APPL'` is not used for it. [Fitted]
- Other items: folders "folder" [Fitted]; disks "disk", server volumes "shared disk"; aliases (the isAlias flag)
  "alias"; the Trash "trash".
- Other files of the Finder and System (type `'FNDR'`, or creator `'MACS'` with a type not above) read "system file".
  [Fitted]

### 2.5 ClassicMac's table

When the volume does not name a kind, ClassicMac uses its own table (`KnownKinds`), in this order after §2.3's steps
1–3 [ClassicMac]:

1. The table's kind of (*T*, *C*): "SimpleText text document", "Apple Help page", "Microsoft Word 3–5 document", the
   archive and disk-image formats ClassicMac reads, and about 140 others.
2. §2.3 step 4, the System's `'istd'` kind.
3. The table's kind of *T* for any creator ("text document", "PICT picture", "StuffIt archive", …).
4. When the user supplied a type and creator database (§2.6), its kind of (*T*, *C*), else of *T* for any creator.
5. "*name* document", *name* being the table's name for *C* (about eighty applications), else the database's.
6. "document".

The table is ClassicMac's own words. Its codes were cross-checked against public type and creator lists (§9); no entry
or description was copied from them. The Finder's strings of §2.4 have English copies there, used when the volume has
no Finder to read them from.

### 2.6 A type and creator database the user supplies

A user may give ClassicMac a copy of a type and creator list as a spreadsheet (Office Open XML, `.xlsx`) laid out like
TCDB's (§9): one header row, then one row per file seen. It is not shipped with ClassicMac: TCDB grants no licence to
redistribute its data. [ClassicMac]

| Column (header) | Holds |
| --- | --- |
| File Name | An example file name, often "*application*—*kind*" ("SimpleText—AIFF Sound File") |
| Type | The file type |
| Creator | The creator; `****` or `????` for any creator |
| Comments | Usually the application or vendor; "Unspecified Creator" for none |
| Category | A rough category ("Graphics GIF", "Pref", "Application") |

Columns are found by their header, in any order; Type and Creator must be there. Only the first sheet
(`xl/worksheets/sheet1.xml`) and the shared strings (`xl/sharedStrings.xml`) are read, by ECMA-376: shared, inline
and formula strings are text; numbers are not. [Doc: ECMA-376 Part 1, SpreadsheetML]

1. Each cell's text is Mac OS Roman read as Windows-1252: its characters are turned back into bytes by Windows-1252
   (its five unassigned bytes as the control characters of the same value) and decoded as Mac OS Roman, so "Ñ" is
   the em dash and "ð" the Apple logo. A text with a character Windows-1252 cannot have written is kept as it is.
   [Fitted]
2. A type or creator is its cell's bytes, exactly four. Anything else is skipped, including the codes the spreadsheet
   turned into numbers ("26.2"). [Fitted]
3. A row of type `APPL` names its creator's application by its File Name; a row of type `****` or `????` names its
   creator by its Comments. An `APPL` row's name comes first.
4. Any other row gives a kind of (*T*, *C*), or of *T* for any creator: "*application* *kind*" when its File Name has
   an em dash, bullet or U+FFFD between them; else its Category; else "*Comments* document". A row with none of these
   is skipped. Of several rows for the same pair, the first of the best of these three wins.
5. The kind's application is the Comments, unless it is blank or "Unspecified Creator".

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
- `FinderKindResolver` stands in for the desktop database on one volume: `ForFiles` takes the files the Finder would
  register (an application type, or the hasBundle flag) and, for the System's and Finder's strings, the System
  Resources, Finder and System files (creator `'MACS'`). It follows §2.3 steps 1–4 (`Find`, `FindByApplication`,
  `FindStandard`) and §2.4 from the Finder's resources (`FindFinderKind`), with the region set when it is made (0 by
  default), and returns the kind with its source (`FinderKindSource`) and the resource it came from. A file's fork is
  read only when needed: first the files whose creator is asked about; when they do not have the kind, every file with
  a bundle, once. A fork that cannot be read counts as one with no `'kind'`. [ClassicMac]
- `KnownKinds.Resolve` gives every file a kind: §2.4 (from the volume's Finder, else the English copies), then §2.3
  steps 1–3, then §2.5; `KnownKinds.Describe` says where it came from ("from SimpleText’s 'kind' 128", "built-in").
  `KnownKinds.ResourceType` names resource types for the viewer. [ClassicMac]
- `TypeCreatorDatabase.Load` reads §2.6's spreadsheet with the .NET library's zip and XML readers (no spreadsheet
  package); a file that is no xlsx, or has no Type and Creator columns, is `InvalidDataException`. `Find` and
  `ApplicationName` answer §2.5 steps 4 and 5, and its kinds are described as "TCDB (your copy)". [ClassicMac]
- The app's View ▸ Type/Creator Database… chooses the file, reads it in the background and keeps its path in
  `settings.json` (`typeCreatorDatabase`); the next session reads it again at start. A kept file that cannot be read
  is reported in the status bar and stays kept; View ▸ Forget Type/Creator Database drops it. [ClassicMac]

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
- `tests/ClassicMac.Resources.Decoders.Tests/FinderKindTests.cs`: the layout (padding, a short resource), the
  resolver's steps, the region and the reserved word, the 63-character cut, kinds signed for another creator, the
  Finder's and System's strings, its laziness and damaged input, on forks made in code. `KnownKindsTests.cs`: §2.4's
  English copies, §2.5's order and the source descriptions.
- `tests/ClassicMac.App.Tests/FileKindsTests.cs` and the CLI's `Info_names_kinds_from_the_volumes_applications`: an
  HFS volume made with `HfsBuilder` holding an application with a `'kind'`, one with only a bundle, System Resources
  with `'istd'` kinds and documents of each, one whose application is missing.
- `tests/ClassicMac.Resources.Decoders.Tests/TypeCreatorDatabaseTests.cs`: §2.6 on spreadsheets made in code by
  `XlsxBuilder` (shared strings as rich-text runs, inline strings, number cells, columns in another order, the
  Windows-1252 decoding, codes of the wrong length, damaged files) and its place in §2.5's order. `FileKindsTests.cs`:
  choosing, keeping, reading at start and forgetting it in the app.
- TCDB 2003.8, read locally (not kept): about 44,200 rows give about 28,400 kinds, read in about a second.
- The 20 `'kind'` resources on a Mac OS 9.0 startup disk (SimpleText, Disk Copy, Keychain Access, Sherlock 2, Script
  Editor, Netscape Communicator, System Resources, control panels and extensions) all read whole with §1.4, and the
  kinds of its 949 files were listed through the app, checked locally; no Apple data is kept.

The other resources are not checked against those of real applications.

## 8. Not covered

- The Finder's desktop database and how it caches bundles.
- The other Finder resources: `'open'`, `'mstr'`, `'hfdr'`.
- The desktop database files themselves (Desktop DB, Desktop DF); kinds are rebuilt from the volume's files.
- `PBDTGetAPPL`'s search of other volumes: only the file's own volume is searched.
- Writing these resources.

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Finder Interface: bundles, file references, icon
   families.
2. Apple, *Inside Macintosh: Processes* (1992), Process Manager: the size resource.
3. Apple, MPW Rez templates (`Types.r`): field order and the `'SIZE'` flag names.
4. Cross-checks for the codes of §2.5's table only, none of whose entries or text is reused: Ilan Szekely, *TCDB*
   (Type/Creator Database, 2003, shareware, no reuse licence); macdisk.com's signature list; whitefiles.org's type and
   creator list; BYU's creator code list; Wikipedia, "Creator code". TCDB's spreadsheet is also the layout of §2.6,
   which reads a copy the user has; ClassicMac holds none of its data.
5. Ecma International, ECMA-376 *Office Open XML File Formats*, Part 1 (SpreadsheetML: shared strings, cells) and
   Part 2 (Open Packaging Conventions): the xlsx of §2.6.
