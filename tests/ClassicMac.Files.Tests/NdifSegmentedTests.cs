using ClassicMac.Core;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace ClassicMac.Files.Tests;

// Editing a segmented NDIF image (docs/formats/disk-images/ndif.md §3.4): its parts joined, the image made again around
// the changed disk and cut again into as many parts, each keeping its name, Finder info and the image ID.
public sealed class NdifSegmentedTests : IDisposable
{
    private static readonly FourCC Bcem = FourCC.FromString("bcem"), BcmCount = FourCC.FromString("bcm#");
    private readonly string directory = Directory.CreateTempSubdirectory("cm-ndif-seg").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static byte[] Volume()
    {
        var volume = HfsWriter.Format(400 * 1024, "Seg Disk");
        var noise = new byte[40_000];
        new Random(2).NextBytes(noise);
        return HfsWriter.CreateFile(ForkData.FromBytes(volume), "Noise", noise, ReadOnlyMemory<byte>.Empty, FinderInfo.Empty);
    }

    private static IReadOnlyList<MacFile> Parts(byte[] volume, NdifFormat format = NdifFormat.ReadOnly) =>
        NdifWriter.Split(NdifWriter.Create(volume, "Seg.img", NdifCreateOptions.Default with { Format = format }), 3, "Seg");

    private static byte[] Record(MacFile part) => ResourceFork.Read(part.ResourceFork.ToArray()).Find(BcmCount, 128)!.GetData().ToArray();

    private static byte[] Decoded(IReadOnlyList<MacFile> parts)
    {
        var diagnostics = new List<Diagnostic>();
        var context = new ContainerContext(ContainerReadOptions.Default with { VerifyChecksums = true }, diagnostics, siblings: () => parts.Skip(1));
        var disk = Assert.Single(NdifReader.Instance.Read(parts[0], context)).DataFork.ToArray();
        Assert.Empty(diagnostics);
        return disk;
    }

    [Theory]
    [InlineData(NdifFormat.ReadOnly)]
    [InlineData(NdifFormat.Adc)]
    public void A_segmented_image_is_made_again_in_as_many_parts(NdifFormat format)
    {
        var volume = Volume();
        var parts = Parts(volume, format);
        var changed = HfsWriter.CreateFolder(ForkData.FromBytes(volume), "Docs");

        var written = NdifWriter.RewriteSegmented([parts[2], parts[0], parts[1]], changed);              // in any order

        Assert.Equal(parts.Select(p => p.Name), written.Select(p => p.Name));
        Assert.Equal(parts.Select(p => p.FinderInfo), written.Select(p => p.FinderInfo));
        Assert.All(written, p => Assert.Equal(Record(parts[0])[4..20], Record(p)[4..20]));                // the same image ID
        Assert.Equal([1, 2, 3], written.Select(p => (int)Record(p)[1]));
        Assert.All(written, p => Assert.Equal(NdifReader.Crc(p.DataFork), new BigEndianReader(Record(p)).ReadUInt32At(20)));
        var map = new BigEndianReader(ResourceFork.Read(written[0].ResourceFork.ToArray()).Find(Bcem, 128)!.GetData());
        Assert.Equal((12, 1u), (map.ReadUInt16At(0), map.ReadUInt32At(0x54)));
        Assert.Equal(changed, Decoded(written));
    }

    // An edit that shrinks the image below a sector per part: the data is padded with zeros to equal parts, which read
    // back as the disk (chunks are found by offset).
    [Fact]
    public void An_image_grown_smaller_than_its_parts_is_padded()
    {
        var volume = Volume();
        var image = NdifWriter.Create(volume, "Seg.img", NdifCreateOptions.Default with { ChunkSectors = 4 });
        var count = (int)((image.DataFork.Length + 511) / 512);                             // a sector each
        var parts = NdifWriter.Split(image, count, "Seg");
        var emptied = HfsWriter.ReplaceFork(ForkData.FromBytes(volume), "Noise", HfsFork.Data, new byte[40_000]);  // compresses to little

        var written = NdifWriter.RewriteSegmented(parts, emptied);

        Assert.Equal(count, written.Count);
        Assert.True(written.Sum(p => p.DataFork.Length) > NdifWriter.Rewrite(
            NdifWriter.Create(volume, "Seg.img", NdifCreateOptions.Default with { ChunkSectors = 4 }), emptied).DataFork.Length);
        Assert.Single(written.Select(p => p.DataFork.Length).Distinct());
        Assert.Equal(emptied, Decoded(written));
    }

