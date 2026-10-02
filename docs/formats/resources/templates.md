# Resource templates (TMPL)

A `TMPL` resource describes the fields of another resource type, so that a generic editor can show and edit any
resource of that type. ResEdit introduced templates, and other editors (Resorcerer, later ResEdit-like tools) extended
them. This document follows ResEdit 2.1.3, whose template editor decides what every field type means. ClassicMac reads
templates, reads and writes resources through them, and the app's Edit tab shows a resource through one when it has
no form of its own.

| | |
| --- | --- |
| Identified by | Resource type `'TMPL'`; the resource's name is the type it describes |
| ClassicMac | Reads templates; reads and writes resources through them; `ClassicMac.Resources.Decoders.Templates.ResourceTemplate` |
| Verified against | ResEdit 2.1.3: its 78 templates and the 471 resources of templated types in its own resource fork |
| Sources | ResEdit 2.1.3's template editor, traced in disassembly |

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

### 1.1 The template

A `TMPL` is a sequence of fields to the end of the resource, with no count and no padding [Code: ResEdit 2.1.3]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 1 + n | Label | Pascal string |
| +$01 + n | 4 | Field type | Four characters, §1.2 |

The resource's name is the type it describes; its ID does not matter. A label of `*****` is only a convention for list
lines. [Code: ResEdit 2.1.3]

### 1.2 Field types

Every field of the described resource starts right after the previous one: there is no implicit alignment, and words
and longs may sit at odd offsets [Code: ResEdit 2.1.3].

| Type | Bytes | Meaning |
| --- | --- | --- |
| `DBYT`, `DWRD`, `DLNG` | 1, 2, 4 | Signed decimal number |
| `HBYT`, `HWRD`, `HLNG` | 1, 2, 4 | Hex number, shown as `$` and 2, 4 or 8 digits |
| `CHAR` | 1 | One character; a 0 byte is empty |
| `TNAM` | 4 | A four-character type; shorter text is padded with spaces |
| `BOOL` | 2 | True when the first byte is not 0 (the second is ignored); written `01 00` or `00 00` |
| `BBIT` | 1 bit | One bit, bit 7 first; they come in runs of 8, one byte a run |
| `RECT` | 8 | Four signed words: top, left, bottom, right |
| `PSTR` | 1 + n | Pascal string |
| `ESTR`, `OSTR` | 1 + n (+ 1) | Pascal string, then a 0 byte when needed to make the string's total length (length byte and text) even or odd |
| `WSTR`, `LSTR` | 2 + n, 4 + n | String after a 16- or 32-bit length |
| `CSTR` | n + 1 | C string: the text and a NUL |
| `ECST`, `OCST` | n + 1 (+ 1) | C string, then a 0 byte when needed to make its total length (with the NUL) even or odd |
| `HEXD` | The rest | Hex dump of every byte left; must be the template's last field |
| `Hnnn` | $nnn | Hex dump of a fixed $nnn bytes; shorter input is zero-filled |
| `Cnnn` | $nnn | C string in a fixed $nnn-byte field: the text to the first NUL, at most $nnn − 1 characters, zero-filled |
| `Pnnn` | 1 + $nnn | Pascal string in a fixed field of a length byte and $nnn characters, zero-filled |
| `FBYT`, `FWRD`, `FLNG` | 1, 2, 4 | Filler: not shown, written as zeros |
| `AWRD`, `ALNG` | 0–1, 0–3 | Zero bytes to an even offset, or a multiple of 4, from the start of the resource data |
| `OCNT`, `ZCNT` | 2 | The item count of the `LSTC` right after it: `OCNT` is the count, `ZCNT` the count − 1 ($FFFF is 0 items) |
| `LSTC`, `LSTB`, `LSTZ`, `LSTE` | 0 | List begin and end (§1.3) |

`nnn` is three uppercase hex digits. The pad bytes of `ESTR`, `OSTR`, `ECST` and `OCST` are skipped without being
checked. [Code: ResEdit 2.1.3]

### 1.3 Lists

A list begin holds the fields up to its `LSTE` as one item, repeated [Code: ResEdit 2.1.3]:

| Begin | Items | Written back as |
| --- | --- | --- |
| `LSTC` | The count from the `OCNT` or `ZCNT` just before it (0 skips the list) | The items; the count word from the number of items |
| `LSTB` | Until the data ends at the start of an item | The items |
| `LSTZ` | Until a 0 byte at the start of an item (that byte is consumed) or the data ends | The items, then one 0 byte |
| `LSTE` | Closes the innermost list | — |

