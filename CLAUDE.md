# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

ClassicMac reads, converts and edits classic Mac OS files in .NET: resource forks and the containers they travel in,
resource decoders, a CLI and an Avalonia desktop app. The project is at the planning stage; `docs/PLAN.md` is the plan
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

## Graphics: QuickDraw and PICT (src/ClassicMac.Graphics … .SkiaSharp)

QuickDraw.Pict (formerly <https://github.com/inexin/QuickDraw.Pict>) moved into this repo with its history on
2026-09-29 and became one package, `ClassicMac.Graphics`, in layers (a folder and namespace each), with the
`ClassicMac.Graphics.ImageSharp` and `ClassicMac.Graphics.SkiaSharp` adapters. It decodes and encodes QuickDraw PICT pictures and draws
**exactly the pixels a Macintosh draws**. The public drawing API is `QuickDrawPort`, whose members carry QuickDraw's names.

- The specs: `docs/formats/graphics/pict.md` (the picture format and playback), `docs/formats/graphics/quickdraw.md`
  (the drawing rules), `graphics/quicktime.md`, `graphics/macpaint.md`, and `resources/icons.md`, `icon-families.md`,
  `cursors.md`, `patterns.md`. Update the matching section in the same change as any behaviour change (each has a "Not
  covered" section (§8) and a "Variants" section (§4) with the Mac OS 9 differences; screen depths are
  `docs/formats/graphics/quickdraw.md` §4.6–§4.11).
- **Two QuickDraws:** `PictDecodeOptions.QuickDraw` selects `MacOS9` (default) or `MacRom` (the 68k ROM $077D). Any
  behaviour change must keep **both** correct.
- Match QuickDraw's integer behaviour exactly: signed 16-bit wraps, truncating versus rounding divides, Fixed maths.
  Comments cite the routine or rule being reproduced.
- Golden feature pictures: `dotnet run --project tools/GoldenPictures` writes `tests/golden/pict`; capturing emulator
  screenshots is in `tests/golden/README.md`. `GoldenTests` skip without screenshots; the text golden needs Apple fonts
  in `tests/golden/fonts/` (gitignored, never committed).
- Tests (`tests/ClassicMac.Graphics.Tests`, covering the package and both adapters) build pictures opcode by opcode with
  `PictBuilder` and small fonts with `TestFont`.

### Architecture

**Layers.** `src/ClassicMac.Graphics` (on Core only) is one package; each layer is a folder with its own namespace
and may use only the layers below it, which `LayeringTests` checks. The adapters and the decoders share its internals
(`InternalsVisibleTo`) until the renderer has a public drawing API.

| Folder (namespace) | Contains | Uses |
| --- | --- | --- |
| root, `MacPaint/` (`ClassicMac.Graphics`) | `RgbaBitmap`, `RgbaColor`, `PixMap` records, standard colour tables, `MacPaintFile` (also QuickTime's `PNTG` codec) | Core (`PackBits`, shared with StuffIt's method 6) |
| `Fonts/` (`.Fonts`) | The Font Manager's resources: `BitmapFont` (`NFNT`/`FONT`), `FontFamily` (`FOND`), `fctb`, `OutlineFont` (`sfnt`) | base |
| `QuickTime/` (`.QuickTime`) | Image descriptions, the codecs, `IPictImageCodec`, QTIF files | base |
| `QuickDraw/` (`.QuickDraw`) | The renderer: the public `QuickDrawPort` (state and verbs) over `Engine/`, `Regions/`, `Text/`; `QuickDrawPattern`, `Region`, `FontLibrary`, `QuickDrawOptions` | base, Fonts |
| `Pict/` (`.Pict`) | The PICT format: `PictReader` (`Decode`, or `Read` with the `PictInfo`), `GrafPort`, `PictWriter`, `PictHeader`/`PictInfo`, options, the `$8200` opcode | all of the above |

`ClassicMac.Graphics.ImageSharp` and `ClassicMac.Graphics.SkiaSharp` are separate packages (the ImageSharp format plugin and the
SkiaSharp adapter) on `ClassicMac.Graphics`.

Icons, cursors and patterns (`QuickDrawResources`) are in `ClassicMac.Resources.Decoders` (`Images/`).

**Decoding pipeline**
- `PictReader` parses the opcode stream and drives a `GrafPort`: DrawPicture's play state (picture-space pen, text
  origin, "same shape" operands, font-name map) and the picture-to-canvas mapping, on top of a `QuickDrawPort` that
  holds the drawing state (pen, patterns, fore/back/op/hilite colours, clip, text state) and draws.
