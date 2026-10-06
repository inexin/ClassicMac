# CLAUDE.md: ClassicMac.Graphics

Guidance for `src/ClassicMac.Graphics`, the `.ImageSharp` and `.SkiaSharp` adapters, `tests/ClassicMac.Graphics.Tests`
and the QuickDraw resources in `ClassicMac.Resources.Decoders/Images`. The repository-wide rules are in the root
`CLAUDE.md`.

QuickDraw.Pict (formerly <https://github.com/inexin/QuickDraw.Pict>) moved into this repo with its history on
2026-09-29 and became one package, `ClassicMac.Graphics`, in layers (a folder and namespace each), with the
`ClassicMac.Graphics.ImageSharp` and `ClassicMac.Graphics.SkiaSharp` adapters. It decodes and encodes QuickDraw PICT pictures and draws
**exactly the pixels a Macintosh draws**. The public drawing API is `QuickDrawPort`, whose members carry QuickDraw's names.

- The specs: `docs/formats/graphics/pict.md` (the picture format and playback), `docs/formats/graphics/quickdraw.md`
  (the drawing rules), `graphics/quicktime.md`, `graphics/macpaint.md`, `graphics/tiff.md`, and `resources/icons.md`,
  `icon-families.md`, `cursors.md`, `patterns.md`. Update the matching section in the same change as any behaviour change (each has a "Not
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

## Architecture

**Layers.** `src/ClassicMac.Graphics` (on Core only) is one package; each layer is a folder with its own namespace
and may use only the layers below it, which `LayeringTests` checks. The adapters and the decoders share its internals
(`InternalsVisibleTo`) until the renderer has a public drawing API.

| Folder (namespace) | Contains | Uses |
| --- | --- | --- |
| root, `MacPaint/`, `Tiff/` (`ClassicMac.Graphics`) | `RgbaBitmap`, `RgbaColor`, `PixMap` records, standard colour tables, `MacPaintFile` (also QuickTime's `PNTG` codec), `TiffFile` | Core (`PackBits`, shared with StuffIt's method 6) |
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
- `ClassicMac.Graphics/Tiff/TiffFile`: TIFF images.
- `ClassicMac.Graphics/Pict/PictWriter`: the encoder.
