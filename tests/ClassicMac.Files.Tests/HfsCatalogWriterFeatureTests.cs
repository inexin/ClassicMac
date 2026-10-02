using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

public sealed class HfsCatalogWriterFeatureTests
{
    [Fact]
    public void CreatingFileAddsBothForksAndUpdatesVolumeCounts()
    {
        byte[] existingData = Bytes(90, 1);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Existing", existingData, Array.Empty<byte>());
        byte[] source = WithFreeSpace(builder.Build("Volume"));
        byte[] original = (byte[])source.Clone();
        byte[] data = Bytes(800, 2);
        byte[] resource = Bytes(300, 3);
        FinderInfo finder = FinderInfo.Read([.. "TEXTttxt"u8, .. new byte[24]]);

        byte[] output = HfsWriter.CreateFile(ForkData.FromBytes(source), "New", data, resource, finder);

        Assert.Equal(original, source);
        Assert.Equal(data, File(output, "New").DataFork.ToArray());
        Assert.Equal(resource, File(output, "New").ResourceFork.ToArray());
        Assert.Equal(existingData, File(output, "Existing").DataFork.ToArray());
        Assert.Equal(finder.Type, File(output, "New").FinderInfo.Type);
        Assert.Equal(finder.Creator, File(output, "New").FinderInfo.Creator);
        Assert.Equal(U32(source, 0x54) + 1, U32(output, 0x54));
        Assert.Equal(U32(source, 0x1E) + 1, U32(output, 0x1E));
        Assert.Equal(U16(source, 0x0C) + 1, U16(output, 0x0C));
        Assert.Equal(2, FolderValence(output, HfsBuilder.Root));
        if (Environment.GetEnvironmentVariable("CLASSICMAC_HFS_SYNTHETIC_OUTPUT") is { Length: > 0 } path)
            System.IO.File.WriteAllBytes(path, output);
    }

