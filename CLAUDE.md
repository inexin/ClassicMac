# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

ClassicMac reads, converts and edits classic Mac OS files in .NET: resource forks and the containers they travel in,
resource decoders, a CLI and an Avalonia desktop app. The project is in development; `docs/PLAN.md` is the plan
and the source of decisions (architecture, packages, phases, open questions). Read it before starting work, and update
it in the same change when a decision changes.

## Ground truth

- Apple's documentation (*Inside Macintosh*, Technical Notes, file-format notes) is the specification.
- Where it is silent or ambiguous, the answer comes from the Mac OS code that handles the format (disassembly),
  never from another implementation's guess. Other projects (see the plan's prior-art tables) are behavioural
  references only.
- A rule fitted to real data rather than read from code is marked as such in code comments and docs.
- The resource extractor in the Realmz project is inspiration, not authority; re-check anything taken from it. Realmz
  data files are a test corpus, but ClassicMac is standalone and never depends on Realmz.

## Licensing

- MIT. Code ported from MIT projects keeps a notice in `THIRD-PARTY-NOTICES.md`; GPL/LGPL projects are reference only.
- Never commit Apple source code, Apple fonts or other Apple files, or private test harnesses.

## Per-project guidance

Each of these has its own `CLAUDE.md`; read it before working there.

| Where | Covers |
| --- | --- |
| `src/ClassicMac.Graphics/CLAUDE.md` | QuickDraw and PICT: the specs, the two QuickDraws, integer exactness, golden pictures, the layers and the decoding pipeline (also for the adapters, the graphics tests and `Resources.Decoders/Images`) |
| `src/ClassicMac.Code/CLAUDE.md` | PEF, 68k code and the disassemblers, and their corpus tests |
| `src/ClassicMac.App/CLAUDE.md` | The Avalonia app: views and view models, screenshot baselines, the icon |
| `tools/Fuzz/CLAUDE.md` | The fuzz targets |
| `docs/formats/CLAUDE.md` | Writing the format specs |

Each project also has a `README.md` (its NuGet page for packages; `src/ClassicMac.Formats` is the package that carries Resources,
Files, Code and Resources.Decoders); update it when what the project offers changes.

## Reading and writing binary data (ClassicMac.Core)

Every big-endian read goes through `BigEndianReader` and every big-endian write through `BigEndianWriter`. Outside
Core, `BinaryPrimitives` is used only for little-endian data (FAT, ISO's both-endian fields, WAV, LHA, zip).

**`BigEndianReader`**: a class over `ReadOnlyMemory<byte>`, so it is passed without `ref`, can be a field and works in
iterators and lambdas.
- `new BigEndianReader(bytes)` wraps the bytes without copying (a `byte[]` converts implicitly).
- `new BigEndianReader(stream)` reads from the stream's current position to its end and leaves it open. Public
  file-level APIs take a `Stream` at the edge and read it this way.
- Reads are sequential (`ReadUInt16`, `ReadFourCC`, `ReadMacRect` …) or at absolute offsets (`ReadUInt32At`, which do
  not move `Position`). `Try…` variants return false instead of throwing. `ReadSubReader(n)` gives a bounded reader
  over the next bytes, and `ReadSubReaderAt(offset, n)` one at an absolute offset (a structure inside the buffer, read
  by offsets from its own start), without copying. `Source` is the underlying memory.
- Truncated input throws `EndOfStreamException`.
- Create **one reader per buffer** (a header, node, record or resource) and pass it to internal helpers as a
  `BigEndianReader` parameter. Do not build a reader per field read: it is a class, so that is an allocation each time.
- Methods that read with a reader take `ReadOnlyMemory<byte>` (or a `BigEndianReader`), not `ReadOnlySpan<byte>`,
  so callers' bytes are not copied. Code that only consumes bytes keeps `ReadOnlySpan<byte>`: CRCs, hashes,
  decompressors, text decoding, comparisons.

**`BigEndianWriter`**: a class over a buffer that grows, like a `StringBuilder` for bytes.
- `Write…` appends. `Write…At(offset, value)` overwrites bytes already written, for a length or offset known only
  later: write a placeholder, then patch it.
- `new BigEndianWriter(array)` wraps an existing array, all of it counting as written, so `Write…At` patches it in
  place (disk-image blocks, B-tree nodes). Don't append to such a writer: appending moves to a new array.
- Hand the result on with `ToArray()`, `WrittenMemory`/`WrittenSpan` or `WriteTo(stream)`. `WriteZeros(n)` writes padding.
- The generic overloads (`WriteUInt32<T>`, `WriteUInt16At<T>` …) take any number and throw
  `ArgumentOutOfRangeException` when it does not fit the field. Pass `int` and `long` values directly, with no cast.
  Cast explicitly only to wrap or pack bits on purpose (e.g. `(ushort)(count - 1)` for an empty list, packed
  attribute words); a value of the field's own type uses the exact overload.

**Numbers**: format offsets and lengths are often u32. Do offset arithmetic in `long`, so `offset + length` cannot
wrap. Check a value against the buffer before casting it to `int` to index. Where a value stays `int`, write bounds
checks subtract-first (`offset > length - size`), as the Mac OS code does.

## Conventions

- Work on `main` (no feature branches).
- No partial classes: a class lives in one file. Split a large class into separate classes with their own names (use
  `using static` where call sites should stay short), never into partial files. `partial` is only for types a tool
  requires it on (Avalonia code-behind, CommunityToolkit.Mvvm view models, `[GeneratedRegex]`, JSON source generation),
  and then the hand-written part is still one file; generated tables go in classes of their own.
- Every `if`/`else`/loop body has braces, one statement per line (`.editorconfig`); the build enforces the style
  (`EnforceCodeStyleInBuild`, warnings are errors). `dotnet format style --diagnostics IDE0011` fixes braces.
- The build also runs the .NET analyzers' recommended rules (`AnalysisMode` in `Directory.Build.props`) as errors;
  `.editorconfig` lists the rules turned off, each with its reason. Text the user sees or a test compares is formatted
  and parsed with `CultureInfo.InvariantCulture` and compared with a `StringComparison`. A recurring catch filter uses
  `ExceptionFilters` (Core).
- A change to how a format is read or written updates its document in `docs/formats/<category>/` in the same commit;
  the authoring rules are in `docs/formats/CLAUDE.md`.
