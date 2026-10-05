# ClassicMac.Graphics.SkiaSharp

[SkiaSharp](https://github.com/mono/SkiaSharp) integration for QuickDraw PICT pictures, QuickTime image files (QTIF) and MacPaint documents: decode to `SKBitmap`/`SKImage` exactly as a Macintosh draws them (with ClassicMac.Graphics) and encode as PICT.

```csharp
using SKBitmap bitmap = PictSkia.Decode(File.ReadAllBytes("picture.pict"));
using SKImage image   = PictSkia.DecodeImage(bytes, new PictSkiaOptions { ScreenDepth = 8 });
SKBitmap? any         = PictSkia.DecodeAny(bytes);          // PICT, QTIF or MacPaint
bitmap.SaveAsPict("copy.pict");
```

SkiaSharp has no registry for managed codecs, so this package is an adapter rather than a format plugin: `SKCodec` and
`SKBitmap.Decode` do not see PICT. `PictSkiaOptions` has the same options as the ImageSharp decoder, with a
`TypefaceResolver` for the outline-text fallback. JPEG, PNG, GIF, WebP and BMP QuickTime images are decoded with Skia's
own codecs (Skia has no TIFF decoder). On Linux, add a SkiaSharp native-assets package, such as
`SkiaSharp.NativeAssets.Linux`.

The drawing engine, its options and accuracy: [ClassicMac.Graphics](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Graphics).

Part of [ClassicMac](https://github.com/inexin/ClassicMac). MIT.
