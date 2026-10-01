using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Archives;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

public sealed class StuffItFeatureTests
{
    [Fact]
    public void LegacyStuffItArchiveCommentIsReadFromTheSitCResourceAsMacRoman()
    {
        byte[] archive = StuffItFixture.BuildLegacyV2File("Inside", "payload"u8.ToArray(), []);
        var resourceFork = new ResourceFork();
        resourceFork.Add(new Resource(FourCC.FromString("SitC"), 0, new byte[] { 0x43, 0x61, 0x66, 0x8E }));
        var input = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit"),
            DataFork = ForkData.FromBytes(archive),
            ResourceFork = ForkData.FromBytes(resourceFork.ToArray()),
        };
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(input, "Host file",
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal("payload"u8.ToArray(), Assert.Single(result.Leaves()).File.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.comment" &&
            diagnostic.Severity == DiagnosticSeverity.Info && diagnostic.Message == "StuffIt comment: Café");
    }

    [Fact]
    public void LegacyStuffItWithoutASitCResourceDoesNotReportAnArchiveComment()
    {
        var input = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit"),
            DataFork = ForkData.FromBytes(StuffItFixture.BuildLegacyV2File("Inside", [], [])),
        };
        var diagnostics = new List<Diagnostic>();

        _ = ContainerUnwrapper.Default.Unwrap(input, "Host file", new ContainerContext(diagnostics: diagnostics));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.comment");
    }

    [Fact]
    public void StuffItSplitProbeRequiresTheCompleteHeaderAndAValidInternalName()
    {
        byte[] valid = StuffItSplitFixture.BuildVolume(1, "Read Me.bin", 0, 3, "abc"u8);
        byte[] wrongSignature = (byte[])valid.Clone();
        wrongSignature[0] = 0;
        byte[] invalidNameLength = (byte[])valid.Clone();
        invalidNameLength[4] = 64;
        byte[] emptyName = (byte[])valid.Clone();
        emptyName[4] = 0;

        Assert.True(StuffItSplitReader.Instance.CanRead(ForkData.FromBytes(valid)));
        Assert.False(StuffItSplitReader.Instance.CanRead(ForkData.FromBytes(valid.AsSpan(0, 99).ToArray())));
        Assert.False(StuffItSplitReader.Instance.CanRead(ForkData.FromBytes(wrongSignature)));
        Assert.False(StuffItSplitReader.Instance.CanRead(ForkData.FromBytes(invalidNameLength)));
        Assert.False(StuffItSplitReader.Instance.CanRead(ForkData.FromBytes(emptyName)));
    }

    [Fact]
    public void StuffItSplitSetOpenedFromItsFinalVolumeRestoresBothForksAndMetadata()
    {
        byte[] resource = "resource fork"u8.ToArray();
        byte[] data = "data fork spanning the remaining volumes"u8.ToArray();
        byte[] combinedForks = [.. resource, .. data];
        int firstCut = 3;
        int secondCut = resource.Length + 5;
        byte[] firstPart = StuffItSplitFixture.BuildVolume(1, "Read Me.bin", resource.Length, data.Length,
            combinedForks.AsSpan(0, firstCut));
        byte[] secondPart = StuffItSplitFixture.BuildVolume(2, "Read Me.bin", resource.Length, data.Length,
            combinedForks.AsSpan(firstCut, secondCut - firstCut));
        byte[] finalPart = StuffItSplitFixture.BuildVolume(3, "Read Me.bin", resource.Length, data.Length,
            combinedForks.AsSpan(secondCut));
        var firstVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit1"),
            DataFork = ForkData.FromBytes(firstPart),
        };
        var secondVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit2"),
            DataFork = ForkData.FromBytes(secondPart),
        };
        var finalVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit3"),
            DataFork = ForkData.FromBytes(finalPart),
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(finalVolume, "Host file",
            new ContainerContext(siblings: () => [firstVolume, secondVolume]));
        MacFile restored = Assert.Single(result.Children).File;

        Assert.Equal("Read Me.bin", restored.Name.ToString());
        Assert.Equal(data, restored.DataFork.ToArray());
        Assert.Equal(resource, restored.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), restored.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), restored.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, restored.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), restored.Created);
        Assert.Equal(new MacDate(2_600_000_000), restored.Modified);
    }

    [Fact]
    public void StuffItSplitReaderCanReadAForkWhenTheHostNameIsProvidedInContext()
    {
        byte[] firstBytes = StuffItSplitFixture.BuildVolume(1, "Read Me.bin", 0, 6, "abc"u8);
        byte[] finalBytes = StuffItSplitFixture.BuildVolume(2, "Read Me.bin", 0, 6, "def"u8);
        var sibling = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit1"),
            DataFork = ForkData.FromBytes(firstBytes),
        };
        var context = new ContainerContext(
            hostName: MacString.FromMacRoman("archive.sit2"),
            siblings: () => [sibling]);

        MacFile restored = Assert.Single(StuffItSplitReader.Instance.Read(ForkData.FromBytes(finalBytes), context));

        Assert.Equal("Read Me.bin", restored.Name.ToString());
        Assert.Equal("abcdef"u8.ToArray(), restored.DataFork.ToArray());
    }

    [Fact]
    public void DefaultUnwrapperReadsAStuffItArchiveSplitAcrossVolumes()
    {
        byte[] data = "payload from the segmented archive"u8.ToArray();
        byte[] resource = "resource"u8.ToArray();
        byte[] archive = StuffItFixture.BuildLegacyV2File("Inside", data, resource);
        int cut = archive.Length / 2;
        byte[] firstPart = StuffItSplitFixture.BuildVolume(1, "Backup.sit", 0, archive.Length,
            archive.AsSpan(0, cut));
        byte[] finalPart = StuffItSplitFixture.BuildVolume(2, "Backup.sit", 0, archive.Length,
            archive.AsSpan(cut));
        var firstVolume = new MacFile
        {
            Name = MacString.FromMacRoman("backup.sit1"),
            DataFork = ForkData.FromBytes(firstPart),
        };
        var finalVolume = new MacFile
        {
            Name = MacString.FromMacRoman("backup.sit2"),
            DataFork = ForkData.FromBytes(finalPart),
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(finalVolume, "Host file",
            new ContainerContext(siblings: () => [firstVolume]));
        MacFile restored = Assert.Single(result.Leaves()).File;

        Assert.Equal("Inside", restored.MacPath);
        Assert.Equal(data, restored.DataFork.ToArray());
        Assert.Equal(resource, restored.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItSplitSetReportsAMissingVolumeInsteadOfReturningTruncatedForks()
    {
        byte[] missingVolume = StuffItSplitFixture.BuildVolume(1, "Read Me.bin", 0, 6, "abc"u8);
        byte[] finalVolumeBytes = StuffItSplitFixture.BuildVolume(3, "Read Me.bin", 0, 6, "def"u8);
        var finalVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit3"),
            DataFork = ForkData.FromBytes(finalVolumeBytes),
        };
        var sibling = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit1"),
            DataFork = ForkData.FromBytes(missingVolume),
        };
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(finalVolume, "Host file",
            new ContainerContext(diagnostics: diagnostics, siblings: () => [sibling]));

        Assert.Empty(result.Children);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void StuffItSplitSetReportsWhenContiguousVolumesEndBeforeTheDeclaredForks()
    {
        byte[] partialVolume = StuffItSplitFixture.BuildVolume(1, "Read Me.bin", 0, 6, "abc"u8);
        var input = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit1"),
            DataFork = ForkData.FromBytes(partialVolume),
        };
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(input, "Host file",
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(result.Children);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.missing-volume" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void StuffItSplitSetRejectsTwoSiblingFilesClaimingTheSameVolumeNumber()
    {
        byte[] duplicateVolumeBytes = StuffItSplitFixture.BuildVolume(2, "Read Me.bin", 0, 6, "abc"u8);
        byte[] finalVolumeBytes = StuffItSplitFixture.BuildVolume(3, "Read Me.bin", 0, 6, "def"u8);
        var firstVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit1"),
            DataFork = ForkData.FromBytes(StuffItSplitFixture.BuildVolume(1, "Read Me.bin", 0, 6, ""u8)),
        };
        var secondVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit2"),
            DataFork = ForkData.FromBytes(duplicateVolumeBytes),
        };
        var duplicateVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive-copy.sit2"),
            DataFork = ForkData.FromBytes(duplicateVolumeBytes),
        };
        var finalVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit3"),
            DataFork = ForkData.FromBytes(finalVolumeBytes),
        };
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(finalVolume, "Host file",
            new ContainerContext(diagnostics: diagnostics, siblings: () =>
                [firstVolume, secondVolume, duplicateVolume]));

        Assert.Empty(result.Children);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "container.unreadable" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItSplitSetHonorsTheCombinedInputSizeLimit()
    {
        byte[] firstBytes = StuffItSplitFixture.BuildVolume(1, "Read Me.bin", 0, 6, "abc"u8);
        byte[] finalBytes = StuffItSplitFixture.BuildVolume(2, "Read Me.bin", 0, 6, "def"u8);
        var firstVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit1"),
            DataFork = ForkData.FromBytes(firstBytes),
        };
        var finalVolume = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit2"),
            DataFork = ForkData.FromBytes(finalBytes),
        };
        var context = new ContainerContext(
            options: new ContainerReadOptions { MaxExpandedBytesPerInput = firstBytes.Length + finalBytes.Length - 1 },
            siblings: () => [firstVolume]);

        Assert.Throws<InvalidDataException>(() => StuffItSplitReader.Instance.Read(finalVolume, context));
    }

    [Fact]
    public void LegacyStuffItVersion2StoredFilePreservesBothForksAndFinderMetadata()
    {
        byte[] image = StuffItFixture.BuildLegacyV2File("Read Me", "data fork"u8.ToArray(),
            "resource fork"u8.ToArray());
        var diagnostics = new List<Diagnostic>();
        ForkData input = ForkData.FromBytes(image);

        Assert.True(StuffItReader.Instance.CanRead(input));
        MacFile file = Assert.Single(StuffItReader.Instance.Read(input,
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal("data fork"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("resource fork"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void LegacyStuffItDeluxe45OriginalArchiveExpandsItsMacintoshDataAndResourceForks()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItLegacy45");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "StuffItDeluxe45.sit"));
        byte[] expectedPicture = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedPicture.pict"));
        byte[] expectedPictureResource = File.ReadAllBytes(Path.Combine(fixtureDirectory,
            "ExpectedPictureResource.bin"));
        byte[] expectedImageResource = File.ReadAllBytes(Path.Combine(fixtureDirectory,
            "ExpectedTestImageResource.bin"));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(["Test Image", "Test Text", "testfile.PICT", "testfile.jpg", "testfile.png", "testfile.txt"],
            files.Select(file => file.Name.ToMacRoman()).Order(StringComparer.Ordinal));
        MacFile picture = Assert.Single(files, file => file.Name.ToMacRoman() == "testfile.PICT");
        Assert.Equal(expectedPicture, picture.DataFork.ToArray());
        Assert.Equal(expectedPictureResource, picture.ResourceFork.ToArray());
        MacFile image = Assert.Single(files, file => file.Name.ToMacRoman() == "Test Image");
        Assert.Equal(expectedImageResource, image.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("testfile.stuffit651_dlx.mac9.sit")]
    [InlineData("testfile.stuffit651_dlx.macx1.sit")]
    [InlineData("testfile.stuffit7_dlx.mac9.sit")]
    [InlineData("testfile.stuffit7_dlx.macx1.sit")]
    public void OriginalStuffItDeluxe65And70MacArchivesPreserveFilesAndBothForks(string archiveName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItOriginalCrossVersion");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, archiveName));
        string legacyFixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItLegacy45");
        byte[] expectedPicture = File.ReadAllBytes(Path.Combine(legacyFixtureDirectory, "ExpectedPicture.pict"));
        byte[] expectedPictureResource = File.ReadAllBytes(Path.Combine(legacyFixtureDirectory,
            "ExpectedPictureResource.bin"));
        byte[] expectedImageResource = File.ReadAllBytes(Path.Combine(legacyFixtureDirectory,
            "ExpectedTestImageResource.bin"));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(["Test Image", "Test Text", "testfile.PICT", "testfile.jpg", "testfile.png", "testfile.txt"],
            files.Select(file => file.Name.ToMacRoman()).Order(StringComparer.Ordinal));
        MacFile picture = Assert.Single(files, file => file.Name.ToMacRoman() == "testfile.PICT");
        Assert.Equal(expectedPicture, picture.DataFork.ToArray());
        Assert.Equal(expectedPictureResource, picture.ResourceFork.ToArray());
        MacFile image = Assert.Single(files, file => file.Name.ToMacRoman() == "Test Image");
        Assert.Empty(image.DataFork.ToArray());
        Assert.Equal(expectedImageResource, image.ResourceFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedTestText.bin")),
            Assert.Single(files, file => file.Name.ToMacRoman() == "Test Text").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedTestFile.jpg")),
            Assert.Single(files, file => file.Name.ToMacRoman() == "testfile.jpg").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedTestFile.png")),
            Assert.Single(files, file => file.Name.ToMacRoman() == "testfile.png").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedTestFile.txt")),
            Assert.Single(files, file => file.Name.ToMacRoman() == "testfile.txt").DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void LegacyStuffItDeluxe45OriginalArchiveReadsItsResourceForkComment()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItLegacy45");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "StuffItDeluxe45WithComment.sit"));
        byte[] appleDouble = File.ReadAllBytes(Path.Combine(fixtureDirectory, "StuffItDeluxe45CommentAppleDouble.bin"));
        var context = new ContainerContext(hostName: MacString.FromMacRoman("archive.sit"));
        MacFile sidecar = Assert.Single(AppleSingleReader.AppleDouble.Read(ForkData.FromBytes(appleDouble), context));
        var input = new MacFile
        {
            Name = MacString.FromMacRoman("archive.sit"),
            DataFork = ForkData.FromBytes(archive),
            ResourceFork = sidecar.ResourceFork,
        };
        var diagnostics = new List<Diagnostic>();

        _ = ContainerUnwrapper.Default.Unwrap(input, "Host file",
            new ContainerContext(diagnostics: diagnostics));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.comment" &&
            diagnostic.Severity == DiagnosticSeverity.Info &&
            diagnostic.Message.StartsWith("StuffIt comment: ", StringComparison.Ordinal) &&
            diagnostic.Message.Length > "StuffIt comment: ".Length);
    }

    [Fact]
    public void LegacyStuffItVersion2ReportsIncorrectPreviousSiblingOffsetButKeepsFileReadable()
    {
        byte[] image = StuffItFixture.BuildLegacyV2File("Read Me", "payload"u8.ToArray(), []);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(22 + 50), 22);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(22 + 110),
            StuffItFixture.Crc16Arc(image.AsSpan(22, 110)));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal("payload"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.previous-link-mismatch" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void LegacyStuffItVersion2ReportsIncorrectParentOffsetButKeepsFileReadable()
    {
        byte[] image = StuffItFixture.BuildLegacyV2File("Read Me", "payload"u8.ToArray(), []);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(22 + 58), 22);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(22 + 110),
            StuffItFixture.Crc16Arc(image.AsSpan(22, 110)));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal("payload"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.parent-link-mismatch" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void LegacyStuffItVersion2AcceptsParentAndPreviousLinksForNestedAndSiblingEntries()
    {
        byte[] image = StuffItFixture.BuildLegacyV2FolderAndSiblingFiles();
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Collection(files,
            file =>
            {
                Assert.Equal("Folder:Child", file.MacPath);
                Assert.Equal("inside"u8.ToArray(), file.DataFork.ToArray());
            },
            file =>
            {
                Assert.Equal("Sibling", file.MacPath);
                Assert.Equal("outside"u8.ToArray(), file.DataFork.ToArray());
            });
        Assert.DoesNotContain(diagnostics, diagnostic =>
            diagnostic.Code is "archive.previous-link-mismatch" or "archive.parent-link-mismatch");
    }

    [Fact]
    public void LegacyStuffItFixedHuffmanMethodCanDecodePackBitsOnlyBlocks()
    {
        byte[] packed = [0x02, (byte)'a', (byte)'b', (byte)'c', 0x80, 0xFE, (byte)'!'];
        byte[] expected = "abc!!!"u8.ToArray();
        byte[] compressed = new byte[15];
        BinaryPrimitives.WriteInt32BigEndian(compressed, -9);
        BinaryPrimitives.WriteInt32BigEndian(compressed.AsSpan(9), -6);
        packed.AsSpan(0, 5).CopyTo(compressed.AsSpan(4));
        compressed[9 + 4] = 0xFE;
        compressed[9 + 5] = (byte)'!';
        byte[] image = StuffItFixture.BuildLegacyV2Method6File("Read Me", compressed, expected);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.Empty(file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItV5FixedHuffmanMethodCanDecodePackBitsOnlyBlocks()
    {
        byte[] expected = "xyz"u8.ToArray();
        byte[] compressed = [0xFF, 0xFF, 0xFF, 0xF8, 0x02, (byte)'x', (byte)'y', (byte)'z'];
        byte[] image = StuffItFixture.BuildFile("Read Me", expected, [], dataMethod: 6, encodedData: compressed);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void LegacyStuffItFixedHuffmanMethodDecodesHuffmanAndPackBitsBlocks()
    {
        // The fixed codebook maps leaf 1 to the literal-run control byte and leaf 2 to 'A'.
        // The following Huffman stream ends with the method-6 sentinel (leaf 257), producing
        // the PackBits sequence [0x00, 'A'] and therefore the first byte of the data fork.
        // A following PackBits-only block contributes the second byte.
        byte[] compressed = [
            0x00, 0x00, 0x00, 0x0F,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x41,
            0x05, 0xFF, 0xE0,
            0xFF, 0xFF, 0xFF, 0xFA, 0x00, 0x21,
        ];
        byte[] image = StuffItFixture.BuildLegacyV2Method6File("Read Me", compressed, [0x41, 0x21]);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        MacFile file = Assert.Single(files);
        Assert.Equal(new byte[] { 0x41, 0x21 }, file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.compression-unsupported");
    }

    [Fact]
    public void StuffItV5FixedHuffmanMethodDecodesHuffmanCodedBlocks()
    {
        // The final Huffman code selects the second end-marker leaf (258).
        byte[] compressed = [
            0x00, 0x00, 0x00, 0x0F,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x42,
            0x05, 0xFF, 0xF0,
        ];
        byte[] image = StuffItFixture.BuildFile("Read Me", [0x42], [], dataMethod: 6, encodedData: compressed);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(new byte[] { 0x42 }, file.DataFork.ToArray());
    }

    [Fact]
    public void LegacyStuffItFixedHuffmanMethodRejectsAHuffmanStreamWithoutItsEndMarker()
    {
        byte[] compressed = [
            0x00, 0x00, 0x00, 0x0D,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x41,
            0x05,
        ];
        byte[] image = StuffItFixture.BuildLegacyV2Method6File("Read Me", compressed, [0x41]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext()));
    }

    [Fact]
    public void LegacyStuffItFixedHuffmanMethodRejectsInvalidPackBitsBlockLength()
    {
        byte[] compressed = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(compressed, -3);
        byte[] image = StuffItFixture.BuildLegacyV2Method6File("Read Me", compressed, [0]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext()));
    }

    [Fact]
    public void LegacyStuffItFixedHuffmanMethodRejectsTruncatedPackBitsLiteral()
    {
        byte[] compressed = [0xFF, 0xFF, 0xFF, 0xFB, 0x00];
        byte[] image = StuffItFixture.BuildLegacyV2Method6File("Read Me", compressed, [0x41]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext()));
    }

    [Fact]
    public void LegacyStuffItFixedHuffmanMethodRejectsPackBitsOutputBeyondDeclaredForkLength()
    {
        byte[] compressed = [0xFF, 0xFF, 0xFF, 0xF9, 0x01, 0x41, 0x42];
        byte[] image = StuffItFixture.BuildLegacyV2Method6File("Read Me", compressed, [0x41]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext()));
    }

    [Fact]
    public void LegacyStuffItVersion1ReadsSequentialMembersAndFolderMarkers()
    {
        byte[] image = StuffItFixture.BuildLegacyV1NestedFile();
        var diagnostics = new List<Diagnostic>();

        Assert.True(StuffItReader.Instance.CanRead(ForkData.FromBytes(image)));
        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Docs:Read Me", file.MacPath);
        Assert.Equal("data fork"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("resource fork"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void LegacyStuffItVersion1RejectsFolderEndWithoutOpenFolder()
    {
        byte[] image = StuffItFixture.BuildLegacyV1NestedFile();
        const int firstMember = 22;
        image[firstMember] = 33;
        image[firstMember + 1] = 33;
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(firstMember + 110),
            StuffItFixture.Crc16Arc(image.AsSpan(firstMember, 110)));

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext()));
    }

    [Fact]
    public void StoredStuffItV5FilePreservesBothForksAndFinderMetadata()
    {
        byte[] image = StuffItFixture.BuildFile("Read Me", "data fork"u8.ToArray(), "resource fork"u8.ToArray());
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal("data fork"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("resource fork"u8.ToArray(), file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItV5FolderEntriesBecomeMacFileFolderPaths()
    {
        byte[] image = StuffItFixture.BuildNestedFile();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("Docs:Read Me", file.MacPath);
        Assert.Equal("inside"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItV5Utf8NamesRemainAvailableInMacPaths()
    {
        byte[] image = StuffItFixture.BuildFile("文書", "text"u8.ToArray(), []);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("文書", file.MacPath);
    }

    [Fact]
    public void DefaultUnwrapperRecognizesStuffItArchives()
    {
        byte[] image = StuffItFixture.BuildFile("Read Me", "inside"u8.ToArray(), []);
        var input = new MacFile { Name = MacString.FromMacRoman("archive.sit"), DataFork = ForkData.FromBytes(image) };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(input, "host", new ContainerContext());

        MacFile unpacked = Assert.Single(result.Children).File;
        Assert.Equal("Read Me", unpacked.MacPath);
        Assert.Equal("inside"u8.ToArray(), unpacked.DataFork.ToArray());
    }

    [Fact]
    public void EncryptedStuffItEntryIsReportedAndNotOpened()
    {
        byte[] image = StuffItFixture.BuildFile("Locked", "secret"u8.ToArray(), [], encrypted: true);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.encrypted" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void UnsupportedStuffItCompressionIsReportedAndNotReturnedAsAnEmptyFile()
    {
        byte[] image = StuffItFixture.BuildFile("Compressed", "encoded"u8.ToArray(), [], dataMethod: 4);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.compression-unsupported");
    }

    [Fact]
    public void StuffItMethod13RejectsAnIllegalControlByteInsteadOfBeingTreatedAsUnsupported()
    {
        byte[] image = StuffItFixture.BuildFile("Malformed LZ+Huffman", [0], [], dataMethod: 13,
            encodedData: [0x60]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItMethod13DecodesARealStuffIt45DataFork()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod13");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "DataFork.bin"));
        byte[] expected = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.jpg"));
        byte[] image = StuffItFixture.BuildFile("testfile.jpg", expected, [], dataMethod: 13,
            encodedData: encoded);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("Preset1ResourceFork.bin", 332, 0xF0F8)]
    [InlineData("DynamicResourceFork.bin", 9134, 0xB07B)]
    public void StuffItMethod13DecodesRealStuffIt45ForksAndMatchesTheirStoredChecksums(
        string fixtureName, int expectedLength, ushort expectedCrc)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod13");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, fixtureName));
        byte[] image = StuffItFixture.BuildFile("real method 13 fork", new byte[expectedLength], [],
            dataMethod: 13, encodedData: encoded, dataCrcOverride: expectedCrc);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedLength, file.DataFork.Length);
        Assert.Equal(expectedCrc, StuffItFixture.Crc16Arc(file.DataFork.ToArray()));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItMethod13DecodesAResourceFork()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod13");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "Preset1ResourceFork.bin"));
        byte[] image = StuffItFixture.BuildFile("real resource fork", [], new byte[332],
            resourceMethod: 13, encodedResource: encoded, resourceCrcOverride: 0xF0F8);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(332, file.ResourceFork.Length);
        Assert.Equal((ushort)0xF0F8, StuffItFixture.Crc16Arc(file.ResourceFork.ToArray()));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItMethod13DecodesDistinctDynamicTreesAndBackReferences()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod13");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "DistinctDynamicTrees.bin"));
        byte[] expected = "AAAAB"u8.ToArray();
        byte[] image = StuffItFixture.BuildFile("distinct dynamic trees", expected, [], dataMethod: 13,
            encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14DecodesAByteFromItsCompressedBlocks()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "SingleLiteral.bin"));
        byte[] expected = [(byte)'A'];
        byte[] image = StuffItFixture.BuildFile("method 14 literal", expected, [], dataMethod: 14,
            encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14AcceptsAnEmptyForkWithNoBlocks()
    {
        byte[] image = StuffItFixture.BuildFile("empty method 14 fork", [], [], dataMethod: 14,
            encodedData: [0, 0]);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Empty(file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14ExpandsALengthDistancePair()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "LiteralAndMatch.bin"));
        byte[] expected = "AAAAA"u8.ToArray();
        byte[] image = StuffItFixture.BuildFile("method 14 match", expected, [], dataMethod: 14,
            encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14UsesExtendedLengthAndDistanceCodes()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExtendedLengthAndDistance.bin"));
        byte[] expected = "ABCDEFGHIABCDEFGHIABCD"u8.ToArray();
        byte[] image = StuffItFixture.BuildFile("method 14 extended match", expected, [],
            dataMethod: 14, encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14ReadsRecursivelyEncodedTreeLengths()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "EncodedTreeLengths.bin"));
        byte[] expected = "A"u8.ToArray();
        byte[] image = StuffItFixture.BuildFile("method 14 encoded tree", expected, [], dataMethod: 14,
            encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14ExpandsRepeatedCodeLengths()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "RepeatedCodeLengths.bin"));
        byte[] expected = "A"u8.ToArray();
        byte[] image = StuffItFixture.BuildFile("method 14 repeated code lengths", expected, [],
            dataMethod: 14, encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14DecodesIndependentBlocksInOrder()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "TwoLiteralBlocks.bin"));
        byte[] expected = "AB"u8.ToArray();
        byte[] image = StuffItFixture.BuildFile("method 14 blocks", expected, [], dataMethod: 14,
            encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14DecodesAResourceFork()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "SingleLiteral.bin"));
        byte[] expected = "A"u8.ToArray();
        byte[] image = StuffItFixture.BuildFile("method 14 resource", [], expected,
            resourceMethod: 14, encodedResource: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Empty(file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItMethod14RejectsABlockThatExceedsItsCompressedFork()
    {
        byte[] encoded = [1, 0, 99, 0, 0, 0, 1, 0, 0, 0];
        byte[] image = StuffItFixture.BuildFile("truncated method 14 block", "A"u8.ToArray(), [],
            dataMethod: 14, encodedData: encoded);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItMethod14RejectsABlockExpandedLengthBeyondTheFork()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "SingleLiteral.bin"));
        encoded[6] = 2;
        byte[] image = StuffItFixture.BuildFile("oversized method 14 block", "A"u8.ToArray(), [],
            dataMethod: 14, encodedData: encoded);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItMethod14CanExpandAZeroInitializedHistoryMatch()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod14");
        byte[] encoded = File.ReadAllBytes(Path.Combine(fixtureDirectory, "MatchBeforeHistory.bin"));
        byte[] expected = new byte[4];
        byte[] image = StuffItFixture.BuildFile("method 14 initial history", expected, [],
            dataMethod: 14, encodedData: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMethod15DecodesRealStuffItDeluxe651DataAndResourceForks()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItMethod15");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "StuffItDeluxe651.sit"));
        byte[] expected = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedPicture.pict"));
        byte[] expectedPictureResource = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedPictureResource.bin"));
        byte[] expectedResource = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedTestImageResource.bin"));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        MacFile picture = Assert.Single(files, file => file.MacPath == "testfile.PICT");
        Assert.Equal(expected, picture.DataFork.ToArray());
        Assert.Equal(expectedPictureResource, picture.ResourceFork.ToArray());
        MacFile image = Assert.Single(files, file => file.MacPath == "Test Image");
        Assert.Equal(expectedResource, image.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItMethod15RejectsATruncatedArithmeticStream()
    {
        byte[] image = StuffItFixture.BuildFile("truncated method 15", [0], [], dataMethod: 15,
            encodedData: [0]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItCompressMethodDecodesLzwKwKwKCaseInBothForks()
    {
        byte[] expected = "ABABABA"u8.ToArray();
        byte[] encoded = StuffItFixture.EncodeCompressCodes(65, 66, 257, 259);
        byte[] image = StuffItFixture.BuildFile("LZW", expected, expected, dataMethod: 2, encodedData: encoded,
            resourceMethod: 2, encodedResource: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItCompressMethodRejectsAnInvalidLzwCode()
    {
        byte[] image = StuffItFixture.BuildFile("Bad LZW", "A"u8.ToArray(), [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressCodes(65, 300));

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItCompressMethodRejectsLzwOutputLongerThanTheForkLength()
    {
        byte[] image = StuffItFixture.BuildFile("Long LZW", "AB"u8.ToArray(), [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressCodes(65, 66, 257, 259));

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItCompressMethodIncreasesCodeWidthWhenTheDictionaryReaches512Entries()
    {
        byte[] expected = [.. Enumerable.Range(0, 256).Select(static value => (byte)value), 0];
        byte[] image = StuffItFixture.BuildFile("Wide LZW", expected, [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressWidthBoundary());

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItCompressMethodCanClearAndRestartItsDictionary()
    {
        byte[] image = StuffItFixture.BuildFile("Reset LZW", "ABC"u8.ToArray(), [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressWithClear());

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("ABC"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItCompressMethodSupportsItsFullFourteenBitDictionary()
    {
        const int outputLength = 16_129;
        byte[] expected = new byte[outputLength];
        byte[] image = StuffItFixture.BuildFile("Full LZW", expected, [], dataMethod: 2,
            encodedData: StuffItFixture.EncodeCompressRepeatedLiterals(outputLength));

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItHuffmanMethodDecodesATreeWrittenInTheBitStream()
    {
        byte[] encoded = StuffItFixture.EncodeHuffman("ABA", ((byte)'A', (byte)'B'));
        byte[] image = StuffItFixture.BuildFile("Huffman", "ABA"u8.ToArray(), "ABA"u8.ToArray(), dataMethod: 3,
            encodedData: encoded, resourceMethod: 3, encodedResource: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("ABA"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("ABA"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItHuffmanMethodRejectsATruncatedPrefixTree()
    {
        byte[] image = StuffItFixture.BuildFile("Bad Huffman", [0], [], dataMethod: 3, encodedData: [0]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItHuffmanMethodRejectsTruncatedSymbolData()
    {
        byte[] encoded = StuffItFixture.EncodeHuffman("A", ((byte)'A', (byte)'B'));
        byte[] image = StuffItFixture.BuildFile("Short Huffman", "AAAAAAA"u8.ToArray(), [], dataMethod: 3,
            encodedData: encoded);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItLzahMethodDecodesAdaptiveLiteralsAndCopiesFromItsInitialWindow()
    {
        byte[] encodedData = StuffItFixture.EncodeLzah(65, 66, 65, 66, 65, 66, 65);
        byte[] encodedResource = StuffItFixture.EncodeLzah(256);
        byte[] image = StuffItFixture.BuildFile("LZAH", "ABABABA"u8.ToArray(), "   "u8.ToArray(),
            dataMethod: 5, encodedData: encodedData, resourceMethod: 5, encodedResource: encodedResource);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("ABABABA"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("   "u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Theory]
    [InlineData(0, 3, 0, 32, 32, 32)]
    [InlineData(3, 4, 4, 0, 0, 0)]
    [InlineData(11, 5, 17, 45, 46, 47)]
    [InlineData(23, 6, 47, 200, 200, 200)]
    [InlineData(47, 7, 119, 82, 82, 82)]
    [InlineData(63, 8, 255, 3, 3, 3)]
    public void StuffItLzahMethodDecodesEachOffsetPrefixLength(int highBits, int codeLength, int code,
        byte firstByte, byte secondByte, byte thirdByte)
    {
        Assert.InRange(highBits, 0, 63);
        byte[] expected = [firstByte, secondByte, thirdByte];
        byte[] image = StuffItFixture.BuildFile("Offset LZAH", expected, [],
            dataMethod: 5, encodedData: StuffItFixture.EncodeLzahWithOffset(code, codeLength, 0, 256));

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItLzahMethodRejectsInputThatEndsBeforeTheDeclaredForkLength()
    {
        byte[] image = StuffItFixture.BuildFile("Truncated LZAH", "A"u8.ToArray(), [], dataMethod: 5,
            encodedData: []);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItLzahMethodRenormalizesItsAdaptiveTreeForLongForks()
    {
        const int outputLength = 33_000;
        byte[] expected = [.. Enumerable.Range(0, outputLength).Select(static value => (byte)value)];
        ushort[] symbols = expected.Select(static value => (ushort)value).ToArray();
        byte[] image = StuffItFixture.BuildFile("Long LZAH", expected, [], dataMethod: 5,
            encodedData: StuffItFixture.EncodeLzah(symbols));

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMwMethodDecodesLiteralsAndDictionaryPhrasesInBothForks()
    {
        byte[] expected = "ABAB"u8.ToArray();
        byte[] encoded = StuffItFixture.EncodeMwCodes(65, 66, 256);
        byte[] image = StuffItFixture.BuildFile("MW", expected, expected, dataMethod: 8, encodedData: encoded,
            resourceMethod: 8, encodedResource: encoded);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItMwMethodRejectsInputThatEndsBeforeTheDeclaredForkLength()
    {
        byte[] image = StuffItFixture.BuildFile("Truncated MW", "A"u8.ToArray(), [], dataMethod: 8,
            encodedData: []);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItMwMethodStartsANewDictionaryGroupAtTheNextFreeCode()
    {
        byte[] image = StuffItFixture.BuildFile("MW reset", "AB"u8.ToArray(), [], dataMethod: 8,
            encodedData: StuffItFixture.EncodeMwCodes(65, 256, 66));

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal("AB"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMwMethodWidensCodesAfterTheDictionaryReaches512Entries()
    {
        byte[] expected = [.. Enumerable.Range(0, 256).Select(static value => (byte)value), 0, 1];
        byte[] image = StuffItFixture.BuildFile("Wide MW", expected, [], dataMethod: 8,
            encodedData: StuffItFixture.EncodeMwLiterals(expected));

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StuffItMwMethodRejectsACodeOutsideItsCurrentDictionary()
    {
        byte[] image = StuffItFixture.BuildFile("Bad MW", "A"u8.ToArray(), [], dataMethod: 8,
            encodedData: StuffItFixture.EncodeMwCodes(300));

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItRle90MethodDecodesBothForks()
    {
        byte[] data = [.. Enumerable.Repeat((byte)'A', 3), (byte)'B', 0x90, 0x90];
        byte[] encodedData = [(byte)'A', 0x90, 3, (byte)'B', 0x90, 0, 0x90, 2];
        byte[] resource = [(byte)'R', (byte)'R'];
        byte[] encodedResource = [(byte)'R', 0x90, 2];
        byte[] image = StuffItFixture.BuildFile("Runs", data, resource, dataMethod: 1,
            encodedData: encodedData, resourceMethod: 1, encodedResource: encodedResource);

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext()));

        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StuffItRle90RejectsARunBeforeAnyLiteralByte()
    {
        byte[] image = StuffItFixture.BuildFile("Malformed", [0x90], [], dataMethod: 1,
            encodedData: [0x90, 2]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItRle90RejectsATrailingRunMarker()
    {
        byte[] image = StuffItFixture.BuildFile("Malformed", [0x90], [], dataMethod: 1,
            encodedData: [(byte)'A', 0x90]);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItV5FileWhoseForkExceedsItsArchiveIsRejected()
    {
        byte[] image = StuffItFixture.BuildFile("Truncated", "data"u8.ToArray(), []);
        Array.Resize(ref image, image.Length - 1);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItArchiveRejectsRootMemberOffsetBeyondAddressableInput()
    {
        byte[] image = StuffItFixture.BuildFile("file", [], []);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(94), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItForkRejectsLengthBeyondAddressableInput()
    {
        byte[] image = StuffItFixture.BuildFile("file", "x"u8.ToArray(), []);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100 + 38), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItHeaderAndForkChecksumMismatchesAreReportedWithoutDiscardingDecodedBytes()
    {
        byte[] image = StuffItFixture.BuildFile("damaged", "payload"u8.ToArray(), []);
        image[100 + 8] ^= 1;
        image[^1] ^= 1;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("payloae"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.header-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void StuffItMemberChainCycleIsRejected()
    {
        byte[] image = StuffItFixture.BuildFile("loop", [], []);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(92), 2);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(100 + 22), 100);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItMemberNameMustBeValidUtf8()
    {
        byte[] image = StuffItFixture.BuildFile("name", [], []);
        image[100 + 48] = 0xFF;

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(
            ForkData.FromBytes(image), new ContainerContext()));
    }

    [Fact]
    public void StuffItRespectsTheConfiguredEntryLimit()
    {
        byte[] image = StuffItFixture.BuildFile("file", [], []);

        Assert.Throws<InvalidDataException>(() => StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(options: new ContainerReadOptions { MaxVolumeEntries = 0 })));
    }

    [Fact]
    public void StuffItProbeRequiresTheV5SignatureAndVersion()
    {
        byte[] valid = StuffItFixture.BuildFile("file", [], []);
        byte[] wrongVersion = (byte[])valid.Clone();
        wrongVersion[82] = 4;

        Assert.True(StuffItReader.Instance.CanRead(ForkData.FromBytes(valid)));
        Assert.False(StuffItReader.Instance.CanRead(ForkData.FromBytes(wrongVersion)));
        Assert.False(StuffItReader.Instance.CanRead(ForkData.FromBytes("StuffIt "u8.ToArray())));
    }

    private static class StuffItSplitFixture
    {
        public static byte[] BuildVolume(byte volumeNumber, string name, int resourceLength, int dataLength,
            ReadOnlySpan<byte> payload)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            byte[] result = new byte[100 + payload.Length];
            result[0] = 0xB0;
            result[1] = 0x56;
            result[2] = 0;
            result[3] = volumeNumber;
            result[4] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(result.AsSpan(5));
            U32(result, 68, FourCC.FromString("TEXT").Value);
            U32(result, 72, FourCC.FromString("ttxt").Value);
            U16(result, 76, 0x4000);
            U32(result, 78, 2_500_000_000);
            U32(result, 82, 2_600_000_000);
            U32(result, 86, checked((uint)resourceLength));
            U32(result, 90, checked((uint)dataLength));
            payload.CopyTo(result.AsSpan(100));
            return result;
        }

        private static void U16(Span<byte> data, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);

        private static void U32(Span<byte> data, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);
    }

    private static class StuffItFixture
    {
        private const int ArchiveHeaderLength = 100;
        private const uint CreateSeconds = 2_500_000_000;
        private const uint ModifySeconds = 2_600_000_000;

        public static byte[] BuildFile(string name, byte[] data, byte[] resource, bool encrypted = false,
            byte dataMethod = 0, byte[]? encodedData = null, byte resourceMethod = 0, byte[]? encodedResource = null,
            ushort? dataCrcOverride = null, ushort? resourceCrcOverride = null)
        {
            encodedData ??= data;
            encodedResource ??= resource;
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            int memberHeaderLength = 48 + nameBytes.Length;
            int finderInfoLength = 36;
            int resourceInfoLength = encodedResource.Length == 0 ? 0 : 14;
            int dataOffset = ArchiveHeaderLength + memberHeaderLength + finderInfoLength + resourceInfoLength + encodedResource.Length;
            byte[] image = new byte[dataOffset + encodedData.Length];
            WriteArchiveHeader(image, 1, ArchiveHeaderLength);

            Span<byte> member = image.AsSpan(ArchiveHeaderLength, memberHeaderLength);
            U32(member, 0, 0xA5A5A5A5);
            U16(member, 6, checked((ushort)memberHeaderLength));
            member[9] = encrypted ? (byte)0x20 : (byte)0;
            U32(member, 10, CreateSeconds);
            U32(member, 14, ModifySeconds);
            U32(member, 22, 0); // no following member
            U16(member, 30, checked((ushort)nameBytes.Length));
            U32(member, 34, checked((uint)data.Length));
            U32(member, 38, checked((uint)encodedData.Length));
            U16(member, 42, dataCrcOverride ?? Crc16Arc(data));
            member[46] = dataMethod;
            nameBytes.CopyTo(member[48..]);
            U16(member, 32, HeaderCrc(member));

            Span<byte> finder = image.AsSpan(ArchiveHeaderLength + memberHeaderLength, finderInfoLength);
            if (encodedResource.Length != 0) U16(finder, 0, 1);
            "TEXTttxt"u8.CopyTo(finder[4..]);
            U16(finder, 12, 0x4000);
            int forksAt = ArchiveHeaderLength + memberHeaderLength + finderInfoLength;
            if (encodedResource.Length != 0)
            {
                U32(image.AsSpan(forksAt), 0, checked((uint)resource.Length));
                U32(image.AsSpan(forksAt), 4, checked((uint)encodedResource.Length));
                U16(image.AsSpan(forksAt), 8, resourceCrcOverride ?? Crc16Arc(resource));
                image[forksAt + 12] = resourceMethod;
                encodedResource.CopyTo(image.AsSpan(forksAt + resourceInfoLength, encodedResource.Length));
            }
            encodedData.CopyTo(image, dataOffset);
            U32(image, 84, checked((uint)image.Length));
            return image;
        }

        public static byte[] BuildLegacyV2File(string name, byte[] data, byte[] resource)
        {
            const int archiveHeaderLength = 22;
            const int memberHeaderLength = 112;
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            if (nameBytes.Length is 0 or > 31) throw new ArgumentOutOfRangeException(nameof(name));
            int resourceOffset = archiveHeaderLength + memberHeaderLength;
            int dataOffset = resourceOffset + resource.Length;
            byte[] image = new byte[dataOffset + data.Length];
            "SIT!"u8.CopyTo(image);
            U16(image, 4, 1);
            U32(image, 6, checked((uint)image.Length));
            "rLau"u8.CopyTo(image.AsSpan(10));
            image[14] = 2;
            U32(image, 16, archiveHeaderLength);
            U16(image, 20, 1);

            Span<byte> member = image.AsSpan(archiveHeaderLength, memberHeaderLength);
            member[0] = 0;
            member[1] = 0;
            member[2] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(member[3..]);
            U16(member, 34, Crc16Arc(member[..34]));
            U16(member, 48, 0);
            U32(member, 50, 0);
            U32(member, 54, 0);
            U32(member, 58, 0);
            U32(member, 62, uint.MaxValue);
            "TEXTttxt"u8.CopyTo(member[66..]);
            U16(member, 74, 0x4000);
            U32(member, 76, 2_500_000_000);
            U32(member, 80, 2_600_000_000);
            U32(member, 84, checked((uint)resource.Length));
            U32(member, 88, checked((uint)data.Length));
            U32(member, 92, checked((uint)resource.Length));
            U32(member, 96, checked((uint)data.Length));
            U16(member, 100, Crc16Arc(resource));
            U16(member, 102, Crc16Arc(data));
            U16(member, 110, Crc16Arc(member[..110]));
            resource.CopyTo(image.AsSpan(resourceOffset));
            data.CopyTo(image.AsSpan(dataOffset));
            return image;
        }

        public static byte[] BuildLegacyV2FolderAndSiblingFiles()
        {
            const int archiveHeaderLength = 22;
            const int memberHeaderLength = 112;
            const int folderOffset = archiveHeaderLength;
            const int childOffset = folderOffset + memberHeaderLength;
            byte[] childData = "inside"u8.ToArray();
            byte[] siblingData = "outside"u8.ToArray();
            int siblingOffset = childOffset + memberHeaderLength + childData.Length;
            byte[] image = new byte[siblingOffset + memberHeaderLength + childData.Length + siblingData.Length];

            "SIT!"u8.CopyTo(image);
            U16(image, 4, 2);
            U32(image, 6, checked((uint)image.Length));
            "rLau"u8.CopyTo(image.AsSpan(10));
            image[14] = 2;
            U32(image, 16, folderOffset);

            WriteLegacyV2Member(image, folderOffset, "Folder", parent: 0, previous: 0,
                next: checked((uint)siblingOffset), firstChild: childOffset, childCount: 1, data: []);
            WriteLegacyV2Member(image, childOffset, "Child", parent: folderOffset, previous: 0,
                next: 0, firstChild: uint.MaxValue, childCount: 0, data: childData);
            WriteLegacyV2Member(image, siblingOffset, "Sibling", parent: 0, previous: folderOffset,
                next: 0, firstChild: uint.MaxValue, childCount: 0, data: siblingData);
            return image;
        }

        private static void WriteLegacyV2Member(byte[] image, int offset, string name, uint parent,
            uint previous, uint next, uint firstChild, ushort childCount, byte[] data)
        {
            Span<byte> member = image.AsSpan(offset, 112);
            byte[] nameBytes = Encoding.ASCII.GetBytes(name);
            member[2] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(member[3..]);
            U16(member, 48, childCount);
            U32(member, 50, previous);
            U32(member, 54, next);
            U32(member, 58, parent);
            U32(member, 62, firstChild);
            U32(member, 88, checked((uint)data.Length));
            U32(member, 96, checked((uint)data.Length));
            U16(member, 102, Crc16Arc(data));
            U16(member, 110, Crc16Arc(member[..110]));
            data.CopyTo(image.AsSpan(offset + 112));
        }

        public static byte[] BuildLegacyV2Method6File(string name, byte[] compressed, byte[] expanded)
        {
            byte[] image = BuildLegacyV2File(name, compressed, []);
            Span<byte> member = image.AsSpan(22, 112);
            BinaryPrimitives.WriteUInt32BigEndian(member[88..], checked((uint)expanded.Length));
            BinaryPrimitives.WriteUInt16BigEndian(member[102..], Crc16Arc(expanded));
            member[1] = 6;
            BinaryPrimitives.WriteUInt16BigEndian(member[110..], Crc16Arc(member[..110]));
            return image;
        }

        public static byte[] BuildLegacyV1NestedFile()
        {
            const int archiveHeaderLength = 22;
            const int memberHeaderLength = 112;
            byte[] folderName = "Docs"u8.ToArray();
            byte[] fileName = "Read Me"u8.ToArray();
            byte[] data = "data fork"u8.ToArray();
            byte[] resource = "resource fork"u8.ToArray();

            byte[] folder = BuildLegacyV1Marker(folderName, 32);
            byte[] fileHeader = new byte[memberHeaderLength];
            fileName.CopyTo(fileHeader.AsSpan(3));
            fileHeader[0] = 0;
            fileHeader[1] = 0;
            fileHeader[2] = checked((byte)fileName.Length);
            "TEXTttxt"u8.CopyTo(fileHeader.AsSpan(66));
            U16(fileHeader, 74, 0x4000);
            U32(fileHeader, 76, CreateSeconds);
            U32(fileHeader, 80, ModifySeconds);
            U32(fileHeader, 84, checked((uint)resource.Length));
            U32(fileHeader, 88, checked((uint)data.Length));
            U32(fileHeader, 92, checked((uint)resource.Length));
            U32(fileHeader, 96, checked((uint)data.Length));
            U16(fileHeader, 100, Crc16Arc(resource));
            U16(fileHeader, 102, Crc16Arc(data));
            U16(fileHeader, 110, Crc16Arc(fileHeader.AsSpan(0, 110)));

            byte[] endFolder = BuildLegacyV1Marker([], 33);
            int archiveLength = archiveHeaderLength + folder.Length + fileHeader.Length + resource.Length +
                data.Length + endFolder.Length + 4;
            byte[] image = new byte[archiveLength];
            "SIT!"u8.CopyTo(image);
            U16(image, 4, 1);
            U32(image, 6, checked((uint)archiveLength));
            "rLau"u8.CopyTo(image.AsSpan(10));
            image[14] = 1;
            int offset = archiveHeaderLength;
            folder.CopyTo(image, offset);
            offset += folder.Length;
            fileHeader.CopyTo(image, offset);
            offset += fileHeader.Length;
            resource.CopyTo(image, offset);
            offset += resource.Length;
            data.CopyTo(image, offset);
            offset += data.Length;
            endFolder.CopyTo(image, offset);
            offset += endFolder.Length;
            "PEnd"u8.CopyTo(image.AsSpan(offset));
            return image;
        }

        private static byte[] BuildLegacyV1Marker(byte[] name, byte method)
        {
            byte[] marker = new byte[112];
            marker[0] = method;
            marker[1] = method;
            marker[2] = checked((byte)name.Length);
            name.CopyTo(marker.AsSpan(3));
            U16(marker, 110, Crc16Arc(marker.AsSpan(0, 110)));
            return marker;
        }

        public static byte[] BuildNestedFile()
        {
            byte[] folderName = "Docs"u8.ToArray();
            byte[] fileName = "Read Me"u8.ToArray();
            byte[] child = BuildFile("Read Me", "inside"u8.ToArray(), []);
            int childOffset = ArchiveHeaderLength + 48 + folderName.Length;
            int childLength = child.Length - ArchiveHeaderLength;
            byte[] image = new byte[childOffset + childLength];
            WriteArchiveHeader(image, 1, ArchiveHeaderLength);

            Span<byte> folder = image.AsSpan(ArchiveHeaderLength, 48 + folderName.Length);
            U32(folder, 0, 0xA5A5A5A5);
            U16(folder, 6, checked((ushort)folder.Length));
            folder[9] = 0x40;
            U32(folder, 10, CreateSeconds);
            U32(folder, 14, ModifySeconds);
            U32(folder, 22, 0);
            U16(folder, 30, checked((ushort)folderName.Length));
            U32(folder, 34, checked((uint)childOffset));
            U16(folder, 46, 1);
            folderName.CopyTo(folder[48..]);
            U16(folder, 32, HeaderCrc(folder));
            child.AsSpan(ArchiveHeaderLength).CopyTo(image.AsSpan(childOffset));
            U32(image, childOffset + 22, 0);
            U16(image.AsSpan(childOffset + 32), 0, HeaderCrc(image.AsSpan(childOffset, 48 + fileName.Length)));
            U32(image, 84, checked((uint)image.Length));
            return image;
        }

        private static void WriteArchiveHeader(Span<byte> image, ushort rootEntries, uint firstEntry)
        {
            "StuffIt "u8.CopyTo(image);
            image[82] = 5;
            U32(image, 84, 0);
            U16(image, 92, rootEntries);
            U32(image, 94, firstEntry);
        }

        public static byte[] EncodeCompressCodes(params ushort[] codes)
        {
            const int CodeBits = 9;
            int codeBytes = (codes.Length * CodeBits + 7) / 8;
            byte[] encoded = new byte[9 + codeBytes];
            encoded[1] = 1; // Block-mode clear code 256, padded to the next 8-code group.
            for (int index = 0; index < codes.Length; index++)
            {
                int bitOffset = index * CodeBits;
                for (int bit = 0; bit < CodeBits; bit++)
                    if ((codes[index] & (1 << bit)) != 0)
                        encoded[9 + (bitOffset + bit) / 8] |= (byte)(1 << ((bitOffset + bit) & 7));
            }
            return encoded;
        }

        public static byte[] EncodeCompressWidthBoundary()
        {
            byte[] encoded = new byte[9 + 256 * 9 / 8 + 2];
            encoded[1] = 1;
            for (int code = 0; code < 256; code++)
                for (int bit = 0; bit < 9; bit++)
                    if ((code & (1 << bit)) != 0)
                    {
                        int bitOffset = code * 9 + bit;
                        encoded[9 + bitOffset / 8] |= (byte)(1 << (bitOffset & 7));
            }
            return encoded;
        }

        public static byte[] EncodeCompressWithClear()
        {
            byte[] encoded = new byte[20];
            encoded[1] = 1;
            WriteCode(encoded, 72, 65, 9);
            WriteCode(encoded, 81, 66, 9);
            WriteCode(encoded, 90, 256, 9);
            WriteCode(encoded, 144, 67, 9);
            return encoded;
        }

        public static byte[] EncodeCompressRepeatedLiterals(int count)
        {
            var codes = new List<(ushort Value, int Width)>(count);
            int width = 9;
            int nextCode = 257;
            bool hasPrevious = false;
            for (int index = 0; index < count; index++)
            {
                codes.Add((0, width));
                if (hasPrevious && nextCode < 1 << 14)
                {
                    nextCode++;
                    if (width < 14 && nextCode == 1 << width) width++;
                }
                hasPrevious = true;
            }

            int bitCount = codes.Sum(static code => code.Width);
            byte[] encoded = new byte[9 + (bitCount + 7) / 8];
            encoded[1] = 1;
            int bitOffset = 72;
            foreach ((ushort value, int codeWidth) in codes)
            {
                WriteCode(encoded, bitOffset, value, codeWidth);
                bitOffset += codeWidth;
            }
            return encoded;
        }

        public static byte[] EncodeHuffman(string text, (byte Zero, byte One) symbols)
        {
            var bits = new List<bool>();
            bits.Add(false); // Root internal node.
            WriteTreeLeaf(bits, symbols.Zero);
            WriteTreeLeaf(bits, symbols.One);
            foreach (char value in text) bits.Add(value == symbols.Zero ? false : true);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int index = 0; index < bits.Count; index++)
                if (bits[index]) encoded[index / 8] |= (byte)(0x80 >> (index & 7));
            return encoded;
        }

        public static byte[] EncodeLzah(params ushort[] symbols)
            => EncodeLzah(symbols, 0, 3, 0);

        public static byte[] EncodeLzahWithOffset(int offsetCode, int offsetCodeLength, int offsetLowBits,
            params ushort[] symbols)
            => EncodeLzah(symbols, offsetCode, offsetCodeLength, offsetLowBits);

        private static byte[] EncodeLzah(ushort[] symbols, int offsetCode, int offsetCodeLength, int offsetLowBits)
        {
            const int LeafCount = 314;
            const int TreeSize = LeafCount * 2 - 1;
            var frequencies = new int[TreeSize + 1];
            var forward = new int[TreeSize];
            var backward = new int[TreeSize + LeafCount];
            for (int symbol = 0; symbol < LeafCount; symbol++)
            {
                frequencies[symbol] = 1;
                forward[symbol] = symbol + TreeSize;
                backward[symbol + TreeSize] = symbol;
            }
            for (int node = LeafCount, child = 0; node < TreeSize; node++, child += 2)
            {
                frequencies[node] = frequencies[child] + frequencies[child + 1];
                forward[node] = child;
                backward[child] = backward[child + 1] = node;
            }
            frequencies[TreeSize] = ushort.MaxValue;

            var bits = new List<bool>();
            foreach (ushort symbol in symbols)
            {
                int child = backward[symbol + TreeSize];
                var path = new List<bool>();
                for (int parent = backward[child]; parent != 0; child = parent, parent = backward[child])
                    path.Add(forward[parent] + 1 == child);
                path.Reverse();
                bits.AddRange(path);
                if (frequencies[TreeSize - 1] >= 0x8000)
                    ReorderLzahTree(frequencies, forward, backward, TreeSize, LeafCount);
                UpdateLzahTree(symbol, frequencies, forward, backward, TreeSize);
                if (symbol >= 256)
                {
                    for (int bit = offsetCodeLength - 1; bit >= 0; bit--)
                        bits.Add((offsetCode & (1 << bit)) != 0);
                    for (int bit = 5; bit >= 0; bit--)
                        bits.Add((offsetLowBits & (1 << bit)) != 0);
                }
            }

            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] EncodeMwCodes(params ushort[] codes)
        {
            var encoded = new byte[(codes.Length * 9 + 7) / 8];
            int bitPosition = 0;
            foreach (ushort code in codes)
            {
                for (int bit = 0; bit < 9; bit++, bitPosition++)
                    if ((code & (1 << bit)) != 0)
                        encoded[bitPosition / 8] |= (byte)(1 << (bitPosition & 7));
            }
            return encoded;
        }

        public static byte[] EncodeMwLiterals(ReadOnlySpan<byte> bytes)
        {
            var encoded = new List<byte>();
            int bitPosition = 0;
            int codeWidth = 9;
            int nextCode = 256;
            int nextWidthBoundary = 512;
            foreach (byte value in bytes)
            {
                int requiredBytes = (bitPosition + codeWidth + 7) / 8;
                while (encoded.Count < requiredBytes) encoded.Add(0);
                for (int bit = 0; bit < codeWidth; bit++, bitPosition++)
                    if ((value & (1 << bit)) != 0)
                        encoded[bitPosition / 8] |= (byte)(1 << (bitPosition & 7));
                if (nextCode < 16_385 && nextCode == nextWidthBoundary)
                {
                    nextWidthBoundary <<= 1;
                    codeWidth++;
                }
                if (nextCode < 16_385) nextCode++;
            }
            return [.. encoded];
        }

        private static void UpdateLzahTree(ushort symbol, int[] frequencies, int[] forward, int[] backward,
            int treeSize)
        {
            int node = backward[symbol + treeSize];
            while (node != 0)
            {
                int weight = ++frequencies[node];
                int swap = node + 1;
                if (frequencies[swap] < weight)
                {
                    while (frequencies[++swap] < weight) { }
                    swap--;
                    frequencies[node] = frequencies[swap];
                    frequencies[swap] = weight;

                    int child = forward[node];
                    backward[child] = swap;
                    if (child < treeSize) backward[child + 1] = swap;
                    forward[node] = forward[swap];
                    forward[swap] = child;
                    child = forward[node];
                    backward[child] = node;
                    if (child < treeSize) backward[child + 1] = node;
                    node = swap;
                }
                node = backward[node];
            }
        }

        private static void ReorderLzahTree(int[] frequencies, int[] forward, int[] backward, int treeSize,
            int leafCount)
        {
            int leaf = 0;
            for (int node = 0; node < treeSize; node++)
            {
                if (forward[node] < treeSize) continue;
                frequencies[leaf] = (frequencies[node] + 1) >> 1;
                forward[leaf++] = forward[node];
            }

            int nextNode = leafCount;
            for (int child = 0; child < treeSize - 1; child += 2, nextNode++)
            {
                int combinedFrequency = frequencies[child] + frequencies[child + 1];
                int insertAt = nextNode - 1;
                while (insertAt >= 0 && combinedFrequency < frequencies[insertAt]) insertAt--;
                insertAt++;
                Array.Copy(frequencies, insertAt, frequencies, insertAt + 1, nextNode - insertAt);
                Array.Copy(forward, insertAt, forward, insertAt + 1, nextNode - insertAt);
                frequencies[insertAt] = combinedFrequency;
                forward[insertAt] = child;
            }

            for (int node = 0; node < treeSize; node++)
            {
                int child = forward[node];
                backward[child] = node;
                if (child < treeSize) backward[child + 1] = node;
            }
        }

        private static void WriteTreeLeaf(List<bool> bits, byte symbol)
        {
            bits.Add(true);
            for (int bit = 7; bit >= 0; bit--) bits.Add((symbol & (1 << bit)) != 0);
        }

        private static void WriteCode(Span<byte> output, int bitOffset, ushort value, int width)
        {
            for (int bit = 0; bit < width; bit++)
                if ((value & (1 << bit)) != 0)
                    output[(bitOffset + bit) / 8] |= (byte)(1 << ((bitOffset + bit) & 7));
        }

        private static ushort HeaderCrc(ReadOnlySpan<byte> header)
        {
            byte[] copy = header.ToArray();
            copy[32] = copy[33] = 0;
            return Crc16Arc(copy);
        }

        public static ushort Crc16Arc(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }

        private static void U16(Span<byte> data, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);

        private static void U32(Span<byte> data, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);

    }
}
