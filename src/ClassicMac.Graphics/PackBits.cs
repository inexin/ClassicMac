using System;
using ClassicMac.Core;

namespace ClassicMac.Graphics
{
    internal static class PackBits
    {
        // Standard PackBits (flag n >= 0: copy n + 1 bytes; n < 0: repeat the next byte 1 - n times; -128: no-op),
        // reading at most maxInput bytes. Returns the count read.
        public static long Unpack(BigEndianStreamReader src, Span<byte> dst, long maxInput = long.MaxValue)
        {
            long start = src.Position, end = maxInput == long.MaxValue ? long.MaxValue : start + maxInput;
            int op = 0;
            while (op < dst.Length && More(src, end))
            {
                sbyte flag = (sbyte)src.ReadByte();
                if (flag == -128) continue;
                if (flag < 0)
                {
                    if (!More(src, end)) break;
                    byte b = src.ReadByte();
                    for (int i = 0; i < 1 - flag && op < dst.Length; i++) dst[op++] = b;
                }
                else
                    for (int i = 0; i <= flag && op < dst.Length && More(src, end); i++) dst[op++] = src.ReadByte();
            }
            return src.Position - start;
        }

        private static bool More(BigEndianStreamReader src, long end) =>
            src.Position < end && !src.IsAtEnd;
    }
}
