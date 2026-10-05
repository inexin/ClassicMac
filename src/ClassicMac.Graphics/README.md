# ClassicMac.Graphics

Classic Mac OS graphics that draw exactly the pixels a Macintosh draws: a software QuickDraw, QuickDraw PICT pictures
(version 1, version 2 and extended version 2) read and written, QuickTime still images, MacPaint documents and the
Font Manager's resources. One package on ClassicMac.Core, with a namespace per layer; the adapters for ImageSharp and
SkiaSharp are separate packages.

| Package or namespace | What it is |
|---|---|
| `ClassicMac.Graphics` | The base: the RGBA `RgbaBitmap`, colours, PixMaps, standard colour tables, PackBits, MacPaint documents. |
| `ClassicMac.Graphics.Fonts` | Bitmap strikes (`NFNT`/`FONT`), families (`FOND`), font colour tables, TrueType `sfnt` data ([bitmap-fonts.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/bitmap-fonts.md), [font-families.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/font-families.md), [outline-fonts.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/outline-fonts.md)). |
| `ClassicMac.Graphics.QuickTime` | QuickTime still images: the codecs, the codec hook, QTIF files. |
| `ClassicMac.Graphics.QuickDraw` | The software QuickDraw that draws everything (Mac OS 9 or the 68k ROM), text, screen depths. |
| `ClassicMac.Graphics.Pict` | `PictReader` decodes to an RGBA `RgbaBitmap` (`Read` also gives the `PictInfo`); `PictWriter` writes pictures; `PictHeader` detects pictures and reads their header. |
| `ClassicMac.Graphics.ImageSharp` | [ImageSharp](https://github.com/SixLabors/ImageSharp) format plugin on top of the core: detection, decoding, encoding, `SaveAsPict`. |
| `ClassicMac.Graphics.SkiaSharp` | [SkiaSharp](https://github.com/mono/SkiaSharp) integration: decode to `SKBitmap`/`SKImage`, encode `SKBitmap`/`SKPixmap`, `SaveAsPict`. |

## Decoding and encoding

```csharp
RgbaBitmap bitmap = PictReader.Decode(bytes);
RgbaBitmap exact  = PictReader.Decode(bytes, new PictDecodeOptions { Fonts = library, ImageCodec = myJpegCodec });
RgbaBitmap screen = PictReader.Decode(bytes, new PictDecodeOptions { ScreenDepth = 8 });  // as on an 8-bit screen
PictWriter.Write(stream, bitmap);                                       // 24-bit, 72 dpi
PictWriter.Write(stream, bitmap, new PictWriteOptions { Format = PictPixelFormat.Indexed8, Palette = colors,
    HorizontalResolution = 144, VerticalResolution = 144, IccProfile = icc });
```

## Drawing

`QuickDrawPort` is QuickDraw itself, without a picture: a colour port on an `RgbaBitmap`, drawing what the chosen
QuickDraw draws. Its members carry QuickDraw's names.

```csharp
var port = new QuickDrawPort(new RgbaBitmap(200, 100), new QuickDrawOptions { Fonts = library, ScreenDepth = 8 });
port.PenSize = new MacPoint(2, 2);
port.FrameRoundRect(new MacRect(10, 10, 60, 110), 16, 16);
port.FillOval(new MacRect(20, 120, 80, 190), QuickDrawPattern.Gray);
port.TextFont = 3; port.TextSize = 12;
port.MoveTo(20, 90);
port.DrawString("Hello");
port.CopyBits(PixMap.FromBitMap(icon, 4, new MacRect(0, 0, 32, 32)), new MacRect(0, 0, 32, 32),
    new MacRect(10, 150, 42, 182), TransferMode.SrcOr);
```

## What is drawn

- **Shapes**: rects, round rects, ovals, arcs and wedges, lines, polygons and regions, framed, painted, erased,
  inverted and filled, with pen size and patterns (1-bit and pixel patterns), clip regions and the Origin opcode.
  Shapes are scan-converted the way QuickDraw does, pixel for pixel.
- **Transfer modes**: every Boolean pattern and source mode, the arithmetic modes (blend, addPin, addOver, subPin,
  subOver, addMax, adMin), transparent and hilite.
- **Bitmaps** (CopyBits): 1/2/4/8-bit indexed and 16/32-bit direct pixel maps in every packing, stretched or shrunk from
  the source to the destination rect like QuickDraw's StretchBits, with mask regions and fore/back colorizing.
- **Text**: with a `FontLibrary` of `FOND` / `NFNT` / `FONT` / `fctb` resources (from a font suitcase, the System
  file or an application), text is drawn by the Font Manager and character generator: font and size substitution,
  bold, italic, underline, outline, shadow, condense, extend, space and character extra, fractional widths, text
  ratios, font-name mapping, pen fractions, and color bitmap fonts. No Apple fonts are included; without a matching font (or for
  TrueType-only families), text goes to an `ITextFallback` (the ImageSharp plugin renders it with SixLabors.Fonts).
- **QuickTime images**: `raw `, `rle ` (Animation), `rpza` (Road Pizza), `smc ` (Graphics), `cvid` (Cinepak), `8BPS`,
  `yuv2`, `YVU9`, `tga ` and `PNTG` are decoded by the core; others go to an `IPictImageCodec`. A decoded image skips
  the picture's "QuickTime is required" fallback.
- **QuickTime image files and MacPaint documents**: `QuickTimeImageFile` decodes standalone QTIF files (with their
  resolution and ICC profile) and `MacPaintFile` decodes MacPaint (PNTG) documents, MacBinary-wrapped or not. The
  ImageSharp plugin registers both formats for loading.
- **Icons, cursors and patterns**: `QuickDrawResources` decodes the QuickDraw image resources of classic Mac OS resource
  forks (`ICON`, `ICN#`, `ics#`, `icm#`, `SICN`, `icl4/8`, `ics4/8`, `icm4/8`, `cicn`, `CURS`, `crsr`, `PAT `,
  `PAT#`, `ppat`, `ppt#`) from each resource's bytes, with the system's loading rules (computed masks for icon lists
  without one, cursor XOR masks, pixel-pattern types).
- **Screen depth**: `ScreenDepth` = 1, 2, 4, 8 or 16 draws the picture as it looks on that screen: the default color
  table, QuickDraw's color matching (inverse tables), transfer modes on pixel values and ditherCopy error diffusion.
- **Resolution**: extended version 2 pictures decode at their native resolution, or with
  `Resolution = PictResolution.PictureFrame` at their 72 dpi frame, scaled the way `DrawPicture` scales.
- **Metadata**: `PictInfo` has the version, frame, bounds, resolution, all picture comments and the embedded ICC profile.

The writer stores 1/2/4/8-bit indexed, 16-bit and 32-bit (with or without alpha) pictures with their resolution and an
ICC profile, as a `.pict` file or a bare picture, splitting images too wide for one bitmap opcode into strips.

## Accuracy

Pictures are drawn as Mac OS 9's QuickDraw draws them (the default), or with
`QuickDraw = QuickDrawVersion.MacRom` as the classic 68k QuickDraw of the Macintosh ROM (Mac OS ROM $077D). Picture
playback, shape rasterization, regions, CopyBits scaling and transfer modes, pixel data, the Font Manager, the text
character generator and drawing on indexed and 16-bit screens are reproduced from the system's own code and checked
pixel for pixel against it, in both modes. The QuickTime codecs match ffmpeg's decoders on real and generated samples.
Not modelled: TrueType text (it goes to the outline fallback), and QuickTime codecs' own dithering on indexed
screens.

The format and every rendering rule the library follows are specified in [pict.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/graphics/pict.md) and
[quickdraw.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/graphics/quickdraw.md) (with [quicktime.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/graphics/quicktime.md),
[macpaint.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/graphics/macpaint.md), and [icons.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/icons.md),
[icon-families.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/icon-families.md), [cursors.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/cursors.md) and
[patterns.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/patterns.md)),
in enough detail to write a compatible decoder and encoder without reading the source.

The adapters: [ClassicMac.Graphics.ImageSharp](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Graphics.ImageSharp) and [ClassicMac.Graphics.SkiaSharp](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Graphics.SkiaSharp).

Part of [ClassicMac](https://github.com/inexin/ClassicMac) (formerly QuickDraw.Pict). MIT.
