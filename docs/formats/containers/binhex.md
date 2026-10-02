# BinHex 4.0

A BinHex 4.0 file (`.hqx`) is one Mac file as 7-bit text, made for mail and news: the file is laid out as a binary
stream (header, data fork, resource fork, each with a CRC), run-length encoded, then written six bits per character.
Yves Lempereur defined it; it has no Apple specification, and the only Apple BinHex code on a Mac OS 9.0 disk is an
encoder (§4.1). ClassicMac reads and writes it.

| | |
| --- | --- |
| Identified by | Extension `.hqx`; the line `(This file must be converted with BinHex 4.0)` followed by a colon |
| ClassicMac | Reads and writes; `ClassicMac.Files.Containers` (`BinHexReader`, `BinHexWriter`) |
| Verified against | Nothing yet |
| Sources | Lempereur's format as described by its author and by Peter N. Lewis; RFC 1741; the Web Sharing Extension 1.5.1 (Apple's encoder, traced) |

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

### 1.1 Text framing

The encoded text follows a line that reads `(This file must be converted with BinHex 4.0)`. Anything before that line
(mail or news headers, a message) is ignored. After the line, the encoded data starts after a colon `:` and ends at the
next colon. [Author] Encoders write the data in lines of 64 characters; line breaks carry no data. [Author]

### 1.2 Six-bit encoding

Each character stands for six bits. The 64 characters, in value order 0 to 63, are [Author]:

```
!"#$%&'()*+,-012345689@ABCDEFGHIJKLMNPQRSTUVXYZ[`abcdefhijklmpqr
```

| Values | Characters |
| --- | --- |
| 0–12 | `!` `"` `#` `$` `%` `&` `'` `(` `)` `*` `+` `,` `-` |
| 13–21 | `0`–`6`, `8`, `9` (no `7`) |
| 22 | `@` |
| 23–36 | `A`–`N` |
| 37–43 | `P`–`V` (no `O`) |
| 44–46 | `X` `Y` `Z` (no `W`) |
| 47–48 | `[` and the backquote |
| 49–54 | `a`–`f` |
| 55–60 | `h`–`m` (no `g`) |
| 61–63 | `p` `q` `r` (no `n`, `o`) |

The six-bit values are concatenated most significant bit first and cut into bytes: four characters make three bytes.
At the end, bits that do not make a whole byte are dropped. [Author]

### 1.3 Run-length encoding

The byte stream of §1.2 is run-length encoded with the marker byte `$90` [Author]:

| Bytes | Meaning |
| --- | --- |
| *b* (not `$90`) | the byte *b* |
| `$90 $00` | a literal `$90` byte |
| `$90` *n* (*n* ≥ 1) | the previous output byte repeated so that it appears *n* times in all, counting the one already written |

So `41 90 04` expands to `41 41 41 41`, and `90 00 90 03` to `90 90 90`: after a literal `$90`, a run repeats `$90`.
Runs are at most 255 long, and the encoding covers the whole binary stream, so a run may cross from one part into the
next (a fork into its CRC). [Author]

### 1.4 Binary stream

The expanded stream, with *n* the name length, *d* the data fork length and *r* the resource fork length [Author]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 1 | name length | `u8`, *n* = 1–63 [Author] |
| +1 | *n* | file name | Mac OS Roman [Author] |
| +1+*n* | 1 | version | `u8`, 0 [Author] |
| +2+*n* | 4 | file type | `OSType` [Author] |
| +6+*n* | 4 | creator | `OSType` [Author] |
| +10+*n* | 2 | Finder flags | `u16`, `fdFlags` [Author] |
| +12+*n* | 4 | data fork length | `u32`, *d* [Author] |
| +16+*n* | 4 | resource fork length | `u32`, *r* [Author] |
| +20+*n* | 2 | header CRC | `u16`, over bytes 0 to 19+*n* [Author] |
| +22+*n* | *d* | data fork | [Author] |
| +22+*n*+*d* | 2 | data fork CRC | `u16`, over the data fork [Author] |
| +24+*n*+*d* | *r* | resource fork | [Author] |
| +24+*n*+*d*+*r* | 2 | resource fork CRC | `u16`, over the resource fork [Author] |

There is no padding between parts. An empty fork is followed by its CRC, `$0000`. BinHex carries no location, folder,
`FXInfo` or dates. [Author]

### 1.5 CRCs

