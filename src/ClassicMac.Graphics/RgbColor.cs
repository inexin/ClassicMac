namespace ClassicMac.Graphics;

/// <summary>
/// QuickDraw's <c>RGBColor</c>: three 16-bit components. QuickDraw keeps a port's colours at this precision, and some
/// transfer modes and colour matching use every bit; <see cref="RgbaColor"/> is the 8-bit pixel colour.
/// </summary>
public readonly record struct RgbColor(ushort Red, ushort Green, ushort Blue)
{
    /// <summary>Black (0, 0, 0).</summary>
    public static RgbColor Black => new(0, 0, 0);

    /// <summary>White ($FFFF, $FFFF, $FFFF).</summary>
    public static RgbColor White => new(0xFFFF, 0xFFFF, 0xFFFF);

    /// <summary>The colour as 8-bit components: the high byte of each, as QuickDraw draws it on a 32-bit screen.</summary>
    public RgbaColor ToRgba() => new((byte)(Red >> 8), (byte)(Green >> 8), (byte)(Blue >> 8));

    /// <summary>An 8-bit colour widened to 16 bits (each byte repeated, so $FF becomes $FFFF).</summary>
    public static RgbColor FromRgba(RgbaColor color) => new((ushort)(color.R * 257), (ushort)(color.G * 257), (ushort)(color.B * 257));

    internal (ushort r, ushort g, ushort b) Tuple => (Red, Green, Blue);
}
