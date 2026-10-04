using ClassicMac.Core;
using ClassicMac.Files.Checksums;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// Saving edited resources back into the containers they came from.
public sealed class ForkSaverTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cm-save-" + Guid.NewGuid().ToString("N"));
    private static readonly FourCC Str = FourCC.FromString("STR ");

    public ForkSaverTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static ResourceFork Fork(byte value)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, new byte[] { value, value }) { Name = MacString.FromMacRoman("s") });
        return fork;
    }

    private static MacFile File(ResourceFork fork) => new()
    {
        Name = MacString.FromMacRoman("Edited File"),
        FinderInfo = FinderInfo.Read([.. "TEXTttxt"u8, .. new byte[24]]),
        DataFork = ForkData.FromBytes(new byte[] { 1, 2, 3 }),
        ResourceFork = ForkData.FromBytes(fork.ToArray()),
    };

    // Opens a host file as the app does; the file whose resources are edited is the input or the one file it holds.
    private static (SaveLocation? Location, HostFile Host) Open(string path, bool inData = false)
    {
        var host = HostFiles.Read(path);
        var root = ContainerUnwrapper.Default.Unwrap(host.File, HostFiles.FormatName(host.Layout), new ContainerContext());
        var file = root.Children.Count == 1 ? root.Children[0] : root;
        return (ForkSaver.Locate(path, host, root, file, inData), host);
    }

    private static ResourceFork ReadBack(string path)
    {
        var (location, _) = Open(path);
        var file = location!.File;
        return ResourceFork.Read((location.ForkInDataFork ? file.DataFork : file.ResourceFork).ToArray());
    }

    [Theory]
    [InlineData(SaveAsFormat.MacBinary, SaveTarget.MacBinary)]
    [InlineData(SaveAsFormat.BinHex, SaveTarget.BinHex)]
    [InlineData(SaveAsFormat.AppleSingle, SaveTarget.AppleSingle)]
    [InlineData(SaveAsFormat.AppleDoublePair, SaveTarget.AppleDoubleHeader)]
    [InlineData(SaveAsFormat.BasiliskEntry, SaveTarget.BasiliskResourceFork)]
    public void Edits_save_back_into_each_container(SaveAsFormat format, SaveTarget target)
    {
        var path = Path.Combine(directory, "Edited File");
        ForkSaver.SaveAs(path, format, File(Fork(1)), Fork(1));
        var (location, _) = Open(path);
        Assert.Equal(target, location!.Target);

        var saved = ForkSaver.Save(location, Fork(2));

        Assert.Equal(new byte[] { 2, 2 }, ReadBack(path).Find(Str, 128)!.GetData().ToArray());
        Assert.True(System.IO.File.Exists(location.Path + ".orig"));
        var reopened = Open(path).Location!.File;
        Assert.Equal(new byte[] { 1, 2, 3 }, reopened.DataFork.ToArray());   // the rest written back as read
        Assert.Equal("TEXT", reopened.FinderInfo.Type.ToString());
        // A second save keeps the first .orig.
        var orig = System.IO.File.ReadAllBytes(location.Path + ".orig");
        ForkSaver.Save(saved, Fork(3));
        Assert.Equal(orig, System.IO.File.ReadAllBytes(location.Path + ".orig"));
        Assert.Equal(new byte[] { 3, 3 }, ReadBack(path).Find(Str, 128)!.GetData().ToArray());
    }

    [Fact]
    public void A_MacBinary_II_file_is_saved_as_MacBinary_III()
    {
        var path = Path.Combine(directory, "old.bin");
        var bytes = MacBinaryWriter.ToArray(File(Fork(1)));
        bytes[102] = bytes[103] = bytes[104] = bytes[105] = 0;   // no 'mBIN': MacBinary II
        bytes[122] = 129;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(124), Crc16Xmodem.Compute(bytes.AsSpan(0, 124)));
        System.IO.File.WriteAllBytes(path, bytes);
        var (location, _) = Open(path);
        Assert.Equal(SaveTarget.MacBinary, location!.Target);

        ForkSaver.Save(location, Fork(2));

        Assert.True(MacBinaryReader.III.CanRead(ForkData.FromFile(path)));
    }

    [Fact]
    public void A_raw_fork_file_saves_as_a_fork()
    {
        var path = Path.Combine(directory, "Stuff.rsrc");
        System.IO.File.WriteAllBytes(path, Fork(1).ToArray());
        var (location, _) = Open(path, inData: true);
        Assert.Equal(SaveTarget.RawFork, location!.Target);
        ForkSaver.Save(location, Fork(4));
        Assert.Equal(new byte[] { 4, 4 }, ResourceFork.Read(System.IO.File.ReadAllBytes(path)).Find(Str, 128)!.GetData().ToArray());
    }

    [Fact]
    public void A_file_changed_on_disk_is_not_overwritten_without_asking()
    {
        var path = Path.Combine(directory, "f.bin");
        ForkSaver.SaveAs(path, SaveAsFormat.MacBinary, File(Fork(1)), Fork(1));
        var (location, _) = Open(path);
        System.IO.File.AppendAllText(path, "x");

        Assert.Throws<FileChangedException>(() => ForkSaver.Save(location!, Fork(2)));
        ForkSaver.Save(location!, Fork(2), overwriteChanged: true);
    }
}
