# Rez source (.r)

Rez is MPW's resource compiler: it reads source of `data` statements (and typed `resource` statements over `type`
templates) and writes a resource fork; DeRez writes a fork back as such source. Retro68 ships a Rez of its own that
reads most of the same source. ClassicMac writes a resource fork as MPW DeRez's output, byte for byte, or in the subset
both compilers read alike, and compiles `data`, `read` and `include` statements as MPW's Rez does.

| | |
| --- | --- |
| Identified by | Text files, extension `.r`; MPW's are Mac OS Roman with CR line ends, type `'TEXT'`, creator `'MPS '` |
| ClassicMac | Reads (`data`, `read`, `include`) and writes; `ClassicMac.Resources.Rez` (`RezCompiler`, `RezWriter`), the CLI's `rez` and `derez` |
| Verified against | MPW 3.6 (MPW-GM) Rez and DeRez on Mac OS 9.0 in SheepShaver: 44 source files compiled and decompiled, and DeRez's output compared byte for byte (§7) |
| Sources | Apple, *Building and Managing Programs in MPW* (Rez and DeRez); MPW 3.6's Rez and DeRez, run; Retro68's Rez (GPL), behaviour only |

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

A `data` statement is one resource [Doc]:

```
data 'TYPE' (id[, "name"][, attributes]) {
	$"0123 4567 …"
};
```

- **Type:** a four-character literal. Escapes work inside it, and a three-character literal is right-justified after a
  zero byte (`'A\'B'` is `00 41 27 42`) [Verified: MPW 3.6 Rez].
- **Id:** an integer, `$` hex, `0x` hex, `0b` binary or leading-`0` octal [Verified: MPW 3.6 Rez].
- **Name:** a string. `""` gives no name at all, so a data statement cannot make an empty name that is present
  [Verified: MPW 3.6 Rez].
- **Attributes:** keywords `sysheap`/`appheap`, `purgeable`/`nonpurgeable`, `locked`/`unlocked`,
  `protected`/`unprotected`, `preload`/`nonpreload` (case-insensitive), or a number. MPW warns on bits $80 and $01 and
  refuses bit $02 (changed) and the keywords `changed` and `unchanged` [Verified: MPW 3.6 Rez].
- **Data:** hex strings `$"…"` (white space between digits ignored) and strings.
- **String escapes** in MPW: `\n` $0D, `\r` $0A, `\t` $09, `\b` $08, `\v` $0B, `\f` $0C, `\?` $7F, `\'`, `\"`, `\\`,
  `\ooo` octal, `\0xHH` (exactly two digits), `\$HH`, `\0dDDD` decimal, `\0bBBBBBBBB` binary; any other `\c` is `c`
  [Verified: MPW 3.6 Rez].

## 2. Reading

MPW's Rez compiles [Verified: MPW 3.6 Rez]:

1. **Lexing:** keywords case-insensitive; `/* */` and `//` comments; the integers, strings, escapes and hex strings of
   §1; adjacent strings and hex strings in a body join.
