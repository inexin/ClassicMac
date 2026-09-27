# Changelog

## Unreleased

- Repository layout, build settings and CI.
- Core model: `FourCC`, `MacString`, `MacDate`, `FinderInfo`, `MacFile`, `Resource`, `ResourceFork`, diagnostics,
  `ReadOptions` and `IContainerReader`.
- Resource fork reader (damage reported as diagnostics) and canonical writer.
- `classicmac` CLI: `list` for raw resource forks (text or JSON); `info` and `extract` parsed, not yet implemented;
  limit options, exit codes.