Lists nest; an `ALNG` inside a list aligns each item from the start of the data. [Code: ResEdit 2.1.3]

## 2. Reading

### 2.1 Finding a template

ResEdit looks a template up by name, the type's four characters, in the open files and its own resource fork
(Get1NamedResource) [Code: ResEdit 2.1.3].

### 2.2 Which templates are refused

ResEdit 2.1.3 refuses a template, and will not open a resource with it, when [Code: ResEdit 2.1.3, its template check,
`STR#` 150 messages 4–11]:

1. a field type is not one of §1.2 (`UBYT`, `KBYT`, `BCNT`, `BSKP`, `COLR` and other later types included);
2. `HEXD` is not the last field;
3. a run of `BBIT`s is not a multiple of 8;
4. an `OCNT` or `ZCNT` is not followed by `LSTC`, or an `LSTC` does not follow one;
5. a list has no `LSTE`, or an `LSTE` no list begin;
6. an `LSTB` follows another. ResEdit clears its mark of an open `LSTB` only when a list nested inside it closes, so
   two top-level `LSTB`s in a row are refused too.

### 2.3 Reading a resource

1. Read the fields in template order, each from where the previous one ended (§1.2).
2. A count field sets the number of items of the `LSTC` after it; `LSTB` and `LSTZ` read items as §1.3 says.
3. Where the template needs bytes past the end of the data, ResEdit offers to append zeros and then opens the
   resource. Strings, `HEXD` and C strings stop at the end of the data. [Code: ResEdit 2.1.3]
4. Where data is left after the last field, ResEdit offers to cut it off. [Code: ResEdit 2.1.3]
5. ResEdit refuses a count of $800 or more, and lists of $800 or more shown fields. [Code: ResEdit 2.1.3]

## 3. Writing

Each field is written from its shown value, in template order [Code: ResEdit 2.1.3]:

1. Numbers as decimal, or as hex after `$`, in any numeric field.
2. Counts from the number of items in their list; the editor keeps them in step as items are added and removed.
3. Fillers, alignment and pad bytes as zeros.
4. A `BOOL` as `01 00` or `00 00`.
5. Fixed fields zero-filled to their size.

ResEdit refuses a Pascal string over 255 characters, a `CHAR` of more than one character, a `TNAM` of more than four,
text too long for a `Pnnn` or `Cnnn` field, and malformed numbers or hex. It warns about a `RECT` whose top is below
its bottom or whose left is right of its right. [Code: ResEdit 2.1.3]

## 4. Variants

- A `Pnnn` field is 1 + $nnn bytes in ResEdit 2.1.3 [Code: ResEdit 2.1.3]; some later tools make it $nnn bytes (not
  checked).
- Later tools add field types (Resorcerer's keyed sections `KBYT`…`KEYE`, `BCNT`, `BSKP`, `UBYT`, `COLR`, `DATE` and
  others), which ResEdit 2.1.3 refuses (§2.2).

## 5. ClassicMac

- `ResourceTemplate.Parse` reads a `TMPL`; a template whose data ends inside a field throws. `Problems` lists what
  §2.2 refuses, and a template with problems is not used. [ClassicMac]
- `ResourceTemplate.Find` takes the first `TMPL` in a fork whose name is the type's four characters. [ClassicMac]
- `Read` gives the fields' values as text: decimal numbers, `$` hex, a `RECT` as "top, left, bottom, right",
  characters and strings as Mac OS Roman text, hex dumps as hex digits, flags as "1" or "0". Fillers and alignment are
  left out. Missing bytes read as zeros and are counted; data after the template is returned apart. [ClassicMac]
- `Write` writes the values back as §3, then the data that was after the template, kept rather than cut off. A value
  its field cannot hold is refused with a message naming the field. Decimal numbers may be signed or unsigned within
  the field's size; flags accept "1", "0", "true", "false" and empty. [ClassicMac]
- ResEdit's limits on counts and list sizes (§2.3 step 5) are not applied; a count is limited only by its 16-bit word.
  [ClassicMac]
- The app shows a resource through a template when it has no form of its own: one row per field (a check box for
  `BOOL` and `BBIT`, counts read-only), lists with their items to add, insert and remove. Like ResEdit it takes the
  template from the resource's own file, then from any other open file whose resources are loaded, so opening a copy
  of ResEdit makes its templates available. Apply makes an undoable edit. A resource that has a form of its own and a
  template also gets an "Edit with template" check box that swaps between the two. [ClassicMac]
