namespace ClassicMac.Graphics.QuickDraw;

/// <summary>The QuickDraw implementation to draw as. The two differ in a few rounding and edge rules.</summary>
public enum QuickDrawVersion
{
    /// <summary>Mac OS 9's native PowerPC QuickDraw and Font Manager, as in Mac OS 9.0 (the build analysed and tested; later 9.x may differ).</summary>
    MacOS9,

    /// <summary>The 68k QuickDraw and Font Manager of the Macintosh ROM (Mac OS ROM 1.6), as on Macs before Mac OS 9.</summary>
    MacRom,
}
