# ClassicMac format documentation

Implementer's specifications of the formats ClassicMac reads and writes. Each one describes its format completely
enough to write a reader (and, where ClassicMac writes the format, a writer) without reading ClassicMac's code, and
says where every rule comes from. They describe what classic Mac OS does: where Apple's documentation is silent, the
behaviour of Apple's own code decides, as traced in disassembly and checked on real software in an emulator.

| Document | Formats | Code |
| --- | --- | --- |
| [RESOURCE-FORK.md](RESOURCE-FORK.md) | Resource forks: header, map, attributes; how Mac OS 9 and the 68k ROM open them; compressed resources (`dcmp` 0–3) | `ClassicMac.Resources` |
| [CONTAINERS.md](CONTAINERS.md) | MacBinary I/II/III, BinHex 4.0, AppleSingle and AppleDouble | `ClassicMac.Files.Containers` |
| [HOST-FOLDERS.md](HOST-FOLDERS.md) | Mac files on other file systems: Basilisk II / SheepShaver folders, AppleDouble `._` files, PC Exchange, File Exchange names; what `unpack` writes | `ClassicMac.Files` (`HostFiles`), `ClassicMac.Files.Export` |
| [HFS-MFS.md](HFS-MFS.md) | Apple partition maps, MFS and HFS volumes | `ClassicMac.Files.Hfs` |
| [DISK-IMAGES.md](DISK-IMAGES.md) | Disk Copy 4.2, DART, NDIF (Disk Copy 6), ShrinkWrap, UDIF; the ADC, KenCode, LZH and bzip2 codecs | `ClassicMac.Files.Hfs`, `ClassicMac.Files.Compression` |
| [FAT.md](FAT.md) | DOS partition tables, FAT12/16/32 with long names, PC Exchange and File Exchange data on FAT | `ClassicMac.Files.Fat` |
| [ISO9660.md](ISO9660.md) | ISO 9660 and High Sierra as Mac OS 9 reads them, Apple's extensions, raw CD images and cue sheets | `ClassicMac.Files.Iso` |
| [SOUND.md](SOUND.md) | `snd ` resources; MACE 3:1 and 6:1, IMA 4:1, µ-law; the WAV output | `ClassicMac.Resources.Decoders.Sound` |
| [TEXT.md](TEXT.md) | `STR `, `STR#`, `TEXT` and `styl`, `vers`; the text, RTF and JSON output | `ClassicMac.Resources.Decoders.Text` |
| [EXPORT-MANIFEST.md](EXPORT-MANIFEST.md) | What `extract` writes: folders, file names, `manifest.json` format 1.1, image outputs | `ClassicMac.Resources.Export`, `ClassicMac.Resources.Decoders.Images` |

Pictures, icons, cursors and patterns are drawn by [QuickDraw.Pict](https://github.com/inexin/QuickDraw.Pict), whose
`docs/PICT-FORMAT.md` specifies them (section 18 for icons, cursors and patterns).
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
