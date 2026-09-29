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
2026-09-29 and was split into layer projects. It decodes and encodes QuickDraw PICT pictures and draws **exactly the
pixels a Macintosh draws**. The plan's merge phase continues with shared types: Core's geometry, `ClassicMac.Fonts`
for text, and a public drawing API for the renderer.

- `docs/formats/PICT-FORMAT.md` is the full format and rendering spec; update the matching section in the same change
  as any behaviour change (§16 what isn't covered, §17 the Mac OS 9 differences, §19 screen depths).
- **Two QuickDraws:** `PictDecodeOptions.QuickDraw` selects `MacOS9` (default) or `MacRom` (the 68k ROM $077D). Any
  behaviour change must keep **both** correct.
- Match QuickDraw's integer behaviour exactly: signed 16-bit wraps, truncating versus rounding divides, Fixed maths.
  Comments cite the routine or rule being reproduced.
- Golden feature pictures: `dotnet run --project tools/GoldenPictures` writes `tests/golden/pict`; capturing emulator
  screenshots is in `tests/golden/README.md`. `GoldenTests` skip without screenshots; the text golden needs Apple fonts
  in `tests/golden/fonts/` (gitignored, never committed).
- Tests (`tests/ClassicMac.Pict.Tests`, covering every graphics project) build pictures opcode by opcode with
  `PictBuilder` and small fonts with `TestFont`.

### Architecture

**Layers.** Each layer is a project that references only the layers below it. Until the renderer has a public drawing
API, lower layers share their internals with the ones above (`InternalsVisibleTo`).

| Project | Contains | References |
| --- | --- | --- |
| `ClassicMac.Graphics` | `PictBitmap`, `PictColor`/`PictRect`, `PixMap` records, standard colour tables, PackBits, `MacPaintFile` (also QuickTime's `PNTG` codec) | nothing |
| `ClassicMac.QuickTime` | Image descriptions, the codecs, `IPictImageCodec`, QTIF files | Graphics |
| `ClassicMac.QuickDraw` | The renderer: `Engine/`, `Regions/`, `Text/`, `Pattern`, `PictFontLibrary` | Graphics |
| `ClassicMac.Pict` | The PICT format: `PictReader` (`Decode`, or `Read` with the `PictInfo`), `GrafPort`, `PictWriter`, `PictHeader`/`PictInfo`, options, the `$8200` opcode | Graphics, QuickTime, QuickDraw |
| `ClassicMac.ImageSharp`, `ClassicMac.SkiaSharp` | The ImageSharp format plugin and the SkiaSharp adapter | Pict |

Icons, cursors and patterns (`QuickDrawResources`) are in `ClassicMac.Resources.Decoders` (`Images/`).

**Decoding pipeline**
- `PictReader` parses the opcode stream and drives a `GrafPort`. The port holds the play state: pen, patterns,
  fore/back/op/hilite colours, clip, text state, and picture-to-canvas mapping.
- The port draws onto a `PictBitmap` (RGBA canvas) through the renderer in `ClassicMac.QuickDraw/Engine/`:
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
`ClassicMac.QuickDraw/Engine/ScreenDevice.cs`:
- `ScreenDevice`: default clut, inverse table, Color2Index.
- `DeviceModes`: index-level transfer modes, PatDither, ditherCopy.
- The device rides on `PortColors.Device`. Painter and Bits branch to it. The only canvas write sites are
  `Painter.FillRegion`/`FillMask` and `Bits.CopyBits`.

**Text** (`ClassicMac.QuickDraw/Text/`)
- `PictFontLibrary` holds caller-supplied `FOND`/`NFNT`/`FONT`/`fctb` resources, parsed from resource forks by
  `ResourceFork`.
- `FontManager.Swap` reproduces the Font Manager's font choice and outputs a `FontSelection`: widths, style extras
  and stretch.
- `TextDrawer` is the character generator. It draws into a 1-bit buffer, then does one StretchBits. It also has a
  colour-NFNT path.
- Text without a usable bitmap strike, including TrueType-only families, goes to `IPictTextFallback`.

**Other formats**
- `ClassicMac.QuickTime`: the built-in codecs and QTIF files; `ClassicMac.Pict/QuickTimeImage` parses the 0x8200/0x8201 opcodes. Other
  codecs go through `IPictImageCodec`.
- `ClassicMac.Resources.Decoders/Images/QuickDrawResources`: icons, cursors and patterns from resource bytes.
- `ClassicMac.Graphics/MacPaint/MacPaintFile`: MacPaint documents.
- `ClassicMac.Pict/PictWriter`: the encoder.

## Conventions

- Work on `main` (no feature branches).
- A change to how a format is read or written updates its document in `docs/formats/` in the same commit.
