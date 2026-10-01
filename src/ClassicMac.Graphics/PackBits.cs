using System;
using ClassicMac.Core;

namespace ClassicMac.Graphics
{
    internal static class PackBits
    {
        // Standard PackBits (flag n >= 0: copy n + 1 bytes; n < 0: repeat the next byte 1 - n times; -128: no-op).
        public static int Unpack(ref BigEndianReader src, Span<byte> dst)
        {
            int start = src.Position;
            int op = 0;
            while (src.Remaining > 0 && op < dst.Length)
            {
                sbyte flag = (sbyte)src.ReadByte();
                if (flag == -128) continue;
                if (flag < 0)
                {
                    if (src.Remaining == 0) break;
                    byte b = src.ReadByte();
                    for (int i = 0; i < 1 - flag && op < dst.Length; i++) dst[op++] = b;
                }
                else
                    for (int i = 0; i <= flag && src.Remaining > 0 && op < dst.Length; i++) dst[op++] = src.ReadByte();
            }
            return src.Position - start;
        }
    }
}
