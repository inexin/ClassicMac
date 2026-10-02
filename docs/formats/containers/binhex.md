# BinHex 4.0

A BinHex 4.0 file (`.hqx`) is a Mac file as 7-bit text: the file is laid out as a binary stream (header, data fork,
resource fork, each with a CRC), run-length encoded, then written six bits per character. Decoding reverses the three
steps: find the text, turn characters into bytes, expand runs, then parse the binary stream. [Author]

Contents

1. [Text framing](#1-text-framing)
2. [Six-bit encoding](#2-six-bit-encoding)
3. [Run-length encoding](#3-run-length-encoding)
4. [Binary layout](#4-binary-layout)
5. [CRCs](#5-crcs)
6. [Reading](#6-reading)
7. [Apple's encoder](#7-apples-encoder)
8. [Writing BinHex 4.0](#8-writing-binhex-40)
9. [Diagnostics](#9-diagnostics)
10. [Not covered](#10-not-covered)

---

## 1. Text framing

The encoded text follows a line that reads `(This file must be converted with BinHex 4.0)`. Anything before that line
(mail or news headers, a message) is ignored. After the line, the encoded data starts after a colon `:` and ends at the
next colon. [Author] Encoders write the data in lines of 64 characters; line breaks carry no data and are skipped.
[Author]

ClassicMac's reader:

- looks for the text `(This file must be converted with BinHex` (without the version number and closing parenthesis)
  in the first 64 KiB of the input [Fitted], so mail and news headers of any usual size precede it;
- requires a CR or LF after the marker, then takes the first colon after that line break (still within the first
  64 KiB) as the start of the data;
- skips CR, LF, space and tab inside the data; any other character outside the alphabet stops decoding with
  `binhex.bad-character` (Error), keeping what was decoded;
- reports `binhex.truncated` (Error) when the input ends before the closing colon, and decodes what it has.

---

## 2. Six-bit encoding

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
[Author] At the end, bits that do not make a whole byte are dropped. [Author]

---

## 3. Run-length encoding

The byte stream from §2 is run-length encoded with the marker byte `$90` [Author]:

| Bytes | Meaning |
| --- | --- |
| *b* (not `$90`) | the byte *b* |
| `$90 $00` | a literal `$90` byte |
| `$90` *n* (*n* ≥ 1) | the previous output byte repeated so that it appears *n* times in all, counting the one already written |

So `41 90 04` expands to `41 41 41 41`, and `90 00 90 03` to `90 90 90`: after a literal `$90`, a run repeats `$90`.
[Author] Runs are at most 255 long, and the encoding covers the whole binary stream, so a run may cross from one part
into the next (a fork into its CRC). [Author]

ClassicMac treats `$90 $01` as adding nothing and a run at the very start as repeating a zero byte; reports a `$90`
that ends the data as `binhex.truncated` (Error); and stops with an error (the input is unreadable) when the expanded
stream grows past `MaxExpandedBytesPerInput` ([unwrapping.md §3.3](unwrapping.md#33-expanded-bytes-limit)).

---

## 4. Binary layout

The expanded stream [Author]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| 0 | 1 | `u8` | name length *n*, 1–63 [Author] |
| 1 | *n* | bytes | file name, Mac OS Roman [Author] |
| 1+*n* | 1 | `u8` | version, 0 [Author] |
| 2+*n* | 4 | `OSType` | file type [Author] |
| 6+*n* | 4 | `OSType` | creator [Author] |
| 10+*n* | 2 | `u16` | Finder flags (`fdFlags`) [Author] |
| 12+*n* | 4 | `u32` | data fork length *d* [Author] |
| 16+*n* | 4 | `u32` | resource fork length *r* [Author] |
| 20+*n* | 2 | `u16` | header CRC, over bytes 0 to 19+*n* [Author] |
| 22+*n* | *d* | bytes | data fork [Author] |
| 22+*n*+*d* | 2 | `u16` | data fork CRC, over the data fork [Author] |
| 24+*n*+*d* | *r* | bytes | resource fork [Author] |
| 24+*n*+*d*+*r* | 2 | `u16` | resource fork CRC, over the resource fork [Author] |

There is no padding between parts. An empty fork is followed by its CRC, `$0000`. [Author] BinHex carries no
location, folder, `FXInfo` or dates [Author]; ClassicMac leaves them zero and the dates unrecorded.

---

## 5. CRCs

Each of the three CRCs is the CRC-16 of [unwrapping.md §2.3](unwrapping.md#23-crc-16) over its part alone, starting from 0 for each part. [Author]

ClassicMac checks all three. A mismatch is `binhex.crc` (Warning) and the data is kept: a changed byte in a long
download is better kept and flagged than lost. A CRC cut off by the end of the data is `binhex.truncated` (Error).

---

## 6. Reading

ClassicMac:

1. Reads the whole input into memory (at most `MaxExpandedBytesPerInput`; a larger input is unreadable).
2. Decodes (§§1–2) and expands (§3).
3. Requires a name length of 1–63 and room for the whole header (22 + *n* bytes); otherwise the input is unreadable.
4. Reports a version byte other than 0 as `binhex.version` (Info) and reads on.
5. Checks the header CRC, then slices the data fork and the resource fork. A fork longer than what was decoded is
   `binhex.fork-truncated` (Error); the decoded part is kept and its CRC is not checked.

---

## 7. Apple's encoder

The only Apple BinHex code on a Mac OS 9.0 disk is an encoder in the Web Sharing Extension 1.5.1, which serves a file
as BinHex when a client asks for it [Code: Web Sharing Extension 1.5.1]:

- It writes a cache file named after the source (cut to 28 characters) plus `.hqx`, of type `'TEXT'` and creator
  `'ttxt'`, and rewrites it when the source's modification date is newer.
- The text is the marker line, then 64-character lines, as in 4.1.
- The header takes the name from the file's specification (so at most 31 bytes), version 0, the type and creator, and
  `fdFlags` as stored, with no bits cleared.
- The fork lengths are the forks' end-of-file values, signed 32-bit, with **no cap**; the data fork, then the
  resource fork, each followed by its CRC ([unwrapping.md §2.3](unwrapping.md#23-crc-16)).

It has no decoder. URL Access and the Software Update engine decode `.hqx` and `.bin` files with Aladdin's StuffIt
code linked into them, not Apple's; Internet Config holds only the file-type mappings [Code: Mac OS 9.0 disk].

ClassicMac reads such files like any other: a 31-byte name is within 1–63, and it limits a fork only by
`MaxExpandedBytesPerInput` ([unwrapping.md §3.3](unwrapping.md#33-expanded-bytes-limit)).

---

## 8. Writing BinHex 4.0

`BinHexWriter` writes BinHex 4.0 [Author]:

- The binary stream of §4: name (cut to 63 bytes; `?` when empty [ClassicMac]), version 0, type, creator,
  Finder flags, fork lengths and the header CRC; the data fork and its CRC; the resource fork and its CRC.
- Run-length encoded (§3): a `$90` byte as `$90 $00`, and a byte that appears 3 to 255 times in a row as the
  byte, `$90` and the count [ClassicMac: any run length decodes the same].
- Six bits per character (§2), the last character zero-filled.
- The text: `(This file must be converted with BinHex 4.0)`, CR, then `:`, the characters in lines of 64 (the first
  line counting the colon), and `:` and CR at the end [ClassicMac: CR line ends, as on the Mac].

---

## 9. Diagnostics

Codes these readers and the unwrapper emit. Offsets, where given, are in the container's input. None of these formats
is handled by Mac OS ([applesingle-appledouble.md](applesingle-appledouble.md)), so the "original tools" column describes the format authors' intent where it is
stated; the behaviour of MacBinary, BinHex and StuffIt programs was not traced.

| Code | Severity | Meaning | ClassicMac | Original tools |
| --- | --- | --- | --- | --- |
| `binhex.bad-character` | Error | a character outside the alphabet (and not whitespace) inside the data | stops decoding; keeps what was decoded | not traced |
| `binhex.truncated` | Error | the input ends before the closing colon; or inside a `$90` run; or where a CRC should be | keeps what was decoded | not traced |
| `binhex.fork-truncated` | Error | a fork is longer than the decoded data | keeps the decoded part; skips its CRC | not traced |
| `binhex.crc` | Warning | a header or fork CRC does not match | keeps the data | the format's CRCs exist to reject a damaged transfer [Author] |
| `binhex.version` | Info | the header's version byte is not 0 | reads on | not traced |

---

## 10. Not covered

- BinHex 1.0, 2.0 and 3.0 (`.hex`, `.hcx`): not recognised. BinHex 4.0 files split into several parts (as Usenet
  posted them) must be joined first.
