using ClassicMac.Core;
using ClassicMac.Files.Archives;

namespace ClassicMac.Files.Tests;

// Member layouts only original StuffIt archives show: DropStuff 7.0.3 folders (TestData/StuffIt703/README.md), and
// StuffIt Deluxe 7.0's return receipt, StuffIt 7.0 for Windows and StuffIt Deluxe 4.5's encryption from the CC0
// corpus (TestData/StuffItOriginalCrossVersion/README.md).
public sealed class StuffItOriginalLayoutTests
{
    private static readonly string StuffIt703 = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffIt703");
    private static readonly string Forks = Path.Combine(AppContext.BaseDirectory, "TestData", "StuffIt151", "Forks");
    private static readonly string CrossVersion =
        Path.Combine(AppContext.BaseDirectory, "TestData", "StuffItOriginalCrossVersion");

    [Theory]
    [InlineData("S703b.sit")] // Better Compression: methods 15 and 0
    [InlineData("S703f.sit")] // Faster Compression: methods 13 and 0
    public void DropStuff703ArchiveExpandsItsFoldersWithForksAndFinderInfo(string archiveName)
    {
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(
            ForkData.FromBytes(File.ReadAllBytes(Path.Combine(StuffIt703, archiveName))),
            new ContainerContext(diagnostics: diagnostics));

        string root = Path.GetFileNameWithoutExtension(archiveName);
        Assert.Equal([$"{root}:Big.txt", $"{root}:Empty", $"{root}:Folder:Inner", $"{root}:ReadMe"],
            files.Select(file => file.MacPath).Order(StringComparer.Ordinal));
        AssertFile(files, $"{root}:Big.txt", "Big.txt.data", null);
        AssertFile(files, $"{root}:Empty", "Empty.data", null);
        AssertFile(files, $"{root}:Folder:Inner", "Folder__Inner.data", null);
        AssertFile(files, $"{root}:ReadMe", "ReadMe.data", "ReadMe.rsrc");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void DropStuff703FirstFolderMemberFollowsTheFolderEndMarker()
    {
        byte[] archive = File.ReadAllBytes(Path.Combine(StuffIt703, "S703b.sit"));
        var reader = new BigEndianReader(archive);
        // The root folder at $72 is followed by its end marker ($CB: no name, first child $FFFFFFFF), its first
        // member comes after that, and its last member (ReadMe, $54A) links back to the marker.
        Assert.Equal(0xFBu, reader.ReadUInt32At(0x72 + 34));
        Assert.Equal(0xFFFFFFFFu, reader.ReadUInt32At(0xCB + 34));
        Assert.Equal(0xCBu, reader.ReadUInt32At(0x54A + 22));

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(archive), new ContainerContext());

        Assert.Equal(4, files.Count);
    }

    [Fact]
    public void StuffItXArchiveIsNotTakenForAStuffItArchive()
    {
        byte[] archive = File.ReadAllBytes(Path.Combine(StuffIt703, "S703x.sitx"));
        var diagnostics = new List<Diagnostic>();

        ContainerNode result = ContainerUnwrapper.Default.Unwrap(Path.Combine(StuffIt703, "S703x.sitx"),
            diagnostics: diagnostics);

        Assert.False(StuffItReader.Instance.CanRead(ForkData.FromBytes(archive)));
        Assert.Empty(result.Children);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ReturnReceiptPrependedByStuffItDeluxe70IsTheFirstRootMember()
    {
        byte[] archive = File.ReadAllBytes(Path.Combine(CrossVersion, "testfile.stuffit7_dlx.mac9.rreceipt.sit"));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(["StuffItReturnReceipt.txt", "Test Image", "Test Text", "testfile.jpg", "testfile.PICT",
            "testfile.png", "testfile.txt"], files.Select(file => file.MacPath));
        MacFile receipt = files[0];
        Assert.Equal(0x41, receipt.DataFork.Length);
        Assert.Equal(FourCC.FromString("TEXT"), receipt.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), receipt.FinderInfo.Creator);
        Assert.Equal(File.ReadAllBytes(Path.Combine(CrossVersion, "ExpectedTestFile.jpg")),
            files[3].DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(CrossVersion, "ExpectedTestFile.txt")),
            files[6].DataFork.ToArray());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void StuffIt70ForWindowsVersion3MembersHaveA32ByteFinderBlock()
    {
        byte[] archive = File.ReadAllBytes(Path.Combine(CrossVersion, "testfile.stuffit7.win.sit"));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Equal(["sources:testfile.jpg", "sources:testfile.png", "sources:testfile.txt"],
            files.Select(file => file.MacPath));
        Assert.Equal(File.ReadAllBytes(Path.Combine(CrossVersion, "ExpectedTestFile.jpg")), files[0].DataFork.ToArray());
        Assert.Equal(File.ReadAllBytes(Path.Combine(CrossVersion, "ExpectedTestFile.png")), files[1].DataFork.ToArray());
        Assert.Equal("Testing 123\n"u8.ToArray(), files[2].DataFork.ToArray()); // the Windows source ends in LF
        Assert.All(files, file => Assert.Equal(0, file.ResourceFork.Length));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void StuffItDeluxe45EncryptedEntriesAreReportedNotDecoded()
    {
        byte[] archive = File.ReadAllBytes(Path.Combine(CrossVersion, "testfile.stuffit45_dlx.mac9.password.sit"));
        var diagnostics = new List<Diagnostic>();

        IReadOnlyList<MacFile> files = StuffItReader.Instance.Read(ForkData.FromBytes(archive),
            new ContainerContext(diagnostics: diagnostics));

        Assert.Empty(files);
        Assert.Equal(6, diagnostics.Count);
        Assert.All(diagnostics, diagnostic => Assert.Equal("archive.encrypted", diagnostic.Code));
    }

    private static void AssertFile(IReadOnlyList<MacFile> files, string macPath, string dataName, string? resourceName)
    {
        MacFile file = Assert.Single(files, file => file.MacPath == macPath);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Forks, dataName)), file.DataFork.ToArray());
        Assert.Equal(resourceName is null ? [] : File.ReadAllBytes(Path.Combine(Forks, resourceName)),
            file.ResourceFork.ToArray());
        Assert.Equal(FourCC.FromString("TEXT"), file.FinderInfo.Type);
        Assert.Equal(FourCC.FromString("ttxt"), file.FinderInfo.Creator);
        Assert.Equal((FinderFlags)0, file.FinderInfo.Flags);
    }
}
