# ClassicMac app icon

A cube of blocks: paper-coloured blocks with one blue block on the near top corner and one amber block at the bottom of the far column. A volume made of blocks, two of them picked out. Flat style with black outlines.

Two drawings, chosen by size:

- **Large (128 px and up):** 3 × 3 × 3 blocks.
- **Small (16–64 px):** 2 × 2 × 2 blocks, same colours and arrangement (blue near top corner, amber bottom back). 16–32 are drawn pixel by pixel in 2:1 pixel isometric; 40–64 are rendered from a vector with heavier lines.

Each comes **with the frame** (the light rounded tile, macOS grid) and **without** (the cube alone, transparent).

## Files

| File | What it is | Use it for |
| --- | --- | --- |
| `classicmac.ico` | Windows icon, no frame: 16, 20, 24, 32, 40, 48, 64, 256 (PNG entries) | `ApplicationIcon` in the csproj, `Window.Icon` |
| `classicmac-tile.ico` | Windows icon on the tile: 16, 32, 48, 64, 256 | Only if you want the framed look on Windows too |
| `classicmac.icns` | macOS icon on the tile: 16–512 at @1x and @2x, 1024 | `CFBundleIconFile` in the app bundle's Info.plist |
| `png/classicmac-<n>.png` | No frame, 16–1024 | Linux hicolor theme, About box, empty state, README |
| `png-tile/classicmac-tile-<n>.png` | On the tile, 16–1024 | macOS, store pages, social previews |
| `source/classicmac.svg` | Vector master, large drawing, no frame | Linux `scalable/apps`, any size from 128 |
| `source/classicmac-tile.svg` | Vector master, large drawing, on the tile (824 px tile on a 1024 canvas) | Regenerating the macOS sizes from 128 |
| `source/classicmac-small.svg`, `classicmac-small-tile.svg` | Vector, small drawing, without and with the tile | A 2 × 2 × 2 at any size, if a place needs it |
| `source/classicmac-mid.svg`, `classicmac-mid-tile.svg` | The small drawing with heavier lines | Regenerating 40, 48 and 64 |
| `source/classicmac-16.svg` … `-32.svg` | Pixel versions, no frame, 16, 20, 24, 32 | Regenerating the small sizes; never scale these |
| `source/classicmac-tile-16.svg`, `-tile-32.svg` | Pixel versions on the tile | macOS 16 and 32 |
| `source/classicmac-mono.svg` | One-colour glyph (`currentColor`): the small cube's outline, the blue block solid, the amber block at 45% | A toolbar or status spot that needs a single-colour mark |

Colours: paper #F7F3E8 / #E4DCC4 / #CFC6AC (top, left, right faces), blue #7D96EC / #2E4EC2 / #1F3591, amber #F8D27E / #F2B544 / #E8A530, outline #1A1A1A, tile #E8E8E6.

## In the app

- **Windows:** add `<ApplicationIcon>Assets\classicmac.ico</ApplicationIcon>` to `ClassicMac.App.csproj` and set `Icon="/Assets/classicmac.ico"` on `MainWindow`.
- **macOS:** copy `classicmac.icns` into the bundle's `Contents/Resources` and set `CFBundleIconFile` to `classicmac` when packaging.
- **Linux:** install `png/classicmac-<n>.png` as `share/icons/hicolor/<n>x<n>/apps/classicmac.png` (16, 24, 32, 48, 64, 128, 256, 512) and `source/classicmac.svg` as `share/icons/hicolor/scalable/apps/classicmac.svg`; the `.desktop` file uses `Icon=classicmac`.
- **Title bar (S1):** the 16 px PNG at 100% scaling, 32 px at 200%. At 125% and 150%, use the 20 and 24 px versions rather than scaling, per the pixel rules in [../TOKENS.md](../TOKENS.md).
- **About box (S7):** `png/classicmac-128.png`, or 64 in compact layouts.

## History

Replaces the diagonal-stripes monitor (2026-10-02). Chosen 2026-10-05 from the cube rounds on the design canvas (rounds 11–14): K2H with the blocks left in place and the amber block moved to the bottom.
