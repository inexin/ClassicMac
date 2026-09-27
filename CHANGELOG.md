# Changelog

## Unreleased

- Repository layout, build settings and CI.
- Packages: `ClassicMac.Core` (`FourCC`, `MacString`, `MacDate`, `MacPoint`, `MacRect`, `Fixed`, `UnsignedFixed`,
  diagnostics), `ClassicMac.Files` (`MacFile`,
  `FinderInfo`, `ForkData`, `IContainerReader`, `ContainerReadOptions`) and `ClassicMac.Resources` (`Resource`,
  `ResourceFork`, `ReadOptions`), each with its own test project.
- Resource fork reader (damage reported as diagnostics) and canonical writer.
- Compressed resources: `ResourceDecompression` with System `dcmp` 0, 1, 2 (from disassembly) and 3 (behavioural);
  Mac OS 9 or 68k ROM Resource Manager behaviour via `ReadOptions.ResourceManager`.
- `classicmac` CLI: `list` for raw resource forks (text or JSON); `info` and `extract` parsed, not yet implemented;
  limit options, exit codes.
