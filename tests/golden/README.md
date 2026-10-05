# Goldens

## App screenshot baselines

`app/` holds the viewer's own UI drawn headless (`tests/ClassicMac.App.Tests`, `WindowTests` through `Baselines`):
`<frame>-light.png`, `<frame>-dark.png` and `<frame>-150.png` (light theme at 150% display scaling) for the frames
tree, icon, text, sound, document, dialog, menu, edit-DITL and hex-edit. They are ClassicMac's own UI, so they are
committed.

- Compared on Windows only; elsewhere the window tests end skipped with a message (text rasterisation differs by
  platform). CI sets `CLASSICMAC_SKIP_BASELINES=1` and skips them too (hosted Windows runners now and then render a
  frame differently); compare them locally before pushing a UI change. A pixel matches when no channel differs by more than 2 (Skia's CPU-specific blending rounds a level or
  two apart); up to 8 stray pixels may differ by up to 32 (Skia's per-process path and glyph caches shift an
  anti-aliased edge pixel depending on what earlier tests drew); anything more fails.
- A failure lists every frame that differs and writes `<frame>-<variant>-actual.png` and `-diff.png` (differences in
  magenta on the faded frame) to `%TEMP%\classicmac-baselines\`.
- After an intended UI change, regenerate them all with one command and review the PNG diff before committing:

  ```sh
  CLASSICMAC_UPDATE_BASELINES=1 dotnet run --project tests/ClassicMac.App.Tests -- -class ClassicMac.App.Tests.WindowTests
  ```

  (PowerShell: `$env:CLASSICMAC_UPDATE_BASELINES=1; dotnet run --project tests/ClassicMac.App.Tests -- -class ClassicMac.App.Tests.WindowTests; Remove-Item Env:CLASSICMAC_UPDATE_BASELINES`.)

## Emulator goldens

`pict/` holds feature pictures written by `tools/GoldenPictures`:

| Picture | Covers |
|---|---|
| `shapes` | Every verb on rects, round rects, ovals, arcs, polygons and regions; pen sizes 1x1, 2x2, 3x1. |
| `modes` | The eight pattern pen modes over stripes. |
| `lines` | Line slopes at three pen sizes. |
| `copybits1` | 1-bit CopyBits in every Boolean mode, StretchBits ratios, and a mask region. |
| `copybitsColor` | 8-, 16- and 32-bit CopyBits at 1:1, shrunk and stretched. |
| `color` | Fore/back colors, colorized CopyBits, arithmetic modes with OpColor, hilite. |
| `text` | Chicago, Geneva, New York and Monaco at several sizes, every style, srcXor text. |
| `highres` | A 144 dpi extended version 2 picture drawn into its 72 dpi frame. |
| `originClip` | The Origin opcode, pattern alignment and a non-rectangular clip. |

Each picture is 240 x 160 with a 1-pixel black frame around it, so the test can find it in a screenshot.

### Capturing

1. In Basilisk II or SheepShaver, run System 7.5 to Mac OS 9 with the monitor set to **millions of colors**, and copy
   `pict/` into the emulated Mac (through the shared folder). If the files have no type, set them to `PICT`, e.g. with
   ResEdit's Get Info, so SimpleText opens them.
2. Open each picture in SimpleText. It draws the picture at 100%.
3. Take a screenshot at the emulator's native size (no window scaling): Cmd-Shift-3 inside the Mac, or the emulator's
   own screenshot, or the host's if the window is 1:1. Crop it or not; the test finds the frame.
4. Save it as PNG in `screens/<picture name>.png`, e.g. `screens/shapes.png`.

For `text`, copy the Mac's font suitcases or System file resource forks (Chicago, Geneva, New York, Monaco) into
`fonts/` (any file names; resource-fork data, e.g. from `..namedfork/rsrc` or a `.rsrc` export). They are not committed.

### Checking

`dotnet test` runs `GoldenTests`: every screenshot must match QuickDraw.Pict's rendering exactly. On a mismatch it
writes `golden-diff-<name>.png` next to the test binaries (differences in magenta).

Regenerate the pictures with `dotnet run --project tools/GoldenPictures -- tests/golden/pict` (only needed when the
generator changes; existing screenshots then need recapturing).
