# ClassicMac format documentation

Implementer's specifications of the formats ClassicMac reads and writes. Each one describes its format completely
enough to write a reader (and, where ClassicMac writes the format, a writer) without reading ClassicMac's code, and
says where every rule comes from. They describe what classic Mac OS does: where Apple's documentation is silent, the
behaviour of Apple's own code decides, as traced in disassembly and checked on real software in an emulator.

## Reference builds

"Mac OS 9" in these documents means **Mac OS 9.0** (System and Finder `'vers'` 9.0, 1999; not 9.0.4 or later), the
build every [Code] rule was read from and every [Verified] result was checked on, run in SheepShaver on the NewWorld
"Mac OS ROM" file. Later 9.x releases changed native code and may differ, bugs included; a rule confirmed on 9.0 is not
claimed for them. The native code is in the System file's data fork:

| Fragment | Version (cfrg) | Size (bytes) | MD5 |
| --- | --- | --- | --- |
| NQD (native QuickDraw) | 2 | 272110 | 707386184e1116e90eac98dee8a68346 |
| FontManager | 3 | 196388 | e8c26caabcf4899e3a4c46c0b9760f9e |
| IconUtils | 1 | 22572 | fcb1fb8b24b305463beeb5d1dd1f7084 |
| IconServicesLib | 1 | 102295 | b4cc469f96a37304c5885150f1e1ab74 |
| MacDialogsLib | 0 | 65339 | 241bc9791802c86e29897e7814bb162d |
| MenusLib | 0 | 101770 | 00e2e2f43ec91287f4c6bb1150cef4d4 |
| ControlsLib | 0 | 59769 | f64fadc010d3c8d0d580be2f5adc9f43 |
| WindowsLib | 1 | 134885 | d0fb8648a2c6c7f46b5ad3cc4d00a6a6 |
| AppearanceLib | 1 | 317505 | 3f0f0d3a8afb7676a3bffd130e1f73f8 |

(Other components: Sound Manager 3.5.1, Appearance 1.1.4, File Exchange 3.0.3, Text Encoding Converter 1.5, Foreign File
Access 5.3.) "The ROM" or "the 68k ROM" means the 68k code in the Mac OS ROM image with ROM version `$077D`, the classic
QuickDraw and Toolbox every Mac before Mac OS 9 used in some revision.

**Mac OS 9.2.2** (System `'vers'` 9.2.2) was compared with 9.0 from the code only; it does not run in SheepShaver, so
nothing was verified on it. Every fragment above is a newer version there (NQD cfrg 8, IconUtils 3, IconServicesLib 4,
FontManager 598020), yet almost every rule these documents give is unchanged, including Mac OS 9.0's bugs such as the
scaled CopyMask on a 1-bit screen. The differences found [Code: 9.2.2]:

- icons: a 16-bit screen takes the 32-bit members (`il32`, `is32`, `ih32`) instead of `icl8`; an `icns` element over
  $18FFF bytes fails the whole family; the open transform on a one-row mask draws an empty row;
- QuickDraw: a hidden pen (pnVis < 0) also stops CopyMask and CopyDeepMask into the port's own pixels;
- menus: a `MENU` item's icon byte is sign-extended into its text encoding; items of height 0, separators included, are
  not drawn; an item under 32 counts as enabled by its enableFlags bit alone;
- fonts: a `FOND`'s language word is kept only when ffVersion ≥ 4; the System's bitmap fonts were renumbered (Geneva
  9 and 12 are `NFNT` 1025 and 1026, Monaco 9 is 1027, the bitmaps unchanged);
- Appearance: utility window titles use the application's script font.

ClassicMac follows 9.0; a 9.2.2 mode would take these differences.

