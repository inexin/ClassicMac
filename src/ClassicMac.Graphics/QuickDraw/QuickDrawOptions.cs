using System;

namespace ClassicMac.Graphics.QuickDraw;

/// <summary>How a <see cref="QuickDrawPort"/> draws: which QuickDraw, on what screen, with which fonts.</summary>
public sealed class QuickDrawOptions
{
    /// <summary>Default options: Mac OS 9's QuickDraw on a 32-bit screen, no fonts.</summary>
    public static QuickDrawOptions Default { get; } = new();

    /// <summary>
    /// Which Macintosh QuickDraw to reproduce: <see cref="QuickDrawVersion.MacOS9"/> (the default) or the 68k QuickDraw
    /// of the Macintosh ROM.
    /// </summary>
    public QuickDrawVersion Version { get; init; } = QuickDrawVersion.MacOS9;

    /// <summary>
    /// The depth of the screen drawn on: 32 (the default) in full colour; 1, 2, 4 and 8 into an indexed screen with the
    /// Macintosh's default colour table for that depth, and 16 into a 5-5-5 screen, with QuickDraw's own colour
    /// matching, transfer modes on pixel values and dithering. The canvas stays RGBA: each pixel the colour it has
    /// on that screen.
    /// </summary>
    public int ScreenDepth
    {
        get => screenDepth;
        init => screenDepth = value is 1 or 2 or 4 or 8 or 16 or 32
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ScreenDepth), value, "The screen depth must be 1, 2, 4, 8, 16 or 32.");
    }
    private readonly int screenDepth = 32;

    /// <summary>Bitmap fonts to draw text with, exactly as QuickDraw does. Text in a family the library lacks goes to <see cref="TextFallback"/>.</summary>
    public FontLibrary? Fonts { get; init; }

    /// <summary>Rasterizes text no bitmap font can draw. Null draws nothing for it.</summary>
    public ITextFallback? TextFallback { get; init; }

    /// <summary>
    /// The system highlight colour a new port starts with. Null uses the chosen QuickDraw's default: Mac OS 9's
    /// lavender ($CCCC, $CCCC, $FFFF), or light cyan ($9999, $CCCC, $CCCC) for the ROM.
    /// </summary>
    public RgbColor? HiliteColor { get; init; }

    /// <summary>
    /// Keeps the alpha channel of 32-bit sources that carry one when they are copied with srcCopy. QuickDraw ignores
    /// it, so it is off by default.
    /// </summary>
    public bool PreserveAlpha { get; init; }
}