Each of the three CRCs is the CRC-16 of [unwrapping.md §1.3](unwrapping.md#13-crc-16) over its part alone, starting
from 0 for each part. [Author]

## 2. Reading

1. Find the marker text `(This file must be converted with BinHex` (without the version number and closing
   parenthesis) within the search window (§5). Without it, the input is not BinHex.
2. Require a CR or LF after the marker, then take the first colon after that line break (still within the window) as
   the start of the data. Without one, the input is not BinHex.
3. Decode characters into bytes (§1.2) up to the closing colon. CR, LF, space and tab are skipped. Any other character
   outside the alphabet stops decoding with `binhex.bad-character` (Error), keeping what was decoded. An input that
   ends before the closing colon is `binhex.truncated` (Error); what was decoded is used.
4. Expand runs (§1.3). `$90 $01` adds nothing, and a run at the very start repeats a zero byte. A `$90` that ends the
   data is `binhex.truncated` (Error).
5. Parse the header (§1.4). A name length outside 1–63, or too little data for the whole header (22 + *n* bytes),
   makes the input unusable. A version byte other than 0 is `binhex.version` (Info); reading goes on.
6. Check the header CRC, then slice the data fork and the resource fork and check each one's CRC. A CRC mismatch is
   `binhex.crc` (Warning) and the data is kept: a changed byte in a long download is better kept and flagged than
   lost. A CRC cut off by the end of the data is `binhex.truncated` (Error).
7. A fork longer than what was decoded is `binhex.fork-truncated` (Error): the decoded part is kept and its CRC is not
   checked.

The input is unusable (`container.unreadable`, [unwrapping.md §2.2](unwrapping.md#22-unwrapping-a-file)) when it has
no marker and data, when it or its expansion is larger than the expanded-bytes limit (§5), or when its header has no
valid name or is too short to hold its fields.

## 3. Writing

1. The binary stream of §1.4: name length and name, version 0, type, creator, Finder flags, fork lengths and the
   header CRC; the data fork and its CRC; the resource fork and its CRC.
2. Run-length encoded (§1.3): a `$90` byte as `$90 $00`, and a byte that appears 3 to 255 times in a row as the byte,
   `$90` and the count. Any choice of runs decodes the same. [Author]
3. Six bits per character (§1.2), the last character zero-filled.
4. The text: `(This file must be converted with BinHex 4.0)`, a line end, then `:`, the characters in lines of 64 (the
   first line counting the colon), and `:` and a line end at the end.

## 4. Variants

### 4.1 Apple's encoder

The only Apple BinHex code on a Mac OS 9.0 disk is an encoder in the Web Sharing Extension 1.5.1, which serves a file
as BinHex when a client asks for it [Code: Web Sharing Extension 1.5.1]:

1. It writes a cache file named after the source (cut to 28 characters) plus `.hqx`, of type `'TEXT'` and creator
   `'ttxt'`, and rewrites it when the source's modification date is newer.
2. The text is the marker line, then 64-character lines, as in §1.1.
3. The header takes the name from the file's specification (so at most 31 bytes), version 0, the type and creator,
   and `fdFlags` as stored, with no bits cleared.
4. The fork lengths are the forks' end-of-file values, signed 32-bit, with **no cap**; the data fork, then the
   resource fork, each followed by its CRC (§1.5).

It has no decoder. URL Access and the Software Update engine decode `.hqx` and `.bin` files with Aladdin's StuffIt
code linked into them, not Apple's; Internet Config holds only the file-type mappings [Code: Mac OS 9.0 disk].

Its files read like any other: a 31-byte name is within 1–63, and a fork is limited only by the expanded-bytes limit
(§5).

### 4.2 Earlier versions

BinHex 1.0, 2.0 and 3.0 (`.hex`, `.hcx`) are different formats (§8).

## 5. ClassicMac

- The marker is looked for in the first 64 KiB of the input, so mail and news headers of any usual size precede it.
  [Fitted]
- The reader reads the whole input into memory, at most the expanded-bytes limit (`ContainerReadOptions.MaxExpandedBytesPerInput`,
  [unwrapping.md §5](unwrapping.md#5-classicmac)); a larger input, or an expansion that grows past it, is unusable.
  [ClassicMac]
- All three CRCs are checked; a mismatch is a Warning, not an error (§2 step 6). [ClassicMac]
- The location, folder and `FXInfo` are left zero and the dates unrecorded. [ClassicMac]
- `BinHexWriter` writes §3 with CR line ends, as on the Mac; it encodes runs of 3 or more. A name longer than 63 bytes
  is cut to 63; an empty name is written as `?`. [ClassicMac]
- Format name: `BinHex 4.0`.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `binhex.bad-character` | Error | a character outside the alphabet (and not whitespace) inside the data | stops decoding; keeps what was decoded | no Apple decoder [Code: Mac OS 9.0 disk] |
| `binhex.crc` | Warning | a header or fork CRC does not match | keeps the data | no Apple decoder; the format's CRCs exist to reject a damaged transfer [Author] |
| `binhex.fork-truncated` | Error | a fork is longer than the decoded data | keeps the decoded part; skips its CRC | no Apple decoder |
| `binhex.truncated` | Error | the input ends before the closing colon; or inside a `$90` run; or where a CRC should be | keeps what was decoded | no Apple decoder |
| `binhex.version` | Info | the header's version byte is not 0 | reads on | no Apple decoder |

## 7. Verification

No file made by a BinHex application is in the tests; the inputs are built by `tests/ClassicMac.Files.Tests/Fixtures.cs`
(`BinHex`).

- `tests/ClassicMac.Files.Tests/BinHexTests.cs`: `Decodes_name_Finder_info_and_both_forks` (§1, §2),
  `Long_runs_expand` (§1.3), `A_CRC_mismatch_is_a_warning`, `Truncated_text_keeps_what_was_decoded`,
  `Characters_outside_the_alphabet_stop_decoding`, `Without_a_header_the_input_is_unusable`, `Expansion_is_limited`,
  `Text_without_the_marker_is_not_BinHex`.
- `tests/ClassicMac.Files.Tests/ContainerWriterTests.cs`: `BinHex_reads_back` (§3 read back by §2),
  `BinHex_run_length_encoding_marks_literal_90_bytes_and_runs_of_three_or_more` (§3 step 2).
- `tests/ClassicMac.Files.Tests/UnwrapTests.cs`: `Nested_containers_unwrap_to_a_tree` (BinHex inside AppleSingle,
  holding MacBinary).

## 8. Not covered

- BinHex 1.0, 2.0 and 3.0 (`.hex`, `.hcx`): not recognised.
- BinHex 4.0 files split into several parts (as Usenet posted them) must be joined first.
- MIME encapsulation ([unwrapping.md §8](unwrapping.md#8-not-covered)).

## 9. References

1. Yves Lempereur, BinHex 4.0, as described by its author.
2. Peter N. Lewis, *BinHex 4.0 Definition* (1991).
3. RFC 1741, MIME content type for BinHex encoded files.
4. Shared structures (Finder information, CRC-16): [unwrapping.md](unwrapping.md).
