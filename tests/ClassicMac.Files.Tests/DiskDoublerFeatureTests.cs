using System.Buffers.Binary;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class DiskDoublerFeatureTests
{
    [Fact]
    public void CanReadRecognizesOnlyACompleteDda2Header()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive();

        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes("DDA2"u8.ToArray())));
        Assert.True(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(archive)));

        archive[12] ^= 0x01;
        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(archive)));
    }

    [Fact]
    public void DiskDoublerPro411Method10FileExpandsToOriginalApplicationOutput()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "DiskDoublerPro411Dd3TestFile.dd"));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("testfile.PICT.dd")));

        MacFile file = Assert.Single(files);
        Assert.Equal("testfile.PICT", file.Name.ToMacRoman());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict")),
            file.DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin")),
            file.ResourceFork.ToArray());
    }

    [Fact]
    public void DiskDoublerPro411Dda2ArchiveExpandsItsOriginalDd3File()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory,
            "DiskDoublerPro411Dda2Dd3Archive.dd"));

        var context = new ContainerContext();
        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive), context);
        MacFile file = Assert.Single(files, candidate => candidate.Name.ToMacRoman() == "testfile.PICT");

        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict")),
            file.DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin")),
            file.ResourceFork.ToArray());
        Assert.DoesNotContain(context.Diagnostics, diagnostic => diagnostic.Code == "archive.entry-unsupported" &&
            diagnostic.Message.Contains("0x1000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DiskDoublerPro411Dda2Dd1Archive.dd")]
    [InlineData("DiskDoublerPro411Dda2Dd2Archive.dd")]
    public void DiskDoublerPro411OriginalDda2ArchiveExpandsBothPictureForks(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, fixtureName));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        MacFile picture = Assert.Single(files, file => file.Name.ToMacRoman() == "testfile.PICT");
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict")),
            picture.DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin")),
            picture.ResourceFork.ToArray());
    }

    [Fact]
    public void DiskDoublerPro411Dda2ArchiveRestoresItsUncompressedImageEntries()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory,
            "DiskDoublerPro411Dda2Dd3Archive.dd"));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        MacFile jpeg = Assert.Single(files, candidate => candidate.Name.ToMacRoman() == "testfile.jpg");
        Assert.Equal(FourCC.FromString("JPEG"), jpeg.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("GKON"), jpeg.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x0100, jpeg.FinderInfo.Flags);
        Assert.Equal(new[] { "sources" }, jpeg.FolderPath.Select(folder => folder.ToMacRoman()));
        Assert.Equal(new MacDate(0xB6757900), jpeg.Created);
        Assert.Equal(new MacDate(0xB6757900), jpeg.Modified);
        Assert.Equal(220, jpeg.DataFork.Length);
        Assert.Equal([0xFF, 0xD8], jpeg.DataFork.Slice(0, 2).ToArray());
        Assert.Empty(jpeg.ResourceFork.ToArray());

        MacFile png = Assert.Single(files, candidate => candidate.Name.ToMacRoman() == "testfile.png");
        Assert.Equal(FourCC.FromString("PNGf"), png.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("GKON"), png.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x0100, png.FinderInfo.Flags);
        Assert.Equal(87, png.DataFork.Length);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            png.DataFork.Slice(0, 8).ToArray());
        Assert.Empty(png.ResourceFork.ToArray());
    }

    [Fact]
    public void DefaultUnwrapperReadsTheOriginalDiskDoublerArchiveFromTheCc0Corpus()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory,
            "DiskDoublerPro411Dda2Dd3Archive.dd"));
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("Original.dd"),
            DataFork = ForkData.FromBytes(archive)
        };
        var context = new ContainerContext();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", context);

        MacFile picture = Assert.Single(result.Children, node => node.File.Name.ToMacRoman() == "testfile.PICT").File;
        MacFile jpeg = Assert.Single(result.Children, node => node.File.Name.ToMacRoman() == "testfile.jpg").File;
        MacFile png = Assert.Single(result.Children, node => node.File.Name.ToMacRoman() == "testfile.png").File;
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict")),
            picture.DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin")),
            picture.ResourceFork.ToArray());
        Assert.Equal([0xFF, 0xD8], jpeg.DataFork.Slice(0, 2).ToArray());
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            png.DataFork.Slice(0, 8).ToArray());
        Assert.DoesNotContain(context.Diagnostics, diagnostic => diagnostic.Code == "container.unreadable");
    }

    [Fact]
    public void DiskDoublerDda2RawForkLengthCannotExtendPastItsRecord()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory,
            "DiskDoublerPro411Dda2Dd3Archive.dd"));
        int offset = FindDda2Record(archive, 0x1000, "testfile.jpg");
        Assert.NotEqual(-1, offset);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(offset + 46 + 32), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void DiskDoublerDda2RawEntryRestoresBothForks()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] original = File.ReadAllBytes(Path.Combine(fixtureDirectory,
            "DiskDoublerPro411Dda2Dd3Archive.dd"));
        int recordOffset = FindDda2Record(original, 0x1000, "testfile.jpg");
        Assert.NotEqual(-1, recordOffset);

        const int recordHeaderLength = 46;
        const int rawMetadataLength = 44;
        int recordLengthOffset = recordOffset + 42;
        int recordLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(recordLengthOffset)));
        int metadataOffset = recordOffset + recordHeaderLength;
        int dataLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(metadataOffset + 32)));
        int dataOffset = metadataOffset + rawMetadataLength;
        byte[] resource = "resource fork for a raw DDA2 entry"u8.ToArray();
        int resourceInsertionOffset = dataOffset + dataLength;
        byte[] archive = new byte[original.Length + resource.Length];
        original.AsSpan(0, resourceInsertionOffset).CopyTo(archive);
        resource.CopyTo(archive.AsSpan(resourceInsertionOffset));
        original.AsSpan(resourceInsertionOffset).CopyTo(archive.AsSpan(resourceInsertionOffset + resource.Length));
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(recordLengthOffset),
            checked((uint)(recordLength + resource.Length)));
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(metadataOffset + 36), checked((uint)resource.Length));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()), candidate => candidate.Name.ToMacRoman() == "testfile.jpg");

        Assert.Equal([0xFF, 0xD8], file.DataFork.Slice(0, 2).ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
    }

    [Theory]
    [InlineData("DiskDoublerPro411Dda2Dd1Archive.dd")]
    [InlineData("DiskDoublerPro411Dda2Dd2Archive.dd")]
    [InlineData("DiskDoublerPro411Dda2Dd3Archive.dd")]
    public void DiskDoublerPro411Dda2RecordChecksumsMatchEveryRecord(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(
            ForkData.FromBytes(File.ReadAllBytes(Path.Combine(fixtureDirectory, fixtureName))),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(7, files.Count);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(0x9000, "sources", 46)]       // a directory record's CRC covers bytes 0-85
    [InlineData(0x5000, "testfile.PICT", 46)] // a file record's covers bytes 0-53
    [InlineData(0x1000, "testfile.png", 46)]  // a raw record's covers bytes 0-87
    public void DiskDoublerDda2RecordWithADamagedHeaderIsReported(ushort entryType, string name, int damagedByte)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "DiskDoublerPro411Dda2Dd3Archive.dd"));
        int offset = FindDda2Record(archive, entryType, name);
        Assert.NotEqual(-1, offset);
        archive[offset + damagedByte] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        _ = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext(diagnostics: diagnostics));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("archive.header-crc", diagnostic.Code);
        Assert.Contains(name, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskDoublerDda2RawRecordWithADamagedDataForkIsReported()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, "DiskDoublerPro411Dda2Dd3Archive.dd"));
        int offset = FindDda2Record(archive, 0x1000, "testfile.jpg");
        archive[offset + 46 + 44 + 100] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        _ = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext(diagnostics: diagnostics));

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("archive.fork-checksum", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    // StuffIt Deluxe 4.5 archive of the files DiskDoubler 3.7.7 compressed with DiskDoubler A (method 1): a folder
    // whose files carry other bytes where a folder's first-child link would be, a first file linked back to its
    // folder, and method-1 files with empty forks.
    [Fact]
    public void DiskDoubler377FilesInAStuffIt45ArchiveExpandToTheOriginals()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        string crossVersion = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItOriginalCrossVersion");
        string legacy = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItLegacy45");
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(
            Path.Combine(fixtureDirectory, "StuffIt45DiskDoubler377DdaFiles.sit"), diagnostics: diagnostics);

        ContainerNode archive = result;
        Assert.Equal(7, archive.Children.Count);
        Assert.All(archive.Children, node =>
        {
            Assert.Equal(["sources"], node.File.FolderPath.Select(folder => folder.ToMacRoman()));
            Assert.Equal(FourCC.FromString("DDAP"), node.File.FinderInfo.Creator);
            Assert.Equal(DiskDoublerReader.Instance.FormatName, Assert.Single(node.Children).Format);
        });
        MacFile Expanded(string name) =>
            Assert.Single(archive.Children, node => node.File.Name.ToMacRoman() == name).Children[0].File;
        Assert.Empty(Expanded("Test Image").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(legacy, "ExpectedTestImageResource.bin")),
            Expanded("Test Image").ResourceFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(crossVersion, "ExpectedTestText.bin")),
            Expanded("Test Text").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict")),
            Expanded("testfile.PICT").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin")),
            Expanded("testfile.PICT").ResourceFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(crossVersion, "ExpectedTestFile.jpg")),
            Expanded("testfile.jpg").DataFork.ToArray());
        Assert.Empty(Expanded("testfile.jpg").ResourceFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(crossVersion, "ExpectedTestFile.png")),
            Expanded("testfile.png").DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(crossVersion, "ExpectedTestFile.txt")),
            Expanded("testfile.txt").DataFork.ToArray());
        Assert.Equal(FourCC.FromString("JPEG"), Expanded("testfile.jpg").FinderInfo.Type);
        Assert.Equal(FourCC.FromString("GKON"), Expanded("testfile.jpg").FinderInfo.Creator);
        Assert.Empty(diagnostics);
    }

    private static int FindDda2Record(byte[] archive, ushort entryType, string name)
    {
        int offset = 62;
        while (offset <= archive.Length - 46 && archive.AsSpan(offset, 4).SequenceEqual("DDA2"u8))
        {
            ushort currentType = BinaryPrimitives.ReadUInt16BigEndian(archive.AsSpan(offset + 4));
            if (currentType == 0xBBBB) break;
            int nameLength = archive[offset + 6];
            int recordLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(offset + 42)));
            if (currentType == entryType &&
                System.Text.Encoding.ASCII.GetString(archive, offset + 7, nameLength) == name)
                return offset;
            if (recordLength < 46 || recordLength > archive.Length - offset) break;
            offset += recordLength;
        }
        return -1;
    }

    [Theory]
    [InlineData("DiskDoublerPro411Ad1TestFile.dd")]
    [InlineData("DiskDoublerPro411Ad2TestFile.dd")]
    [InlineData("DiskDoublerPro411Dd1TestFile.dd")]
    [InlineData("DiskDoublerPro411Dd2TestFile.dd")]
    [InlineData("DiskDoubler377DdaTestFile.dd")]
    [InlineData("DiskDoubler377DdbTestFile.dd")]
    public void DiskDoublerStandaloneFilesExpandToOriginalApplicationOutput(string fixtureName)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] archive = File.ReadAllBytes(Path.Combine(fixtureDirectory, fixtureName));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("testfile.PICT.dd"))));

        Assert.Equal("testfile.PICT", file.Name.ToMacRoman());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict")),
            file.DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin")),
            file.ResourceFork.ToArray());
    }

    [Fact]
    public void StandaloneDiskDoublerMethod9CopiesAnUncompressedAdnBlock()
    {
        byte[] expected = "uncompressed ADn block"u8.ToArray();
        byte[] encoded = DiskDoublerFixture.BuildAdnStoredBlock(expected);
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expected, [], dataMethod: 9, encodedData: encoded);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("Stored.dd"))));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void StandaloneDiskDoublerMethod9RejectsAnInvalidAdnBlockHeaderChecksum()
    {
        byte[] expected = "uncompressed ADn block"u8.ToArray();
        byte[] encoded = DiskDoublerFixture.BuildAdnStoredBlock(expected);
        encoded[11] ^= 0x01;
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expected, [], dataMethod: 9, encodedData: encoded);

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("Corrupt.dd"))));
    }

    [Fact]
    public void StandaloneDiskDoublerMethod1DecodesBothForksAndValidatesTheirChecksums()
    {
        byte[] expectedData = "ABC"u8.ToArray();
        byte[] expectedResource = "XY"u8.ToArray();
        byte[] encodedData = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 66, 67)];
        byte[] encodedResource = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(88, 89)];
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expectedData, expectedResource,
            dataMethod: 1, encodedData: encodedData, resourceMethod: 1, encodedResource: encodedResource);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void StandaloneDiskDoublerMethod2DecodesBothForksAndValidatesTheirChecksums()
    {
        byte[] expectedData = "AAA"u8.ToArray();
        byte[] expectedResource = "AAA"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expectedData, expectedResource,
            dataMethod: 2, encodedData: [0x41, 0x41, 0xF0],
            resourceMethod: 2, encodedResource: [0x41, 0x41, 0xF0]);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void StandaloneDiskDoublerMethod4DecodesBothHuffmanForksAndValidatesTheirChecksums()
    {
        byte[] expectedData = "ABBA"u8.ToArray();
        byte[] expectedResource = "BA"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expectedData, expectedResource,
            dataMethod: 4, encodedData: DiskDoublerFixture.BuildHuffmanFork(expectedData),
            resourceMethod: 4, encodedResource: DiskDoublerFixture.BuildHuffmanFork(expectedResource));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void StandaloneDiskDoublerMethod5DecodesBothAdaptiveHuffmanForks()
    {
        byte[] expected = "A"u8.ToArray();
        byte[] encoded = [1, 0x41];
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expected, expected,
            dataMethod: 5, encodedData: encoded, resourceMethod: 5, encodedResource: encoded);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void StandaloneDiskDoublerMethod8DecodesBothCompactProForksAndChecksTheirCrcs()
    {
        byte[] expectedData = "A"u8.ToArray();
        byte[] expectedResource = "RRR"u8.ToArray();
        byte[] encodedData = [.. new byte[16], .. DiskDoublerFixture.BuildLzhLiteral((byte)'A')];
        byte[] encodedResource = [1, .. new byte[15], (byte)'R', 0x81, 0x82, 3];
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expectedData, expectedResource,
            dataMethod: 8, encodedData: encodedData, resourceMethod: 8, encodedResource: encodedResource);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc");
    }

    [Fact]
    public void StandaloneDiskDoublerMethod3ExpandsEscapedRunsAndLiteralEscapeBytes()
    {
        byte[] encoded = [(byte)'A', 0x44, 3, 0x44, 0, (byte)'B', 0x44, 3];
        byte[] expected = [(byte)'A', (byte)'A', (byte)'A', 0x44, (byte)'B', (byte)'B', (byte)'B'];
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(expected, [], dataMethod: 3,
            encodedData: encoded);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("Runs.dd"))));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2DiskDoublerMethod3ExpandsBothForks()
    {
        byte[] data = [(byte)'A', (byte)'A', (byte)'A', 0x44, (byte)'B', (byte)'B', (byte)'B'];
        byte[] encodedData = [(byte)'A', 0x44, 3, 0x44, 0, (byte)'B', 0x44, 3];
        byte[] resource = [(byte)'z', (byte)'z', (byte)'z', (byte)'z'];
        byte[] encodedResource = [(byte)'z', 0x44, 4];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Runs", 0,
            data, resource, dataMethod: 3, resourceMethod: 3,
            encodedData: encodedData, encodedResource: encodedResource));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x44 })]
    [InlineData(new byte[] { 0x44, 2 })]
    [InlineData(new byte[] { (byte)'A', 0x44 })]
    [InlineData(new byte[] { (byte)'A' })]
    [InlineData(new byte[] { (byte)'A', (byte)'B', (byte)'C' })]
    [InlineData(new byte[] { (byte)'A', 0x44, 4 })]
    public void DiskDoublerMethod3RejectsMalformedCodesAndDeclaredLengthMismatches(byte[] encoded)
    {
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(new byte[2], [], dataMethod: 3,
            encodedData: encoded);

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("Malformed.dd"))));
    }

    [Fact]
    public void Dda2Method10ForksDecodeOriginalApplicationCompressedPayloads()
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] standalone = File.ReadAllBytes(Path.Combine(fixtureDirectory, "DiskDoublerPro411Dd3TestFile.dd"));
        int dataLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(4)));
        int compressedDataLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(8)));
        int resourceLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(12)));
        int compressedResourceLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(16)));
        byte[] compressedData = standalone.AsSpan(84, compressedDataLength).ToArray();
        byte[] compressedResource = standalone.AsSpan(84 + compressedDataLength, compressedResourceLength).ToArray();
        byte[] data = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict"));
        byte[] resource = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin"));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("testfile.PICT", 0,
            data, resource, dataMethod: 10, resourceMethod: 10, encodedData: compressedData,
            encodedResource: compressedResource));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(dataLength, file.DataFork.Length);
        Assert.Equal(resourceLength, file.ResourceFork.Length);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
    }

    [Theory]
    [InlineData("DiskDoublerPro411Ad1TestFile.dd", 9)]
    [InlineData("DiskDoublerPro411Ad2TestFile.dd", 6)]
    public void Dda2AutoDoublerMethodsDecodeOriginalApplicationCompressedPayloads(string fixtureName, byte method)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal");
        byte[] standalone = File.ReadAllBytes(Path.Combine(fixtureDirectory, fixtureName));
        int dataLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(4)));
        int compressedDataLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(8)));
        int resourceLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(12)));
        int compressedResourceLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(standalone.AsSpan(16)));
        byte[] compressedData = standalone.AsSpan(84, compressedDataLength).ToArray();
        byte[] compressedResource = standalone.AsSpan(84 + compressedDataLength, compressedResourceLength).ToArray();
        byte[] data = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedDataFork.pict"));
        byte[] resource = File.ReadAllBytes(Path.Combine(fixtureDirectory, "ExpectedResourceFork.bin"));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("testfile.PICT", 0,
            data, resource, dataMethod: method, resourceMethod: method, encodedData: compressedData,
            encodedResource: compressedResource));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(dataLength, file.DataFork.Length);
        Assert.Equal(resourceLength, file.ResourceFork.Length);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StandaloneMethod10RejectsACorruptBlockHeaderChecksum()
    {
        byte[] archive = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal",
            "DiskDoublerPro411Dd3TestFile.dd"));
        archive[84 + 21] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("testfile.PICT.dd"))));
    }

    [Fact]
    public void StandaloneMethod10RejectsAnIncorrectExpandedBlockChecksum()
    {
        byte[] archive = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "DiskDoublerOriginal",
            "DiskDoublerPro411Dd3TestFile.dd"));
        archive[84 + 19] ^= 0x01;
        archive[84 + 21] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("testfile.PICT.dd"))));
    }

    [Fact]
    public void Dda2RejectsAnInvalidArchiveHeaderChecksum()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive();
        archive[12] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(
            ForkData.FromBytes(archive), new ContainerContext()));
    }

    [Fact]
    public void Dda2AppliesDeltaPreprocessingToEachForkIndependently()
    {
        byte[] encodedData = [0x10, 0x01, 0x02, 0xFD];
        byte[] encodedResource = [0xFE, 0x05, 0x04];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Delta", 0,
            encodedData, encodedResource, dataDelta: 1, resourceDelta: 1));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(new byte[] { 0x10, 0x11, 0x13, 0x10 }, file.DataFork.ToArray());
        Assert.Equal(new byte[] { 0xFE, 0x03, 0x07 }, file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2DeltaType2AccumulatesThreeInterleavedByteLanesWithWraparound()
    {
        byte[] encodedData = [1, 10, 100, 1, 2, 3, 255];
        byte[] encodedResource = [250, 2, 1, 10, 20];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Three delta",
            0, encodedData, encodedResource, dataDelta: 2, resourceDelta: 2));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(new byte[] { 1, 10, 100, 2, 12, 103, 1 }, file.DataFork.ToArray());
        Assert.Equal(new byte[] { 250, 2, 1, 4, 22 }, file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2AppliesDeltaAfterCompressedForkChecksumValidation()
    {
        byte[] decodedBeforeDelta = [0x10, 0x01, 0x02, 0xFD];
        byte[] encoded = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(0x10, 0x01, 0x02, 0xFD)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Compressed delta", 0,
            decodedBeforeDelta, [], dataMethod: 1, encodedData: encoded, dataDelta: 1));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(new byte[] { 0x10, 0x11, 0x13, 0x10 }, file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void StandaloneDiskDoublerAppliesDeltaPreprocessingToBothForks()
    {
        byte[] encodedData = [0x10, 0x01, 0x02, 0xFD];
        byte[] encodedResource = [0x41, 0x01, 0x01];
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(encodedData, encodedResource, dataDelta: 1,
            resourceDelta: 1);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("Delta.dd"))));

        Assert.Equal(new byte[] { 0x10, 0x11, 0x13, 0x10 }, file.DataFork.ToArray());
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StandaloneDiskDoublerDeltaType2PreservesOneAndTwoByteTails()
    {
        byte[] encodedData = [250];
        byte[] encodedResource = [250, 10];
        byte[] archive = DiskDoublerFixture.BuildStandaloneFile(encodedData, encodedResource,
            dataDelta: 2, resourceDelta: 2);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(hostName: MacString.FromMacRoman("Short.dd"))));

        Assert.Equal(encodedData, file.DataFork.ToArray());
        Assert.Equal(encodedResource, file.ResourceFork.ToArray());
    }

    [Fact]
    public void StandaloneDiskDoublerFileExtractsBothForksAndUsesTheHostName()
    {
        byte[] data = "standalone data"u8.ToArray();
        byte[] resource = "standalone resource"u8.ToArray();
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile(data, resource);
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("Sample.dd"),
            DataFork = ForkData.FromBytes(packed)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        MacFile file = Assert.Single(result.Children).File;
        Assert.Equal("Sample", file.Name.ToMacRoman());
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
    }

    [Fact]
    public void CanReadRecognizesStandaloneDiskDoublerFileWithAValidHeaderChecksum()
    {
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile("data"u8.ToArray(), []);

        Assert.True(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(packed)));

        packed[12] ^= 0x01;
        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(packed)));
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(
            ForkData.FromBytes(packed), new ContainerContext()));
    }

    [Fact]
    public void CanReadRecognizesLegacyStandaloneDiskDoublerFilesWithoutAHeaderChecksum()
    {
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile("data"u8.ToArray(), [], headerChecksum: false);

        Assert.True(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(packed)));
    }

    [Fact]
    public void StandaloneDiskDoublerFileWithZeroHeaderChecksumIsStillReadable()
    {
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile("legacy"u8.ToArray(), [], headerChecksum: false);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(packed),
            new ContainerContext(hostName: MacString.FromMacRoman("Old.dd"))));

        Assert.Equal("legacy"u8.ToArray(), file.DataFork.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void StandaloneDiskDoublerFilesDecodeTheSupportedCompressedMethods(byte method)
    {
        byte[] expected = "A"u8.ToArray();
        byte[] encoded = CompressedFork(method, expected, (byte)'A');
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile(expected, [], method, encoded);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(packed),
            new ContainerContext(hostName: MacString.FromMacRoman("Compressed.dd"))));

        Assert.Equal("Compressed", file.Name.ToMacRoman());
        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void StandaloneDiskDoublerFilesDecodeCompressedResourceForks(byte method)
    {
        byte[] expected = "R"u8.ToArray();
        byte[] encoded = CompressedFork(method, expected, (byte)'R');
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile([], expected, resourceMethod: method,
            encodedResource: encoded);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(packed),
            new ContainerContext(hostName: MacString.FromMacRoman("Resource.dd"))));

        Assert.Empty(file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(0, 3)]
    public void StandaloneDiskDoublerFilesReportUnsupportedMethodsAndDeltaProcessing(byte method, ushort delta)
    {
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile([1], [], method, dataDelta: delta);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(packed),
            new ContainerContext(diagnostics: diagnostics, hostName: MacString.FromMacRoman("Unsupported.dd")));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.method-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void StandaloneDiskDoublerFileRejectsForkPayloadPastTheFileEnd()
    {
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile("data"u8.ToArray(), []);
        Array.Resize(ref packed, packed.Length - 1);

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(
            ForkData.FromBytes(packed), new ContainerContext()));
    }

    [Fact]
    public void ReadRejectsInputTooShortToContainAFormatSignature()
    {
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(
            ForkData.FromBytes(new byte[] { 0xAB, 0xCD, 0x00 }), new ContainerContext()));
    }

    [Fact]
    public void StandaloneDiskDoublerFileReportsAForkChecksumMismatchButKeepsTheDecodedFork()
    {
        byte[] packed = DiskDoublerFixture.BuildStandaloneFile("A"u8.ToArray(), [], dataMethod: 2,
            encodedData: [0x41]);
        packed[48] ^= 0x01;
        DiskDoublerFixture.UpdateStandaloneHeaderChecksum(packed);
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(packed),
            new ContainerContext(diagnostics: diagnostics, hostName: MacString.FromMacRoman("Bad.dd"))));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Offset == 48);
    }

    private static byte[] CompressedFork(byte method, byte[] expected, byte symbol) => method switch
    {
        1 => [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(symbol)],
        2 => [symbol],
        4 => DiskDoublerFixture.BuildHuffmanFork(expected, symbolA: symbol),
        8 => [.. new byte[16], .. DiskDoublerFixture.BuildLzhLiteral(symbol)],
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    [Fact]
    public void CanReadRecognizesLegacyDdarArchivesWithACompleteHeader()
    {
        Assert.True(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes(DiskDoublerFixture.BuildLegacyArchive())));
        Assert.False(DiskDoublerReader.Instance.CanRead(ForkData.FromBytes("DDAR"u8.ToArray())));
    }

    [Fact]
    public void LegacyDdarReadsStoredForksAndFinderMetadata()
    {
        byte[] data = "legacy data"u8.ToArray();
        byte[] resource = "legacy resource"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFile("Legacy", data, resource));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        MacFile file = Assert.Single(files);
        Assert.Equal("Legacy", file.MacPath);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
    }

    [Fact]
    public void LegacyDdarFolderMarkersBuildAndUnwindNestedPaths()
    {
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFolder("Folder"),
            DiskDoublerFixture.BuildLegacyFolder("Nested"),
            DiskDoublerFixture.BuildLegacyFile("Note", "inside"u8.ToArray(), []),
            DiskDoublerFixture.BuildLegacyEndFolder(),
            DiskDoublerFixture.BuildLegacyFile("Sibling", [], []));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        Assert.Equal(new[] { "Folder:Nested:Note", "Folder:Sibling" }, files.Select(file => file.MacPath));
    }

    [Fact]
    public void LegacyDdarSkipsRedundantTrailingSingleFileHeaders()
    {
        byte[] redundantHeader = new byte[84];
        BinaryPrimitives.WriteUInt32BigEndian(redundantHeader, 0xABCD0054);
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFile("File", [1], []), redundantHeader);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("File", file.MacPath);
        Assert.Equal(new byte[] { 1 }, file.DataFork.ToArray());
    }

    [Fact]
    public void LegacyDdarRejectsTruncatedHeadersPayloadsAndUnmatchedFolderEnds()
    {
        byte[] validHeader = DiskDoublerFixture.BuildLegacyArchive();
        byte[] truncatedHeader = [.. validHeader, .. "DDAR"u8.ToArray(), 0, 0, 0];
        byte[] payload = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFile("Truncated", [1], []));
        Array.Resize(ref payload, payload.Length - 1);
        byte[] unmatchedFolderEnd = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyEndFolder());

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(truncatedHeader),
            new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(payload),
            new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(unmatchedFolderEnd),
            new ContainerContext()));
    }

    [Fact]
    public void LegacyDdarHonorsFolderNestingLimit()
    {
        byte[] archive = DiskDoublerFixture.BuildLegacyArchive(
            DiskDoublerFixture.BuildLegacyFolder("Folder"));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxNestingDepth = 0 })));
    }

    [Fact]
    public void DefaultUnwrapperReadsDda2StoredEntryWithBothForksAndFinderMetadata()
    {
        byte[] data = "data fork"u8.ToArray();
        byte[] resource = "resource fork"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Read Me", 0, data, resource));
        var container = new MacFile
        {
            Name = MacString.FromMacRoman("archive.dd"),
            DataFork = ForkData.FromBytes(archive)
        };

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(container, "test", new ContainerContext());

        MacFile file = Assert.Single(result.Children).File;
        Assert.Equal("Read Me", file.MacPath);
        Assert.Equal(data, file.DataFork.ToArray());
        Assert.Equal(resource, file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0x4000, file.FinderInfo.Flags);
        Assert.Equal(new MacDate(2_500_000_000), file.Created);
        Assert.Equal(new MacDate(2_600_000_000), file.Modified);
    }

    [Fact]
    public void Dda2DirectoryDepthProducesNestedMacPaths()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFolder("Folder", 0),
            DiskDoublerFixture.BuildFolder("Nested", 1),
            DiskDoublerFixture.BuildFile("Note", 2, "inside"u8.ToArray(), []));

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext());

        MacFile file = Assert.Single(files);
        Assert.Equal("Folder:Nested:Note", file.MacPath);
        Assert.Equal(new[] { "Folder", "Nested" }, file.FolderPath.Select(folder => folder.ToMacRoman()));
        Assert.Empty(file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2UnsupportedCompressionSkipsThatEntryAndContinuesAtTheNextRecord()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Unsupported", 0, [1], [], dataMethod: 11),
            DiskDoublerFixture.BuildFile("Stored", 0, "available"u8.ToArray(), []));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal("Stored", Assert.Single(files).MacPath);
        Assert.Equal("available"u8.ToArray(), files[0].DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.method-unsupported" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Dda2CompactProMethodDecodesLzhAndRleForks()
    {
        byte[] expectedData = "A"u8.ToArray();
        byte[] expectedResource = "RRR"u8.ToArray();
        byte[] dataStream = [.. new byte[16], .. DiskDoublerFixture.BuildLzhLiteral((byte)'A')];
        byte[] resourceStream = [1, .. new byte[15], (byte)'R', 0x81, 0x82, 3];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("CompactPro", 0,
            expectedData, expectedResource, dataMethod: 8, resourceMethod: 8,
            encodedData: dataStream, encodedResource: resourceStream));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc");
    }

    [Fact]
    public void Dda2MacCompressMethodDecodesLzwFork()
    {
        byte[] expected = "ABC"u8.ToArray();
        byte[] compressed = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 66, 67)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Lzw", 0,
            expected, [], dataMethod: 1, encodedData: compressed));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expected, file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void Dda2Method2DecodesAnInitialAdaptiveHuffmanTreeSymbol()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive", 0,
            "AAA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0]));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void Dda2Method5UsesOneAdaptiveTreeWhenTreeCountIsOne()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive", 0,
            "A"u8.ToArray(), [], dataMethod: 5, encodedData: [1, 0x41]));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method5WrapsTheNextTreeIndexByTheDeclaredTreeCount()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive", 0,
            "AB"u8.ToArray(), [], dataMethod: 5, encodedData: [2, 0x41, 0x42]));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("AB"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method5TreatsAZeroTreeCountAs256()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive", 0,
            "AAA"u8.ToArray(), [], dataMethod: 5, encodedData: [0, 0x41, 0x41, 0xF0]));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method5RejectsAMissingTreeCount()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "A"u8.ToArray(), [], dataMethod: 5, encodedData: []));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method7DecodesLiteralBytes()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsLiterals([0xBE]), entryCount: 2);
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("LZS", 0,
            "A"u8.ToArray(), [], dataMethod: 7, encodedData: payload));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method7DecodesBackreferences()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsRepeat([0xBE, 0xBD], offset: 2, length: 2));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("LZS", 0,
            "ABAB"u8.ToArray(), [], dataMethod: 7, encodedData: payload));

        var diagnostics = new List<Diagnostic>();
        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("ABAB"u8.ToArray(), file.DataFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void Dda2Method7DecodesAnElevenBitBackreferenceOffset()
    {
        byte[] source = Enumerable.Range(0, 128).Select(index => (byte)(index % 2 == 0 ? 'A' : 'B')).ToArray();
        byte[] encodedSymbols = source.Select(value => (byte)(value ^ 0xFF)).ToArray();
        byte[] expected = [.. source, (byte)'A', (byte)'B'];
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsRepeat(encodedSymbols, offset: 128, length: 2));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("LZS", 0,
            expected, [], dataMethod: 7, encodedData: payload));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Theory]
    [InlineData(23)]
    [InlineData(38)]
    public void Dda2Method7DecodesAnExtendedBackreferenceLength(int matchLength)
    {
        byte[] expected = Enumerable.Repeat((byte)'A', matchLength + 1).ToArray();
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsRepeat([0xBE], offset: 1, length: matchLength));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("LZS", 0,
            expected, [], dataMethod: 7, encodedData: payload));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method7DecodesAResourceFork()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsLiterals([0xBE]));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("LZS", 0,
            [], "A"u8.ToArray(), resourceMethod: 7, encodedResource: payload));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void StandaloneMethod7DecodesBothForks()
    {
        byte[] data = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsLiterals([0xBE]));
        byte[] resource = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsLiterals([0xBD]));
        byte[] standalone = DiskDoublerFixture.BuildStandaloneFile("A"u8.ToArray(), "B"u8.ToArray(),
            dataMethod: 7, encodedData: data, resourceMethod: 7, encodedResource: resource);

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(standalone),
            new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
        Assert.Equal("B"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2Method7RejectsATruncatedCompressedStream()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload([0x00]);
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "A"u8.ToArray(), [], dataMethod: 7, encodedData: payload));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method7RejectsATruncatedHeader()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "A"u8.ToArray(), [], dataMethod: 7, encodedData: []));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method7RejectsATruncatedDictionary()
    {
        byte[] payload = [.. new byte[6], 0, 0, 0, 1];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "A"u8.ToArray(), [], dataMethod: 7, encodedData: payload));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method7RejectsAnInvalidBackreference()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsRepeat([], offset: 1, length: 2));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Invalid offset", 0,
            "A"u8.ToArray(), [], dataMethod: 7, encodedData: payload));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method7RejectsAMatchThatExceedsTheDeclaredForkLength()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsRepeat([0xBE], offset: 1, length: 2));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Overrun", 0,
            "AA"u8.ToArray(), [], dataMethod: 7, encodedData: payload));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method7RejectsAnEndMarkerBeforeTheDeclaredForkLength()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(DiskDoublerFixture.BuildStacLzsLiterals([]));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Short", 0,
            "A"u8.ToArray(), [], dataMethod: 7, encodedData: payload));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method7ReportsAnIncorrectForkChecksum()
    {
        byte[] payload = DiskDoublerFixture.BuildStacLzsPayload(
            DiskDoublerFixture.BuildStacLzsLiterals([0xBE]));
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad checksum", 0,
            "A"u8.ToArray(), [], dataMethod: 7, encodedData: payload));
        archive[62 + 60 + 44] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("A"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2Method2UsesThePreviousSymbolToAdaptItsTree()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive", 0,
            "AAA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0]));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method2AppliesTheInfoSelectedOutputXor()
    {
        byte[] encoded = [0x41, 0x41, 0xF0];
        byte[] expected = [0x1B, 0x1B, 0x1B];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive XOR", 0,
            expected, [], dataMethod: 2, encodedData: encoded, info1: 0x2A));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Theory]
    [InlineData(0x2A, 0x80)]
    [InlineData(0x29, 0x00)]
    public void Dda2Method2LeavesOutputUnchangedWhenInfoDisablesTheXor(byte info1, byte info2)
    {
        byte[] expected = "AAA"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive no XOR", 0,
            expected, [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0], info1: info1, info2: info2));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2Method2DecodesTheResourceFork()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Adaptive resource", 0,
            [], "A"u8.ToArray(), resourceMethod: 2, encodedResource: [0x41]));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal("A"u8.ToArray(), file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2Method2RejectsTruncatedAdaptiveHuffmanCodes()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "AA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41]));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2Method2ReportsChecksumMismatchAfterDecoding()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad adaptive checksum", 0,
            "AAA"u8.ToArray(), [], dataMethod: 2, encodedData: [0x41, 0x41, 0xF0]));
        archive[62 + 60 + 44] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("AAA"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2MacCompressMethodDecodesLzwDictionaryReferences()
    {
        byte[] expected = "AAA"u8.ToArray();
        byte[] compressed = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 256)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Repeated", 0,
            expected, [], dataMethod: 1, encodedData: compressed));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressMethodChangesCodeWidthAtDictionaryBoundary()
    {
        byte[] expected = Enumerable.Range(0, 258).Select(index => (byte)(index % 256)).ToArray();
        byte[] compressed = [0, 0, 10, .. DiskDoublerFixture.PackLzwWidthTransition(expected)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Wide codes", 0,
            expected, [], dataMethod: 1, encodedData: compressed));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressBlockModeClearsDictionaryAndStartsAnotherCodeGroup()
    {
        byte[] expected = "AB"u8.ToArray();
        byte[] compressed = [0, 0, 0x89, .. DiskDoublerFixture.PackLzwClearAndRestart(65, 66)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Block mode", 0,
            expected, [], dataMethod: 1, encodedData: compressed));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressMethodRejectsAnUnassignedLzwCode()
    {
        byte[] compressed = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 300)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad code", 0,
            "AB"u8.ToArray(), [], dataMethod: 1, encodedData: compressed));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2MacCompressMethodAppliesTheArchiveXorVariantToDecodedBytes()
    {
        byte xor = 0x5A;
        byte[] expected = "ABC"u8.ToArray();
        byte[] encrypted = expected.Select(value => (byte)(value ^ xor)).ToArray();
        byte[] compressed = [(byte)(0 ^ xor), (byte)(0 ^ xor), (byte)(9 ^ xor),
            .. DiskDoublerFixture.PackLsbCodes(encrypted[0], encrypted[1], encrypted[2])];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Xor", 0,
            expected, [], dataMethod: 1, encodedData: compressed, info1: 0x2A));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2MacCompressMethodReportsChecksumMismatchAfterDecoding()
    {
        byte[] encoded = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes(65, 66, 67)];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad checksum", 0,
            "ABC"u8.ToArray(), [], dataMethod: 1, encodedData: encoded));
        archive[62 + 60 + 44] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("ABC"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2MacCompressMethodDecodesTheResourceFork()
    {
        byte[] expected = "R"u8.ToArray();
        byte[] encoded = [0, 0, 9, .. DiskDoublerFixture.PackLsbCodes((byte)'R')];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Resource", 0,
            [], expected, resourceMethod: 1, encodedResource: encoded));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Empty(file.DataFork.ToArray());
        Assert.Equal(expected, file.ResourceFork.ToArray());
    }

    [Fact]
    public void Dda2HuffmanMethodDecodesBothForksAndChecksTheirByteSums()
    {
        byte[] expectedData = "ABBA"u8.ToArray();
        byte[] expectedResource = "BA"u8.ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Huffman", 0,
            expectedData, expectedResource, dataMethod: 4, resourceMethod: 4,
            encodedData: DiskDoublerFixture.BuildHuffmanFork(expectedData),
            encodedResource: DiskDoublerFixture.BuildHuffmanFork(expectedResource)));
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal(expectedData, file.DataFork.ToArray());
        Assert.Equal(expectedResource, file.ResourceFork.ToArray());
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum");
    }

    [Fact]
    public void Dda2HuffmanMethodRejectsTruncatedTreesAndReportsBadByteSum()
    {
        byte[] truncated = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            [1], [], dataMethod: 4, encodedData: []));
        byte[] mismatch = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad sum", 0,
            "AB"u8.ToArray(), [], dataMethod: 4,
            encodedData: DiskDoublerFixture.BuildHuffmanFork("AB"u8)));
        mismatch[62 + 60 + 44] ^= 1;
        var diagnostics = new List<Diagnostic>();

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(truncated),
            new ContainerContext()));
        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(mismatch),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("AB"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-checksum" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2HuffmanMethodAppliesTheInfoSelectedXorVariant()
    {
        const byte xor = 0x5A;
        byte[] expected = "ABBA"u8.ToArray();
        byte[] transformed = expected.Select(value => (byte)(value ^ xor)).ToArray();
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Huffman XOR", 0,
            expected, [], dataMethod: 4,
            encodedData: DiskDoublerFixture.BuildHuffmanFork(transformed, (byte)('A' ^ xor), (byte)('B' ^ xor)),
            info1: 0x2A));

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));

        Assert.Equal(expected, file.DataFork.ToArray());
    }

    [Fact]
    public void Dda2CompactProMethodReportsForkChecksumMismatch()
    {
        byte[] encodedData = [1, .. new byte[15], (byte)'X'];
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad CRC", 0,
            "X"u8.ToArray(), [], dataMethod: 8, encodedData: encodedData));
        archive[62 + 60 + 44] ^= 0x01;
        var diagnostics = new List<Diagnostic>();

        MacFile file = Assert.Single(DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics)));

        Assert.Equal("X"u8.ToArray(), file.DataFork.ToArray());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.fork-crc" &&
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Dda2CompactProMethodRejectsATruncatedMethodHeader()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Truncated", 0,
            "A"u8.ToArray(), [], dataMethod: 8, encodedData: new byte[15]));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2MacCompressMethodRejectsInvalidFlagsAndTruncatedHeader()
    {
        byte[] invalidFlags = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Bad flags", 0,
            [1], [], dataMethod: 1, encodedData: [0, 0, 8]));
        byte[] truncated = DiskDoublerFixture.BuildArchive(DiskDoublerFixture.BuildFile("Short header", 0,
            [1], [], dataMethod: 1, encodedData: [0, 0]));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(invalidFlags),
            new ContainerContext()));
        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(truncated),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2RejectsTruncatedFileMetadataAndForkPayload()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Truncated", 0, "data"u8.ToArray(), []));
        Array.Resize(ref archive, archive.Length - 9);

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2RejectsFolderDepthThatHasNoParentDirectory()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("Orphan", 1, "data"u8.ToArray(), []));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext()));
    }

    [Fact]
    public void Dda2HonorsTheConfiguredVolumeEntryLimit()
    {
        byte[] archive = DiskDoublerFixture.BuildArchive(
            DiskDoublerFixture.BuildFile("First", 0, [], []),
            DiskDoublerFixture.BuildFile("Second", 0, [], []));

        Assert.Throws<InvalidDataException>(() => DiskDoublerReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(options: ContainerReadOptions.Default with { MaxVolumeEntries = 1 })));
    }

    private static class DiskDoublerFixture
    {
        private const int ArchiveHeaderLength = 62;
        private const int RecordHeaderLength = 46;
        private const int FileHeaderLength = 80;
        private const int LegacyArchiveHeaderLength = 78;
        private const int LegacyRecordHeaderLength = 124;

        public static byte[] BuildLegacyArchive(params byte[][] records)
        {
            byte[] archive = new byte[LegacyArchiveHeaderLength + records.Sum(record => record.Length)];
            "DDAR"u8.CopyTo(archive);
            int offset = LegacyArchiveHeaderLength;
            foreach (byte[] record in records)
            {
                record.CopyTo(archive, offset);
                offset += record.Length;
            }
            return archive;
        }

        public static byte[] BuildLegacyFolder(string name) => BuildLegacyRecord(name, isDirectory: true,
            isEndDirectory: false, [], []);

        public static byte[] BuildLegacyEndFolder() => BuildLegacyRecord("", isDirectory: false,
            isEndDirectory: true, [], []);

        public static byte[] BuildLegacyFile(string name, byte[] data, byte[] resource) =>
            BuildLegacyRecord(name, isDirectory: false, isEndDirectory: false, data, resource);

        private static byte[] BuildLegacyRecord(string name, bool isDirectory, bool isEndDirectory,
            byte[] data, byte[] resource)
        {
            byte[] nameBytes = MacString.FromMacRoman(name).Bytes.ToArray();
            if (nameBytes.Length > 63) throw new ArgumentOutOfRangeException(nameof(name));
            byte[] bytes = new byte[LegacyRecordHeaderLength + data.Length + resource.Length];
            "DDAR"u8.CopyTo(bytes);
            bytes[8] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(bytes, 9);
            bytes[72] = isDirectory ? (byte)1 : (byte)0;
            bytes[73] = isEndDirectory ? (byte)1 : (byte)0;
            U32(bytes, 74, checked((uint)data.Length));
            U32(bytes, 78, checked((uint)resource.Length));
            U32(bytes, 82, 2_500_000_000);
            U32(bytes, 86, 2_600_000_000);
            "TEXTttxt"u8.CopyTo(bytes.AsSpan(90));
            U16(bytes, 98, 0x4000);
            data.CopyTo(bytes, LegacyRecordHeaderLength);
            resource.CopyTo(bytes, LegacyRecordHeaderLength + data.Length);
            return bytes;
        }

        public static byte[] BuildArchive(params byte[][] records)
        {
            int length = ArchiveHeaderLength + records.Sum(record => record.Length) + 6;
            byte[] archive = new byte[length];
            "DDA2"u8.CopyTo(archive);
            int offset = ArchiveHeaderLength;
            foreach (byte[] record in records)
            {
                record.CopyTo(archive, offset);
                offset += record.Length;
            }
            "DDA2"u8.CopyTo(archive.AsSpan(offset));
            U16(archive, offset + 4, 0xBBBB);
            U16(archive, 60, Crc16Xmodem(archive.AsSpan(0, 60)));
            return archive;
        }

        public static byte[] BuildStandaloneFile(byte[] data, byte[] resource, byte dataMethod = 0,
            byte[]? encodedData = null, bool headerChecksum = true, ushort dataDelta = 0, byte resourceMethod = 0,
            byte[]? encodedResource = null, ushort resourceDelta = 0)
        {
            encodedData ??= data;
            encodedResource ??= resource;
            byte[] archive = new byte[84 + encodedData.Length + encodedResource.Length];
            U32(archive, 0, 0xABCD0054);
            U32(archive, 4, checked((uint)data.Length));
            U32(archive, 8, checked((uint)encodedData.Length));
            U32(archive, 12, checked((uint)resource.Length));
            U32(archive, 16, checked((uint)encodedResource.Length));
            archive[20] = dataMethod;
            archive[21] = resourceMethod;
            U32(archive, 24, 2_600_000_000);
            U32(archive, 28, 2_500_000_000);
            "TEXTttxt"u8.CopyTo(archive.AsSpan(32));
            U16(archive, 40, 0x4000);
            U16(archive, 54, dataDelta);
            U16(archive, 56, resourceDelta);
            if (dataMethod == 1) U16(archive, 48, MacCompressChecksum(data, encodedData, 0, 0));
            if (dataMethod is 2 or 4 or 5) U16(archive, 48, ByteSum(data));
            if (dataMethod == 7) U16(archive, 48, StacLzsChecksum(data));
            if (dataMethod == 8) U16(archive, 48, Crc16Ibm(data));
            if (resourceMethod == 1) U16(archive, 50, MacCompressChecksum(resource, encodedResource, 0, 0));
            if (resourceMethod is 2 or 4 or 5) U16(archive, 50, ByteSum(resource));
            if (resourceMethod == 7) U16(archive, 50, StacLzsChecksum(resource));
            if (resourceMethod == 8) U16(archive, 50, Crc16Ibm(resource));
            encodedData.CopyTo(archive, 84);
            encodedResource.CopyTo(archive, 84 + encodedData.Length);
            if (headerChecksum) U16(archive, 82, Crc16Xmodem(archive.AsSpan(0, 82)));
            return archive;
        }

        public static byte[] BuildAdnStoredBlock(byte[] data)
        {
            if (data.Length > 0x2000) throw new ArgumentOutOfRangeException(nameof(data));
            byte[] block = new byte[12 + data.Length];
            U16(block, 0, checked((ushort)data.Length));
            U16(block, 2, checked((ushort)data.Length));
            block[9] = 1;
            byte headerXor = 0;
            for (int index = 0; index < 11; index++) headerXor ^= block[index];
            block[11] = headerXor;
            data.CopyTo(block, 12);
            return block;
        }

        public static void UpdateStandaloneHeaderChecksum(byte[] archive) =>
            U16(archive, 82, Crc16Xmodem(archive.AsSpan(0, 82)));

        public static byte[] BuildFolder(string name, int depth)
        {
            byte[] bytes = NewRecord(name, checked((uint)depth + 2), RecordHeaderLength + 16, 0x8000);
            U32(bytes, RecordHeaderLength + 8, 2_500_000_000);
            U32(bytes, RecordHeaderLength + 12, 2_600_000_000);
            return bytes;
        }

        public static byte[] BuildFile(string name, int depth, byte[] data, byte[] resource, byte dataMethod = 0,
            byte resourceMethod = 0, byte[]? encodedData = null, byte[]? encodedResource = null, byte info1 = 0,
            byte info2 = 0, ushort dataDelta = 0, ushort resourceDelta = 0)
        {
            encodedData ??= data;
            encodedResource ??= resource;
            int payloadLength = checked(encodedData.Length + encodedResource.Length);
            byte[] bytes = NewRecord(name, checked((uint)depth + 2), RecordHeaderLength + 10 + 4 + FileHeaderLength + payloadLength);
            U32(bytes, RecordHeaderLength + 10, 0xABCD0054);
            int header = RecordHeaderLength + 14;
            U32(bytes, header, checked((uint)data.Length));
            U32(bytes, header + 4, checked((uint)encodedData.Length));
            U32(bytes, header + 8, checked((uint)resource.Length));
            U32(bytes, header + 12, checked((uint)encodedResource.Length));
            bytes[header + 16] = dataMethod;
            bytes[header + 17] = resourceMethod;
            bytes[header + 18] = info1;
            bytes[header + 48] = info2;
            U16(bytes, header + 50, dataDelta);
            U16(bytes, header + 52, resourceDelta);
            U32(bytes, header + 20, 2_600_000_000);
            U32(bytes, header + 24, 2_500_000_000);
            "TEXTttxt"u8.CopyTo(bytes.AsSpan(header + 28));
            U16(bytes, header + 36, 0x4000);
            if (dataMethod == 8) U16(bytes, header + 44, Crc16Ibm(data));
            if (resourceMethod == 8) U16(bytes, header + 46, Crc16Ibm(resource));
            if (dataMethod == 1 && encodedData.Length >= 3) U16(bytes, header + 44,
                MacCompressChecksum(data, encodedData, info1, info2));
            if (dataMethod is 2 or 5) U16(bytes, header + 44, ByteSum(data));
            if (dataMethod == 7) U16(bytes, header + 44, StacLzsChecksum(data));
            if (resourceMethod == 1 && encodedResource.Length >= 3) U16(bytes, header + 46,
                MacCompressChecksum(resource, encodedResource, info1, info2));
            if (resourceMethod is 2 or 5) U16(bytes, header + 46, ByteSum(resource));
            if (resourceMethod == 7) U16(bytes, header + 46, StacLzsChecksum(resource));
            if (dataMethod == 4) U16(bytes, header + 44, ByteSum(data));
            if (resourceMethod == 4) U16(bytes, header + 46, ByteSum(resource));
            encodedData.CopyTo(bytes, RecordHeaderLength + 14 + FileHeaderLength);
            encodedResource.CopyTo(bytes, RecordHeaderLength + 14 + FileHeaderLength + encodedData.Length);
            return bytes;
        }

        private static ushort Crc16Ibm(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xA001));
            }
            return crc;
        }

        private static ushort Crc16Xmodem(ReadOnlySpan<byte> bytes)
        {
            ushort crc = 0;
            foreach (byte value in bytes)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) == 0 ? crc << 1 : (crc << 1) ^ 0x1021);
            }
            return crc;
        }

        private static ushort MacCompressChecksum(ReadOnlySpan<byte> data, ReadOnlySpan<byte> encoded,
            byte info1, byte info2)
        {
            byte xor = info1 >= 0x2A && (info2 & 0x80) == 0 ? (byte)0x5A : (byte)0;
            uint sum = (uint)(encoded[0] ^ xor) + (uint)(encoded[1] ^ xor) + (uint)(encoded[2] ^ xor);
            foreach (byte value in data) sum += value;
            return (ushort)sum;
        }

        private static ushort ByteSum(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            foreach (byte value in data) sum += value;
            return (ushort)sum;
        }

        private static ushort StacLzsChecksum(ReadOnlySpan<byte> data)
        {
            byte xor = 0;
            foreach (byte value in data) xor ^= value;
            if ((data.Length & 1) == 0) xor ^= 0xFF;
            return xor;
        }

        public static byte[] BuildHuffmanFork(ReadOnlySpan<byte> plain, byte symbolA = (byte)'A',
            byte symbolB = (byte)'B')
        {
            var bits = new List<bool>();
            bits.Add(false);
            bits.Add(true);
            WriteBits(bits, symbolA, 8);
            bits.Add(true);
            WriteBits(bits, symbolB, 8);
            foreach (byte value in plain)
                bits.Add(value == symbolB);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] BuildLzhLiteral(byte value)
        {
            var bits = new List<bool>();
            int symbolCount = ((value + 2) / 2) * 2;
            WriteBits(bits, symbolCount / 2, 8);
            for (int symbol = 0; symbol < symbolCount; symbol++) WriteBits(bits, symbol == value ? 1 : 0, 4);
            WriteBits(bits, 0, 8);
            WriteBits(bits, 0, 8);
            WriteBits(bits, 1, 1);
            WriteBits(bits, 0, 1);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] BuildStacLzsPayload(ReadOnlySpan<byte> compressedStream, uint entryCount = 0)
        {
            int streamOffset = checked(18 + (int)entryCount * 2);
            byte[] payload = new byte[checked(streamOffset + compressedStream.Length)];
            U32(payload, 6, entryCount);
            for (int index = 0; index < compressedStream.Length; index++)
                payload[streamOffset + index] = (byte)(compressedStream[index] ^ 0xFF);
            return payload;
        }

        public static byte[] BuildStacLzsLiterals(ReadOnlySpan<byte> values)
        {
            var bits = new List<bool>();
            foreach (byte value in values)
            {
                bits.Add(false);
                WriteBits(bits, value, 8);
            }
            AppendStacLzsEndMarker(bits);
            return PackMsbBits(bits);
        }

        public static byte[] BuildStacLzsRepeat(ReadOnlySpan<byte> literals, int offset, int length)
        {
            if (offset is <= 0 or > 2047) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length < 2) throw new ArgumentOutOfRangeException(nameof(length));
            var bits = new List<bool>();
            foreach (byte value in literals)
            {
                bits.Add(false);
                WriteBits(bits, value, 8);
            }
            bits.Add(true);
            bool shortOffset = offset <= 127;
            bits.Add(shortOffset);
            WriteBits(bits, offset, shortOffset ? 7 : 11);
            WriteStacLzsLength(bits, length);
            AppendStacLzsEndMarker(bits);
            return PackMsbBits(bits);
        }

        private static void WriteStacLzsLength(List<bool> bits, int length)
        {
            if (length is >= 2 and <= 4)
            {
                WriteBits(bits, length - 2, 2);
                return;
            }
            bits.Add(true);
            bits.Add(true);
            if (length <= 7)
            {
                WriteBits(bits, length - 5, 2);
                return;
            }
            bits.Add(true);
            bits.Add(true);
            if (length <= 22)
            {
                WriteBits(bits, length - 8, 4);
                return;
            }

            WriteBits(bits, 15, 4);
            int remainder = length - 23;
            while (remainder >= 15)
            {
                WriteBits(bits, 15, 4);
                remainder -= 15;
            }
            WriteBits(bits, remainder, 4);
        }

        private static void AppendStacLzsEndMarker(List<bool> bits)
        {
            bits.Add(true);
            bits.Add(true);
            WriteBits(bits, 0, 7);
        }

        private static byte[] PackMsbBits(List<bool> bits)
        {
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(0x80 >> (bit & 7));
            return encoded;
        }

        public static byte[] PackLsbCodes(params int[] codes)
        {
            var bits = new List<bool>();
            foreach (int code in codes)
                for (int bit = 0; bit < 9; bit++) bits.Add((code & (1 << bit)) != 0);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(1 << (bit & 7));
            return encoded;
        }

        public static byte[] PackLzwWidthTransition(ReadOnlySpan<byte> literals)
        {
            var bits = new List<bool>();
            for (int index = 0; index < literals.Length; index++)
            {
                int width = index < 257 ? 9 : 10;
                int code = literals[index];
                for (int bit = 0; bit < width; bit++) bits.Add((code & (1 << bit)) != 0);
            }
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(1 << (bit & 7));
            return encoded;
        }

        public static byte[] PackLzwClearAndRestart(byte first, byte afterClear)
        {
            var bits = new List<bool>();
            AppendCode(bits, first, 9);
            AppendCode(bits, 256, 9);
            while (bits.Count % (9 * 8) != 0) bits.Add(false);
            AppendCode(bits, afterClear, 9);
            byte[] encoded = new byte[(bits.Count + 7) / 8];
            for (int bit = 0; bit < bits.Count; bit++)
                if (bits[bit]) encoded[bit / 8] |= (byte)(1 << (bit & 7));
            return encoded;
        }

        private static void AppendCode(List<bool> bits, int code, int width)
        {
            for (int bit = 0; bit < width; bit++) bits.Add((code & (1 << bit)) != 0);
        }

        private static void WriteBits(List<bool> bits, int value, int count)
        {
            for (int bit = count - 1; bit >= 0; bit--) bits.Add((value & (1 << bit)) != 0);
        }

        private static byte[] NewRecord(string name, uint depth, int totalSize, ushort type = 0)
        {
            byte[] nameBytes = MacString.FromMacRoman(name).Bytes.ToArray();
            if (nameBytes.Length is 0 or > 31) throw new ArgumentOutOfRangeException(nameof(name));
            byte[] bytes = new byte[totalSize];
            "DDA2"u8.CopyTo(bytes);
            U16(bytes, 4, type);
            bytes[6] = checked((byte)nameBytes.Length);
            nameBytes.CopyTo(bytes, 7);
            U32(bytes, 38, depth);
            U32(bytes, 42, checked((uint)totalSize));
            return bytes;
        }

        private static void U16(Span<byte> bytes, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], value);

        private static void U32(Span<byte> bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(bytes[offset..], value);
    }
}
