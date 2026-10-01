using System;
using System.IO;

namespace ClassicMac.Graphics
{
    internal static class PictSpanReaderExtensions
    {
        public static short ReadI16BE(this ref ClassicMac.Core.BigEndianReader reader) => reader.ReadInt16();
        public static ushort ReadU16BE(this ref ClassicMac.Core.BigEndianReader reader) => reader.ReadUInt16();
        public static int ReadI32BE(this ref ClassicMac.Core.BigEndianReader reader) => reader.ReadInt32();
        public static uint ReadU32BE(this ref ClassicMac.Core.BigEndianReader reader) => reader.ReadUInt32();

        public static PictRect ReadRectBE(this ref ClassicMac.Core.BigEndianReader reader)
        {
            var rect = reader.ReadMacRect();
            return new PictRect(rect.Top, rect.Left, rect.Bottom, rect.Right);
        }

        public static void Skip(this ref ClassicMac.Core.BigEndianReader reader, long count)
        {
            if (count < 0 || count > int.MaxValue) throw new EndOfStreamException();
            reader.Skip((int)count);
        }

        public static byte[] ReadExactly(this ref ClassicMac.Core.BigEndianReader reader, int count) => reader.ReadBytes(count).ToArray();
    }
}
