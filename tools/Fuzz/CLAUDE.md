# CLAUDE.md: tools/Fuzz

Guidance for the fuzz targets. How to run them is in `README.md`; the repository-wide rules are in the root `CLAUDE.md`.

- Reader targets are in `FuzzTargets.cs`, writer targets in `WriterTargets.cs`. A new target goes in `All`, the
  seeds in `FuzzSeeds.cs`, the matrix of `.github/workflows/fuzz.yml`, the table in `README.md` and the fuzzing item
  of `docs/PLAN.md`.
- **No instrumented code in static initialisers**: libFuzzer sets up its coverage map only when the target starts, so
  anything built from ClassicMac's assemblies (options, decoders, models) is `Lazy<T>`, made on first use.
- Targets are strict: only `InvalidDataException` and `EndOfStreamException` (and what an API documents) may escape a
  reader, and a fault a reader or decoder reports (`*-fault`) counts as a crash. A writer target that sees its own
  output refused, or read back differently from what it meant to write, throws.
- Writer targets take their choices from the input through `FuzzReader` (zeros once it runs out), so every input is a
  valid script and the corpus stays small.
- The test builders (`HfsPlusBuilder`, `PefBuilder`, `CodeFixtures`) are linked from the test projects, not copied.
- A crash found is reproduced with `Fuzz replay`, fixed test-first, and its input becomes a test in the project that
  owns the code (as bytes or a builder, never a committed real file).
