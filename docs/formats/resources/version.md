# Versions (vers)

A `'vers'` resource gives the version of a file (ID 1) or of the product it belongs to (ID 2): a `NumVersion` record,
a region code, and short and long version strings. The Finder shows the strings in Get Info and the list view.
ClassicMac decodes `'vers'` to JSON with a display string ([text-output.md §2.4](../output/text-output.md#24-vers)),
and edits and writes it back.

| | |
| --- | --- |
| Identified by | Resource type `'vers'`, IDs 1 and 2 |
| ClassicMac | Reads and writes: `ClassicMac.Resources.Decoders.Text` (`VersionDecoder`, `VersionResource`) |
| Verified against | Nothing yet; the non-release byte is fitted to Apple's and other developers' files |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials*, Finder Interface; Apple's Rez template (`SysTypes.r`, MPW); Finder 9.0 (disassembly) |

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

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 | Major version | Binary-coded decimal |
| +$01 | 1 | Minor and bug-fix versions | Minor in the high nibble, bug fix in the low nibble |
| +$02 | 1 | Release stage | Below |
| +$03 | 1 | Non-release revision | For a stage before final (§2) |
| +$04 | 2 | Region code | `i16`; country code in older documentation; 0 = United States |
| +$06 | 1 + *n* | Short version string | Pascal string, e.g. `1.2` |
| … | 1 + *m* | Long version string | Pascal string: version and copyright, e.g. `1.2, © 1996` |

The first four bytes are the `NumVersion` record [Doc: Inside Macintosh: Macintosh Toolbox Essentials, Finder
Interface]. The release stages [Doc]:

| Value | Stage | Letter |
| --- | --- | --- |
| $20 | Development | d |
| $40 | Alpha | a |
| $60 | Beta | b |
| $80 | Final (release) | f |

Strings are Mac OS Roman ([styled-text.md §1.1](styled-text.md#11-mac-os-roman)).

## 2. Reading

1. The major version is BCD: high nibble × 10 + low nibble [Doc].
2. Minor and bug fix are the two nibbles of byte +$01 [Doc].
3. The non-release byte has no Apple reading: no Mac OS 9.0 code interprets it, since the Finder copies only the
   strings [Code: Finder 9.0]. *Inside Macintosh* does not say whether it is BCD, and Apple's Rez template marks the
   two bytes before it "in BCD" but this one only as a hex byte [Doc: SysTypes.r], which suggests binary. Apple's own
   files store BCD (Disk Copy 6.5b13: $13 for "6.5b13"), some other developers' binary ($0F for 15). Read it as BCD
   when both nibbles are 0–9 (`$12` → 12), as binary otherwise (`$0F` → 15) [Fitted: Apple's and others' files].
4. The region code is a signed integer [Doc].
5. Read the short version string, then the long one. A long version string may hold a CR: Disk Copy writes the image
   checksum on a second line (`…image␍CRC: $…`) [Fitted].

The Finder shows only the strings [Code: Finder 9.0]:

- `'vers'` 1's long string is Get Info's "Version:" field. With no `'vers'` 1 the Finder uses the string of the file's
  owner resource (the creator code, ID 0), and when that is empty, "n/a".
- `'vers'` 1's short string is the list view's Version column, and AppleScript's `version` property.
- `'vers'` 2's long string is shown at the top of the Get Info window.
- The numbers are never shown; the Finder only compares the first word of `'vers'` 1 as a number in places.

## 3. Writing

What a writer must produce: the fixed part with the major version in BCD (0–99), minor and bug fix as nibbles (0–15),
the stage byte, the non-release number in BCD (0–99, as Apple's own files store it) and the region code, then the
short and long version strings as Pascal strings [Doc] [Fitted: the non-release byte].

## 4. Variants

None.

## 5. ClassicMac

### 5.1 The display string

ClassicMac shows the numeric version as one string [ClassicMac]:

```
major "." minor [ "." bugFix  if bugFix > 0 ] [ letter nonRelease  if stage ≠ $80 or nonRelease > 0 ]
```

An unknown stage byte uses the letter `?` and the stage name `unknown ($XX)` (two upper-case hex digits). The Finder
shows the strings, not this number (§2). [ClassicMac]

| Bytes +$00–+$03 | Display | Stage |
| --- | --- | --- |
| `04 84 80 00` | 4.8.4 | final |
| `01 00 60 03` | 1.0b3 | beta |
| `06 50 60 13` | 6.5b13 | beta |
| `03 00 60 0F` | 3.0b15 | beta |
| `10 25 20 12` | 10.2.5d12 | development |
| `02 10 40 01` | 2.1a1 | alpha |
| `01 20 80 00` | 1.2 | final |
| `01 00 80 02` | 1.0f2 | final |

### 5.2 Reading and writing

- Output: JSON ([text-output.md §2.4](../output/text-output.md#24-vers)). [ClassicMac]
- BCD nibbles above 9 are not rejected; they simply add (`$1A` → 20). The region code is kept as a number.
  [ClassicMac]
- Under 7 bytes (the 6-byte header and a length byte): no output, `text.vers-short`; `VersionResource.Read` returns
  null. [ClassicMac]
- A short string whose length runs past the data is cut, the long string is then empty, and `text.vers-short` is
  reported. A long string that runs past the data is cut, with the same diagnostic. No long string at all (the data
  ends after the short one) gives an empty long string, silently. Bytes after the long string are ignored. An unknown
  stage gives no diagnostic. [ClassicMac]
- The editor (`VersionResource.Write`) writes as §3 and refuses a major or non-release number outside 0–99 or a minor
  or bug fix outside 0–15 with `ArgumentException`, as it does a string Mac OS Roman cannot hold or over 255 bytes. A
  line break in the long string is written as CR and read back as `\n`. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `text.vers-short` | Warning | Under 7 bytes; or a version string runs past the data | Under 7 bytes: no output. A cut string: cuts it and writes the JSON | Not traced |

## 7. Verification

- Golden fixture `'vers'` 1 (`tests/ClassicMac.Resources.Decoders.Tests/GoldenFixtures.cs`, `Golden/vers-1.json`):
  `01 20 80 00 00 00`, "1.2", "1.2, © 1996" gives display `1.2`, final.
- `TextDecoderTests.Versions_display_in_their_usual_form`: 4.8.4, 1.0b3, 10.2.5d12 (BCD), 3.0b15 (binary non-release
  byte) and 2.1a1, and the long string read. `Too_short_versions_are_left_raw`: 3 bytes give no output and
  `text.vers-short`.
- `TextResourcesTests.Versions_round_trip_in_BCD`: a `'vers'` read and written back byte for byte; a minor of 16
  refused.
- The other rows of §5.1 (6.5b13, 1.0f2) are the rule applied; Disk Copy 6.5b13's `'vers'` stores $13 [Fitted].

## 8. Not covered

- What the Finder does with a cut `'vers'` has not been traced.

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), Finder Interface: the version resource.
2. Apple, Rez template for `'vers'`, `SysTypes.r` (MPW).
3. Code traced: Finder 9.0 (Get Info, the list view's Version column).
