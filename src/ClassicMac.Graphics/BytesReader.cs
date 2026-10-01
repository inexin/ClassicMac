using System;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Graphics
{
    // Stream readers over bytes already in memory, for the parsers that read only from streams.
    internal static class BytesReader
    {
        public static BigEndianStreamReader Over(byte[] data, int offset = 0) =>
            new(new MemoryStream(data, offset, data.Length - offset, writable: false));

        public static BigEndianStreamReader Over(ReadOnlySpan<byte> data) => Over(data.ToArray());
    }
}
