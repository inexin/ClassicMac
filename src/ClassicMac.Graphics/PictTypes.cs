namespace ClassicMac.Graphics
{
    /// <summary>An 8-bit RGBA color.</summary>
    public readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255);

    /// <summary>
    /// A QuickDraw rectangle as the engine computes with it (32-bit; Core's <c>MacRect</c> is the 16-bit Rect of the
    /// public API): <see cref="Right"/> and <see cref="Bottom"/> are exclusive.
    /// </summary>
    internal readonly record struct PictRect(int Top, int Left, int Bottom, int Right)
    {
        /// <summary>Right − Left.</summary>
        public int Width => Right - Left;

        /// <summary>Bottom − Top.</summary>
        public int Height => Bottom - Top;

        /// <summary>True when the rectangle encloses no pixels (QuickDraw <c>EmptyRect</c>).</summary>
        public bool IsEmpty => Bottom <= Top || Right <= Left;

        /// <summary>A public 16-bit rectangle as the engine's.</summary>
        public static PictRect From(ClassicMac.Core.MacRect r) => new(r.Top, r.Left, r.Bottom, r.Right);

        /// <summary>The rectangle as a 16-bit <c>MacRect</c> (each side truncated to 16 bits).</summary>
        public ClassicMac.Core.MacRect ToMacRect() => new((short)Top, (short)Left, (short)Bottom, (short)Right);
    }

    /// <summary>A picture comment (opcodes 0x00A0 / 0x00A1); <see cref="Data"/> is empty for short comments.</summary>
    public readonly record struct PictComment(int Kind, byte[] Data);
}
