# Strings (STR, STR#)

Contents

1. [Strings (STR)](#1-strings-str)
2. [String lists (STR#)](#2-string-lists-str)
3. [Writing text resources](#3-writing-text-resources)
4. [Diagnostics](#4-diagnostics)

---

## 1. Strings (STR)

A `'STR '` resource (type code with a trailing space) is one Pascal string [Doc] (*Inside Macintosh: Text*, String
Resource).

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | Length *n* (0–255) |
| +$01 | *n* | bytes | The text |

- Bytes after the string are ignored [ClassicMac].
- If the length byte says more than the resource holds, the text is cut to the bytes present and `text.string-short`
  is reported; an empty resource gives an empty text and the same diagnostic [ClassicMac].
- Output: the text as a `.txt` file ([text-output.md §1.1](../output/text-output.md#11-text-files)) [ClassicMac].

Example (golden `'STR '` 128, "Greeting"):

```
0E 43 61 66 8E 20 C4 AA 0D 6C 69 6E 65 20 32        → STR_-128.txt:  Café ƒ™␊line 2
```

(␊ = LF, from the stored CR with the default LineEndings.)

---

## 2. String lists (STR#)

A `'STR#'` resource is a count followed by that many Pascal strings, packed with no padding [Doc]
(*Inside Macintosh: Text*, String List Resource).

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | u16 | Count of strings |
| +$02 | … | Pascal strings | The strings, one after another |

- The Toolbox numbers the strings from **1** (`GetIndString`), and returns an empty string for an index past the
  count [Doc]; for index 0 or a missing `'STR#'` too (the count compared unsigned) [Code: Mac OS 9.0 Help Manager's
  copy of the glue]. ClassicMac's JSON array is 0-based: array element *i* is string *i* + 1 [ClassicMac].
- *Inside Macintosh* declares the count as an integer; ClassicMac reads it unsigned, so a count of $8000 or more is
  read as a large count and the data's end stops the list [ClassicMac].
- **Cut lists.** When the data ends before the count is reached, the strings read so far are kept; a string whose
  length byte runs past the data is kept as far as it goes and is the last one; a missing length byte ends the list.
  `text.string-list-short` is reported [ClassicMac].
- A resource shorter than 2 bytes gives no output and no diagnostic [ClassicMac].
- Bytes after the last counted string are ignored [ClassicMac].
- Output: JSON ([text-output.md §1.3](../output/text-output.md#13-str-json)) [ClassicMac].

Example (golden `'STR#'` 128):

```
00 03  05 48 75 6D 61 6E  03 83 6C 66  00           → {"strings": ["Human", "Élf", ""]}
```

A cut list: `00 03  03 6F 6E 65  05 74 77` gives `["one", "tw"]` and `text.string-list-short`.

---

## 3. Writing text resources

ClassicMac's editor (`TextResources`, `VersionResource`) writes these resources back [ClassicMac, following the formats
above]:

- **Text** is Mac OS Roman ([styled-text.md §2](styled-text.md#2-mac-os-roman)); a line break is written as a carriage return. Text that Mac OS Roman cannot hold
  is refused, not approximated.
- **`STR `:** one Pascal string, at most 255 bytes; nothing after it.
- **`STR#`:** the count (at most 65535), then the strings as Pascal strings.

---

## 4. Diagnostics

All are warnings: the output is still written from what could be read, except where noted [ClassicMac]. On the Mac,
`GetIndString` returns an empty string for an index past a list's count (§2), and TextEdit applies any `'styl'`
without complaint ([styled-text.md §4.4](styled-text.md#44-how-runs-map-onto-the-text)); what the Toolbox does with the other damaged cases below has not been traced.

| Code | Severity | Emitted by | Meaning |
| --- | --- | --- | --- |
| `text.string-short` | Warning | `'STR '` | The length byte says more than the resource holds (or the resource is empty); the text is cut |
| `text.string-list-short` | Warning | `'STR#'` | The data ends before the counted strings; the strings read so far (the last one possibly cut) are kept |
