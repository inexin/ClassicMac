using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Tests;

// The container writers: what each writes, the matching reader reads back.
public class ContainerWriterTests
{
    // A file with both forks, Finder info (flags in both bytes, location, folder, script and extended flags) and dates;
    // the data fork holds runs and $90 bytes for BinHex's run-length encoding.
    private static MacFile Sample()
    {
        byte[] extended = [0, 0, 0, 0, 0, 0, 0, 0, 1, 0x40, 0, 0, 0, 0, 0, 0];
        return new MacFile
        {
            Name = MacString.FromMacRoman("Read Me ƒ"),
            FinderInfo = FinderInfo.Empty with
            {
                Type = FourCC.FromString("TEXT"),
                Creator = FourCC.FromString("ttxt"),
                Flags = (FinderFlags)0x2101,
                Location = new MacPoint(40, 60),
                Folder = 3,
                Extended = extended,
            },
            Created = new MacDate(3_000_000_000),
            Modified = new MacDate(3_000_000_600),
            DataFork = ForkData.FromBytes((byte[])[.. "Hello"u8, .. Enumerable.Repeat((byte)0x90, 5), .. Enumerable.Repeat((byte)'z', 300), 0x90, 0]),
            ResourceFork = ForkData.FromBytes(Enumerable.Range(0, 200).Select(i => (byte)i).ToArray()),
        };
    }

    private static void AssertSame(MacFile expected, MacFile actual, bool dates, bool extended)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal((expected.FinderInfo.Type, expected.FinderInfo.Creator, expected.FinderInfo.Flags),
            (actual.FinderInfo.Type, actual.FinderInfo.Creator, actual.FinderInfo.Flags));
        Assert.Equal(expected.DataFork.ToArray(), actual.DataFork.ToArray());
        Assert.Equal(expected.ResourceFork.ToArray(), actual.ResourceFork.ToArray());
        if (dates) Assert.Equal((expected.Created, expected.Modified), (actual.Created, actual.Modified));
        if (extended)
        {
            Assert.Equal((expected.FinderInfo.Location, expected.FinderInfo.Folder), (actual.FinderInfo.Location, actual.FinderInfo.Folder));
            Assert.Equal(expected.FinderInfo.Extended.Span[8..10].ToArray(), actual.FinderInfo.Extended.Span[8..10].ToArray());
        }
    }

    [Fact]
    public void MacBinary_III_reads_back()
    {
        var file = Sample();
        var bytes = MacBinaryWriter.ToArray(file);

        Assert.Equal(128 + 384 + 256, bytes.Length); // both forks padded to 128
        Assert.True(MacBinaryReader.III.CanRead(ForkData.FromBytes(bytes)));
        AssertSame(file, MacBinaryReader.III.Read(ForkData.FromBytes(bytes), new ContainerContext())[0], dates: true, extended: true);
    }

    [Fact]
    public void BinHex_reads_back()
    {
        var file = Sample();
        var text = BinHexWriter.ToText(file);

        Assert.StartsWith("(This file must be converted with BinHex 4.0)\r:", text, StringComparison.Ordinal);
        Assert.All(text.Split('\r')[1..^2], line => Assert.Equal(64, line.Length));
        var context = new ContainerContext();
        AssertSame(file, BinHexReader.Instance.Read(ForkData.FromBytes(System.Text.Encoding.ASCII.GetBytes(text)), context)[0], dates: false, extended: false);
        Assert.Empty(context.Diagnostics);
    }

    [Fact]
    public void BinHex_run_length_encoding_marks_literal_90_bytes_and_runs_of_three_or_more()
    {
        Assert.Equal([0x41, 0x41, 0x90, 0x00, 0x42, 0x90, 0x05], BinHexWriter.RunLength([0x41, 0x41, 0x90, 0x42, 0x42, 0x42, 0x42, 0x42]));
        Assert.Equal([0x90, 0x00, 0x90, 0x03], BinHexWriter.RunLength([0x90, 0x90, 0x90]));
    }

    [Fact]
    public void AppleSingle_reads_back()
    {
        var file = Sample();
        var bytes = new MemoryStream();
        AppleDoubleWriter.WriteAppleSingle(file, bytes);

        AssertSame(file, AppleSingleReader.AppleSingle.Read(ForkData.FromBytes(bytes.ToArray()), new ContainerContext())[0], dates: true, extended: true);
    }
}
