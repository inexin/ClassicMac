using System.Collections.Generic;

namespace ClassicMac.Files.Hfs;

// One of the volume's B-trees as First Aid sees it: the file's bytes from the alternate MDB's extents, its node count
// from its PEOF (not the header's), and what the walk computes, which the header record and node map are compared with.
internal sealed class FirstAidTree(int fileId, byte[] bytes, int nodeSize, List<(uint Start, uint Count)> extents)
{
    /// <summary>The tree file's extents (the alternate MDB's, and for the catalog its overflow extents).</summary>
    public List<(uint Start, uint Count)> Extents { get; } = extents;

    /// <summary>3 for the extents overflow file, 4 for the catalog.</summary>
    public int FileId { get; } = fileId;

    public bool IsCatalog => FileId == 4;

    public BTreeFile File { get; } = new(bytes, nodeSize, wordKeyLength: false);

    public int NodeSize { get; } = nodeSize;

    /// <summary>The PEOF in nodes, Disk First Aid's totalNodes.</summary>
    public uint TotalNodes { get; } = (uint)(bytes.Length / nodeSize);

    /// <summary>Disk First Aid's fixed maximum key length: 7 for the extents tree, 37 for the catalog.</summary>
    public int MaxKeyLength => IsCatalog ? 37 : 7;

    /// <summary>The nodes the walk reached (header, index, leaf and map nodes).</summary>
    public HashSet<uint> Reached { get; } = [];

    public int Depth { get; set; }

    public uint LeafRecords { get; set; }

    public uint FirstLeaf { get; set; }

    public uint LastLeaf { get; set; }

    /// <summary>The leaf records in key order, as the walk found them: key (with its length byte), data, and node.</summary>
    public List<(byte[] Key, byte[] Data, uint Node)> Records { get; } = [];
}
