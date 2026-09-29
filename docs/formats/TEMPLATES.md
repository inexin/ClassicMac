# Resource templates (`TMPL`)

Read and written by `ClassicMac.Resources.Decoders` (`Templates/ResourceTemplate`); the app's Edit tab shows a
resource through one when it has no form of its own.

A `TMPL` resource describes the fields of another resource type, for a generic editor. ResEdit introduced them and
other editors (Resorcerer, later ResEdit-like tools) extended them. This document follows **ResEdit 2.1.3**: its
template editor decides what every field type means [Code: ResEdit 2.1.3's template editor]. Big-endian throughout.

## Contents

1. [The resource](#1-the-resource)
2. [Field types](#2-field-types)
3. [Lists](#3-lists)
4. [Which templates are refused](#4-which-templates-are-refused)
5. [Data that does not fit](#5-data-that-does-not-fit)
6. [Finding a template](#6-finding-a-template)
7. [Writing](#7-writing)
8. [ClassicMac](#8-classicmac)
9. [Not covered](#9-not-covered)

## 1. The resource

A `TMPL` is a sequence of fields, each a Pascal string (the label) and a four-character field type, with no count and no
padding, to the end of the resource. The resource's **name** is the type it describes; its ID does not matter. A label
of `*****` is only a convention for list lines. [Code]

## 2. Field types

Every field starts right after the previous one: there is **no implicit alignment**, and words and longs may sit at odd
offsets. [Code]

| Type | Bytes | Meaning |
|---|---|---|
| `DBYT`, `DWRD`, `DLNG` | 1, 2, 4 | signed decimal number |
| `HBYT`, `HWRD`, `HLNG` | 1, 2, 4 | hex number, shown `$` and 2, 4 or 8 digits |
| `CHAR` | 1 | one character; a 0 byte is empty |
| `TNAM` | 4 | a four-character type; shorter text is padded with spaces |
| `BOOL` | 2 | true when the **first** byte is not 0 (the second is ignored); written `01 00` or `00 00` |
| `BBIT` | 1 bit | one bit, bit 7 first; they come in runs of 8, one byte a run |
| `RECT` | 8 | four signed words: top, left, bottom, right |
| `PSTR` | 1 + n | Pascal string |
| `ESTR`, `OSTR` | 1 + n (+ 1) | Pascal string, then a 0 byte when needed to make the string's total length (length byte and text) even or odd |
| `WSTR`, `LSTR` | 2 + n, 4 + n | string after a 16- or 32-bit length |
| `CSTR` | n + 1 | C string: the text and a NUL |
| `ECST`, `OCST` | n + 1 (+ 1) | C string, then a 0 byte when needed to make its total length (with the NUL) even or odd |
| `HEXD` | the rest | hex dump of every byte left; must be the template's last field |
| `Hnnn` | $nnn | hex dump of a fixed $nnn bytes; shorter input is zero-filled |
| `Cnnn` | $nnn | C string in a fixed $nnn-byte field: the text to the first NUL, at most $nnn − 1 characters, zero-filled |
| `Pnnn` | 1 + $nnn | Pascal string in a fixed field of a length byte and $nnn characters, zero-filled |
| `FBYT`, `FWRD`, `FLNG` | 1, 2, 4 | filler: not shown, written as zeros |
| `AWRD`, `ALNG` | 0–1, 0–3 | zero bytes to an even offset, or a multiple of 4, **from the start of the resource data** |
| `OCNT`, `ZCNT` | 2 | the item count of the `LSTC` right after it: `OCNT` is the count, `ZCNT` the count − 1 ($FFFF is 0 items) |
| `LSTC`, `LSTB`, `LSTZ`, `LSTE` | 0 | list begin and end (§3) |

`nnn` is three **uppercase** hex digits. A `Pnnn` field is 1 + $nnn bytes in ResEdit 2.1.3; some later tools make it
$nnn bytes (ClassicMac follows ResEdit). The pad bytes of `ESTR`, `OSTR`, `ECST` and `OCST` are skipped without being
checked. [Code]

## 3. Lists

A list begin holds the fields up to its `LSTE` as one item, repeated: [Code]

| Begin | Items | Written back as |
|---|---|---|
| `LSTC` | the count from the `OCNT` or `ZCNT` just before it (0 skips the list) | the items; the count word from the number of items |
| `LSTB` | until the data ends at the start of an item | the items |
| `LSTZ` | until a 0 byte at the start of an item (that byte is consumed) or the data ends | the items, then one 0 byte |
| `LSTE` | closes the innermost list | — |

Lists nest; an `ALNG` inside a list aligns each item from the start of the data.

## 4. Which templates are refused

ResEdit 2.1.3 refuses a template (it will not open the resource with it) when: [Code]

- a field type is not one of §2 (`UBYT`, `KBYT`, `BCNT`, `BSKP`, `COLR` and other later types included);
- `HEXD` is not the last field;
- a run of `BBIT`s is not a multiple of 8;
- an `OCNT` or `ZCNT` is not followed by `LSTC`, or an `LSTC` does not follow one;
- a list has no `LSTE`, or an `LSTE` no list begin;
- an `LSTB` follows another. ResEdit clears its mark of an open `LSTB` only when a list nested inside it closes, so two
  top-level `LSTB`s in a row are refused too.

Every template in ResEdit 2.1.3 itself passes. [Verified: ClassicMac's check on ResEdit 2.1.3's 78 templates]

## 5. Data that does not fit

- **Too short:** where the template needs bytes past the end of the data, ResEdit offers to append zeros and then opens
  the resource. Strings, `HEXD` and C strings stop at the end of the data. [Code]
- **Too long:** where data is left after the last field, ResEdit offers to cut it off. [Code]
- ResEdit refuses a count of $800 or more and lists of $800 or more shown fields. [Code]

Applied to every resource of a templated type in ResEdit 2.1.3's own fork (471), the templates consume 377 exactly;
91 are short (`ALRT`s without the Auto Position word, `DLOG`s and `WIND`s without the alignment and position word);
3 have data after the terminator (pop-up `MENU`s). [Verified: ClassicMac, byte for byte on writing back]

## 6. Finding a template

ResEdit looks a template up by name, the type's four characters, in the open files and its own resource fork
(Get1NamedResource). [Code]

## 7. Writing

Each field is written from its shown value, in template order: [Code]

- numbers as decimal or as hex after `$`, in any numeric field;
- counts from the number of items in their list (the editor keeps them in step as items are added and removed);
- fillers, alignment and pad bytes as zeros;
- a `BOOL` as `01 00` or `00 00`;
- fixed fields zero-filled to their size.

ResEdit refuses a Pascal string over 255 characters, a `CHAR` of more than one character, a `TNAM` of more than four,
text too long for a `Pnnn` or `Cnnn` field, and malformed numbers or hex. It warns about a `RECT` whose top is below its
bottom or whose left is right of its right. [Code]

## 8. ClassicMac

- `ResourceTemplate.Parse` reads a `TMPL`; `Problems` lists what §4 refuses (a template with problems is not used).
- `Read` gives the fields' values as text: decimal numbers, `$` hex, a `RECT` as "top, left, bottom, right",
  characters and strings as Mac OS Roman text, hex dumps as hex digits, flags as "1" or "0". Fillers and alignment
  are left out. Missing bytes read as zeros and are counted; data after the template is returned apart.
- `Write` writes the values back as §7, then the data that was after the template (kept rather than cut off)
  [ClassicMac]. A value its field cannot hold is refused with a message naming the field. Decimal numbers may be
  signed or unsigned within the field's size [ClassicMac].
- **The app** shows a resource through a template when it has no form of its own: one row per field (a check box
  for `BOOL` and `BBIT`, counts read-only), lists with their items to add, insert and remove. Like ResEdit it takes the
  template from the resource's own file, then from any other open file whose resources are loaded, so opening a copy of
  ResEdit makes its templates available. Apply makes an undoable edit. [ClassicMac]
- Read back and written again, all 471 resources of §5 come out byte for byte (the short ones zero-filled).

## 9. Not covered

- The field types of later tools (Resorcerer's keyed sections `KBYT`…`KEYE`, `BCNT`, `BSKP`, `UBYT`, `COLR`, `DATE`
  and others) are refused, as ResEdit 2.1.3 refuses them.
- ResEdit's limits on list sizes (§5) are not applied.
- Templates are not built in: ClassicMac ships none of ResEdit's.
