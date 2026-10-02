using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

public sealed class HfsSaveAsFeatureTests
{
    [Fact]
    public void SaveAsWritesReopenableVolumeAndLeavesSourceUntouched()
    {
        var builder = new HfsBuilder();
        byte[] otherData = "Keep this file"u8.ToArray();
        byte[] otherResource = "Keep this fork"u8.ToArray();
        builder.File(HfsBuilder.Root, "Target", "Target data"u8.ToArray(), Resource(1).ToArray(),
            type: "TEXT", creator: "ttxt");
        builder.File(HfsBuilder.Root, "Other", otherData, otherResource);
        byte[] sourceImage = PadToInteropVolume(builder.Build("Volume"));
        if (Environment.GetEnvironmentVariable("CLASSICMAC_HFS_INTEROP_SOURCE") is { Length: > 0 } sourceArtifact)
        {
            System.IO.File.WriteAllBytes(sourceArtifact, sourceImage);
        }

        string directory = Path.Combine(Path.GetTempPath(), "cm-hfs-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.hfs");
            string outputPath = Path.Combine(directory, "saved.hfs");
            System.IO.File.WriteAllBytes(sourcePath, sourceImage);
            MacFile target = Assert.Single(Read(sourceImage), file => file.MacPath == "Target");

            string savedPath = ForkSaver.SaveHfsImageAs(sourcePath, outputPath, target, Resource(2));

            Assert.Equal(Path.GetFullPath(outputPath), savedPath);
            Assert.Equal(sourceImage, System.IO.File.ReadAllBytes(sourcePath));
            byte[] savedImage = System.IO.File.ReadAllBytes(outputPath);
            if (Environment.GetEnvironmentVariable("CLASSICMAC_HFS_INTEROP_IMAGE") is { Length: > 0 } interopPath)
            {
                System.IO.File.WriteAllBytes(interopPath, savedImage);
            }

            MacFile savedTarget = Assert.Single(Read(savedImage), file => file.MacPath == "Target");
            MacFile savedOther = Assert.Single(Read(savedImage), file => file.MacPath == "Other");
            Assert.Equal("Target data"u8.ToArray(), savedTarget.DataFork.ToArray());
            Assert.Equal(Resource(2).ToArray(), savedTarget.ResourceFork.ToArray());
            Assert.Equal(otherData, savedOther.DataFork.ToArray());
            Assert.Equal(otherResource, savedOther.ResourceFork.ToArray());
            Assert.Equal(target.FinderInfo.Type, savedTarget.FinderInfo.Type);
            Assert.Equal(target.FinderInfo.Creator, savedTarget.FinderInfo.Creator);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectedSaveLeavesSourceAndExistingDestinationUntouched()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", "Data"u8.ToArray(), Resource(1).ToArray());
        byte[] sourceImage = builder.Build("Volume");
        BinaryPrimitives.WriteUInt16BigEndian(sourceImage.AsSpan(2 * HfsBuilder.Block + 0x0A), 0x8000);
        string directory = Path.Combine(Path.GetTempPath(), "cm-hfs-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.hfs");
            string outputPath = Path.Combine(directory, "saved.hfs");
            byte[] destination = "Existing destination"u8.ToArray();
            System.IO.File.WriteAllBytes(sourcePath, sourceImage);
            System.IO.File.WriteAllBytes(outputPath, destination);
            MacFile target = Assert.Single(Read(sourceImage));

            Assert.Throws<InvalidDataException>(() =>
                ForkSaver.SaveHfsImageAs(sourcePath, outputPath, target, Resource(2)));

            Assert.Equal(sourceImage, System.IO.File.ReadAllBytes(sourcePath));
            Assert.Equal(destination, System.IO.File.ReadAllBytes(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAsRefusesToUseTheSourceAsItsDestination()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", "Data"u8.ToArray(), Resource(1).ToArray());
        byte[] sourceImage = builder.Build("Volume");
        string directory = Path.Combine(Path.GetTempPath(), "cm-hfs-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sourcePath = Path.Combine(directory, "source.hfs");
            System.IO.File.WriteAllBytes(sourcePath, sourceImage);
            MacFile target = Assert.Single(Read(sourceImage));

            Assert.Throws<InvalidOperationException>(() =>
                ForkSaver.SaveHfsImageAs(sourcePath, sourcePath, target, Resource(2)));

            Assert.Equal(sourceImage, System.IO.File.ReadAllBytes(sourcePath));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExternalClassicHfsImageCanBeEditedAndReopened()
    {
        string? sourcePath = Environment.GetEnvironmentVariable("CLASSICMAC_HFS_INTEROP_INPUT");
        string? outputPath = Environment.GetEnvironmentVariable("CLASSICMAC_HFS_INTEROP_OUTPUT");
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        byte[] original = System.IO.File.ReadAllBytes(sourcePath);
        MacFile target = Assert.Single(Read(original), file => file.MacPath == "Target");

        ForkSaver.SaveHfsImageAs(sourcePath, outputPath, target, Resource(3));

        Assert.Equal(original, System.IO.File.ReadAllBytes(sourcePath));
        MacFile saved = Assert.Single(Read(System.IO.File.ReadAllBytes(outputPath)), file => file.MacPath == "Target");
        Assert.Equal(target.DataFork.ToArray(), saved.DataFork.ToArray());
        Assert.Equal(Resource(3).ToArray(), saved.ResourceFork.ToArray());
    }

    private static ResourceFork Resource(byte value)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR "), 128, new byte[] { value, value }));
        return fork;
    }

    private static byte[] PadToInteropVolume(byte[] image)
    {
        const int allocationBlocks = 1600;
        int mdb = 2 * HfsBuilder.Block;
        int oldBlocks = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mdb + 0x12));
        int oldFree = BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mdb + 0x22));
        Array.Resize(ref image, (HfsBuilder.FirstAllocationBlock + allocationBlocks + 2) * HfsBuilder.Block);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(mdb + 0x12), allocationBlocks);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(mdb + 0x22),
            checked((ushort)(oldFree + allocationBlocks - oldBlocks)));
        image.AsSpan(mdb, HfsBuilder.Block).CopyTo(image.AsSpan(image.Length - 2 * HfsBuilder.Block));
        return image;
    }

    private static IReadOnlyList<MacFile> Read(byte[] image) =>
        HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());
}
