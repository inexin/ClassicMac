using System;

namespace ClassicMac.Files.Checksums;

// CRC-16 with polynomial $1021, initial value 0, no reflection or final XOR (CRC-16/XMODEM, "CCITT"), which MacBinary
// II, BinHex 4.0 and PackIt use.
internal static class Crc16Xmodem
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Compute(ReadOnlySpan<byte> bytes, ushort crc = 0)
    {
        foreach (var b in bytes)
        {
            crc = (ushort)(crc << 8 ^ Table[(crc >> 8 ^ b) & 0xFF]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (ushort)(i << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1);
            }

            table[i] = crc;
        }
        return table;
    }
}
