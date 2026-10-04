using System;

namespace ClassicMac.Code.M68k;

/// <summary>The form of a jump-table entry.</summary>
public enum JumpTableEntryKind
{
    /// <summary>Near, as stored: <c>offset.w | MOVE.W #seg,-(SP) | _LoadSeg</c>; the offset is from the code after the 4-byte header.</summary>
    NearUnloaded,
    /// <summary>Near, loaded (a running application's form): <c>seg.w | JMP addr.l</c>.</summary>
    NearLoaded,
    /// <summary>Entry 1 of a far ("32-bit everything") table: <c>0000 FFFF 0000 0000</c>.</summary>
    FarMarker,
    /// <summary>Far, as stored: <c>seg.w | _LoadSeg | offset.l</c>; the offset is from the resource start.</summary>
    FarUnloaded,
    /// <summary>Far, loaded: <c>seg.w | JMP addr.l</c>.</summary>
    FarLoaded,
    /// <summary>None of the forms.</summary>
    Unrecognized,
}

/// <summary>
/// One 8-byte jump-table entry. Calls go to the entry plus 2 (<c>JSR n(A5)</c> with n = <see cref="A5Offset"/> + 2)
/// [Doc: Inside Macintosh II, the Segment Loader; Mac OS Runtime Architectures, the far model].
/// </summary>
/// <param name="Index">The entry's index (entry 0 is the application's entry point).</param>
/// <param name="A5Offset">Where the entry is, from A5: the jump-table offset plus 8 × <paramref name="Index"/>.</param>
/// <param name="Kind">The entry's form.</param>
/// <param name="Segment">The segment (the <c>'CODE'</c> ID); 0 for the marker and unrecognized entries.</param>
/// <param name="Offset">For an unloaded entry, the routine's offset (from the code for near, from the resource for far).</param>
/// <param name="Address">For a loaded entry, the routine's address.</param>
/// <param name="Raw">The entry's 8 bytes, big-endian.</param>
public sealed record JumpTableEntry(int Index, int A5Offset, JumpTableEntryKind Kind, short Segment, uint Offset, uint Address, ulong Raw)
{
    /// <summary>The routine's offset in its <c>'CODE'</c> resource (the near header's 4 bytes added), for an unloaded entry; otherwise null.</summary>
    public long? ResourceOffset => Kind switch
    {
        JumpTableEntryKind.NearUnloaded => Offset + 4L,
        JumpTableEntryKind.FarUnloaded => Offset,
        _ => null,
    };
}
