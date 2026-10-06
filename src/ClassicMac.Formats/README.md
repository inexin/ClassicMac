# ClassicMac.Formats

Classic Mac OS files in .NET: reach the resource fork whatever it is wrapped in, convert resources and documents to
modern formats, edit them and write them back. No dependencies beyond ClassicMac.Core and ClassicMac.Graphics.

```
dotnet add package ClassicMac.Formats
```

The package carries four assemblies, each documented on its own:

| Assembly | What it is |
| --- | --- |
| [ClassicMac.Resources](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Resources) | Resource forks: read, edit, write as the Resource Manager does, `dcmp` 0–3, export with a manifest and back, Rez and DeRez |
| [ClassicMac.Files](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Files) | Mac files and every container they travel in: wrappers, archives, disk images, HFS, HFS Plus, MFS, ISO 9660 and FAT; HFS writing; First Aid |
| [ClassicMac.Code](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Code) | PEF, `cfrg`, 68k applications and code resources; 68k and PowerPC disassemblers |
| [ClassicMac.Resources.Decoders](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Resources.Decoders) | Resources and documents to modern files: pictures, icons, sounds, text, fonts, dialogs and menus, Finder windows, documents to HTML, code listings |

```csharp
// Every file in an archive, with its resources decoded to modern files.
var root = ContainerUnwrapper.Default.Unwrap("Game.sit");
var options = new ExportOptions { DecodersFor = ResourceDecoders.CreateFor() };
foreach (var node in root.Leaves())
{
    var file = node.File;
    if (MacFileResources.Read(file).Fork is { } fork)
    {
        var source = new ExportSource(file.Name, [], file.FinderInfo.Type, file.FinderInfo.Creator, (ushort)file.FinderInfo.Flags);
        ResourceExporter.Export(fork, Path.Combine("out", file.Name.ToString()), source, options);
    }
}
```

Related packages: [ClassicMac.Graphics](https://github.com/inexin/ClassicMac/tree/main/src/ClassicMac.Graphics) (QuickDraw
and PICT on their own), its ImageSharp and SkiaSharp adapters, and the `classicmac` command-line tool
(`dotnet tool install --global ClassicMac.Cli`).

The formats are specified in [docs/formats](https://github.com/inexin/ClassicMac/tree/main/docs/formats). Part of
[ClassicMac](https://github.com/inexin/ClassicMac). MIT.
