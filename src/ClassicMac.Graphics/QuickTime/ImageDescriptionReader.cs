using System;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Graphics;

namespace ClassicMac.Graphics.QuickTime
{
    // QuickTime image descriptions, shared by the PICT opcode and QTIF files.
    internal static class ImageDescriptionReader
    {
        // An ImageDescription at p (86 bytes: idSize, cType, reserved, version, revision, vendor, temporal and spatial
        // quality, width, height, hRes, vRes, dataSize, frameCount, name[32], depth, clutID), with the color table that
        // follows it when clutID is 0 (else the standard table for the id or depth). Null if it does not fit.
        public static PictImageDescription? Read(ReadOnlySpan<byte> block, int p, out int idSize)
        {
            idSize = 0;
            if (p < 0 || p + 86 > block.Length) return null;
            int idStart = p;
            var reader = new ClassicMac.Core.BigEndianReader(block) { Position = p };
            idSize = reader.ReadInt32();
            string codec = Encoding.Latin1.GetString(reader.ReadBytes(4));
            reader.Skip(8 + 2 + 2 + 4 + 4 + 4);                       // reserved, version, revision, vendor, qualities
            int width = (ushort)reader.ReadInt16(), height = (ushort)reader.ReadInt16();
            double hRes = reader.ReadInt32() / 65536.0, vRes = reader.ReadInt32() / 65536.0;
            reader.ReadInt32();                                      // dataSize
            reader.ReadInt16();                                      // frameCount
            var nameBytes = reader.ReadBytes(32);
            int nameLength = Math.Min(nameBytes[0], (byte)31);
            string name = Encoding.Latin1.GetString(nameBytes.Slice(1, nameLength));
            int depth = reader.ReadInt16(), clutId = reader.ReadInt16();
            RgbaColor[]? table = StandardColorTables.ForId(clutId);
            if (clutId == 0 && idSize > 86 && reader.Position + 8 <= block.Length)
                table = ReadColorTable(block, reader.Position, idStart + idSize);
            table ??= StandardColorTables.ForDepth(depth);
            return new PictImageDescription(codec, width, height, depth, clutId, hRes, vRes, name) { ColorTable = table };
        }

        // A ColorTable stored after the image description: ctSeed, ctFlags, ctSize, then (value, r, g, b) entries.
        private static RgbaColor[]? ReadColorTable(ReadOnlySpan<byte> block, int p, int end)
        {
            var reader = new ClassicMac.Core.BigEndianReader(block);
            int size = reader.ReadUInt16At(p + 6) + 1;
            if (size <= 0 || size > 256) return null;
            var table = new RgbaColor[size];
            reader.Position = p + 8;
            for (int i = 0; i < size && reader.Position + 8 <= Math.Min(end, block.Length); i++)
            {
                reader.ReadUInt16();
                byte red = reader.ReadByte(); reader.ReadByte();
                byte green = reader.ReadByte(); reader.ReadByte();
                byte blue = reader.ReadByte(); reader.ReadByte();
                table[i] = new RgbaColor(red, green, blue);
            }
            return table;
        }
    }
}
