using System;
using System.Collections.Generic;

namespace ClassicMac.Files.Hfs;

// One of the volume's B-trees as First Aid sees it: the file's bytes from its extents, its node count from its PEOF (not
// the header's), and what the walk computes, which the header record and node map are compared with. An HFS Plus tree
// has 16-bit key lengths and its own key orders.
internal sealed class FirstAidTree(int fileId, byte[] bytes, int nodeSize, List<(uint Start, uint Count)> extents, bool plus = false)
{
    /// <summary>An HFS Plus tree: the extents (3), catalog (4) or attributes (8) file's.</summary>
    public bool Plus { get; } = plus;

    public bool IsAttributes => FileId == 8;

    /// <summary>The tree file's extents (the alternate MDB's, and for the catalog its overflow extents).</summary>
    public List<(uint Start, uint Count)> Extents { get; } = extents;

    /// <summary>3 for the extents overflow file, 4 for the catalog.</summary>
    public int FileId { get; } = fileId;

    public bool IsCatalog => FileId == 4;

    public BTreeFile File { get; } = new(bytes, nodeSize, wordKeyLength: plus);

    public int NodeSize { get; } = nodeSize;

    /// <summary>The PEOF in nodes, Disk First Aid's totalNodes.</summary>
    public uint TotalNodes { get; } = (uint)(bytes.Length / nodeSize);

    /// <summary>The fixed maximum key length: HFS 7 and 37 (Disk First Aid's), HFS Plus 10, 516 and 266 (TN1150).</summary>
    public int MaxKeyLength => Plus ? FileId switch { 4 => 516, 8 => 266, _ => 10 } : IsCatalog ? 37 : 7;

    /// <summary>The tree's key order.</summary>
    public int Compare(byte[] left, byte[] right) => (Plus, FileId) switch
    {
        (false, 4) => HfsCatalogKeys.CompareCatalogKeys(left, right),
        (false, _) => HfsBTreeWriting.CompareExtentsKeys(left, right),
        (true, 4) => HfsPlusBTree.CompareCatalogKeys(left, right, caseFolding: true),
        (true, 8) => HfsPlusAttributes.CompareAttributeKeys(left, right),
        _ => HfsPlusBTree.CompareExtentKeys(left, right),
    };

    /// <summary>A key's length, without its length field.</summary>
    public int KeyLength(ReadOnlySpan<byte> key) => Plus ? key[0] << 8 | key[1] : key[0];

    /// <summary>The nodes the walk reached (header, index, leaf and map nodes).</summary>
    public HashSet<uint> Reached { get; } = [];

    public int Depth { get; set; }

    public uint LeafRecords { get; set; }

    public uint FirstLeaf { get; set; }

    public uint LastLeaf { get; set; }

    /// <summary>The leaf records in key order, as the walk found them: key (with its length byte), data, and node.</summary>
    public List<(byte[] Key, byte[] Data, uint Node)> Records { get; } = [];
}
