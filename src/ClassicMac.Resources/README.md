# ClassicMac.Resources

Classic Mac OS resource forks: read, edit and write them as the Resource Manager does, with no dependencies beyond
ClassicMac.Core.

- **`ResourceFork`**: read from a stream or bytes, find resources by type and ID, add, remove, renumber; written back
  as the Resource Manager's compaction leaves a fork. Duplicates keep the first, as `GetResource` does. Mac OS 9's
  model by default, the 68k ROM's on request (`ReadOptions`).
- **Compressed resources:** the System's `dcmp` 0–3 decompressors; others plug in through `IResourceDecompressor`.
- **Editing:** `EditSession` applies `IResourceEdit`s (add, delete, duplicate, set data and info) with undo and redo,
  checked by the Resource Manager's rules.
- **Export:** `ResourceExporter` writes a fork's resources to a folder with a versioned JSON manifest, decoded by any
  `IResourceDecoder`s (the built-in ones are in ClassicMac.Resources.Decoders); `ResourcePacker` rebuilds the fork
  from such a folder.
- **Rez:** `RezWriter` writes a fork as MPW DeRez does; `RezCompiler` compiles `data`, `read` and `include`
  statements.
- **Aliases:** `AliasRecord` reads `alis` records.

```csharp
using var stream = File.OpenRead("App.rsrc");
var fork = ResourceFork.Read(stream);
foreach (var resource in fork.OfType(FourCC.FromString("PICT")))
{
    Console.WriteLine($"{resource.Id} {resource.Name} {resource.GetData().Length} bytes");
}
```

The format: [resource-fork.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/resource-fork.md),
[compressed-resources.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/resources/compressed-resources.md),
the manifest: [export-manifest.md](https://github.com/inexin/ClassicMac/blob/main/docs/formats/output/export-manifest.md).

Part of [ClassicMac](https://github.com/inexin/ClassicMac). MIT.
