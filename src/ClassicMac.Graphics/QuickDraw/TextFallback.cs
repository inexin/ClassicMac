namespace ClassicMac.Graphics.QuickDraw
{
    /// <summary>
    /// Rasterizes a run of text into a 1-bit mask, which the decoder then transfers with the picture's text mode
    /// and foreground color, like QuickDraw's own text drawing.
    /// </summary>
    public interface ITextFallback
    {
        /// <summary>Renders <paramref name="text"/>, or returns null to draw nothing.</summary>
        TextFallbackMask? Render(string text, TextFallbackStyle style);
    }

    /// <summary>A rasterized text run: ink bits plus where the pen (on the baseline) sits inside the mask.</summary>
    public sealed class TextFallbackMask
    {
        /// <summary>Creates a mask.</summary>
        /// <param name="width">Mask width in pixels.</param>
        /// <param name="height">Mask height in pixels.</param>
        /// <param name="originX">Horizontal pen position within the mask (the left edge of the run on the baseline).</param>
        /// <param name="originY">Baseline row within the mask.</param>
        /// <param name="bits">Width × height bytes, row-major; non-zero is ink.</param>
        /// <param name="advance">How far the pen moves after the run, in pixels.</param>
        public TextFallbackMask(int width, int height, int originX, int originY, byte[] bits, float advance)
        {
            if (bits == null || bits.Length != width * height)
            {
                throw new System.ArgumentException("Expected width × height mask bytes.", nameof(bits));
            }

            Width = width;
            Height = height;
            OriginX = originX;
            OriginY = originY;
            Bits = bits;
            Advance = advance;
        }

        /// <summary>Mask width in pixels.</summary>
        public int Width { get; }

        /// <summary>Mask height in pixels.</summary>
        public int Height { get; }

        /// <summary>Horizontal pen position within the mask.</summary>
        public int OriginX { get; }

        /// <summary>Baseline row within the mask.</summary>
        public int OriginY { get; }

        /// <summary>Row-major ink bytes; non-zero is ink.</summary>
        public byte[] Bits { get; }

        /// <summary>Pen advance in pixels.</summary>
        public float Advance { get; }
    }

    /// <summary>The QuickDraw text state a run of text is drawn with.</summary>
    /// <param name="FontId">QuickDraw font number (TxFont), e.g. 0 system, 2 New York, 3 Geneva, 4 Monaco, 20 Times, 21 Helvetica, 22 Courier.</param>
    /// <param name="Face">QuickDraw style bits (TxFace): 1 bold, 2 italic, 4 underline, 8 outline, 16 shadow, 32 condense, 64 extend.</param>
    /// <param name="Size">Point size (TxSize); 0 means the font's default size.</param>
    /// <param name="FontName">The font's name, when the picture names it (a picture's fontName opcode, 0x002C).</param>
    public readonly record struct TextFallbackStyle(int FontId, int Face, int Size, string? FontName = null);
}
