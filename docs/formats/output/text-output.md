# Text output (text, JSON, RTF)

Contents

1. [Text output](#1-text-output)
2. [JSON output](#2-json-output)
3. [RTF output](#3-rtf-output)
4. [Decoders](#4-decoders)

---

## 1. Text output

`'STR '` and `'TEXT'` are written as `.txt` files [ClassicMac]:

- **UTF-8, no byte-order mark**, the decoded text only: no trailing line break is added [ClassicMac].
- **LineEndings** (a decode option) sets how line breaks are written [ClassicMac]:
  - `Lf` (the default): every CR becomes LF. An LF already in the text stays, so a stored CR LF becomes LF LF.
  - `AsStored`: CRs are kept.
  There is no CR LF choice. The CLI and the app use the default.
- LineEndings also applies to the strings of the `'STR#'` JSON and to the long version string of the `'vers'` JSON
  (as JSON escapes, `\n` or `\r`), but not to the short version string, and never to RTF, which writes every line
  break as `\par` (§3) [ClassicMac].

---

## 2. JSON output

### 2.1 Common form

- UTF-8, no byte-order mark, one object per file, indented two spaces [ClassicMac].
- Non-ASCII characters are written as themselves, not escaped; `"` and `\` are escaped, CR and LF as `\r` and `\n`,
  other control characters as `\u00XX` [ClassicMac].
- Lines end with LF on every platform, and the file ends with one LF, so outputs and their hashes are the same
  everywhere [ClassicMac].

### 2.2 STR#

```json
{
  "strings": [
    "Human",
    "Élf",
    ""
  ]
}
```

| Field | Type | Meaning |
| --- | --- | --- |
| `strings` | array of string | The strings in stored order (element 0 is `GetIndString` index 1), as read ([strings.md §2](../resources/strings.md#2-string-lists-str)) |

[ClassicMac]

### 2.3 styl

One object per stored element, in **stored order** (not sorted, negative starts kept), with the stored values
unchanged [ClassicMac]:

| Field | Type | Meaning |
| --- | --- | --- |
| `start` | integer | `scrpStartChar` |
| `height` | integer | `scrpHeight` |
| `ascent` | integer | `scrpAscent` |
| `font` | integer | `scrpFont` |
| `fontName` | string | The font's name ([styled-text.md §4.3](../resources/styled-text.md#43-fonts-by-id)) |
| `face` | integer | `scrpFace`, 0–255 ([styled-text.md §4.2](../resources/styled-text.md#42-face-bits)) |
| `size` | integer | `scrpSize`, 0 kept as 0 |
| `color` | array of 3 integers | Red, green, blue, 0–65535 |

The array is `runs`. The golden `'styl'` 128 gives (first run shown):

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

A `'styl'` is written as JSON whether or not a `'TEXT'` of the same ID exists [ClassicMac].

### 2.4 vers

| Field | Type | Meaning |
| --- | --- | --- |
| `display` | string | The display string ([version.md §3](../resources/version.md#3-the-display-string)) |
| `major` | integer | Major version (BCD decoded) |
| `minor` | integer | Minor version (0–15) |
| `bugFix` | integer | Bug-fix version (0–15) |
| `stage` | string | `development`, `alpha`, `beta`, `final` or `unknown ($XX)` |
| `nonRelease` | integer | Non-release number (BCD, or binary when not BCD; [version.md §2](../resources/version.md#2-reading)) |
| `region` | integer | Region code |
| `shortVersion` | string | Short version string |
| `longVersion` | string | Long version string, with LineEndings applied |

[ClassicMac]. The golden `'vers'` 1:

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

---

## 3. RTF output

A `'TEXT'` with a `'styl'` of the same ID is also written as RTF, built from the runs of [styled-text.md §4.4](../resources/styled-text.md#44-how-runs-map-onto-the-text) [ClassicMac]. The
meaning of each control word is the RTF specification's [Author]; the choice of control words for each Mac style is
ClassicMac's [ClassicMac].

### 3.1 Structure

The file is ASCII only (all other characters are escaped), with LF line breaks [ClassicMac]:

```
{\rtf1\ansi\ansicpg1252\deff0\uc1
{\fonttbl{\f0 <name>;}{\f1 <name>;}…}
{\colortbl;\red<r>\green<g>\blue<b>;…}
<run><run>…}
```

- **Header**: RTF version 1, ANSI character set, code page 1252, default font `\f0`, one fallback character after each
  `\u` escape [Author]. The code page never matters, since no byte above $7E is written raw [ClassicMac].
- **Font table**: one entry per distinct font ID, in order of first use by the runs, named as in [styled-text.md §4.3](../resources/styled-text.md#43-fonts-by-id); entry *i* is
  `\fi` [ClassicMac]. No font family or character set is given, so readers pick a substitute for fonts they lack
  [ClassicMac]. Two IDs with the same name (1 and 3, both Geneva) get two entries [ClassicMac].
- **Colour table**: an empty first entry (`;`, the reader's automatic colour [Author]), then one entry per distinct
  8-bit colour, in order of first use; the colour of table position *j* (1-based) is `\cfj` [ClassicMac].
- The file ends with `}` and LF. There is no paragraph formatting (`\pard`, alignment, tab stops), since `'styl'`
  stores none [ClassicMac].

### 3.2 Runs

Each run is written as `\plain` (reset character formatting [Author]), its font, size and colour, its face control
words, one space, then its escaped text [ClassicMac]:

| Mac style | RTF | Meaning in RTF [Author] |
| --- | --- | --- |
| Font ID | `\fN` | Font table entry *N* |
| Size *s* points | `\fs(2s)` | Size in half-points |
| Colour | `\cfN` | Colour table entry *N* |
| Bold ($01) | `\b` | Bold |
| Italic ($02) | `\i` | Italic |
| Underline ($04) | `\ul` | Continuous underline |
| Outline ($08) | `\outl` | Outline |
| Shadow ($10) | `\shad` | Shadow |
| Condense ($20) | `\expnd-2\expndtw-10` | Character spacing −0.5 pt (quarter-points, then twips) |
| Extend ($40) | `\expnd2\expndtw10` | Character spacing +0.5 pt |

Face words come in the order of the table [ClassicMac]. Every run repeats its font, size and colour even when they
equal the previous run's [ClassicMac].

Differences from the Mac, accepted for now [ClassicMac]:

- QuickDraw's condense and extend change each character's width by one pixel, 1 point at 72 dpi ([styled-text.md §4.2](../resources/styled-text.md#42-face-bits)), not 0.5 pt.
- QuickDraw's underline breaks around descenders; RTF's `\ul` is continuous.

### 3.3 Text escaping

| Character | Written as |
| --- | --- |
| `\`, `{`, `}` | `\\`, `\{`, `\}` [Author] |
| CR or LF | `\par` and an LF (the LF is only for readability) [Author] |
| Tab | `\tab ` [Author] |
| Other control characters (below U+0020) | Dropped [ClassicMac] |
| U+0020–U+007E | Themselves |
| U+007F and above | `\uN?`, with *N* the code point as a **signed** 16-bit number, and `?` the fallback [Author] |

For example `é` (U+00E9) is `\u233?`, `“` (U+201C) `舠?`, and the Apple logo U+F8FF `\u-1793?` [Author].

### 3.4 Example

The golden `'TEXT'` 128 with `'styl'` 128 ([styled-text.md §3](../resources/styled-text.md#3-plain-text-text), [styled-text.md §4.4](../resources/styled-text.md#44-how-runs-map-onto-the-text)) gives:

```
{\rtf1\ansi\ansicpg1252\deff0\uc1
{\fonttbl{\f0 Times;}{\f1 Monaco;}{\f2 Geneva;}}
{\colortbl;\red0\green0\blue0;\red255\green0\blue0;\red0\green128\blue0;}
\plain\f0\fs36\cf1\b Title\par
\plain\f1\fs20\cf2\i Body \plain\f2\fs24\cf3\ul text, 舠?quoted舡?.}
```

---

## 4. Decoders

The decoders' names and versions, recorded in the manifest: `text.string`, `text.string-list`, `text.text`,
`text.style`, `text.version`, each version 1 [ClassicMac].
