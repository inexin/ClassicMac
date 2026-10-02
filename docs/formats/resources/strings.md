# Strings (STR, STR#)

A `'STR '` resource (the type code ends in a space) holds one Pascal string; a `'STR#'` resource holds a list of them,
which the Toolbox reads by index with `GetIndString`. Applications keep their messages, labels and other text in
them. ClassicMac decodes `'STR '` to text and `'STR#'` to JSON ([text-output.md](../output/text-output.md)), and edits
and writes both back.

| | |
| --- | --- |
| Identified by | Resource types `'STR '` and `'STR#'` |
| ClassicMac | Reads and writes: `ClassicMac.Resources.Decoders.Text` (`StringDecoder`, `StringListDecoder`, `TextResources`) |
| Verified against | Nothing yet |
| Sources | *Inside Macintosh: Text*; the Mac OS 9.0 Help Manager's copy of the `GetIndString` glue (disassembly) |

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

The shared conventions of [README.md](../README.md#conventions) hold. A Pascal string holds 0–255 bytes; nothing pads
it, so the next field starts right after its last byte. The text is Mac OS Roman
([styled-text.md §1.1](styled-text.md#11-mac-os-roman)).

### 1.1 Strings (STR)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Length | *n*, 0–255 |
| +$01 | *n* | Text | |

[Doc: Inside Macintosh: Text, String Resource]

### 1.2 String lists (STR#)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Count | Declared as an integer |
| +$02 | … | Strings | That many Pascal strings, one after another, packed with no padding |

[Doc: Inside Macintosh: Text, String List Resource]

## 2. Reading

### 2.1 Strings (STR)

1. Read the length byte, then that many bytes.
2. Bytes after the string are not part of it.

### 2.2 String lists (STR#)

1. Read the count, then the strings in order.
2. The Toolbox numbers the strings from 1 (`GetIndString`) and returns an empty string for an index past the count
   [Doc: Inside Macintosh: Text]; also for index 0 or a missing `'STR#'`, the count compared unsigned [Code: Mac OS
   9.0 Help Manager's copy of the glue].
3. Bytes after the last counted string are not part of the list.

## 3. Writing

What a writer must produce [Doc: Inside Macintosh: Text]:

- `'STR '`: one Pascal string, at most 255 bytes, nothing after it.
- `'STR#'`: the count, at most 65535, then the strings as Pascal strings.
- Text in Mac OS Roman, a line break as CR ([styled-text.md §3](styled-text.md#3-writing)).

## 4. Variants

None.

## 5. ClassicMac

- Output: a `'STR '` as a `.txt` file ([text-output.md §1.1](../output/text-output.md#11-text-files)), a `'STR#'` as
  JSON ([text-output.md §1.3](../output/text-output.md#13-str-json)), whose array is 0-based: element *i* is string
  *i* + 1. [ClassicMac]
- `'STR '`: bytes after the string are ignored. When the length byte says more than the resource holds, the text is
  cut to the bytes present and `text.string-short` is reported; an empty resource gives an empty text and the same
  diagnostic. [ClassicMac]
- `'STR#'`: the count is read unsigned, so a count of $8000 or more is a large count and the data's end stops the list.
  [ClassicMac]
- A cut `'STR#'`: when the data ends before the count is reached, the strings read so far are kept; a string whose
  length byte runs past the data is kept as far as it goes and is the last one; a missing length byte ends the list.
  `text.string-list-short` is reported. `TextResources.ReadStringList` drops the cut string. [ClassicMac]
- A `'STR#'` under 2 bytes gives no output and no diagnostic. Bytes after the last counted string are ignored, with no
  diagnostic. [ClassicMac]
- The editor (`TextResources.WriteString`, `WriteStringList`) writes as §3 and refuses text Mac OS Roman cannot hold, a
  string over 255 bytes and a list over 65535 strings, with `ArgumentException`; a line break (`\n` or `\r\n`) is
  written as CR, and read back as `\n`. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `text.string-list-short` | Warning | A `'STR#'` ends before its counted strings | Keeps the strings read so far, the last one possibly cut | `GetIndString` past the data: not traced |
| `text.string-short` | Warning | A `'STR '` length byte says more than the resource holds, or the resource is empty | Cuts the text to the bytes present | Not traced |

## 7. Verification

- Golden fixtures (`tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs`, outputs in `Golden/`,
  `GoldenTests`):
  - `'STR '` 128 ("Greeting"): `0E 43 61 66 8E 20 C4 AA 0D 6C 69 6E 65 20 32` gives `STR_-128.txt`,
    `Café ƒ™␊line 2` (the CR as LF with the default LineEndings).
  - `'STR#'` 128: `00 03  05 48 75 6D 61 6E  03 83 6C 66  00` gives `{"strings": ["Human", "Élf", ""]}`.
- `TextDecoderTests`: strings to UTF-8 text; a cut `'STR '` (`text.string-short`); lists to JSON; a cut list,
  `00 03  03 6F 6E 65  05 74 77`, gives `["one", "tw"]` and `text.string-list-short`.
- `TextResourcesTests.Strings_and_lists_round_trip`: CR written for `\n`; Pascal strings and lists written and read
  back; over 255 bytes and non-Roman text refused.

## 8. Not covered

- What the Toolbox does with a cut `'STR '` or `'STR#'` has not been traced.

## 9. References

1. Apple, *Inside Macintosh: Text* (1993): String Resource, String List Resource, `GetIndString`.
2. Code traced: the Mac OS 9.0 Help Manager (`'PACK'` 14), its copy of the `GetIndString` glue.
