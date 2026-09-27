# Changelog

## Unreleased

- Repository layout, build settings and CI.
- Core model: `FourCC`, `MacString`, `MacDate`, `FinderInfo`, `MacFile`, `Resource`, `ResourceFork`, diagnostics,
  `ReadOptions` and `IContainerReader`.
- Resource fork reader (damage reported as diagnostics) and canonical writer.
- Compressed resources: `ResourceDecompression` with System `dcmp` 0, 1, 2 (from disassembly) and 3 (behavioural);
  Mac OS 9 or 68k ROM Resource Manager behaviour via `ReadOptions.ResourceManager`.
- `classicmac` CLI: `list` for raw resource forks (text or JSON); `info` and `extract` parsed, not yet implemented;
  limit options, exit codes.
