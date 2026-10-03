using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Tests;

namespace ClassicMac.Resources.Cli.Tests;

// The volume the write tests change: Docs (holding Letter, with 'STR ' 128 "a") and Read Me ("hello"), with free space
// for new files.
internal static class WritableDisk
{
    public static readonly FourCC Str = FourCC.FromString("STR ");

    public static string Build(string folder, string name = "disk.img")
    {
        var builder = new HfsBuilder();
        var docs = builder.Folder(HfsBuilder.Root, "Docs");
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { 1, (byte)'a' }));
        builder.File(docs, "Letter", "data"u8.ToArray(), fork.ToArray());
        builder.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, WithFreeSpace(builder.Build("Disk")));
        return path;
    }

    private static byte[] WithFreeSpace(byte[] image)
    {
        const int allocationBlocks = 1600;
        int oldBlocks = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12));
        int oldFree = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22));
        Array.Resize(ref image, (HfsBuilder.FirstAllocationBlock + allocationBlocks + 2) * HfsBuilder.Block);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12), allocationBlocks);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22), checked((ushort)(oldFree + allocationBlocks - oldBlocks)));
        image.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block).CopyTo(image.AsSpan(image.Length - 2 * HfsBuilder.Block));
        return image;
    }
}
