namespace ClassicMac.Graphics
{
    // The Macintosh's standard color tables ('clut' resources 1, 2, 4, 8 and the gray ramps 33-40), which QuickTime
    // image descriptions refer to by id. Index 0 is white and the last entry black.
    internal static class StandardColorTables
    {
        public static RgbaColor[]? ForId(int id) => id switch
        {
            1 => Gray(1),
            2 => new[] { Rgb(0xFF, 0xFF, 0xFF), Rgb(0xAC, 0xAC, 0xAC), Rgb(0x55, 0x55, 0x55), Rgb(0, 0, 0) },
            4 => FourBit,
            8 => EightBit,
            >= 33 and <= 40 => Gray(id - 32),
            _ => null,
        };

        // The default table for an image depth without a table id.
        public static RgbaColor[]? ForDepth(int depth) => depth switch
        {
            1 or 2 or 4 or 8 => ForId(depth),
            >= 33 and <= 40 => Gray(depth - 32),
            _ => null,
        };

        // The exact 16-bit components of clut 4 (not byte-replicated) and clut 8 (each byte repeated); null for others.
        public static (ushort r, ushort g, ushort b)[]? Exact(int id) => id switch
        {
            4 => FourBit16,
            8 => System.Array.ConvertAll(EightBit, c => ((ushort)(c.R * 257), (ushort)(c.G * 257), (ushort)(c.B * 257))),
            _ => null,
        };

        private static readonly (ushort r, ushort g, ushort b)[] FourBit16 =
        {
            (0xFFFF, 0xFFFF, 0xFFFF), (0xFC00, 0xF37D, 0x052F), (0xFFFF, 0x648A, 0x028C), (0xDD6B, 0x08C2, 0x06A2),
            (0xF2D7, 0x0856, 0x84EC), (0x46E3, 0x0000, 0xA53E), (0x0000, 0x0000, 0xD400), (0x0241, 0xAB54, 0xEAFF),
            (0x1F21, 0xB793, 0x1431), (0x0000, 0x64AF, 0x11B0), (0x5600, 0x2C9D, 0x0524), (0x90D7, 0x7160, 0x3A34),
            (0xC000, 0xC000, 0xC000), (0x8000, 0x8000, 0x8000), (0x4000, 0x4000, 0x4000), (0x0000, 0x0000, 0x0000),
        };

        private static RgbaColor Rgb(int r, int g, int b) => new RgbaColor((byte)r, (byte)g, (byte)b);

        private static readonly RgbaColor[] FourBit =
        {
            Rgb(0xFF, 0xFF, 0xFF), Rgb(0xFC, 0xF3, 0x05), Rgb(0xFF, 0x64, 0x02), Rgb(0xDD, 0x08, 0x06),
            Rgb(0xF2, 0x08, 0x84), Rgb(0x46, 0x00, 0xA5), Rgb(0x00, 0x00, 0xD4), Rgb(0x02, 0xAB, 0xEA),
            Rgb(0x1F, 0xB7, 0x14), Rgb(0x00, 0x64, 0x11), Rgb(0x56, 0x2C, 0x05), Rgb(0x90, 0x71, 0x3A),
            Rgb(0xC0, 0xC0, 0xC0), Rgb(0x80, 0x80, 0x80), Rgb(0x40, 0x40, 0x40), Rgb(0x00, 0x00, 0x00),
        };

        // 8-bit: the 6x6x6 cube of 0xFF..0x00 in steps of 0x33 (red slowest, from white), then red, green, blue
        // and gray ramps of the ten levels between (0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11),
        // then black.
        private static readonly RgbaColor[] EightBit = BuildEightBit();

        private static RgbaColor[] BuildEightBit()
        {
            var table = new RgbaColor[256];
            int i = 0;
            for (int r = 5; r >= 0; r--)
                for (int g = 5; g >= 0; g--)
                    for (int b = 5; b >= 0; b--)
                        if (i < 215) table[i++] = Rgb(r * 0x33, g * 0x33, b * 0x33);
            int[] ramp = { 0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11 };
            foreach (var v in ramp) table[i++] = Rgb(v, 0, 0);
            foreach (var v in ramp) table[i++] = Rgb(0, v, 0);
            foreach (var v in ramp) table[i++] = Rgb(0, 0, v);
            foreach (var v in ramp) table[i++] = Rgb(v, v, v);
            table[255] = Rgb(0, 0, 0);
            return table;
        }

        private static RgbaColor[] Gray(int bits)
        {
            int n = 1 << bits;
            var table = new RgbaColor[n];
            for (int k = 0; k < n; k++)
            {
                int v = 255 - k * 255 / (n - 1);
                table[k] = Rgb(v, v, v);
            }
            return table;
        }
    }
}
