# ClassicMac

Tools for classic Mac OS files in .NET: read resource forks out of whatever they are wrapped in (raw forks,
AppleDouble, MacBinary, BinHex, HFS disk images), convert the resources to modern formats, and browse and edit them in
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

The graphics packages (`ClassicMac.Graphics`, `.QuickTime`, `.QuickDraw`, `.Pict`, `.ImageSharp`, `.SkiaSharp`; formerly
QuickDraw.Pict) draw PICT pictures and QuickDraw images exactly as a Macintosh does: usage in
[docs/GRAPHICS.md](docs/GRAPHICS.md), spec in [docs/formats/PICT-FORMAT.md](docs/formats/PICT-FORMAT.md).

## Licence

MIT. See `LICENSE`.