| Document | Formats | Code |
| --- | --- | --- |
| [RESOURCE-FORK.md](RESOURCE-FORK.md) | Resource forks: header, map, attributes; how Mac OS 9 and the 68k ROM open them; compressed resources (`dcmp` 0–3) | `ClassicMac.Resources` |
| [CONTAINERS.md](CONTAINERS.md) | MacBinary I/II/III, BinHex 4.0, AppleSingle and AppleDouble | `ClassicMac.Files.Containers` |
| [HOST-FOLDERS.md](HOST-FOLDERS.md) | Mac files on other file systems: Basilisk II / SheepShaver folders, AppleDouble `._` files, PC Exchange, File Exchange names; what `unpack` writes | `ClassicMac.Files` (`HostFiles`), `ClassicMac.Files.Export` |
| [HFS-MFS.md](HFS-MFS.md) | Apple partition maps, MFS and HFS volumes | `ClassicMac.Files.Hfs` |
| [ARCHIVES.md](ARCHIVES.md) | StuffIt v5 stored, RLE90 and Compress/LZW forks (initial subset) | `ClassicMac.Files.Archives` |
| [DISK-IMAGES.md](DISK-IMAGES.md) | Disk Copy 4.2, DART, NDIF (Disk Copy 6), ShrinkWrap, UDIF; the ADC, KenCode, LZH and bzip2 codecs | `ClassicMac.Files.Hfs`, `ClassicMac.Files.Compression` |
| [FAT.md](FAT.md) | DOS partition tables, FAT12/16/32 with long names, PC Exchange and File Exchange data on FAT | `ClassicMac.Files.Fat` |
| [ISO9660.md](ISO9660.md) | ISO 9660 and High Sierra as Mac OS 9 reads them, Apple's extensions, raw CD images and cue sheets | `ClassicMac.Files.Iso` |
| [SOUND.md](SOUND.md) | `snd ` resources; MACE 3:1 and 6:1, IMA 4:1, µ-law; the WAV output | `ClassicMac.Resources.Decoders.Sound` |
| [TEXT.md](TEXT.md) | `STR `, `STR#`, `TEXT` and `styl`, `vers`; the text, RTF and JSON output | `ClassicMac.Resources.Decoders.Text` |
| [INTERFACE.md](INTERFACE.md) | `MENU`, `MBAR`, `WIND`, `DLOG`, `ALRT`, `DITL`, `CNTL`, their colour tables and Appearance extensions; the JSON output | `ClassicMac.Resources.Decoders.Interface` |
| [PALETTES.md](PALETTES.md) | `clut` colour tables and `pltt` palettes; the JSON and `.act` output | `ClassicMac.Resources.Decoders.Colors` |
| [TEMPLATES.md](TEMPLATES.md) | `TMPL` resource templates, as ResEdit 2.1.3 reads and writes resources through them | `ClassicMac.Resources.Decoders.Templates` |
| [FINDER.md](FINDER.md) | `BNDL`, `FREF`, `SIZE`; the JSON output | `ClassicMac.Resources.Decoders.Finder` |
| [FONTS.md](FONTS.md) | `NFNT`/`FONT` strikes, `FOND` families, `fctb`, `sfnt`; the glyph sheet, BDF and JSON output | `ClassicMac.Graphics.Fonts`, `ClassicMac.Resources.Decoders.Fonts` |
| [DOCUMENTS.md](DOCUMENTS.md) | DOCMaker stand-alone documents and SimpleText documents with pictures; the HTML output | `ClassicMac.Resources.Decoders.Documents` |
| [PICT.md](PICT.md) | QuickDraw pictures (PICT v1, v2, extended v2): container, opcodes, operands, DrawPicture's play state; writing pictures | `ClassicMac.Graphics.Pict` |
| [QUICKDRAW.md](QUICKDRAW.md) | How QuickDraw draws (Mac OS 9 and the 68k ROM): shapes, patterns, transfer modes, CopyBits, bitmap text and the Font Manager, screen depths | `ClassicMac.Graphics.QuickDraw` |
| [QUICKTIME.md](QUICKTIME.md) | QuickTime still images in pictures, the codecs, QTIF files | `ClassicMac.Graphics.QuickTime` |
| [MACPAINT.md](MACPAINT.md) | MacPaint documents | `ClassicMac.Graphics` |
| [ICONS.md](ICONS.md) | Icon, cursor and pattern resources; icon families (`icns`, 48 × 48, 32-bit, 8-bit masks) | `ClassicMac.Resources.Decoders.Images` |
| [EXPORT-MANIFEST.md](EXPORT-MANIFEST.md) | What `extract` writes: folders, file names, `manifest.json` format 1.1, image outputs | `ClassicMac.Resources.Export`, `ClassicMac.Resources.Decoders.Images` |

