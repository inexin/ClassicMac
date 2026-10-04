using System;
using System.Collections.Generic;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Graphics;

namespace ClassicMac.Graphics.QuickDraw;

// A Macintosh resource fork (Inside Macintosh: More Macintosh Toolbox, "Resource File Format"): a header with the
// data and map offsets and lengths, resource data as length-prefixed blocks, and a map with the type list, each
// type's reference list (id, name offset, attributes, 24-bit data offset) and the name list (Pascal strings).
internal static class ResourceFork
{
    public static IEnumerable<(string type, int id, string? name, byte[] data)> Read(byte[] fork)
    {
        if (fork.Length < 16)
        {
            throw new ArgumentException("Not a resource fork.", nameof(fork));
        }

        var s = fork.AsMemory();
        var reader = new BigEndianReader(s);
        int dataOffset = (int)reader.ReadUInt32();
        int mapOffset = (int)reader.ReadUInt32();
        if (dataOffset < 16 || mapOffset < 16 || mapOffset + 30 > fork.Length || dataOffset > fork.Length)
        {
            throw new ArgumentException("Not a resource fork.", nameof(fork));
        }

        var map = new BigEndianReader(s.Slice(mapOffset));
        int typeList = mapOffset + map.ReadUInt16At(24);
        int nameList = mapOffset + map.ReadUInt16At(26);
        if (typeList + 2 > fork.Length)
        {
            throw new ArgumentException("Not a resource fork.", nameof(fork));
        }

        int types = reader.ReadUInt16At(typeList) + 1;
        var result = new List<(string, int, string?, byte[])>();
        for (int t = 0; t < types && typeList + 2 + 8 * (t + 1) <= fork.Length; t++)
        {
            int entry = typeList + 2 + 8 * t;
            string type = Encoding.Latin1.GetString(fork, entry, 4);
            int count = reader.ReadUInt16At(entry + 4) + 1;
            int refs = typeList + reader.ReadUInt16At(entry + 6);
            for (int r = 0; r < count && refs + 12 * (r + 1) <= fork.Length; r++)
            {
                int re = refs + 12 * r;
                int id = reader.ReadInt16At(re);
                int nameOffset = reader.ReadUInt16At(re + 2);
                int dataAt = dataOffset + ((s.Span[re + 5] << 16) | (s.Span[re + 6] << 8) | s.Span[re + 7]);
                if (dataAt + 4 > fork.Length)
                {
                    continue;
                }

                int length = (int)reader.ReadUInt32At(dataAt);
                if (length < 0 || dataAt + 4 + length > fork.Length)
                {
                    continue;
                }

                string? name = null;
                if (nameOffset != 0xFFFF && nameList + nameOffset < fork.Length)
                {
                    int at = nameList + nameOffset;
                    int n = Math.Min(fork[at], fork.Length - at - 1);
                    name = Encoding.Latin1.GetString(fork, at + 1, n);
                }
                result.Add((type, id, name, s.Slice(dataAt + 4, length).ToArray()));
            }
        }
        return result;
    }
}