    [Fact]
    public void Parts_that_do_not_make_one_image_are_refused()
    {
        var parts = Parts(Volume());
        var other = Parts(Volume());
        var disk = Decoded(parts);

        Assert.Throws<InvalidDataException>(() => NdifWriter.RewriteSegmented([parts[0], parts[1]], disk));               // one missing
        Assert.Throws<InvalidDataException>(() => NdifWriter.RewriteSegmented([parts[0], parts[1], other[2]], disk));     // another image's
        Assert.Throws<InvalidDataException>(() => NdifWriter.RewriteSegmented([parts[1], parts[2], parts[1]], disk));     // no part 1
    }

    private string WriteParts(IReadOnlyList<MacFile> parts)
    {
        foreach (var part in parts)
        {
            HostFiles.Write(part, directory, new HostWriteOptions { Layout = HostLayout.AppleDouble });
        }

        return Path.Combine(directory, "Seg 1of3");
    }

    [Fact]
    public void The_session_edits_the_disk_and_saves_every_part_as_new_files()
    {
        var volume = Volume();
        var first = WriteParts(Parts(volume));
        var session = InputEditSession.Open(first);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);
        Assert.Equal(volume, session.Volume);

        session.AddFolder("Docs");
        var target = Path.Combine(directory, "out", "Edited 1of3");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var written = session.SaveAs(target);

        foreach (var name in new[] { "Edited 1of3", "Edited 2of3", "Edited 3of3" })
        {
            Assert.Contains(Path.Combine(directory, "out", name), written);
            Assert.Contains(Path.Combine(directory, "out", "._" + name), written);
        }

        var reopened = InputEditSession.Open(target);
        Assert.Contains(HfsReader.Instance.ReadFolders(ForkData.FromBytes(reopened.Volume), new ContainerContext()), f => f.MacPath == "Docs");
        Assert.Equal("dseg", HostFiles.Read(Path.Combine(directory, "out", "Edited 3of3")).File.FinderInfo.Type.ToString());
    }

    // Save in place writes every part over itself, each original kept as .orig the first time.
    [Fact]
    public void The_session_saves_every_part_in_place()
    {
        var volume = Volume();
        var first = WriteParts(Parts(volume));
        var originals = Enumerable.Range(1, 3).Select(n => File.ReadAllBytes(Path.Combine(directory, $"Seg {n}of3"))).ToList();
        var session = InputEditSession.Open(first);

        session.Delete("Noise");
        session.SaveInPlace();

        for (var n = 1; n <= 3; n++)
        {
            Assert.Equal(originals[n - 1], File.ReadAllBytes(Path.Combine(directory, $"Seg {n}of3.orig")));
        }

        var reopened = InputEditSession.Open(first);
        Assert.Empty(HfsReader.Instance.Read(ForkData.FromBytes(reopened.Volume), new ContainerContext()));
        Assert.Equal(Record(HostFiles.Read(first).File)[4..20], Record(HostFiles.Read(Path.Combine(directory, "Seg 3of3")).File)[4..20]);
    }

    // With a part missing the image cannot be made again: it is read only.
    [Fact]
    public void An_image_missing_a_part_is_read_only()
    {
        var parts = Parts(Volume());
        var first = WriteParts([parts[0], parts[1]]);

        Assert.Equal(InputEditKind.ReadOnly, InputEditSession.Open(first).Kind);
    }
    // Disk Copy 6.3.3's own four-part image (the harness's run14/out/seg), copied as a Basilisk II folder: edited and saved
    // in place, every part rewritten, and read back with the folder added.
    [Fact]
    public void Disk_Copy_s_segmented_image_is_edited_in_place()
    {
        var source = !ClassicMac.Tests.CorpusFolders.Any ? null
            : ClassicMac.Tests.CorpusFolders.EnumerateFiles("S5M RO seg 1of4", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).FirstOrDefault(d => !Path.GetFileName(d!).StartsWith('.'));
        if (source is null)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the harness's run14/out images to run this.");
        }

        foreach (var name in Enumerable.Range(1, 4).Select(n => $"S5M RO seg {n}of4"))
        {
            foreach (var sub in new[] { "", ".rsrc", ".finf" })
            {
                Directory.CreateDirectory(Path.Combine(directory, sub));
                File.Copy(Path.Combine(source, sub, name), Path.Combine(directory, sub, name));
            }
        }

        var first = Path.Combine(directory, "S5M RO seg 1of4");
        var session = InputEditSession.Open(first);
        Assert.Equal(InputEditKind.HfsVolume, session.Kind);
        session.AddFolder("Edited Here");
        session.SaveInPlace();

        var reopened = InputEditSession.Open(first);
        Assert.Contains(HfsReader.Instance.ReadFolders(ForkData.FromBytes(reopened.Volume), new ContainerContext()), f => f.MacPath == "Edited Here");
        Assert.All(Enumerable.Range(1, 4), n => Assert.True(File.Exists(Path.Combine(directory, $"S5M RO seg {n}of4.orig"))));
    }
}
