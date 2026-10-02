using System;

namespace ClassicMac.Files.Archives;

/// <summary>The CRC-32 of zip and gzip (ISO 3309: reflected polynomial $EDB88320, initial and final XOR all ones).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var b in bytes)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }
        return table;
    }
}
