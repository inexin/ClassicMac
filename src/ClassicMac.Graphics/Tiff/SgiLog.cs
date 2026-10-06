using System;

namespace ClassicMac.Graphics;

/// <summary>
/// SGILog high dynamic range pixels (Greg Ward's LogLuv encoding; docs/formats/graphics/tiff.md §2.10): LogL16, a sign
/// and 15 bits of log luminance; LogLuv32, that and 8-bit u′ and v′, each row's pixels run-length coded a byte plane at a
/// time, most significant first; and LogLuv24, 10 bits of log luminance and a 14-bit index into a grid of the visible
/// (u′, v′), not compressed. The pixels become CIE XYZ, then a display image by a global tone mapping.
/// </summary>
internal static class SgiLog
{
    /// <summary>
    /// Decodes one row of <paramref name="pixels"/> values of <paramref name="planes"/> bytes from
    /// <paramref name="input"/> at <paramref name="at"/>; false when the data ends first.
    /// </summary>
    public static bool DecodeRow(ReadOnlySpan<byte> input, ref int at, Span<uint> pixels, int planes)
    {
        pixels.Clear();
        for (var shift = 8 * (planes - 1); shift >= 0; shift -= 8)
        {
            var i = 0;
            while (i < pixels.Length && at < input.Length)
            {
                var control = input[at++];
                if (control >= 128)
                {
                    // A run: the next byte, control − 126 times.
                    if (at >= input.Length)
                    {
                        return false;
                    }

                    var value = (uint)input[at++] << shift;
                    for (var n = control - 126; n > 0 && i < pixels.Length; n--)
                    {
                        pixels[i++] |= value;
                    }
                }
                else
                {
                    // Literal bytes, as many as the control says.
                    for (var n = control; n > 0 && i < pixels.Length && at < input.Length; n--)
                    {
                        pixels[i++] |= (uint)input[at++] << shift;
                    }
                }
            }

            if (i < pixels.Length)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One row of LogLuv24 pixels: three bytes each, most significant first, not compressed.</summary>
    public static bool ReadRow24(ReadOnlySpan<byte> input, ref int at, Span<uint> pixels)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            if (at + 3 > input.Length)
            {
                return false;
            }

            pixels[i] = (uint)(input[at] << 16 | input[at + 1] << 8 | input[at + 2]);
            at += 3;
        }

        return true;
    }

    /// <summary>
    /// A LogLuv24 value as CIE XYZ: Y = 2^((Le + 0.5) / 64 − 12) from its top 10 bits, 0 for Le 0; (u′, v′) the centre
    /// of the grid cell its low 14 bits index, the equal-energy white for an index past the grid.
    /// </summary>
    public static (double X, double Y, double Z) Xyz24(uint luv)
    {
        var le = luv >> 14 & 0x3FF;
        if (le == 0)
        {
            return (0, 0, 0);
        }

        var y = Math.Exp(Math.Log(2) / 64 * (le + 0.5) - Math.Log(2) * 12);
        var (u, v) = Cell((int)(luv & 0x3FFF));
        return FromChromaticity(u, v, y);
    }

    // The centre of a grid cell (libtiff's uv_decode): the last row whose cells before it are at most the index.
    private static (double U, double V) Cell(int index)
    {
        if (index < 0 || index >= LogLuvTable.Cells)
        {
            return (0.210526316, 0.473684211);
        }

        int lower = 0, upper = LogLuvTable.Rows.Length;
        while (upper - lower > 1)
        {
            var middle = (lower + upper) >> 1;
            var past = index - LogLuvTable.Rows[middle].Before;
            if (past > 0)
            {
                lower = middle;
            }
            else if (past < 0)
            {
                upper = middle;
            }
            else
            {
                lower = middle;
                break;
            }
        }

        var row = LogLuvTable.Rows[lower];
        var u = (double)row.UStart + (index - row.Before + 0.5) * LogLuvTable.CellSize;
        var v = (double)LogLuvTable.VStart + (lower + 0.5) * LogLuvTable.CellSize;
        return (u, v);
    }

    private static (double X, double Y, double Z) FromChromaticity(double u, double v, double y)
    {
        var s = 1 / (6 * u - 16 * v + 12);
        var x = 9 * u * s;
        var yc = 4 * v * s;
        return (x / yc * y, y, (1 - x - yc) / yc * y);
    }

    /// <summary>The luminance of a LogL16 value: 2^((Le + 0.5) / 256 − 64), 0 for Le 0, negative with the sign bit.</summary>
    public static double Luminance(uint logL)
    {
        var le = logL & 0x7FFF;
        if (le == 0)
        {
            return 0;
        }

        var y = Math.Exp(Math.Log(2) / 256 * (le + 0.5) - Math.Log(2) * 64);
        return (logL & 0x8000) != 0 ? -y : y;
    }

    /// <summary>A LogLuv32 value as CIE XYZ: its luminance, and u′ = (ue + 0.5) / 410, v′ = (ve + 0.5) / 410.</summary>
    public static (double X, double Y, double Z) Xyz(uint luv)
    {
        var y = Luminance(luv >> 16);
        if (y <= 0)
        {
            return (0, 0, 0);
        }

        var u = ((luv >> 8 & 0xFF) + 0.5) / 410;
        var v = ((luv & 0xFF) + 0.5) / 410;
        return FromChromaticity(u, v, y);
    }

    /// <summary>
    /// The image tone-mapped into <paramref name="bitmap"/> (tiff.md §5): Reinhard's global operator at the key 0.18
    /// over the log-average luminance, colour from XYZ by libtiff's matrix for its primaries and equal-energy white,
    /// then the sRGB curve.
    /// </summary>
    public static void ToneMap(RgbaBitmap bitmap, double[] x, double[] y, double[] z)
    {
        double sum = 0;
        var count = 0;
        foreach (var luminance in y)
        {
            if (luminance > 0)
            {
                sum += Math.Log(1e-6 + luminance);
                count++;
            }
        }

        var average = count == 0 ? 1 : Math.Exp(sum / count);
        for (var i = 0; i < y.Length; i++)
        {
            var at = i * 4;
            if (y[i] <= 0)
            {
                (bitmap.Pixels[at], bitmap.Pixels[at + 1], bitmap.Pixels[at + 2], bitmap.Pixels[at + 3]) = (0, 0, 0, 255);
                continue;
            }

            var scaled = 0.18 / average * y[i];
            var display = scaled / (1 + scaled);
            var factor = display / y[i];
            var r = (2.690 * x[i] - 1.276 * y[i] - 0.414 * z[i]) * factor;
            var g = (-1.022 * x[i] + 1.978 * y[i] + 0.044 * z[i]) * factor;
            var b = (0.061 * x[i] - 0.224 * y[i] + 1.163 * z[i]) * factor;
            (bitmap.Pixels[at], bitmap.Pixels[at + 1], bitmap.Pixels[at + 2], bitmap.Pixels[at + 3]) = (Curve(r), Curve(g), Curve(b), 255);
        }
    }

    private static byte Curve(double v)
    {
        v = Math.Clamp(v, 0, 1);
        var s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Round(s * 255);
    }
}
