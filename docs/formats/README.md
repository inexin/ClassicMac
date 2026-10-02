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

(Other components: Sound Manager 3.5.1, Appearance 1.1.4 (the control panel; the Appearance Manager's Gestalt `'apvr'` is $0111, 1.1.1), File Exchange 3.0.3, Text Encoding Converter 1.5, Foreign File
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
- fonts: the font-load request copies a `FOND`'s language word only when ffVersion ≥ 4 (9.0: ≥ 1; the family's own
  language needs ffVersion ≥ 4 in both); the System's bitmap fonts were renumbered (Geneva
  9 and 12 are `NFNT` 1025 and 1026, Monaco 9 is 1027, the bitmaps unchanged);
- Appearance: utility window titles use the application's script font.

ClassicMac follows 9.0; a 9.2.2 mode would take these differences.

## Index

One file per format, in its category folder. A codec used by more than one format has its own file in `codecs/`.

### Containers

| Document | Formats | Code |
| --- | --- | --- |
| [unwrapping.md](containers/unwrapping.md) | The structures the single-file containers share (Finder information, dates, CRC-16); how nested containers are unwrapped: reader order, nesting depth, the expanded-bytes limit | `ClassicMac.Files` (`ContainerUnwrapper`, `ContainerReadOptions`) |
| [macbinary.md](containers/macbinary.md) | MacBinary I, II and III; writing MacBinary III | `ClassicMac.Files.Containers` |
| [binhex.md](containers/binhex.md) | BinHex 4.0; writing it | `ClassicMac.Files.Containers` |
| [applesingle-appledouble.md](containers/applesingle-appledouble.md) | AppleSingle and AppleDouble; writing them | `ClassicMac.Files.Containers` |
| [uuencode.md](containers/uuencode.md) | uuencode, historical and Base64 | `ClassicMac.Files.Containers` |
| [writing.md](containers/writing.md) | Saving edited resources back into the container they came from | `ClassicMac.Files.Editing` |
| [host-folders.md](containers/host-folders.md) | Mac files on other file systems: Basilisk II / SheepShaver folders, AppleDouble `._` files, macOS named forks; host names; what `unpack` writes | `ClassicMac.Files` (`HostFiles`), `ClassicMac.Files.Export` |

### Archives

| Document | Formats | Code |
| --- | --- | --- |
| [stuffit.md](archives/stuffit.md) | StuffIt 1.x–4.x (`SIT!`) | `ClassicMac.Files.Archives` |
| [stuffit5.md](archives/stuffit5.md) | StuffIt 5 | `ClassicMac.Files.Archives` |
| [stuffit-segments.md](archives/stuffit-segments.md) | StuffIt split files: SegmentIt (`$B056`) and StuffIt 1.5.1's segments (`$41A7`) | `ClassicMac.Files.Archives` |
| [packit.md](archives/packit.md) | PackIt: stored, Huffman and encrypted entries | `ClassicMac.Files.Archives` |
| [lha.md](archives/lha.md) | LHA and LArc, header levels 0–3 | `ClassicMac.Files.Archives` |
| [diskdoubler.md](archives/diskdoubler.md) | DiskDoubler archives (`DDA2`, `DDAR`) and standalone files; its split files (`SPLT`) | `ClassicMac.Files.Archives` |
| [compact-pro.md](archives/compact-pro.md) | Compact Pro, including segmented archives | `ClassicMac.Files.Archives` |
| [zip.md](archives/zip.md) | zip with AppleDouble `._`/`__MACOSX` entries and the Info-ZIP and ZipIt Mac extra fields | `ClassicMac.Files.Archives` |
| [tar-gzip.md](archives/tar-gzip.md) | tar and gzip with Mac data | `ClassicMac.Files.Archives` |
| [sea.md](archives/sea.md) | Self-extracting archives (`.sea`) | `ClassicMac.Files.Archives` |

### Disk images

