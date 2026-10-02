# Versions (vers)

Contents

1. [Layout](#1-layout)
2. [Reading](#2-reading)
3. [The display string](#3-the-display-string)
4. [Writing text resources](#4-writing-text-resources)
5. [Diagnostics](#5-diagnostics)

---

## 1. Layout

A `'vers'` resource describes the version of a file (ID 1) or of the product it belongs to (ID 2) [Doc]
(*Inside Macintosh: Macintosh Toolbox Essentials*, Finder Interface). The Finder shows only the strings
[Code: Finder 9.0]:

- `'vers'` 1's **long** string is Get Info's "Version:" field. With no `'vers'` 1 the Finder uses the string of the
  file's owner resource (the creator code, ID 0), and when that is empty, "n/a".
- `'vers'` 1's **short** string is the list view's Version column (and AppleScript's `version` property).
- `'vers'` 2's **long** string is shown at the top of the Get Info window.
- The numbers are never shown; the Finder only compares the first word of `'vers'` 1 as a number in places.

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 1 | u8 | Major version, binary-coded decimal |
| +$01 | 1 | u8 | Minor version (high nibble) and bug-fix version (low nibble) |
| +$02 | 1 | u8 | Release stage (below) |
| +$03 | 1 | u8 | Non-release revision number, for a stage before final |
| +$04 | 2 | i16 | Region code (country code in older documentation): 0 = United States |
| +$06 | 1 + *n* | Pascal string | Short version string, e.g. `1.2` |
| … | 1 + *m* | Pascal string | Long version string: version and copyright, e.g. `1.2, © 1996` |

The first four bytes are the `NumVersion` record [Doc]. Stages [Doc]:

| Value | Stage | Letter |
| --- | --- | --- |
| $20 | Development | d |
| $40 | Alpha | a |
| $60 | Beta | b |
| $80 | Final (release) | f |

---

## 2. Reading

- The major version is read as BCD: `(high nibble × 10) + low nibble` [Doc]. Nibbles above 9 are not rejected; they
  simply add (`$1A` → 20) [ClassicMac].
- Minor and bug-fix are the two nibbles of byte 1 [Doc].
- The non-release byte has no Apple reading: no Mac OS 9.0 code interprets it, since the Finder copies only the
  strings [Code: Finder 9.0]. *Inside Macintosh* does not say whether it is BCD, and Apple's Rez template marks the
  two bytes before it "in BCD" but this one only as a hex byte [Doc] (`SysTypes.r`), which suggests binary. Apple's
  own files store BCD, though (Disk Copy 6.5b13: $13 for "6.5b13"), and some other developers' binary ($0F for
  15). ClassicMac reads it as BCD when both nibbles are 0–9 (`$12` → 12), and as binary otherwise (`$0F` → 15)
  [Fitted: Apple's and others' files].
- The region code is a signed integer, kept as a number [Doc].
- Strings are Mac OS Roman ([styled-text.md §2](styled-text.md#2-mac-os-roman)). Long version strings may hold a CR: Disk Copy writes the image checksum on a second
  line (`…image␍CRC: $…`) [Fitted].
- Fewer than 7 bytes (the 6-byte header and a length byte): no output, `text.vers-short` [ClassicMac].
- A short string whose length runs past the data is cut; the long string is then empty; `text.vers-short` [ClassicMac].
  A long string that runs past the data is cut, with the same diagnostic. No long string at all (the data ends after
  the short one) gives an empty long string, silently [ClassicMac].
- Bytes after the long string are ignored [ClassicMac].

---

## 3. The display string

ClassicMac shows the numeric version as one string [ClassicMac]:

```
major "." minor [ "." bugFix  if bugFix > 0 ] [ letter nonRelease  if stage ≠ $80 or nonRelease > 0 ]
```

An unknown stage byte uses the letter `?` and the stage name `unknown ($XX)` (two upper-case hex digits)
[ClassicMac]. The Finder shows the strings, not this number (§1).

| Bytes 0–3 | Display | Stage |
| --- | --- | --- |
| `04 84 80 00` | 4.8.4 | final |
| `01 00 60 03` | 1.0b3 | beta |
| `06 50 60 13` | 6.5b13 | beta |
| `03 00 60 0F` | 3.0b15 | beta |
| `10 25 20 12` | 10.2.5d12 | development |
| `02 10 40 01` | 2.1a1 | alpha |
| `01 20 80 00` | 1.2 | final |
| `01 00 80 02` | 1.0f2 | final |

Output: JSON ([text-output.md §1.5](../output/text-output.md#15-vers-json)) [ClassicMac].

---

## 4. Writing text resources

ClassicMac's editor (`TextResources`, `VersionResource`) writes these resources back [ClassicMac, following the formats
above]:

- **`vers`:** the fixed part with the major version in BCD (0–99), minor and bug fix as nibbles (0–15), the stage byte,
  the non-release number in BCD (0–99, as Apple's own files store it) and the region code, then the short and long
  version strings as Pascal strings.

---

## 5. Diagnostics

All are warnings: the output is still written from what could be read, except where noted [ClassicMac]. On the Mac,
`GetIndString` returns an empty string for an index past a list's count ([strings.md §2](strings.md#2-string-lists-str)), and TextEdit applies any `'styl'`
without complaint ([styled-text.md §4.4](styled-text.md#44-how-runs-map-onto-the-text)); what the Toolbox does with the other damaged cases below has not been traced.

| Code | Severity | Emitted by | Meaning |
| --- | --- | --- | --- |
| `text.vers-short` | Warning | `'vers'` | Under 7 bytes: **no output**. Or a version string runs past the data: it is cut |
