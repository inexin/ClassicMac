using ClassicMac.Core;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// The volume Disk First Aid was run on live (the spec's base, 2026-10-04): a 1 MB volume ClassicMac formats, with
// folders D1 and D1:D2 and files T1 (5,000 bytes), D1:w6plain (both forks) and D1:D2:T2. 2,042 blocks of 512 bytes,
// the highest CNID 20, drNxtCNID 21. Each fault test changes one field of a copy.
internal static class FirstAidImages
{
    public const int Sector = 512;

    public static byte[] Base()
    {
        var image = HfsWriter.Format(1024 * 1024, "First Aid");
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "D1");
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "D1:D2");
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "T1", new byte[5000], ReadOnlyMemory<byte>.Empty, FinderInfo.Empty);
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "D1:w6plain", new byte[700], new byte[300], FinderInfo.Empty);
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "D1:D2:T2", new byte[10], ReadOnlyMemory<byte>.Empty, FinderInfo.Empty);
        return image;
    }

    /// <summary>The offset of the alternate MDB: the volume's second-to-last sector.</summary>
    public static int Alternate(byte[] image) => image.Length - 2 * Sector;

    /// <summary>The offset of the primary MDB.</summary>
    public const int Primary = 1024;

    public static ushort U16(byte[] image, int offset) => new BigEndianReader(image).ReadUInt16At(offset);

    public static uint U32(byte[] image, int offset) => new BigEndianReader(image).ReadUInt32At(offset);

    public static void Put16(byte[] image, int offset, int value) => new BigEndianWriter(image).WriteUInt16At(offset, (ushort)value);

    public static void Put32(byte[] image, int offset, long value) => new BigEndianWriter(image).WriteUInt32At(offset, (uint)value);

    public static FirstAidReport Verify(byte[] image) => HfsFirstAid.Verify(ForkData.FromBytes(image));
}