| Document | Formats | Code |
| --- | --- | --- |
| [diskcopy42.md](disk-images/diskcopy42.md) | Disk Copy 4.2 | `ClassicMac.Files.Hfs` |
| [dart.md](disk-images/dart.md) | DART | `ClassicMac.Files.Hfs` |
| [ndif.md](disk-images/ndif.md) | NDIF (Disk Copy 6), self-mounting and segmented images | `ClassicMac.Files.Hfs` |
| [udif.md](disk-images/udif.md) | UDIF (`.dmg`) | `ClassicMac.Files.Hfs` |
| [raw-images.md](disk-images/raw-images.md) | Raw disk images (ShrinkWrap 2.1, DiskDup+ and others); how Disk Copy chooses a format by file type | `ClassicMac.Files.Hfs` |
| [cd-images.md](disk-images/cd-images.md) | Raw CD images, cue sheets and multisession discs | `ClassicMac.Files.Iso` |
| [rom.md](disk-images/rom.md) | Macintosh ROM images: the ROM resource table, combinations, the resources listed as a synthesised resource fork; the NewWorld "Mac OS ROM" file and its LZSS image | `ClassicMac.Files.Rom` |

### File systems

| Document | Formats | Code |
| --- | --- | --- |
| [partition-map.md](file-systems/partition-map.md) | Apple partition maps | `ClassicMac.Files.Hfs` |
| [mfs.md](file-systems/mfs.md) | MFS volumes | `ClassicMac.Files.Hfs` |
| [hfs.md](file-systems/hfs.md) | HFS volumes; finding a volume; conservative HFS writing | `ClassicMac.Files.Hfs` |
| [hfs-plus.md](file-systems/hfs-plus.md) | HFS Plus and HFSX | `ClassicMac.Files.Hfs` |
| [mbr.md](file-systems/mbr.md) | DOS partition tables | `ClassicMac.Files.Fat` |
| [fat.md](file-systems/fat.md) | FAT12/16/32 with long names | `ClassicMac.Files.Fat` |
| [pc-exchange.md](file-systems/pc-exchange.md) | PC Exchange and File Exchange data on FAT volumes and host folders: `RESOURCE.FRK`, `FINDER.DAT`, name conversion, the extension map | `ClassicMac.Files.Fat`, `ClassicMac.Files.Containers` (`PcExchange`, `ExtensionMap`) |
| [iso9660.md](file-systems/iso9660.md) | ISO 9660 and High Sierra as Mac OS 9 reads them, Apple's extensions | `ClassicMac.Files.Iso` |

### Resources

