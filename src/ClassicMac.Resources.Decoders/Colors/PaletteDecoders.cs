using System;
using System.Collections.Generic;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Colors
{
    /// <summary>A palette entry: its index, its colour (0–65535 per component), and for a <c>'pltt'</c> its usage and tolerance.</summary>
    /// <param name="Index">The pixel value it stands for.</param>
    /// <param name="Value">The stored value: a colour table's value field, or a palette's position.</param>
    /// <param name="Red">Red.</param>
    /// <param name="Green">Green.</param>
    /// <param name="Blue">Blue.</param>
    /// <param name="Usage">A palette entry's usage flags; 0 for a colour table.</param>
    /// <param name="Tolerance">A palette entry's tolerance; 0 for a colour table.</param>
    public readonly record struct PaletteEntry(int Index, int Value, ushort Red, ushort Green, ushort Blue, ushort Usage = 0, ushort Tolerance = 0);

    /// <summary>Reads colour tables (<c>'clut'</c>) and palettes (<c>'pltt'</c>).</summary>
    public static class Palettes
    {
        /// <summary>
        /// A <c>'clut'</c> (a <c>ColorTable</c>): seed, flags, count less one, then value and <c>RGBColor</c> per entry. With
        /// flag bit 15 (a device's table) the entries are in pixel-value order and the value field is not an index.
        /// </summary>
        public static IReadOnlyList<PaletteEntry> ReadColorTable(ReadOnlyMemory<byte> data, out int seed, out ushort flags, out bool complete)
        {
            seed = 0;
            flags = 0;
            complete = data.Length >= 8;
            var entries = new List<PaletteEntry>();
            if (!complete) return entries;
            var reader = new BigEndianReader(data);
            seed = reader.ReadInt32();
            flags = reader.ReadUInt16();
            var count = reader.ReadInt16() + 1;
            var device = (flags & 0x8000) != 0;
            for (var i = 0; i < count; i++)
            {
                var e = 8 + i * 8;
                if (e + 8 > data.Length)
                {
                    complete = false;
                    break;
                }
                var value = reader.ReadInt16At(e);
                entries.Add(new PaletteEntry(device ? i : value, value, reader.ReadUInt16At(e + 2),
                    reader.ReadUInt16At(e + 4), reader.ReadUInt16At(e + 6)));
            }
            return entries;
        }

        /// <summary>
        /// A <c>'pltt'</c> (Palette Manager): a count and 14 reserved bytes, then 16 bytes per entry: <c>RGBColor</c>, usage,
        /// tolerance and 6 private bytes.
        /// </summary>
        public static IReadOnlyList<PaletteEntry> ReadPalette(ReadOnlyMemory<byte> data, out bool complete)
        {
            complete = data.Length >= 16;
            var entries = new List<PaletteEntry>();
            if (!complete) return entries;
            var reader = new BigEndianReader(data);
            var count = reader.ReadInt16();
            for (var i = 0; i < count; i++)
            {
                var e = 16 + i * 16;
                if (e + 16 > data.Length)
                {
                    complete = false;
                    break;
                }
                entries.Add(new PaletteEntry(i, i, reader.ReadUInt16At(e), reader.ReadUInt16At(e + 2),
                    reader.ReadUInt16At(e + 4), reader.ReadUInt16At(e + 6), reader.ReadUInt16At(e + 8)));
            }
            return entries;
        }

        /// <summary>
        /// An Adobe colour table (<c>.act</c>): 256 RGB triplets (8 bits each; unused ones black), then the number of colours
        /// and the transparent index ($FFFF, none), as big-endian words. Each entry goes to its index; entries with an index
        /// outside 0–255 are left out.
        /// </summary>
        public static byte[] Act(IReadOnlyList<PaletteEntry> entries)
        {
            var act = new byte[772];
            var highest = -1;
            foreach (var e in entries)
            {
                if (e.Index is < 0 or > 255) continue;
                act[e.Index * 3] = (byte)(e.Red >> 8);
                act[e.Index * 3 + 1] = (byte)(e.Green >> 8);
                act[e.Index * 3 + 2] = (byte)(e.Blue >> 8);
                highest = Math.Max(highest, e.Index);
            }
            var writer = new BigEndianWriter(act);
            writer.WriteUInt16At(768, (ushort)(highest + 1));
            writer.WriteUInt16At(770, 0xFFFF);
            return act;
        }
    }

    /// <summary>Colour tables (<c>'clut'</c>) and palettes (<c>'pltt'</c>) as JSON (the exact colours), and as an Adobe <c>.act</c> palette.</summary>
    internal sealed class PaletteDecoder(string name, string type) : IResourceDecoder, IBuiltInDecoder
    {
        private readonly FourCC handled = FourCC.FromString(type);

        public string Name => name;

        public int Version => 1;

        public bool CanDecode(FourCC t) => t == handled;

        public IReadOnlyCollection<FourCC> Types => [handled];

        public static IEnumerable<IResourceDecoder> All() => [new PaletteDecoder("color.table", "clut"), new PaletteDecoder("color.palette", "pltt")];

        public IReadOnlyList<DecodedFile> Decode(DecodeInput input)
        {
            var data = input.Data;
            var isTable = handled == FourCC.FromString("clut");
            int seed = 0;
            ushort flags = 0;
            bool complete;
            var entries = isTable ? Palettes.ReadColorTable(data, out seed, out flags, out complete) : Palettes.ReadPalette(data, out complete);
            if (!complete)
            {
                input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "color.short",
                    $"{input.Resource}: the data ends before the entries it counts; {entries.Count} read."));
            }
            var json = MacText.Json(w =>
            {
                w.WriteStartObject();
                if (isTable)
                {
                    w.WriteNumber("seed", seed);
                    w.WriteNumber("flags", flags);
                    w.WriteBoolean("device", (flags & 0x8000) != 0);
                }
                w.WriteStartArray("entries");
                foreach (var e in entries)
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", e.Index);
                    if (isTable) w.WriteNumber("value", e.Value);
                    w.WriteNumber("red", e.Red);
                    w.WriteNumber("green", e.Green);
                    w.WriteNumber("blue", e.Blue);
                    w.WriteString("hex", $"#{e.Red >> 8:x2}{e.Green >> 8:x2}{e.Blue >> 8:x2}");
                    if (!isTable)
                    {
                        w.WriteNumber("usage", e.Usage);
                        w.WriteStartArray("usageNames");
                        foreach (var (bit, flag) in UsageNames)
                        {
                            if ((e.Usage & bit) != 0) w.WriteStringValue(flag);
                        }
                        w.WriteEndArray();
                        w.WriteNumber("tolerance", e.Tolerance);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
            return [new DecodedFile(".json", json), new DecodedFile(".act", Palettes.Act(entries))];
        }

        // The Palette Manager's usage flags (Inside Macintosh: Advanced Color Imaging, Palette Manager); 0 is pmCourteous.
        private static readonly (int Bit, string Name)[] UsageNames =
        [
            (0x0001, "pmDithered"), (0x0002, "pmTolerant"), (0x0004, "pmAnimated"), (0x0008, "pmExplicit"), (0x0010, "pmWhite"),
            (0x0020, "pmBlack"), (0x0100, "pmInhibitG2"), (0x0200, "pmInhibitC2"), (0x0400, "pmInhibitG4"), (0x0800, "pmInhibitC4"),
            (0x1000, "pmInhibitG8"), (0x2000, "pmInhibitC8"),
        ];
    }
}
