# ClassicMac.Resources.Decoders

The built-in decoders that turn classic Mac OS resources and documents into modern files, for ClassicMac.Resources'
exporter, the CLI and the app. Each decodes exactly what the Mac showed or played.

| Area | Resources | Output |
| --- | --- | --- |
| Images | `PICT`, icons and icon families, cursors, patterns, `cicn`, colour tables | PNG (32-bit RGBA, or as on a 1–16 bit screen) or lossless WebP |
| Sound | `snd ` (MACE 3 and 6, IMA4, µ-law, PCM), AIFF | WAV |
| Text | `STR `, `STR#`, `TEXT` with `styl`, `vers` and other text resources, in the file's encoding | UTF-8, RTF, JSON |
| Fonts | `NFNT`, `FONT`, `FOND`, `sfnt` | PNG strikes, JSON metrics, TrueType (made loadable on request) |
| Interface | `DLOG`, `ALRT`, `DITL`, `MENU`, `WIND`, `CNTL` and others, drawn in the Platinum appearance | PNG previews, JSON |
| Finder | Finder windows and icons, `BNDL`/`FREF`, aliases, kinds (with a type/creator database) | PNG, JSON |
| Documents | DOCMaker, SimpleText, Word 4/5/6/98; help pages for previews | HTML folders |
| Code | `CODE`, code resources, PEF fragments | 68k and PowerPC listings (`.s`), `code.json` |
| Templates | `TMPL`s in the file, and ClassicMac's own for common types | Typed fields for editors |

```csharp
var options = new ExportOptions
{
    DecodersFor = ResourceDecoders.CreateFor(new DecodeOptions { ScreenDepth = 8 }),
    DocumentsFor = ResourceDecoders.CreateDocumentConvertersFor(),
};
var source = new ExportSource(file.Name, [], file.FinderInfo.Type, file.FinderInfo.Creator, (ushort)file.FinderInfo.Flags);
ResourceExporter.Export(fork, "out", source, options);
```

`DecodeOptions` chooses the screen depth, the QuickDraw (Mac OS 9 or the 68k ROM), the text encoding and line
endings, the image encoder (PNG or WebP), the largest image and whether fonts are made loadable.
Other decoders plug in through `IResourceDecoder` and `IDocumentConverter`.

The formats: [resources](https://github.com/inexin/ClassicMac/tree/main/docs/formats/resources),
[documents](https://github.com/inexin/ClassicMac/tree/main/docs/formats/documents),
[codecs](https://github.com/inexin/ClassicMac/tree/main/docs/formats/codecs),
[output](https://github.com/inexin/ClassicMac/tree/main/docs/formats/output).

Part of [ClassicMac](https://github.com/inexin/ClassicMac). MIT.
