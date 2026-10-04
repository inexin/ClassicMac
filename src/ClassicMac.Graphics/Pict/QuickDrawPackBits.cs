using System;
using System.Collections.Generic;

namespace ClassicMac.Graphics.Pict;

// _PackBits as QuickDraw packs a recorded picture's rows (docs/formats/codecs/packbits.md §4.2): a run of 3 to 128 equal
// bytes as (1 − count, byte), anything else in literals of up to 128 (count − 1, bytes). Mac OS 9's NQDPackBits ends a
// literal where a run of 3 begins (Core's PackBits.Pack packs the same). The ROM's ($A8CF) differs twice: its look-ahead
// reads up to 2 bytes past the row (in StdBits, the next row), so a row ending in "b b" before another b packs as the
// run FF b; and a run beginning at a literal's 128th byte goes into the literal.
internal static class QuickDrawPackBits
{
    public static byte[] Pack(ReadOnlySpan<byte> row, ReadOnlySpan<byte> following, bool rom)
    {
        if (!rom)
        {
            return ClassicMac.Core.PackBits.Pack(row, 1);
        }

        int n = row.Length;
        // The byte at i, the ROM's look-ahead reading on past the row.
        int At(ReadOnlySpan<byte> r, ReadOnlySpan<byte> f, int i) => i < n ? r[i] : i - n < f.Length ? f[i - n] : -1;
        var output = new List<byte>(n + n / 64 + 2);
        int at = 0;
        while (at < n)
        {
            // A run starts where three bytes are equal, the third possibly past the row; it takes only the row's bytes.
            if (At(row, following, at + 1) == row[at] && At(row, following, at + 2) == row[at] && at + 1 < n)
            {
                int run = 2;
                while (at + run < n && run < 128 && row[at + run] == row[at])
                {
                    run++;
                }

                output.Add((byte)(1 - run));
                output.Add(row[at]);
                at += run;
                continue;
            }

            int start = at++;
            while (at < n && at - start < 128
                && !(at - start < 127 && at + 1 < n && At(row, following, at + 1) == row[at] && At(row, following, at + 2) == row[at]))
            {
                at++;
            }

            output.Add((byte)(at - start - 1));
            output.AddRange(row[start..at]);
        }

        return [.. output];
    }
}
