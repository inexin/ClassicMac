using System;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;

namespace ClassicMac.Resources.Decoders.Interface;

/// <summary>
/// A piece of Mac OS 9's Platinum appearance as pixels, stretched like a nine-slice: <see cref="Left"/> columns and
/// <see cref="Top"/> rows are drawn as they are at the left and top, <see cref="Right"/> and <see cref="Bottom"/> at the
/// right and bottom, and the one column and row between them repeat to fill the size asked for [Verified: measured
/// from Mac OS 9.0's screen with Appearance 1.1.1, Platinum theme; every piece below repeats exactly along its middle in
/// the captures, ClassicMac's dialog test fork, docs/formats/resources/windows-dialogs.md §2.4].
/// </summary>
/// <remarks>
/// One character per pixel: a hexadecimal digit <c>n</c> is the grey $nnnnnn ('0' black, 'F' white); 'r' $FF9999 and
/// 'R' $FF6666 (an alert frame's tint); 'l' $CCCCFF, 'm' $9999FF, 'n' $6666CC, 'o' $333399 (the scroll box in the
/// default accent colour); '~' the window's content colour; ' ' not drawn.
/// </remarks>
internal sealed class PlatinumArt
{
    private readonly string[] rows;

    public PlatinumArt(int left, int top, int right, int bottom, params string[] rows)
    {
        this.rows = rows;
        (Left, Top, Right, Bottom) = (left, top, right, bottom);
        if (rows.Length != top + 1 + bottom)
        {
            throw new ArgumentException("The rows do not match the zones.", nameof(rows));
        }

        foreach (var row in rows)
        {
            if (row.Length != left + 1 + right)
            {
                throw new ArgumentException($"Row '{row}' does not match the zones.", nameof(rows));
            }
        }
    }

    public int Left { get; }

    public int Top { get; }

    public int Right { get; }

    public int Bottom { get; }

    /// <summary>The size the art has unstretched.</summary>
    public int Width => Left + 1 + Right;

    public int Height => Top + 1 + Bottom;

    // The source row or column of a target one.
    private static int Source(int t, int size, int near, int far) => t < near ? t : t >= size - far ? near + 1 + far - (size - t) : near;

