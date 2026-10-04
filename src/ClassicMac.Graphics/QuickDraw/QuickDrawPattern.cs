using ClassicMac.Graphics;

namespace ClassicMac.Graphics.QuickDraw;

/// <summary>
/// A QuickDraw pattern: the classic 8×8 1-bit <c>Pattern</c> (one byte per row, the most significant bit leftmost; a 1
/// draws the foreground colour, a 0 the background colour), or a colour <c>PixPat</c> read from a picture.
/// </summary>
// A PixPat replaces the 1-bit pattern: type 1 is a full PixMap pattern, type 2 (ditherPat) a solid RGB color.
// Operand layout: Inside Macintosh: Imaging With QuickDraw, Appendix A, Listing A-1.
public sealed partial class QuickDrawPattern
{
    internal byte[] Mono = new byte[8];
    internal PixMap? Pixels;          // PixPat type 1
    internal RgbaColor? Rgb;          // PixPat type 2 (ditherPat)
    internal (ushort r, ushort g, ushort b) Rgb16;   // its exact 16-bit components

    private QuickDrawPattern() { }

    /// <summary>QuickDraw's <c>black</c>: every pixel the foreground colour.</summary>
    public static QuickDrawPattern Black => FromMono(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });

    /// <summary>QuickDraw's <c>white</c>: every pixel the background colour.</summary>
    public static QuickDrawPattern White => FromMono(new byte[8]);

    /// <summary>QuickDraw's <c>gray</c> ($AA55AA55AA55AA55).</summary>
    public static QuickDrawPattern Gray => FromMono(new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 });

    /// <summary>QuickDraw's <c>ltGray</c> ($8822882288228822).</summary>
    public static QuickDrawPattern LightGray => FromMono(new byte[] { 0x88, 0x22, 0x88, 0x22, 0x88, 0x22, 0x88, 0x22 });

    /// <summary>QuickDraw's <c>dkGray</c> ($77DD77DD77DD77DD).</summary>
    public static QuickDrawPattern DarkGray => FromMono(new byte[] { 0x77, 0xDD, 0x77, 0xDD, 0x77, 0xDD, 0x77, 0xDD });

    /// <summary>A 1-bit pattern from its eight rows (a <c>'PAT '</c> resource, or a <c>'PAT#'</c> entry).</summary>
    /// <exception cref="System.ArgumentException">Not eight bytes.</exception>
    public static QuickDrawPattern FromBits(System.ReadOnlySpan<byte> rows) =>
        rows.Length == 8 ? FromMono(rows.ToArray()) : throw new System.ArgumentException("A pattern is eight bytes.", nameof(rows));

    /// <summary>The 1-bit pattern's rows (for a colour pattern, the 1-bit pattern it falls back to).</summary>
    public System.ReadOnlySpan<byte> Bits => Mono;

    internal static QuickDrawPattern FromMono(byte[] rows) => new QuickDrawPattern { Mono = rows };

    // BkPixPat / PnPixPat / FillPixPat operands: patType, the 1-bit fallback pattern, then a PixMap + ColorTable +
    // PixData or an RGBColor. The ROM reads the RGBColor for type 2 only; Mac OS 9 reads a PixMap for types 1 and 3
    // only.
    internal static QuickDrawPattern Read(ClassicMac.Core.BigEndianReader b, bool macOS9)
    {
        int patType = b.ReadUInt16();
        var pattern = FromMono(b.ReadBytes(8).ToArray());
        if (macOS9 ? patType != 1 && patType != 3 : patType == 2)
        {
            int r = b.ReadUInt16(), g = b.ReadUInt16(), bl = b.ReadUInt16();
            pattern.Rgb = new RgbaColor((byte)(r >> 8), (byte)(g >> 8), (byte)(bl >> 8));
            pattern.Rgb16 = ((ushort)r, (ushort)g, (ushort)bl);
        }
        else
        {
            pattern.Pixels = PixMap.ReadPatternPixMap(b, macOS9);
        }

        return pattern;
    }
}
