# Mac OS text encodings

Classic Mac OS text is bytes in the encoding of a script: Mac OS Roman for most Western systems, and for each other
script an encoding of its own. The Japanese, Chinese and Korean ones use one byte for ASCII and two for everything
else. A file says nothing about its encoding, except where a resource names a script or region. ClassicMac reads
every encoding below and, by default, Mac OS Roman.

| | |
| --- | --- |
| Used by | Every text the decoders read: `TEXT`, `STR `, `STR#`, `styl` documents, menus, dialogs, aliases, Finder resources, Help pages ([help-pages.md](../resources/help-pages.md)) |
| ClassicMac | Reads and writes; `ClassicMac.Core` (`MacEncodings`, `MacTextEncoding`) |
| Verified against | Apple's mapping tables, every code of all 16 (§7) |
| Sources | Apple's Mac OS mapping tables (unicode.org `VENDORS/APPLE`, version 2.x, Apple's licence); Apple's TextCommon.h (Universal Interfaces 3.4), the base encodings; *Inside Macintosh: Text* |

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

The encodings and their Text Encoding Converter numbers (`kTextEncodingMac…`) [Doc]:

| Encoding | Number | Bytes | Apple's table | IANA name |
| --- | --- | --- | --- | --- |
| Mac OS Roman | 0 | 1 | ROMAN.TXT | `macintosh` |
| Japanese | 1 | 1 or 2 (Shift-JIS) | JAPANESE.TXT | `x-mac-japanese` |
| Chinese Traditional | 2 | 1 or 2 (Big5) | CHINTRAD.TXT | `x-mac-chinesetrad` |
| Korean | 3 | 1 or 2 (KS X 1001) | KOREAN.TXT | `x-mac-korean` |
| Arabic | 4 | 1 | ARABIC.TXT | `x-mac-arabic` |
| Hebrew | 5 | 1 | HEBREW.TXT | `x-mac-hebrew` |
| Greek | 6 | 1 | GREEK.TXT | `x-mac-greek` |
| Cyrillic | 7 | 1 | CYRILLIC.TXT | `x-mac-cyrillic` |
| Thai | 21 | 1 | THAI.TXT | `x-mac-thai` |
| Chinese Simplified | 25 | 1 or 2 (GB 2312) | CHINSIMP.TXT | `x-mac-chinesesimp` |
| Central European | 29 | 1 | CENTEURO.TXT | `x-mac-ce` |
| Turkish | 35 | 1 | TURKISH.TXT | `x-mac-turkish` |
| Croatian | 36 | 1 | CROATIAN.TXT | `x-mac-croatian` |
| Icelandic | 37 | 1 | ICELAND.TXT | `x-mac-icelandic` |
| Romanian | 38 | 1 | ROMANIAN.TXT | `x-mac-romanian` |
| Ukrainian | 152 | 1 | UKRAINE.TXT | `x-mac-ukrainian` |

In the two-byte encodings a lead byte starts a two-byte code; every other byte is a character of its own [Doc]. Each
of Apple's tables maps every code to one Unicode character or a sequence of them, some with a direction hint (Arabic,
Hebrew) or a character from Apple's corporate-use area [Doc].

## 2. Reading

1. A byte that is not a lead byte is the character its table gives.
2. A lead byte and the byte after it are one code, the character the table gives.
3. A code the table does not define, and a lead byte whose next byte makes no code (or that ends the text), are not
   text.

[Doc]

## 3. Writing

Each character becomes its code; where a sequence maps to one code, the sequence is written as that code. A character
the encoding does not hold cannot be written [Doc].

## 4. Variants

None.

## 5. ClassicMac

- Mac OS Roman is ClassicMac's own table (`MacRoman`); the other encodings are read from .NET's Mac code pages into a
  table of every one- and two-byte code, a byte the decoder holds back being a lead byte. Where Apple's table differs
  (1,664 codes: Apple's additions in the two-byte encodings with their transcoding hints, the euro sign, Ω for
  U+2126, Romanian's comma-below letters, Arabic's and Hebrew's punctuation as ASCII, Mac OS Japanese's ¥ at $5C and
  backslash at $80), Apple's mapping is used, a two-byte code making its first byte a lead byte.
  `tools/EncodingTables` writes these corrections, as codes and Unicode only, into `MacEncodingCorrections.g.cs` from
  Apple's tables. [ClassicMac]
- A code that is not text (§2 step 3) becomes U+FFFD; after a lead byte whose next byte makes no code, that next byte
  is read on its own. [ClassicMac]
- Writing takes the longest text at each place that maps to a code (codes of one byte before codes of two); a
  character the encoding does not hold is refused. [ClassicMac]
- The decoders read text in `DecodeOptions.TextEncoding`, Mac OS Roman by default, and record its IANA name in the
  manifest. The CLI's `--encoding` sets it ([cli.md §2](../../cli.md#2-read-commands)). [ClassicMac]

## 6. Diagnostics

None.

## 7. Verification

- `tests/ClassicMac.Core.Tests/MacEncodingsTests.cs`: the numbers and names, names parsed, Mac OS Roman, each kind of
  script decoded and encoded back, Apple's mapping where the code pages differ, lead bytes without a second byte, text
  an encoding cannot hold. With Apple's mapping tables in `tests/golden/encodings` (gitignored, never committed), every
  code of every table is checked: all 16 match.
- `tests/ClassicMac.Cli.Tests/PathCommandTests.cs`, `Encoding_reads_text_in_another_Mac_script`.

## 8. Not covered

- Codes .NET's code pages define that Apple's tables do not are left as .NET reads them.
- File and resource names (`MacString`) are shown as Mac OS Roman.
- Choosing the encoding from what a file says: a font family's script, a `'vers'` region code, a `styl` run's font,
  HFS Plus's text encoding hints; and an encoding setting in the app.
- Symbol, Dingbats, the Indic scripts, Farsi, Celtic, Gaelic and Inuit.

## 9. References

1. Apple Computer, Mac OS mapping tables to Unicode, unicode.org `Public/MAPPINGS/VENDORS/APPLE` (2005), Apple's
   licence; not committed.
2. Apple Computer, `TextCommon.h`, Universal Interfaces 3.4: the base text encodings.
3. Apple Computer, *Inside Macintosh: Text* (1993): scripts and the Script Manager.
4. Microsoft, .NET's code pages 10001–10082 (`System.Text.Encoding.CodePages`, MIT): the tables ClassicMac reads,
   corrected to Apple's.
