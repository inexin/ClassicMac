# The public QuickDraw drawing API (design)

*Status: decided, 2026-09-29 (the three decisions at the end, as proposed); being built. Stage 3 of the QuickDraw.Pict
merge ([PLAN.md](PLAN.md), phase 9).*

## Why

The renderer in `ClassicMac.Graphics.QuickDraw` draws exactly what a Macintosh draws, but only picture playback can
reach it: `Pict/GrafPort` is the one caller, and everything it calls is internal. Several things need the renderer without a
picture:

- dialog, alert and menu previews drawn as the Dialog and Menu Managers draw them (the app approximates them with
  Avalonia today);
- icons, cursors and patterns scaled with CopyBits or drawn at a screen depth, and the Icon Utilities' transforms;
- a caller's own drawing: a game's or an application's screens rebuilt from their resources.

## Shape

One mutable port object, named and behaving like a colour QuickDraw port, so *Inside Macintosh: Imaging With
QuickDraw* documents it. Names follow QuickDraw's own (`FrameRect`, `PaintOval`, `CopyBits`, `DrawString`), with the
current port as the instance instead of `SetPort`.

```csharp
var canvas = new RgbaBitmap(320, 200);                    // today's RgbaBitmap (see Names)
var port = new QuickDrawPort(canvas, new QuickDrawOptions { Version = QuickDrawVersion.MacOS9, Fonts = fonts });
port.PenSize(2, 2);
port.ForeColor = RgbColor.Red;
port.FrameRoundRect(new MacRect(10, 10, 60, 110), 16, 16);
port.TextFont(3); port.TextSize(12); port.TextFace(QuickDrawStyle.Bold);
port.MoveTo(20, 90);
port.DrawString("Hello");
port.CopyBits(icon, icon.Bounds, new MacRect(100, 100, 132, 132), TransferMode.SrcCopy, mask: null);
```

### The port (`ClassicMac.Graphics.QuickDraw.QuickDrawPort`)

| Group | Members |
| --- | --- |
| Setup | `QuickDrawPort(RgbaBitmap canvas, QuickDrawOptions? options)`; `PortRect`; `SetOrigin(h, v)`; `Clip` (a `Region`, null for none) |
| Pen | `PenSize`, `PenMode`, `PenPattern`, `PenLocation`, `MoveTo`/`Move`, `LineTo`/`Line`, `PenNormal`, `HidePen`/`ShowPen` |
| Colour | `ForeColor`, `BackColor`, `OpColor`, `HiliteColor` (16-bit `RgbColor`); `BackPattern`, `FillPattern`; `HiliteMode()` |
| Shapes | `Frame`/`Paint`/`Erase`/`Invert`/`Fill` × `Rect`, `RoundRect`, `Oval`, `Arc`, `Poly`, `Rgn` (`Fill…` takes a pattern) |
| Bits | `CopyBits(PixMap source, MacRect src, MacRect dst, TransferMode mode, Region? mask)` |
| Text | `TextFont`, `TextFace`, `TextSize`, `TextMode`, `SpaceExtra`, `CharExtra`, `FractionalWidths`, `ScaleDisable`; `DrawString`, `DrawText`, `DrawChar`; `StringWidth`, `TextWidth`, `GetFontInfo` |
| Pictures | `DrawPicture(byte[] picture, MacRect destination)`: a PICT played into this port (the PICT reader's own path) |

- **Coordinates** are `MacRect`/`MacPoint` from Core: 16-bit, as QuickDraw's are. The engine keeps its internal
  32-bit `PictRect` for intermediate results; nothing public uses it.
- **Behaviour** is the engine's, both QuickDraws, every screen depth: nothing is re-implemented for the API. What the
  engine does not model yet (ScrollRect, CopyMask/CopyDeepMask, pictures inside regions, …) is left out, not
  approximated.
- **Threading:** a port is single-threaded, like a Mac port. The font library is shared read-only, as now.

### Supporting types

| Type | What | From |
| --- | --- | --- |
| `QuickDrawOptions` | `Version`, `ScreenDepth`, `Fonts`, `TextFallback`, `HiliteColor`, `PreserveAlpha` | the drawing half of `PictDecodeOptions`, which then holds these plus picture-only settings |
| `QuickDrawVersion` | `MacOS9`, `MacRom` | `QuickDrawVersion`, renamed and moved down |
| `RgbColor` | QuickDraw's `RGBColor`: three 16-bit components (the engine already keeps them exactly) | new |
| `TransferMode` | `SrcCopy` … `PatCopy` … `Blend` … `Hilite`, `GrayishTextOr`, `DitherCopy`; any 16-bit value, since the engine models odd modes too | the engine's constants |
| `QuickDrawPattern` | an 8×8 1-bit pattern (`Black`, `Gray`, …, or 8 bytes), or a colour `'ppat'` | `Pattern`, made public read-only |
| `Region` | immutable; `FromRect`, from `'RGN '`-format data, set operations, `Offset`, `Inset`, `Contains`; shape regions (`Oval`, `RoundRect`, `Arc`, `Poly`) as the port's version draws them | `Region` + `RegionShapes`, made public |
| `PixMap` | a CopyBits source: 1-bit bitmap, indexed with a colour table, direct; or a view of an `RgbaBitmap` | `PixMap`, made public read-only with factory methods |
| `FontLibrary` | families and strikes the text draws with | `FontLibrary`, renamed |
| `ITextFallback` | outline text when no bitmap strike fits | `ITextFallback`, renamed |

### How the picture reader uses it

`Pict/GrafPort` becomes DrawPicture's play state on top of a `QuickDrawPort`: it maps picture coordinates to the port
(MapRect, MapPoint, MapFixPt, the text ratio) and keeps the "same shape" operands, then calls the port. The few
playback-only inputs (the pen's fixed-point fraction, the picture's text scaling, the Origin opcode's pattern shift)
stay internal members of the port. Every existing test and golden picture must come out unchanged: that is the
proof the API draws what the engine drew.

## Order of work

1. The public types (`RgbColor`, `TransferMode`, `QuickDrawPattern`, `Region`, `PixMap`, `QuickDrawOptions`,
   `QuickDrawVersion`, `FontLibrary`, `ITextFallback`) and the renames.
2. `QuickDrawPort` over the engine; `Pict/GrafPort` rebuilt on it. Tests: each verb through the port and through an
   equivalent PICT gives identical pixels.
3. `DrawPicture` onto a port; the ImageSharp and SkiaSharp adapters unchanged apart from the renames.
4. Docs: `docs/GRAPHICS.md` gains a drawing section; `PICT-FORMAT.md` splits into `PICT.md` (opcodes) and
   `QUICKDRAW.md` (drawing rules), as the plan has it, so the API's documentation points at the drawing rules.

Icons as full resource decoders (suite choice, transforms, CalcMask, `icns`) and Dialog Manager drawing follow as
users of the API, each with its own disassembly questions.

## Decisions (2026-09-29)

1. **The base names.** `RgbaBitmap` and `RgbaColor` are the base layer's image and colour for every format now, not
   just PICT: renamed `RgbaBitmap` and `RgbaColor` (avoiding `Bitmap`/`Color`, which clash with host libraries).
2. **16-bit colours:** `RgbColor` (16-bit) on the port, as QuickDraw's; `RgbaColor` (8-bit) stays the pixel type.
3. **Scope of the first cut:** the table above (everything the engine already does). Later: ScrollRect,
   CopyMask/CopyDeepMask, OpenRgn/OpenPoly recording, picture recording into a PICT (`OpenPicture`).
