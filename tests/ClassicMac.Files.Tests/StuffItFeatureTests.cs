using System.Buffers.Binary;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

public sealed class StuffItFeatureTests
{
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
        byte[] image = StuffItFixture.BuildFile("Compressed", "encoded"u8.ToArray(), [], dataMethod: 2);
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(image),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "archive.compression-unsupported");
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

    private static class StuffItFixture
    {
        private const int ArchiveHeaderLength = 100;
        private const uint CreateSeconds = 2_500_000_000;
        private const uint ModifySeconds = 2_600_000_000;

        public static byte[] BuildFile(string name, byte[] data, byte[] resource, bool encrypted = false,
            byte dataMethod = 0, byte[]? encodedData = null, byte resourceMethod = 0, byte[]? encodedResource = null)
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
            U16(member, 42, Crc16Arc(data));
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
                U16(image.AsSpan(forksAt), 8, Crc16Arc(resource));
                image[forksAt + 12] = resourceMethod;
                encodedResource.CopyTo(image.AsSpan(forksAt + resourceInfoLength, encodedResource.Length));
            }
            encodedData.CopyTo(image, dataOffset);
            U32(image, 84, checked((uint)image.Length));
            return image;
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

        private static ushort HeaderCrc(ReadOnlySpan<byte> header)
        {
            byte[] copy = header.ToArray();
            copy[32] = copy[33] = 0;
            return Crc16Arc(copy);
        }

        private static ushort Crc16Arc(ReadOnlySpan<byte> bytes)
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