| Document | Formats | Code |
| --- | --- | --- |
| [resource-fork.md](resources/resource-fork.md) | Resource forks: header, map, attributes; how Mac OS 9 and the 68k ROM open and write them | `ClassicMac.Resources` |
| [compressed-resources.md](resources/compressed-resources.md) | Compressed resources (`dcmp` 0–3) | `ClassicMac.Resources` |
| [strings.md](resources/strings.md) | `STR `, `STR#` | `ClassicMac.Resources.Decoders.Text` |
| [styled-text.md](resources/styled-text.md) | Mac OS Roman, `TEXT` and `styl`, SimpleText documents | `ClassicMac.Resources.Decoders.Text` |
| [version.md](resources/version.md) | `vers` | `ClassicMac.Resources.Decoders.Text` |
| [menus.md](resources/menus.md) | `MENU`, `MBAR` | `ClassicMac.Resources.Decoders.Interface` |
| [windows-dialogs.md](resources/windows-dialogs.md) | `WIND`, `DLOG`, `ALRT`, positioning, their colour tables and Appearance extensions; the JSON output and the viewer's previews | `ClassicMac.Resources.Decoders.Interface` |
| [dialog-items.md](resources/dialog-items.md) | `DITL` | `ClassicMac.Resources.Decoders.Interface` |
| [controls.md](resources/controls.md) | `CNTL` | `ClassicMac.Resources.Decoders.Interface` |
| [bitmap-fonts.md](resources/bitmap-fonts.md) | `NFNT`/`FONT` strikes, `fctb`; the glyph sheet, BDF and JSON output | `ClassicMac.Graphics.Fonts`, `ClassicMac.Resources.Decoders.Fonts` |
| [font-families.md](resources/font-families.md) | `FOND` families; finding a family's strikes | `ClassicMac.Graphics.Fonts` |
| [outline-fonts.md](resources/outline-fonts.md) | `sfnt` | `ClassicMac.Graphics.Fonts` |
| [icons.md](resources/icons.md) | Icon resources (`ICON`, `ICN#`, `ics#`, `icm#`, `SICN`, `icl4`/`icl8` and the rest, `cicn`); writing icons and cursors | `ClassicMac.Resources.Decoders.Images` |
| [icon-families.md](resources/icon-families.md) | Drawing icon suites; icon families (`icns`, 48 × 48, 32-bit, 8-bit masks) | `ClassicMac.Resources.Decoders.Images` |
| [cursors.md](resources/cursors.md) | `CURS`, `crsr` | `ClassicMac.Resources.Decoders.Images` |
| [patterns.md](resources/patterns.md) | `PAT `, `PAT#`, `ppat`, `ppt#` | `ClassicMac.Resources.Decoders.Images` |
| [sound.md](resources/sound.md) | `snd ` resources; the WAV output | `ClassicMac.Resources.Decoders.Sound` |
| [palettes.md](resources/palettes.md) | `clut` colour tables and `pltt` palettes; the JSON and `.act` output | `ClassicMac.Resources.Decoders.Colors` |
| [templates.md](resources/templates.md) | `TMPL` resource templates, as ResEdit 2.1.3 reads and writes resources through them | `ClassicMac.Resources.Decoders.Templates` |
| [finder.md](resources/finder.md) | `BNDL`, `FREF`, `SIZE`; the JSON output | `ClassicMac.Resources.Decoders.Finder` |
| [documents.md](resources/documents.md) | DOCMaker stand-alone documents and SimpleText documents with pictures | `ClassicMac.Resources.Decoders.Documents` |

### Graphics

| Document | Formats | Code |
| --- | --- | --- |
| [pict.md](graphics/pict.md) | QuickDraw pictures (PICT v1, v2, extended v2): container, opcodes, operands, DrawPicture's play state; writing pictures | `ClassicMac.Graphics.Pict` |
| [quickdraw.md](graphics/quickdraw.md) | How QuickDraw draws (Mac OS 9 and the 68k ROM): shapes, patterns, transfer modes, CopyBits, bitmap text and the Font Manager, screen depths | `ClassicMac.Graphics.QuickDraw` |
| [quicktime.md](graphics/quicktime.md) | QuickTime still images in pictures, the codecs, QTIF files | `ClassicMac.Graphics.QuickTime` |
| [macpaint.md](graphics/macpaint.md) | MacPaint documents | `ClassicMac.Graphics` |

### Codecs

