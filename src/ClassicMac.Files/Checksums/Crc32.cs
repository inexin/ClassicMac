using System;

namespace ClassicMac.Files.Checksums;

/// <summary>
/// The CRC-32 of zip and gzip (ISO 3309: reflected polynomial $EDB88320, initial and final XOR all ones); Compact Pro and
/// StuffIt's method 15 use it too.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> bytes) => ~Update(uint.MaxValue, bytes);

    /// <summary>The register after more bytes, without the final XOR (Compact Pro stores it so).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc = Update(crc, b);
        }

        return crc;
    }

    public static uint Update(uint crc, byte value) => Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

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
