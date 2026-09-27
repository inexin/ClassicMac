# Changelog

## Unreleased

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
- NDIF disk images (Disk Copy 6: read-only, ADC-compressed, `.smi`, segmented) with ADC in
  `ClassicMac.Files.Compression`; container readers can see the whole file (resource fork) and sibling files;
  `ClassicMac.Files` references `ClassicMac.Resources`.
- CD volumes in `ClassicMac.Files.Iso`: ISO 9660 and High Sierra as Mac OS 9 reads them (Apple `AA`/`BA` Finder
  info, associated files as resource forks, the Mac's name, date and listing rules), matching OS 9 on a test disc.
- `classicmac` CLI: `info` and `list` read through containers and disk images, showing Mac paths (text or JSON);
  `extract` parsed, not yet implemented;
  limit options, exit codes.
