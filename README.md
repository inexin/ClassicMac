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
```

Related: [QuickDraw.Pict](https://github.com/inexin/QuickDraw.Pict), which draws PICT pictures and QuickDraw images
exactly as a Macintosh does, and is planned to merge into this repo later.

## Licence

MIT. See `LICENSE`.
