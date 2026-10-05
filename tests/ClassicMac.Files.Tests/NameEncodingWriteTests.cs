using ClassicMac.Core;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// Names written in another encoding (docs/formats/codecs/text-encodings.md §5): with ContainerReadOptions.NameEncoding, an
// HFS volume's names are read and written in it: paths name items by their characters, new names are stored as that
// encoding's bytes.
public sealed class NameEncodingWriteTests : IDisposable
{
    private static readonly byte[] Nihongo = [0x93, 0xFA, 0x96, 0x7B];                      // 日本 in Mac OS Japanese
    private static readonly ContainerReadOptions Japanese = ContainerReadOptions.Default with { NameEncoding = MacTextEncoding.Japanese };
    private readonly string folder = Directory.CreateTempSubdirectory("cm-names").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string Disk()
    {
        var disk = HfsWriter.Format(800 * 1024, "Disk");
        disk = HfsWriter.CreateFolder(ForkData.FromBytes(disk), MacRoman.Decode(Nihongo));             // stored as those bytes
        disk = HfsWriter.CreateFile(ForkData.FromBytes(disk), MacRoman.Decode(Nihongo) + ":Letter", "hi"u8.ToArray(), ReadOnlyMemory<byte>.Empty, FinderInfo.Empty);
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk);
        return path;
    }

    private static IReadOnlyList<MacFile> Files(byte[] volume) => HfsReader.Instance.Read(ForkData.FromBytes(volume), new ContainerContext());

    private static IReadOnlyList<MacFolder> Folders(byte[] volume) => HfsReader.Instance.ReadFolders(ForkData.FromBytes(volume), new ContainerContext());

    [Fact]
    public void New_names_are_stored_in_the_encoding()
    {
        var session = InputEditSession.Open(Disk(), Japanese);

        session.AddFolder("漢字");
        session.AddFile("日本:手紙", new MacFile { Name = MacString.FromMacRoman("x"), DataFork = ForkData.FromBytes("hi"u8.ToArray()) });

        var volume = session.Volume;
        Assert.Contains(Folders(volume), f => f.Name.Bytes.SequenceEqual(MacEncodings.Encode("漢字", MacTextEncoding.Japanese)));
        var letter = Files(volume).Single(f => f.Name.Bytes.SequenceEqual(MacEncodings.Encode("手紙", MacTextEncoding.Japanese)));
        Assert.Equal(Nihongo, letter.FolderPath.Single().Bytes.ToArray());
    }

    [Fact]
    public void Items_are_named_by_their_characters_to_rename_move_and_delete()
    {
        var session = InputEditSession.Open(Disk(), Japanese);

        session.Rename("日本:Letter", "手紙");
        session.AddFolder("中");
        session.Move("日本:手紙", "中");
        session.Rename("日本", "空");
        session.Delete("空");

        var volume = session.Volume;
        Assert.Equal(MacEncodings.Encode("中", MacTextEncoding.Japanese), Files(volume).Single().FolderPath.Single().Bytes.ToArray());
        Assert.DoesNotContain(Folders(volume), f => f.Name.Bytes.SequenceEqual(Nihongo));
    }

    // A name the encoding cannot hold is refused; read as Mac OS Roman (the default) names stay as before.
    [Fact]
    public void A_name_the_encoding_cannot_hold_is_refused_and_Roman_is_unchanged()
    {
        var session = InputEditSession.Open(Disk(), ContainerReadOptions.Default with { NameEncoding = MacTextEncoding.Greek });
        Assert.Throws<ArgumentException>(() => session.AddFolder("日本語"));

        var roman = InputEditSession.Open(Disk());
        roman.AddFolder("Café");
        Assert.Contains(Folders(roman.Volume), f => f.Name.Bytes.SequenceEqual(MacRoman.Encode("Café")));
        roman.Rename(MacRoman.Decode(Nihongo), "Docs");                                   // the Roman reading of the bytes
        Assert.Contains(Folders(roman.Volume), f => f.Name.ToMacRoman() == "Docs");
    }
}
