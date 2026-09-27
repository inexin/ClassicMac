# Changelog

## Unreleased

- Repository layout, build settings and CI.
- Packages: `ClassicMac.Core` (`FourCC`, `MacString`, `MacDate`, `MacPoint`, `MacRect`, `Fixed`, `UnsignedFixed`,
  diagnostics), `ClassicMac.Files` (`MacFile`, `FinderInfo`, `ForkData`, `IContainerReader`, `ContainerReadOptions`)
  and `ClassicMac.Resources` (`Resource`, `ResourceFork`, `ReadOptions`), each with its own test project.
- Resource fork reader (damage reported as diagnostics) and canonical writer; map attributes (`mAttr`) and map
  flags (`mInMemoryAttr`) kept as separate bytes.
- Compressed resources: `ResourceDecompression` with System `dcmp` 0–3 (from disassembly), including dcmp 3's
  overshoot into the memory after the block; Mac OS 9 or 68k ROM Resource Manager behaviour via
  `ReadOptions.ResourceManager`.
- Containers in `ClassicMac.Files`: AppleSingle/AppleDouble (v1, v2), MacBinary I/II/III, BinHex 4.0; host files
  with Basilisk II `.rsrc`/`.finf`, AppleDouble `._` files and macOS named forks; `ContainerUnwrapper` for nesting;
  lazy `ForkData` slices. `MacRoman` in Core.
- `classicmac` CLI: `info` and `list` read through containers (text or JSON); `extract` parsed, not yet implemented;
  limit options, exit codes.
