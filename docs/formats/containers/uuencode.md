# uuencode

A uuencoded file (`.uu`, `.uue`) is a file's bytes as 7-bit text, from the Unix `uuencode` utility. Mac files went
through Usenet and mail this way, usually as a MacBinary (`.bin`) or BinHex/StuffIt file inside. The format is
POSIX's. It carries a name and the bytes only: no type, creator, flags, dates or resource fork. [Doc] ClassicMac reads
it, in both the historical and the base64 encoding, and unwraps what it holds.

| | |
| --- | --- |
| Identified by | Extensions `.uu`, `.uue`; a line `begin <mode> <name>` or `begin-base64 <mode> <name>` |
| ClassicMac | Reads; `ClassicMac.Files.Containers` (`UuencodeReader`) |
| Verified against | Nothing yet |
| Sources | The Single UNIX Specification, `uuencode` (the *Output Files* and *Extended Description* sections); RFC 2045 (base64) |

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

[Doc] in this file is the Single UNIX Specification's `uuencode`.

### 1.1 Framing

The text is a sequence of lines. A block starts with the line [Doc]:

```
begin <mode> <name>             historical encoding
begin-base64 <mode> <name>      base64 encoding (uuencode -m)
```

`<mode>` is the file's mode in octal, `<name>` the pathname to create; each is preceded by one space. [Doc] Lines
before the `begin` line (mail or news headers, a message) are ignored.

### 1.2 Historical encoding

Each line after `begin` is [Doc]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +0 | 1 | count | the number of bytes the line encodes, *n*, as the character `$20` + *n* [Doc] |
| +1 | 4 per 3 bytes | data | the bytes in groups of 3, each written as 4 characters of 6 bits each, most significant first, each the character `$20` + value; the last group is padded with zero bits [Doc] |

Encoders write at most 45 bytes (61 characters) a line. A value of 0 may be written as a space or as the backquote
`` ` `` ($60); a decoder takes each character as (*c* − `$20`) AND `$3F`, which maps both to 0. The line whose count
is 0 (a lone backquote or space) ends the data, and the next line is `end`. [Doc]

### 1.3 Base64 encoding

After `begin-base64` the lines are base64 (the alphabet `A`–`Z`, `a`–`z`, `0`–`9`, `+`, `/`, with `=` padding;
RFC 2045's), and the line `====` ends the block. [Doc]

## 2. Reading

1. Split the text into lines; CR LF, CR and LF alike end a line.
2. Outside a block, a line opens one when it starts with `begin` or `begin-base64`, then one space, 1–6 octal digits,
   one space and a name that is not blank (trailing spaces and tabs are not part of it). Every other line is ignored.
3. Inside a historical block, per line:
   1. A blank line is the zero-count line: a mailer has stripped its space. [Fitted]
   2. A line holding a character outside `$20`–`$60` is skipped with `uuencode.bad-line` (Error); reading goes on.
   3. A count of 0 ends the data.
   4. Otherwise decode the count's groups. A line shorter than its count calls for is read as if its missing
      characters were zeros (a mailer strips trailing spaces, which stand for zeros) [Fitted]; characters past the
      count's groups are ignored (some encoders add a check character) [Fitted].
4. After the zero-count line, blank lines are skipped. If the next line is not `end`, the block's file is kept with
   `uuencode.missing-end` (Warning), and that line is looked at for the next `begin`.
5. Inside a base64 block, per line (trailing spaces and tabs removed): `====` ends the block. A line holding a
   character outside the alphabet and `=` is skipped with `uuencode.bad-line` (Error). Otherwise the characters are
   concatenated across lines; at a `=`, the bits that do not make a byte are dropped.
6. Each block becomes a file whose data fork is the decoded bytes, named by the `begin` line; it has no resource fork
   and zero Finder information. Every block in the whole input is read, so a file with several `begin` blocks yields
   several files, in order; text between blocks is ignored.
7. An input that ends inside a block keeps what was decoded, with `uuencode.truncated` (Error); one that ends after
   the zero-count line but before `end`, with `uuencode.missing-end` (Warning).

An input with no `begin` line, or larger than the expanded-bytes limit, is unusable (`container.unreadable`,
[unwrapping.md §2.2](unwrapping.md#22-unwrapping-a-file)).

The decoded file is unwrapped like any other ([unwrapping.md §2](unwrapping.md#2-reading)): a `.bin` that is MacBinary
becomes the Mac file it holds, a `.hqx` is read as BinHex, a `.sit` as StuffIt. The name's extension plays no part.

## 3. Writing

None.

## 4. Variants

The historical encoding (§1.2) and the base64 encoding of `uuencode -m` (§1.3). [Doc]

## 5. ClassicMac

- A file is recognised by a `begin` or `begin-base64` line (§2 step 2) in its first 64 KiB, the same window as
  BinHex's marker search ([binhex.md §5](binhex.md#5-classicmac)). [Fitted] Once recognised, the whole input is
  searched for blocks.
- The mode is ignored. The name is the `begin` line's path with only its last `/`-separated component kept, cut to
  255 bytes, its bytes taken as Mac OS Roman. [ClassicMac]
- The reader reads the whole input into memory, at most the expanded-bytes limit
  (`ContainerReadOptions.MaxExpandedBytesPerInput`, [unwrapping.md §5](unwrapping.md#5-classicmac)); the decoded
  bytes are always fewer than the text. [ClassicMac]
- Format name: `uuencode`.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `uuencode.bad-line` | Error | a line inside a block holds a character outside the encoding's range | skips the line | no Apple decoder; POSIX defines the character range [Doc] |
| `uuencode.missing-end` | Warning | the line after the zero-count line is not `end`, or the input ends before it | keeps the file | no Apple decoder; POSIX requires the `end` line [Doc] |
| `uuencode.truncated` | Error | the input ends inside a block, before its zero-count or `====` line | keeps what was decoded | no Apple decoder |

## 7. Verification

No file made by a `uuencode` program is in the tests; the inputs are text built in the tests.

- `tests/ClassicMac.Files.Tests/UuencodeTests.cs`: `Decodes_a_block_with_any_line_break` (CR LF, CR and LF; space or
  backquote for 0), `Known_vector`, `Stripped_trailing_spaces_decode_as_zeros` (§2 step 3.4),
  `Several_blocks_are_several_files`, `Base64_blocks_decode`, `A_bad_line_is_skipped`,
  `A_missing_end_line_is_a_warning`, `Truncated_text_keeps_what_was_decoded`, `Text_without_a_begin_line_is_not_uuencode`,
  `A_uuencoded_MacBinary_file_unwraps_to_the_Mac_file`.

## 8. Not covered

- uuencode files split into several parts (`part 1/3` posts) must be joined first; a part without its own `begin`
  line is not recognised.
- xxencode and yEnc.
- The `begin` line's mode (permissions): not read.

## 9. References

1. The Open Group, *The Single UNIX Specification* (POSIX.1-2017), `uuencode`.
2. RFC 2045, MIME Part One (the base64 alphabet).
