# ClassicMac app icon

The diagonal-stripes monitor: a beige monitor on a stand, its dark screen crossed by a diagonal band in six colours. Flat style with black outlines.

## Files

| File | What it is | Use it for |
| --- | --- | --- |
| `classicmac.ico` | Windows icon: 16, 20, 24, 32, 40, 48, 64, 256 (PNG entries) | `ApplicationIcon` in the csproj, `Window.Icon` |
| `classicmac.icns` | macOS icon, light rounded tile: 16–512 at @1x and @2x, 1024 | `CFBundleIconFile` in the app bundle's Info.plist |
| `png/classicmac-<n>.png` | No background, 16–512 | Linux hicolor theme, About box, empty state, README |
| `png-tile/classicmac-tile-<n>.png` | On the light tile, 16–1024 | macOS, store pages, social previews |
| `source/classicmac.svg` | Vector master, no background | Linux `scalable/apps`, any size above 32 |
| `source/classicmac-tile.svg` | Vector master on the tile (macOS grid: 824 px tile on a 1024 canvas) | Regenerating the macOS sizes |
| `source/classicmac-16.svg` … `-32.svg` | Hand-drawn pixel versions for 16, 20, 24 and 32 | Regenerating the small sizes; never scale these |
| `source/classicmac-mono.svg` | One-colour outline glyph (`currentColor`) | A toolbar or status spot that needs a single-colour mark |

Sizes 16–32 are drawn pixel by pixel (fewer, chunkier stripes, a simpler stand); 40 and up are rendered from the vector master.

## In the app

- **Windows:** add `<ApplicationIcon>Assets\classicmac.ico</ApplicationIcon>` to `ClassicMac.App.csproj` and set `Icon="/Assets/classicmac.ico"` on `MainWindow`.
- **macOS:** copy `classicmac.icns` into the bundle's `Contents/Resources` and set `CFBundleIconFile` to `classicmac` when packaging.
- **Linux:** install `png/classicmac-<n>.png` as `share/icons/hicolor/<n>x<n>/apps/classicmac.png` (16, 24, 32, 48, 64, 128, 256, 512) and `source/classicmac.svg` as `share/icons/hicolor/scalable/apps/classicmac.svg`; the `.desktop` file uses `Icon=classicmac`.
- **Title bar (S1):** the 16 px PNG at 100% scaling, 32 px at 200%. At 125% and 150%, use the 20 and 24 px versions rather than scaling, per the pixel rules in [../TOKENS.md](../TOKENS.md).
- **About box (S7):** `png/classicmac-128.png`, or 64 in compact layouts.

## Note

Six stripes in this order echo Apple's old striped logo. The icon contains no Apple shape, and the owner decided (2026-10-02) to use it as it is.