Pictures, icons, cursors and patterns are drawn by `ClassicMac.Graphics` (`.QuickDraw`, `.Pict`), which
[PICT.md](PICT.md) and [QUICKDRAW.md](QUICKDRAW.md) specify ([ICONS.md](ICONS.md) for icons, cursors and patterns).
[EXPORT-MANIFEST.md](EXPORT-MANIFEST.md) covers what ClassicMac adds: masks, cursor JSON and numbered list outputs.

## Conventions

These hold in every document unless it says otherwise.

- All multi-byte values are **big-endian**. Offsets are in hex (`+$1C` or `0x1C`), sizes in decimal.
- `u8`/`u16`/`u32` are unsigned, `i8`/`i16`/`i32` signed.
- `OSType` is four bytes read as characters (a type or creator code, `'TEXT'`). Codes are shown in quotes, including
  trailing spaces (`'snd '`).
- A Pascal string (`Str63`, `Str255`) is a length byte and that many bytes. Text is **Mac OS Roman** unless noted.
- A Mac date is a `u32` count of seconds since 1 January 1904, in the machine's **local time**.
- `Fixed` is a signed 16.16 fixed-point `i32`. An unsigned `Fixed` (sample rates) is a `u32`.
- `Rect` is four `i16`: top, left, bottom, right. `Point` is two `i16`: v, h.
- A **block** or **sector** is 512 bytes unless a format says otherwise.
- Structures are shown as tables of offset, size, type and meaning. Reserved fields are named, not skipped.

## Where the rules come from

Every rule carries one of these tags:

| Tag | Meaning |
| --- | --- |
| **[Doc]** | Apple's documentation: *Inside Macintosh* (named volume), a Technical Note, a Developer Note, or a standard (ECMA-119 for ISO 9660, Microsoft's FAT specification) |
| **[Code]** | Apple's own code, traced in disassembly, with the software and version named (the Mac OS 9.0 System file, the 68k ROM, Disk Copy 6.3.3, Sound Manager 3.5.1, File Exchange 3.0, …) |
| **[Verified]** | Checked against the real software's output, running in an emulator (SheepShaver or Basilisk II, Mac OS 9.0 unless stated) |
| **[Author]** | The published specification of a format's author, for formats Apple did not define (MacBinary, BinHex, LZHUF) |
| **[Fitted]** | Fitted to real files, with no code traced: the weakest source, and marked so it can be replaced |

What ClassicMac itself decides (its output formats, names, limits, severities, and how it recovers where the Mac would
crash or read stray memory) is design, not format. It is marked **[ClassicMac]**, or said in a sentence that begins
"ClassicMac …", and carries no source tag.

Where Apple's implementations disagree (Mac OS 9 and the 68k ROM, Disk Copy versions, PowerPC and 68k code), each
document says which one ClassicMac follows by default, and what the others do.

Other projects that handle these formats are behavioural references only. GPL and LGPL code is never a source for
ClassicMac.

## Diagnostics

ClassicMac reads damaged files as far as it can. It reports each problem as a diagnostic with a severity (Info,
Warning, Error) and a stable code (`fork.map-length`, `ndif.bad-checksum`, …), and throws only when a file cannot be
read at all. Each document lists its codes, and what the Mac does in the same case.

## Keeping them current

A change to how ClassicMac reads or writes a format updates its document in the same commit.
