# uuencode

A uuencoded file (`.uu`, `.uue`) is a file's bytes as 7-bit text, from the Unix `uuencode` utility; Mac files went
through Usenet and mail this way, usually as a MacBinary (`.bin`) or BinHex/StuffIt file inside. The format is
POSIX's: the Single UNIX Specification, `uuencode` (the *Output Files* and *Extended Description* sections), cited as
**[Doc]** in this section. It carries a name and the bytes only: no type, creator, flags, dates or resource fork. [Doc]

Contents

1. [Framing](#1-framing)
2. [Historical encoding](#2-historical-encoding)
3. [Base64 encoding](#3-base64-encoding)
4. [Reading](#4-reading)
5. [Diagnostics](#5-diagnostics)
6. [Not covered](#6-not-covered)

---

## 1. Framing

The text is a sequence of lines; ClassicMac takes CR LF, CR and LF alike as line breaks. A block starts with the line
[Doc]:

```
begin <mode> <name>             historical encoding
begin-base64 <mode> <name>      base64 encoding (uuencode -m)
```

`<mode>` is the file's mode in octal, `<name>` the pathname to create; each is preceded by one space. [Doc] Lines
before the `begin` line (mail or news headers, a message) are ignored.

ClassicMac:

- recognises a file by a `begin` or `begin-base64` line in its first 64 KiB [Fitted, as BinHex's marker search: [binhex.md §1](binhex.md#1-text-framing)],
  where the line starts with that word, then one space, 1–6 octal digits, one space and a name that is not blank;
- ignores the mode; keeps the name's last `/`-separated component, cut to 255 bytes, as the file's name, its bytes
  taken as Mac OS Roman; trailing spaces and tabs on the line are not part of it;
- reads every block in the whole input, so a file with several `begin` blocks yields several files, in order; text
  between blocks is ignored.

---

## 2. Historical encoding

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

---

## 3. Base64 encoding

After `begin-base64` the lines are base64 (the alphabet `A`–`Z`, `a`–`z`, `0`–`9`, `+`, `/`, with `=` padding; RFC
2045's), and the line `====` ends the block. [Doc] ClassicMac concatenates the lines' characters, drops the bits after
a `=` that do not make a byte, and skips a line holding any other character with `uuencode.bad-line` (Error).

---

## 4. Reading

ClassicMac reads the whole input into memory (at most `MaxExpandedBytesPerInput`; a larger input is unreadable; the
decoded bytes are always fewer than the text). Each block becomes a file whose data fork is the decoded bytes; it has
no resource fork and zero Finder information. An input that ends inside a block keeps what was decoded, with
`uuencode.truncated` (Error); one that ends after the zero-count line but before `end`, with `uuencode.missing-end`
(Warning). An input with no `begin` line is unreadable.

The decoded file is unwrapped like any other ([unwrapping.md §3](unwrapping.md#3-unwrapping-nested-containers)): a `.bin` that is MacBinary becomes the Mac file it holds, a
`.hqx` is read as BinHex, a `.sit` as StuffIt. ClassicMac does not look at the name's extension for this.

---

## 5. Diagnostics

Codes these readers and the unwrapper emit. Offsets, where given, are in the container's input. None of these formats
is handled by Mac OS ([applesingle-appledouble.md](applesingle-appledouble.md)), so the "original tools" column describes the format authors' intent where it is
stated; the behaviour of MacBinary, BinHex and StuffIt programs was not traced.

| Code | Severity | Meaning | ClassicMac | Original tools |
| --- | --- | --- | --- | --- |
| `uuencode.bad-line` | Error | a line inside a block holds a character outside the encoding's range | skips the line | not traced |
| `uuencode.truncated` | Error | the input ends inside a block, before its zero-count or `====` line | keeps what was decoded | not traced |
| `uuencode.missing-end` | Warning | the line after the zero-count line is not `end` | keeps the file | POSIX requires the `end` line [Doc] |

---

## 6. Not covered

- uuencode files split into several parts (`part 1/3` posts) must be joined first; a part without its own `begin` line
  is not recognised. xxencode, yEnc and the `begin` line's mode (permissions) are not read.
