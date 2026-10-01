# Single-file containers — an implementer's specification

This document describes the wrappers that carry one Macintosh file (name, Finder information, dates, data fork and
resource fork) through systems that know only flat byte streams: MacBinary I, II and III, BinHex 4.0, AppleSingle and
AppleDouble; and uuencode, which carries a file's bytes alone (often one of the others). It is complete enough to write a reader for each of them, and the AppleDouble writer, without reading
ClassicMac's code. It also describes how ClassicMac unwraps containers nested in one another (a MacBinary file inside a
BinHex file inside an AppleSingle file). The code is in `ClassicMac.Files.Containers` (the readers and the writer) and
`ClassicMac.Files` (`ContainerUnwrapper`, `ContainerReadOptions`).

None of these formats is read by Mac OS itself. MacBinary and BinHex were defined by their authors for modem
transfers and have no Apple specification. On a Mac OS 9.0 disk no Apple code handles MacBinary, and the only Apple
BinHex code is an encoder in the Web Sharing Extension (section 4.7). AppleSingle and AppleDouble were defined by
Apple for A/UX and foreign file systems, but no Apple code in Mac OS 7.1–9 handles them (section 5). So the authors'
and Apple's published descriptions are the whole reference, and nothing here could be checked in an emulator.

References:

- Apple Computer, *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note, versions 1 and 2; version 2 is
  also the basis of RFC 1740 (MIME encapsulation of Macintosh files).
- The MacBinary I specification (1985), the MacBinary II specification (1987) and the MacBinary III specification
  (1996).
- Yves Lempereur's BinHex 4.0 format, as described by its author and in Peter N. Lewis's *BinHex 4.0 Definition*
  (1991); RFC 1741 (MIME content type for BinHex encoded files).
- The Open Group, *The Single UNIX Specification* (POSIX.1-2017), `uuencode` (the [Doc] source of section 8).
- *Inside Macintosh: Macintosh Toolbox Essentials*, the Finder Interface chapter (`FInfo`, `FXInfo`, Finder flags).
- Shared conventions and source tags: [README.md](README.md). Host-folder layouts that use AppleDouble (`._` files,
  `name.rsrc`) and what `unpack` writes: [HOST-FOLDERS.md](HOST-FOLDERS.md).

Contents

