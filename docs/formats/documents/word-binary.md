# Microsoft Word 6 to 97 binary documents (Word 6 and Word 98 for the Macintosh)

Word 98 for the Macintosh (1998) saved documents of type `'W8BN'`, creator `'MSWD'`, in the Word 97 binary format it
shared with Word 97 for Windows; Word 6 for the Macintosh (1994) saved `'W6BN'` documents in Word 6 for Windows' format,
its forerunner (§4.1). The document is a compound file
([compound-file.md](../containers/compound-file.md)): a WordDocument stream that begins with the FIB and holds the
text and the formatting pages, and a table stream with the piece table, the bin tables, the fonts and the styles.
Microsoft published the format as [MS-DOC]. ClassicMac reads the main text with its character and paragraph formatting
into a styled document, which the viewer shows and `convert` and `extract` write as HTML ([html.md](../output/html.md)).

| | |
| --- | --- |
| Identified by | Type `'W8BN'` or `'W6BN'`; a compound file with a WordDocument stream that starts `$A5EC` (Word 97, nFib `$00C1` or more) or `$A5DC` (Word 6 and 95, nFib `$0065`–`$0068`) |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Documents` (`WordBinaryDocuments`) |
| Verified against | Word 6.0 and Word 98 for the Macintosh documents with known content, Word 98's fast saved too (§7) |
| Sources | Microsoft's [MS-DOC] (the format's author); for Word 6, other readers' behaviour (LibreOffice, Apache POI, wv), no published specification |

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

All values are **little-endian**, whatever system wrote the file. A *CP* is a character's position in the document's
text; an *FC* is a byte offset in the WordDocument stream. [Author: [MS-DOC] §2.2.1]

### 1.1 FIB

The WordDocument stream starts with the FIB ([MS-DOC] §2.5.1): FibBase (32 bytes), `csw` and FibRgW97 (28 bytes),
`cslw` and FibRgLw97 (88 bytes), `cbRgFcLcb` and the FibRgFcLcb pairs. The fields ClassicMac reads [Author: [MS-DOC]
§2.5.2, §2.5.4, §2.5.6]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | wIdent | `$A5EC`; `$A5DC` in Word 6 and 95 (§4.1) |
| +$02 | 2 | nFib | `$00C1` from Word 97 on; less for Word 6 and 95 |
| +$0A | 2 | Flags | `$0004` fComplex (fast saved), `$0100` fEncrypted, `$0200` fWhichTblStm (the table stream is 1Table, else 0Table), `$8000` fObfuscated |
| +$4C | 4 | ccpText | The main text's length in CPs (FibRgLw97 + 12) |
| +$50 to +$6B | 4 each | ccpFtn, ccpHdd, ccpMcr, ccpAtn, ccpEdn, ccpTxbx, ccpHdrTxbx | Each part's length in CPs; the parts' text follows the main text in this order (§1.7) |
| +$98 | 2 | cbRgFcLcb | How many pairs follow (`$005D` for Word 97) |
| +$9A | 8 each | FibRgFcLcb97 | An FC and a length in the table stream per structure |

The pairs read, by index from 0: 1 `Stshf` (the style sheet), 2 `PlcffndRef`, 3 `PlcffndTxt`, 12 `PlcfBteChpx`,
13 `PlcfBtePapx`, 15 `SttbfFfn`, 33 `Clx`, 46 `PlcfendRef`, 47 `PlcfendTxt`. [Author: [MS-DOC] §2.5.6]

### 1.2 Piece table

The Clx ([MS-DOC] §2.9.38) is zero or more Prc (`$01`, a 2-byte size, a grpprl) and one Pcdt: `$02`, a 4-byte size,
and a PlcPcd ([MS-DOC] §2.8.35): *n* + 1 CPs, then *n* 8-byte Pcds ([MS-DOC] §2.9.177): 2 bytes of flags, an
FcCompressed, a 2-byte Prm. [Author]

FcCompressed ([MS-DOC] §2.9.73): bits 0–29 an FC, bit 30 `fCompressed`. When set, the piece's text is one byte a
character from FC ÷ 2, in Windows-1252 (bytes `$80`–`$9F` map to its characters there, the rest are Latin-1); when
clear, UTF-16 from the FC. [Author]

A Pcd's Prm ([MS-DOC] Prm, Prm0, Prm1) is a fast save's property changes to the piece's text. Bit 0 set (Prm1):
bits 1–15 index the Clx's Prcs, whose grpprl applies. Clear (Prm0): bits 1–7 an isprm naming one sprm, bits 8–15
its one-byte operand; 0 is no change. The isprms used here: `$05` sprmPJc, `$18` sprmPFInTable, `$19` sprmPFTtp,
`$53` sprmCPlain, `$55`–`$56` sprmCFBold and sprmCFItalic, `$58`–`$5C` sprmCFOutline, sprmCFShadow, sprmCFSmallCaps,
sprmCFCaps and sprmCFVanish, `$5E` sprmCKul, `$62` sprmCIco; [MS-DOC] lists the rest. [Author] They are Word 6's
sprm numbers for the same properties, so a Word 6 Prm0's isprm is read as its sprm. [Fitted: the tables agree; no
Word 6 Prm sample]

### 1.3 Bin tables and FKP pages

PlcBteChpx and PlcBtePapx ([MS-DOC] §2.8.5, §2.8.6): *n* + 1 FCs, then *n* 4-byte page numbers (the low 22 bits). Page
*pn* is the 512 bytes at FC *pn* × 512 of the WordDocument stream [Author]:

| Page | Layout |
| --- | --- |
| ChpxFkp ([MS-DOC] §2.9.33) | *crun* + 1 FCs; *crun* offset bytes; Chpx blocks (§2.9.32: a size byte, a grpprl); *crun* in the last byte |
| PapxFkp ([MS-DOC] §2.9.174) | *cpara* + 1 FCs; *cpara* BxPaps of 13 bytes (§2.9.23: the offset byte, 12 bytes of line data); PapxInFkp blocks; *cpara* in the last byte |

A block is at twice its offset byte from the page start; offset 0 means none. A PapxInFkp ([MS-DOC] §2.9.175) is a
count *cb* and 2 × *cb* − 1 bytes, or 0, a count *cb′* and 2 × *cb′* bytes, of GrpPrlAndIstd (§2.9.113): the style's
`istd` (2 bytes) and a grpprl. [Author]

### 1.4 Sprms

A grpprl is a list of Sprms ([MS-DOC] §2.2.5.1): a 2-byte code whose top 3 bits, `spra`, give the operand's size: 0 or
1 one byte, 2, 4 and 5 two, 3 four, 7 three, 6 a size byte and that many (sprmTDefTable `$D608`: a 2-byte size, less
one; sprmPChgTabs `$C615`: a size byte, 255 meaning it must be worked out). The ones read [Author: [MS-DOC] §2.6.1,
§2.6.2]:

| Sprm | Property |
| --- | --- |
| `$0835`, `$0836`, `$0838`, `$0839` | Bold, italic, outline, shadow (ToggleOperand) |
| `$083A` | Small caps (ToggleOperand) |
| `$083B`, `$083C` | All caps, hidden (ToggleOperand) |
| `$2A3E` | Underline kind (0 none) |
| `$2A42` | The colour, an Ico ([MS-DOC] §2.9.119): 0 automatic, 1 black, 2 blue, 3 cyan, 4 green, 5 magenta, 6 red, 7 yellow, 8 white, 9–16 the dark colours and grey |
| `$4A43` | Size in half points |
| `$4A4F` | The font: an index into SttbfFfn |
| `$2403`, `$2461` | Alignment: 0 left, 1 centred, 2 right, 3 and 4 justified |
| `$840F`, `$845E` | Left indent, twips |
| `$840E`, `$845D` | Right indent, twips |
| `$8411`, `$8460` | First-line indent, twips |
| `$A413`, `$A414` | Space before, space after, twips |
| `$2416` | In a table |
| `$2417` | The row's end mark |
| `$D608` | sprmTDefTable, on the row's end mark: after its size word, the cell count, then the row's left edge and each cell's right edge (`i16` twips) |
| `$0855` | sprmCFSpec: the character is special (a picture, a note's mark) |
| `$6A03` | sprmCPicLocation: a picture's PICF, by its offset in the Data stream (§1.8) |

A ToggleOperand ([MS-DOC] §2.9.327) is 0 off, 1 on, `$80` the style's value, `$81` its opposite. [Author]

### 1.5 Fonts and styles

- SttbfFfn ([MS-DOC] §2.9.286): a count, a zero extra size, then per font a size byte and an FFN (§2.9.82) whose name
  starts at its byte 39, null-terminated UTF-16. [Author]
- The STSH ([MS-DOC] §2.9.271): a size and the STSHI (§2.9.272: the style count at +0, the StdfBase size at +2, the
  default font's index at +12), then per style a size and an STD (§2.9.258): the StdfBase (§2.9.260: the style kind in
  the low 4 bits and the base style in the high 12 of its second word), the name (a count, UTF-16, a null), then the
  property exceptions, each a size, the UPX and a pad to an even size: a paragraph style's UpxPapx (an `istd` and a
  grpprl) and UpxChpx, a character style's UpxChpx. [Author]
- Default character properties: the default font, 10 point (20 half points). [Author: [MS-DOC] §2.6.1]

### 1.6 Characters

| Character | Meaning |
| --- | --- |
| `$0D` | Paragraph end |
| `$07` | End of a table cell; with sprm `$2417`, the end of the row |
| `$0B` | Line break |
| `$0C` | Page or section break |
| `$0E` | Column break |
| `$13`, `$14`, `$15` | A field's start, the separator between its instructions and its result, its end |
| `$1E`, `$1F` | Non-breaking hyphen, optional hyphen |
| `$01`, `$02`, `$05`, `$08` | With sprmCFSpec: a picture (§1.8), an auto-numbered note's mark (§1.7), an annotation mark, a drawing |

[Author: [MS-DOC]]

### 1.7 Footnotes and endnotes

Each part's text follows the main text, in the order of §1.1's counts: the footnotes from ccpText, the endnotes after
the footnotes, headers, macros and annotations. Footnotes and endnotes are listed alike [Author: [MS-DOC] PlcffndRef,
PlcffndTxt, PlcfendRef, PlcfendTxt]:

- The references (`PlcffndRef`, `PlcfendRef`): *n* + 1 CPs in the main text, then an FRD (`i16`) per note, more than 0
  when it is auto-numbered.
- The texts (`PlcffndTxt`, `PlcfendTxt`): *n* + 2 CPs in the part's text, each note's first, then the end.
- An auto-numbered note's mark is `$02` with sprmCFSpec, at its reference and at the start of its text [Verified: Word
  6.0 and 98 documents].

### 1.8 Pictures

A picture is a `$01` with sprmCFSpec; its sprmCPicLocation is the offset of its PICFAndOfficeArtData in the Data stream
[Author: [MS-DOC] PICFAndOfficeArtData, PICF, PICMID]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 4 | lcb | The structure's bytes |
| +$04 | 2 | cbHeader | The PICF's bytes (`$44`) |
| +$06 | 2 | mfpf.mm | `$64` (MM_SHAPE): an Office Art shape follows; `$66` (MM_SHAPEFILE): a name (a length byte and the characters), then the shape |
| +$1C, +$1E | 2 each | dxaGoal, dyaGoal | The picture's size in twips |
| +$20, +$22 | 2 each | mx, my | The scale, in thousandths |

The shape's records ([MS-ODRAW] OfficeArtRecordHeader: a version in the low 4 bits and an instance in the high 12, a
type, a length) hold the picture in a blip, inside containers (version `$F`) and an FBSE (36 bytes, a name of the
length at its +33, then the blip) [Author: [MS-ODRAW] OfficeArtFBSE, OfficeArtBlip]:

| Type | Blip | After the record header |
| --- | --- | --- |
| `$F01C` | PICT | A UID (two for instance `$543`), a 34-byte metafile header (cbSize, rcBounds, ptSize, cbSave, compression: 0 deflated (zlib), `$FE` stored; filter), then the `PICT` without its 512-byte header |
| `$F01E` | PNG | A UID (two for instance `$6E1`), a tag byte, the PNG file |
| `$F01D`, `$F02A` | JPEG | A UID (two for instances `$46B`, `$6E3`), a tag byte, the JPEG file |

Word 98 for the Macintosh stored its sample's picture, made from RTF's `\macpict`, as a PNG blip [Verified: Word 98
documents].

## 2. Reading

1. Read the compound file and its WordDocument stream; check wIdent and nFib (§1.1).
2. When fEncrypted is set, stop (§5).
3. Open the table stream the FIB names.
4. Read the piece table (§1.2); the main text is CPs 0 up to ccpText, each with its FC. [Author: [MS-DOC] §2.4.1]
5. Read the fonts, the styles and the bin tables (§1.3, §1.5).
6. A paragraph runs to its mark; its properties are the PAPX whose FC range holds the mark's FC: the style's paragraph
   properties (through its base styles), then the PAPX's sprms. [Author: [MS-DOC] §2.4.2, §2.4.6]
7. A character's properties are its paragraph style's character properties, then the CHPX whose FC range holds its
   FC. [Author: [MS-DOC] §2.4.6]

## 3. Writing

None.

## 4. Variants

| nFib | Writer | Notes |
| --- | --- | --- |
| Less than `$0065` | Word 2 and earlier | Not in compound files; reported, not read |
| `$0065`, `$0068` | Word 6, Word 95 (Word 6 for the Macintosh, `'W6BN'`) | §4.1 |
| `$00C1` | Word 97, Word 98 | This document |
| `$00D9` and up (in fibRgCswNew) | Word 2000 and later | FibBase still says `$00C1`; more FibRgFcLcb pairs; read the same way |

[Author: [MS-DOC] §2.5.1]

### 4.1 Word 6 and 95

Microsoft documented Word 6's format only to licensees; no public specification was found. Its structures are Word
97's forerunners, read as other readers read them, and checked against documents Word 6.0 for the Macintosh wrote
[Verified: Word 6.0 documents, §7]: their FIB starts `$A5DC` with nFib `$0068`, chse is 256 (Mac OS Roman), and their
text, character and paragraph formats, line breaks and tables read as below. None of them was fast saved.

| Part | Word 6 | Source |
| --- | --- | --- |
| Streams | Everything is in the WordDocument stream; there is no table stream | [Reference: Apache POI] |
| FIB | +$14 chse (character set), +$18 fcMin, +$1C fcMac, +$34 ccpText; the FC and length pairs of §1.1 in the same order from +$58 (Stshf +$60, PlcfBteChpx +$B8, PlcfBtePapx +$C0, SttbfFfn +$D0, Clx +$160), with no count | [Reference: Apache POI] |
| Text | 8-bit: Mac OS Roman when chse is 256, else Windows-1252 | [Reference: wv] |
| Pieces | A Clx as §1.2 when fast saved, its FCs plain byte offsets; else the text is ccpText bytes from fcMin | [Reference: Apache POI] |
| Bin tables | Page numbers of 2 bytes | [Reference: Apache POI] |
| PAPX FKP | BXs of 7 bytes (the offset byte, 6 bytes of line data); a PAPX is a count of words and that many words: the `istd` (2 bytes) and the grpprl | [ClassicMac]: assumed, as Word 97's forerunner |
| Notes | The footnotes' tables as §1.1's pairs 2 and 3; the endnotes' at +$1D2 (references) and +$1DA (texts), outside the pairs' order; the parts' lengths from +$34 in §1.1's order | [Verified: Word 6.0 documents] |
| Pictures | Sprm 68 (sprmCPicLocation, after a size byte) is the PICF's offset in the WordDocument stream, 117 sprmCFSpec; the PICF as §1.8, its mm 8: a stand-in Windows metafile (an 18-byte header, whose size field undercounts, then records up to the one of function 0), followed by the `PICT` itself | [Verified: Word 6.0 documents] |
| Sprms | One-byte codes with sizes by code; the ones read: 5 alignment, 16 right, 17 left and 19 first-line indents, 21 and 22 space before and after, 24 in a table, 25 the row's end, 83 back to the style's character properties, 85 bold, 86 italic, 88 outline, 89 shadow, 90 small caps, 91 caps, 92 hidden, 93 font, 94 underline, 98 colour (an Ico, 6 red), 99 size, 190 the table's cell definitions (as `$D608`) | [Reference: LibreOffice; Verified: Word 6.0 documents] |
| Fonts | The table's size in bytes, then per font a size byte (less one), ffid, a weight word, a charset, the alternate name's index, and the name, 8-bit and null-terminated | [Reference: Apache POI] |
| Styles | As §1.5, with a 14-byte STSHI (the default font at +12) and the name as a length byte, the characters and a null, the UPXs from the next even offset | [ClassicMac]: assumed, as Word 97's forerunner |

## 5. ClassicMac

- One chapter, titled with the file's name; paragraphs carry their alignment, indents and spacing in points; fonts are
  named as the document names them. [ClassicMac]
- Paragraph ends, page, section and column breaks become CR; a line break (`$0B`) becomes U+2028, a `<br>` in HTML. A
  cell's end becomes a tab and a row's end a CR, and the rows are a `DocumentTable` with the cell edges in points,
  written as an HTML table. Small caps and the colour are carried on the runs. [ClassicMac]
- A field shows its result; its instructions are left out. [ClassicMac]
- A note's mark shows its number, footnotes and endnotes each numbered from 1 (Word's own numbering formats are not
  read); the notes' text follows the main text in the chapter, each a `DocumentNote`, written as an HTML section of
  notes linked both ways. [ClassicMac]
- A picture is an option space (U+00A0) its `DocumentPicture` is anchored at, at its goal size scaled, placed by its
  paragraph's alignment: a `PICT` drawn, a PNG or JPEG file kept as it is. One that cannot be found or read is
  reported and left out. [ClassicMac]
- Annotation marks and drawings are left out; non-breaking and optional hyphens become U+2011 and U+00AD. Hidden text
  is left out and all-caps text upper-cased. [ClassicMac]
- A fast-saved document is read through its piece table as [MS-DOC] specifies, each piece's Prm applied over its
  characters' properties, and over a paragraph's from the piece that holds its mark (§1.2). A Prm1 naming a Prc the
  Clx lacks is reported and left out. [ClassicMac]
- An encrypted or obfuscated document is reported and not read. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `word.bad-container` | Error | The compound file cannot be read, or has no WordDocument stream | Reads nothing | Not traced |
| `word.bad-fib` | Error | The WordDocument stream does not start with a FIB, or the table stream it names is missing | Reads nothing | Not traced |
| `word.bad-pieces` | Error, Warning | No piece table (Error), or a piece past the end of the stream (Warning) | Reads nothing, or cuts the text there | Not traced |
| `word.bad-sprm` | Warning | A property list's sprm runs past its end, or a Word 6 sprm code is unknown | Leaves out the rest of the list | Not traced |
| `word.bad-styles` | Warning | The style sheet's header is damaged | Reads without styles | Not traced |
| `word.bad-zone` | Warning | A table-stream structure or a formatting page lies past its stream's end | Leaves it out | Not traced |
| `word.encrypted` | Error | fEncrypted is set (encrypted or obfuscated) | Reads nothing | Word asks for the password |
| `word.not-shown` | Info | The text has special characters other than pictures and note marks | Leaves them out | Word shows them |
| `word.bad-picture` | Warning | A picture's PICF is outside its stream, or holds no picture this reader reads (an EMF, WMF, DIB or TIFF blip) | Leaves the picture out | Not traced |
| `word.bad-notes` | Warning | A note's text cannot be found in its part's list | Leaves the note out | Not traced |
| `word.piece-properties` | Warning | A piece's Prm1 names a Prc the Clx does not have | Reads the text without those changes | Not traced |
| `word.unsupported-version` | Error | nFib below `$0065` (older than Word 6) | Reads nothing | Not traced |

The compound file's own diagnostics are in [compound-file.md §6](../containers/compound-file.md#6-diagnostics).

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/WordBinaryTests.cs` builds documents byte by byte from [MS-DOC]
  (`WordBinaryFixtures.cs`, in a compound file from `CompoundFileBuilder.cs`): character sprms, a compressed and a
  UTF-16 piece, paragraph sprms, styles through their base, toggles, hidden and all-caps text, fields, tables,
  special characters, encryption and obfuscation, the 0Table stream, a Prm, an older nFib, other compound files, a
  footnote and an endnote, a deflated PICT blip, PNG and JPEG blips, a missing picture, and the HTML output.