2. **`data` 'TYPE' (id[, name][, attributes]) { values };** a resource of the values joined. A hex string in place of
   the type makes **no resource and no error**. Attribute keywords set or clear their bit; a set and a clear of the
   same bit is an error ("Too many or conflicting resource attributes"); `changed` and `unchanged` are errors; a number
   sets the byte, bit $02 is an error ("Invalid resource attributes (126)") and bits $80 and $01 a warning ("Illegal or
   reserved resource attributes set").
3. **`read` 'TYPE' (…) "file";** a resource of the file's data fork; **`$$Read("file")`** the same bytes as a value.
4. **`include` "file" ['TYPE' [(id)]];** the file's resources (all, of the type, or the one), copied as they are:
   names (an empty one too), every attribute bit (the changed bit too) and any type.
5. **`$$Format("format", values…)`** a string; `$$CountOf` and the typed statements (`type`, `resource`) work over
   templates.
6. Any error: the fork is not written.

## 3. Writing

DeRez writes each resource of the fork, in the fork's order, as [Verified: MPW 3.6 DeRez]:

1. **The header:** `data '` type `' (` id, then `, "` name `"` when the resource has a name (an empty one too: `""`),
   then the attributes when any are set, then `) {` and CR.
   - Attributes: keywords, highest bit first (`sysheap, purgeable, locked, protected, preload`), when every set bit is
     one of $40, $20, $10, $08, $04; otherwise the whole byte as `$XX` in upper-case hex (`$C1`, `$7E`).
   - Name: `\"`, `\\`, `\n` for $0D, `\0xHH` (upper-case hex) for other control bytes and $7F; Mac OS Roman above
     $7F as is.
   - Type: `\'`, `\\`, `\0xHH` for control bytes, Mac OS Roman above $7F as is; a leading zero byte is left out.
2. **The data**, 16 bytes a line: a TAB, `$"`, the bytes in upper-case hex in groups of two separated by a space,
   `"`, then spaces up to character 55 (the TAB counting as one), then `/* ` and the bytes as text and ` */`, then CR.
   - The text: printable ASCII as is; $09 as `∆` ($C6) and $0D as `¬` ($C2); other control bytes and $7F as `.`;
     Mac OS Roman above $7F as is; a `/` right after a `*` as `.`, so the comment cannot end early.
   - No data: the header is followed directly by the end.
3. **The end:** `};`, CR, and a blank line (CR).

With `-e`, the names' and types' control bytes are written as they are; the quote, the backslash and (in names) $0D
are still escaped [Verified: MPW 3.6 DeRez]. `-m` had no effect on `data` output.

## 4. Variants

### 4.1 Retro68's Rez

Retro68's Rez reads the same `data` statements with these differences [Reference: Retro68]:

- `\n` is $0A and `\r` $0D, the other way round from MPW; `\\` does not work.
- Keywords are lower-case only; there are no numeric attributes, but there is the keyword `changed`.
- Character literals (types) take no escapes.

So MPW DeRez's output is not safe Retro68 input: names with `\n` or `\\`, `$XX` attributes and escaped types read
differently or fail [Verified: MPW 3.6 DeRez] [Reference: Retro68].

## 5. ClassicMac

- **The MPW dialect** (`RezDialect.Mpw`, the default) writes §3 byte for byte: Mac OS Roman, CR line ends;
  `RezOptions.RawNames` is `-e`. [ClassicMac]
- **The portable dialect** (`RezDialect.Portable`) writes what both compilers read alike: ASCII only, LF line ends;
  names with `\0xHH` for every byte outside $20–$7E and for `"` and `\`; keyword attributes only; the comment text with
  every byte outside $20–$7E as `.`. What it cannot hold is reported and written as near as it goes: an empty name is
  left out (`rez.empty-name`), attribute bits $80, $02 and $01 are dropped (`rez.attributes`), and a type that is not
  four printable characters (or holds `'` or `\`) is written in MPW's form (`rez.type`). [ClassicMac]
- `classicmac derez <path> [-o file] [--portable] [-e]` writes a file's resource fork
  ([cli.md §2.8](../../cli.md#28-derez)). [ClassicMac]
- **Compiling** (`RezCompiler.Compile`, `classicmac rez`, [cli.md §2.9](../../cli.md#29-rez)) reads §2's `data`,
  `read` and `include` statements and `$$Read` and `$$Format` (`%d %i %x %X %c %s %%`, an `l` ignored), with MPW's
  escapes or, with `RezCompileOptions.Retro68Escapes`, Retro68's. The files `read` and `include` name are found beside
  the source. A resource defined twice is an error (`rez.duplicate`). Preprocessor lines (`#include "Types.r"` …) are
  skipped with a warning, and typed statements (`type`, `resource`, `change`, `delete`, `enum`, `symbol`) are an error
  (`rez.typed`). The fork is written by ClassicMac's fork writer, which clears the changed bit as the Resource Manager
  does, where MPW's Rez keeps an included resource's. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `rez.attributes` | Warning | The portable dialect meets attribute bits $80, $02 or $01 | Leaves them out | MPW Rez warns on $80 and $01 and refuses $02; Retro68 has no numeric attributes [Verified: MPW 3.6 Rez] |
| `rez.conflicting-attributes` | Error | A keyword sets and another clears the same attribute | Writes no fork | The same error [Verified: MPW 3.6 Rez] |
| `rez.duplicate` | Error | Two resources of one type and ID | Writes no fork | Not tried |
| `rez.empty-name` | Warning | The portable dialect meets an empty name | Leaves the name out | `""` makes no name in MPW Rez [Verified: MPW 3.6 Rez] |
| `rez.file` | Error | A file `read`, `$$Read` or `include` names cannot be read | Writes no fork | An error [Verified: MPW 3.6 Rez] |
| `rez.hex-type` | Info | A hex string in place of a type | Makes no resource | The same, silently [Verified: MPW 3.6 Rez] |
| `rez.invalid-attributes` | Error | A numeric attribute sets bit $02 | Writes no fork | "Invalid resource attributes" [Verified: MPW 3.6 Rez] |
| `rez.preprocessor` | Warning | A `#` line | Skips it | Preprocesses it |
| `rez.reserved-attributes` | Warning | A numeric attribute sets bit $80 or $01 | Sets them | The same warning [Verified: MPW 3.6 Rez] |
| `rez.syntax` | Error | Source Rez does not read: a bad escape, `changed`, an odd hex string, an unknown statement | Writes no fork | An error [Verified: MPW 3.6 Rez] |
| `rez.type` | Warning | The portable dialect meets a type that is not four printable characters, or holds `'` or `\` | Writes MPW's form | Retro68's character literals take no escapes [Reference: Retro68] |
| `rez.typed` | Error | A typed statement (`type`, `resource` …) | Writes no fork | Compiles it through its template |

## 7. Verification

- `tests/ClassicMac.Resources.Tests/RezWriterTests.cs`: data lines and the comment column, names, attributes and types
  as DeRez writes them, `-e`, the portable dialect and its diagnostics.
- `tests/ClassicMac.Cli.Tests/PathCommandTests.cs`: `Derez_writes_a_resource_fork_as_Rez_source`, and
  `Derez_matches_MPW_DeRez`, which with `CLASSICMAC_MPW_DISK` set to a disk of MPW DeRez's outputs (made with Apple's
  tools, not committed) compares every one byte for byte: 43 files, among them a resource with every byte value,
  names with quotes, backslashes, CR, Mac OS Roman and control bytes, attributes $C1, $7E and $80, an empty name,
  three-character and control-byte types, and `-e`.
- `tests/ClassicMac.Resources.Tests/RezCompilerTests.cs`: every escape and integer form, Retro68's escapes, types,
  names and attributes, the errors, the hex-string type, `read`, `$$Read` and `include`, and what `RezWriter` writes
  (both dialects) compiling back to the same fork.
- `tests/ClassicMac.Cli.Tests/PathCommandTests.cs`: `Rez_compiles_source_into_a_resource_fork`, and
  `Rez_compiles_what_MPWs_Rez_compiles`, which with `CLASSICMAC_MPW_DISK` compiles each test source on that disk and
  compares the resources with MPW's Rez output (the changed bit aside), and refuses what MPW refused.

## 8. Not covered

- Typed statements (`type`, `resource` and their templates), the preprocessor, `$$CountOf` and the other functions,
  and DeRez's `-only`, `-skip` and include-file options.
- Whether MPW Rez reads the portable dialect's LF line ends as it reads CR (not yet compiled there).

## 9. References

1. Apple Computer, *Building and Managing Programs in MPW*, second edition (1993): Rez and DeRez.
2. MPW 3.6 (MPW-GM), Apple Computer: Rez 3.6 and DeRez 3.6, run on Mac OS 9.0 in SheepShaver. Not committed.
3. Retro68, <https://github.com/autc04/Retro68>, its Rez: GPL; behaviour only, nothing copied.
