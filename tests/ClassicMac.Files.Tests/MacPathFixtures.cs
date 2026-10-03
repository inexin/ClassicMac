using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// A disk for the file commands' tests (docs/cli.md): disk.img, an HFS volume "Disk" holding
//   System Folder:Finder      FNDR/MACS, data "finder", resources 'STR ' 128 "Greeting" and 'TEXT' 128, invisible
//   System Folder:Read Me     TEXT/ttxt, "Hello\rWorld" (Mac OS Roman, with "é")
//   Inner.img                 rohd/ddsk, an HFS volume "Inner" holding Deep:Note (TEXT/ttxt "deep note")
//   Archive.bin               a MacBinary II file of "Packed" (APPL/PACK, "packed", resources 'STR ' 1; created
//                             1999-01-24 05:20:00, modified a minute later)
//   A/B                       a name with a slash
//   Empty                     a folder with nothing in it
internal static class MacPathFixtures
{
    public static byte[] Fork(params (string Type, short Id, string? Name, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, name, data) in resources)
        {
            var resource = new Resource(FourCC.FromString(type), id, data);
            if (name is not null)
            {
                resource.Name = MacString.FromMacRoman(name);
            }

            fork.Add(resource);
        }

        return fork.ToArray();
    }

    public static string Disk(string folder)
    {
        var inner = new HfsBuilder();
        var deep = inner.Folder(HfsBuilder.Root, "Deep");
        inner.File(deep, "Note", "deep note"u8.ToArray(), []);
        var disk = new HfsBuilder { CatalogLeaves = 4 };
        var system = disk.Folder(HfsBuilder.Root, "System Folder");
        disk.File(system, "Finder", "finder"u8.ToArray(), Fork(("STR ", 128, "Greeting", [2, (byte)'H', (byte)'i']), ("TEXT", 128, null, "text"u8.ToArray())),
            info: new FinderInfo { Type = FourCC.FromString("FNDR"), Creator = FourCC.FromString("MACS"), Flags = FinderFlags.IsInvisible | FinderFlags.HasBundle });
        disk.File(system, "Read Me", [.. "Hello\rWorld "u8, 0x8E], []);
        disk.File(HfsBuilder.Root, "Inner.img", inner.Build("Inner"), [], type: "rohd", creator: "ddsk");
        disk.File(HfsBuilder.Root, "Archive.bin",
            Fixtures.MacBinary(2, "Packed", "packed"u8.ToArray(), Fork(("STR ", 1, null, [1, (byte)'p'])), type: "APPL", creator: "PACK",
                created: 3_000_000_000, modified: 3_000_000_060), [],
            type: "BINA", creator: "SITx");
        disk.File(HfsBuilder.Root, "A/B", "slash"u8.ToArray(), []);
        disk.Folder(HfsBuilder.Root, "Empty");
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }
}
