using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The check after a fork is replaced (hfs.md §5.5): the target's catalog record may differ from the source's only in the
// changed fork's lengths and extents and the modification date, and its other fork reads as it did.
public sealed class ForkVerifyTests
{
    private static (HfsVolume Source, HfsVolume Result, HfsCatalogEditing.CatalogEditState After, (byte[] Key, byte[] Data) Before,
        List<(byte[] Key, byte[] Data)> Overflow) Replaced()
    {
        var builder = new HfsBuilder();
        builder.File(HfsBuilder.Root, "Letter", "dear sir"u8.ToArray(), new byte[300]);
        var source = new HfsVolume(ForkData.FromBytes(builder.Build("Disk")));
        var before = HfsCatalogEditing.OpenCatalog(source, writable: false);
        var record = before.Records.Single(r => r.Data.Length >= 102 && r.Data[0] == 2);
        var result = HfsWriter.ReplaceFork(source, "Letter", HfsFork.Data, "new text"u8.ToArray());
        var after = HfsCatalogEditing.OpenCatalog(result, writable: false);
        return (source, result, after, (record.Key, record.Data.ToArray()), []);
    }

    private static void Verify(HfsVolume source, HfsVolume result, HfsCatalogEditing.CatalogEditState after, (byte[] Key, byte[] Data) before,
        List<(byte[] Key, byte[] Data)> overflow, DateTime written)
    {
        var mdb = new byte[162];
        source.Read(1024, mdb);
        var reader = new BigEndianReader(mdb);
        HfsForkWriting.Verify(source, result, after, before, overflow, ((uint)reader.ReadUInt16At(0x1C) * 512, reader.ReadUInt32At(0x14),
            reader.ReadUInt16At(0x12)), "Letter", HfsFork.Data, "new text"u8, written, 0, 0);
    }

    private static DateTime Written(HfsCatalogEditing.CatalogEditState after) =>
        new MacDate(new BigEndianReader(after.Records.Single(r => r.Data.Length >= 102 && r.Data[0] == 2).Data).ReadUInt32At(0x30)).ToDateTime();

    [Fact]
    public void A_replaced_fork_verifies()
    {
        var (source, result, after, before, overflow) = Replaced();

        Verify(source, result, after, before, overflow, Written(after));
    }

    [Fact]
    public void A_record_changed_beyond_the_fork_is_refused()
    {
        var (source, result, after, before, overflow) = Replaced();
        before.Data[4] ^= 0x01;                                                    // the Finder type, as if the edit changed it

        var error = Assert.Throws<InvalidDataException>(() => Verify(source, result, after, before, overflow, Written(after)));

        Assert.Contains("metadata", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_other_fork_is_refused()
    {
        var (source, result, after, before, overflow) = Replaced();
        var record = new BigEndianReader(after.Records.Single(r => r.Data.Length >= 102 && r.Data[0] == 2).Data);
        long resource = after.FirstBlock + (long)record.ReadUInt16At(0x56) * after.BlockSize;
        result.Write(resource, [0x55]);                                            // a byte of the resource fork, which was not edited

        var error = Assert.Throws<InvalidDataException>(() => Verify(source, result, after, before, overflow, Written(after)));

        Assert.Contains("both forks", error.Message, StringComparison.Ordinal);
    }
}
