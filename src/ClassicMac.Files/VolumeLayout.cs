using System.Collections.Generic;
using System.Linq;

namespace ClassicMac.Files;

/// <summary>A run of allocation blocks: the first and how many.</summary>
public readonly record struct BlockRange(long Start, long Count)
{
    /// <summary>The block after the last.</summary>
    public long End => Start + Count;
}

/// <summary>
/// Where a volume's free space and files lie (docs/formats/file-systems/hfs.md §5.7), in allocation blocks: whether a
/// defragmentation is worth it, how small a resize can make the volume, and what an allocation map draws.
/// </summary>
/// <param name="BlockCount">The allocation blocks (<c>drNmAlBlks</c>).</param>
/// <param name="BlockSize">Their size in bytes (<c>drAlBlkSiz</c>).</param>
/// <param name="Files">The files on the volume.</param>
/// <param name="SplitFiles">The files with a fork in more than one extent.</param>
/// <param name="SplitForks">The forks in more than one extent.</param>
/// <param name="MostExtents">The most extents one fork has (0 when no fork has blocks).</param>
/// <param name="FreeRuns">The runs of free blocks, in block order.</param>
/// <param name="SplitExtents">The extents of the forks in more than one extent, in block order.</param>
/// <param name="SmallestSize">The smallest size in bytes a resize shrinks the volume to as it lies now.</param>
/// <param name="SmallestSizeDefragmented">The smallest size once it is defragmented: room for its blocks in use.</param>
public sealed record VolumeLayout(long BlockCount, long BlockSize, int Files, int SplitFiles, int SplitForks, int MostExtents,
    IReadOnlyList<BlockRange> FreeRuns, IReadOnlyList<BlockRange> SplitExtents, long SmallestSize, long SmallestSizeDefragmented)
{
    /// <summary>The free blocks.</summary>
    public long FreeBlocks => FreeRuns.Sum(r => r.Count);

    /// <summary>The longest run of free blocks.</summary>
    public long LargestFreeRun => FreeRuns.Count == 0 ? 0 : FreeRuns.Max(r => r.Count);

    /// <summary>Whether a defragmentation would change anything: a split fork, or the free space in more than one run.</summary>
    public bool CanDefragment => SplitForks > 0 || FreeRuns.Count > 1;
}
