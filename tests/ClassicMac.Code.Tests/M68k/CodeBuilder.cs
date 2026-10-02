using ClassicMac.Code.M68k;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Code.Tests.M68k;

// Builds 68k application pieces field by field: CODE 0, jump-table entries, near and far segment headers, and forks.
internal static class CodeBuilder
{
    public static readonly FourCC Code = FourCC.FromString("CODE");

    public static byte[] NearEntry(ushort offset, short segment) => Words(offset, 0x3F3C, (ushort)segment, 0xA9F0);

    public static byte[] FarEntry(short segment, uint offset) => Words((ushort)segment, 0xA9F0, (ushort)(offset >> 16), (ushort)offset);

    public static byte[] LoadedEntry(short segment, uint address) => Words((ushort)segment, 0x4EF9, (ushort)(address >> 16), (ushort)address);

    public static byte[] FarMarker => Words(0, 0xFFFF, 0, 0);

    public static byte[] Words(params ushort[] words)
    {
        var w = new BigEndianWriter();
        foreach (var word in words) w.WriteUInt16(word);
        return w.ToArray();
    }

    // CODE 0: aboveA5, belowA5, jump-table size, jump-table offset, then the entries.
    public static byte[] Code0(byte[][] entries, uint? above = null, uint below = 0x100, uint? jtSize = null, uint jtOffset = 0x20)
    {
        var w = new BigEndianWriter();
        uint size = jtSize ?? (uint)(8 * entries.Length);
        w.WriteUInt32(above ?? jtOffset + size);
        w.WriteUInt32(below);
        w.WriteUInt32(size);
        w.WriteUInt32(jtOffset);
        foreach (var e in entries) w.WriteBytes(e);
        return w.ToArray();
    }

    public static byte[] Near(ushort firstOffset, ushort count, params byte[] code)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(firstOffset);
        w.WriteUInt16(count);
        w.WriteBytes(code);
        return w.ToArray();
    }

    // +$18, +$20 and +$24 get distinct nonzero values by default, so a reader that swaps or skips them is seen.
    public static byte[] Far(uint firstNear, uint nearCount, uint firstFar, uint farCount, byte[] code, uint a5Relocations = 0,
        uint pcRelocations = 0, uint a5AtLast = 0x11111111, uint addressAtLast = 0x22222222, uint reserved = 0x33333333)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(0xFFFF);
        w.WriteUInt16(0);
        w.WriteUInt32(firstNear);
        w.WriteUInt32(nearCount);
        w.WriteUInt32(firstFar);
        w.WriteUInt32(farCount);
        w.WriteUInt32(a5Relocations);
        w.WriteUInt32(a5AtLast);
        w.WriteUInt32(pcRelocations);
        w.WriteUInt32(addressAtLast);
        w.WriteUInt32(reserved);
        w.WriteBytes(code);
        return w.ToArray();
    }

    public static Resource Add(this ResourceFork fork, string type, short id, byte[] data, string? name = null,
        ResourceAttributes attributes = ResourceAttributes.None)
    {
        var resource = new Resource(FourCC.FromString(type), id, data) { Attributes = attributes };
        if (name is not null) resource.Name = MacString.FromMacRoman(name);
        fork.Add(resource);
        return resource;
    }

    public static byte[] Zeros(int n) => new byte[n];
}
