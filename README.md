# ClassicMac

Tools for classic Mac OS files in .NET: read resource forks out of whatever they are wrapped in (raw forks,
AppleDouble, MacBinary, BinHex, archives, HFS disk images and CDs), convert the resources to modern formats, and browse and edit them in
a cross-platform desktop app.

Status: in development. See [docs/PLAN.md](docs/PLAN.md) and [CHANGELOG.md](CHANGELOG.md).

## Trying it

```
dotnet run --project src/ClassicMac.App -- "some disk.img"          # the viewer
dotnet run --project src/ClassicMac.Resources.Cli -- list "some disk.img"
dotnet run --project src/ClassicMac.Resources.Cli -- extract "App.rsrc" -o out
dotnet run --project src/ClassicMac.Resources.Cli -- unpack "some disk.img" -o files
dotnet run --project src/ClassicMac.Resources.Cli -- convert "Manual" -o manual
dotnet run --project src/ClassicMac.Resources.Cli -- pack out -o "App.rsrc"
```

The graphics package `ClassicMac.Graphics` (QuickDraw, PICT, QuickTime images, MacPaint and fonts; formerly
QuickDraw.Pict), with the `ClassicMac.Graphics.ImageSharp` and `ClassicMac.Graphics.SkiaSharp` adapters, draws PICT pictures and QuickDraw images exactly as a Macintosh does: usage in
[docs/GRAPHICS.md](docs/GRAPHICS.md), specs in [docs/formats/PICT.md](docs/formats/PICT.md) and [docs/formats/QUICKDRAW.md](docs/formats/QUICKDRAW.md).

## Licence

MIT. See `LICENSE`.
