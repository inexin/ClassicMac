using System;
using System.Collections.Generic;

namespace ClassicMac.Code.Disassembly;

/// <summary>A low-memory global: a system variable at a fixed address.</summary>
/// <param name="Address">Its address.</param>
/// <param name="Name">Its name (<c>CurrentA5</c>).</param>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Type">Its type as Multiversal Interfaces declares it (<c>Ptr</c>, <c>INTEGER</c>,
/// <c>Byte[8]</c>).</param>
public readonly record struct LowMemoryGlobal(uint Address, string Name, int Size, string Type);

/// <summary>
/// The low-memory globals [Doc: Inside Macintosh, the global-variable summaries of each manager], from Multiversal
/// Interfaces (tools/TrapTables), for naming absolute addresses in disassembly.
/// </summary>
public static partial class LowMemoryGlobals
{
    // In a nested class so that the generated table is initialized first.
    private static class Index
    {
        public static readonly LowMemoryGlobal[] All = Build();
    }

    /// <summary>Every global, in address order.</summary>
    public static IReadOnlyList<LowMemoryGlobal> All => Index.All;

    /// <summary>The global that starts at <paramref name="address"/>.</summary>
    /// <param name="address">The address.</param>
    /// <param name="global">The global.</param>
    /// <returns>True when a global starts there.</returns>
    public static bool TryGet(uint address, out LowMemoryGlobal global)
    {
        int i = Array.BinarySearch(Index.All, new LowMemoryGlobal(address, "", 0, ""), ByAddress.Instance);
        global = i >= 0 ? Index.All[i] : default;
        return i >= 0;
    }

    /// <summary>The global that contains <paramref name="address"/>, and the offset into it.</summary>
    /// <param name="address">The address.</param>
    /// <param name="global">The global.</param>
    /// <param name="offset">The address's offset from the global's start.</param>
    /// <returns>True when a global covers the address.</returns>
    public static bool TryFind(uint address, out LowMemoryGlobal global, out int offset)
    {
        int i = Array.BinarySearch(Index.All, new LowMemoryGlobal(address, "", 0, ""), ByAddress.Instance);
        if (i < 0)
            i = ~i - 1;
        if (i >= 0 && address - Index.All[i].Address < (uint)Index.All[i].Size)
        {
            global = Index.All[i];
            offset = (int)(address - global.Address);
            return true;
        }
        global = default;
        offset = 0;
        return false;
    }

    private static LowMemoryGlobal[] Build()
    {
        var all = new LowMemoryGlobal[Table.Length];
        for (int i = 0; i < all.Length; i++)
            all[i] = new LowMemoryGlobal(Table[i].Address, Table[i].Name, Table[i].Size, Table[i].Type);
        return all;
    }

    private sealed class ByAddress : IComparer<LowMemoryGlobal>
    {
        public static readonly ByAddress Instance = new();
        public int Compare(LowMemoryGlobal x, LowMemoryGlobal y) => x.Address.CompareTo(y.Address);
    }
}
