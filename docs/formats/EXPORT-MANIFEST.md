# Extracted resources and manifest.json — a specification

This document specifies what ClassicMac writes when it extracts the resources of a resource fork: the folder layout,
the names of folders and files, the files written for each resource, and `manifest.json`, the record of everything
written. It is complete enough for another tool to read an export (or to produce one that ClassicMac's tools accept)
without reading ClassicMac's code. The writer is `ResourceExporter` (`ClassicMac.Resources.Export`), driven by
`Unpacker.Extract` (`ClassicMac.Files.Export`) for inputs holding several forks; the image outputs come from
`ClassicMac.Resources.Decoders.Images`. The CLI command `extract` and the viewer's export commands use this code.

Unlike the other documents in this folder, this one describes a format ClassicMac defines itself. Mac data appears in
it only as the values the manifest records (types, IDs, attributes, Finder flags); how that data is read is specified
in [RESOURCE-FORK.md](RESOURCE-FORK.md), [CONTAINERS.md](CONTAINERS.md) and [HOST-FOLDERS.md](HOST-FOLDERS.md). How
icons, cursors, patterns and pictures are drawn is specified by QuickDraw.Pict's `docs/PICT-FORMAT.md` (sections 1–19,
section 18 for icons, cursors and patterns); this document covers only what ClassicMac adds on top.

Contents

