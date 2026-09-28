# Changelog

## Unreleased

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