    /// <summary>
    /// Paints the art into <paramref name="port"/> stretched to <paramref name="width"/> × <paramref name="height"/> at
    /// (<paramref name="h"/>, <paramref name="v"/>), with PaintRect in patCopy, one run of a colour at a time.
    /// </summary>
    public void Paint(QuickDrawPort port, int h, int v, int width, int height, RgbColor content)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var saved = port.ForeColor;
        int y = 0;
        while (y < height)
        {
            // Target rows that map to the same source row are painted as one band.
            int sy = Source(y, height, Top, Bottom), end = y + 1;
            while (end < height && Source(end, height, Top, Bottom) == sy)
            {
                end++;
            }

            var row = rows[sy];
            int x = 0;
            while (x < width)
            {
                char c = row[Source(x, width, Left, Right)];
                int run = x + 1;
                while (run < width && row[Source(run, width, Left, Right)] == c)
                {
                    run++;
                }

                if (c != ' ')
                {
                    port.ForeColor = c == '~' ? content : Color(c);
                    port.PaintRect(Rect(v + y, h + x, v + end, h + run));
                }
                x = run;
            }
            y = end;
        }
        port.ForeColor = saved;
    }

    /// <summary>Paints the art at its own size.</summary>
    public void Paint(QuickDrawPort port, int h, int v, RgbColor content) => Paint(port, h, v, Width, Height, content);

    /// <summary>The art turned a quarter: rows become columns (a vertical scroll bar's pieces drawn horizontally).</summary>
    public PlatinumArt Transposed()
    {
        var turned = new string[Width];
        for (int x = 0; x < Width; x++)
        {
            var chars = new char[Height];
            for (int y = 0; y < Height; y++)
            {
                chars[y] = rows[y][x];
            }

            turned[x] = new string(chars);
        }
        return new PlatinumArt(Top, Left, Bottom, Right, turned);
    }

    public static RgbColor Color(char c) => c switch
    {
        'r' => new(0xFFFF, 0x9999, 0x9999),
        'R' => new(0xFFFF, 0x6666, 0x6666),
        'l' => new(0xCCCC, 0xCCCC, 0xFFFF),
        'm' => new(0x9999, 0x9999, 0xFFFF),
        'n' => new(0x6666, 0x6666, 0xCCCC),
        'o' => new(0x3333, 0x3333, 0x9999),
        _ => Grey(Convert.ToInt32(c.ToString(), 16)),
    };

    private static RgbColor Grey(int digit)
    {
        var value = (ushort)(digit * 0x1111);
        return new RgbColor(value, value, value);
    }

    public static MacRect Rect(int top, int left, int bottom, int right) =>
        new((short)Math.Clamp(top, short.MinValue, short.MaxValue), (short)Math.Clamp(left, short.MinValue, short.MaxValue),
            (short)Math.Clamp(bottom, short.MinValue, short.MaxValue), (short)Math.Clamp(right, short.MinValue, short.MaxValue));

    // ---- The pieces, as Mac OS 9.0 draws them (Appearance 1.1.1, Platinum) ----

    /// <summary>The theme's dialog background (<c>kThemeBrushDialogBackgroundActive</c>), also every alert's.</summary>
    public static readonly RgbColor Background = new(0xDDDD, 0xDDDD, 0xDDDD);

    /// <summary>The title bar's fill, behind the title and the boxes.</summary>
    public static readonly RgbColor TitleBar = new(0xCCCC, 0xCCCC, 0xCCCC);

    /// <summary>A movable modal or modal dialog's frame (dBoxProc): 6 pixels left and top, 7 right and bottom with the shadow.</summary>
    public static readonly PlatinumArt DialogFrame = new(6, 6, 7, 7,
        "0000000000000 ",
        "0BBBBBBBBBBB0 ",
        "0BFFFFFFFFB500",
        "0BF~~~~~~~9500",
        "0BF~~~~~~~9500",
        "0BF~~~~~~~9500",
        "0BF~~~~~~~9500",
        "0BF~~~~~~~9500",
        "0BF~~~~~~~9500",
        "0BF~~~~~~~9500",
        "0BB99999999500",
        "0B555555555500",
        "00000000000000",
        "  000000000000");

    /// <summary>An alert's frame: the dialog frame in the alert's red tint.</summary>
    public static readonly PlatinumArt AlertFrame = new(6, 6, 7, 7,
        "0000000000000 ",
        "0rrrrrrrrrrr0 ",
        "0rFFFFFFFFrR00",
        "0rF~~~~~~~9R00",
        "0rF~~~~~~~9R00",
        "0rF~~~~~~~9R00",
        "0rF~~~~~~~9R00",
        "0rF~~~~~~~9R00",
        "0rF~~~~~~~9R00",
        "0rF~~~~~~~9R00",
        "0rr99999999R00",
        "0rRRRRRRRRRR00",
        "00000000000000",
        "  000000000000");

    /// <summary>A movable modal dialog's frame (movableDBoxProc): a 22-row title bar above the dialog frame, 27 rows in all.</summary>
    public static readonly PlatinumArt MovableFrame = new(6, 27, 7, 7,
        "0000000000000 ",
        "0FFFFFFFFFFC0 ",
        "0FCCCCCCCCC900",
        "0FCCCCCCCCC900",
        "0FCCCFFCCCC900",
        "0FCCCC77CCC900",
        "0FCCCFFCCCC900",
        "0FCCCC77CCC900",
        "0FCCCFFCCCC900",
        "0FCCCC77CCC900",
        "0FCCCFFCCCC900",
        "0FCCCC77CCC900",
        "0FCCCFFCCCC900",
        "0FCCCC77CCC900",
        "0FCCCFFCCCC900",
        "0FCCCC77CCC900",
        "0FCCCCCCCCC900",
        "0FCCCCCCCCC900",
        "0FCCCCCCCCC900",
        "0FCCCCCCCCC900",
        "0C999999999900",
        "00000000000000",
        "0DDDDDDDDDDD00",
        "0DFFFFFFFFD500",
        "0DF~~~~~~~9500",
        "0DF~~~~~~~9500",
        "0DF~~~~~~~9500",
        "0DF~~~~~~~9500",
        "0DF~~~~~~~9500",
        "0DF~~~~~~~9500",
        "0DF~~~~~~~9500",
        "0DD99999999500",
        "0D555555555500",
        "00000000000000",
        "  000000000000");

    /// <summary>
    /// A document window's frame (documentProc): a 22-row title bar with the collapse box at the right, the content
    /// framed by a black line, a 6-pixel border and the shadow. The right zone (24 columns) holds the collapse box.
    /// </summary>
    public static readonly PlatinumArt DocumentFrame = new(6, 22, 24, 7,
        "000000000000000000000000000000 ",
        "0FFFFFFFFFFFFFFFFFFFFFFFFFFFC0 ",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCFFFCCCCC888888888888CCC900",
        "0FCCCC777CCCC822222222222FCC900",
        "0FCCCFFFCCCCC82FCCCCCCCC2FCC900",
        "0FCCCC777CCCC82C99AABBC82FCC900",
        "0FCCCFFFCCCCC82C9AABBCC82FCC900",
        "0FCCCC777CCCC822222222222FCC900",
        "0FCCCFFFCCCCC82CABBCCDD82FCC900",
        "0FCCCC777CCCC822222222222FCC900",
        "0FCCCFFFCCCCC82CBCCDDEE82FCC900",
        "0FCCCC777CCCC82CCCDDEEF82FCC900",
        "0FCCCFFFCCCCC82C888888882FCC900",
        "0FCCCC777CCCC822222222222FCC900",
        "0FCCCCCCCCCCCCFFFFFFFFFFFFCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCC999999999999999999999CCC900",
        "0FCC900000000000000000000FCC900",
        "0FCC90~~~~~~~~~~~~~~~~~~0FCC900",
        "0FCC900000000000000000000FCC900",
        "0FCCCFFFFFFFFFFFFFFFFFFFFFCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0C99999999999999999999999999900",
        "0000000000000000000000000000000",
        "  00000000000000000000000000000");

    /// <summary>
    /// A close box, for a document window with one [ClassicMac: not in the captures; the collapse box's frame without
    /// its bars, at the mirrored place].
    /// </summary>
    public static readonly PlatinumArt CloseBox = new(6, 6, 6, 6,
        "888888888888C",
        "822222222222F",
        "82FFFFFFFFC2F",
        "82FCCCCCCC82F",
        "82FCCCCCCC82F",
        "82FCCCCCCC82F",
        "82FCCCCCCC82F",
        "82FCCCCCCC82F",
        "82FCCCCCCC82F",
        "82FCCCCCCC82F",
        "82C888888882F",
        "822222222222F",
        "CFFFFFFFFFFFF");

    /// <summary>A push button: a rounded bevel, filled light grey.</summary>
    public static readonly PlatinumArt Button = new(5, 5, 5, 5,
        "  2000002  ",
        " 0BDDDDDB0 ",
        "2BFFFFFFDB2",
        "0DFFDDDDA70",
        "0DFDDDDDA70",
        "0DFDDDDDA70",
        "0DFDDDDDA70",
        "0DFDDDDAA70",
        "2BDAAAAA772",
        " 0B7777770 ",
        "  2000002  ");

    /// <summary>The default push button with its ring, 3 pixels outside the button's rectangle (an alert's default item).</summary>
    public static readonly PlatinumArt DefaultButton = new(9, 8, 9, 9,
        "   2000000000002   ",
        "  0DDDDDDDDDDDDC0  ",
        " 0DDAAAAAAAAAAAAB0 ",
        "2DDA72000000027AA82",
        "0DA70BDDDDDDDB07A70",
        "0DA2BFFFFFFFFDB2A70",
        "0DA0DFFDDDDDDA70A70",
        "0DA0DFDDDDDDDA70A70",
        "0DA0DFDDDDDDDA70A70",
        "0DA0DFDDDDDDDA70A70",
        "0DA0DFDDDDDDDA70A70",
        "0DA0DFDDDDDDAA70A70",
        "0DA2BDAAAAAAA772A70",
        "0DA70B7777777707A70",
        "2CAA72000000027A872",
        " 0BAAAAAAAAAAAA870 ",
        "  087777777777770  ",
        "   2000000000002   ");

    /// <summary>A check box, off: 12 × 12.</summary>
    public static readonly PlatinumArt CheckBox = new(5, 5, 6, 6,
        "000000000000",
        "0FFFFFFFFFD0",
        "0FDDDDDDDD80",
        "0FDDDDDDDD80",
        "0FDDDDDDDD80",
        "0FDDDDDDDD80",
        "0FDDDDDDDD80",
        "0FDDDDDDDD80",
        "0FDDDDDDDD80",
        "0FDDDDDDDD80",
        "0D8888888880",
        "000000000000");

    /// <summary>A check box, on: the box with its check mark, which overhangs the box by two columns.</summary>
    public static readonly PlatinumArt CheckBoxOn = new(5, 5, 8, 6,
        "000000000000  ",
        "0FFFFFFFFFD00 ",
        "0FDDDDDDDD007A",
        "0FDDDDDDD000A ",
        "0FDDDDDD0050  ",
        "0F00DDD00770  ",
        "0FD00D007A80  ",
        "0FDA0007AD80  ",
        "0FDDA07ADD80  ",
        "0FDDD7ADDD80  ",
        "0D8888888880  ",
        "000000000000  ");

    /// <summary>A radio button, off: 12 × 12 (its edge pixels are blended with a white background).</summary>
    public static readonly PlatinumArt RadioButton = new(5, 5, 6, 6,
        "    4004    ",
        "  05DDDB40  ",
        " 0BDEFFFD80 ",
        " 5DEFFEEDB4 ",
        "4DEFFEEDDB84",
        "0DFFEEDDBB80",
        "0DFEEDDBBA80",
        "4BFEDDBBAA84",
        " 5DDDBBAA84 ",
        " 08BBBAA880 ",
        "  05888840  ",
        "    4004    ");

    /// <summary>A scroll bar's track, from a black line (the bar's top or the scroll box's bottom) down: shaded below it.</summary>
    public static readonly PlatinumArt Track = new(3, 3, 3, 0,
        "0000000",
        "07777C0",
        "0788BC0",
        "078ABC0");

    /// <summary>The scroll box (thumb), 16 × 17 including its black lines, with its grip.</summary>
    public static readonly PlatinumArt Thumb = new(8, 8, 7, 8,
        "0000000000000000",
        "0Ellllllllllllm0",
        "0lmmmmmmmmmmmmn0",
        "0lmmmmmmmmmmmmn0",
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0",
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0",
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0",
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0",
        "0lmmmmmmmmmmmmn0",
        "0lmmmmmmmmmmmmn0",
        "0lmmmmmmmmmmmmn0",
        "0mnnnnnnnnnnnnn0",
        "0000000000000000");

    /// <summary>The scroll box without its grip, stretched to any length (a proportional thumb).</summary>
    public static readonly PlatinumArt ThumbBody = new(8, 2, 7, 2,
        "0000000000000000",
        "0Ellllllllllllm0",
        "0lmmmmmmmmmmmmn0",
        "0mnnnnnnnnnnnnn0",
        "0000000000000000");

    /// <summary>The scroll box's grip, 8 rows, centred in it.</summary>
    public static readonly PlatinumArt Grip = new(8, 4, 7, 3,
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0",
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0",
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0",
        "0lmmEllllllmmmn0",
        "0lmmmooooooommn0");

    /// <summary>
    /// The Finder's folder window (a zoomable document window): the close box at the left of the 22-row title bar,
    /// the zoom and collapse boxes at the right, the content framed by a black line, a 6-pixel border and the shadow;
    /// the grow box's corner in the bottom border. Measured from a Mac OS 9.0 Finder window [Verified].
    /// </summary>
    public static readonly PlatinumArt FinderFrame = new(22, 22, 39, 7,
        "0000000000000000000000000000000000000000000000000000000000000 ",
        "0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFC0 ",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCC888888888888CCCCCFFCCCCC888888888888CCCC888888888888CCC900",
        "0FCC822222222222FCCCCC77CCCC822222222222FCCC822222222222FCC900",
        "0FCC82FCCCCCCCC2FCCCCFFCCCCC82FCCCC2CCC2FCCC82FCCCCCCCC2FCC900",
        "0FCC82C99AABBC82FCCCCC77CCCC82C99AA2BC82FCCC82C99AABBC82FCC900",
        "0FCC82C9AABBCC82FCCCCFFCCCCC82C9AAB2CC82FCCC82C9AABBCC82FCC900",
        "0FCC82CAABBCCD82FCCCCC77CCCC82CAABB2CD82FCCC822222222222FCC900",
        "0FCC82CABBCCDD82FCCCCFFCCCCC82CABBC2DD82FCCC82CABBCCDD82FCC900",
        "0FCC82CBBCCDDE82FCCCCC77CCCC82222222DE82FCCC822222222222FCC900",
        "0FCC82CBCCDDEE82FCCCCFFCCCCC82CBCCDDEE82FCCC82CBCCDDEE82FCC900",
        "0FCC82CCCDDEEF82FCCCCC77CCCC82CCCDDEEF82FCCC82CCCDDEEF82FCC900",
        "0FCC82C888888882FCCCCFFCCCCC82C888888882FCCC82C888888882FCC900",
        "0FCC822222222222FCCCCC77CCCC822222222222FCCC822222222222FCC900",
        "0FCCCFFFFFFFFFFFFCCCCCCCCCCCCFFFFFFFFFFFFCCCCFFFFFFFFFFFFCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCC9999999999999999999999999999999999999999999999999999CCC900",
        "0FCC9000000000000000000000000000000000000000000000000000FCC900",
        "0FCC90~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~0FCC900",
        "0FCC9000000000000000000000000000000000000FCCCCCCCCCCCCCCCCC900",
        "0FCCCFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0FCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC900",
        "0C999999999999999999999999999999999999999999999999999999999900",
        "00000000000000000000000000000000000000000000000000000000000000",
        "  000000000000000000000000000000000000000000000000000000000000");

    /// <summary>The grow box, 16 × 16 from its top-left black corner, in the content's bottom-right corner.</summary>
    public static readonly PlatinumArt GrowBox = new(7, 7, 8, 8,
        "0000000000000000",
        "0FFFFFFFFFFFFFFF",
        "0FCCCCCCCCCCCCCC",
        "0FCCCCCCCCCCCCCC",
        "0FCCCCCCCFFCCCCC",
        "0FCCCCCCFC7CCCCC",
        "0FCCCCCFC7CFFCCC",
        "0FCCCCFC7CFC7CCC",
        "0FCCCFC7CFC7CFFC",
        "0FCCFC7CFC7CFC7C",
        "0FCCA7CFC7CFC7CC",
        "0FCCCCFC7CFC7CCC",
        "0FCCCCA7CFC7CCCC",
        "0FCCCCCCFC7CCCCC",
        "0FCCCCCCA7CCCCCC",
        "0FCCCCCCCCCCCCCC");

    /// <summary>A scroll bar with nothing to scroll: an empty  trough under a 555 line.</summary>
    public static readonly PlatinumArt InactiveTrack = new(7, 1, 8, 0,
        "0555555555555550",
        "0EEEEEEEEEEEEEE0");

    /// <summary>A scroll bar's up arrow with nothing to scroll: grey (888) on the trough.</summary>
    public static readonly PlatinumArt InactiveUpArrow = new(7, 7, 8, 8,
        "0555555555555550",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEE88EEEEEE0",
        "0EEEEE8888EEEEE0",
        "0EEEE888888EEEE0",
        "0EEE88888888EEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0888888888888880");

    /// <summary>The down arrow with nothing to scroll.</summary>
    public static readonly PlatinumArt InactiveDownArrow = new(7, 7, 8, 8,
        "0888888888888880",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEE88888888EEE0",
        "0EEEE888888EEEE0",
        "0EEEEE8888EEEEE0",
        "0EEEEEE88EEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0EEEEEEEEEEEEEE0",
        "0000000000000000");

    /// <summary>The up arrow, 16 × 16 including the black lines it shares.</summary>
    public static readonly PlatinumArt UpArrow = new(8, 8, 7, 7,
        "0000000000000000",
        "0FFFFFFFFFFFFFD0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDD00DDDDDB0",
        "0FDDDD0000DDDDB0",
        "0FDDD000000DDDB0",
        "0FDD00000000DDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0DBBBBBBBBBBBBB0",
        "0000000000000000");

    /// <summary>The down arrow, 16 × 16 including the black lines it shares.</summary>
    public static readonly PlatinumArt DownArrow = new(8, 8, 7, 7,
        "0000000000000000",
        "0FFFFFFFFFFFFFD0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDD00000000DDB0",
        "0FDDD000000DDDB0",
        "0FDDDD0000DDDDB0",
        "0FDDDDD00DDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0FDDDDDDDDDDDDB0",
        "0DBBBBBBBBBBBBB0",
        "0000000000000000");
}
