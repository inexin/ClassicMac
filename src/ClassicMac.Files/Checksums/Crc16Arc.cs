using System;

namespace ClassicMac.Files.Checksums;

// CRC-16/ARC (the "IBM" CRC: reflected polynomial $A001, initial value 0, no final XOR), which StuffIt and LHA use.
internal static class Crc16Arc
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> bytes, ushort crc = 0)
    {
        foreach (var b in bytes)
        {
            crc = Update(crc, b);
        }

        return crc;
    }

    public static ushort Update(ushort crc, byte value) => (ushort)((crc >> 8) ^ Table[(crc ^ value) & 0xFF]);

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }

            table[i] = crc;
        }
        return table;
    }
}