- `tests/ClassicMac.Resources.Decoders.Tests/WordSixTests.cs` builds Word 6 documents byte by byte as §4.1 lays them
  out (`WordSixFixtures.cs`): Mac OS Roman and Windows text, character and paragraph sprms, styles and `sprmCPlain`,
  tables and fields, a piece table, an unknown sprm, encryption.
- `tests/ClassicMac.Resources.Decoders.Tests/WordSampleTests.cs` on `Word/w6-*.bin`: 10 documents Word 6.0 for the
  Macintosh wrote in SheepShaver for ClassicMac (our own content, listed in `Word/CONTENTS.txt`): text, every character
  format (small caps, red), the paragraph formats, a 3 × 3 table with its cell edges, and edited documents. Word saved
  them in full.
- The same tests on `Word/w98-*.bin`: 10 documents Word 98 for the Macintosh (8.0) wrote on Mac OS 9.2.2 for
  ClassicMac, the same content saved in full (nFib 193), and five fast saved (fComplex 1, cQuickSaves 1, one edit
  each): the text, formats and table read as Word showed them, the fast saves through their piece tables with each
  insertion in the formatting of the character before it, and a picture and a note, full and edited: Word 6 and 98
  made the RTF's footnote an endnote, Word 6 kept the `PICT` after a stand-in metafile and Word 98 made it a PNG, both
  shown at 64 × 64; and the HTML's linked note and picture. None of the five pieces carries a Prm; `w98-prm-fast`, a
  fast save that only centred a paragraph, does (Prm0 `$010A` on that paragraph's mark), and reads with only that
  paragraph centred.

## 8. Not covered

- Fast-saved Word 6 documents (none was made: Word 6 saved every sample in full); Word 95's East Asian and Unicode text.
- A Prm1 from a real document (only built ones are tested), and Prm0s for properties this reader does not apply.
- Character styles (`sprmCIstd`), list numbering, tabs, borders, line spacing, sections, headers and East Asian text.
- Notes' numbering formats (the DOP's) and custom marks; a picture's cropping; EMF, WMF, DIB and TIFF blips, and
  Word 97's pictures stored as metafiles.
- Decrypting password-protected documents.

## 9. References

1. Microsoft, *[MS-DOC]: Word (.doc) Binary File Format*: §2.2.5.1 Sprm, §2.4.1 Retrieving Text, §2.4.2 Determining
   Paragraph Boundaries, §2.4.6 Applying Properties, §2.5 the FIB, §2.6 the sprms, §2.8 the PLCs, §2.9 the structures.
2. LibreOffice, the Word filter's Word 6 sprm table (MPL 2.0): Word 6's sprm codes and sizes. Behaviour only.
3. Apache POI, HWPF's Word 6 and 95 support (Apache 2.0): the FIB's offsets, the font table, the bin tables, documents
   with no piece table. Behaviour only.
4. wv, its notes on the FIB (GPL): the meaning of chse. Behaviour only; no code is taken from it.
5. Microsoft, *[MS-ODRAW]: Office Drawing Binary File Format*: OfficeArtRecordHeader, OfficeArtFBSE, OfficeArtBlipPICT,
   OfficeArtBlipPNG, OfficeArtBlipJPEG, OfficeArtMetafileHeader.
