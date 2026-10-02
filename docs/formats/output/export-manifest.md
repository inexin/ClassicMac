# Extracted resources and manifest.json

What ClassicMac writes when it extracts the resources of a resource fork: a folder per fork with a subfolder per
resource type, the files decoded from each resource (or its data), and `manifest.json`, the record of everything
written, format 1.2. The format is ClassicMac's own: no Mac software reads or writes it. Mac data appears in it only as
the values the manifest records (types, IDs, attributes, Finder flags); how that data is read is in
[resource-fork.md](../resources/resource-fork.md), [unwrapping.md](../containers/unwrapping.md) and
[host-folders.md](../containers/host-folders.md), and how icons, cursors, patterns and pictures are drawn in
[pict.md](../graphics/pict.md), [quickdraw.md](../graphics/quickdraw.md), [quicktime.md](../graphics/quicktime.md)
and [icons.md](../resources/icons.md). This document covers what ClassicMac adds, completely enough for another tool
to read an export, or produce one ClassicMac's tools accept, without reading ClassicMac's code. `extract` writes
exports, `pack` rebuilds a fork from one, and the viewer's export commands write them too.

| | |
| --- | --- |
| Identified by | A folder holding `manifest.json` whose `$schema` is `https://raw.githubusercontent.com/inexin/ClassicMac/main/schemas/manifest-1.schema.json` and whose `formatVersion` is `1.x` |
| ClassicMac | Writes and reads (`pack`); `ClassicMac.Resources.Export` (`ResourceExporter`, `ResourcePacker`, `ExportManifest`), `ClassicMac.Files.Export` (`Unpacker.Extract`), `ClassicMac.Resources.Decoders.Images` |
| Verified against | Nothing yet (a format of ClassicMac's own) |
| Sources | ClassicMac's design; the JSON Schema `schemas/manifest-1.schema.json`; RFC 8259 (JSON); ISO/IEC 15948 (PNG) |

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

Nearly every rule in this document is a choice ClassicMac made for its own output format, and is [ClassicMac] unless
it carries another tag: another tool that consumes an export relies on the rule, but it could have been chosen
otherwise. [Doc] and [Code] mark facts about the Mac data recorded.

### 1.1 Conventions

The shared conventions of [README.md](../README.md) apply to the Mac values recorded here (big-endian fields,
`OSType`, Mac OS Roman text). In addition:

- **JSON** (`manifest.json` and the JSON files decoders write) is UTF-8 without a byte-order mark, as RFC 8259
  requires. Field names are camelCase.
- **Paths** in the manifest are relative to the folder holding `manifest.json` and use `/` as the separator on every
  host.
- **Hashes** are SHA-256, written as 64 lowercase hex digits.
- **Mac text in JSON** (a file name, a resource name, a type or creator code) is written as Unicode converted from
  Mac OS Roman, one character per byte, except that bytes `$00`–`$1F`, `$7F` and `$5C` (backslash) are written as
  `\xHH` (backslash, `x`, two uppercase hex digits). Because the backslash itself is escaped, the conversion is
  reversible: the original bytes can always be recovered. In the JSON source such a string holds a literal backslash,
  so it appears as `\\xHH` there. The CLI's `--type` option reads the same text.
- **Host names** (folder and file names on the output disk) use a different escape, `%XX` (§3.3).
- A **fork** is a resource fork as ClassicMac reads it (a map of resources); a **resource** is one entry of it: type,
  ID, name, attribute byte and data. The **stored data** is a resource's bytes as they are in the fork; its **data**
  is what an application sees, which differs only for compressed resources (decompressed).

### 1.2 The export folder

One export folder per fork:

```
Realmz resources/
  manifest.json
  CODE/1 Main.bin
  CODE/1 Main.s
  CODE/1 Main.json
  PICT/128 Title Screen.png
  snd%20/200 Door.wav
  snd%20/200 Door.json
  STR#/-16455.json
  CURS/128.png
  CURS/128.json
  SICN/128.1.png
  SICN/128.2.png
  raw/CODE/1.bin              (only with raw copies kept)
  raw/PICT/128.bin
  document/index.html         (only for a document, with document converters)
  document/chapter-01.html
  document/images/pict-2067.png
  …
```

| Entry | Content |
| --- | --- |
| `<type folder>/` | One per resource type (§3.4) |
| `<type folder>/<stem><extension>` | The files for each resource, the stem `<id> <name>` or `<id>` (§3.5). A decoder may write several files for one resource; a resource no decoder handles is written as its data, `.bin` (§3.7) |
| `raw/<type folder>/<id>.bin` | With raw copies kept: each resource's stored data (§3.7) |
| `document/` | With document converters: a file that is a whole document (DOCMaker or SimpleText) converted (§3.14) |
| `manifest.json` | Every resource, with where it came from and what was written (§1.3). Written last: a folder without it is an incomplete export |

When an input holds several forks (a disk image, an archive), each gets its own export folder, placed as `unpack`
places files (§3.2).

### 1.3 manifest.json

`manifest.json` is written in the export folder's root. Format 1 is described by the JSON Schema
`schemas/manifest-1.schema.json`, published at the URL its `$schema` field gives. The current version is 1.2.

- `formatVersion` is `"<major>.<minor>"`. A new optional field raises the minor version; any other change (a field
  removed, renamed or given another meaning) raises the major version and gets a new schema file.
- A reader rejects a manifest whose major version is newer than it knows, and ignores fields it does not know.
- What ClassicMac writes (a reader must accept any valid JSON with the same content):
  - UTF-8 without a byte-order mark, indented by two spaces, fields in the order of the tables below;
  - fields whose value is null are written as `null`, never left out;
  - characters outside ASCII, and a few ASCII ones (`<`, `>`, `&`, `'`, `+`, `` ` ``), are written as `\uXXXX`
    escapes;
  - line breaks are LF on every platform, so manifests and their hashes are the same everywhere; there is no final
    line break.

### 1.4 The top level

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `$schema` | string | The schema's URL (not required by the schema; always written) | `"https://raw.githubusercontent.com/inexin/ClassicMac/main/schemas/manifest-1.schema.json"` |
| `formatVersion` | string, `1.<minor>` | The format's version (§1.3) | `"1.2"` |
| `source` | object | The Mac file the fork came from (§1.5) | |
| `fork` | object | The fork's own attributes (§1.6) | |
| `resources` | array of objects | One entry per exported resource, in the fork's order (§1.7) | |
| `diagnostics` | array of objects | Problems found reading the fork and exporting it (§1.9) | `[]` |
| `document` | object or null | Since 1.2: the file converted as a whole document (§1.10); `null` when it is none, or when no converter ran | `null` |

### 1.5 source

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `name` | string | The Mac file's name (Mac text, §1.1) | `"Realmz"` |
| `formats` | array of strings | The formats the file was found through, outermost first (below) | `["host file", "MacBinary II"]` |
| `type` | string | The file type from its Finder info (Mac text, four bytes) | `"APPL"` |
| `creator` | string | The creator from its Finder info | `"RLMZ"` |
| `flags` | integer, 0–65535 | The Finder flags (`fdFlags` [Doc: *Inside Macintosh: Macintosh Toolbox Essentials*, Finder Interface]) | `8448` |

- `type`, `creator` and `flags` are whatever Finder info the file has; a file with none (a raw fork file on a PC)
  gives `"\\x00\\x00\\x00\\x00"` and 0.
- `formats` names the chain of readers that led to the file: the host format (`host file`, `AppleDouble pair`,
  `Basilisk II folder`, `macOS named fork`, `PC Exchange folder`), then each container and volume format as its reader
  names it (`MacBinary II`, `BinHex 4.0`, `AppleSingle`, …; see the documents in [README.md](../README.md)), and
  `data fork as resource fork` when the fork was found in the file's data fork. A plain file read as a fork on its own
  gives `["raw resource fork"]`. The names are for people and are not a closed list. The viewer writes less (§4.2).

### 1.6 fork

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `attributes` | integer, 0–255 | The resource map's attribute byte (`mAttr`, map offset 22) as stored: `$80` read-only, `$40` compact, `$20` changed [Doc: *Inside Macintosh: More Macintosh Toolbox*, Resource Manager] | `0` |
| `mapFlags` | integer, 0–255 | The byte after it (map offset 23) as stored, which the Resource Manager uses for in-memory flags (bit 0 is the 68k ROM's `decompressionPasswordBit` [Code: 68k ROM `$077D`]) | `0` |

Both are specified in [resource-fork.md](../resources/resource-fork.md).

### 1.7 resources[]

Every field below is always written.

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `type` | string | The type as Mac text (§1.1) | `"snd "` |
| `typeBytes` | string, 8 uppercase hex digits | The type's four bytes; the authority when `type` is hard to read | `"736E6420"` |
| `id` | integer, −32768–32767 | The resource ID, as read | `128` |
| `name` | string or null | The resource name as Mac text; `null` when it has none, `""` when it has an empty one | `"Door"` |
| `attributes` | integer, 0–255 | The attribute byte as read: `$40` system heap, `$20` purgeable, `$10` locked, `$08` protected, `$04` preload, `$02` changed [Doc: Resource Manager chapter], `$01` compressed [Code: ROM and Mac OS 9 CheckLoad] | `32` |
| `size` | integer ≥ 0 | The main file's length in bytes | `176` |
| `storedSize` | integer ≥ 0 | The stored data's length (compressed length when compressed) | `106` |
| `dcmp` | integer or null | The `'dcmp'` ID from the compressed-resource header, when the compressed attribute is set and the header says the data is compressed; otherwise `null` | `2` |
| `decoder` | string | What wrote the files: `raw` for the data itself, otherwise the decoder's name (§3.8) | `"sound.snd"` |
| `decoderVersion` | integer ≥ 1 | The decoder's version; 1 for `raw` | `1` |
| `path` | string | The main file, relative to the manifest | `"snd%20/200 Door.wav"` |
| `sha256` | string | SHA-256 of the main file | `"b9c6…1651"` |
| `storedSha256` | string | SHA-256 of the stored data | `"7078…793c"` |
| `rawPath` | string or null | The stored data's copy in `raw/` (§3.7), or `null` without raw copies | `"raw/snd%20/200.bin"` |
| `warnings` | array of strings | The messages of every diagnostic raised for this resource (§1.8) | `[]` |
| `otherFiles` | array of objects, or null | Since 1.1: the files the decoder wrote besides the main one, in its order (§1.8); `[]` when none. ClassicMac never writes `null` | `[{"path": "snd%20/200 Door.json", "sha256": "738c…0d2d"}]` |
| `encoding` | string or null | Since 1.1: the text encoding the main file was decoded with, as an IANA name (`macintosh` for Mac OS Roman); `null` for outputs that are not decoded text, and for `raw` | `"macintosh"` |

- For a `raw` resource, `size` is the data's length and the main file is the data. If the resource is not compressed,
  `sha256` equals `storedSha256`.
- `dcmp` records the header, not the outcome: when decompression fails, the main file of a `raw` resource holds the
  stored data and `size` equals `storedSize`, while `dcmp` still gives the header's ID. The resource's `warnings` say
  so.
- A resource flagged compressed whose data has no compressed-resource header has `dcmp` `null` and is used as stored.
- The compressed-resource header and the `'dcmp'` IDs are specified in
  [compressed-resources.md](../resources/compressed-resources.md).

### 1.8 warnings and otherFiles

`warnings` holds the message of every diagnostic raised while this resource was decompressed, decoded and exported,
of any severity (despite its name it also holds `info` messages), in the order raised. The same diagnostics, with
their severity and code, are in the top-level `diagnostics` (§1.9). Each message begins with the resource's
description, `'<type>' <id>` or `'<type>' <id> "<name>"`.

Each `otherFiles` entry:

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `path` | string | The file, relative to the manifest | `"CURS/128.json"` |
| `sha256` | string | SHA-256 of the file | `"7d60…409e"` |

### 1.9 diagnostics[]

The fork's own diagnostics (from reading it) come first, then those of each resource in the fork's order. A
resource's diagnostic identical to one already listed (same severity, code, message and offset) is not repeated.

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `severity` | string | `info`, `warning` or `error` | `"warning"` |
| `code` | string | The diagnostic's stable code (§6) | `"image.undecodable"` |
| `message` | string | What happened, for people | `"'ICN#' 128: …"` |

A diagnostic's offset in the file, which the CLI prints, is not recorded.

### 1.10 document

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `converter` | string | The converter's name | `"document.html"` |
| `converterVersion` | integer | The converter's version, raised when its output changes | `1` |
| `path` | string | The entry page, relative to the manifest | `"document/index.html"` |
| `files` | array of objects | Every file of the document, the entry page first: `path` (relative to the manifest) and `sha256` | `[{"path": "document/index.html", "sha256": "…"}]` |

### 1.11 Example

An excerpt of the manifest of a fixture fork exported with the built-in decoders (three of its 116 resources; §7):

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
      "name": "Main",
      "attributes": 0,
      "size": 14,
      "storedSize": 14,
      "dcmp": null,
      "decoder": "code.segment",
      "decoderVersion": 1,
      "path": "CODE/1 Main.bin",
      "sha256": "cc86903d7627e46f7008a05f9473b7530f2b3bbc5326b51a9bcd319b4f07072a",
      "storedSha256": "cc86903d7627e46f7008a05f9473b7530f2b3bbc5326b51a9bcd319b4f07072a",
      "rawPath": null,
      "warnings": [],
      "otherFiles": [
        {
          "path": "CODE/1 Main.s",
          "sha256": "bb6d41ab35c192549e55b2a289e033b59875c56420d7557a72fd231a66dd858f"
        },
        {
          "path": "CODE/1 Main.json",
          "sha256": "fb925490faecfde9fdb95762caf5ab172534b9a9ab780dbd16c2c7358a1a5a34"
        }
      ],
      "encoding": null
    }
  ],
  "diagnostics": [],
  "document": null
}
```

A resource no decoder handles (the fixture's `DATA` 0) has `"decoder": "raw"`, `"path": "DATA/0.bin"` and
`sha256` equal to `storedSha256`. A compressed resource exported with raw copies has, for instance, `"attributes": 1`, `"dcmp": 0`, `"size"` the
decompressed length, `"storedSize"` the stored length, and `"rawPath": "raw/CODE/1.bin"`.

### 1.12 PNG files

Each image is written as a PNG (ISO/IEC 15948):

| Part | Content |
| --- | --- |
| Signature | `89 50 4E 47 0D 0A 1A 0A` |
| `IHDR` | width, height (`u32`), bit depth 8, colour type 6 (RGBA), compression 0, filter method 0, interlace 0 |
| `IDAT` | one chunk: a zlib stream of every row, top down, each preceded by filter type 0 (none) |
| `IEND` | empty |

- There are no other chunks: no gamma, colour profile, resolution or time. The pixels are exact: every value is the
  one the renderer drew. Alpha is straight (not premultiplied).
- The output depends only on the pixels and on the zlib implementation (.NET's `ZLibStream`, optimal level): the same
  image always gives the same bytes on one runtime, but a runtime with another zlib may compress differently. Compare
  decoded pixels, not file hashes, across runtimes.

### 1.13 Cursor JSON

`<stem>.json`, written beside a cursor's PNG (§3.12), holds what a PNG cannot:

| Name | Type | Meaning | Example |
| --- | --- | --- | --- |
| `width` | integer | The image's width (16) | `16` |
| `height` | integer | The image's height (16) | `16` |
| `hotspot` | object | The hotspot: `h` (horizontal) and `v` (vertical), each clamped to 0–15 as SetCursor does | `{"h": 8, "v": 7}` |
| `inverted` | array of `height` strings | One string per row, top down, of `width` characters: `1` where the cursor inverts the screen (mask 0 and an XOR of `FFFFFF`), `0` elsewhere | `["1111000000010000", …]` |
| `xor` | array of `height` strings, optional | Written only when some pixel XORs the screen with a value other than `000000` (no change) or `FFFFFF` (invert), which only a `crsr` can do. One string per row of `width` values, separated by spaces, each six uppercase hex digits `RRGGBB`: the value XORed into the screen's RGB on a 32-bit screen; `000000` for pixels the cursor paints | `["000000 FFFFFF 7F3F00 …", …]` |

The JSON is written indented by two spaces, with non-ASCII characters as they are, and ends with a line feed.

## 2. Reading

### 2.1 Checking an export

1. Every file listed (`path`, `otherFiles[].path`, `rawPath`, `document.files[].path`) exists and its SHA-256
   matches: `sha256` against the file, `storedSha256` against the `raw/` copy when there is one.
2. Files in the folder that the manifest does not list are not part of the export (§3.1).

### 2.2 Rebuilding a fork

1. A manifest whose major version is not 1 is refused.
2. The fork's attribute and map-flag bytes come from `fork`.
3. The resources, in the manifest's order, take their type from `typeBytes`, ID from `id`, name from `name` (`\xHH`
   unescaped, the rest encoded as Mac OS Roman; `null`: no name) and attribute byte from `attributes`.
4. A main file whose SHA-256 equals `sha256` is unchanged: the resource takes its stored data, byte for byte,
   compressed or not, from the `raw/` copy (`rawPath`), else from the base fork's resource of the same type and ID
   when its hash equals `storedSha256`. A `raw/` copy that no longer matches `storedSha256` is packed as it is, with a
   warning; a base resource that does not match is not used, with a warning. Changes to `otherFiles` are ignored,
   with a warning.
5. A resource whose main file is its data, a `raw` resource or one whose main file is `.bin` (the code decoders,
   §3.8), takes its main file as the data when it has neither (or when the file changed). That is the data after
   decompression, so a compressed resource loses its compressed attribute (`$01`), with a warning.
6. Any other decoded resource whose main file changed needs an encoder for its decoder; none of the built-in decoders
   has one, so it is an error. One with no stored data (no `raw/` copy, no base) is an error too.
7. A missing main file is an error, or, when deletes are allowed, the resource is left out.
8. Files in the folder the manifest does not list are ignored: resources are not added this way.

The fork itself is laid out anew by the fork writer ([resource-fork.md](../resources/resource-fork.md)), so its bytes
can differ from the original's where the original's layout was not canonical.

## 3. Writing

### 3.1 One fork

1. If the export folder already holds any file or folder and overwriting is off, nothing is written and the export
   fails ("… is not empty").
2. Otherwise the folder is created if needed. With overwriting on, files of the same names are replaced; other files
   already there are left in place and are not listed in the manifest.
3. The resources of the chosen types are exported in the fork's order (§3.7); then the document, if any (§3.14); then
   `manifest.json`.

The file's data fork is read only for a document converter that needs it (a SimpleText document's text); without it,
the data fork counts as empty.

### 3.2 Several forks

For the forks found under one unwrapped input (a container tree, [host-folders.md](../containers/host-folders.md),
[unwrapping.md](../containers/unwrapping.md)):

1. One fork in the list is exported straight into the output folder.
2. Several forks are each exported into a folder of their own, `<output>/<placement>/<file name>`:
   - `<placement>` is the folder the file would get from `unpack`: folders inside volumes become folders; a container
     that in the end holds one file (MacBinary, BinHex, AppleSingle, a disk image of one file) is replaced by that
     file; one that holds several files, or folders, becomes a folder named after the outermost container of its
     chain ([host-folders.md](../containers/host-folders.md));
   - `<file name>` is the Mac file's name made host-safe (§3.3, up to 255 characters), and made distinct from the
     other names in the same folder (§3.6). The file's own export then goes inside that folder.
3. Each fork folder is checked for emptiness on its own (§3.1). A fork whose folder cannot be written is reported and
   skipped; the others are still exported.
4. The length budget (§3.5) of a fork's export is the path limit minus the length of `<placement>/<file name>/`, but
   never less than 24.

The order of forks is the caller's. Folder names and the ` ~2` suffixes they may get depend on that order.

### 3.3 Host-safe names

Names must be valid and distinct on Windows, macOS and Linux, whose disks usually ignore case. The manifest, not the
name, is the authority for a resource's type, ID and name, so names only have to be safe and readable; they need not
be reversible. A Mac name (bytes) becomes a host name, with a length limit:

1. An empty name becomes `%00`.
2. Each byte becomes its Mac OS Roman character, except that it is written as `%XX` (the byte in uppercase hex) when
   it is a control character (`$00`–`$1F`, `$7F`), one of `% / \ : * ? " < > |`, or a space or dot that is the
   name's last byte.
3. If the name, up to its first `.` and with trailing spaces ignored, is a reserved Windows device name (`CON`, `PRN`,
   `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, in any case), the last character of that part is escaped: `COM1` →
   `COM%31`, `nul.txt` → `nu%6C.txt`.
4. If the result is longer than the limit, it is cut:
   - an extension is kept whole: the text from the last unescaped `.` when that dot is not the first character and
     starts at most 5 characters (counting an escape as one) from the end;
   - the rest is cut at the end, by whole characters and whole escapes (never inside a `%XX`), to fit the limit minus
     the extension's length;
   - trailing spaces and dots are then removed from the cut part, and the extension appended.

The result may therefore be shorter than the limit. Lengths are counted in UTF-16 code units (characters from Mac OS
Roman are one each).

### 3.4 Type folders

- A type's folder is its four bytes as a host name (§3.3): `PICT` → `PICT`, `snd ` → `snd%20`, `PAT ` → `PAT%20`,
  `STR#` → `STR#`, a type with a control byte `$01` → `%01…`.
- Case collisions: the distinct types of the resources being exported (after the type filter) are grouped by their
  plain folder names compared without regard to case (ordinal, ignore-case). Every type in a group of two or more
  gets `~` and its four bytes as eight uppercase hex digits appended: `PICT` and `pict` become `PICT~50494354` and
  `pict~70696374`. Types that collide with nothing keep the plain name.
- No type folder can be named `raw`: a type is always four bytes, and a folder name from four bytes has at least four
  characters. The name `manifest.json` cannot collide either.

### 3.5 File stems and the length budget

Each resource's files share a stem:

- the ID in decimal (a leading `-` for negative IDs), a space and the resource's name: `128 Title Screen`,
  `-16455 Messages`;
- only the ID when the resource has no name or an empty one: `128`;
- made host-safe (§3.3) with a limit, the budget:

  ```
  budget = max(8, path limit − length(type folder) − 1 − length(longest extension among the resource's files))
  ```

  The path limit (200 by default, §5.1) counts from the export folder, so a file's path relative to the manifest,
  `<type folder>/<stem><extension>`, stays within it. The ID (at most 6 characters) always fits in 8.
- A Mac name that itself looks like it ends in an extension (`128 Read Me.txt`) keeps that part when the stem is cut,
  by rule 4 of §3.3.

The limit is a target, not a guarantee: the minimum of 8, the ` ~N` suffix of §3.6 and the fork folders of §3.2 can
take a path past it.

### 3.6 Unique names

Within one type folder, each file name is made distinct from the names already written there, without regard to
case: if `<stem><extension>` is taken, ` ~2`, ` ~3`, … is inserted before the part from the last `.`:
`1 Good.txt` → `1 Good ~2.txt`; `128.1.png` → `128.1 ~2.png`.

Since each stem starts with the ID, two resources collide only when a fork holds the same type and ID twice (a damaged
fork), or when one decoder writes two files with the same extension. Fork folders (§3.2) are made unique the same way
within their parent folder.

### 3.7 The files written for a resource

1. The resource's data is read, decompressed if compressed ([compressed-resources.md](../resources/compressed-resources.md)).
   When decompression is impossible the stored data is used instead and the reason is reported.
2. The first decoder that handles the type (§3.8) is asked for files.
3. Its files are written to the type folder, the first being the main file, each named `<stem><extension>` (§3.5,
   §3.6). A decoder that writes several images gives them numbered extensions (`.1.png`, `.2.png`, …; §3.11).
4. With raw copies kept, the stored data (as in the fork: still compressed if compressed) is also written to
   `raw/<type folder>/<id>.bin`, and `rawPath` points to it. The name holds only the ID and is not made unique: a fork
   with a duplicate type and ID writes that file twice, the last one winning. The folder exists so that a fork can be
   rebuilt byte for byte (§2.2); its name has three characters, so it never clashes with a type folder (§3.4).
5. An entry is added to the manifest (§1.7).

A resource is written raw (one file, `<stem>.bin`, holding its data, decompressed, as applications see it, with
decoder name `raw` and version 1) when:

- no decoder handles its type (always so when no decoders are given, as with the CLI's `--raw`);
- the decoder returns no files (it reports why, or `export.not-decoded` is reported);
- the decoder throws a data error; `export.decoder-failed` is reported.

### 3.8 Decoders

A decoder has a name and a version, both recorded in the manifest, and says which types it handles. Given a resource
(its data, access to other resources of the same fork, decompressed, and a list for diagnostics) it returns its
files, the main one first, or none when it cannot decode the resource.

- The name is a dotted `<group>.<kind>` string, stable across releases.
- The version is raised whenever the decoder's output for the same input changes, so that a tool can tell outputs of
  different versions apart.
- Applications add decoders of their own for private types; the first decoder in the list that handles a type is
  used.

The built-in decoders, in this order; each type is handled by exactly one, and all are version 1:

| Name | Types | Main file | Other files | `encoding` | Specified in |
| --- | --- | --- | --- | --- | --- |
| `text.string` | `STR ` | `.txt` | — | `macintosh` | [strings.md](../resources/strings.md), [text-output.md](text-output.md) |
| `text.string-list` | `STR#` | `.json` | — | `macintosh` | [strings.md](../resources/strings.md), [text-output.md](text-output.md) |
| `text.text` | `TEXT` | `.txt` | `.rtf` when a `styl` of the same ID exists | `macintosh` | [styled-text.md](../resources/styled-text.md), [text-output.md](text-output.md) |
| `text.style` | `styl` | `.json` | — | null | [styled-text.md](../resources/styled-text.md), [text-output.md](text-output.md) |
| `text.version` | `vers` | `.json` | — | `macintosh` | [version.md](../resources/version.md), [text-output.md](text-output.md) |
| `image.picture` | `PICT` | `.png` | — | null | §3.9 |
| `image.icon` | `ICON`, `ICN#`, `ics#`, `icm#`, `icl4`, `icl8`, `ics4`, `ics8`, `icm4`, `icm8`, `cicn`; `SICN` | `.png`; `SICN`: `.1.png` | `SICN`: `.2.png`, … | null | §3.10 |
| `image.icon-family` | `icns` | the largest, deepest member (§3.10) | the other members, largest and deepest first | null | §3.10 |
| `image.cursor` | `CURS`, `crsr` | `.png` | `.json` (§1.13) | null | §3.12 |
| `image.pattern` | `PAT `, `ppat`; `PAT#`, `ppt#` | `.png`; lists: `.1.png` | lists: `.2.png`, … | null | §3.13 |
| `sound.snd` | `snd ` | `.wav`, or `.json` for a sound of commands only | `.json` after a `.wav` | null | [sound.md](../resources/sound.md) |
| `ui.menu`, `ui.menu-bar`, `ui.window`, `ui.dialog`, `ui.alert`, `ui.dialog-items`, `ui.control` | `MENU`, `MBAR`, `WIND`, `DLOG`, `ALRT`, `DITL`, `CNTL` | `.json` | — | `macintosh` | [windows-dialogs.md](../resources/windows-dialogs.md) |
| `ui.colors`, `ui.menu-colors`, `ui.item-colors`, `ui.dialog-extension`, `ui.alert-extension`, `ui.menu-extension` | `wctb`, `dctb`, `actb`, `cctb`; `mctb`; `ictb`; `dlgx`; `alrx`; `xmnu` | `.json` | — | null | [windows-dialogs.md](../resources/windows-dialogs.md) |
| `color.table`, `color.palette` | `clut`, `pltt` | `.json` | `.act` | null | [palettes.md](../resources/palettes.md) |
| `finder.bundle`, `finder.file-reference`, `finder.size` | `BNDL`, `FREF`, `SIZE` | `.json` | — | `macintosh` | [finder.md](../resources/finder.md) |
| `font.bitmap`, `font.family`, `font.outline`, `font.colors` | `NFNT`, `FONT`; `FOND`; `sfnt`; `fctb` | strikes `.png`; `.json`; `.ttf`; `.json` | strikes `.bdf`, `.json`; `sfnt` `.json` | `FOND`: `macintosh` | [bitmap-fonts.md](../resources/bitmap-fonts.md) |
| `code.segment` | `CODE` | `.bin`, the data | `.s`, `.json`; `'CODE'` 0: `.json` | null | [disassembly.md](disassembly.md) |
| `code.cfrg` | `cfrg` | `.bin`, the data | `.json` | null | [disassembly.md](disassembly.md) |
| `code.resource` | Native and 68k code resources ([disassembly.md §5.1](disassembly.md#51-the-decoded-types)) | `.bin`, the data | `.s`, `.json` | null | [disassembly.md](disassembly.md) |

The image extensions are those of the configured image encoder (`.png` by default, §1.12). The code decoders' main
file is the data itself, so `pack` takes it back without raw copies (§2.2). Every other type is written raw.

### 3.9 Pictures

The image decoders hand each resource to the renderer, which draws it as the Mac would into a width × height grid of
8-bit RGBA pixels, as [pict.md](../graphics/pict.md), [icons.md](../resources/icons.md) and
[quickdraw.md](../graphics/quickdraw.md) specify. The renderer reports damaged data only by throwing; the image
decoders turn such an exception into `image.undecodable` with the renderer's message, and the resource is written raw.

1. A `PICT` is drawn at its native resolution.
2. The screen depth (32 by default; 1, 2, 4, 8 or 16) is the depth of the screen the picture is drawn on. Below 32,
   QuickDraw's colour matching and dithering for that depth apply
   ([quickdraw.md §4.6](../graphics/quickdraw.md#46-screen-depths)); the file is still 8-bit RGBA. Only pictures use it;
   icons, cursors and patterns are drawn at full colour.
3. The QuickDraw model is Mac OS 9's by default, or the 68k ROM's
   ([quickdraw.md §4.1](../graphics/quickdraw.md#41-mac-os-9-bitmaps) and
   [pict.md §4.2](../graphics/pict.md#42-mac-os-9) list the differences).
4. Before drawing, the picture frame (`picFrame`, the `Rect` at bytes 2–9 of the resource) gives the canvas size. If
   width × height exceeds the pixel limit (64 Mi pixels, 67,108,864, by default), the picture is not drawn:
   `image.too-large`, and the resource is written raw. The check needs at least 10 bytes; shorter data goes to the
   renderer, which rejects it.
5. Parts of the canvas the picture does not draw on are transparent
   ([pict.md §2.2](../graphics/pict.md#22-the-drawing-space-and-the-canvas)).

### 3.10 Icons and their masks

| Types | Output |
| --- | --- |
| `ICON` | 32 × 32, black on white, opaque |
| `ICN#`, `ics#`, `icm#` | the first icon of the list, with the list's mask as transparency (mask bit 0 → alpha 0); a list without a mask half gets a computed mask ([icons.md](../resources/icons.md)) |
| `icl4`, `icl8`, `ics4`, `ics8`, `icm4`, `icm8` | the colour icon, masked by the icon list of the same ID and size in the same fork: `icl*` by `ICN#`, `ics*` by `ics#`, `icm*` by `icm#` |
| `cicn` | the colour icon with its own mask |
| `SICN` | one image per 16 × 16 icon, numbered (§3.11), unmasked |
| `icns` | one image per image member, named by it: `.it32.png`, `.ih32.png`, `.ich8.png`, `.ich4.png`, `.ich.png`, `.il32.png`, `.icl8.png`, `.icl4.png`, `.ICN.png`, `.is32.png`, `.ics8.png`, `.ics4.png`, `.ics.png`, `.icm8.png`, `.icm4.png`, `.icm.png`, in that order (the 1-bit members drop their `#`); each through the mask Icon Services picks for its size ([icon-families.md](../resources/icon-families.md)): an 8-bit mask of the same size as alpha, any other as a hard edge |

- The mask of a colour icon is taken as the Finder draws it: from the 1-bit list of the same ID
  ([icons.md](../resources/icons.md)). The list is read decompressed if needed; diagnostics from reading it are
  reported against the colour icon.
- When the fork has no such list, the colour icon is drawn fully opaque and `image.no-mask` (Info) is reported; the
  icon is still decoded. The Mac's Icon Utilities draw nothing in that case (noMaskFoundErr,
  [icons.md](../resources/icons.md)); ClassicMac prefers a visible image.

### 3.11 Numbered list outputs

A resource holding a list of images (`SICN`, `PAT#`, `ppt#`) is written as one file per item, in the list's order,
with extensions `.1.png`, `.2.png`, …: `SICN/128.1.png`, `SICN/128.2.png`. The first is the main file (`path`,
`size`, `sha256`); the rest are `otherFiles` in order. A list of no items gives no files, and the resource is written
raw.

### 3.12 Cursors

A cursor (`CURS`, `crsr`) gives two files:

- `<stem>.png`, 16 × 16: the pixels the cursor paints (mask bit 1), black and white for `CURS`, the colour pixels for
  `crsr`; every other pixel transparent, including those where the cursor inverts or XORs the screen;
- `<stem>.json` (§1.13).

On the Mac a cursor is drawn as `screen = (screen AND NOT mask) XOR image`: where the mask is 0, a `CURS` data bit 1
inverts the screen, and a `crsr` pixel XORs the screen with its complement ([cursors.md](../resources/cursors.md)).
The JSON records that.

### 3.13 Patterns

| Types | Output |
| --- | --- |
| `PAT ` | 8 × 8, black on white |
| `ppat` | the pixel pattern at its own size |
| `PAT#`, `ppt#` | one image per pattern, numbered (§3.11) |

### 3.14 Documents

With document converters, each converter in turn is asked whether the file is a document; the first that gives files
has them written into `document/` beside the type folders, and `document` (§1.10) records them. Documents are
converted only when every type is exported (no type filter). A converter that throws a data error is reported
(`export.converter-failed`) and the next converter is tried.

The built-in converter, `document.html` version 1, converts DOCMaker and SimpleText documents to HTML
([html.md](html.md)). Its diagnostics (`document.*`) join the manifest's `diagnostics`.

### 3.15 Producing an export

Another tool may write exports that ClassicMac's readers accept: any valid JSON meeting the schema, with
`formatVersion` `1.2` (or an earlier 1.x without the later fields), and files where the manifest says. The folder and
file names of §3.3–§3.6 are ClassicMac's choice and are not required: a reader finds files through the manifest's
paths.

## 4. Variants

### 4.1 Format versions

| Version | Adds | A reader of 1.x |
| --- | --- | --- |
| 1.0 | — | |
| 1.1 | `otherFiles` and `encoding` in resource entries | Accepts their absence and, as the schema allows, `null` |
| 1.2 | `document` at the top level | Accepts its absence and `null` |

### 4.2 What the viewer records differently

Exports from the viewer follow this specification, with two differences in `source.formats`: *Export Resources*
writes an empty list, and *Extract All Resources* only the format of the file's own node, not the chain above it.

## 5. ClassicMac

### 5.1 ExportOptions

Every tunable value of an export (`ClassicMac.Resources.Export.ExportOptions`):

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Types` | set of types, or null | null (all) | Export only resources of these types. The filter applies before type-folder collisions are worked out (§3.4) |
| `MaxPathLength` | integer | 200 | The path limit: the longest a written path should be, in characters, counted from the export folder (§3.2, §3.5) |
| `KeepRaw` | boolean | false | Also write each resource's stored data to `raw/` (§3.7) |
| `Overwrite` | boolean | false | Allow writing into an export folder that already holds files (§3.1) |
| `Decoders` | list of decoders | empty | The decoders tried, in order; empty exports everything raw. `ResourceDecoders.Create` gives the built-in ones |
| `Documents` | list of document converters | empty | The converters tried for the whole file (§3.14); empty converts none. `ResourceDecoders.CreateDocumentConverters` gives the built-in one |
| `ReadOptions` | `ReadOptions` | `ReadOptions.Default` | Size limits and the Resource Manager model (Mac OS 9 or 68k ROM) used to decompress resources ([resource-fork.md](../resources/resource-fork.md)) |

The fork's source is given separately, as an `ExportSource` (name, formats, type, creator, Finder flags), and becomes
the manifest's `source`.

### 5.2 DecodeOptions

The options of the built-in decoders (`ClassicMac.Resources.Decoders.DecodeOptions`):

| Name | Type | Default | Meaning |
| --- | --- | --- | --- |
| `TextEncoding` | enum | Mac OS Roman | The encoding text resources are read with; recorded as `encoding` |
| `LineEndings` | enum | LF | Line breaks in `.txt` output: LF, or as stored (CR) ([text-output.md §5](text-output.md#5-classicmac)) |
| `ImageEncoder` | `IImageEncoder` | `PngEncoder` | How images are written: a name, an extension and `Encode(width, height, rgba)`. PNG (§1.12) is the only one built in; the manifest records the extension through the file names but not the encoder's name |
| `ScreenDepth` | integer | 32 | The screen depth pictures are drawn at: 1, 2, 4, 8, 16 or 32 (§3.9) |
| `MaxImagePixels` | integer | 67,108,864 | The pixel limit: the largest picture drawn (§3.9) |
| `QuickDraw` | `ResourceManagerModel` | `MacOS9` | Whose QuickDraw pictures are drawn as: Mac OS 9's (`MacOS9`) or the 68k ROM's (`Rom68k`) (§3.9) |

Pictures are drawn by `PictReader.Decode`, icons, cursors and patterns by `QuickDrawResources`, with
`ClassicMac.Graphics`'s default options otherwise. The renderer exceptions turned into `image.undecodable` (§3.9) are
not-supported, end-of-stream, argument, overflow, invalid-data and index-out-of-range. The decoder errors the exporter
catches (§3.7) are `InvalidDataException`, `EndOfStreamException`, `ArgumentException`, `IndexOutOfRangeException`,
`FormatException`, `OverflowException` and `NotSupportedException`; any other exception is not caught by the
exporter, and `Unpacker.Extract` reports a file-system exception as a failed fork folder.

### 5.3 The extract command

```
classicmac extract <input> [-o <dir>] [--raw] [--keep-raw] [-t <type>]… [--overwrite] [--screen-depth <n>] [--no-documents]
```

| Option | Maps to |
| --- | --- |
| `-o`, `--output <dir>` | The output folder; default `<input name without extension> resources` next to the input. It is not numbered: an existing non-empty folder is refused unless `--overwrite` is given |
| `--raw` | `Decoders` and `Documents` empty: every resource written as its data, `.bin`, and no document |
| `--keep-raw` | `KeepRaw` |
| `-t`, `--type <type>` | `Types`; repeatable; four characters (`"snd "`) or `\xHH` escapes. A value that is not four characters is a usage error |
| `--overwrite` | `Overwrite` |
| `--screen-depth <n>` | `DecodeOptions.ScreenDepth`; one of 1, 2, 4, 8, 16, 32 (default 32) |
| `--no-documents` | `Documents` empty. By default the built-in converter runs (§3.14) |
| `--max-resource-size`, `--max-nesting-depth`, `--max-expanded-bytes`, `--verify`, `--strict`, `-q` | The options every command takes: limits (`ReadOptions`, `ContainerReadOptions`), exit-code strictness and quiet output |

`MaxPathLength` is not exposed (200). The other decode options use their defaults; the QuickDraw model follows
`ReadOptions.ResourceManager` (Mac OS 9, as the CLI does not change it).

The input is opened and unwrapped as by `list` and `unpack`. A plain file that no container reader recognises is read
as a raw resource fork (formats `["raw resource fork"]`); otherwise each file inside is read for a resource fork, or a
data fork that holds one. Only forks with at least one resource of the chosen types are kept. They are exported as
§3.2 says: straight into the output folder when there is one, a folder each when there are several. Files without
resources get no folder. A file that is a document also gets its `document/` folder (§3.14); the `convert` command
writes documents alone ([documents.md §5](../resources/documents.md#5-classicmac)).

Output: one line on stdout, `<n> resources from <m> files, to <folder>`. Diagnostics go to stderr, one per line:
`<source>: <severity>[ at <offset>]: <message> [<code>]`, where `<source>` is the input's name, followed by
` > <Mac path>` for a file inside it. With `-q` only errors are printed.

| Exit code | Meaning for `extract` |
| --- | --- |
| 0 | Done. Warnings, if any, were printed |
| 1 | Done, but an error diagnostic was reported while reading or exporting (or a warning, with `--strict`) |
| 2 | The command line is wrong (an unknown option, a bad `--type` or `--screen-depth`, an input that does not exist) |
| 3 | The input is neither a Mac container nor a resource fork |
| 4 | Reading the input failed at the file-system level, or a fork's export folder could not be written (including an existing non-empty folder without `--overwrite`). Takes precedence over 1 |

### 5.4 The pack command

`ResourcePacker.Pack` rebuilds a fork as §2.2 says; `PackOptions.Base` is the base fork and `PackOptions.AllowDeletes`
allows deletes.

```
classicmac pack <folder> -o <file> [--base <file>] [--data <file>] [--container raw|appledouble|applesingle|macbinary|binhex]
    [--allow-deletes] [--overwrite]
```

| Option | Meaning |
| --- | --- |
| `<folder>` | An export folder with its `manifest.json` (one fork's: with several forks, each file's folder) |
| `-o`, `--output <file>` | The file to write (required). An existing file is refused unless `--overwrite` is given |
| `--base <file>` | The file the export was made from (any input `extract` reads, holding one resource fork): `PackOptions.Base` |
| `--data <file>` | A data fork for AppleSingle, MacBinary and BinHex (default empty; an export holds no data fork) |
| `--container` | `raw` (default): the resource fork itself; `appledouble`: an AppleDouble header file; `applesingle`, `macbinary` (III), `binhex` (4.0): see [writing.md](../containers/writing.md). The container's name, type, creator and Finder flags come from the manifest's `source` |
| `--allow-deletes` | `PackOptions.AllowDeletes` |

Output: one line on stdout, `<n> resources (<bytes> bytes of resource fork), to <file>`. Diagnostics go to stderr as
for `extract`. When the pack has an error, nothing is written ("Nothing written: …").

| Exit code | Meaning for `pack` |
| --- | --- |
| 0 | Packed |
| 1 | An error diagnostic (nothing written), or a warning with `--strict` (written) |
| 2 | A wrong command line, or a base holding no resource fork or several |
| 3 | No readable manifest (or a newer major format) |
| 4 | The output exists without `--overwrite`, or cannot be written |

### 5.5 Folders chosen by the CLI and the viewer

- CLI `extract`: §5.3.
- The viewer puts every export into a new folder made by `ExportFolders.CreateNew(parent, name)`, which tries `name`,
  then `name 2`, `name 3`, … and creates the first path that is neither a folder nor a file. It never writes into an
  existing folder.
  - *Export Resources* (a file, one resource type of a file, or an input that is a raw fork): folder
    `<file's host name> resources`; one fork, exported directly (only the selected type when a type is selected).
  - *Extract All Resources* (an input, a container or a folder): folder `<item's host name> resources`; every file
    under the item that has at least one resource, as §3.2 says.
  - Both convert a file that is a document into `document/` (§3.14), as `extract` does; *Export Resources* of one
    type does not.
  - *Convert Documents* (an input, a container, a folder or a file): folder `<item's host name> documents`, by
    `DocumentConverter.Convert` ([documents.md §5](../resources/documents.md#5-classicmac)); when
    the item holds no document, the new folder is removed again.
  - *Save Resource As* writes one resource's decoded file (or its data as `.bin`) to a file the user chooses, with no
    manifest. Its suggested name is the stem of §3.5 cut to 200 characters. It offers one file per kind, by its last
    extension: a list resource's first image as `.png`, a sidecar `.json` only when it is the only output.

### 5.6 Names in the code

| Rule | Code |
| --- | --- |
| One fork (§3.1) | `ResourceExporter.Export(fork, directory, source, options, dataFork)` |
| Several forks (§3.2) | `Unpacker.Extract(root, forks, directory, options)`; placement by `OutputLayout` |
| Host-safe names (§3.3) | `HostNames.ToHostName(name, maxLength)` |
| Type folders (§3.4) | `HostNames.TypeFolder(type, collides)` |
| Unique names (§3.6) | `HostNames.MakeUnique` |
| Mac text (§1.1) | `FourCC.ToString`, `MacString.ToString` |
| Decoders (§3.8) | `IResourceDecoder`, `DecodeInput` (with `DecodeInput.Find` for other resources), `ResourceDecoders.Create(DecodeOptions)` |
| Manifest JSON (§1.3) | `ExportManifest`, serialized by `System.Text.Json` with its default encoder |
| PNG (§1.12) | `PngEncoder` |

## 6. Diagnostics

The exporter, the image decoders and the packer report these codes. Each exporter code goes to the resource's
`warnings` (message only) and to the manifest's `diagnostics`, and is printed by the CLI; the packer's are printed by
`pack`.

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `export.converter-failed` | Warning | A document converter threw a data error (`ResourceExporter`, `convert`) | Writes no document; tries the next converter | Not applicable |
| `export.decoder-failed` | Warning | The decoder threw a data error (§3.7); the message gives the decoder and the error | Writes the resource raw | Not applicable |
| `export.failed` | Error | The viewer could not write an export (a file-system error, or an export folder that failed) | Shows it in the viewer's diagnostics list only; never written to a manifest | Not applicable |
| `export.not-decoded` | Info | The decoder returned no files and nothing had been reported for the resource (neither by decompression nor by the decoder) | Writes the resource raw | Not applicable |
| `icon.family-header` | Warning | `image.icon-family`: the data is not an `icns` | Writes nothing but the raw resource | Treats the family as empty |
| `icon.family-ignored` | Info | `image.icon-family`: elements Mac OS 9 does not know (`TOC `, `info`, `icnV`, `name`, …) | Skips them | Skips them |
| `icon.family-length` | Warning | `image.icon-family`: the length word differs from the data's size | Writes nothing but the raw resource | Treats the family as empty |
| `icon.member-size` | Info | `image.icon-family`: a 1-, 4- or 8-bit member or an 8-bit mask is not its exact size | Drops the member | Drops the member |
| `image.no-mask` | Info | `image.icon`, `image.icon-family`: a colour icon has no 1-bit icon list of the same ID for its mask, or an icon family has no mask at all | Draws it opaque (§3.10); the resource is still decoded | Draws nothing (noMaskFoundErr) |
| `image.too-large` | Warning | `image.picture`: the picture's frame is over the pixel limit (§3.9) | Writes the resource raw | Not applicable |
| `image.undecodable` | Warning | An image decoder: the renderer rejected the data (too short, a bad structure, an unsupported variant); the message is the renderer's | Writes the resource raw | Not applicable |
| `pack.base-differs` | Warning | The base's resource of that type and ID is not the one exported | Does not use it | Not applicable |
| `pack.decompressed` | Warning | A compressed `raw` resource is written from its decompressed file | Clears its compressed attribute | Not applicable |
| `pack.deleted` | Info | A resource's main file is gone, with deletes allowed | Leaves the resource out | Not applicable |
| `pack.missing-file` | Error | A resource's main file is gone, without deletes allowed | Writes nothing | Not applicable |
| `pack.no-encoder` | Error | A decoded resource's main file (not a `.bin`) changed, and its decoder has no encoder | Writes nothing | Not applicable |
| `pack.no-stored-data` | Error | An unchanged decoded resource has no `raw/` copy and no matching resource in the base | Writes nothing | Not applicable |
| `pack.other-changed` | Warning | A decoder's other file changed | Ignores it; only the main file counts | Not applicable |
| `pack.raw-changed` | Warning | A `raw/` copy no longer matches `storedSha256` | Packs it as it is | Not applicable |

Other codes reach the manifest from the code the exporter calls:

- reading the fork and decompressing resources (`fork.*`, `resource.*`):
  [resource-fork.md](../resources/resource-fork.md), [compressed-resources.md](../resources/compressed-resources.md);
- the text decoders (`text.*`): [styled-text.md](../resources/styled-text.md);
- the sound decoder (`sound.*`): [sound.md](../resources/sound.md);
- the interface decoders (`ui.*`): [windows-dialogs.md](../resources/windows-dialogs.md);
- the palette decoders (`color.*`): [palettes.md](../resources/palettes.md);
- the Finder decoders (`finder.*`): [finder.md](../resources/finder.md);
- the font decoders (`font.*`): [bitmap-fonts.md](../resources/bitmap-fonts.md);
- the code decoders (`code.*`, `m68k.*`, `pef.*`, `cfrg.*`): [disassembly.md](disassembly.md) and the documents it
  links;
- the document converter (`document.*`): [documents.md](../resources/documents.md), [html.md](html.md).

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenTests.cs`, `The_export_manifest_matches_its_golden`: the manifest
  of the whole fixture fork, exported with the built-in decoders, against
  `tests/ClassicMac.Resources.Decoders.Tests/Golden/manifest.json` (the excerpt of §1.11).
- `tests/ClassicMac.Resources.Tests/ExportTests.cs`: type folders and the manifest; compressed resources written
  decompressed and kept raw on request; types differing only in case and colliding names kept apart; paths within the
  limit and the type filter; decoders' files and the raw fallback; a folder with files not written into unless
  overwriting.
- `tests/ClassicMac.Core.Tests/HostNamesTests.cs`: host-safe names, long names cut before the extension, colliding
  names numbered, type folder names.
- `tests/ClassicMac.Resources.Cli.Tests/ExtractTests.cs`: a raw fork file into the output folder; a folder per file
  with resources on a disk; the type filter; corpus images with matching manifests; files that are neither containers
  nor forks.
- `tests/ClassicMac.Resources.Cli.Tests/PackTests.cs`: an export with raw copies packs back byte for byte; the base
  fork gives the stored data without raw copies; raw files can change, decoded ones cannot, and deletes need
  allowing; decoded code packs back from its `.bin` files; containers carry the name and Finder info.
- `tests/ClassicMac.Resources.Cli.Tests/CorpusExportTests.cs`, `Corpus_exports_without_decoder_errors`: with
  `CLASSICMAC_CORPUS` set, every export of the corpus, with raw copies, packs back with each resource's stored bytes
  and attributes unchanged. Not committed.

## 8. Not covered

- Encoders for decoded files: a changed decoded file cannot be packed (§2.2).
- Image encoders other than PNG.

## 9. References

1. IETF, RFC 8259, *The JavaScript Object Notation (JSON) Data Interchange Format*.
2. ISO/IEC 15948, *Portable Network Graphics (PNG)*.
3. JSON Schema 2020-12, <https://json-schema.org/>; the schema `schemas/manifest-1.schema.json`.
4. Apple, *Inside Macintosh: More Macintosh Toolbox* (Resource Manager) and *Macintosh Toolbox Essentials* (Finder
   Interface).