    [Fact]
    public void CreatingFilesAndFoldersCanPreserveSuppliedMacDates()
    {
        byte[] source = WithFreeSpace(new HfsBuilder().Build("Volume"));
        MacDate created = new(2_500_000_000);
        MacDate modified = new(2_600_000_000);

        byte[] withFolder = HfsWriter.CreateFolder(ForkData.FromBytes(source), "Archive", created, modified);
        byte[] withFile = HfsWriter.CreateFile(ForkData.FromBytes(withFolder), "Archive:Document",
            "data"u8.ToArray(), "resource"u8.ToArray(), FinderInfo.Empty, created, modified);

        MacFile file = File(withFile, "Archive:Document");
        Assert.Equal(created, file.Created);
        Assert.Equal(modified, file.Modified);
        Assert.Equal("data"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("resource"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.Equal(created.Seconds, FolderDate(withFile, 16, 10));
        Assert.Equal(modified.Seconds, FolderDate(withFile, 16, 14));
    }

    [Fact]
    public void DeletingFileReclaimsBothForksAndPreservesOtherFiles()
    {
        byte[] otherData = Bytes(120, 4);
        byte[] otherResource = Bytes(60, 5);
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", Bytes(1100, 6), Bytes(600, 7));
        builder.File(HfsBuilder.Root, "Other", otherData, otherResource);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        byte[] output = HfsWriter.DeleteFile(ForkData.FromBytes(source), "Target");

        Assert.Equal(original, source);
        Assert.DoesNotContain(Read(output), file => file.MacPath == "Target");
        Assert.Equal(otherData, File(output, "Other").DataFork.ToArray());
        Assert.Equal(otherResource, File(output, "Other").ResourceFork.ToArray());
        Assert.Equal(U32(source, 0x54) - 1, U32(output, 0x54));
        Assert.Equal(U16(source, 0x0C) - 1, U16(output, 0x0C));
        Assert.Equal(U16(source, 0x22) + 5, U16(output, 0x22));
        Assert.Equal(1, FolderValence(output, HfsBuilder.Root));
    }

    [Fact]
    public void DeletingAFileAlsoRemovesItsOptionalCatalogThread()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", "data"u8.ToArray(), Array.Empty<byte>(), thread: true);
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();
        int catalog = U16(source, 0x1C) * HfsBuilder.Block + U16(source, 0x96) * HfsBuilder.Block;
        uint recordsBefore = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(catalog + 14 + 6));

        byte[] output = HfsWriter.DeleteFile(ForkData.FromBytes(source), "Target");

        Assert.Equal(original, source);
        Assert.Empty(Read(output));
        Assert.Equal(recordsBefore - 2, BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(catalog + 14 + 6)));
    }

    // Names with control characters (a folder's "Icon\r", folder art named with a tab) are found by their Mac OS
    // Roman bytes, as the catalog stores them.
    [Fact]
    public void FilesNamedWithControlCharactersCanBeDeleted()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Icon\r", [], Bytes(HfsBuilder.Block, 41));
        builder.File(HfsBuilder.Root, "\t", Bytes(HfsBuilder.Block, 42), []);
        builder.File(HfsBuilder.Root, "Other", [7], []);
        byte[] source = builder.Build("Volume");

        byte[] output = HfsWriter.DeleteFile(ForkData.FromBytes(HfsWriter.DeleteFile(ForkData.FromBytes(source), "Icon\r")), "\t");

        Assert.Equal(["Other"], Read(output).Select(f => f.MacPath));
    }

    [Fact]
    public void DeletingAFragmentedFileReclaimsOverflowExtentsAndPreservesOtherFiles()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Fragmented", Bytes(5 * HfsBuilder.Block, 31),
            Bytes(2 * HfsBuilder.Block, 32), fragments: 5);
        byte[] survivor = Bytes(300, 33);
        builder.File(HfsBuilder.Root, "Survivor", survivor, Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        byte[] output = HfsWriter.DeleteFile(ForkData.FromBytes(source), "Fragmented");

        Assert.Equal(original, source);
        Assert.Equal(survivor, File(output, "Survivor").DataFork.ToArray());
        Assert.DoesNotContain(Read(output), file => file.MacPath == "Fragmented");
        Assert.Equal(U16(source, 0x22) + 7, U16(output, 0x22));
    }

    [Fact]
    public void FolderAndContainedFileCanBeCreatedThenRemoved()
    {
        // JotaRandom/hfsutils test_hfsutils.sh exercises mkdir, copy in, list, and copy out.
        byte[] source = WithFreeSpace(new HfsBuilder().Build("Volume"));
        byte[] original = (byte[])source.Clone();

        byte[] withFolder = HfsWriter.CreateFolder(ForkData.FromBytes(source), "Documents");
        byte[] withFile = HfsWriter.CreateFile(ForkData.FromBytes(withFolder), "Documents:Notes",
            "Hello"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
        Assert.Equal("Hello"u8.ToArray(), File(withFile, "Documents:Notes").DataFork.ToArray());
        Assert.Equal(U32(source, 0x58) + 1, U32(withFile, 0x58));
        Assert.Equal(1, FolderValence(withFolder, HfsBuilder.Root));
        Assert.Equal(1, FolderValence(withFile, 16));

        byte[] withoutFile = HfsWriter.DeleteFile(ForkData.FromBytes(withFile), "Documents:Notes");
        byte[] withoutFolder = HfsWriter.DeleteFolder(ForkData.FromBytes(withoutFile), "Documents");

        Assert.Equal(original, source);
        Assert.Empty(Read(withoutFolder));
        Assert.Equal(U32(source, 0x58), U32(withoutFolder, 0x58));
        Assert.Equal(U32(source, 0x54), U32(withoutFolder, 0x54));
        Assert.Equal(0, FolderValence(withoutFolder, HfsBuilder.Root));
    }

    [Fact]
    public void ExistingEmptyFolderCanBeDeletedWithItsThread()
    {
        var builder = new HfsBuilder();
        builder.Folder(HfsBuilder.Root, "Unused");
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        byte[] output = HfsWriter.DeleteFolder(ForkData.FromBytes(source), "Unused");

        Assert.Equal(original, source);
        Assert.Empty(Read(output));
        Assert.Equal(0, FolderValence(output, HfsBuilder.Root));
        Assert.Equal(U32(source, 0x58) - 1, U32(output, 0x58));
    }

    [Fact]
    public void HfsPlusWrapperRejectsEveryCatalogEditWithoutChangingTheSource()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Target", "data"u8.ToArray(), Array.Empty<byte>());
        builder.Folder(HfsBuilder.Root, "Unused");
        byte[] source = builder.Build("Volume");
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(2 * HfsBuilder.Block + 0x7C), 0x482B);
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFile(ForkData.FromBytes(source),
            "New", Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty));
        Assert.Throws<InvalidDataException>(() => HfsWriter.DeleteFile(ForkData.FromBytes(source), "Target"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFolder(ForkData.FromBytes(source), "New"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.DeleteFolder(ForkData.FromBytes(source), "Unused"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void NonemptyFolderCannotBeDeleted()
    {
        var builder = new HfsBuilder();
        uint folder = builder.Folder(HfsBuilder.Root, "Documents");
        builder.File(folder, "Notes", Bytes(5, 8), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() =>
            HfsWriter.DeleteFolder(ForkData.FromBytes(source), "Documents"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void DuplicateNamesAreRejectedWithoutChangingTheImage()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Report", Bytes(8, 1), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFile(ForkData.FromBytes(source),
            "report", Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty));
        Assert.Equal(original, source);
    }

    [Fact]
    public void CatalogUsesClassicHfsOrderForAccentedNames()
    {
        string? external = Environment.GetEnvironmentVariable("CLASSICMAC_HFS_NAMES_INPUT");
        byte[] image = string.IsNullOrEmpty(external)
            ? WithFreeSpace(new HfsBuilder().Build("Volume"))
            : System.IO.File.ReadAllBytes(external);
        string[] names = ["Á", "F", "Éa", "á", "Ez", "ª", "º"];
        foreach (string name in names)
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), name,
                Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty);

        Assert.Equal(new[] { "á", "ª", "Ez", "Éa", "F", "º", "Á" },
            Read(image).Select(file => file.Name.ToString()).Where(names.Contains));
        if (Environment.GetEnvironmentVariable("CLASSICMAC_HFS_NAMES_OUTPUT") is { Length: > 0 } path)
            System.IO.File.WriteAllBytes(path, image);
    }

    [Theory]
    [InlineData("report", "Report")]
    [InlineData(" ", "\u00A0")]
    [InlineData("ä", "Ä")]
    [InlineData("œ", "Œ")]
    public void RelStringEquivalentNamesCannotShareAFolder(string first, string second)
    {
        byte[] source = WithFreeSpace(new HfsBuilder().Build("Volume"));
        byte[] image = HfsWriter.CreateFile(ForkData.FromBytes(source), first,
            Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty);

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFile(ForkData.FromBytes(image), second,
            Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty));
        Assert.Single(Read(image));
    }

    [Fact]
    public void LockedFileCannotBeDeleted()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Locked", Bytes(8, 2), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        source[builder.FirstFileRecordOffset + 2] |= 1;
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.DeleteFile(ForkData.FromBytes(source), "Locked"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void NamesLongerThanTheHfsLimitAreRejected()
    {
        byte[] source = new HfsBuilder().Build("Volume");
        byte[] original = (byte[])source.Clone();

        Assert.Throws<ArgumentException>(() => HfsWriter.CreateFolder(ForkData.FromBytes(source), new string('A', 32)));
        Assert.Equal(original, source);
    }

    [Fact]
    public void ACorruptCatalogNodeMapIsRejectedBeforeEditing()
    {
        byte[] source = new HfsBuilder().Build("Volume");
        int catalog = U16(source, 0x1C) * HfsBuilder.Block + U16(source, 0x96) * HfsBuilder.Block;
        BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(catalog + 14 + 26), 1);
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFolder(ForkData.FromBytes(source), "New"));
        Assert.Throws<InvalidDataException>(() => HfsWriter.ReplaceFork(ForkData.FromBytes(source),
            "Existing", HfsFork.Data, "new data"u8.ToArray()));
        Assert.Equal(original, source);
    }

    [Fact]
    public void CatalogBlocksMarkedFreeAreRejectedBeforeEditing()
    {
        byte[] source = new HfsBuilder().Build("Volume");
        int bitmap = U16(source, 0x0E) * HfsBuilder.Block;
        int catalogBlock = U16(source, 0x96);
        source[bitmap + catalogBlock / 8] &= (byte)~(0x80 >> (catalogBlock % 8));
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFolder(ForkData.FromBytes(source), "New"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void IncorrectVolumeFreeBlockCountIsRejectedBeforeEditing()
    {
        byte[] source = new HfsBuilder().Build("Volume");
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(2 * HfsBuilder.Block + 0x22),
            checked((ushort)(U16(source, 0x22) + 1)));
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFolder(ForkData.FromBytes(source), "New"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void InconsistentParentValenceIsRejectedBeforeEditing()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Existing", Array.Empty<byte>(), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        int firstLeaf = U16(source, 0x1C) * HfsBuilder.Block +
            U16(source, 0x96) * HfsBuilder.Block + HfsBuilder.Block;
        int firstRecord = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(firstLeaf + HfsBuilder.Block - 2));
        int data = (firstLeaf + firstRecord + 1 + source[firstLeaf + firstRecord] + 1) & ~1;
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(data + 4), 0);
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFile(ForkData.FromBytes(source), "New",
            Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(17u)]
    public void DuplicateCatalogIdIsRejectedBeforeEditing(uint duplicateId)
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Existing", "data"u8.ToArray(), Array.Empty<byte>());
        builder.File(HfsBuilder.Root, "Other", "other"u8.ToArray(), Array.Empty<byte>());
        byte[] source = builder.Build("Volume");
        BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(builder.FirstFileRecordOffset + 20), duplicateId);
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFolder(ForkData.FromBytes(source), "New"));
        Assert.Equal(original, source);
    }

    [Fact]
    public void ExhaustedVolumeRejectsFileCreationWithoutChangingTheSource()
    {
        byte[] source = new HfsBuilder().Build("Volume");
        byte[] original = (byte[])source.Clone();

        Assert.Throws<InvalidDataException>(() => HfsWriter.CreateFile(ForkData.FromBytes(source), "New",
            "Needs a block"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty));
        Assert.Equal(original, source);
    }

    [Fact]
    public void ExternalVolumeCatalogMutationsReopen()
    {
        string? sourcePath = Environment.GetEnvironmentVariable("CLASSICMAC_HFS_INTEROP_INPUT");
        string? outputPath = Environment.GetEnvironmentVariable("CLASSICMAC_HFS_CATALOG_INTEROP_OUTPUT");
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(outputPath)) return;

        byte[] source = System.IO.File.ReadAllBytes(sourcePath);
        byte[] withFolder = HfsWriter.CreateFolder(ForkData.FromBytes(source), "Created");
        byte[] withFile = HfsWriter.CreateFile(ForkData.FromBytes(withFolder), "Created:Added",
            "Created by ClassicMac"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
        byte[] withoutOriginal = HfsWriter.DeleteFile(ForkData.FromBytes(withFile), "Target");

        Assert.Equal(source, System.IO.File.ReadAllBytes(sourcePath));
        Assert.Equal("Created by ClassicMac"u8.ToArray(), File(withoutOriginal, "Created:Added").DataFork.ToArray());
        Assert.DoesNotContain(Read(withoutOriginal), file => file.MacPath == "Target");
        System.IO.File.WriteAllBytes(outputPath, withoutOriginal);
    }

    [Fact]
    public void CreatingManyFilesGrowsTheCatalogTree()
    {
        string? external = Environment.GetEnvironmentVariable("CLASSICMAC_HFS_CATALOG_STRESS_INPUT");
        byte[] source = string.IsNullOrEmpty(external)
            ? WithFreeSpace(new HfsBuilder().Build("Volume"))
            : System.IO.File.ReadAllBytes(external);
        byte[] original = (byte[])source.Clone();
        uint catalogLengthBefore = U32(source, 0x92);
        int filesBefore = Read(source).Count;
        int fileCount = string.IsNullOrEmpty(external) ? 16 : 80;
        byte[] image = source;

        for (int index = 0; index < fileCount; index++)
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"File{index:D2}",
                new byte[] { checked((byte)index) }, Array.Empty<byte>(), FinderInfo.Empty);

        Assert.Equal(original, source);
        Assert.Equal(filesBefore + fileCount, Read(image).Count);
        for (int index = 0; index < fileCount; index++)
            Assert.Equal(new byte[] { checked((byte)index) }, File(image, $"File{index:D2}").DataFork.ToArray());
        if (string.IsNullOrEmpty(external)) Assert.True(U32(image, 0x92) > catalogLengthBefore);
        Assert.Equal(U32(source, 0x54) + fileCount, U32(image, 0x54));
        if (Environment.GetEnvironmentVariable("CLASSICMAC_HFS_CATALOG_STRESS_OUTPUT") is { Length: > 0 } path)
            System.IO.File.WriteAllBytes(path, image);
    }

    [Fact]
    public void CatalogGrowthAddsMapNodesWhenTheHeaderMapIsFull()
    {
        byte[] source = WithFreeSpace(new HfsBuilder().Build("Volume"));
        int filesBefore = Read(source).Count;
        int catalogHeader = U16(source, 0x1C) * HfsBuilder.Block +
            U16(source, 0x96) * checked((int)U32(source, 0x14));
        int mapStart = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(catalogHeader + HfsBuilder.Block - 6));
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(catalogHeader + HfsBuilder.Block - 8),
            checked((ushort)(mapStart + 2)));
        byte[] original = (byte[])source.Clone();
        byte[] image = source;

        for (int index = 0; index < 128; index++)
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"Entry{index:D2}",
                Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty);

        Assert.Equal(original, source);
        Assert.Equal(filesBefore + 128, Read(image).Count);
        uint mapNode = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(catalogHeader));
        Assert.NotEqual(0u, mapNode);
        int mapOffset = catalogHeader + checked((int)mapNode * HfsBuilder.Block);
        Assert.Equal(2, image[mapOffset + 8]);
        Assert.Equal(14, BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mapOffset + HfsBuilder.Block - 2)));
        Assert.Equal(506, BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(mapOffset + HfsBuilder.Block - 4)));
    }

    [Fact]
    public void CatalogGrowthUsesOverflowExtentsWhenThreePrimaryRunsAreFull()
    {
        string? external = Environment.GetEnvironmentVariable("CLASSICMAC_HFS_OVERFLOW_INPUT");
        byte[] source = string.IsNullOrEmpty(external)
            ? WithFreeSpace(new HfsBuilder().Build("Volume"))
            : System.IO.File.ReadAllBytes(external);
        int filesBefore = Read(source).Count;
        int bitmap = U16(source, 0x0E) * HfsBuilder.Block;
        int reserved = 0;
        for (int block = 5; block < U16(source, 0x12); block += 2)
        {
            byte mask = (byte)(0x80 >> (block % 8));
            if ((source[bitmap + block / 8] & mask) != 0) continue;
            source[bitmap + block / 8] |= mask;
            reserved++;
        }
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(2 * HfsBuilder.Block + 0x22),
            checked((ushort)(U16(source, 0x22) - reserved)));
        byte[] original = (byte[])source.Clone();
        byte[] image = source;

        for (int index = 0; index < 128; index++)
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"Entry{index:D2}",
                Array.Empty<byte>(), Array.Empty<byte>(), FinderInfo.Empty);

        Assert.Equal(original, source);
        Assert.Equal(filesBefore + 128, Read(image).Count);
        Assert.Equal(3, Enumerable.Range(0, 3).Count(index => U16(image, 0x96 + index * 4 + 2) != 0));
        int primaryBlocks = Enumerable.Range(0, 3).Sum(index => U16(image, 0x96 + index * 4 + 2));
        Assert.True(U32(image, 0x92) > primaryBlocks * U32(image, 0x14));

        byte[] edited = HfsWriter.ReplaceFork(ForkData.FromBytes(image), "Entry127",
            HfsFork.Data, "Overflow catalog"u8.ToArray());
        Assert.Equal("Overflow catalog"u8.ToArray(), File(edited, "Entry127").DataFork.ToArray());
        Assert.Equal(filesBefore + 128, Read(edited).Count);
        if (Environment.GetEnvironmentVariable("CLASSICMAC_HFS_OVERFLOW_OUTPUT") is { Length: > 0 } path)
            System.IO.File.WriteAllBytes(path, edited);
    }

    [Fact]
    public void DeepCatalogCanBeReopenedAndHaveAlternatingFilesDeleted()
    {
        // Distrotech/hfsutils test2.tcl stresses nested directories, many files, a remount, and partial deletion.
        const int topLevelCount = 4;
        const int middleLevelCount = 4;
        const int leafCount = 5;
        byte[] image = WithFreeSpace(new HfsBuilder().Build("Volume"));
        var expected = new Dictionary<string, (byte[] Data, byte[] Resource)>(StringComparer.Ordinal);

        for (int top = 0; top < topLevelCount; top++)
        {
            string topName = $"Top{top:D2}";
            image = HfsWriter.CreateFolder(ForkData.FromBytes(image), topName);
            for (int middle = 0; middle < middleLevelCount; middle++)
            {
                string middleName = $"Middle{middle:D2}";
                string middlePath = $"{topName}:{middleName}";
                image = HfsWriter.CreateFolder(ForkData.FromBytes(image), middlePath);
                for (int leaf = 0; leaf < leafCount; leaf++)
                {
                    string leafName = $"Leaf{leaf:D2}";
                    string leafPath = $"{middlePath}:{leafName}";
                    image = HfsWriter.CreateFolder(ForkData.FromBytes(image), leafPath);
                    string filePath = $"{leafPath}:File";
                    byte[] data = System.Text.Encoding.ASCII.GetBytes($"data:{filePath}");
                    byte[] resource = System.Text.Encoding.ASCII.GetBytes($"resource:{filePath}");
                    image = HfsWriter.CreateFile(ForkData.FromBytes(image), filePath, data, resource,
                        FinderInfo.Empty);
                    expected.Add(filePath, (data, resource));
                }
            }
        }

        var reopened = Read(image).ToDictionary(file => file.MacPath, StringComparer.Ordinal);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), reopened.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, forks) in expected)
        {
            Assert.Equal(forks.Data, reopened[path].DataFork.ToArray());
            Assert.Equal(forks.Resource, reopened[path].ResourceFork.ToArray());
        }

        int index = 0;
        foreach (string path in expected.Keys.ToArray())
        {
            if (index++ % 2 == 0)
            {
                image = HfsWriter.DeleteFile(ForkData.FromBytes(image), path);
                expected.Remove(path);
            }
        }

        reopened = Read(image).ToDictionary(file => file.MacPath, StringComparer.Ordinal);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), reopened.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, forks) in expected)
        {
            Assert.Equal(forks.Data, reopened[path].DataFork.ToArray());
            Assert.Equal(forks.Resource, reopened[path].ResourceFork.ToArray());
        }
    }

    [Theory]
    [InlineData(20260930)]
    [InlineData(20261001)]
    [InlineData(20261002)]
    public void InterleavedCatalogAndForkEditsPreserveTheWholeVolume(int seed)
    {
        // Distrotech/hfsutils test1.tcl interleaves file and fork growth across reopens.
        byte[] original = WithFreeSpace(new HfsBuilder().Build("Volume"));
        byte[] originalSnapshot = (byte[])original.Clone();
        byte[] image = HfsWriter.CreateFolder(ForkData.FromBytes(original), "Documents");
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "Archive");
        var expected = new Dictionary<string, (byte[] Data, byte[] Resource)>(StringComparer.Ordinal);
        var random = new Random(seed);

        for (int step = 0; step < 80; step++)
        {
            byte[] previous = image;
            byte[] snapshot = (byte[])previous.Clone();
            if (expected.Count < 5 || (expected.Count < 18 && random.Next(3) == 0))
            {
                string folder = random.Next(2) == 0 ? "Documents" : "Archive";
                string path = $"{folder}:Item{step:D2}";
                byte[] data = Bytes(random.Next(0, 900), step);
                byte[] resource = Bytes(random.Next(0, 700), step + 1);
                image = HfsWriter.CreateFile(ForkData.FromBytes(previous), path, data, resource, FinderInfo.Empty);
                expected.Add(path, (data, resource));
            }
            else
            {
                string path = expected.Keys.ElementAt(random.Next(expected.Count));
                int choice = random.Next(3);
                if (choice == 0)
                {
                    image = HfsWriter.DeleteFile(ForkData.FromBytes(previous), path);
                    expected.Remove(path);
                }
                else
                {
                    byte[] replacement = Bytes(random.Next(0, 1200), step + 2);
                    HfsFork fork = choice == 1 ? HfsFork.Data : HfsFork.Resource;
                    image = HfsWriter.ReplaceFork(ForkData.FromBytes(previous), path, fork, replacement);
                    var old = expected[path];
                    expected[path] = fork == HfsFork.Data
                        ? (replacement, old.Resource) : (old.Data, replacement);
                }
            }

            Assert.Equal(snapshot, previous);
            var actual = Read(image).ToDictionary(file => file.MacPath);
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, forks) in expected)
            {
                Assert.Equal(forks.Data, actual[path].DataFork.ToArray());
                Assert.Equal(forks.Resource, actual[path].ResourceFork.ToArray());
            }
        }

        foreach (string path in expected.Keys.ToArray())
            image = HfsWriter.DeleteFile(ForkData.FromBytes(image), path);
        image = HfsWriter.DeleteFolder(ForkData.FromBytes(image), "Documents");
        image = HfsWriter.DeleteFolder(ForkData.FromBytes(image), "Archive");
        Assert.Empty(Read(image));
        Assert.Equal(originalSnapshot, original);
    }

    private static IReadOnlyList<MacFile> Read(byte[] image) =>
        HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());

    private static MacFile File(byte[] image, string path) =>
        Assert.Single(Read(image), file => file.MacPath == path);

    private static ushort U16(byte[] image, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + offset));

    private static uint U32(byte[] image, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(2 * HfsBuilder.Block + offset));

    private static byte[] Bytes(int length, int seed) =>
        Enumerable.Range(0, length).Select(index => (byte)(index * 37 + seed)).ToArray();

    private static ushort FolderValence(byte[] image, uint folderId) =>
        checked((ushort)FolderField(image, folderId, 4, 2));

    private static uint FolderDate(byte[] image, uint folderId, int offset) =>
        FolderField(image, folderId, offset, 4);

    private static uint FolderField(byte[] image, uint folderId, int fieldOffset, int fieldLength)
    {
        int blockSize = checked((int)U32(image, 0x14));
        int firstBlock = U16(image, 0x1C) * HfsBuilder.Block;
        int catalogBlock = U16(image, 0x96);
        int catalogLength = checked((int)U32(image, 0x92));
        byte[] tree = image.AsSpan(firstBlock + catalogBlock * blockSize, catalogLength).ToArray();
        uint leaf = BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan(24));
        while (leaf != 0)
        {
            int at = checked((int)leaf * HfsBuilder.Block);
            int count = BinaryPrimitives.ReadUInt16BigEndian(tree.AsSpan(at + 10));
            for (int index = 0; index < count; index++)
            {
                int start = BinaryPrimitives.ReadUInt16BigEndian(tree.AsSpan(at + HfsBuilder.Block - 2 * (index + 1)));
                int keyEnd = at + start + 1 + tree[at + start];
                int record = (keyEnd + 1) & ~1;
                if (tree[record] == 1 && BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan(record + 6)) == folderId)
                    return fieldLength == 2
                        ? BinaryPrimitives.ReadUInt16BigEndian(tree.AsSpan(record + fieldOffset))
                        : BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan(record + fieldOffset));
            }
            leaf = BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan(at));
        }
        throw new InvalidDataException($"Folder {folderId} was not found in the catalog.");
    }

    private static byte[] WithFreeSpace(byte[] image)
    {
        const int allocationBlocks = 1600;
        int oldBlocks = U16(image, 0x12);
        int oldFree = U16(image, 0x22);
        Array.Resize(ref image, (HfsBuilder.FirstAllocationBlock + allocationBlocks + 2) * HfsBuilder.Block);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x12), allocationBlocks);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2 * HfsBuilder.Block + 0x22),
            checked((ushort)(oldFree + allocationBlocks - oldBlocks)));
        image.AsSpan(2 * HfsBuilder.Block, HfsBuilder.Block)
            .CopyTo(image.AsSpan(image.Length - 2 * HfsBuilder.Block));
        return image;
    }
}
