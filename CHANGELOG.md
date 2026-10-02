# Changelog

## Unreleased

- Files: a container's files are probed for formats in parallel, all probes of a file sharing one read of its head and
  tail: opening a 500 MB disk of 5,170 files takes 0.4 s, and unwrapping everything on it (`list`) 2.4 s instead of 11 s.
- StuffIt: archives decode about ten times faster: forks decode in parallel, method 15 (Arsenic) writes into arrays
  and checks its CRC-32 from a table, and the fork CRC-16 is table-driven (a 38 MB archive of 1,826 files: 9.3 s to
  0.8 s).
- Viewer: opening a disk reads its files, not what is inside them: archives and disk images on it are read when their
  node is first expanded (a 500 MB Mac OS 9 disk opens in under a second instead of 22 s); exports still read
  everything. Problems found inside a nested file name it (`disk.hfv › Disks:Tools.img`).
- CLI: a diagnostic from inside a nested file names it after the input (`disk.hfv > Disks:Tools.img: …`).
- Files: `ForkData.ReadAt` reads without opening a stream or copying, and a host file stays open between reads, so
  probing a disk's files for containers is about six times faster. `ContainerUnwrapper.Unwrap(…, levels)` and
  `Expand` read containers a level at a time; `Diagnostic.Location` names the nested file a problem is in.
- Host folders: a file's own Basilisk II (`.rsrc`/`.finf`) or AppleDouble companions win over a `FINDER.DAT` or
  `RESOURCE.FRK` in its folder, so an unpacked Mac folder that holds a `FINDER.DAT` reads back as written
  (containers/host-folders.md §2.1).
