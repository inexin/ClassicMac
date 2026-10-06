# ClassicMac

Tools for classic Mac OS files in .NET: reach the resource fork whatever it is wrapped in (raw forks, AppleDouble,
MacBinary, BinHex, archives, HFS and HFS Plus disk images, CDs), convert resources and documents to modern formats,
edit them and write them back, and browse it all in a cross-platform desktop app.

Status: in development. See [docs/PLAN.md](docs/PLAN.md) and [CHANGELOG.md](CHANGELOG.md).

## Apps

| App | What it is |
| --- | --- |
| [Desktop app](src/ClassicMac.App/README.md) (`ClassicMac.App`) | The viewer and editor (Avalonia; Windows, macOS, Linux) |
| [Command line](src/ClassicMac.Cli/README.md) (`classicmac`, `ClassicMac.Cli`) | The command-line tool, with an MCP server and a DOS-like shell; `classicmac help` lists its commands |

```
dotnet run --project src/ClassicMac.App -- "some disk.img"
dotnet run --project src/ClassicMac.Cli -- ls "some disk.img"
dotnet run --project src/ClassicMac.Cli -- extract "App.rsrc" -o out
```

## Libraries

Each is a NuGet package; the arrows in [the plan's architecture](docs/PLAN.md#architecture) show what uses what.

| Package | What it is |
| --- | --- |
| [ClassicMac.Core](src/ClassicMac.Core/README.md) | Shared types: four-character codes, Mac strings and dates, Mac text encodings, big-endian reading and writing, diagnostics |
| [ClassicMac.Resources](src/ClassicMac.Resources/README.md) | Resource forks: read, edit, write, `dcmp` decompression, export with a manifest, Rez and DeRez |
| [ClassicMac.Files](src/ClassicMac.Files/README.md) | Mac files and their containers: wrappers, archives, disk images and file systems, HFS writing and First Aid |
| [ClassicMac.Graphics](src/ClassicMac.Graphics/README.md) | QuickDraw, drawing exactly what a Mac draws: PICT read and write, QuickTime images, MacPaint, fonts |
| [ClassicMac.Graphics.ImageSharp](src/ClassicMac.Graphics.ImageSharp/README.md) | PICT, QTIF and MacPaint as an ImageSharp format |
| [ClassicMac.Graphics.SkiaSharp](src/ClassicMac.Graphics.SkiaSharp/README.md) | PICT, QTIF and MacPaint to and from SkiaSharp bitmaps |
| [ClassicMac.Code](src/ClassicMac.Code/README.md) | Classic Mac code: PEF, `cfrg`, 68k applications and code resources, 68k and PowerPC disassemblers |
| [ClassicMac.Resources.Decoders](src/ClassicMac.Resources.Decoders/README.md) | Resources and documents to modern files: images, sound, text, fonts, UI resources, documents to HTML, code listings |

## Building and testing

.NET 10 SDK. `ClassicMac.slnx` holds everything; warnings and the recommended analyzers are errors.

```
dotnet build
dotnet test
```

- The app's screenshot tests compare against `tests/golden/app` on Windows; `CLASSICMAC_UPDATE_BASELINES=1` rewrites
  them, `CLASSICMAC_SKIP_BASELINES=1` skips them.
- Golden pictures and emulator screenshots: [tests/golden/README.md](tests/golden/README.md).
- Corpus tests over real files run only when `CLASSICMAC_CORPUS`, `CLASSICMAC_CODE_CORPUS` or
  `CLASSICMAC_HFSPLUS_REFERENCE_IMAGE` point at them.
- Fuzzing: [tools/Fuzz/README.md](tools/Fuzz/README.md).

## Documentation

- [docs/formats](docs/formats/README.md): every format read or written, specified well enough to implement without the
  source, each rule tagged with where it comes from.
- [docs/cli.md](docs/cli.md): the command line, the MCP server and the shell.
- [docs/PLAN.md](docs/PLAN.md): goals, architecture, decisions and open work.
- [design/](design/APP-DESIGN-BRIEF.md): the app's design brief, tokens and icon.

## Licence

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). No Apple code, fonts or files are
included.
