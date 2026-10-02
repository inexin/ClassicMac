# Text output (text, JSON, RTF)

The files ClassicMac writes for the text resources: `'STR '` and `'TEXT'` as UTF-8 text, `'STR#'`, `'styl'` and
`'vers'` as JSON, and a `'TEXT'` with a `'styl'` of the same ID also as RTF. They are ClassicMac's own outputs: no Mac
software reads or writes them, though the RTF uses the RTF specification's control words. How the resources are read
is in [strings.md](../resources/strings.md), [styled-text.md](../resources/styled-text.md) and
[version.md](../resources/version.md).

| | |
| --- | --- |
| Identified by | `.txt`, `.json` and `.rtf` files from the `text.*` decoders, as an export's manifest names them ([export-manifest.md §3.8](export-manifest.md#38-decoders)) |
| ClassicMac | Writes; `ClassicMac.Resources.Decoders.Text` (`StringDecoder`, `StringListDecoder`, `TextDecoder`, `StyleDecoder`, `VersionDecoder`, `Rtf`) |
| Verified against | Nothing yet (outputs of ClassicMac's own) |
| Sources | ClassicMac's design; the RTF specification for the meaning of each control word |

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

Every rule here is [ClassicMac] unless it carries another tag. [Author] marks the RTF specification's meaning of a
control word.

### 1.1 Text files

`'STR '` and `'TEXT'` are written as `.txt` files: UTF-8 with no byte-order mark, the decoded text only; no trailing
line break is added. Line breaks are written as §3.1 says.

### 1.2 JSON files

- UTF-8, no byte-order mark, one object per file, indented two spaces.
- Non-ASCII characters are written as themselves, not escaped; `"` and `\` are escaped, CR and LF as `\r` and `\n`,
  other control characters as `\u00XX`.
- Lines end with LF on every platform, and the file ends with one LF, so outputs and their hashes are the same
  everywhere.

### 1.3 STR# JSON

| Field | Type | Meaning |
| --- | --- | --- |
| `strings` | array of string | The strings in stored order (element 0 is `GetIndString` index 1), as read ([strings.md §1.2](../resources/strings.md#12-string-lists-str)), with line breaks as §3.1 says |

```json
{
  "strings": [
    "Human",
    "Élf",
    ""
  ]
}
```

### 1.4 styl JSON

`runs` is an array of one object per stored element, in stored order (not sorted, negative starts kept), with the
stored values unchanged:

| Field | Type | Meaning |
| --- | --- | --- |
| `start` | integer | `scrpStartChar` |
| `height` | integer | `scrpHeight` |
| `ascent` | integer | `scrpAscent` |
| `font` | integer | `scrpFont` |
| `fontName` | string | The font's name ([styled-text.md §1.5](../resources/styled-text.md#15-fonts-by-id)) |
| `face` | integer | `scrpFace`, 0–255 ([styled-text.md §1.4](../resources/styled-text.md#14-face-bits)) |
| `size` | integer | `scrpSize`, 0 kept as 0 |
| `color` | array of 3 integers | Red, green, blue, 0–65535 |

A `'styl'` is written as JSON whether or not a `'TEXT'` of the same ID exists. The golden `'styl'` 128 gives (first
run shown):

```json
{
  "runs": [
    {
      "start": 0,
      "height": 22,
      "ascent": 18,
      "font": 20,
      "fontName": "Times",
      "face": 1,
      "size": 18,
      "color": [
        0,
        0,
        0
      ]
    },
    …
  ]
}
```

### 1.5 vers JSON

| Field | Type | Meaning |
| --- | --- | --- |
| `display` | string | The display string ([version.md §5.1](../resources/version.md#51-the-display-string)) |
| `major` | integer | Major version (BCD decoded) |
| `minor` | integer | Minor version (0–15) |
| `bugFix` | integer | Bug-fix version (0–15) |
| `stage` | string | `development`, `alpha`, `beta`, `final` or `unknown ($XX)` |
| `nonRelease` | integer | Non-release number (BCD, or binary when not BCD; [version.md §2](../resources/version.md#2-reading)) |
| `region` | integer | Region code |
| `shortVersion` | string | Short version string |
| `longVersion` | string | Long version string, with line breaks as §3.1 says |

The golden `'vers'` 1:

```
01 20 80 00 00 00 03 31 2E 32 0B 31 2E 32 2C 20 A9 20 31 39 39 36
```

```json
{
  "display": "1.2",
  "major": 1,
  "minor": 2,
  "bugFix": 0,
  "stage": "final",
  "nonRelease": 0,
  "region": 0,
  "shortVersion": "1.2",
  "longVersion": "1.2, © 1996"
}
```

### 1.6 RTF files

A `'TEXT'` with a `'styl'` of the same ID is also written as RTF, built from the runs of
[styled-text.md §2.3](../resources/styled-text.md#23-how-runs-map-onto-the-text). The file is ASCII only (all other
characters are escaped, §3.3), with LF line breaks:

```
{\rtf1\ansi\ansicpg1252\deff0\uc1
{\fonttbl{\f0 <name>;}{\f1 <name>;}…}
{\colortbl;\red<r>\green<g>\blue<b>;…}
<run><run>…}
```

| Part | Content |
| --- | --- |
| Header | RTF version 1, ANSI character set, code page 1252, default font `\f0`, one fallback character after each `\u` escape [Author]. The code page never matters, since no byte above `$7E` is written raw |
| Font table | One entry per distinct font ID, in order of first use by the runs, named as in [styled-text.md §1.5](../resources/styled-text.md#15-fonts-by-id); entry *i* is `\fi`. No font family or character set is given, so readers pick a substitute for fonts they lack. Two IDs with the same name (1 and 3, both Geneva) get two entries |
| Colour table | An empty first entry (`;`, the reader's automatic colour [Author]), then one entry per distinct 8-bit colour, in order of first use; the colour of table position *j* (1-based) is `\cfj` |
| Runs | §3.2 |
| End | `}` and LF |

There is no paragraph formatting (`\pard`, alignment, tab stops), since `'styl'` stores none.

## 2. Reading

None. ClassicMac does not read these files back.

## 3. Writing

### 3.1 Line breaks

1. In `.txt` files, every CR becomes LF by default; an LF already in the text stays, so a stored CR LF becomes LF LF.
   With line breaks kept as stored, CRs are kept. There is no CR LF choice.
2. The same choice applies to the strings of the `'STR#'` JSON and to the long version string of the `'vers'` JSON
   (as the JSON escapes `\n` or `\r`), but not to the short version string.
3. RTF writes every line break as `\par` (§3.3), whatever the choice.

### 3.2 RTF runs

Each run is written as `\plain` (reset character formatting [Author]), its font, size and colour, its face control
words, one space, then its escaped text (§3.3):

| Mac style | RTF | Meaning in RTF [Author] |
| --- | --- | --- |
| Font ID | `\fN` | Font table entry *N* |
| Size *s* points | `\fs(2s)` | Size in half-points |
| Colour | `\cfN` | Colour table entry *N* |
| Bold (`$01`) | `\b` | Bold |
| Italic (`$02`) | `\i` | Italic |
| Underline (`$04`) | `\ul` | Continuous underline |
| Outline (`$08`) | `\outl` | Outline |
| Shadow (`$10`) | `\shad` | Shadow |
| Condense (`$20`) | `\expnd-2\expndtw-10` | Character spacing −0.5 pt (quarter-points, then twips) |
| Extend (`$40`) | `\expnd2\expndtw10` | Character spacing +0.5 pt |

Face words come in the order of the table. Every run repeats its font, size and colour even when they equal the
previous run's.

### 3.3 RTF text escaping

| Character | Written as |
| --- | --- |
| `\`, `{`, `}` | `\\`, `\{`, `\}` [Author] |
| CR or LF | `\par` and an LF (the LF is only for readability) [Author] |
| Tab | `\tab ` [Author] |
| Other control characters (below U+0020) | Dropped |
| U+0020–U+007E | Themselves |
| U+007F and above | `\uN?`, with *N* the code point as a signed 16-bit number, and `?` the fallback [Author] |

For example `é` (U+00E9) is `\u233?`, `“` (U+201C) `舠?`, and the Apple logo U+F8FF `\u-1793?` [Author].

### 3.4 RTF example

The golden `'TEXT'` 128 with `'styl'` 128 ([styled-text.md §1.2](../resources/styled-text.md#12-plain-text-text),
[styled-text.md §2.3](../resources/styled-text.md#23-how-runs-map-onto-the-text)) gives:

```
{\rtf1\ansi\ansicpg1252\deff0\uc1
{\fonttbl{\f0 Times;}{\f1 Monaco;}{\f2 Geneva;}}
{\colortbl;\red0\green0\blue0;\red255\green0\blue0;\red0\green128\blue0;}
\plain\f0\fs36\cf1\b Title\par
\plain\f1\fs20\cf2\i Body \plain\f2\fs24\cf3\ul text, 舠?quoted舡?.}
```

## 4. Variants

None.

## 5. ClassicMac

- `DecodeOptions.LineEndings` sets the line-break choice of §3.1: `Lf` (the default) or `AsStored`. The CLI and the
  app use the default.
- The decoders' names and versions, recorded in the manifest: `text.string`, `text.string-list`, `text.text`,
  `text.style`, `text.version`, each version 1.
- Differences from the Mac, accepted:
  - QuickDraw's condense and extend change each character's width by one pixel, 1 point at 72 dpi
    ([styled-text.md §1.4](../resources/styled-text.md#14-face-bits)), not 0.5 pt;
  - QuickDraw's underline breaks around descenders; RTF's `\ul` is continuous.

## 6. Diagnostics

None of their own. The text decoders' codes (`text.string-short`, `text.string-list-short`, `text.styl-short`,
`text.vers-short`) are reading problems, listed in [strings.md](../resources/strings.md),
[styled-text.md](../resources/styled-text.md) and [version.md](../resources/version.md).

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/GoldenTests.cs`: every fixture decoded as `extract` decodes it, compared
  with `tests/ClassicMac.Resources.Decoders.Tests/Golden/` (the `.txt`, `.json` and `.rtf` outputs kept as files,
  among them `TEXT-128.rtf`, the example of §3.4; `CLASSICMAC_UPDATE_GOLDEN=1` rewrites them).

## 8. Not covered

- Encodings other than Mac OS Roman for the text.
- Paragraph formatting in RTF: `'styl'` has none.

## 9. References

1. Microsoft, *Rich Text Format (RTF) Specification*, version 1.9.1.