- Code: a new package, `ClassicMac.Code`, reads classic Mac code: PEF containers (sections, pattern-initialized data,
  the loader's imports, relocations and export hash, transition vectors, traceback tables) and `'cfrg'`
  (`.Ppc`); 68k applications (`'CODE'` 0 and the jump table, near and far segments, the entry point and the bootstrap
  shape, MPW `%A5Init`, CodeWarrior `'DATA'` 0, Retro68 `'RELA'`) and code resources (the standard header, `DRVR`, the
  `$A9FF` package form, components, routine descriptors) (`.M68k`); and disassembles them (`.Disassembly`): the 68k and
  PowerPC disassemblers ported from resource_dasm, trap, selector and low-memory names from Multiversal Interfaces,
  MacsBug names, and annotated listings with a function and reference model (code/*.md, output/disassembly.md).
- Extract: `CODE`, `cfrg` and code resources (native `ncod`, `nlib`, `ndrv`, …; 68k `CDEF`, `WDEF`, `DRVR`, `PACK`,
  `INIT`, `XCMD`, …) are decoded: the data stays the main file (`.bin`), with the listing (`.s`) and the model
  (`.json`) beside it; `CODE` 0 and `cfrg` give JSON (output/disassembly.md, output/export-manifest.md §3.8).
- Pack: a decoded resource whose main file is `.bin` packs back from that file, as a raw resource does, so decoded
  code round-trips without `--keep-raw` (output/export-manifest.md §2.2).
- CLI: `disasm <input>` writes a listing per 68k segment, code resource and fragment (the data-fork fragments the
  `'cfrg'` resources name included) and `code.json`, for every Mac file inside the input; `--cpu 68k|ppc|both`
  (output/disassembly.md §3.2).
- Viewer: a code resource's preview is its listing.
- Docs: the format documents are in category folders under `docs/formats/` (`containers/`, `archives/`,
  `disk-images/`, `file-systems/`, `resources/`, `graphics/`, `codecs/`, `output/`), one file per format.
- Interface previews: dialogs and alerts are drawn as Mac OS 9 with Appearance (Platinum) draws them, frames and
  controls pixel for pixel; text through the fonts in open files (resources/windows-dialogs.md §2.4).
- Icons: Mac OS 9's 8-bit icon masks (`l8mk`, `s8mk`, `h8mk`) in icon suites at 8 bits and more (resources/icon-families.md).
- Inputs: Mac ROM images (the ROM's resource table, and the New World `Mac OS ROM` file; disk-images/rom.md); `.sea`
  self-extracting archives through their data fork; StuffIt 1.5.1 segment sets.
- Pixel patterns: a colour table's size is signed, so ResEdit's `ppat`s with an empty table load and draw.
- Inputs: uuencode (`.uu`, `begin-base64`); zip (stored, DEFLATE, ZIP64) and tar/gzip with Mac data: AppleDouble `._`
  and `__MACOSX/` pairing, Info-ZIP and ZipIt Mac extra fields (archives/zip.md, archives/tar-gzip.md, containers/uuencode.md).
- CD images: multisession discs are read by their last session's descriptors, as the Mac reads them, from cue sheets
  and whole-disc raw images (disk-images/cd-images.md §2.2 and §5.3).
- HFS: catalog keys whose length leaves out the pad byte, as Mac OS writes them, are no longer rejected; most files on
  Mac-written volumes were missing from listings (file-systems/hfs.md §1.9).
- Editor III: Volume ▸ New File, Import File, New Folder and Delete on plain HFS images, saved with Save As.
- Viewer: files and resources drag out to the desktop (files as data fork plus AppleDouble, or MacBinary).
- Core: `BigEndianReader` is a class over `ReadOnlyMemory<byte>`, with a constructor that reads a stream from its
  current position to its end; readers are passed without `ref` and can be fields. Every big-endian read in the
  libraries goes through it, and the methods that read with it take `ReadOnlyMemory<byte>` instead of
  `ReadOnlySpan<byte>`.
- Core: `BigEndianWriter` is a class that grows as it is written, like a `StringBuilder` for bytes, with `Write…At`
  to patch a length or offset written earlier, `ToArray` and `WriteTo(Stream)`. `BigEndianStreamWriter` is gone;
  `PictWriter` builds its output with `BigEndianWriter`.
- Graphics: PICT pictures, QuickTime image files and MacPaint documents decode from streams, seekable or not, from the
  current position to the end, leaving the stream open. `QuickTimeImageFile.Read(Stream)` returns a
  `QuickTimeImageResult` with the description and ICC profile; `PictSkia.DecodeAny(Stream)`. The ImageSharp decoders
  read their streams directly.
- Edit tab: "Edit with template" shows a resource that has a typed form through its `TMPL` instead, when one is at hand.
- Hex editing in the hex view: Edit Bytes types hex digits over or into a resource's bytes, with Delete, Backspace and
  cursor keys, applied as one undoable edit.
- Resource forks: a map offset past the end of the fork is recovered from the end of the data area, as ResEdit does
  (`fork.map-recovered`; resources/resource-fork.md §5.3). A damaged fork opened this way saves as a clean one.
- Templates: resources without a form of their own are edited through a `TMPL` from their file or any other open
  file, as ResEdit 2.1.3 reads and writes them; `ResourceTemplate` in the decoders library (resources/templates.md).
- Editor II, import: Resource ▸ Import Image or Sound makes a `PICT`, `cicn`, icon, icon family or cursor from an image
  (PNG, JPEG, …) and a `snd ` from a WAV file, as an undoable edit; `ImageImport` and `SoundImport` in the decoders
  library (resources/icons.md, graphics/pict.md §3, resources/sound.md §3).
- Editor II, UI templates: forms for `DLOG`, `ALRT`, `WIND`, `DITL`, `MENU` and `CNTL` in the Edit tab, the dialog or
  menu preview beside them redrawn as they change; `InterfaceWriter` writes the templates (the Writing sections of resources/windows-dialogs.md, menus.md and dialog-items.md).
- Editor II, first part: an Edit tab with forms for `STR `, `STR#`, `TEXT` (its `styl` runs kept in step) and `vers`,
  applied as undoable edits; the library writes them (`TextResources`, `VersionResource`; the Writing sections of resources/strings.md, styled-text.md and version.md).
- The viewer shows an icon's suite as the Finder draws it: each size, plain, selected, disabled, offline and open, and
  the label colours, at the chosen screen depth.
- Editing (Editor I): the app adds, duplicates, deletes and renumbers resources, changes their names and attributes,
  edits their bytes as hex or replaces them from a file, with undo and redo, and saves back into the file (raw fork,
  AppleDouble, Basilisk II, MacBinary as III, BinHex, AppleSingle), verified, keeping the original as `.orig`; Save As
  writes any of those forms. Ctrl+S is Save; Save Resource As moved to Ctrl+E. Library: `ClassicMac.Resources.Editing`
  (`EditSession`) and `ClassicMac.Files.Editing` (`ForkSaver`).
- `IconSuite.Plot` draws icon suites into a `QuickDrawPort` as PlotIconSuite/PlotIconID do: member choice by rect
  and depth, alignment, the selected/disabled/offline/open transforms and labels, for Mac OS 9 or the ROM.
  `QuickDrawPort.CopyMask` and `Region.FromBitMap` (BitMapToRegion) are new.
- Icon families: `IconFamily` reads `icns` resources as Mac OS 9's Icon Services does (all 20 members: 48 × 48,
  32-bit with its RLE, 8-bit masks) and builds families from classic icon resources; the `image.icon-family` decoder
  exports each member through the mask Icon Services picks for its size.
- `QuickDrawPort`: `SetOrigin`, `HidePen`/`ShowPen`, `CharExtra`, `TextWidth`/`StringWidth`/`CharWidth`, `GetFontInfo`,
  and `DrawPicture` onto a port (saving and restoring its state, clipped to nothing until the picture's ClipRgn, as
  the Mac does). Pictures now start with the pen ScalePt((1, 1)) to the destination, as DrawPicture does.
- The PICT specification is split into `docs/formats/graphics/pict.md` (the format and playback), `quickdraw.md` (the
  drawing rules), `quicktime.md`, `macpaint.md` and the icon documents in `docs/formats/resources/`.
- `QuickDrawPort`: the renderer as a public colour QuickDraw port (shapes, lines, regions, patterns, CopyBits, text)
  with QuickDraw's names, drawing exactly what the picture player draws, which now runs on it. Public `RgbColor`,
  `TransferMode`, `QuickDrawStyle`, `QuickDrawPattern`, `QuickDrawOptions`, `Region` and `PixMap`
  (`docs/QUICKDRAW-API.md`, since removed).
- Renamed for the public drawing API (`docs/QUICKDRAW-API.md`, since removed): `PictBitmap` → `RgbaBitmap`,
  `PictColor` → `RgbaColor`, `PictQuickDraw` → `QuickDrawVersion`, `PictFontLibrary` → `FontLibrary`,
  `IPictTextFallback` → `ITextFallback` (with `TextFallbackMask`, `TextFallbackStyle`); the last four are in
  `ClassicMac.Graphics.QuickDraw`.
- The QuickDraw renderer reads fonts with `ClassicMac.Graphics.Fonts` instead of its own parser. In Mac OS 9 mode a
  strike is now read by Mac OS 9's rules (depth in fontType bits 2–3, rowWords' top bit ignored, the location table
  just before the offset/width table); only damaged or unusual fonts draw differently. A strike whose characters do not
  run within 0–255 is treated as missing.
- The graphics package: QuickDraw.Pict becomes `ClassicMac.Graphics`, one package in layers with a namespace each
  (`ClassicMac.Graphics`, `.Fonts`, `.QuickTime`, `.QuickDraw`, `.Pict`), plus `ClassicMac.Graphics.ImageSharp` and
  `ClassicMac.Graphics.SkiaSharp`. `PictInfo`'s frame and bounds are Core's `MacRect`. `PictBitmap.Info` is gone: `PictReader.Read` returns the bitmap with its `PictInfo`. `QuickDrawResources` is part of
  `ClassicMac.Resources.Decoders`. Usage in `docs/GRAPHICS.md`.
- QuickDraw.Pict moved into this repository with its history: the picture renderer and PICT reader/writer
  (`src/QuickDraw.Pict`), its ImageSharp and SkiaSharp packages, tests and golden tools. The decoders use it directly
  instead of the NuGet package, so the Origin fix (below) reaches them; its spec is `docs/formats/PICT-FORMAT.md`.
- Fonts (`ClassicMac.Graphics.Fonts`): bitmap strikes (`NFNT`, `FONT`), families (`FOND`) with their width,
  kerning and style-mapping tables, font colour tables and TrueType `sfnt` data. Font decoders: strikes to a glyph
  sheet PNG, BDF and metrics JSON; families and font colours to JSON; `sfnt` to `.ttf`. Specified in
  `docs/formats/resources/bitmap-fonts.md`, `font-families.md` and `outline-fonts.md`.
- `pack`: an export folder back into a resource fork, raw or in an AppleDouble, AppleSingle, MacBinary III or
  BinHex 4.0 file (new writers for the last three); unchanged resources come back byte for byte from `raw/` or a
  base fork. The corpus test now packs every export back.
- Finder resources (`BNDL`, `FREF`, `SIZE`) to JSON, a bundle with each file type's icon, specified in
  `docs/formats/resources/finder.md`.
- Colour tables (`clut`) and palettes (`pltt`) to JSON and Adobe `.act`, specified in `docs/formats/resources/palettes.md`;
  the viewer shows them as swatches.
- Interface resources to JSON: menus and menu bars, window, dialog and alert templates, dialog item lists and
  control templates, and their colour tables and Appearance extensions (`ui.*` decoders), specified in
  `docs/formats/resources/` (`menus.md`, `windows-dialogs.md`, `dialog-items.md`, `controls.md`). The viewer draws
  dialogs, alerts, item lists and menus in the System 7 style.
- DOCMaker stand-alone documents and SimpleText documents with pictures: read into a styled-document model
  (`StyledDocuments`) and converted to an HTML folder (`HtmlDocuments`: a page per chapter, a contents page, the
  pictures as PNG reflowed into the text, picture actions as links). Specified in
  `docs/formats/resources/documents.md` and `docs/formats/output/html.md`.
  `extract` adds a document's HTML folder as `document/` (manifest format 1.2: a `document` field; `--no-documents`
  leaves it out); the new `convert` command writes only the documents. Document converters plug in through
  `IDocumentConverter` and `ExportOptions.Documents`. The viewer previews documents a chapter at a time with the same
  reflow (picture links followed), has Export ▸ Convert Documents, and includes `document/` in its resource exports.
- UDIF disk images (`.dmg`): Disk Copy 6.4/6.5's (block tables in an embedded resource fork) and Mac OS X's (XML
  property list), with zeros, raw, ADC, zlib and bzip2 runs (a bzip2 decoder in `ClassicMac.Files.Compression`) and
  CRC-32/MD5 checksums checked with `--verify`; encrypted and segmented images refused. NDIF and UDIF share a
  chunked-disk reader.
- Phase 3 exit check: golden outputs for every decoder (fixtures made in code; text outputs as files, images and
  sounds as hashes; the export manifest pinned) and a corpus export test with a committed baseline of counts and
  output hashes; `CLASSICMAC_CORPUS` takes several folders. A resource fork whose map gives over 1,000 errors (another
  format read as a fork) is refused instead of read slowly.
- Viewer app (`src/ClassicMac.App`, Avalonia): open files, disk images and resource forks, browse them down to
  individual resources, see details and diagnostics; preview images, styled text, strings and version resources,
  sounds (waveform and playback through SoundFlow), and any fork or resource in hex; export a resource, a file's resources, everything under a node, or unpack it
  (AppleDouble, Basilisk II) into new folders. `MacFileResources` and `ClassicMac.Files.Export` (`Unpacker`,
  `OutputLayout`, `ExportFolders`) in Files, shared with the CLI; `StyledText` in Decoders.

- Repository layout, build settings and CI.
- Packages: `ClassicMac.Core` (`FourCC`, `MacString`, `MacDate`, `MacPoint`, `MacRect`, `Fixed`, `UnsignedFixed`,
  diagnostics), `ClassicMac.Files` (`MacFile`, `FinderInfo`, `ForkData`, `IContainerReader`, `ContainerReadOptions`)
  and `ClassicMac.Resources` (`Resource`, `ResourceFork`, `ReadOptions`), each with its own test project.
- Resource fork reader (damage reported as diagnostics; whether Mac OS 9 or the ROM would open the fork) and a
  writer that lays forks out as the Resource Manager's compaction does; map attributes (`mAttr`) and map
  flags (`mInMemoryAttr`) kept as separate bytes.
- Compressed resources: `ResourceDecompression` with System `dcmp` 0–3 (from disassembly), including dcmp 3's
  overshoot into the memory after the block; Mac OS 9 or 68k ROM Resource Manager behaviour via
  `ReadOptions.ResourceManager`.
- Containers in `ClassicMac.Files`: AppleSingle/AppleDouble (v1, v2), MacBinary I/II/III, BinHex 4.0; host files
  with PC Exchange `RESOURCE.FRK`/`FINDER.DAT`, Basilisk II `.rsrc`/`.finf`, AppleDouble `._` files and macOS named
  forks; `ContainerUnwrapper` for nesting;
  lazy `ForkData` slices. `MacRoman` in Core.
- Disk images in `ClassicMac.Files.Hfs`: HFS and MFS volumes (files with folder paths, forks read in place through
  their extents), Disk Copy 4.2 and Apple partition maps; `MacFile.FolderPath` and `MacPath`. The file layer is one
  package: single-file readers moved to `ClassicMac.Files.Containers`.
- FAT volumes in `ClassicMac.Files.Fat`: FAT12/16/32 with long names, and the PC Exchange / File Exchange data
  on them (Mac names, Finder info, dates, resource forks), matching what OS 9 lists; DOS partition tables.
  `DosTime` and `PcExchange.Apply` shared with host folders. Long names converted as File Exchange converts them;
  an opt-in `ExtensionMap` for placeholder types.
- DART images ("fast" RLE, "best" LZH, stored; checksums checked), verified on DART 1.5.3's own files; LZH also
  decodes NDIF chunk type $82, and KenCode ($80, Disk Copy's "Smaller (KC)") is decoded too.
- Image decoders (`ClassicMac.Resources.Decoders.Images`, through QuickDraw.Pict): `PICT`, icons (`ICON`, `ICN#`
  and the colour icon families masked by their lists, `cicn`, `SICN`), cursors (PNG + JSON), patterns; written as
  PNG by a built-in encoder behind `IImageEncoder`; `extract --screen-depth`.
- Sound decoder (`ClassicMac.Resources.Decoders.Sound`): `snd ` formats 1 and 2 with standard, extended and
  compressed headers, uncompressed PCM to WAV (loops and base note in `smpl`) plus a JSON of the exact rate, header and
  commands; `SoundResource` model. MACE 3:1/6:1, IMA4 and µ-law decoded as the Mac OS 9 Sound Manager decodes them
  (disassembly; byte-identical to its output); format 2 headers found as SndPlay finds them.
- NDIF disk images (Disk Copy 6: read-only, ADC-compressed, DART RLE chunks, map versions 10–12 (Disk Copy 6.1–6.5), `.smi`, segmented
  parts found by their `bcm#` ID, CRC-32 verified on request) with ADC in
  `ClassicMac.Files.Compression`; container readers can see the whole file (resource fork) and sibling files;
  `ClassicMac.Files` references `ClassicMac.Resources`.
  Version 2 maps (Disk Image Mounter, Disk Copy 6.0.1) read as Disk Copy 6.1.2's driver reads them, with a request
  for real samples. CLI `--verify` checks disk image checksums.
- Raw CD images (`.bin`, 2352/2336-byte sectors) and cue sheets, read as the 2048-byte blocks a drive hands the Mac.
- CD volumes in `ClassicMac.Files.Iso`: ISO 9660 and High Sierra as Mac OS 9 reads them (Apple `AA`/`BA` Finder
  info, associated files as resource forks, the Mac's name, date and listing rules), matching OS 9 on a test disc.
- Writing Mac files to the host: `HostFiles.Write` (AppleDouble or Basilisk II layout, `HostWriteOptions`),
  `AppleDoubleWriter`, `HostNames`, `FinderInfo.Write`; `classicmac unpack` writes every file inside an input
  (through containers and disk images) to a folder. Basilisk II folder names follow SheepShaver (Windows-1252
  bytes, its escape set), checked in the emulator.
- `classicmac` CLI: `info` and `list` read through containers and disk images, showing Mac paths (text or JSON);
  `extract` decodes text resources by default (`STR `/`TEXT` → UTF-8, `STR#`/`styl`/`vers` → JSON, `TEXT` + `styl`
  → RTF; `ClassicMac.Resources.Decoders`, `--raw` for the data itself) and writes every resource into type folders
  with a `manifest.json` (format 1.1, with a JSON
  Schema), a folder per file for disk images; `--keep-raw` keeps the stored bytes; `HostNames` moved to Core;
  limit options, exit codes.

## QuickDraw.Pict (before it moved here)

### Unreleased (before the move)

**QuickDraw.Pict**
- Fixed: the Origin opcode ($000C) reads dh before dv, as Mac OS 9.0's DrawPicture does; pictures that move their
  origin (DOCMaker's, for one) drew mostly outside their frame.

**QuickDraw.Pict.SkiaSharp** (new)
- Decode pictures, QTIF files and MacPaint documents to `SKBitmap`/`SKImage` (`PictSkia.Decode`, `DecodeImage`,
  `DecodeAny`), with the same options as the ImageSharp decoder.
- Encode `SKBitmap`/`SKPixmap` as PICT (`PictSkia.Encode`, `SaveAsPict`).
- JPEG, PNG, GIF, WebP and BMP QuickTime images through Skia's codecs; outline-font text fallback through Skia.

### 0.1.0 — 2026-09-27

First release.

**QuickDraw.Pict**
- Reads PICT version 1, version 2 and extended version 2 pictures, bare or with a 512-byte file header. It draws them
  with a software QuickDraw engine to an RGBA `PictBitmap`. The engine covers:
  - shapes, regions, patterns, pen modes and transfer modes;
  - CopyBits scaling and masks;
  - bitmap-font and color-bitmap-font text through a Font Manager model;
  - QuickTime images: raw, Animation, Road Pizza, Graphics, Cinepak, 8BPS, YUV2, YVU9, Targa and MacPaint.
- Two QuickDraw models: Mac OS 9 (default) and the 68k ROM.
- `ScreenDepth` draws a picture as it looks on a 1, 2, 4, 8 or 16-bit screen.
- `PictWriter` writes 1/2/4/8-bit indexed, 16-bit and 32-bit pictures with resolution and an ICC profile.
- `PictInfo` gives the header, resolution, comments and ICC profile.
- `QuickTimeImageFile` (QTIF) and `MacPaintFile` (PNTG).
- `QuickDrawResources` decodes icons, cursors and patterns from resource data.

**QuickDraw.Pict.ImageSharp**
- ImageSharp format plugin: PICT detection, decoding and encoding (`SaveAsPict`).
- It also loads QTIF and MacPaint files.
- JPEG, PNG, GIF, TIFF, WebP and BMP images inside QuickTime pictures are decoded through ImageSharp.
- Outline-font fallback for text without bitmap fonts.
