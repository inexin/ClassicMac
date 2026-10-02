using System;
using System.Collections.Generic;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Graphics.Pict;
using ClassicMac.Graphics.QuickDraw;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.Resources.Decoders.Interface
{
    /// <summary>How <see cref="DialogRenderer"/> draws.</summary>
    public sealed class DialogRenderOptions
    {
        /// <summary>The defaults: a 32-bit screen, no fonts.</summary>
        public static DialogRenderOptions Default { get; } = new();

        /// <summary>The screen depth: 32 (the default) or 1, 2, 4, 8, 16, through QuickDraw's colour matching.</summary>
        public int ScreenDepth { get; init; } = 32;

        /// <summary>
        /// Bitmap fonts for the text: the system font (Charcoal on Mac OS 9) when the library has it. Text no strike
        /// draws goes to <see cref="TextFallback"/>.
        /// </summary>
        public FontLibrary? Fonts { get; init; }

        /// <summary>Rasterizes text no bitmap font draws; null draws no such text.</summary>
        public ITextFallback? TextFallback { get; init; }
    }

    /// <summary>A drawn dialog: the window's structure (frame, shadow and content) and where the content is in it.</summary>
    /// <param name="Bitmap">The structure region's bounding box; pixels outside the region are transparent.</param>
    /// <param name="ContentLeft">The content's left edge in the bitmap.</param>
    /// <param name="ContentTop">The content's top edge in the bitmap.</param>
    public sealed record DialogRendering(RgbaBitmap Bitmap, int ContentLeft, int ContentTop);

    /// <summary>
    /// Draws dialogs and alerts as Mac OS 9.0 with Appearance 1.1.1 and the Platinum theme draws them
    /// (docs/formats/resources/windows-dialogs.md §5.3): the window's frame for its definition, the content colour, then
    /// each item, all through a <see cref="QuickDrawPort"/>. Frames and controls are the theme's pixels; text is drawn
    /// with the fonts given (the system font is Charcoal 12), so it matches only when they are the Mac's.
    /// </summary>
    public static class DialogRenderer
    {
        // Charcoal 12's metrics, which place every line of text [Verified: baselines 12 below a static text item's top,
        // lines 16 apart, in the captures].
        private const int Ascent = 12, Descent = 3, LineHeight = 16;

        /// <summary>Draws <paramref name="drawing"/>.</summary>
        public static DialogRendering Render(DialogDrawing drawing, DialogRenderOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(drawing);
            options ??= DialogRenderOptions.Default;
            var (art, left, top, right, bottom) = Frame(drawing);
            int width = drawing.Width + left + right, height = drawing.Height + top + bottom;
            var canvas = new RgbaBitmap(width, height);
            var port = new QuickDrawPort(canvas, new QuickDrawOptions
            {
                ScreenDepth = options.ScreenDepth, Version = QuickDrawVersion.MacOS9, Fonts = options.Fonts, TextFallback = options.TextFallback,
            });
            var content = drawing.IsAlert || drawing.ThemeBackground ? PlatinumArt.Background : drawing.Content ?? RgbColor.White;
            var text = new TextTools(port, options);

            // The structure: the frame with the content colour inside.
            if (art is not null) art.Paint(port, 0, 0, width, height, content);
            else
            {
                port.ForeColor = content;
                port.PaintRect(PlatinumArt.Rect(top, left, top + drawing.Height, left + drawing.Width));
                port.ForeColor = RgbColor.Black;
                port.FrameRect(PlatinumArt.Rect(0, 0, height - bottom + 1, width - right + 1));
                if (drawing.Definition == 3 && drawing.Kind == DialogKind.Dialog)
                {
                    // altDBoxProc: a 2-pixel shadow right and below, 2 pixels down and in [Verified].
                    port.PaintRect(PlatinumArt.Rect(2, width - 2, height, width));
                    port.PaintRect(PlatinumArt.Rect(height - 2, 2, height, width));
                }
            }
            if (art == PlatinumArt.DocumentFrame || art == PlatinumArt.MovableFrame) TitleBar(port, text, drawing, art, width);

            // The content, in local coordinates, clipped to the window's port.
            port.SetOrigin(-left, -top);
            var portRect = PlatinumArt.Rect(0, 0, drawing.Height, drawing.Width);
            port.Clip = Region.FromRect(portRect);
            if (drawing.IsAlert && drawing.Kind != DialogKind.Alert) AlertIcon(port, drawing.AlertIcon, options);
            for (var i = 0; i < drawing.Items.Count; i++)
                Item(port, text, drawing.Items[i], drawing.IsAlert && i + 1 == drawing.DefaultItem, content, portRect, options);
            return new DialogRendering(canvas, left, top);
        }

        /// <summary>
        /// The frame art and the structure's margins around the content for the window definition [Verified: the
        /// captures' structure and content rectangles].
        /// </summary>
        private static (PlatinumArt? Art, int Left, int Top, int Right, int Bottom) Frame(DialogDrawing drawing)
        {
            if (drawing.IsAlert) return (PlatinumArt.AlertFrame, 6, 6, 7, 7);
            if (drawing.Kind == DialogKind.ItemList) return (null, 1, 1, 1, 1);
            return drawing.Definition switch
            {
                1 => (PlatinumArt.DialogFrame, 6, 6, 7, 7),
                5 => (PlatinumArt.MovableFrame, 6, 27, 7, 7),
                // documentProc, noGrowDocProc, the zoom variants and rDocProc are all drawn as a document window
                // [ClassicMac: only documentProc is in the captures; no zoom box, no rounded corners].
                0 or 4 or 8 or 12 or (>= 16 and <= 23) => (PlatinumArt.DocumentFrame, 6, 22, 7, 7),
                3 => (null, 1, 1, 3, 3),
                // plainDBox, and other definitions [ClassicMac] as it.
                _ => (null, 1, 1, 1, 1),
            };
        }

        // The title, centred on the structure in a gap cleared in the stripes, and the close box.
        private static void TitleBar(QuickDrawPort port, TextTools text, DialogDrawing drawing, PlatinumArt art, int width)
        {
            bool document = art == PlatinumArt.DocumentFrame;
            int stripesRight = width - (document ? 24 : 7);
            port.ForeColor = PlatinumArt.TitleBar;
            if (document && drawing.GoAway)
            {
                // [ClassicMac: not in the captures] the close box mirrors the collapse box.
                port.PaintRect(PlatinumArt.Rect(4, 5, 16, 24));
                PlatinumArt.CloseBox.Paint(port, 6, 4, PlatinumArt.TitleBar);
            }
            if (drawing.Title.Length == 0) return;
            // The gap: 5 pixels before the text's pen position to 3 after its width; the pen at half the structure's width
            // less the text's [Verified: three titles].
            int textWidth = text.Width(drawing.Title);
            int pen = (width - textWidth) / 2;
            port.ForeColor = PlatinumArt.TitleBar;
            port.PaintRect(PlatinumArt.Rect(4, Math.Max(5, pen - 5), 16, Math.Min(stripesRight, pen + textWidth + 3)));
            port.Clip = Region.FromRect(PlatinumArt.Rect(1, 1, 20, stripesRight));
            text.Draw(drawing.Title, pen, 15);
            port.Clip = null;
        }

        private static void AlertIcon(QuickDrawPort port, DialogImage? icon, DialogRenderOptions options)
        {
            // The icon goes at (10, 20, 42, 52) in the alert [Verified].
            var rect = new MacRect(10, 20, 42, 52);
            if (icon is not null && Image(port, icon, rect, isIcon: true)) return;
            // Without the System's icon, a neutral placeholder [ClassicMac].
            port.ForeColor = new RgbColor(0x8888, 0x8888, 0x8888);
            port.FrameRect(rect);
            port.ForeColor = RgbColor.Black;
        }

        private static void Item(QuickDrawPort port, TextTools text, DialogDrawingItem entry, bool isDefault, RgbColor content, MacRect portRect,
            DialogRenderOptions options)
        {
            var item = entry.Item;
            var r = item.Bounds;
            switch (item.Type)
            {
                case 4:
                    PushButton(port, text, r, item.Text ?? "", isDefault);
                    break;
                case 5:
                    CheckBox(port, text, r, item.Text ?? "", PlatinumArt.CheckBox, false);
                    break;
                case 6:
                    CheckBox(port, text, r, item.Text ?? "", PlatinumArt.RadioButton, false);
                    break;
                case 7:
                    Control(port, text, r, entry.Control);
                    break;
                case 8:
                    text.Box(item.Text ?? "", r, portRect);
                    break;
                case 16:
                    // Editable text: a 1-pixel frame 3 pixels outside the item [Verified], the text inside.
                    port.ForeColor = RgbColor.Black;
                    port.FrameRect(PlatinumArt.Rect(r.Top - 3, r.Left - 3, r.Bottom + 3, r.Right + 3));
                    text.Box(item.Text ?? "", r, portRect);
                    break;
                case 32 or 64:
                    if (entry.Image is { } image) Image(port, image, r, item.Type == 32);
                    break;
                    // User items (0) and help items (1) draw nothing [Verified: a user item].
            }
        }

        private static void PushButton(QuickDrawPort port, TextTools text, MacRect r, string title, bool isDefault)
        {
            if (isDefault) PlatinumArt.DefaultButton.Paint(port, r.Left - 3, r.Top - 3, r.Width + 6, r.Height + 6, PlatinumArt.Background);
            else PlatinumArt.Button.Paint(port, r.Left, r.Top, r.Width, r.Height, PlatinumArt.Background);
            // The title centred, its baseline where a line of the system font is centred [Verified].
            int width = text.Width(title);
            text.Draw(title, r.Left + (r.Width - width) / 2, Baseline(r), r);
        }

        private static int Baseline(MacRect r) => r.Top + (r.Height - (Ascent + Descent)) / 2 + Ascent;

        // A check box or radio button: the 12 x 12 box 2 pixels in, centred vertically, the title 18 pixels in [Verified].
        private static void CheckBox(QuickDrawPort port, TextTools text, MacRect r, string title, PlatinumArt box, bool on)
        {
            int top = r.Top + (r.Height - 12) / 2;
            box.Paint(port, r.Left + 2, top, PlatinumArt.Background);
            if (on && box == PlatinumArt.RadioButton)
            {
                // [ClassicMac: not in the captures] an on radio button's dot.
                port.ForeColor = RgbColor.Black;
                port.PaintOval(PlatinumArt.Rect(top + 4, r.Left + 6, top + 8, r.Left + 10));
            }
            text.Draw(title, r.Left + 18, Baseline(r), r);
        }

        private static void Control(QuickDrawPort port, TextTools text, MacRect r, ControlTemplate? control)
        {
            if (control is null || !control.Visible)
            {
                if (control is null) Placeholder(port, r);
                return;
            }
            // The Dialog Manager puts the control in the item's rectangle. Classic IDs and their Appearance equivalents.
            switch (control.Definition)
            {
                case 0 or 8 or 368 or 376:
                    PushButton(port, text, r, control.Title, false);
                    break;
                case 1 or 9 or 369 or 377:
                    if (control.Value != 0) CheckBoxOn(port, text, r, control.Title);
                    else CheckBox(port, text, r, control.Title, PlatinumArt.CheckBox, false);
                    break;
                case 2 or 10 or 370 or 378:
                    CheckBox(port, text, r, control.Title, PlatinumArt.RadioButton, control.Value != 0);
                    break;
                case >= 16 and <= 31 or >= 384 and <= 387:
                    ScrollBar(port, r, control);
                    break;
                case >= 1008 and <= 1023 or >= 400 and <= 415:
                    Popup(port, text, r, control);
                    break;
                default:
                    Placeholder(port, r);
                    break;
            }
        }

        private static void CheckBoxOn(QuickDrawPort port, TextTools text, MacRect r, string title)
        {
            int top = r.Top + (r.Height - 12) / 2;
            PlatinumArt.CheckBoxOn.Paint(port, r.Left + 2, top, PlatinumArt.Background);
            text.Draw(title, r.Left + 18, Baseline(r), r);
        }

        // Something ClassicMac does not draw: a grey frame where it goes [ClassicMac].
        private static void Placeholder(QuickDrawPort port, MacRect r)
        {
            port.ForeColor = new RgbColor(0x8888, 0x8888, 0x8888);
            port.FrameRect(r);
            port.ForeColor = RgbColor.Black;
        }

        /// <summary>
        /// A scroll bar, the arrows together at the bottom (or right) end as Mac OS 9's default draws them, and the scroll
        /// box placed by the value [Verified: a vertical bar; a horizontal one is the same turned, ClassicMac].
        /// </summary>
        private static void ScrollBar(QuickDrawPort port, MacRect r, ControlTemplate control)
        {
            bool vertical = r.Height >= r.Width;
            int start = vertical ? r.Top : r.Left, end = vertical ? r.Bottom : r.Right, across = vertical ? r.Width : r.Height;
            int origin = vertical ? r.Left : r.Top;
            var (track, thumb, up, down) = vertical
                ? (PlatinumArt.Track, PlatinumArt.Thumb, PlatinumArt.UpArrow, PlatinumArt.DownArrow)
                : (Turned.Track, Turned.Thumb, Turned.UpArrow, Turned.DownArrow);
            void Paint(PlatinumArt art, int at, int length)
            {
                if (length <= 0) return;
                if (vertical) art.Paint(port, origin, at, across, length, PlatinumArt.Background);
                else art.Paint(port, at, origin, length, across, PlatinumArt.Background);
            }
            int arrows = end - 31;                                  // the up arrow's top line
            if (arrows < start)
            {
                Paint(track, start, end - start);
                return;
            }
            int range = arrows - start - 16;
            if (control.Maximum > control.Minimum && range >= 0)
            {
                // The thumb's top line: (value - min) / (max - min) of the track less the thumb, rounded [Verified: one value].
                long value = Math.Clamp((int)control.Value, control.Minimum, control.Maximum) - control.Minimum;
                long span = control.Maximum - control.Minimum;
                int at = start + (int)((2 * value * range + span) / (2 * span));
                Paint(track, start, at - start + 1);
                Paint(track, at + 16, arrows - at - 15);
                Paint(thumb, at, 17);
            }
            else Paint(track, start, arrows - start + 1);
            Paint(up, arrows, 16);
            Paint(down, end - 16, 16);
        }

        private static class Turned
        {
            public static readonly PlatinumArt Track = PlatinumArt.Track.Transposed(), Thumb = PlatinumArt.Thumb.Transposed(),
                UpArrow = PlatinumArt.UpArrow.Transposed(), DownArrow = PlatinumArt.DownArrow.Transposed();
        }

        /// <summary>
        /// A pop-up menu [ClassicMac: not in the captures]: the title in its width, then a button with a down triangle.
        /// </summary>
        private static void Popup(QuickDrawPort port, TextTools text, MacRect r, ControlTemplate control)
        {
            int titleWidth = Math.Clamp((int)control.Maximum, 0, r.Width);
            if (titleWidth > 0 && control.Title.Length > 0) text.Draw(control.Title, r.Left, Baseline(r), PlatinumArt.Rect(r.Top, r.Left, r.Bottom, r.Left + titleWidth));
            int left = r.Left + titleWidth;
            if (r.Right - left < 24) return;
            PlatinumArt.Button.Paint(port, left, r.Top, r.Right - left, r.Height, PlatinumArt.Background);
            port.ForeColor = RgbColor.Black;
            int cx = r.Right - 12, cy = r.Top + r.Height / 2 - 2;
            for (int i = 0; i < 4; i++) port.PaintRect(PlatinumArt.Rect(cy + i, cx - 4 + i, cy + i + 1, cx + 3 - i));
        }

        // An icon or a picture item.
        private static bool Image(QuickDrawPort port, DialogImage image, MacRect r, bool isIcon)
        {
            try
            {
                switch (image.Type.ToString())
                {
                    case "ICON" when image.Data.Length >= 128:
                        // The icon at 32 x 32 from the item's top-left [ClassicMac: the item is the icon's size in practice].
                        port.ForeColor = RgbColor.Black;
                        port.BackColor = RgbColor.White;
                        IconSuite.PlotIcon(port, new MacRect(r.Top, r.Left, (short)(r.Top + 32), (short)(r.Left + 32)), image.Data);
                        return true;
                    case "cicn":
                    {
                        var bitmap = QuickDrawResources.DecodeCicn(image.Data);
                        var bounds = new MacRect(0, 0, (short)bitmap.Height, (short)bitmap.Width);
                        var at = new MacRect(r.Top, r.Left, (short)(r.Top + bitmap.Height), (short)(r.Left + bitmap.Width));
                        port.CopyBits(PixMap.FromBitmap(bitmap), bounds, at, TransferMode.SrcCopy, Mask(bitmap).Offset(r.Left, r.Top));
                        return true;
                    }
                    case "PICT" when !isIcon:
                        port.DrawPicture(image.Data, r);
                        return true;
                }
            }
            catch (Exception e) when (e is ArgumentException or System.IO.EndOfStreamException or NotSupportedException or InvalidOperationException)
            {
                // A damaged image draws nothing.
            }
            return false;
        }

        // The opaque pixels of a decoded colour icon, as a region at (0, 0).
        private static Region Mask(RgbaBitmap bitmap)
        {
            int rowBytes = (bitmap.Width + 15) / 16 * 2;
            var bits = new byte[rowBytes * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                    if (bitmap.Pixels[(y * bitmap.Width + x) * 4 + 3] >= 128) bits[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
            return Region.FromBitMap(PixMap.FromBitMap(bits, rowBytes, new MacRect(0, 0, (short)bitmap.Height, (short)bitmap.Width)));
        }

        /// <summary>Text in the system font, black, measured with what draws it.</summary>
        private sealed class TextTools(QuickDrawPort port, DialogRenderOptions options)
        {
            public int Width(string text)
            {
                if (text.Length == 0) return 0;
                Setup();
                if (port.StringWidth(text) is > 0 and var width) return width;
                if (options.TextFallback?.Render(text, new TextFallbackStyle(0, 0, 12)) is { } mask) return (int)Math.Round(mask.Advance);
                return text.Length * 7;                             // [ClassicMac: Charcoal 12's average width, for layout without fonts]
            }

            private void Setup()
            {
                port.TextFont = 0;
                port.TextSize = 12;
                port.TextFace = 0;
                port.TextMode = TransferMode.SrcOr;
                port.ForeColor = RgbColor.Black;
            }

            // One line at a pen position, clipped to a rectangle within the port's clip.
            public void Draw(string text, int h, int v, MacRect? clip = null)
            {
                if (text.Length == 0) return;
                var saved = port.Clip;
                if (clip is { } c) port.Clip = saved is null ? Region.FromRect(c) : saved.Intersect(Region.FromRect(c));
                Setup();
                port.MoveTo(h, v);
                port.DrawString(text);
                port.Clip = saved;
            }

            /// <summary>
            /// Static or editable text as TextEdit lays it out in the item: left-aligned lines broken at spaces to fit the
            /// width (a return always breaks), clipped to the item [ClassicMac: TextEdit's line breaks are approximated].
            /// </summary>
            public void Box(string text, MacRect r, MacRect portRect)
            {
                int v = r.Top + Ascent;
                foreach (var line in Lines(text, r.Width))
                {
                    if (v - Ascent >= Math.Min(r.Bottom, portRect.Bottom)) break;
                    Draw(line, r.Left, v, r);
                    v += LineHeight;
                }
            }

            private IEnumerable<string> Lines(string text, int width)
            {
                foreach (var paragraph in text.Replace("\r\n", "\r").Split('\r', '\n'))
                {
                    var line = "";
                    int i = 0;
                    while (i < paragraph.Length)
                    {
                        // The next word with the spaces after it.
                        int end = i;
                        while (end < paragraph.Length && paragraph[end] != ' ') end++;
                        while (end < paragraph.Length && paragraph[end] == ' ') end++;
                        var word = paragraph[i..end];
                        if (line.Length > 0 && Width((line + word).TrimEnd()) > width)
                        {
                            yield return line;
                            line = "";
                        }
                        line += word;
                        i = end;
                    }
                    yield return line;
                }
            }
        }
    }
}
