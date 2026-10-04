using ClassicMac.Core;

namespace ClassicMac.Files;

/// <summary>
/// A volume's own dates, from its master directory block or volume header: HFS's <c>drCrDate</c>, <c>drLsMod</c>
/// and <c>drVolBkUp</c>; HFS Plus's <c>createDate</c>, <c>modifyDate</c> and <c>backupDate</c>; MFS's
/// <c>drCrDate</c> and <c>drLsBkUp</c> (MFS keeps no modification date). A zero date is null.
/// </summary>
/// <param name="Format">"HFS", "HFS Plus" or "MFS".</param>
/// <param name="Created">When the volume was created (initialised).</param>
/// <param name="Modified">When it was last modified; MFS has none.</param>
/// <param name="BackedUp">When it was last backed up.</param>
public sealed record VolumeInfo(string Format, MacDate? Created, MacDate? Modified, MacDate? BackedUp)
{
    /// <summary>
    /// Whether the dates after the creation date are in UTC: HFS Plus keeps <c>createDate</c> in local time and the
    /// others in UTC (TN1150); HFS and MFS keep all of them in local time.
    /// </summary>
    public bool UtcAfterCreation => Format == "HFS Plus";

    /// <summary>The volume's name (HFS's and MFS's <c>drVN</c>); null for HFS Plus, whose name is its root folder's.</summary>
    public string? Name { get; init; }

    /// <summary>The allocation block size in bytes (<c>drAlBlkSiz</c>, <c>blockSize</c>).</summary>
    public long BlockSize { get; init; }

    /// <summary>The number of allocation blocks (<c>drNmAlBlks</c>, <c>totalBlocks</c>).</summary>
    public long TotalBlocks { get; init; }

    /// <summary>The free allocation blocks the volume records (<c>drFreeBks</c>, <c>freeBlocks</c>).</summary>
    public long FreeBlocks { get; init; }

    /// <summary>The allocation area's size in bytes.</summary>
    public long TotalBytes => TotalBlocks * BlockSize;

    /// <summary>The free space in bytes, as the Finder shows it.</summary>
    public long FreeBytes => FreeBlocks * BlockSize;

    /// <summary>The files on the volume (HFS's <c>drFilCnt</c>, HFS Plus's <c>fileCount</c>, MFS's <c>drNmFls</c>).</summary>
    public long? Files { get; init; }

    /// <summary>The folders on the volume, the root not counted (<c>drDirCnt</c>, <c>folderCount</c>); null for MFS.</summary>
    public long? Folders { get; init; }

    /// <summary>Whether the volume is locked by software (attribute bit 15, <c>kHFSVolumeSoftwareLockBit</c>).</summary>
    public bool SoftwareLocked { get; init; }

    /// <summary>Whether the volume was locked by hardware when last mounted (attribute bit 7, <c>kHFSVolumeHardwareLockBit</c>).</summary>
    public bool HardwareLocked { get; init; }

    /// <summary>
    /// The blessed System Folder's ID (HFS's <c>drFndrInfo[0]</c>, HFS Plus's <c>finderInfo[0]</c>); null when none is
    /// blessed, and for MFS.
    /// </summary>
    public uint? BlessedFolderId { get; init; }
}

/// <summary>A container reader for a volume format, which can also give the volume's own dates.</summary>
public interface IVolumeReader
{
    /// <summary>The volume's dates, or null when <paramref name="input"/> is not a volume this reader can read.</summary>
    VolumeInfo? ReadVolumeInfo(ForkData input);
}