- The port draws onto a `RgbaBitmap` (RGBA canvas) through the renderer in `ClassicMac.Graphics/QuickDraw/Engine/`:
  - `Painter`: region + pattern fills.
  - `Bits`: CopyBits/StretchBits, including the row and column DDAs and colorizing.
  - `TransferModes`: Boolean, arithmetic and hilite modes.
  - `PictureMapping`: MapPt/MapRect.
- Shapes become `Regions/Region` via `RegionShapes`, which scan-converts the way QuickDraw does.
- Fixed-point maths lives in `Regions/FixedMath`.

**Two QuickDraws.** `PictDecodeOptions.QuickDraw` selects `MacOS9` (the default) or `MacRom` (the 68k ROM $077D).
The difference flows through `PortColors.MacOS9` / `FontSelection.MacOS9`. The difference is scattered through every
layer: DDAs, rounding, colorizing, text placement and the Font Manager. Any behaviour change must keep **both** modes
correct.

**Screen depth.** `PictDecodeOptions.ScreenDepth` (1/2/4/8/16) routes every pixel write through
`ClassicMac.Graphics/QuickDraw/Engine/ScreenDevice.cs`:
- `ScreenDevice`: default clut, inverse table, Color2Index.
- `DeviceModes`: index-level transfer modes, PatDither, ditherCopy.
- The device rides on `PortColors.Device`. Painter and Bits branch to it. The only canvas write sites are
  `Painter.FillRegion`/`FillMask` and `Bits.CopyBits`.

**Text** (`ClassicMac.Graphics/QuickDraw/Text/`)
- `FontLibrary` holds caller-supplied `FOND`/`NFNT`/`FONT`/`fctb` resources (split out of resource forks by
  `ResourceFork`), read by `Fonts/` (`FontFamily`, `BitmapFont`). The renderer uses their internal raw tables; a
  strike is read by the ROM's rules in `MacRom` mode (`BitmapFont.Read(..., rom: true)`: depth bits 2–4, the location
  table right after the strike).
- `FontManager.Swap` reproduces the Font Manager's font choice and outputs a `FontSelection`: widths, style extras
  and stretch.
- `TextDrawer` is the character generator. It draws into a 1-bit buffer, then does one StretchBits. It also has a
  colour-NFNT path.
- Text without a usable bitmap strike, including TrueType-only families, goes to `ITextFallback`.

**Other formats**
- `ClassicMac.Graphics/QuickTime`: the built-in codecs and QTIF files; `Pict/QuickTimeImage` parses the 0x8200/0x8201 opcodes. Other
  codecs go through `IPictImageCodec`.
- `ClassicMac.Resources.Decoders/Images/QuickDrawResources`: icons, cursors and patterns from resource bytes.
- `ClassicMac.Graphics/MacPaint/MacPaintFile`: MacPaint documents.
- `ClassicMac.Graphics/Pict/PictWriter`: the encoder.

## Code: ClassicMac.Code

`src/ClassicMac.Code` (on Core and Resources) reads classic Mac code; the specs are `docs/formats/code/*.md` and
`docs/formats/output/disassembly.md`.

- `ClassicMac.Code.Ppc`: `PefContainer` (sections, `PatternData`, `GetImage`, `GetFixups`, transition vectors),
  `PefLoader` (imports, exports and their hash, relocation headers), `PefRelocator`, `TracebackTable`, `Cfrg`.
- `ClassicMac.Code.M68k`: `CodeApplication` (`'CODE'` 0, the jump table, segments, entry point, build model),
  `SegmentHeader`, `FarRelocations`, `MpwA5Init`, `CodeWarriorData`, `Retro68Relocations`, and the code-resource
  headers (`CodeResourceHeader`, `DriverHeader`, `PackageHeader`, `ComponentResource`, `RoutineDescriptor`).
- `ClassicMac.Code.Disassembly`: `M68kDisassembler` and `PpcDisassembler` (ported from resource_dasm), the generated
  `TrapNames`, `SelectorNames` and `LowMemoryGlobals` (`tools/TrapTables`, from Multiversal Interfaces),
  `MacsBugNames`, the code maps (`M68kCodeMap`: descent, switch tables, gap sweep; `PpcFragmentMap`: TOC, glue,
  functions), the annotators, and `CodeListing` (the `.s` text with its `Functions` and `References`).
- `ClassicMac.Resources.Decoders.Code` turns it into exports (`code.segment`, `code.cfrg`, `code.resource`; the main
  file stays the data, `.bin`) and whole-file listings (`CodeExport`, the CLI's `disasm`).
- Corpus tests are gated by `CLASSICMAC_CODE_CORPUS` and assert facts as constants; real binaries and listings are
  never committed.

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
