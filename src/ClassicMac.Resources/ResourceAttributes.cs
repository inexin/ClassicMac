using System;

namespace ClassicMac.Resources;

/// <summary>
/// A resource's attribute byte (<c>rAttr</c>), from <i>Inside Macintosh: More Macintosh Toolbox</i>, the Resource
/// Manager chapter; bit 0 from the disassembly.
/// </summary>
[Flags]
public enum ResourceAttributes : byte
{
    /// <summary>No attributes.</summary>
    None = 0,
    /// <summary>
    /// The resource is compressed (System 7 onward): <c>resExtended</c> in Apple's private equates, later public as
    /// <c>resCompressed</c>. CheckLoad decompresses it (ROM $077D $FFC7A186; Mac OS 9 <c>RM_CheckLoad</c>).
    /// </summary>
    Compressed = 0x01,
    /// <summary>The resource has been changed and must be written (meaningful in memory only).</summary>
    Changed = 0x02,
    /// <summary>The resource is loaded when the file is opened.</summary>
    Preload = 0x04,
    /// <summary>The resource cannot be changed or removed.</summary>
    Protected = 0x08,
    /// <summary>The resource's handle is locked.</summary>
    Locked = 0x10,
    /// <summary>The resource's handle is purgeable.</summary>
    Purgeable = 0x20,
    /// <summary>The resource is loaded into the system heap.</summary>
    SystemHeap = 0x40,
    /// <summary>Reserved (<c>resSysRef</c> in Apple's headers).</summary>
    SystemReference = 0x80,
}

/// <summary>
/// A resource map's attribute byte, <c>mAttr</c> at map offset 22 (the Resource Manager chapter; layout confirmed
/// in the ROM $077D and Mac OS 9 Resource Managers).
/// </summary>
[Flags]
public enum ResourceForkAttributes : byte
{
    /// <summary>No attributes.</summary>
    None = 0,
    /// <summary>The map's handle goes in the system heap (<c>mapForceSysHeap</c>, private).</summary>
    ForceSystemHeap = 0x01,
    /// <summary>The map has changed and must be written (<c>mapChanged</c>).</summary>
    Changed = 0x20,
    /// <summary>The file must be compacted when it is written (<c>mapCompact</c>).</summary>
    Compact = 0x40,
    /// <summary>The file is read-only (<c>mapReadOnly</c>).</summary>
    ReadOnly = 0x80,
}

/// <summary>
/// The byte after the map attributes, <c>mInMemoryAttr</c> at map offset 23: flags the Resource Manager keeps with an
/// open map. From disk the ROM's NewMap keeps only bits 0, 6 and 7 (<c>andi #$C1</c>, $FFC7938A); the byte is kept
/// here as read so it round-trips.
/// </summary>
[Flags]
public enum ResourceMapFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,
    /// <summary>
    /// <c>decompressionPasswordBit</c>: the 68k ROM looks for <c>'dcmp'</c> resources only in maps with this bit.
    /// Mac OS 9 ignores it.
    /// </summary>
    DecompressionPassword = 0x01,
    /// <summary><c>overrideNextMapBit</c>.</summary>
    OverrideNextMap = 0x02,
    /// <summary><c>dontCountOrIndexDuplicatesBit</c>.</summary>
    DontCountOrIndexDuplicates = 0x04,
    /// <summary><c>twoDeepBit</c>.</summary>
    TwoDeep = 0x08,
    /// <summary><c>preventFileFromBeingClosedBit</c>.</summary>
    PreventFileFromBeingClosed = 0x10,
}