- `Map` gives where each field lies in the data (`TemplateSpan`: its offset, length, the 1-based numbers of the list
  items it is in, and its value), fillers, alignment and the 0 byte ending an `LSTZ` included; a `BBIT` lies in the
  byte holding it. `ByteMeanings.MeaningAt` turns it into what a byte means, for the hex inspector: the field's label
  with its item numbers ("ID of item 3", "Value of item 2.1"); for strings their length, characters and padding
  ("Length of Name of item 1", "Character 2 of Name of item 1"); "Byte *k* of" a hex field; "Bits on: a; off: b"
  for a byte of bit fields (the named ones set, then those clear, most significant first); "Filler", "Alignment",
  "End of" a list or C string; "After the template's fields" for data past it.
  [ClassicMac]
- ClassicMac ships its own templates (`BuiltInTemplates`, made with `ResourceTemplate.FromFields`) for common types
  with no form of their own, written in its own words from Apple's documentation of each layout; none of ResEdit's
  `TMPL` resources are copied, and ResEdit's templates served only as a cross-check of field order and kinds. A `TMPL`
  in the resource's file or another open file wins over a built-in one; the template form and the byte meanings use
  both. [ClassicMac]

  | Type | Fields | Layout from |
  | --- | --- | --- |
  | `MBAR` | count, then menu IDs | [Doc: Inside Macintosh: Macintosh Toolbox Essentials, The Menu Bar Resource] |
  | `BNDL` | owner signature and ID, types (count − 1), each with its local and resource ID pairs (count − 1) | [Doc: Macintosh Toolbox Essentials, The Bundle Resource] |
  | `FREF` | file type, local icon ID, file name (unused) | [Doc: Macintosh Toolbox Essentials, The File Reference Resource] |
  | `SIZE` | the 16 flag bits (bit 15 first), preferred and minimum memory size | [Doc: Inside Macintosh: Processes, The Size Resource] |
  | `TMPL` | label and type pairs to the end | [Doc: ResEdit Reference, Templates] |
  | `CURS` | 32-byte image, 32-byte mask, hot spot (vertical, horizontal) | [Doc: Inside Macintosh: Imaging With QuickDraw, The Cursor Resource] |
  | `PAT ` | 8-byte pattern | [Doc: Imaging With QuickDraw, The Pattern Resource] |
  | `PAT#` | count, then 8-byte patterns | [Doc: Imaging With QuickDraw, The Pattern List Resource] |
  | `clut` | seed, flags, entries (count − 1): value and RGB each | [Doc: Imaging With QuickDraw, The Color Table Resource] |
  | `wctb`, `actb`, `dctb`, `cctb` | as `clut` (the window, alert, dialog and control colour tables) | [Doc: Macintosh Toolbox Essentials, the colour table resources] |
  | `mctb` | count, then per entry menu ID, item, four RGB colours, a reserved word | [Doc: Macintosh Toolbox Essentials, The Menu Color Information Table Resource] |

## 6. Diagnostics

None. ClassicMac emits no diagnostic codes for templates: a refused template's problems are text in
`ResourceTemplate.Problems` (§5), and a value that does not fit is refused with a message.

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/TemplateTests.cs`: every scalar type read and written back; ResEdit's
  padding; counted, to-the-end and zero-terminated lists; short data read as zeros and written zero-filled, extra data
  kept; the refused templates of §2.2, two top-level `LSTB`s in a row included; values that do not fit refused;
  templates found by name.
- `tests/ClassicMac.App.Tests/EditTests.cs`: a template in another open file is used; the count is kept in step as
  items are added; a value that does not fit is refused and the resource left unchanged; the "Edit with template"
  choice for a resource with a form of its own.
- ResEdit 2.1.3, run locally on a copy of the application (Apple's file, not committed) [Verified: ResEdit 2.1.3]:
  - all 78 of its templates pass the check of §2.2;
  - applied to every resource of a templated type in its own resource fork (471), the templates consume 377
    exactly; 91 are short (`ALRT`s without the Auto Position word, `DLOG`s and `WIND`s without the alignment and
    position word); 3 have data after the terminator (pop-up `MENU`s);
  - read back and written again, all 471 come out byte for byte, the short ones zero-filled.

## 8. Not covered

- The field types of later tools (§4) are refused, as ResEdit 2.1.3 refuses them.

## 9. References

1. Apple, ResEdit 2.1.3: its template editor, traced in disassembly, and its templates.
2. Mathemaesthetics, Resorcerer: the later field types. Commercial; not used as a source.