1. [Conventions](#1-conventions)
2. [Overview](#2-overview)
3. [Output folders](#3-output-folders)
4. [Names on disk](#4-names-on-disk)
5. [The files written for a resource](#5-the-files-written-for-a-resource)
6. [manifest.json](#6-manifestjson)
7. [Decoders](#7-decoders)
8. [Image outputs](#8-image-outputs)
9. [Options](#9-options)
10. [The extract command](#10-the-extract-command)
11. [Diagnostics](#11-diagnostics)
12. [Reading and producing an export](#12-reading-and-producing-an-export)

---

## 1. Conventions

The shared conventions of [README.md](README.md) apply to the Mac values recorded here (big-endian fields, `OSType`,
Mac OS Roman text). In addition:

- **JSON** (`manifest.json` and the JSON files decoders write) is UTF-8 without a byte-order mark, as RFC 8259
  requires. Field names are camelCase.
- **Paths** in the manifest are relative to the folder holding `manifest.json` and use `/` as the separator on every
  host.
- **Hashes** are SHA-256, written as 64 lowercase hex digits.
- **Mac text in JSON** (a file name, a resource name, a type or creator code) is written as Unicode converted from
  Mac OS Roman, one character per byte, except that bytes `$00`–`$1F`, `$7F` and `$5C` (backslash) are written as
  `\xHH` (backslash, `x`, two uppercase hex digits). Because the backslash itself is escaped, the conversion is
  reversible: the original bytes can always be recovered. In the JSON source such a string holds a literal backslash,
  so it appears as `\\xHH` there. This is the same text `FourCC.ToString` and `MacString.ToString` give, and what the
  CLI's `--type` option reads back.
- **Host names** (folder and file names on the output disk) use a different escape, `%XX`; see section 4.
- A **fork** is a resource fork as ClassicMac reads it (a map of resources); a **resource** is one entry of it: type,
  ID, name, attribute byte and data. The **stored data** is a resource's bytes as they are in the fork; its **data**
  is what an application sees, which differs only for compressed resources (decompressed).

### Where the rules come from

The tags of [README.md](README.md) mark facts about Mac data: **[Doc]** for Apple's documentation, **[Code]** for
Apple's code. Nearly everything in this document is a design decision of ClassicMac's rather than a fact about the Mac,
and carries a tag of its own:

| Tag | Meaning |
| --- | --- |
| **[ClassicMac]** | A choice ClassicMac made for its own output format. No Mac software reads or writes it; another tool that consumes an export relies on the rule, but it could have been chosen otherwise |

Sections with no tag describe what the current code does, as a specification of format 1.2.

---

## 2. Overview

For each resource fork it exports, ClassicMac writes one **export folder**:

```
Realmz resources/
  manifest.json
  CODE/1 Main.bin
  PICT/128 Title Screen.png
  snd%20/200 Door.wav
  snd%20/200 Door.json
  STR#/-16455.json
  CURS/128.png
  CURS/128.json
  SICN/128.1.png
  SICN/128.2.png
  raw/CODE/1.bin              (only with KeepRaw)
  raw/PICT/128.bin
  document/index.html         (only for a document, with Documents)
  document/chapter-01.html
  document/images/pict-2067.png
  …
```

- One subfolder per resource type, named after the type (section 4.2). **[ClassicMac]**
- In it, the files for each resource, named `<id> <name>` or `<id>` plus an extension (section 4.3). A decoder may write
  several files for one resource; a resource no decoder handles is written as its data, `.bin` (section 5).
  **[ClassicMac]**
- With the KeepRaw option, `raw/<type folder>/<id>.bin` holds each resource's stored data (section 5.3).
  **[ClassicMac]**
- With document converters, a file that is a whole document (a DOCMaker or SimpleText document) is also converted
  into `document/` (section 6.10). **[ClassicMac]**
- `manifest.json` lists every resource with where it came from and what was written (section 6). It is written last:
  a folder without it is an incomplete export.

When an input holds several forks (a disk image, an archive), each gets its own export folder, placed as `unpack`
places files (section 3.2).

---

## 3. Output folders

### 3.1 One fork

`ResourceExporter.Export(fork, directory, source, options, dataFork)` writes one fork into `directory`, which it
creates if needed. `dataFork` reads the file's data fork, for a document converter that needs it (a SimpleText
document's text); without it, the data fork counts as empty.

- If `directory` already holds any file or folder and `Overwrite` is off, nothing is written and the export fails
  (`IOException`, "… is not empty"). **[ClassicMac]**
- With `Overwrite` on, files of the same names are replaced; other files already there are left in place and are not
  listed in the manifest.

### 3.2 Several forks

`Unpacker.Extract(root, forks, directory, options)` exports a list of forks found under one unwrapped input (a
container tree, see [HOST-FOLDERS.md](HOST-FOLDERS.md) and [CONTAINERS.md](CONTAINERS.md)):

- **One fork** in the list: it is exported straight into `directory`. **[ClassicMac]**
- **Several forks:** each is exported into a folder of its own, `directory/<placement>/<file name>`: **[ClassicMac]**
  - `<placement>` is the folder the file would get from `unpack`: folders inside volumes become folders; a container
    that in the end holds one file (MacBinary, BinHex, AppleSingle, a disk image of one file) is replaced by that
    file; one that holds several files, or folders, becomes a folder named after the outermost container of its chain.
    The rules are those of `OutputLayout`, specified with `unpack` in [HOST-FOLDERS.md](HOST-FOLDERS.md).
  - `<file name>` is the Mac file's name made host-safe (section 4.1, up to 255 characters), and made distinct from the
    other names in the same folder (section 4.4). The file's own export then goes inside that folder.
  - Each fork folder is checked for emptiness on its own (section 3.1). A fork whose folder cannot be written is
    reported and skipped; the others are still exported.
  - The length budget (section 4.3) of a fork's export is `MaxPathLength` minus the length of `<placement>/<file
    name>/`, but never less than 24.

The order of forks is the caller's. Folder names and the " ~2" suffixes they may get depend on that order.

### 3.3 Folders chosen by the CLI and the viewer

- **CLI `extract`:** the output folder is `-o <dir>`, or by default `<input name without extension> resources` next to
  the input. It is not numbered: an existing non-empty folder is refused unless `--overwrite` is given (section 10).
- **Viewer:** every export goes into a new folder made by `ExportFolders.CreateNew(parent, name)`, which tries `name`,
  then `name 2`, `name 3`, … and creates the first path that is neither a folder nor a file. It never writes into an
  existing folder. **[ClassicMac]**
  - *Export Resources* (a file, one resource type of a file, or an input that is a raw fork): folder
    `<file's host name> resources`; one fork, exported directly by `ResourceExporter` (only the selected type when a
    type is selected).
  - *Extract All Resources* (an input, a container or a folder): folder `<item's host name> resources`; every file
    under the item that has at least one resource, by `Unpacker.Extract`.
  - Both convert a file that is a document into `document/` (section 6.10), as `extract` does; *Export Resources* of
    one type does not.
  - *Convert Documents* (an input, a container, a folder or a file): folder `<item's host name> documents`, by
    `DocumentConverter.Convert` ([DOCUMENTS.md](DOCUMENTS.md) §7); when the item holds no document, the new folder is
    removed again.
  - *Save Resource As* writes one resource's decoded file (or its data as `.bin`) to a file the user chooses, with no
    manifest. Its suggested name is the stem of section 4.3 cut to 200 characters. It offers one file per kind,
    by its last extension: a list resource's first image as `.png`, a sidecar `.json` only when it is the only
    output.

---

## 4. Names on disk

Names must be valid and distinct on Windows, macOS and Linux, whose disks usually ignore case. The manifest, not the
name, is the authority for a resource's type, ID and name, so names only have to be safe and readable; they need not be
reversible. **[ClassicMac]**

### 4.1 Host-safe names

`HostNames.ToHostName(name, maxLength)` turns a Mac name (bytes) into a host name: **[ClassicMac]**

1. An empty name becomes `%00`.
2. Each byte becomes its Mac OS Roman character, except that it is written as `%XX` (the byte in uppercase hex) when
   it is a control character (`$00`–`$1F`, `$7F`), one of `% / \ : * ? " < > |`, or a space or dot that is the name's
   last byte.
3. If the name, up to its first `.` and with trailing spaces ignored, is a reserved Windows device name (`CON`, `PRN`,
   `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, in any case), the last character of that part is escaped: `COM1` →
   `COM%31`, `nul.txt` → `nu%6C.txt`.
4. If the result is longer than `maxLength` characters it is cut:
   - An **extension** is kept whole: the text from the last unescaped `.` when that dot is not the first character and
     starts at most 5 characters (counting an escape as one) from the end.
   - The rest is cut at the end, by whole characters and whole escapes (never inside a `%XX`), to fit
     `maxLength` minus the extension's length.
   - Trailing spaces and dots are then removed from the cut part, and the extension appended.

The result may therefore be shorter than `maxLength`. Lengths are counted in UTF-16 code units (characters from Mac OS
Roman are one each).

### 4.2 Type folders

The folder for a resource type is `HostNames.TypeFolder(type, collides)`: **[ClassicMac]**

- The type's four bytes as a host name (section 4.1): `PICT` → `PICT`, `snd ` → `snd%20`, `PAT ` → `PAT%20`,
  `STR#` → `STR#`, a type with a control byte `$01` → `%01…`.
- **Case collisions:** the distinct types of the resources being exported (after the `Types` filter, section 9) are
  grouped by their plain folder names compared without regard to case (ordinal, ignore-case). Every type in a group of
  two or more gets `~` and its four bytes as eight uppercase hex digits appended: `PICT` and `pict` become
  `PICT~50494354` and `pict~70696374`. Types that collide with nothing keep the plain name.
- No type folder can be named `raw`: a type is always four bytes, and a folder name from four bytes has at least four
  characters. The name `manifest.json` cannot collide either.

### 4.3 File stems and the length budget

Each resource's files share a **stem**: **[ClassicMac]**

- The Mac bytes of the ID in decimal (a leading `-` for negative IDs), a space and the resource's name: `128 Title
  Screen`, `-16455 Messages`.
- Only the ID when the resource has no name or an empty one: `128`.
- Converted by `ToHostName` (section 4.1) with a length limit, the **budget**:

  ```
  budget = max(8, MaxPathLength − length(type folder) − 1 − length(longest extension among the resource's files))
  ```

  `MaxPathLength` (default 200) counts from the export folder, so a file's path relative to the manifest,
  `<type folder>/<stem><extension>`, stays within it. The ID (at most 6 characters) always fits in 8.
- A Mac name that itself looks like it ends in an extension (`128 Read Me.txt`) keeps that part when the stem is cut,
  by rule 4 of section 4.1.

The limit is a target, not a guarantee: the minimum of 8, the ` ~N` suffix of section 4.4 and the fork folders of
section 3.2 can take a path past it.

### 4.4 Unique names

Within one type folder, each file name is made distinct from the names already written there, without regard to case,
by `HostNames.MakeUnique`: if `<stem><extension>` is taken, ` ~2`, ` ~3`, … is inserted before the part from the last
`.`: `1 Good.txt` → `1 Good ~2.txt`; `128.1.png` → `128.1 ~2.png`. **[ClassicMac]**

Since each stem starts with the ID, two resources collide only when a fork holds the same type and ID twice (a damaged
fork), or when one decoder writes two files with the same extension. Fork folders (section 3.2) are made unique the same
way within their parent folder.

---

## 5. The files written for a resource

Resources are exported in the fork's order. For each one:

1. Its **data** is read, decompressed if compressed, with `ReadOptions` (the Resource Manager model and size limits;
   see [RESOURCE-FORK.md](RESOURCE-FORK.md)). When decompression is impossible the stored data is used instead and the
   reason is reported.
2. The **first decoder** in `ExportOptions.Decoders` whose `CanDecode(type)` is true is asked for files (section 7).
3. Its files are written to the type folder, the first being the **main file**.
4. With KeepRaw, the stored data is written to `raw/` (section 5.3).
5. An entry is added to the manifest.

### 5.1 Decoded files

A decoder returns a list of files, each an extension (with its dot) and content; the main file comes first. Each is
named `<stem><extension>` (section 4). A decoder that writes several images gives them numbered extensions (`.1.png`,
`.2.png`, …; section 8.4). **[ClassicMac]**

### 5.2 The raw fallback

A resource is written **raw** — one file, `<stem>.bin`, holding its data (decompressed, as applications see it), with
decoder name `raw` and version 1 — when: **[ClassicMac]**

- no decoder handles its type (and always when `Decoders` is empty, as with the CLI's `--raw`);
- the decoder returns no files (it reports why, or the exporter adds `export.not-decoded`);
- the decoder throws a data error (`InvalidDataException`, `EndOfStreamException`, `ArgumentException`,
  `IndexOutOfRangeException`, `FormatException`, `OverflowException` or `NotSupportedException`); the exporter reports
  `export.decoder-failed`.

Any other exception is not caught by the exporter; `Unpacker.Extract` reports a file-system exception as a failed
fork folder.

### 5.3 KeepRaw and the raw folder

With `KeepRaw` on, every exported resource's **stored** data (as in the fork: still compressed if compressed) is also
written to `raw/<type folder>/<id>.bin`, and the manifest's `rawPath` points to it. The name holds only the ID, and is
not made unique: a fork with a duplicate type and ID writes that file twice, the last one winning. **[ClassicMac]**

The folder exists so that a fork can be rebuilt byte for byte (section 12). Its name has three characters, so it never
clashes with a type folder (section 4.2).

---

## 6. manifest.json

`manifest.json` is written in the export folder's root. Format 1 is described by the JSON Schema
`schemas/manifest-1.schema.json`, published at the URL the `$schema` field gives. The current version is **1.2**.

### 6.1 Versioning

- `formatVersion` is `"<major>.<minor>"`. A new optional field raises the minor version; any other change (a field
  removed, renamed or given another meaning) raises the major version and gets a new schema file. **[ClassicMac]**
- A reader rejects a manifest whose major version is newer than it knows, and ignores fields it does not know.
  **[ClassicMac]**
- Format 1.1 added `otherFiles` and `encoding` to resource entries. A reader of 1.x must accept their absence (1.0) and,
  as the schema allows, a `null` value.
- Format 1.2 added `document` at the top level. A reader must accept its absence (1.0, 1.1) and `null`.

### 6.2 Serialization

What ClassicMac writes (a reader must accept any valid JSON with the same content):

- UTF-8 without a byte-order mark, indented by two spaces, fields in the order of the tables below.
- Fields whose value is null are written as `null`, never left out.
- Characters outside ASCII, and a few ASCII ones (`<`, `>`, `&`, `'`, `+`, `` ` ``), are written as `\uXXXX` escapes
  (the default of `System.Text.Json`).
- Line breaks are LF on every platform, so manifests and their hashes are the same everywhere; there is no final line
  break.

### 6.3 The top level

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `$schema` | string | The schema's URL (not required by the schema; always written) | `"https://raw.githubusercontent.com/inexin/ClassicMac/main/schemas/manifest-1.schema.json"` |
| `formatVersion` | string, `1.<minor>` | The format's version (section 6.1) | `"1.2"` |
| `source` | object | The Mac file the fork came from (section 6.4) | |
| `fork` | object | The fork's own attributes (section 6.5) | |
| `resources` | array of objects | One entry per exported resource, in the fork's order (section 6.6) | |
| `diagnostics` | array of objects | Problems found reading the fork and exporting it (section 6.8) | `[]` |
| `document` | object or null | Since 1.2: the file converted as a whole document (section 6.10); `null` when it is none, or when no converter ran | `null` |

### 6.4 source

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `name` | string | The Mac file's name (Mac text, section 1) | `"Realmz"` |
| `formats` | array of strings | The formats the file was found through, outermost first (below) | `["host file", "MacBinary II"]` |
| `type` | string | The file type from its Finder info (Mac text, four bytes) | `"APPL"` |
| `creator` | string | The creator from its Finder info | `"RLMZ"` |
| `flags` | integer, 0–65535 | The Finder flags (`fdFlags`, [Doc] *Inside Macintosh: Macintosh Toolbox Essentials*, Finder Interface) | `8448` |

`type`, `creator` and `flags` are whatever Finder info the file has; a file with none (a raw fork file on a PC) gives
`"\\x00\\x00\\x00\\x00"` and 0.

`formats` names the chain of readers that led to the file. The CLI writes the full chain: the host format
(`host file`, `AppleDouble pair`, `Basilisk II folder`, `macOS named fork`, `PC Exchange folder`), then each container
and volume format as its reader names it (`MacBinary II`, `BinHex 4.0`, `AppleSingle`, …; see the documents in
[README.md](README.md)), and `data fork as resource fork` when the fork was found in the file's data fork. A plain file
read as a fork on its own gives `["raw resource fork"]`. The viewer writes less (section 12). The names are for people
and are not a closed list. **[ClassicMac]**

### 6.5 fork

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `attributes` | integer, 0–255 | The resource map's attribute byte (`mAttr`, map offset 22) as stored: `$80` read-only, `$40` compact, `$20` changed ([Doc] *Inside Macintosh: More Macintosh Toolbox*, Resource Manager) | `0` |
| `mapFlags` | integer, 0–255 | The byte after it (map offset 23) as stored, which the Resource Manager uses for in-memory flags (bit 0 is the 68k ROM's `decompressionPasswordBit`; [Code] 68k ROM `$077D`) | `0` |

Both are specified in [RESOURCE-FORK.md](RESOURCE-FORK.md).

### 6.6 resources[]

Every field below is always written in format 1.1.

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `type` | string | The type as Mac text (section 1) | `"snd "` |
| `typeBytes` | string, 8 uppercase hex digits | The type's four bytes; the authority when `type` is hard to read | `"736E6420"` |
| `id` | integer, −32768–32767 | The resource ID | `128` |
| `name` | string or null | The resource name as Mac text; `null` when it has none, `""` when it has an empty one | `"Door"` |
| `attributes` | integer, 0–255 | The attribute byte as stored: `$40` system heap, `$20` purgeable, `$10` locked, `$08` protected, `$04` preload, `$02` changed ([Doc] Resource Manager chapter), `$01` compressed ([Code] ROM and Mac OS 9 CheckLoad) | `32` |
| `size` | integer ≥ 0 | The main file's length in bytes | `176` |
| `storedSize` | integer ≥ 0 | The stored data's length (compressed length when compressed) | `106` |
| `dcmp` | integer or null | The `'dcmp'` ID from the compressed-resource header, when the compressed attribute is set and the header says the data is compressed; otherwise `null` | `2` |
| `decoder` | string | What wrote the files: `raw` for the data itself, otherwise the decoder's name (section 7) | `"sound.snd"` |
| `decoderVersion` | integer ≥ 1 | The decoder's version; 1 for `raw` | `1` |
| `path` | string | The main file, relative to the manifest | `"snd%20/200 Door.wav"` |
| `sha256` | string | SHA-256 of the main file | `"b9c6…1651"` |
| `storedSha256` | string | SHA-256 of the stored data | `"7078…793c"` |
| `rawPath` | string or null | The stored data's copy in `raw/` (section 5.3), or `null` without KeepRaw | `"raw/snd%20/200.bin"` |
| `warnings` | array of strings | The messages of every diagnostic raised for this resource (section 6.7) | `[]` |
| `otherFiles` | array of objects, or null | Since 1.1: the files the decoder wrote besides the main one, in its order (section 6.7); `[]` when none. ClassicMac never writes `null` | `[{"path": "snd%20/200 Door.json", "sha256": "738c…0d2d"}]` |
| `encoding` | string or null | Since 1.1: the text encoding the main file was decoded with, as an IANA name (`macintosh` for Mac OS Roman); `null` for outputs that are not decoded text, and for `raw` | `"macintosh"` |

Notes:

- For a `raw` resource, `size` is the data's length and the main file is the data. If the resource is not compressed,
  `sha256` equals `storedSha256`.
- `dcmp` records the header, not the outcome: when decompression fails, the main file of a `raw` resource holds the
  stored data and `size` equals `storedSize`, while `dcmp` still gives the header's ID. The resource's `warnings` say
  so.
- A resource flagged compressed whose data has no compressed-resource header has `dcmp` `null` and is used as stored.
- `id` and `attributes` are as read; the compressed-resource header and the `'dcmp'` IDs are specified in
  [RESOURCE-FORK.md](RESOURCE-FORK.md).

### 6.7 warnings and otherFiles

`warnings` holds the **message** of every diagnostic raised while this resource was decompressed, decoded and
exported, of any severity (despite its name it also holds `info` messages), in the order raised. The same diagnostics,
with their severity and code, are in the top-level `diagnostics` (section 6.8). Each message begins with the resource's
description, `'<type>' <id>` or `'<type>' <id> "<name>"`.

Each `otherFiles` entry:

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `path` | string | The file, relative to the manifest | `"CURS/128.json"` |
| `sha256` | string | SHA-256 of the file | `"7d60…409e"` |

### 6.8 diagnostics[]

The fork's own diagnostics (from reading it) come first, then those of each resource in the fork's order. A resource's
diagnostic identical to one already listed (same severity, code, message and offset) is not repeated.

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `severity` | string | `info`, `warning` or `error` | `"warning"` |
| `code` | string | The diagnostic's stable code (section 11) | `"image.undecodable"` |
| `message` | string | What happened, for people | `"'ICN#' 128: …"` |

A diagnostic's offset in the file, which the CLI prints, is not recorded.

### 6.9 Example

An excerpt of `tests/ClassicMac.Resources.Decoders.Tests/Golden/manifest.json`, the manifest of a fixture fork exported
with the built-in decoders (three of its 38 resources):

```json
{
  "$schema": "https://raw.githubusercontent.com/inexin/ClassicMac/main/schemas/manifest-1.schema.json",
  "formatVersion": "1.2",
  "source": {
    "name": "Fixtures",
    "formats": [
      "resource fork"
    ],
    "type": "rsrc",
    "creator": "RSED",
    "flags": 0
  },
  "fork": {
    "attributes": 0,
    "mapFlags": 0
  },
  "resources": [
    {
      "type": "STR ",
      "typeBytes": "53545220",
      "id": 128,
      "name": "Greeting",
      "attributes": 0,
      "size": 18,
      "storedSize": 15,
      "dcmp": null,
      "decoder": "text.string",
      "decoderVersion": 1,
      "path": "STR%20/128 Greeting.txt",
      "sha256": "184690f765edbfa0ef25f4a90af7b31491a84c39ce285fcf37f72c193e555080",
      "storedSha256": "86486182ed5a08c4b7970e80b68b5ffbd9f1d75c061f15ec33e863186e1fa05f",
      "rawPath": null,
      "warnings": [],
      "otherFiles": [],
      "encoding": "macintosh"
    },
    {
      "type": "CURS",
      "typeBytes": "43555253",
      "id": 128,
      "name": null,
      "attributes": 0,
      "size": 136,
      "storedSize": 68,
      "dcmp": null,
      "decoder": "image.cursor",
      "decoderVersion": 1,
      "path": "CURS/128.png",
      "sha256": "93df52c335bef6fdf54908aa7cf9fe9356b078ca75280529ac1f1fef178decdb",
      "storedSha256": "e5608ec24b295486114e0701deda234629e618658a47410db221157cab0ea189",
      "rawPath": null,
      "warnings": [],
      "otherFiles": [
        {
          "path": "CURS/128.json",
          "sha256": "7d60fd7634844685a232387888859d69d8cee8886ca2668f8fed61160dd7409e"
        }
      ],
      "encoding": null
    },
    {
      "type": "CODE",
      "typeBytes": "434F4445",
      "id": 1,
      "name": null,
      "attributes": 0,
      "size": 2,
      "storedSize": 2,
      "dcmp": null,
      "decoder": "raw",
      "decoderVersion": 1,
      "path": "CODE/1.bin",
      "sha256": "1ceeabf0c6a5a30bad12cdac0e3ab015a7188a42e6aebb556aad00bb9cd693ad",
      "storedSha256": "1ceeabf0c6a5a30bad12cdac0e3ab015a7188a42e6aebb556aad00bb9cd693ad",
      "rawPath": null,
      "warnings": [],
      "otherFiles": [],
      "encoding": null
    }
  ],
  "diagnostics": [],
  "document": null
}
```

A compressed resource exported with KeepRaw has, for instance, `"attributes": 1`, `"dcmp": 0`, `"size"` the
decompressed length, `"storedSize"` the stored length, and `"rawPath": "raw/CODE/1.bin"`.

### 6.10 document

With `ExportOptions.Documents` (section 9.1), the exporter asks each converter in turn whether the file is a document;
the first that gives files has them written into `document/` beside the type folders. Documents are converted only
when every type is exported (`Types` is null). **[ClassicMac]**

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `converter` | string | The converter's name | `"document.html"` |
| `converterVersion` | integer | The converter's version, raised when its output changes | `1` |
| `path` | string | The entry page, relative to the manifest | `"document/index.html"` |
| `files` | array of objects | Every file of the document, the entry page first: `path` (relative to the manifest) and `sha256` | `[{"path": "document/index.html", "sha256": "…"}]` |

The built-in converter, `document.html` version 1, converts DOCMaker and SimpleText documents to HTML
([DOCUMENTS.md](DOCUMENTS.md) §6). Its diagnostics (`document.*`) join the manifest's `diagnostics`.

---

## 7. Decoders

### 7.1 The decoder contract

A decoder (`IResourceDecoder`) has a **name** and a **version**, both recorded in the manifest, and says which types it
handles. Given a resource (`DecodeInput`: the resource, its data, access to other resources of the same fork,
decompressed, and a list for diagnostics) it returns its files, the main one first, or none when it cannot decode the
resource. **[ClassicMac]**

- The name is a dotted `<group>.<kind>` string, stable across releases.
- The version is raised whenever the decoder's output for the same input changes, so that a tool can tell outputs of
  different versions apart.
- Applications add decoders of their own for private types; the exporter uses the first in `ExportOptions.Decoders`
  that handles a type.

### 7.2 The built-in decoders

`ResourceDecoders.Create(DecodeOptions)` gives these, in this order; each type is handled by exactly one. All are at
version 1.

| Name | Types | Main file | Other files | `encoding` | Specified in |
| --- | --- | --- | --- | --- | --- |
| `text.string` | `STR ` | `.txt` | — | `macintosh` | [TEXT.md](TEXT.md) |
| `text.string-list` | `STR#` | `.json` | — | `macintosh` | [TEXT.md](TEXT.md) |
| `text.text` | `TEXT` | `.txt` | `.rtf` when a `styl` of the same ID exists | `macintosh` | [TEXT.md](TEXT.md) |
| `text.style` | `styl` | `.json` | — | null | [TEXT.md](TEXT.md) |
| `text.version` | `vers` | `.json` | — | `macintosh` | [TEXT.md](TEXT.md) |
| `image.picture` | `PICT` | `.png` | — | null | section 8.2 |
| `image.icon` | `ICON`, `ICN#`, `ics#`, `icm#`, `icl4`, `icl8`, `ics4`, `ics8`, `icm4`, `icm8`, `cicn`; `SICN` | `.png`; `SICN`: `.1.png` | `SICN`: `.2.png`, … | null | section 8.3 |
| `image.cursor` | `CURS`, `crsr` | `.png` | `.json` (section 8.5) | null | section 8.5 |
| `image.pattern` | `PAT `, `ppat`; `PAT#`, `ppt#` | `.png`; lists: `.1.png` | lists: `.2.png`, … | null | section 8.6 |
| `sound.snd` | `snd ` | `.wav`, or `.json` for a sound of commands only | `.json` after a `.wav` | null | [SOUND.md](SOUND.md) |
| `ui.menu`, `ui.menu-bar`, `ui.window`, `ui.dialog`, `ui.alert`, `ui.dialog-items`, `ui.control` | `MENU`, `MBAR`, `WIND`, `DLOG`, `ALRT`, `DITL`, `CNTL` | `.json` | — | `macintosh` | [INTERFACE.md](INTERFACE.md) |
| `color.table`, `color.palette` | `clut`, `pltt` | `.json` | `.act` | null | [PALETTES.md](PALETTES.md) |
| `ui.colors`, `ui.menu-colors`, `ui.item-colors`, `ui.dialog-extension`, `ui.alert-extension`, `ui.menu-extension` | `wctb`, `dctb`, `actb`, `cctb`; `mctb`; `ictb`; `dlgx`; `alrx`; `xmnu` | `.json` | — | null | [INTERFACE.md](INTERFACE.md) |

The image extensions are those of the configured image encoder (`.png` by default, section 8.1). Every other type,
`CODE` included, is written raw.

---

## 8. Image outputs

The image decoders hand each resource to QuickDraw.Pict (`PictReader` for pictures, `QuickDrawResources` for icons,
cursors and patterns), which draws it as the Mac would into a width × height grid of 8-bit RGBA pixels. How each
resource is laid out and drawn is QuickDraw.Pict's specification (`docs/PICT-FORMAT.md`, section 18 for icons, cursors
and patterns, section 19 for screen depths) and is not repeated here. ClassicMac adds the file encoding, the choice of
masks for colour icons, the cursor JSON, numbered list outputs and a size limit.

QuickDraw.Pict reports damaged data only by throwing. The image decoders turn such an exception (not-supported,
end-of-stream, argument, overflow, invalid-data, index-out-of-range) into `image.undecodable` with the library's
message, and the resource is written raw. **[ClassicMac]**

### 8.1 PNG

`PngEncoder` writes each image as a PNG (ISO/IEC 15948): **[ClassicMac]**

| Part | Content |
| --- | --- |
| Signature | `89 50 4E 47 0D 0A 1A 0A` |
| `IHDR` | width, height (`u32`), bit depth 8, colour type 6 (RGBA), compression 0, filter method 0, interlace 0 |
| `IDAT` | one chunk: a zlib stream of every row, top down, each preceded by filter type 0 (none) |
| `IEND` | empty |

- There are no other chunks: no gamma, colour profile, resolution or time. The pixels are exact: every value is the
  one QuickDraw.Pict drew. Alpha is straight (not premultiplied).
- The output depends only on the pixels and on the zlib implementation (.NET's `ZLibStream`, optimal level): the same
  image always gives the same bytes on one runtime, but a runtime with another zlib may compress differently. Compare
  decoded pixels, not file hashes, across runtimes.
- The encoder is chosen by `DecodeOptions.ImageEncoder` (`IImageEncoder`: a name, an extension and
  `Encode(width, height, rgba)`); PNG is the only one built in. The manifest records the extension through the file
  names but not the encoder's name.

### 8.2 Pictures (PICT)

- A `PICT` is drawn by `PictReader.Decode` at its native resolution, with QuickDraw.Pict's default options otherwise.
- **Screen depth:** `DecodeOptions.ScreenDepth` (32 by default; 1, 2, 4, 8 or 16) is the depth of the screen the
  picture is drawn on. Below 32, QuickDraw's colour matching and dithering for that depth apply (QuickDraw.Pict
  section 19); the file is still 8-bit RGBA. Only pictures use it; icons, cursors and patterns are drawn at full colour.
- **QuickDraw model:** Mac OS 9's QuickDraw by default, or the 68k ROM's when `DecodeOptions.QuickDraw` is
  `Rom68k` (QuickDraw.Pict section 17 lists the differences).
- **Pixel limit:** before drawing, the picture frame (`picFrame`, the `Rect` at bytes 2–9 of the resource) gives the
  canvas size. If width × height exceeds `DecodeOptions.MaxImagePixels` (64 Mi pixels, 67,108,864, by default), the
  picture is not drawn: `image.too-large`, and the resource is written raw. The check needs at least 10 bytes; shorter
  data goes to QuickDraw.Pict, which rejects it. **[ClassicMac]**
- Parts of the canvas the picture does not draw on are transparent (QuickDraw.Pict section 2.4).

### 8.3 Icons and their masks

| Types | Output |
| --- | --- |
| `ICON` | 32 × 32, black on white, opaque |
| `ICN#`, `ics#`, `icm#` | the first icon of the list, with the list's mask as transparency (mask bit 0 → alpha 0); a list without a mask half gets a computed mask (QuickDraw.Pict section 18) |
| `icl4`, `icl8`, `ics4`, `ics8`, `icm4`, `icm8` | the colour icon, masked by the icon list of the **same ID and size** in the same fork: `icl*` by `ICN#`, `ics*` by `ics#`, `icm*` by `icm#` |
| `cicn` | the colour icon with its own mask |
| `SICN` | one image per 16 × 16 icon, numbered (section 8.4), unmasked |

- The mask of a colour icon is taken as the Finder draws it: from the 1-bit list of the same ID (QuickDraw.Pict
  section 18). The list is read through `DecodeInput.Find`, decompressed if needed; diagnostics from reading it are
  reported against the colour icon.
- **No list:** when the fork has no such list, the colour icon is drawn fully opaque and `image.no-mask` (info) is
  reported; the icon is still decoded. The Mac's Icon Utilities draw nothing in that case (noMaskFoundErr, per
  QuickDraw.Pict section 18); ClassicMac prefers a visible image. **[ClassicMac]**

### 8.4 Numbered list outputs

A resource holding a list of images (`SICN`, `PAT#`, `ppt#`) is written as one file per item, in the list's order,
with extensions `.1.png`, `.2.png`, …: `SICN/128.1.png`, `SICN/128.2.png`. The first is the main file (`path`,
`size`, `sha256`); the rest are `otherFiles` in order. A list of no items gives no files, and the resource is written
raw. **[ClassicMac]**

### 8.5 Cursors

A cursor (`CURS`, `crsr`) gives two files, **[ClassicMac]**:

- `<stem>.png`, 16 × 16: the pixels the cursor paints (mask bit 1), black and white for `CURS`, the colour pixels for
  `crsr`; every other pixel transparent, including those where the cursor inverts or XORs the screen.
- `<stem>.json`, what a PNG cannot hold, described below.

On the Mac a cursor is drawn as `screen = (screen AND NOT mask) XOR image`: where the mask is 0, a `CURS` data bit 1
inverts the screen, and a `crsr` pixel XORs the screen with its complement (QuickDraw.Pict section 18). The JSON
records that as follows:

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `width` | integer | The image's width (16) | `16` |
| `height` | integer | The image's height (16) | `16` |
| `hotspot` | object | The hotspot: `h` (horizontal) and `v` (vertical), each clamped to 0–15 as SetCursor does | `{"h": 8, "v": 7}` |
| `inverted` | array of `height` strings | One string per row, top down, of `width` characters: `1` where the cursor inverts the screen (mask 0 and an XOR of `FFFFFF`), `0` elsewhere | `["1111000000010000", …]` |
| `xor` | array of `height` strings, optional | Written only when some pixel XORs the screen with a value other than `000000` (no change) or `FFFFFF` (invert), which only a `crsr` can do. One string per row of `width` values, separated by spaces, each six uppercase hex digits `RRGGBB`: the value XORed into the screen's RGB on a 32-bit screen; `000000` for pixels the cursor paints | `["000000 FFFFFF 7F3F00 …", …]` |

The JSON is written indented by two spaces, with non-ASCII characters as they are, and ends with a line feed.

### 8.6 Patterns

| Types | Output |
| --- | --- |
| `PAT ` | 8 × 8, black on white |
| `ppat` | the pixel pattern at its own size |
| `PAT#`, `ppt#` | one image per pattern, numbered (section 8.4) |

---

## 9. Options

### 9.1 ExportOptions

Every tunable value of an export (`ClassicMac.Resources.Export.ExportOptions`):

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Types` | set of types, or null | null (all) | Export only resources of these types. The filter applies before type-folder collisions are worked out (section 4.2) |
| `MaxPathLength` | integer | 200 | The longest a written path should be, in characters, counted from the export folder (sections 3.2, 4.3) |
| `KeepRaw` | boolean | false | Also write each resource's stored data to `raw/` (section 5.3) |
| `Overwrite` | boolean | false | Allow writing into an export folder that already holds files (section 3.1) |
| `Decoders` | list of decoders | empty | The decoders tried, in order; empty exports everything raw. `ResourceDecoders.Create` gives the built-in ones |
| `Documents` | list of document converters | empty | The converters tried for the whole file (section 6.10); empty converts none. `ResourceDecoders.CreateDocumentConverters` gives the built-in one |
| `ReadOptions` | `ReadOptions` | `ReadOptions.Default` | Size limits and the Resource Manager model (Mac OS 9 or 68k ROM) used to decompress resources ([RESOURCE-FORK.md](RESOURCE-FORK.md)) |

The fork's source is given separately, as an `ExportSource` (name, formats, type, creator, Finder flags), and becomes
the manifest's `source`.

### 9.2 DecodeOptions

The options of the built-in decoders (`ClassicMac.Resources.Decoders.DecodeOptions`):

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `TextEncoding` | enum | Mac OS Roman | The encoding text resources are read with; recorded as `encoding` |
| `LineEndings` | enum | LF | Line breaks in `.txt` output: LF, or as stored (CR) |
| `ImageEncoder` | `IImageEncoder` | `PngEncoder` | How images are written (section 8.1) |
| `ScreenDepth` | integer | 32 | The screen depth pictures are drawn at: 1, 2, 4, 8, 16 or 32 (section 8.2) |
| `MaxImagePixels` | integer | 67,108,864 | The largest picture drawn, in pixels (section 8.2) |
| `QuickDraw` | `ResourceManagerModel` | `MacOS9` | Whose QuickDraw pictures are drawn as: Mac OS 9's or the 68k ROM's |

---

## 10. The extract command

```
classicmac extract <input> [-o <dir>] [--raw] [--keep-raw] [-t <type>]… [--overwrite] [--screen-depth <n>] [--no-documents]
```

| Option | Maps to |
| --- | --- |
| `-o`, `--output <dir>` | The output folder; default `<input name without extension> resources` next to the input |
| `--raw` | `Decoders` and `Documents` empty: every resource written as its data, `.bin`, and no document |
| `--keep-raw` | `KeepRaw` |
| `-t`, `--type <type>` | `Types`; repeatable; four characters (`"snd "`) or `\xHH` escapes. A value that is not four characters is a usage error |
| `--overwrite` | `Overwrite` |
| `--screen-depth <n>` | `DecodeOptions.ScreenDepth`; one of 1, 2, 4, 8, 16, 32 (default 32) |
| `--no-documents` | `Documents` empty. By default the built-in converter runs (section 6.10) |
| `--max-resource-size`, `--max-nesting-depth`, `--max-expanded-bytes`, `--verify`, `--strict`, `-q` | The options every command takes: limits (`ReadOptions`, `ContainerReadOptions`), exit-code strictness and quiet output |

`MaxPathLength` is not exposed (200). Decoders other than the screen depth use their defaults; the QuickDraw model
follows `ReadOptions.ResourceManager` (Mac OS 9, as the CLI does not change it).

**What it does.** The input is opened and unwrapped as by `list` and `unpack`. A plain file that no container reader
recognises is read as a raw resource fork (formats `["raw resource fork"]`); otherwise each file inside is read for a
resource fork, or a data fork that holds one. Only forks with at least one resource of the chosen types are kept. They
are exported with `Unpacker.Extract` (section 3.2): straight into the output folder when there is one, a folder each
when there are several. Files without resources get no folder. A file that is a document also gets its `document/`
folder (section 6.10); the `convert` command writes documents alone ([DOCUMENTS.md](DOCUMENTS.md) §7).

**Output.** One line on stdout: `<n> resources from <m> files, to <folder>`. Diagnostics go to stderr, one per line:
`<source>: <severity>[ at <offset>]: <message> [<code>]`, where `<source>` is the input's name, followed by
` > <Mac path>` for a file inside it. With `-q` only errors are printed.

**Exit codes.**

| Code | Meaning for `extract` |
| --- | --- |
| 0 | Done. Warnings, if any, were printed |
| 1 | Done, but an error diagnostic was reported while reading or exporting (or a warning, with `--strict`) |
| 2 | The command line is wrong (an unknown option, a bad `--type` or `--screen-depth`, an input that does not exist) |
| 3 | The input is neither a Mac container nor a resource fork |
| 4 | Reading the input failed at the file-system level, or a fork's export folder could not be written (including an existing non-empty folder without `--overwrite`). Takes precedence over 1 |

---

## 11. Diagnostics

The exporter and the image decoders report these codes. Each goes to the resource's `warnings` (message only) and to
the manifest's `diagnostics`, and is printed by the CLI.

| Code | Severity | Raised by | Meaning |
| --- | --- | --- | --- |
| `export.decoder-failed` | Warning | `ResourceExporter` | The decoder threw a data error (section 5.2); the message gives the decoder and the error. The resource is written raw |
| `export.converter-failed` | Warning | `ResourceExporter`, `convert` | A document converter threw a data error; no document is written, and the next converter is tried |
| `export.not-decoded` | Info | `ResourceExporter` | The decoder returned no files and nothing had been reported for the resource (neither by decompression nor by the decoder). The resource is written raw |
| `image.undecodable` | Warning | image decoders | QuickDraw.Pict rejected the data (too short, a bad structure, an unsupported variant); the message is the library's. The resource is written raw |
| `image.too-large` | Warning | `image.picture` | The picture's frame is over `MaxImagePixels` (section 8.2). The resource is written raw |
| `image.no-mask` | Info | `image.icon` | A colour icon has no 1-bit icon list of the same ID for its mask; it is drawn opaque (section 8.3). The resource is still decoded |

Other codes reach the manifest from the code the exporter calls:

- reading the fork and decompressing resources (`fork.*`, `resource.*`): [RESOURCE-FORK.md](RESOURCE-FORK.md);
- the text decoders (`text.*`): [TEXT.md](TEXT.md);
- the sound decoder (`sound.*`): [SOUND.md](SOUND.md);
- the interface decoders (`ui.*`): [INTERFACE.md](INTERFACE.md);
- the palette decoders (`color.*`): [PALETTES.md](PALETTES.md);
- the document converter (`document.*`): [DOCUMENTS.md](DOCUMENTS.md).

The viewer also reports `export.failed` (Error) when an export cannot be written (a file-system error, or an export
folder that failed in `Unpacker.Extract`). It is shown in the viewer's diagnostics list only and never written to a
manifest.

---

## 12. Reading and producing an export

**Checking an export.** Every file listed (`path`, `otherFiles[].path`, `rawPath`, `document.files[].path`) exists and its SHA-256 matches;
`sha256` against the file, `storedSha256` against the `raw/` copy when there is one. Files in the folder that the
manifest does not list are not part of the export (section 3.1).

**Rebuilding a fork.** The manifest is designed so a fork can be rebuilt from an export (the planned `pack` command;
not built yet):

- The type comes from `typeBytes`, the ID from `id`, the name from `name` (unescape `\xHH`, then encode as Mac OS
  Roman; `null` means no name), the attribute byte from `attributes`, and the map's bytes from `fork`.
- The data comes from `rawPath` when present: the stored bytes, byte for byte, compressed or not.
- Without it, a `raw` resource's main file is its data **after** decompression; written back as it is, its
  compressed attribute (`$01`) no longer matches the data.
- A decoded resource's files are not the stored data. The plan is that a file whose hash is unchanged takes the
  original stored data (from `raw/` or from the original fork), and a changed file is re-encoded by an encoder for its
  decoder; `decoder` and `decoderVersion` say which.

**What the viewer records differently.** Exports from the viewer follow this specification, with two differences in
`source.formats`: *Export Resources* writes an empty list, and *Extract All Resources* only the format of the file's
own node, not the chain above it.

**Producing an export.** Another tool may write exports that ClassicMac's readers accept: any valid JSON meeting the
schema, with `formatVersion` `1.2` (or an earlier 1.x without the later fields), and files where the manifest says. The folder and
file names of sections 3 and 4 are ClassicMac's choice and are not required: a reader finds files through the
manifest's paths.
