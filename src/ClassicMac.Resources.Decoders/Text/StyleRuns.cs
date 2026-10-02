using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders.Text
{
    /// <summary>One style run of a <c>'styl'</c> resource (TextEdit's <c>ScrpSTElement</c>).</summary>
    /// <param name="Start">The first character (byte offset into the text) the style applies to.</param>
    /// <param name="Height">Line height.</param>
    /// <param name="Ascent">Font ascent.</param>
    /// <param name="Font">Font family ID.</param>
    /// <param name="Face">QuickDraw style bits: bold 1, italic 2, underline 4, outline 8, shadow $10, condense $20, extend $40.</param>
    /// <param name="Size">Point size (0: the default).</param>
    /// <param name="Red">Red, 0–65535.</param>
    /// <param name="Green">Green, 0–65535.</param>
    /// <param name="Blue">Blue, 0–65535.</param>
    internal sealed record StyleRun(int Start, short Height, short Ascent, short Font, byte Face, short Size, ushort Red, ushort Green, ushort Blue);

    /// <summary>
    /// <c>'styl'</c>: TextEdit's style scrap (<i>Inside Macintosh: Text</i>, <c>StScrpRec</c>): a count, then 20-byte
    /// elements — start (long), height, ascent, font, face (byte + filler), size, and an <c>RGBColor</c>.
    /// </summary>
    internal static class StyleRuns
    {
        private const int ElementLength = 20;

        public static List<StyleRun> Read(ReadOnlyMemory<byte> data, out bool complete)
        {
            var runs = new List<StyleRun>();
            complete = data.Length >= 2;
            if (!complete)
            {
                return runs;
            }

            var reader = new BigEndianReader(data);
            var count = reader.ReadUInt16();
            for (var i = 0; i < count; i++)
            {
                if (reader.Remaining < ElementLength)
                {
                    complete = false;
                    break;
                }
                int start = reader.ReadInt32();
                short height = reader.ReadInt16(), ascent = reader.ReadInt16(), font = reader.ReadInt16();
                byte face = reader.ReadByte();
                reader.Skip(1); // filler
                short size = reader.ReadInt16();
                ushort red = reader.ReadUInt16(), green = reader.ReadUInt16(), blue = reader.ReadUInt16();
                runs.Add(new StyleRun(start, height, ascent, font, face, size, red, green, blue));
            }
            return runs;
        }

        // The runs as a style scrap: a count, then 20-byte elements.
        public static byte[] Write(IReadOnlyList<StyleRun> runs)
        {
            var writer = new BigEndianWriter(2 + runs.Count * ElementLength);
            writer.WriteUInt16(runs.Count);
            for (var i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                writer.WriteInt32(r.Start);
                writer.WriteInt16(r.Height);
                writer.WriteInt16(r.Ascent);
                writer.WriteInt16(r.Font);
                writer.WriteByte(r.Face);
                writer.WriteZeros(1);
                writer.WriteInt16(r.Size);
                writer.WriteUInt16(r.Red);
                writer.WriteUInt16(r.Green);
                writer.WriteUInt16(r.Blue);
            }
            return writer.ToArray();
        }

        // The standard font family numbers of Inside Macintosh: Text ("Font Family Numbers"); others are the system's
        // own and have no fixed name.
        public static string FontName(short id) => id switch
        {
            0 => "Chicago",
            1 => "Geneva", // applFont: the application font, Geneva on US systems
            2 => "New York",
            3 => "Geneva",
            4 => "Monaco",
            5 => "Venice",
            6 => "London",
            7 => "Athens",
            8 => "San Francisco",
            9 => "Toronto",
            11 => "Cairo",
            12 => "Los Angeles",
            13 => "Zapf Dingbats", // 13–34: the LaserWriter fonts' standard numbers
            14 => "Bookman",
            15 => "Helvetica Narrow",
            16 => "Palatino",
            18 => "Zapf Chancery",
            20 => "Times",
            21 => "Helvetica",
            22 => "Courier",
            23 => "Symbol",
            24 => "Mobile",
            33 => "Avant Garde",
            34 => "New Century Schoolbook",
            _ => $"Font {id}",
        };
    }
}
