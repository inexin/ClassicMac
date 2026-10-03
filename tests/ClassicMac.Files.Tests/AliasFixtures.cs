using ClassicMac.Core;

namespace ClassicMac.Files.Tests;

// An HFS volume "Aliases" with alias files (docs/formats/resources/aliases.md): "Note alias" to Docs:Note, "Moved alias"
// recorded when Note was in Old (found by its ID), "Gone alias" whose original is missing, "Stuff alias" to the folder
// Stuff, and "Chain alias" to "Note alias".
internal static class AliasFixtures
{
    public static readonly FinderInfo AliasInfo = new()
    {
        Type = FourCC.FromString("TEXT"),
        Creator = FourCC.FromString("ttxt"),
        Flags = FinderFlags.IsAlias,
    };

    public static string Disk(string folder, string name = "aliases.img")
    {
        var disk = new HfsBuilder { CatalogLeaves = 4 };
        var docs = disk.Folder(HfsBuilder.Root, "Docs");
        var old = disk.Folder(HfsBuilder.Root, "Old");
        var stuff = disk.Folder(HfsBuilder.Root, "Stuff");
        var note = disk.File(docs, "Note", "Hello"u8.ToArray(), []);
        disk.File(stuff, "Thing", [1], []);
        var noteAlias = disk.File(HfsBuilder.Root, "Note alias", [], AliasBuilder.Fork(
            AliasBuilder.Alias("Aliases", docs, "Note", note, path: "Aliases:Docs:Note", parentName: "Docs", folderIds: [docs])), info: AliasInfo);
        disk.File(HfsBuilder.Root, "Moved alias", [], AliasBuilder.Fork(
            AliasBuilder.Alias("Aliases", old, "Note", note, path: "Aliases:Old:Note", parentName: "Old", folderIds: [old])), info: AliasInfo);
        disk.File(HfsBuilder.Root, "Gone alias", [], AliasBuilder.Fork(
            AliasBuilder.Alias("Aliases", old, "Gone", 999, path: "Aliases:Old:Gone", parentName: "Old", folderIds: [old])), info: AliasInfo);
        disk.File(HfsBuilder.Root, "Stuff alias", [], AliasBuilder.Fork(
            AliasBuilder.Alias("Aliases", 2, "Stuff", stuff, kind: 1, type: "\0\0\0\0", creator: "\0\0\0\0", path: "Aliases:Stuff")),
            info: AliasInfo with { Type = FourCC.FromString("fdrp"), Creator = FourCC.FromString("MACS") });
        disk.File(HfsBuilder.Root, "Chain alias", [], AliasBuilder.Fork(
            AliasBuilder.Alias("Aliases", 2, "Note alias", noteAlias, path: "Aliases:Note alias")), info: AliasInfo);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, disk.Build("Aliases"));
        return path;
    }
}
