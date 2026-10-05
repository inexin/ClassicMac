# ClassicMac.Graphics.ImageSharp

An [ImageSharp](https://github.com/SixLabors/ImageSharp) format plugin for QuickDraw PICT pictures, QuickTime image files (QTIF) and MacPaint documents: detection, decoding exactly as a Macintosh draws them (with ClassicMac.Graphics) and PICT encoding.

```csharp
Configuration.Default.Configure(new PictConfigurationModule());

using var image = Image.Load("picture.pict");   // a .pict file or bare PICT resource data
image.SaveAsPict("copy.pict");
image.SaveAsPict("indexed.pict", new PictEncoder { BitsPerPixel = 8 });
```

Options go through `PictDecoderOptions` with `PictDecoder.Instance.Decode(...)`:

| Option | Effect |
|---|---|
| `BitmapFonts` | Classic Mac bitmap fonts (`FontLibrary`) for exact text. |
| `FontResolver` | Outline font family per QuickDraw font number, for text no bitmap font covers (default: an installed system font). |
| `Resolution` | `Native` (default) or `PictureFrame` (72 dpi). |
| `PreserveAlpha` | Keep the alpha channel of 32-bit pixel maps that have one. |
| `QuickDraw` | `MacOS9` (default) or `MacRom`: which Macintosh QuickDraw to reproduce. |
| `ScreenDepth` | 32 (default), or 1, 2, 4, 8 or 16 to draw as on that screen (default color table, index-level transfer modes, ditherCopy dithering). |

JPEG, PNG, GIF, TIFF, WebP and BMP QuickTime images inside pictures are decoded with ImageSharp's own decoders.

ImageSharp has its own licence (the Six Labors Split License); check that it fits your use.

The drawing engine, its options and accuracy: [ClassicMac.Graphics](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Graphics).

Part of [ClassicMac](https://github.com/inexin/ClassicMac). MIT.
