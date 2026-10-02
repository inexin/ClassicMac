using System.Text;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.Resources.Decoders.Tests;

// Dialogs and alerts drawn as Mac OS 9 with Appearance's Platinum theme draws them, from hand-built forks.
public class DialogRendererTests(ITestOutputHelper output)
{
    // ---- A fork with one of each kind of window and item ----

    private static byte[] W(params int[] words) => words.SelectMany(w => new[] { (byte)(w >> 8), (byte)w }).ToArray();

    private static byte[] L(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] P(string s) => [(byte)s.Length, .. Encoding.Latin1.GetBytes(s)];

    private static byte[] Item(int type, int t, int l, int b, int r, byte[] data) =>
        [.. L(0), .. W(t, l, b, r), (byte)type, (byte)data.Length, .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];

    private static byte[] Text(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Ditl(params byte[][] items) => [.. W(items.Length - 1), .. items.SelectMany(i => i)];

    private static byte[] Dlog(int t, int l, int b, int r, int proc, bool goAway, int ditl, string title)
    {
        byte[] x = [.. W(t, l, b, r, proc), 1, 0, (byte)(goAway ? 1 : 0), 0, .. L(0), .. W(ditl), .. P(title)];
        return [.. x, .. (x.Length % 2 == 1 ? new byte[1] : []), .. W(0)];
    }

    private static byte[] Alrt(int t, int l, int b, int r, int ditl, int stages) => [.. W(t, l, b, r, ditl, stages), .. W(0)];

    private static byte[] Cntl(int t, int l, int b, int r, int value, int max, int min, int proc, string title) =>
        [.. W(t, l, b, r, value), 1, 0, .. W(max, min, proc), .. L(0), .. P(title)];

    private static byte[] Ctab(params (int V, int R, int G, int B)[] e) => [.. L(0), .. W(0, e.Length - 1), .. e.SelectMany(x => W(x.V, x.R, x.G, x.B))];

    /// <summary>The dialogs the reference captures were made from (ClassicMac's own resources).</summary>
    internal static ResourceFork Fork()
    {
        var fork = new ResourceFork();
        void Add(string type, short id, byte[] data) => fork.Add(new Resource(FourCC.FromString(type), id, data));
        var icon = new byte[128];
        for (int y = 0; y < 32; y++)
        {
            uint row = y is 0 or 31 ? 0xFFFFFFFF : 0x80000001u | (1u << (31 - y)) | (1u << y);
            icon[y * 4] = (byte)(row >> 24);
            icon[y * 4 + 1] = (byte)(row >> 16);
            icon[y * 4 + 2] = (byte)(row >> 8);
            icon[y * 4 + 3] = (byte)row;
        }
        Add("ICON", 128, icon);
        var px = new byte[64 * 40 * 4];
        for (int i = 0; i < 64 * 40; i++)
        {
            px[i * 4] = (byte)(i % 64 * 4);
            px[i * 4 + 1] = (byte)(i / 64 * 6);
            px[i * 4 + 2] = 128;
            px[i * 4 + 3] = 255;
        }
        Add("PICT", 128, ImageImport.WritePicture(new RgbaBitmap(64, 40, px)));
        // The goAway flags are written as words, so the byte the Toolbox reads is 0: no close box (as captured).
        Add("DLOG", 128, Dlog(60, 40, 260, 400, 1, false, 128, ""));
        Add("DITL", 128, Ditl(
            Item(4, 160, 270, 180, 340, Text("OK")),
            Item(4, 160, 180, 180, 250, Text("Cancel")),
            Item(8, 10, 60, 42, 350, Text("Static text that wraps over two lines in the Dialog Manager's TextEdit.")),
            Item(16, 55, 63, 71, 247, Text("Edit text")),
            Item(5, 85, 60, 101, 200, Text("Check box")),
            Item(6, 105, 60, 121, 200, Text("Radio one")),
            Item(6, 125, 60, 141, 200, Text("Radio two")),
            Item(32, 10, 15, 42, 47, W(128)),
            Item(0, 85, 220, 141, 340, []),
            Item(8 + 128, 145, 220, 157, 340, Text("Disabled text"))));
        Add("DLOG", 129, Dlog(50, 30, 230, 330, 0, false, 129, "Document Dialog"));
        Add("DITL", 129, Ditl(
            Item(4, 145, 215, 165, 285, Text("Done")),
            Item(64, 10, 10, 50, 74, W(128)),
            Item(7, 10, 280, 130, 296, W(300)),
            Item(7, 70, 10, 86, 200, W(301)),
            Item(8, 100, 10, 132, 260, Text("Picture, scroll bar and a CNTL check box."))));
        Add("CNTL", 300, Cntl(10, 280, 130, 296, 30, 100, 0, 16, ""));
        Add("CNTL", 301, Cntl(70, 10, 86, 200, 1, 1, 0, 1, "CNTL check box"));
        Add("DLOG", 130, Dlog(60, 40, 200, 340, 5, false, 130, "Movable"));
        Add("DITL", 130, Ditl(
            Item(4, 105, 215, 125, 285, Text("OK")),
            Item(8, 10, 10, 60, 285, Text("A coloured dialog: dctb content light yellow, text dark blue.")),
            Item(5, 70, 10, 86, 200, Text("Check box"))));
        Add("dctb", 130, Ctab((0, 0xFFFF, 0xFFFF, 0xCCCC), (1, 0, 0, 0), (2, 0, 0, 0x8000), (3, 0, 0, 0), (4, 0xFFFF, 0xFFFF, 0xFFFF)));
        Add("DLOG", 131, Dlog(60, 40, 200, 340, 5, false, 131, "Appearance"));
        Add("DITL", 131, Ditl(
            Item(4, 105, 215, 125, 285, Text("OK")),
            Item(8, 10, 10, 60, 285, Text("dlgx: theme background and an embedding hierarchy.")),
            Item(5, 70, 10, 86, 200, Text("Check box"))));
        Add("dlgx", 131, [.. W(0), .. L(3)]);
        Add("DLOG", 132, Dlog(60, 40, 140, 300, 2, false, 132, ""));
        Add("DITL", 132, Ditl(Item(8, 10, 10, 40, 250, Text("plainDBoxProc (2)."))));
        Add("DLOG", 133, Dlog(60, 40, 140, 300, 3, false, 133, ""));
        Add("DITL", 133, Ditl(Item(8, 10, 10, 40, 250, Text("altDBoxProc (3)."))));
        Add("ALRT", 200, Alrt(40, 40, 150, 380, 200, 0x4444));
        Add("DITL", 200, Ditl(Item(4, 80, 260, 100, 330, Text("OK")), Item(8, 10, 70, 60, 330, Text("An alert message, drawn beside the alert's icon."))));
        Add("ALRT", 201, Alrt(40, 40, 150, 380, 201, 0x5555));
        Add("DITL", 201, Ditl(Item(4, 80, 260, 100, 330, Text("OK")), Item(4, 80, 180, 100, 250, Text("Cancel")),
            Item(8, 10, 70, 60, 330, Text("Bold item 2 (stages $5555), with an actb."))));
        Add("actb", 201, Ctab((0, 0xEEEE, 0xEEEE, 0xFFFF), (1, 0, 0, 0), (2, 0x8000, 0, 0), (3, 0, 0, 0), (4, 0xFFFF, 0xFFFF, 0xFFFF)));
        return fork;
    }

    private static DialogDrawing Drawing(ResourceFork fork, string type, short id, DialogKind kind = DialogKind.Alert, IReadOnlyList<ResourceFork>? system = null)
    {
        var resource = fork.Find(FourCC.FromString(type), id)!;
        return DialogDrawings.Read(resource, resource.GetData(), fork, DecodeOptions.Default, ReadOptions.Default, [], kind, system)!;
    }

    private static RgbaColor Grey(int v) => new((byte)v, (byte)v, (byte)v);

    private static RgbaColor Hex(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ---- Frames ----

    [Fact]
    public void A_modal_dialog_has_the_platinum_frame_and_shadow()
    {
        var r = DialogRenderer.Render(Drawing(Fork(), "DLOG", 128));
        var b = r.Bitmap;
        Assert.Equal((373, 213, 6, 6), (b.Width, b.Height, r.ContentLeft, r.ContentTop));
        Assert.Equal(Grey(0), b[0, 0]);
        Assert.Equal(Grey(0xBB), b[1, 1]);
        Assert.Equal(Grey(0xFF), b[2, 2]);
        Assert.Equal(Grey(0x99), b[369, 100]);
        Assert.Equal(Grey(0x55), b[370, 100]);
        Assert.Equal(Grey(0), b[372, 100]);                // the shadow
        Assert.Equal(0, b[372, 0].A);                       // outside the structure
        Assert.Equal(0, b[0, 212].A);
        Assert.Equal(Grey(0xFF), b[200, 100]);             // white content
    }

    [Fact]
    public void Alerts_have_a_red_tinted_frame_the_theme_background_and_a_default_ring()
    {
        var b = DialogRenderer.Render(Drawing(Fork(), "ALRT", 201)).Bitmap;
        Assert.Equal((353, 123), (b.Width, b.Height));
        Assert.Equal(Hex(0xFF9999), b[1, 50]);
        Assert.Equal(Hex(0xFF6666), b[350, 50]);
        Assert.Equal(Grey(0xDD), b[100, 70]);                // not the actb's colour
        // OK (item 1, the stage 1 default) at (80, 260): its ring 3 pixels outside; Cancel has none.
        Assert.Equal(Grey(0), b[6 + 260 + 10, 6 + 80 - 3]);
        Assert.Equal(Grey(0xDD), b[6 + 180 + 10, 6 + 80 - 3]);
        Assert.Equal(Grey(0), b[6 + 180 + 10, 6 + 80]);
    }

    [Fact]
    public void Stop_alerts_draw_the_system_icon_when_a_fork_supplies_it_else_a_placeholder()
    {
        var fork = Fork();
        var placeholder = DialogRenderer.Render(Drawing(fork, "ALRT", 200, DialogKind.StopAlert)).Bitmap;
        Assert.Equal(Grey(0x88), placeholder[6 + 20, 6 + 10]);
        Assert.Equal(Grey(0xDD), placeholder[6 + 30, 6 + 20]);
        var system = new ResourceFork();
        system.Add(new Resource(FourCC.FromString("ICON"), 0, Enumerable.Repeat((byte)0xFF, 128).ToArray()));
        var drawn = DialogRenderer.Render(Drawing(fork, "ALRT", 200, DialogKind.StopAlert, [system])).Bitmap;
        Assert.Equal(Grey(0), drawn[6 + 30, 6 + 20]);
        var plain = DialogRenderer.Render(Drawing(fork, "ALRT", 200, DialogKind.Alert, [system])).Bitmap;
        Assert.Equal(Grey(0xDD), plain[6 + 30, 6 + 20]);    // Alert() has no icon
    }

    [Fact]
    public void Movable_and_document_windows_have_title_bars()
    {
        var fork = Fork();
        var movable = DialogRenderer.Render(Drawing(fork, "DLOG", 130));
        Assert.Equal((313, 174, 6, 27), (movable.Bitmap.Width, movable.Bitmap.Height, movable.ContentLeft, movable.ContentTop));
        Assert.Equal(Grey(0xFF), movable.Bitmap[20, 4]);    // the stripes
        Assert.Equal(Grey(0x77), movable.Bitmap[20, 5]);
        Assert.Equal(Grey(0xCC), movable.Bitmap[156, 4]);   // cleared for the title
        Assert.Equal(Hex(0xFFFFCC), movable.Bitmap[100, 100]); // the dctb's content colour
        var themed = DialogRenderer.Render(Drawing(fork, "DLOG", 131)).Bitmap;
        Assert.Equal(Grey(0xDD), themed[100, 100]);         // dlgx: the theme background

        var document = DialogRenderer.Render(Drawing(fork, "DLOG", 129));
        Assert.Equal((313, 209, 6, 22), (document.Bitmap.Width, document.Bitmap.Height, document.ContentLeft, document.ContentTop));
        Assert.Equal(Grey(0x88), document.Bitmap[295, 4]);  // the collapse box
        Assert.Equal(Grey(0), document.Bitmap[5, 100]);     // the line around the content
    }

    [Fact]
    public void Plain_and_alternate_boxes_have_one_pixel_frames()
    {
        var fork = Fork();
        var plain = DialogRenderer.Render(Drawing(fork, "DLOG", 132)).Bitmap;
        Assert.Equal((262, 82), (plain.Width, plain.Height));
        Assert.Equal(Grey(0), plain[261, 40]);
        var alt = DialogRenderer.Render(Drawing(fork, "DLOG", 133)).Bitmap;
        Assert.Equal((264, 84), (alt.Width, alt.Height));
        Assert.Equal((Grey(0), Grey(0), Grey(0)), (alt[261, 40], alt[263, 40], alt[100, 83]));
        Assert.Equal(0, alt[263, 0].A);
    }

    // ---- Items ----

    [Fact]
    public void Buttons_check_boxes_and_edit_text_are_drawn_in_their_items()
    {
        var b = DialogRenderer.Render(Drawing(Fork(), "DLOG", 128)).Bitmap;
        // Cancel at (160, 180, 180, 250): the rounded outline, the bevel and the fill.
        int x = 6 + 180, y = 6 + 160;
        Assert.Equal(Grey(0xFF), b[x, y]);                 // the corner shows the content
        Assert.Equal(Hex(0x222222), b[x + 2, y]);
        Assert.Equal(Grey(0), b[x, y + 10]);
        Assert.Equal(Grey(0xDD), b[x + 3, y + 10]);
        Assert.Equal(Grey(0x77), b[x + 68, y + 10]);
        // The check box at (85, 60): 12 x 12, 2 pixels in, 2 down.
        Assert.Equal(Grey(0), b[6 + 62, 6 + 87]);
        Assert.Equal(Grey(0xDD), b[6 + 66, 6 + 92]);
        Assert.Equal(Grey(0x88), b[6 + 72, 6 + 97]);
        // The edit text frame 3 pixels outside (55, 63, 71, 247).
        Assert.Equal(Grey(0), b[6 + 100, 6 + 52]);
        Assert.Equal(Grey(0), b[6 + 249, 6 + 60]);
        // The icon, framed.
        Assert.Equal(Grey(0), b[6 + 15, 6 + 10]);
    }

    [Fact]
    public void Scroll_bars_place_the_thumb_by_the_value_with_the_arrows_together()
    {
        var b = DialogRenderer.Render(Drawing(Fork(), "DLOG", 129)).Bitmap;
        // CNTL 300 at (10, 280, 130, 296), value 30 of 0-100: the thumb's top line 22 pixels down the track.
        int x = 6 + 280, y = 22 + 10;
        Assert.Equal(Grey(0), b[x + 8, y + 22]);
        Assert.Equal(Hex(0xCCCCFF), b[x + 2, y + 23]);
        Assert.Equal(Grey(0xAA), b[x + 8, y + 10]);         // the track
        Assert.Equal(Grey(0), b[x + 7, y + 95]);             // the up arrow's triangle
        Assert.Equal(Grey(0), b[x + 7, y + 113]);            // the down arrow's
        // The checked CNTL check box at (70, 10).
        Assert.Equal(Grey(0), b[6 + 12, 22 + 77]);
        Assert.Equal(Grey(0), b[6 + 12 + 2, 22 + 77]);      // the check mark
    }

    [Fact]
    public void An_eight_bit_screen_keeps_the_platinum_greys()
    {
        var fork = Fork();
        var full = DialogRenderer.Render(Drawing(fork, "ALRT", 200)).Bitmap;
        var eight = DialogRenderer.Render(Drawing(fork, "ALRT", 200), new DialogRenderOptions { ScreenDepth = 8 }).Bitmap;
        Assert.Equal(full.Pixels, eight.Pixels);
    }

    [Fact]
    public void Text_goes_through_the_fallback_at_the_system_fonts_baselines()
    {
        var fallback = new BlockText();
        DialogRenderer.Render(Drawing(Fork(), "DLOG", 130), new DialogRenderOptions { TextFallback = fallback });
        Assert.Contains("Movable", fallback.Texts);
        Assert.Contains("OK", fallback.Texts);
        var b = DialogRenderer.Render(Drawing(Fork(), "DLOG", 130), new DialogRenderOptions { TextFallback = fallback }).Bitmap;
        // "Movable": 7 characters of 5 pixels, centred on the structure; the text's cells end on the baseline (row 15).
        int pen = (313 - 35) / 2;
        Assert.Equal(Grey(0), b[pen, 14]);
        Assert.Equal(Grey(0xCC), b[pen, 15]);
    }

    // A fallback that draws each character as a 5 x 9 block on the baseline.
    private sealed class BlockText : ITextFallback
    {
        public List<string> Texts { get; } = [];

        public TextFallbackMask Render(string text, TextFallbackStyle style)
        {
            Texts.Add(text);
            int w = text.Length * 5;
            return new TextFallbackMask(w, 9, 0, 9, Enumerable.Repeat((byte)1, w * 9).ToArray(), w);
        }
    }

    // ---- Against Mac OS 9's screen ----

    /// <summary>
    /// Renders each captured job and compares the pixels outside text with the screen captures in the folder that
    /// CLASSICMAC_DIALOG_CAPTURES names (y7_&lt;id&gt;_k&lt;kind&gt;_d&lt;depth&gt;.qdr with the structure mask *_m.qdr); skipped without it.
    /// </summary>
    [Fact]
    public void Matches_the_captures_outside_text()
    {
        var folder = Environment.GetEnvironmentVariable("CLASSICMAC_DIALOG_CAPTURES");
        Assert.SkipWhen(string.IsNullOrEmpty(folder) || !Directory.Exists(folder), "CLASSICMAC_DIALOG_CAPTURES is not set.");
        var fork = Fork();
        (short Id, int Kind)[] jobs = [(128, 0), (129, 0), (130, 0), (131, 0), (132, 0), (133, 0), (200, 2), (200, 3), (200, 4), (200, 5), (201, 2), (201, 4)];
        long total = 0, same = 0;
        foreach (var depth in new[] { 32, 8 })
        {
            foreach (var (id, kind) in jobs)
            {
                var path = Path.Combine(folder!, $"y7_{id}_k{kind}_d{depth}.qdr");
                if (!File.Exists(path))
                {
                    continue;
                }

                var screen = Capture.Read(path);
                var mask = Capture.Read(Path.Combine(folder!, $"y7_{id}_k{kind}_d{depth}_m.qdr"));
                var dialogKind = kind switch { 3 => DialogKind.StopAlert, 4 => DialogKind.NoteAlert, 5 => DialogKind.CautionAlert, _ => DialogKind.Alert };
                var drawing = Drawing(fork, id >= 200 ? "ALRT" : "DLOG", id, dialogKind);
                var ours = DialogRenderer.Render(drawing, new DialogRenderOptions { ScreenDepth = depth });
                var masked = TextAreas(drawing, ours);
                if (Environment.GetEnvironmentVariable("CLASSICMAC_DIALOG_OUT") is { Length: > 0 } outFolder)
                {
                    File.WriteAllBytes(Path.Combine(outFolder, $"ours_{id}_k{kind}_d{depth}.png"),
                        PngEncoder.Instance.Encode(ours.Bitmap.Width, ours.Bitmap.Height, ours.Bitmap.Pixels));
                }

                int n = 0, ok = 0;
                for (int y = 0; y < screen.Height; y++)
                {
                    for (int x = 0; x < screen.Width; x++)
                    {
                        if (mask[x, y] != Grey(0) || masked.Contains(x, y))
                        {
                            continue;
                        }
                        // The alert icons are the System's, not in the test fork.
                        if (kind >= 3 && x >= 26 && x < 58 && y >= 16 && y < 48)
                        {
                            continue;
                        }

                        n++;
                        if (x < ours.Bitmap.Width && y < ours.Bitmap.Height && ours.Bitmap[x, y] == screen[x, y])
                        {
                            ok++;
                        }
                        else if (n - ok <= 5)
                        {
                            output.WriteLine($"  differs at ({x}, {y}): {ours.Bitmap[x, y]} vs {screen[x, y]}");
                        }
                    }
                }

                output.WriteLine($"{id} kind {kind} depth {depth}: {ok}/{n} ({100.0 * ok / Math.Max(1, n):F2}%) of the pixels outside text match; " +
                    $"size {ours.Bitmap.Width}x{ours.Bitmap.Height} vs {screen.Width}x{screen.Height}");
                Assert.Equal((screen.Width, screen.Height), (ours.Bitmap.Width, ours.Bitmap.Height));
                total += n;
                same += ok;
            }
        }

        output.WriteLine($"All: {same}/{total} ({100.0 * same / Math.Max(1, total):F2}%)");
        Assert.True(same >= total * 0.99, $"{same}/{total} match");
    }

    // Where text is drawn, in structure coordinates: the text items, the buttons' and check boxes' titles, the title bar's gap.
    private static Region TextAreas(DialogDrawing drawing, DialogRendering rendering)
    {
        var region = Region.Empty;
        void Add(int top, int left, int bottom, int right) =>
            region = region.Union(Region.FromRect(new MacRect((short)top, (short)left, (short)bottom, (short)right)));
        int dx = rendering.ContentLeft, dy = rendering.ContentTop;
        foreach (var entry in drawing.Items)
        {
            var r = entry.Item.Bounds;
            switch (entry.Item.Type)
            {
                case 8 or 16:
                    Add(dy + r.Top, dx + r.Left, dy + r.Bottom + 4, dx + r.Right);
                    break;
                case 4:
                    Add(dy + r.Top + 4, dx + r.Left + 4, dy + r.Bottom - 4, dx + r.Right - 4);
                    break;
                case 5 or 6 or 7 when entry.Item.Type != 7 || entry.Control?.Definition == 1:
                    Add(dy + r.Top, dx + r.Left + 16, dy + r.Bottom, dx + r.Right);
                    break;
            }
        }
        if (drawing.Kind == DialogKind.Dialog && drawing.Definition is 0 or 5)
        {
            Add(4, 5, 20, rendering.Bitmap.Width - (drawing.Definition == 0 ? 24 : 7));
        }

        return region;
    }

    // A screen capture: 'QDR1', width, height, depth, rowBytes, colour count, error, ... (24 bytes), the colour table
    // (value, red, green, blue), then the pixels.
    private sealed class Capture
    {
        private readonly RgbaColor[] pixels;

        private Capture(int width, int height, RgbaColor[] pixels) => (Width, Height, this.pixels) = (width, height, pixels);

        public int Width { get; }

        public int Height { get; }

        public RgbaColor this[int x, int y] => pixels[y * Width + x];

        public static Capture Read(string path)
        {
            var reader = new BigEndianReader(File.ReadAllBytes(path));
            int w = reader.ReadInt16At(4), h = reader.ReadInt16At(6), depth = reader.ReadInt16At(8), rowBytes = reader.ReadInt16At(10),
                count = reader.ReadInt16At(12);
            var clut = new RgbaColor[count];
            for (int i = 0; i < count; i++)
            {
                clut[i] = new RgbaColor((byte)(reader.ReadUInt16At(24 + 8 * i + 2) >> 8), (byte)(reader.ReadUInt16At(24 + 8 * i + 4) >> 8),
                    (byte)(reader.ReadUInt16At(24 + 8 * i + 6) >> 8));
            }

            int start = 24 + 8 * count;
            var px = new RgbaColor[w * h];
            var data = reader.Source.Span;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int o = start + y * rowBytes;
                    px[y * w + x] = depth == 32
                        ? new RgbaColor(data[o + 4 * x + 1], data[o + 4 * x + 2], data[o + 4 * x + 3])
                        : clut[(data[o + x * depth / 8] >> (8 - depth - x * depth % 8)) & ((1 << depth) - 1)];
                }
            }

            return new Capture(w, h, px);
        }
    }
}
