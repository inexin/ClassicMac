using System;
using System.Buffers.Binary;
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

        public static List<StyleRun> Read(ReadOnlySpan<byte> data, out bool complete)
        {
            var runs = new List<StyleRun>();
            complete = data.Length >= 2;
            if (!complete) return runs;
            var count = BinaryPrimitives.ReadUInt16BigEndian(data);
            for (var i = 0; i < count; i++)
            {
                var at = 2 + i * ElementLength;
                if (at + ElementLength > data.Length)
                {
                    complete = false;
                    break;
                }
                var e = data.Slice(at, ElementLength);
                runs.Add(new StyleRun(
                    BinaryPrimitives.ReadInt32BigEndian(e), BinaryPrimitives.ReadInt16BigEndian(e[4..]),
                    BinaryPrimitives.ReadInt16BigEndian(e[6..]), BinaryPrimitives.ReadInt16BigEndian(e[8..]), e[10],
                    BinaryPrimitives.ReadInt16BigEndian(e[12..]), BinaryPrimitives.ReadUInt16BigEndian(e[14..]),
                    BinaryPrimitives.ReadUInt16BigEndian(e[16..]), BinaryPrimitives.ReadUInt16BigEndian(e[18..])));
            }
            return runs;
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