1. [Conventions](#1-conventions)
2. [Shared structures](#2-shared-structures)
3. [MacBinary I, II and III](#3-macbinary-i-ii-and-iii)
4. [BinHex 4.0](#4-binhex-40)
5. [AppleSingle and AppleDouble](#5-applesingle-and-appledouble)
6. [Writing containers](#6-writing-containers)
7. [Unwrapping nested containers](#7-unwrapping-nested-containers)
8. [uuencode](#8-uuencode)
9. [Diagnostics](#9-diagnostics)
10. [Not covered](#10-not-covered)

---

## 1. Conventions

The conventions of [README.md](README.md) hold: big-endian values, `u8`/`u16`/`u32` and `i16`/`i32`, `OSType` codes in
quotes, Mac dates as local-time seconds since 1904, structures as offset/size/type/meaning tables, and the source tags
**[Doc]**, **[Code]**, **[Verified]**, **[Author]** and **[Fitted]**. In this document:

- **[Doc]** is the *AppleSingle/AppleDouble Formats for Foreign Files* Developer Note (version 1 or 2, as stated) or
  *Inside Macintosh*; in section 8 (uuencode) it is the Single UNIX Specification. **[Author]** is one of the
  MacBinary specifications or the BinHex 4.0 description.
- **[Code]** appears only for the findings that Mac OS does not handle these formats, and for Apple's one BinHex
  encoder (section 4.7). **[Verified]** does not appear: there is no Apple reader to check against.
- A tag at the end of a table row or sentence covers that row or sentence.
- Sentences that begin "ClassicMac …" describe the reader's own choices where the sources leave room: limits, how
  damage is recovered from, diagnostic severities. They are not claims about the format and carry no tag.
- MacBinary offsets are given in hex with the decimal offset the specifications use in parentheses: `+$53` (83).
- "The reader" means a reader that follows this document; "ClassicMac" means ClassicMac's implementation of it.
- A **fork** is a byte sequence of known length. A missing fork is the same as an empty one.

---

## 2. Shared structures

### 2.1 Finder information

All three families carry parts of the Finder's `FInfo` record, and AppleSingle and MacBinary III parts of `FXInfo`.
ClassicMac keeps both as one 32-byte block, `FInfo` then `FXInfo`, the way AppleSingle stores them. [Doc]

`FInfo` (16 bytes):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `OSType` | `fdType`: file type [Doc] |
| +$04 | 4 | `OSType` | `fdCreator`: creator [Doc] |
| +$08 | 2 | `u16` | `fdFlags`: Finder flags (below) [Doc] |
| +$0A | 4 | `Point` | `fdLocation`: the icon's position in its window, v then h [Doc] |
| +$0E | 2 | `i16` | `fdFldr`: the window (folder) the icon is in [Doc] |

`FXInfo` (16 bytes):

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 2 | `i16` | `fdIconID` [Doc] |
| +$02 | 6 | `i16`[3] | reserved [Doc] |
| +$08 | 1 | `i8` | `fdScript`: script code of the name [Doc] |
| +$09 | 1 | `i8` | `fdXFlags`: extended flags [Doc] |
| +$0A | 2 | `i16` | `fdComment`: Finder comment ID [Doc] |
| +$0C | 4 | `i32` | `fdPutAway`: home directory ID [Doc] |

ClassicMac keeps `FXInfo` as 16 uninterpreted bytes, so containers round-trip it.

Finder flag bits (`fdFlags`):

| Bit | Mask | Meaning |
| --- | --- | --- |
| 0 | `$0001` | `isOnDesk`: on the desktop (System 6 and earlier) [Doc] |
| 1–3 | `$000E` | colour label [Doc] |
| 6 | `$0040` | `isShared`: an application that can run more than once from a shared volume [Doc] |
| 7 | `$0080` | `hasNoINITs`: no `INIT` resources [Doc] |
| 8 | `$0100` | `hasBeenInited`: the Finder has recorded the bundle [Doc] |
| 10 | `$0400` | `hasCustomIcon` (resource ID −16455) [Doc] |
| 11 | `$0800` | `isStationery` [Doc] |
| 12 | `$1000` | `nameLocked` [Doc] |
| 13 | `$2000` | `hasBundle`: the file has a `BNDL` resource [Doc] |
| 14 | `$4000` | `isInvisible` [Doc] |
| 15 | `$8000` | `isAlias` [Doc] |

### 2.2 Dates

MacBinary and AppleSingle version 1 store Mac dates: `u32` seconds since 1 January 1904 in the writing Mac's local
time, covering 1904 to 6 February 2040 [Doc]. AppleSingle version 2 stores signed seconds from 1 January 2000,
00:00 UTC (section 5.4) [Doc]. BinHex stores no dates [Author].

ClassicMac treats a MacBinary date of 0 as "not recorded".

### 2.3 CRC-16

MacBinary II/III and BinHex 4.0 use the same 16-bit CRC: polynomial `$1021`, initial value 0, bits processed most
significant first, no reflection and no final XOR [Author]. This is the CRC of the XMODEM protocol (the "CCITT" CRC
of the specifications); the check value over the nine ASCII bytes `123456789` is `$31C3`. A table-driven form, per
byte `b`:

```
crc = (crc << 8) XOR table[((crc >> 8) XOR b) AND $FF]      (16-bit crc)
table[i] = i << 8, then 8 times: if the top bit is set, shift left and XOR $1021, else shift left
```

Some descriptions compute the CRC bit by bit and then feed two zero bytes after the data; that "augmented" form gives
the same value as the direct form above. [Author] The CRC of zero bytes is 0.

---

## 3. MacBinary I, II and III

### 3.1 Layout

A MacBinary file is a 128-byte header, then the data fork, then the resource fork. Each fork is padded with zero bytes
to a multiple of 128; a fork of length 0 takes no space. [Author]

MacBinary II adds an optional secondary header between the header and the data fork, its length at `+$78`, padded to
a multiple of 128; and an optional Finder comment after the resource fork, its length at `+$63`. [Author]

```
+0                      128-byte header
+128                    secondary header, pad(secondaryLength) bytes        (II and III only)
+128+pad(sec)           data fork, pad(dataLength) bytes
  …                     resource fork, pad(resourceLength) bytes
  …                     Get Info comment, commentLength bytes                (II and III, optional)
pad(n) = n rounded up to a multiple of 128
```

The last fork need not be padded: files whose final fork ends the file are common. [Fitted]

### 3.2 Header

| Offset | Size | Type | Meaning | Versions |
| --- | --- | --- | --- | --- |
| +$00 (0) | 1 | `u8` | old version number; must be 0 [Author] | all |
| +$01 (1) | 1 | `u8` | name length, 1–63 [Author] | all |
| +$02 (2) | 63 | bytes | file name, Mac OS Roman; only the first *length* bytes count [Author] | all |
| +$41 (65) | 4 | `OSType` | file type [Author] | all |
| +$45 (69) | 4 | `OSType` | creator [Author] | all |
| +$49 (73) | 1 | `u8` | Finder flags, high byte (bits 8–15) [Author] | all |
| +$4A (74) | 1 | `u8` | zero fill; must be 0 [Author] | all |
| +$4B (75) | 2 | `i16` | icon position, vertical [Author] | all |
| +$4D (77) | 2 | `i16` | icon position, horizontal [Author] | all |
| +$4F (79) | 2 | `i16` | window or folder ID (`fdFldr`) [Author] | all |
| +$51 (81) | 1 | `u8` | "protected" flag in bit 0 [Author] | all |
| +$52 (82) | 1 | `u8` | zero fill; must be 0 [Author] | all |
| +$53 (83) | 4 | `u32` | data fork length [Author] | all |
| +$57 (87) | 4 | `u32` | resource fork length [Author] | all |
| +$5B (91) | 4 | `u32` | creation date (Mac date) [Author] | all |
| +$5F (95) | 4 | `u32` | modification date (Mac date) [Author] | all |
| +$63 (99) | 2 | `u16` | length of the Get Info comment that follows the resource fork [Author] | II, III |
| +$65 (101) | 1 | `u8` | Finder flags, low byte (bits 0–7) [Author] | II, III |
| +$66 (102) | 4 | `OSType` | signature `'mBIN'` [Author] | III |
| +$6A (106) | 1 | `i8` | `fdScript` (`FXInfo` +8) [Author] | III |
| +$6B (107) | 1 | `u8` | `fdXFlags` (`FXInfo` +9) [Author] | III |
| +$6C (108) | 8 | bytes | reserved, zero [Author] | II, III |
| +$74 (116) | 4 | `u32` | total length of the files when unpacked (for on-the-fly compressors) [Author] | II, III |
| +$78 (120) | 2 | `u16` | secondary header length [Author] | II, III |
| +$7A (122) | 1 | `u8` | version of MacBinary the writer follows: 129 for II, 130 for III [Author] | II, III |
| +$7B (123) | 1 | `u8` | minimum version needed to read the file: 129 [Author] | II, III |
| +$7C (124) | 2 | `u16` | CRC-16 (section 2.3) of bytes 0–123 [Author] | II, III |
| +$7E (126) | 2 | bytes | reserved for a computer type and OS ID; zero for the Mac [Author] | II, III |

In MacBinary I, bytes 99–127 are unused and zero, and there are no low Finder flags. [Author]

### 3.3 Versions and detection

Only MacBinary III has a signature. II is told from I by its header CRC, and I has nothing but its zero bytes, so
detection must be strict or ordinary data files starting with a zero byte pass as MacBinary I. [Author] ClassicMac
tries III, then II, then I; each version is a separate reader with its own test, and a header matches at most one.

Checks common to all versions:

1. The input has at least 128 bytes.
2. Bytes 0, 74 and 82 are zero. [Author]
3. The name length (byte 1) is 1–63. [Author]
4. The name contains neither `:` (the HFS path separator, which no Mac name contains) nor a NUL byte. [Fitted]
5. Both fork lengths are at most `$7FFFFF` (8 MiB − 1). [Author] for MacBinary I; ClassicMac applies the limit to II
   and III as well. No Apple code on the Mac OS 9.0 disk reads or writes MacBinary, so there is no Apple limit to
   match [Code: Mac OS 9.0 disk]: the limit is ClassicMac's choice, taken from the MacBinary authors'.

Then:

| Test | Result |
| --- | --- |
| CRC-16 of bytes 0–123 equals the `u16` at +124, and bytes 102–105 are `'mBIN'` | MacBinary III [Author] |
| CRC matches, no `'mBIN'` | MacBinary II [Author] |
| CRC does not match, and bytes 99–125 are all zero | MacBinary I candidate [Fitted] |
| anything else | not MacBinary |

A MacBinary I candidate is accepted only if the input's length is exactly what the header implies: either
`128 + pad(dataLength) + pad(resourceLength)` (both forks padded), or the same with the last non-empty fork unpadded:
`128 + pad(dataLength) + resourceLength` when there is a resource fork, `128 + dataLength` when there is not. [Fitted]
Longer and shorter inputs are rejected. For example, a 10-byte data fork and a 1-byte resource fork give 384 or 257
bytes.

What this rejects, and why:

- A MacBinary II or III file whose header CRC is damaged: its bytes 99–125 are not zero, so it is not MacBinary I
  either. It is treated as a plain file, with no diagnostic. [Fitted]
- Raw resource forks: byte 1 is the second byte of the data offset, normally 0, so the name length is 0.
- Data files that start with zeros but hold NUL bytes in the "name", or whose "fork lengths" do not add up to the file
  length. [Fitted]

ClassicMac does not check bytes 122 and 123 (the writer's version and the minimum version), nor the reserved bytes
108–119 and 126–127 for II and III. Neither does it check the total length of a II or III file, so trailing data
(such as a Get Info comment) is ignored.

### 3.4 Reading

- **Name**: bytes 2 to 2 + length, kept as bytes (Mac OS Roman). [Author]
- **Finder info**: type, creator, location (v at 75, h at 77) and folder (79) into `FInfo`. `fdFlags` is byte 73 as
  the high byte and, for II and III, byte 101 as the low byte; MacBinary I files have a zero low byte. [Author]
  For III, byte 106 goes to `FXInfo` +8 and byte 107 to `FXInfo` +9; the rest of `FXInfo` is zero. [Author] The
  protected flag (byte 81) is not part of the Finder info; ClassicMac ignores it.
- **Dates**: creation at 91, modification at 95, as Mac dates. ClassicMac takes 0 as "no date".
- **Forks**: the data fork starts at `128 + pad(secondaryLength)` (secondary length 0 for MacBinary I), and the
  resource fork at the data fork's start plus `pad(dataLength)`. [Author]
- **Comment**: the Get Info comment (length at 99, after the resource fork) is not read by ClassicMac.

ClassicMac hands out the forks as slices of the input without copying. If a fork runs past the end of the input, it
reports `macbinary.fork-truncated` (Error) and keeps the bytes that are there; a resource fork that starts past the
end is empty. Since MacBinary I is only detected at its exact length, this happens only for II and III.

---

## 4. BinHex 4.0

A BinHex 4.0 file (`.hqx`) is a Mac file as 7-bit text: the file is laid out as a binary stream (header, data fork,
resource fork, each with a CRC), run-length encoded, then written six bits per character. Decoding reverses the three
steps: find the text, turn characters into bytes, expand runs, then parse the binary stream. [Author]

### 4.1 Text framing

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

### 4.2 Six-bit encoding

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

### 4.3 Run-length encoding

The byte stream from 4.2 is run-length encoded with the marker byte `$90` [Author]:

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
stream grows past `MaxExpandedBytesPerInput` (section 7.3).

### 4.4 Binary layout

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

### 4.5 CRCs

Each of the three CRCs is the CRC-16 of section 2.3 over its part alone, starting from 0 for each part. [Author]

ClassicMac checks all three. A mismatch is `binhex.crc` (Warning) and the data is kept: a changed byte in a long
download is better kept and flagged than lost. A CRC cut off by the end of the data is `binhex.truncated` (Error).

### 4.6 Reading

ClassicMac:

1. Reads the whole input into memory (at most `MaxExpandedBytesPerInput`; a larger input is unreadable).
2. Decodes (4.1–4.2) and expands (4.3).
3. Requires a name length of 1–63 and room for the whole header (22 + *n* bytes); otherwise the input is unreadable.
4. Reports a version byte other than 0 as `binhex.version` (Info) and reads on.
5. Checks the header CRC, then slices the data fork and the resource fork. A fork longer than what was decoded is
   `binhex.fork-truncated` (Error); the decoded part is kept and its CRC is not checked.

### 4.7 Apple's encoder

The only Apple BinHex code on a Mac OS 9.0 disk is an encoder in the Web Sharing Extension 1.5.1, which serves a file
as BinHex when a client asks for it [Code: Web Sharing Extension 1.5.1]:

- It writes a cache file named after the source (cut to 28 characters) plus `.hqx`, of type `'TEXT'` and creator
  `'ttxt'`, and rewrites it when the source's modification date is newer.
- The text is the marker line, then 64-character lines, as in 4.1.
- The header takes the name from the file's specification (so at most 31 bytes), version 0, the type and creator, and
  `fdFlags` as stored, with no bits cleared.
- The fork lengths are the forks' end-of-file values, signed 32-bit, with **no cap**; the data fork, then the
  resource fork, each followed by its CRC (section 2.3).

It has no decoder. URL Access and the Software Update engine decode `.hqx` and `.bin` files with Aladdin's StuffIt
code linked into them, not Apple's; Internet Config holds only the file-type mappings [Code: Mac OS 9.0 disk].

ClassicMac reads such files like any other: a 31-byte name is within 1–63, and it limits a fork only by
`MaxExpandedBytesPerInput` (section 7.3).

---

## 5. AppleSingle and AppleDouble

AppleSingle stores a whole file (both forks and its metadata) in one file of entries. AppleDouble splits it: the data
fork goes into a plain *data file*, everything else into a *header file* in AppleSingle's layout with its own magic
number. [Doc] The two formats share every structure below.

No Apple code in Mac OS 7.1 to 9 reads or writes either format: File Exchange 3.0.2 (Mac OS 9.0), PC Exchange 1.0.4
and Foreign File Access contain neither the magic numbers nor code that builds these files, and on a Mac OS 9.0 disk
only mail clients (Netscape Communicator, Outlook Express 4.5, decoding `application/applefile` and
`multipart/appledouble` attachments) and the StuffIt Engine handle them. [Code] PC Exchange and File Exchange use
their own private format (see [FAT.md](FAT.md)).

### 5.1 Header

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `u32` | magic number: `$00051600` AppleSingle, `$00051607` AppleDouble header file [Doc] |
| +$04 | 4 | `u32` | version: `$00010000` (version 1) or `$00020000` (version 2) [Doc] |
| +$08 | 16 | bytes | version 1: the home file system's name, ASCII, padded with spaces; version 2: filler, all zero [Doc] |
| +$18 | 2 | `u16` | number of entries, may be 0 [Doc] |
| +$1A | 12 × count | | the entry table (5.2) [Doc] |

The version 1 home file system names are `Macintosh`, `ProDOS`, `MS-DOS`, `Unix` and `VAX VMS`. [Doc]

A reader recognises a file by the magic number and a version of 1 or 2. ClassicMac requires both and the full 26-byte
header; it rejects other versions (`$00030000`, for instance) rather than guess at them.

### 5.2 Entry table

Each entry descriptor is 12 bytes [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `u32` | entry ID (5.3) [Doc] |
| +$04 | 4 | `u32` | offset of the entry's data from the start of the file [Doc] |
| +$08 | 4 | `u32` | length of the entry's data; may be 0 [Doc] |

Entries may be stored in any order, and their data need not follow the table in the same order. [Doc] Readers skip
entries whose IDs they do not know. [Doc]

ClassicMac:

- reports a table cut off by the end of the file as `applesingle.entries-truncated` (Error) and reads the complete
  descriptors;
- skips an entry whose offset plus length runs past the end of the file, reporting `applesingle.entry-out-of-range`
  (Error);
- lets a later entry with the same ID replace an earlier one;
- slices forks out of the input without copying.

### 5.3 Entry IDs

| ID | Name | Versions | ClassicMac |
| --- | --- | --- | --- |
| 1 | Data Fork | 1, 2 [Doc] | data fork |
| 2 | Resource Fork | 1, 2 [Doc] | resource fork |
| 3 | Real Name | 1, 2 [Doc] | name |
| 4 | Comment | 1, 2 [Doc] | ignored |
| 5 | Icon, black and white | 1, 2 [Doc] | ignored |
| 6 | Icon, colour | 1, 2 [Doc] | ignored |
| 7 | File Info | 1 only [Doc] | creation and modification dates when the home file system is `Macintosh`; else ignored |
| 8 | File Dates Info | 2 [Doc] | creation and modification dates |
| 9 | Finder Info | 1, 2 [Doc] | Finder info |
| 10 | Macintosh File Info | 2 [Doc] | ignored |
| 11 | ProDOS File Info | 2 [Doc] | ignored |
| 12 | MS-DOS File Info | 2 [Doc] | ignored |
| 13 | AFP Short Name | 2 [Doc] | ignored |
| 14 | AFP File Info | 2 [Doc] | ignored |
| 15 | AFP Directory ID | 2 [Doc] | ignored |

ClassicMac reports any other ID as `applesingle.unknown-entry` (Info) and skips it. It does not check which version
defines an ID: entry 8 is read in a version 1 file too (with the version 2 layout), and entry 7 is read only in
version 1 files whose home file system is exactly `Macintosh`.

### 5.4 Entry formats

**Data Fork (1), Resource Fork (2).** The fork's bytes. [Doc] An AppleDouble header file holds no data fork: the data
file is the data fork. [Doc] ClassicMac reads a data fork found in an AppleDouble header anyway, and reports
`applesingle.double-data-fork` (Warning).

**Real Name (3).** The file's name on its home file system, as bytes with no length byte or terminator. [Doc]
ClassicMac keeps the bytes as a Mac name (Mac OS Roman), cut to 255 bytes with `applesingle.name-too-long` (Warning).
Without a Real Name entry, ClassicMac uses the name the input had on the host or in its outer container; with neither,
it reports `applesingle.no-name` (Info) and the name is empty.

**Comment (4), Icons (5, 6).** The Finder comment and icon images. [Doc] Not read by ClassicMac.

**File Info (7), version 1.** Its layout depends on the home file system. [Doc] For `Macintosh` [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `u32` | creation date, Mac date (local, since 1904) [Doc] |
| +$04 | 4 | `u32` | modification date, Mac date [Doc] |
| +$08 | 4 | `u32` | backup date, Mac date [Doc] |
| +$0C | 4 | `u32` | attributes: bit 0 locked, bit 1 protected [Doc] |

ClassicMac reads the first two dates as they are. Fewer than 8 bytes: `applesingle.dates-truncated` (Warning), no
dates.

**File Dates Info (8), version 2** [Doc]:

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 4 | `i32` | creation date [Doc] |
| +$04 | 4 | `i32` | modification date [Doc] |
| +$08 | 4 | `i32` | backup date [Doc] |
| +$0C | 4 | `i32` | last access date [Doc] |

Each date is signed seconds from 1 January 2000, 00:00 UTC; `$80000000` means unknown. [Doc] Seconds 0 is Mac date
3,029,529,600 in UTC.

Mac dates are local, so a reader converts the UTC value into the local time of the Mac that will use the file.
ClassicMac uses `ContainerReadOptions.TimeZone` (default: the machine's zone): in a UTC+2 zone, 3600 becomes
1 January 2000, 03:00. It reads creation and modification only; unknown dates stay unrecorded. A date outside
1904–2040 is `applesingle.date-out-of-range` (Warning) and dropped. An entry shorter than 8 bytes is
`applesingle.dates-truncated` (Warning).

**Finder Info (9).** 32 bytes: `FInfo` then `FXInfo` (section 2.1). [Doc] Some writers store only the 16 bytes of
`FInfo`; ClassicMac pads a short entry with zeros [Fitted] and ignores bytes after the first 32.

**Macintosh File Info (10), version 2** [Doc]: a `u32` of attributes, bit 0 locked and bit 1 protected. Not read by
ClassicMac.

**ProDOS File Info (11), version 2** [Doc]: access (`u16`), file type (`u16`), auxiliary type (`u32`).

**MS-DOS File Info (12), version 2** [Doc]: the MS-DOS attributes (`u16`).

**AFP Short Name (13), AFP File Info (14), AFP Directory ID (15), version 2** [Doc]: the file's AFP short name, AFP
attributes and the ID of its parent directory on an AFP server. Not read by ClassicMac.

### 5.5 Versions 1 and 2

| | Version 1 | Version 2 |
| --- | --- | --- |
| Bytes 8–23 | home file system name, space-padded [Doc] | zero [Doc] |
| Dates | entry 7, whose layout depends on the home file system; for `Macintosh`, local Mac dates since 1904 [Doc] | entry 8, UTC seconds since 2000, signed [Doc] |
| System-specific info | inside entry 7 [Doc] | entries 10–15 [Doc] |

ClassicMac reads the home file system name by trimming trailing spaces and NUL bytes from bytes 8–23, and uses it
only in version 1.

### 5.6 Reading

An AppleSingle file gives one file with both forks, name, Finder info and dates. An AppleDouble header file read on its
own gives the same with an empty data fork; joining it with its data file is the job of the host-folder layer
([HOST-FOLDERS.md](HOST-FOLDERS.md)), which pairs `._name` and `name.rsrc` header files with `name`.

---

## 6. Writing containers

### 6.1 AppleDouble

ClassicMac writes AppleDouble version 2 header files (`AppleDoubleWriter`), which `unpack` puts beside each data file
as `._name` ([HOST-FOLDERS.md](HOST-FOLDERS.md)). The layout follows the version 2 Developer Note [Doc]; the choice of
entries and their order is ClassicMac's.

The file:

| Offset | Size | Contents |
| --- | --- | --- |
| +$00 | 4 | `$00051607` |
| +$04 | 4 | `$00020000` |
| +$08 | 16 | zero |
| +$18 | 2 | 4 (entries) |
| +$1A | 12 | entry 3 (Real Name): offset 74, length *n* |
| +$26 | 12 | entry 8 (File Dates Info): offset 74+*n*, length 16 |
| +$32 | 12 | entry 9 (Finder Info): offset 90+*n*, length 32 |
| +$3E | 12 | entry 2 (Resource Fork): offset 122+*n*, length *r* |
| +$4A (74) | *n* | the name's bytes (Mac OS Roman), no length byte |
| 74+*n* | 16 | creation, modification, backup, access dates (`i32` each) |
| 90+*n* | 32 | `FInfo` and `FXInfo` |
| 122+*n* | *r* | the resource fork |

- There is no padding or alignment, and no Data Fork entry.
- The Resource Fork entry is always written, with length 0 for an empty fork. It comes last so the fork can be
  streamed straight from its source.
- **Dates**: creation and modification are converted from the Mac's local time to UTC in the writer's time zone
  (`HostWriteOptions.TimeZone`, default the machine's zone), then to seconds since 2000 UTC, clamped to
  `-$7FFFFFFF`…`$7FFFFFFF` so no real date becomes the "unknown" value. A missing date, and the backup and access
  dates, are written as `$80000000`. A local time that does not exist (skipped by a daylight-saving change) is
  converted with the UTC offset in force one hour earlier; an ambiguous one (repeated when clocks go back) is taken
  as standard time.
- **Finder info**: `FInfo` from the file's type, creator, flags, location and folder; `FXInfo` as held (zero when the
  source container had none).
- A resource fork too large for its end offset to fit in a `u32` is refused (an argument error, nothing is written).
- The comment, the locked and protected attributes and `FXInfo` fields ClassicMac does not hold are not written.

Reading the result with the reader of section 5 in the same time zone gives back the name, Finder info, dates and
resource fork unchanged.

Example: a file named `Read Me` (7 bytes) with a 300-byte resource fork gives entries at 74 (7 bytes), 81 (16),
97 (32) and 129 (300): a 429-byte header file.

### 6.2 AppleSingle

`AppleDoubleWriter.WriteAppleSingle` writes version 2 AppleSingle files [ClassicMac]: the AppleDouble layout of 6.1
with the magic number `$00051600`, five entries, and a Data Fork entry (1) before the Resource Fork entry; both forks
follow the header, the data fork first. Dates, Finder info and name as in 6.1.

### 6.3 MacBinary III

`MacBinaryWriter` writes MacBinary III [Author], with ClassicMac's choices marked:

- The header of section 3.2: name (cut to 63 bytes; an empty name is written as `?` [ClassicMac]), type, creator,
  Finder flags (high byte at 73, low byte at 101), location, folder, both fork lengths, creation and modification dates
  (0 when unknown), `'mBIN'`, `fdScript` and `fdXFlags` from `FXInfo` +8 and +9, writer version 130, reader version 129,
  and the CRC-16 of bytes 0–123. Everything else is zero: no protected flag, secondary header, comment or unpacked
  length.
- The data fork, then the resource fork, each padded with zeros to a multiple of 128, the last one too.
- A fork over `$7FFFFF` bytes is refused (an argument error, nothing written) [ClassicMac, as the reader's limit].

### 6.4 BinHex 4.0

`BinHexWriter` writes BinHex 4.0 [Author]:

- The binary stream of section 4.4: name (cut to 63 bytes; `?` when empty [ClassicMac]), version 0, type, creator,
  Finder flags, fork lengths and the header CRC; the data fork and its CRC; the resource fork and its CRC.
- Run-length encoded (section 4.3): a `$90` byte as `$90 $00`, and a byte that appears 3 to 255 times in a row as the
  byte, `$90` and the count [ClassicMac: any run length decodes the same].
- Six bits per character (section 4.2), the last character zero-filled.
- The text: `(This file must be converted with BinHex 4.0)`, CR, then `:`, the characters in lines of 64 (the first
  line counting the colon), and `:` and CR at the end [ClassicMac: CR line ends, as on the Mac].

Reading each file with the matching reader gives back its name, Finder info, dates (MacBinary, AppleSingle) and forks.

### 6.5 Saving edited resources back

Edited resources (`ClassicMac.Files.Editing.ForkSaver`, the app's Save) go back into the container they were read from.
Only the resource fork changes; the name, Finder info, dates and data fork are written back as read [ClassicMac].

| The file opened | What Save writes |
| --- | --- |
| A resource fork on its own (a `.rsrc` file, or a data file holding resources) | the fork, in place |
| A data file with an AppleDouble header (`._name`, or `name.rsrc` holding AppleDouble) | the header file (section 6.1); the data file is untouched |
| A Basilisk II / SheepShaver folder entry | `.rsrc/name` (made when there was none); the data file and `.finf/name` are untouched |
| A MacBinary I, II or III file | the whole file, as MacBinary III (section 6.3) |
| A BinHex 4.0 file | the whole file (section 6.4) |
| An AppleSingle file | the whole file (section 6.2) |

- A file inside a disk image or archive, a PC Exchange folder or a macOS named fork is saved only with Save As, to any
  of: MacBinary III, BinHex 4.0, AppleSingle, an AppleDouble pair, a Basilisk II folder entry, or the resource fork alone.
- **Verified:** the new file is written beside the original under a temporary name and read back with the reader for
  its container; its resources (type, ID, name, attributes but the in-memory changed bit, data), the fork's attributes,
  and the name, Finder info and other fork where the container has them must equal what was meant. Only then does it
  replace the original; otherwise the original is left as it was and the save reports what differed.
- **Backup:** the first save keeps the original as `<file>.orig` (beside the file written, so `._name.orig` for an
  AppleDouble header) unless one exists; later saves keep that first original.
- **Changed on disk:** a file whose size or modification time differs from when it was read is not overwritten
  without asking.
- The fork itself is written in the canonical layout of [RESOURCE-FORK.md](RESOURCE-FORK.md) (the writer's compact
  order), so an unchanged resource keeps its content but not necessarily its offset.

**Editing rules** (`ClassicMac.Resources.Editing`) [ClassicMac, as ResEdit enforces them; the Resource Manager's
AddResource checks less]: a type and ID already in the file is refused; IDs below 128 are allowed after a warning
(reserved for the system); the compressed attribute cannot be set by hand, and new data is stored uncompressed;
Duplicate gives the next free ID from 128 up, with the same name and attributes. Every edit can be undone, also after a
save.

---

## 7. Unwrapping nested containers

`ContainerUnwrapper` turns an input into a tree: each node is a file and the name of the format it was found in, and
its children are the files its data fork holds when that data fork is itself a container. The leaves are the files
that are not containers. For example, an AppleSingle file whose data fork is a BinHex file whose data is a MacBinary II
file unwraps to four levels: the host file, `AppleSingle`, `BinHex 4.0`, `MacBinary II`.

### 7.1 Readers and order

For each file, the unwrapper asks every reader in turn whether the file is in its format, and uses the first that says
yes. The order:

1. readers the application registers, first so they can override the built-in ones;
2. AppleSingle, AppleDouble;
3. MacBinary III, MacBinary II, MacBinary I;
4. BinHex 4.0, uuencode;
5. archives: DiskDoubler, PackIt, StuffIt split files and archives, Compact Pro, LHA, zip, gzip, tar (see
   [ARCHIVES.md](ARCHIVES.md));
6. UDIF, Apple partition maps, Disk Copy 4.2, NDIF, DART, HFS, MFS, DOS partition tables (MBR), FAT, raw CD images, cue
   sheets, ISO 9660 / High Sierra (see [DISK-IMAGES.md](DISK-IMAGES.md), [HFS-MFS.md](HFS-MFS.md),
   [FAT.md](FAT.md), [ISO9660.md](ISO9660.md)).

The single-file containers come first because their tests are cheap and read only the start of the data fork, and
because AppleSingle and MacBinary II/III have strong signatures. MacBinary I, the weakest test, comes after the
stronger MacBinary versions. Most tests look at the data fork only; a reader may also look at the resource fork or the
Finder info (NDIF images need both).

- A file with an empty data fork is a leaf; no reader is asked.
- If the chosen reader then fails (the input turns out to be unusable), the file becomes a leaf and the failure is
  reported as `container.unreadable` (Error). The remaining readers are not tried.
- Readers report damage as diagnostics and keep going; only an input they cannot read at all fails.

### 7.2 Nesting depth

The file the unwrapper starts with is at depth 0, the files a container holds are one deeper than the container. A
container found at depth `MaxNestingDepth` or deeper is not opened: it becomes a leaf with `container.too-deep`
(Warning). So at most `MaxNestingDepth` containers are opened along any path; the default is 8. A BinHex file holding
a disk image needs a depth of at least 2 for the disk's files to appear.

### 7.3 Expanded-bytes limit

The unwrapper adds up the data and resource fork lengths of every file any container yields during one unwrap, across
the whole tree. When the total passes `MaxExpandedBytesPerInput` (default 1 GiB), the file that passed it and the rest
of that container's files are dropped, with `container.too-large` (Error). The count is not reset, so each container
still to be read reports the same error for its first file. This guards against inputs that expand without bound, such
as nested disk images or a BinHex file of long runs.

The same limit caps what a single reader holds in memory: BinHex reads its whole input and expands it under this limit
(section 4.6).

### 7.4 Names and sibling files

Each reader gets, besides the data:

- **the host name**: the name of the file being read (the host file's name at the top, the container's own name
  inside). AppleSingle and AppleDouble use it when the container has no Real Name entry. MacBinary and BinHex always
  carry their own name.
- **the sibling files**: the other files in the same folder as the input, read on demand, for formats split across
  files (segmented disk images). At the top these are the other files in the host folder; inside a container, the
  other files the same container yielded in the same folder. The single-file containers do not use them.
- **the options** (`ContainerReadOptions`): nesting depth (8), expanded bytes (1 GiB), the time zone for UTC dates
  (the machine's), and settings for volumes that do not concern these containers.

When unwrapping starts from a host path, the host-folder layer first joins the file with its companions (an
AppleDouble `._` file, Basilisk II `.rsrc`/`.finf` folders, PC Exchange records), and the root node's format is the
host layout's name (`AppleDouble pair`, `Basilisk II folder`, …).

---

## 8. uuencode

A uuencoded file (`.uu`, `.uue`) is a file's bytes as 7-bit text, from the Unix `uuencode` utility; Mac files went
through Usenet and mail this way, usually as a MacBinary (`.bin`) or BinHex/StuffIt file inside. The format is
POSIX's: the Single UNIX Specification, `uuencode` (the *Output Files* and *Extended Description* sections), cited as
**[Doc]** in this section. It carries a name and the bytes only: no type, creator, flags, dates or resource fork. [Doc]

### 8.1 Framing

The text is a sequence of lines; ClassicMac takes CR LF, CR and LF alike as line breaks. A block starts with the line
[Doc]:

```
begin <mode> <name>             historical encoding
begin-base64 <mode> <name>      base64 encoding (uuencode -m)
```

`<mode>` is the file's mode in octal, `<name>` the pathname to create; each is preceded by one space. [Doc] Lines
before the `begin` line (mail or news headers, a message) are ignored.

ClassicMac:

- recognises a file by a `begin` or `begin-base64` line in its first 64 KiB [Fitted, as BinHex's marker search, 4.1],
  where the line starts with that word, then one space, 1–6 octal digits, one space and a name that is not blank;
- ignores the mode; keeps the name's last `/`-separated component, cut to 255 bytes, as the file's name, its bytes
  taken as Mac OS Roman; trailing spaces and tabs on the line are not part of it;
- reads every block in the whole input, so a file with several `begin` blocks yields several files, in order; text
  between blocks is ignored.

### 8.2 Historical encoding

Each line after `begin` is [Doc]:

| Part | Meaning |
| --- | --- |
| first character | the number of bytes the line encodes, *n*, as the character `$20` + *n* |
| then | the bytes in groups of 3, each written as 4 characters of 6 bits each, most significant first, each the character `$20` + value; the last group is padded with zero bits |

Encoders write at most 45 bytes (61 characters) a line [Doc]. A value of 0 may be written as a space or as the
backquote `` ` `` ($60) [Doc]; a decoder takes each character as (*c* − `$20`) AND `$3F`, which maps both to 0. The
line whose count is 0 (a lone backquote or space) ends the data, and the next line is `end`. [Doc]

ClassicMac:

- takes a blank line inside a block as the zero-count line: a mailer has stripped its space [Fitted];
- reads a line shorter than its count calls for as if its missing characters were zeros (a mailer strips trailing
  spaces, which stand for zeros) [Fitted], and ignores characters past the count's groups (some encoders add a check
  character) [Fitted];
- skips a line holding a character outside `$20`–`$60` with `uuencode.bad-line` (Error) and reads on;
- after the zero-count line, skips blank lines; if the next line is not `end`, reports `uuencode.missing-end`
  (Warning), keeps the file and looks at that line for the next `begin`.

### 8.3 Base64 encoding

After `begin-base64` the lines are base64 (the alphabet `A`–`Z`, `a`–`z`, `0`–`9`, `+`, `/`, with `=` padding; RFC
2045's), and the line `====` ends the block. [Doc] ClassicMac concatenates the lines' characters, drops the bits after
a `=` that do not make a byte, and skips a line holding any other character with `uuencode.bad-line` (Error).

### 8.4 Reading

ClassicMac reads the whole input into memory (at most `MaxExpandedBytesPerInput`; a larger input is unreadable; the
decoded bytes are always fewer than the text). Each block becomes a file whose data fork is the decoded bytes; it has
no resource fork and zero Finder information. An input that ends inside a block keeps what was decoded, with
`uuencode.truncated` (Error); one that ends after the zero-count line but before `end`, with `uuencode.missing-end`
(Warning). An input with no `begin` line is unreadable.

The decoded file is unwrapped like any other (section 7): a `.bin` that is MacBinary becomes the Mac file it holds, a
`.hqx` is read as BinHex, a `.sit` as StuffIt. ClassicMac does not look at the name's extension for this.

---

## 9. Diagnostics

Codes these readers and the unwrapper emit. Offsets, where given, are in the container's input. None of these formats
is handled by Mac OS (section 5), so the "original tools" column describes the format authors' intent where it is
stated; the behaviour of MacBinary, BinHex and StuffIt programs was not traced.

| Code | Severity | Meaning | ClassicMac | Original tools |
| --- | --- | --- | --- | --- |
| `macbinary.fork-truncated` | Error | a fork's length runs past the end of the input | keeps the bytes present | not traced |
| `binhex.bad-character` | Error | a character outside the alphabet (and not whitespace) inside the data | stops decoding; keeps what was decoded | not traced |
| `binhex.truncated` | Error | the input ends before the closing colon; or inside a `$90` run; or where a CRC should be | keeps what was decoded | not traced |
| `binhex.fork-truncated` | Error | a fork is longer than the decoded data | keeps the decoded part; skips its CRC | not traced |
| `binhex.crc` | Warning | a header or fork CRC does not match | keeps the data | the format's CRCs exist to reject a damaged transfer [Author] |
| `binhex.version` | Info | the header's version byte is not 0 | reads on | not traced |
| `uuencode.bad-line` | Error | a line inside a block holds a character outside the encoding's range | skips the line | not traced |
| `uuencode.truncated` | Error | the input ends inside a block, before its zero-count or `====` line | keeps what was decoded | not traced |
| `uuencode.missing-end` | Warning | the line after the zero-count line is not `end` | keeps the file | POSIX requires the `end` line [Doc] |
| `applesingle.entries-truncated` | Error | the file ends inside the entry table | reads the complete descriptors | Mac OS: not read [Code] |
| `applesingle.entry-out-of-range` | Error | an entry's offset plus length runs past the end of the file | skips the entry | Mac OS: not read [Code] |
| `applesingle.double-data-fork` | Warning | an AppleDouble header file has a Data Fork entry | uses it | the Developer Note puts the data fork in the data file [Doc] |
| `applesingle.name-too-long` | Warning | the Real Name is longer than 255 bytes | cuts it to 255 | Mac OS: not read [Code] |
| `applesingle.dates-truncated` | Warning | a File Dates Info or version 1 File Info entry is shorter than 8 bytes | ignores its dates | Mac OS: not read [Code] |
| `applesingle.date-out-of-range` | Warning | a version 2 date falls outside 1904–2040 in the reading zone | drops that date | Mac OS: not read [Code] |
| `applesingle.unknown-entry` | Info | an entry ID other than 1–15 | skips it | readers skip unknown entries [Doc] |
| `applesingle.no-name` | Info | no Real Name entry and no host name | leaves the name empty | Mac OS: not read [Code] |
| `container.unreadable` | Error | a reader matched the file but could not read it | the file becomes a leaf | — |
| `container.too-deep` | Warning | a container nested past `MaxNestingDepth` | the file becomes a leaf | — |
| `container.too-large` | Error | unwrapping produced more than `MaxExpandedBytesPerInput` bytes | drops the rest of that container | — |

Inputs a reader cannot read at all raise `container.unreadable` in the unwrapper. For these containers they are: a
MacBinary header that does not pass its version's test; no BinHex marker and data; a BinHex input or expansion larger
than `MaxExpandedBytesPerInput`; a BinHex header with no valid name or too short to hold its fields; an AppleSingle or
AppleDouble header with the wrong magic number or version; a uuencode input with no `begin` line or larger than
`MaxExpandedBytesPerInput`.

A MacBinary II or III file with a damaged header CRC, and a MacBinary I file whose length does not match its header,
produce no diagnostic: they are not recognised, and stay plain files (section 3.3).

---

## 10. Not covered

- Writing AppleDouble or AppleSingle version 1, MacBinary I or II, and MacBinary's secondary header and comment.
- The MacBinary Get Info comment, the protected flag and the unpacked-length field (bytes 99, 81, 116): not read.
- The AppleSingle Comment, icon, Macintosh, ProDOS, MS-DOS and AFP entries: not read. Version 1 File Info for home
  file systems other than `Macintosh`: not read.
- AppleSingle and AppleDouble files describing folders (Finder Info holding `DInfo`/`DXInfo`).
- BinHex 1.0, 2.0 and 3.0 (`.hex`, `.hcx`): not recognised. BinHex 4.0 files split into several parts (as Usenet
  posted them) must be joined first.
- uuencode files split into several parts (`part 1/3` posts) must be joined first; a part without its own `begin` line
  is not recognised. xxencode, yEnc and the `begin` line's mode (permissions) are not read.
- MIME encapsulation (`application/applefile`, `multipart/appledouble`, `application/mac-binhex40`): the reader
  expects the container's bytes, not a mail message's MIME parts (a BinHex file in a mail body is found by its marker
  line).
