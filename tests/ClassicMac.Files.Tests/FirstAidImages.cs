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

    /// <summary>Disk First Aid's own problems, without ClassicMac's.</summary>
    public static List<FirstAidProblem> DiskFirstAid(FirstAidReport report) => [.. report.Problems.Where(p => p.Origin == FirstAidOrigin.DiskFirstAid)];

    // The catalog's node n, by the MDB's first catalog extent (the test volumes' catalogs are in one piece).
    public static int CatalogNode(byte[] image, uint node)
    {
        int blockSize = (int)U32(image, Primary + 0x14);
        int firstBlock = U16(image, Primary + 0x1C) * Sector;
        int nodeSize = U16(image, firstBlock + U16(image, Primary + 0x96) * blockSize + 14 + 18);
        return firstBlock + U16(image, Primary + 0x96) * blockSize + (int)node * nodeSize;
    }

    /// <summary>The offset of the data of the catalog record with key (parent, name), following the leaves.</summary>
    public static int Record(byte[] image, uint parent, string name)
    {
        int header = CatalogNode(image, 0);
        int nodeSize = U16(image, header + 14 + 18);
        var macName = Core.MacRoman.Encode(name);
        for (uint node = U32(image, header + 14 + 10); node != 0; node = U32(image, CatalogNode(image, node)))
        {
            int at = CatalogNode(image, node);
            for (var i = 0; i < U16(image, at + 10); i++)
            {
                int start = at + U16(image, at + nodeSize - 2 * (i + 1));
                int keyLength = image[start];
                if (U32(image, start + 2) == parent && image.AsSpan(start + 7, image[start + 6]).SequenceEqual(macName))
                {
                    return start + ((1 + keyLength + 1) & ~1);
                }
            }
        }

        throw new InvalidOperationException($"No catalog record ({parent}, {name}).");
    }

    /// <summary>Removes the catalog record (parent, name) from its leaf, as if it had been lost; the header's record count follows.</summary>
    public static void RemoveRecord(byte[] image, uint parent, string name)
    {
        int header = CatalogNode(image, 0);
        int nodeSize = U16(image, header + 14 + 18);
        int data = Record(image, parent, name);
        int node = header + (data - header) / nodeSize * nodeSize;
        int count = U16(image, node + 10);
        int index = Enumerable.Range(0, count).Last(i => node + U16(image, node + nodeSize - 2 * (i + 1)) < data);
        int start = U16(image, node + nodeSize - 2 * (index + 1)), end = U16(image, node + nodeSize - 2 * (index + 2));
        int freeStart = U16(image, node + nodeSize - 2 * (count + 1));
        image.AsSpan(node + end, freeStart - end).CopyTo(image.AsSpan(node + start));
        for (int i = index + 1; i <= count; i++)
        {
            Put16(image, node + nodeSize - 2 * i, U16(image, node + nodeSize - 2 * (i + 1)) - (end - start));
        }

        Put16(image, node + 10, count - 1);
        Put32(image, header + 14 + 6, U32(image, header + 14 + 6) - 1);
    }

    /// <summary>The offsets of the extents tree's leaf records' keys, in key order (the tree in one piece).</summary>
    public static List<int> ExtentsKeys(byte[] image)
    {
        int blockSize = (int)U32(image, Primary + 0x14);
        int tree = U16(image, Primary + 0x1C) * Sector + U16(image, Primary + 0x86) * blockSize;
        int nodeSize = U16(image, tree + 14 + 18);
        var keys = new List<int>();
        for (uint node = U32(image, tree + 14 + 10); node != 0; node = U32(image, tree + (int)node * nodeSize))
        {
            int at = tree + (int)node * nodeSize;
            for (var i = 0; i < U16(image, at + 10); i++)
            {
                keys.Add(at + U16(image, at + nodeSize - 2 * (i + 1)));
            }
        }

        return keys;
    }

    public const uint D1 = 16, D2 = 17, T1 = 18, W6Plain = 19, T2 = 20;
}