| Document | Formats | Code |
| --- | --- | --- |
| [adc.md](codecs/adc.md) | Apple Data Compression (NDIF, UDIF) | `ClassicMac.Files.Compression` |
| [kencode.md](codecs/kencode.md) | KenCode (NDIF; the System's `dcmp` 3) | `ClassicMac.Files.Compression` |
| [dart-rle.md](codecs/dart-rle.md) | DART RLE (DART, NDIF) | `ClassicMac.Files.Compression` |
| [lzhuf.md](codecs/lzhuf.md) | DART LZH, Okumura and Yoshizaki's LZHUF (DART, NDIF) | `ClassicMac.Files.Compression` |
| [bzip2.md](codecs/bzip2.md) | bzip2 (UDIF) | `ClassicMac.Files.Compression` |
| [packbits.md](codecs/packbits.md) | PackBits scan lines (PICT) | `ClassicMac.Graphics` |
| [compact-pro-rle-lzh.md](codecs/compact-pro-rle-lzh.md) | Compact Pro's RLE and LZH (Compact Pro, DiskDoubler method 8) | `ClassicMac.Files.Archives` |
| [stuffit-methods.md](codecs/stuffit-methods.md) | The compression methods StuffIt 1.x–4.x and StuffIt 5 share | `ClassicMac.Files.Archives` |
| [mace.md](codecs/mace.md) | MACE 3:1 and 6:1 | `ClassicMac.Resources.Decoders.Sound` |
| [ima4.md](codecs/ima4.md) | IMA 4:1 | `ClassicMac.Resources.Decoders.Sound` |
| [ulaw.md](codecs/ulaw.md) | µ-law | `ClassicMac.Resources.Decoders.Sound` |

### Output

| Document | Formats | Code |
| --- | --- | --- |
| [export-manifest.md](output/export-manifest.md) | What `extract` writes: folders, file names, `manifest.json` format 1.1, image outputs | `ClassicMac.Resources.Export`, `ClassicMac.Resources.Decoders.Images` |
| [text-output.md](output/text-output.md) | The text, JSON and RTF output of the text resources | `ClassicMac.Resources.Decoders.Text` |
| [html.md](output/html.md) | The HTML output of documents | `ClassicMac.Resources.Decoders.Documents` |

Pictures, icons, cursors and patterns are drawn by `ClassicMac.Graphics` (`.QuickDraw`, `.Pict`), which
[pict.md](graphics/pict.md) and [quickdraw.md](graphics/quickdraw.md) specify ([icons.md](resources/icons.md),
[icon-families.md](resources/icon-families.md), [cursors.md](resources/cursors.md) and
[patterns.md](resources/patterns.md) for icons, cursors and patterns). [export-manifest.md](output/export-manifest.md)
covers what ClassicMac adds: masks, cursor JSON and numbered list outputs.

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

Every rule carries one of these tags, written in plain square brackets with an optional detail after a colon
(`[Code: Mac OS 9.0 IconUtils]`, `[Verified: Compact Pro 1.52]`):

| Tag | Meaning |
| --- | --- |
| [Doc] | Apple's documentation: *Inside Macintosh* (named volume), a Technical Note, a Developer Note, or a standard (ECMA-119 for ISO 9660, Microsoft's FAT specification) |
| [Code] | Apple's own code, traced in disassembly, with the software and version named (the Mac OS 9.0 System file, the 68k ROM, Disk Copy 6.3.3, Sound Manager 3.5.1, File Exchange 3.0, …) |
| [Verified] | Checked against the real software or its output: running in an emulator (SheepShaver or Basilisk II, Mac OS 9.0 unless stated), or files made by the original application |
| [Author] | The published specification of a format's author, for formats Apple did not define (MacBinary, BinHex, LZHUF) |
| [Fitted] | Fitted to real files, with no code traced: the weakest source, and marked so it can be replaced |
| [Reference] | Taken from another implementation (named in the detail) and not yet confirmed by code or by the original application's files |

What ClassicMac itself decides (its output formats, names, limits, severities, and how it recovers where the Mac would
crash or read stray memory) is design, not format. It is marked [ClassicMac], or said in a sentence that begins
"ClassicMac …", and carries no source tag.

Where Apple's implementations disagree (Mac OS 9 and the 68k ROM, Disk Copy versions, PowerPC and 68k code), each
document says which one ClassicMac follows by default, and what the others do.

Other projects that handle these formats are behavioural references only. GPL and LGPL code is never a source for
ClassicMac.

## Diagnostics

ClassicMac reads damaged files as far as it can. It reports each problem as a diagnostic with a severity (Info,
Warning, Error) and a stable code (`fork.map-length`, `ndif.bad-checksum`, …), and throws only when a file cannot be
read at all. Each document lists its codes in one table: `Code | Severity | When | ClassicMac does | The Mac does`.

## Keeping them current

A change to how ClassicMac reads or writes a format updates its document in the same commit. The authoring rules are
in [CLAUDE.md](CLAUDE.md), the skeleton in [TEMPLATE.md](TEMPLATE.md).
