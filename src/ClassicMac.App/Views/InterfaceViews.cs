using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassicMac.App.ViewModels;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Views
{
    // What the interface previews share: the system font (Chicago 12, else a close relative), black and grey pens.
    internal static class MacLook
    {
        public static readonly Typeface System = new(new FontFamily("Chicago, Charcoal, Arial, Helvetica, sans-serif"));
        public static readonly Typeface SystemBold = new(new FontFamily("Chicago, Charcoal, Arial, Helvetica, sans-serif"), FontStyle.Normal, FontWeight.Bold);
        public const double Size = 12;
        public static readonly IBrush Black = Brushes.Black;
        public static readonly IBrush White = Brushes.White;
        public static readonly IBrush Grey = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        public static readonly Pen Line = new(Black, 1);
        public static readonly Pen Dotted = new(Grey, 1, DashStyle.Dot);

        public static FormattedText Text(string text, IBrush? brush = null, Typeface? typeface = null, double? maxWidth = null, TextAlignment alignment = TextAlignment.Left)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface ?? System, Size, brush ?? Black)
            {
                TextAlignment = alignment,
            };
            if (maxWidth is { } width) formatted.MaxTextWidth = Math.Max(1, width);
            return formatted;
        }

        // A 1-pixel frame inside a rectangle (QuickDraw's FrameRect).
        public static void Frame(DrawingContext context, Rect rect, Pen? pen = null) => context.DrawRectangle(null, pen ?? Line, rect.Deflate(0.5));
    }

    /// <summary>
    /// A dialog or alert drawn in the System 7 style from its template and items [ClassicMac: an approximation, drawn by the
    /// viewer, not by the Toolbox]: the window frame for its definition, then each item in its rectangle.
    /// </summary>
    internal sealed class DialogView : Control
    {
        public static readonly StyledProperty<DialogPreview?> DialogProperty = AvaloniaProperty.Register<DialogView, DialogPreview?>(nameof(Dialog));

        public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<DialogView, double>(nameof(Scale), 1);

        private const double TitleBar = 19, Gutter = 12;

        static DialogView()
        {
            AffectsMeasure<DialogView>(DialogProperty, ScaleProperty);
            AffectsRender<DialogView>(DialogProperty, ScaleProperty);
        }

        public DialogView() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);

        public DialogPreview? Dialog
        {
            get => GetValue(DialogProperty);
            set => SetValue(DialogProperty, value);
        }

        public double Scale
        {
            get => GetValue(ScaleProperty);
            set => SetValue(ScaleProperty, value);
        }

        private bool HasTitleBar => Dialog?.Definition is 0 or 4 or 5 or 8 or 12 or (>= 16 and <= 23);

        private double Border => Dialog?.Definition switch { 1 or 5 => 6, 3 => 3, _ => 1 };

        protected override Size MeasureOverride(Size availableSize) => Dialog is { } d
            ? new Size((d.Width + 2 * Border + 2 * Gutter) * Scale, (d.Height + 2 * Border + (HasTitleBar ? TitleBar : 0) + 2 * Gutter) * Scale)
            : default;

        public override void Render(DrawingContext context)
        {
            if (Dialog is not { } dialog) return;
            using var scale = context.PushTransform(Matrix.CreateScale(Scale, Scale));
            var border = Border;
            var top = HasTitleBar ? TitleBar : 0;
            var outer = new Rect(Gutter, Gutter, dialog.Width + 2 * border, dialog.Height + 2 * border + top);
            DrawFrame(context, dialog, outer, border, top);
            using var content = context.PushTransform(Matrix.CreateTranslation(Gutter + border, Gutter + border + top));
            using var clip = context.PushClip(new Rect(0, 0, dialog.Width, dialog.Height));
            for (var i = 0; i < dialog.Items.Count; i++) DrawItem(context, dialog.Items[i], i + 1 == dialog.DefaultItem);
        }

        private static void DrawFrame(DrawingContext context, DialogPreview dialog, Rect outer, double border, double top)
        {
            if (dialog.Definition == 3) context.FillRectangle(MacLook.Black, outer.Translate(new Vector(2, 2))); // altDBox's shadow
            context.FillRectangle(dialog.Background is { } c ? new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue)) : MacLook.White, outer);
            MacLook.Frame(context, outer);
            if (dialog.Definition is 1 or 5)
            {
                // The modal dialog box's double border: a thick inner frame inside the thin outer one.
                MacLook.Frame(context, new Rect(outer.X + 3, outer.Y + 3 + top, outer.Width - 6, outer.Height - 6 - top), new Pen(MacLook.Black, 2));
            }
            if (top > 0)
            {
                var bar = new Rect(outer.X, outer.Y, outer.Width, top);
                MacLook.Frame(context, bar);
                for (var y = bar.Y + 4; y < bar.Bottom - 4; y += 2) context.DrawLine(MacLook.Line, new Point(bar.X + 2, y + 0.5), new Point(bar.Right - 2, y + 0.5));
                if (dialog.Title.Length > 0)
                {
                    var title = MacLook.Text(dialog.Title);
                    var x = bar.X + (bar.Width - title.Width) / 2;
                    context.FillRectangle(MacLook.White, new Rect(x - 6, bar.Y + 1, title.Width + 12, top - 2));
                    context.DrawText(title, new Point(x, bar.Y + (top - title.Height) / 2));
                }
            }
        }

        private void DrawItem(DrawingContext context, DialogPreviewItem entry, bool isDefault)
        {
            var item = entry.Item;
            var rect = new Rect(item.Bounds.Left, item.Bounds.Top, Math.Max(0, item.Bounds.Right - item.Bounds.Left), Math.Max(0, item.Bounds.Bottom - item.Bounds.Top));
            switch (item.Type)
            {
                case 4:
                    Button(context, rect, item.Text ?? "", isDefault);
                    break;
                case 5:
                    CheckBox(context, rect, item.Text ?? "", radio: false);
                    break;
                case 6:
                    CheckBox(context, rect, item.Text ?? "", radio: true);
                    break;
                case 7:
                    Control(context, rect, entry.Control, item.ResourceId);
                    break;
                case 8:
                    context.DrawText(MacLook.Text(item.Text ?? "", maxWidth: rect.Width), rect.TopLeft);
                    break;
                case 16:
                    MacLook.Frame(context, rect.Inflate(3));
                    context.DrawText(MacLook.Text(item.Text ?? "", maxWidth: rect.Width), rect.TopLeft);
                    break;
                case 32 or 64:
                    if (entry.Png is { } png)
                    {
                        using var bitmap = new Bitmap(new MemoryStream(png));
                        context.DrawImage(bitmap, item.Type == 32 ? new Rect(rect.X, rect.Y, bitmap.PixelSize.Width, bitmap.PixelSize.Height) : rect);
                    }
                    else
                    {
                        MacLook.Frame(context, rect, MacLook.Dotted);
                    }
                    break;
                case 0:
                    MacLook.Frame(context, rect, MacLook.Dotted); // the application draws it
                    break;
            }
        }

        private static void Button(DrawingContext context, Rect rect, string title, bool isDefault)
        {
            var radius = Math.Min(8, rect.Height / 2);
            context.DrawRectangle(MacLook.White, MacLook.Line, rect.Deflate(0.5), radius, radius);
            var text = MacLook.Text(title, maxWidth: rect.Width, alignment: TextAlignment.Center);
            context.DrawText(text, new Point(rect.X, rect.Y + (rect.Height - text.Height) / 2));
            if (isDefault)
            {
                var ring = rect.Inflate(4);
                context.DrawRectangle(null, new Pen(MacLook.Black, 3), ring.Deflate(1.5), radius + 4, radius + 4);
            }
        }

        private static void CheckBox(DrawingContext context, Rect rect, string title, bool radio)
        {
            var box = new Rect(rect.X + 2, rect.Y + Math.Max(0, (rect.Height - 12) / 2), 12, 12);
            if (radio) context.DrawEllipse(MacLook.White, MacLook.Line, box.Deflate(0.5));
            else context.DrawRectangle(MacLook.White, MacLook.Line, box.Deflate(0.5));
            var text = MacLook.Text(title, maxWidth: Math.Max(1, rect.Width - 18));
            context.DrawText(text, new Point(rect.X + 18, rect.Y + (rect.Height - text.Height) / 2));
        }

        private static void Control(DrawingContext context, Rect rect, ControlTemplate? control, short? id)
        {
            if (control is null)
            {
                MacLook.Frame(context, rect, MacLook.Dotted);
                context.DrawText(MacLook.Text($"CNTL {id}", MacLook.Grey), rect.TopLeft + new Point(2, 1));
                return;
            }
            switch (control.Definition)
            {
                case 0 or 8:
                    Button(context, rect, control.Title, false);
                    break;
                case 1 or 9:
                    CheckBox(context, rect, control.Title, radio: false);
                    break;
                case 2 or 10:
                    CheckBox(context, rect, control.Title, radio: true);
                    break;
                case 16:
                    ScrollBar(context, rect);
                    break;
                case >= 1008 and <= 1023:
                    Popup(context, rect, control);
                    break;
                default:
                    MacLook.Frame(context, rect, MacLook.Dotted);
                    context.DrawText(MacLook.Text($"CDEF {control.Definition >> 4}", MacLook.Grey), rect.TopLeft + new Point(2, 1));
                    break;
            }
        }

        private static void ScrollBar(DrawingContext context, Rect rect)
        {
            context.FillRectangle(MacLook.White, rect);
            MacLook.Frame(context, rect);
            var vertical = rect.Height >= rect.Width;
            var arrow = vertical ? rect.Width : rect.Height;
            if (vertical)
            {
                context.DrawLine(MacLook.Line, new Point(rect.X, rect.Y + arrow - 0.5), new Point(rect.Right, rect.Y + arrow - 0.5));
                context.DrawLine(MacLook.Line, new Point(rect.X, rect.Bottom - arrow + 0.5), new Point(rect.Right, rect.Bottom - arrow + 0.5));
            }
            else
            {
                context.DrawLine(MacLook.Line, new Point(rect.X + arrow - 0.5, rect.Y), new Point(rect.X + arrow - 0.5, rect.Bottom));
                context.DrawLine(MacLook.Line, new Point(rect.Right - arrow + 0.5, rect.Y), new Point(rect.Right - arrow + 0.5, rect.Bottom));
            }
        }

        // A pop-up menu: its title in the first "title width" pixels, then a box with a drop shadow and a triangle.
        private static void Popup(DrawingContext context, Rect rect, ControlTemplate control)
        {
            var titleWidth = Math.Clamp((int)control.Maximum, 0, (int)rect.Width);
            if (titleWidth > 0 && control.Title.Length > 0)
            {
                var title = MacLook.Text(control.Title, maxWidth: titleWidth);
                context.DrawText(title, new Point(rect.X, rect.Y + (rect.Height - title.Height) / 2));
            }
            var box = new Rect(rect.X + titleWidth, rect.Y, Math.Max(20, rect.Width - titleWidth - 1), Math.Max(4, rect.Height - 1));
            context.FillRectangle(MacLook.Black, box.Translate(new Vector(1, 1)));
            context.FillRectangle(MacLook.White, box);
            MacLook.Frame(context, box);
            var cx = box.Right - 12;
            var cy = box.Y + box.Height / 2 - 2;
            var triangle = new StreamGeometry();
            using (var g = triangle.Open())
            {
                g.BeginFigure(new Point(cx - 5, cy), true);
                g.LineTo(new Point(cx + 6, cy));
                g.LineTo(new Point(cx + 0.5, cy + 6));
                g.EndFigure(true);
            }
            context.DrawGeometry(MacLook.Black, null, triangle);
        }
    }

    /// <summary>
    /// A menu drawn pulled down in the System 7 style [ClassicMac: an approximation]: its title in a menu bar strip, then
    /// the items with their marks, styles, Command keys, submenu arrows and dividers; disabled items grey.
    /// </summary>
    internal sealed class MenuView : Control
    {
        public static readonly StyledProperty<MenuResource?> MenuProperty = AvaloniaProperty.Register<MenuView, MenuResource?>(nameof(Menu));

        public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<MenuView, double>(nameof(Scale), 1);

        private const double Bar = 20, Row = 16, Left = 16, Gutter = 12;

        static MenuView()
        {
            AffectsMeasure<MenuView>(MenuProperty, ScaleProperty);
            AffectsRender<MenuView>(MenuProperty, ScaleProperty);
        }

        public MenuResource? Menu
        {
            get => GetValue(MenuProperty);
            set => SetValue(MenuProperty, value);
        }

        public double Scale
        {
            get => GetValue(ScaleProperty);
            set => SetValue(ScaleProperty, value);
        }

        private static double MenuWidth(MenuResource menu) =>
            Math.Max(MacLook.Text(Title(menu)).Width + 20,
                menu.Items.Select(i => Left + MacLook.Text(i.Text, typeface: Face(i.Face)).Width + (i.Submenu is not null || KeyText(i) is not null ? 40 : 12)).DefaultIfEmpty(80).Max());

        protected override Size MeasureOverride(Size availableSize) => Menu is { } menu
            ? new Size((MenuWidth(menu) + 2 * Gutter + 2) * Scale, (Bar + menu.Items.Count * Row + 2 * Gutter + 3) * Scale)
            : default;

        private static string Title(MenuResource menu) => InterfaceNames.Chicago(menu.Title);

        private static Typeface Face(byte face) => (face & 1) != 0 ? MacLook.SystemBold : MacLook.System;

        private static string? KeyText(ClassicMac.Resources.Decoders.Interface.MenuItem item) => item.KeyKind is null && item.KeyEquivalent is > 0x20 and < 0x7F ? $"⌘{(char)item.KeyEquivalent}" : null;

        public override void Render(DrawingContext context)
        {
            if (Menu is not { } menu) return;
            using var scale = context.PushTransform(Matrix.CreateScale(Scale, Scale));
            var width = MenuWidth(menu);

            // The title, highlighted as when the menu is pulled down, on a strip of menu bar.
            var bar = new Rect(Gutter, Gutter, width + 2, Bar);
            context.FillRectangle(MacLook.White, bar);
            context.DrawLine(MacLook.Line, new Point(bar.X, bar.Bottom - 0.5), new Point(bar.Right, bar.Bottom - 0.5));
            var title = MacLook.Text(Title(menu), MacLook.White);
            context.FillRectangle(MacLook.Black, new Rect(bar.X + 4, bar.Y, title.Width + 14, Bar - 1));
            context.DrawText(title, new Point(bar.X + 11, bar.Y + (Bar - 1 - title.Height) / 2));

            // The menu, with its drop shadow.
            var box = new Rect(Gutter, Gutter + Bar - 1, width + 2, menu.Items.Count * Row + 2);
            context.FillRectangle(MacLook.Black, new Rect(box.X + 1, box.Y + 1, box.Width + 1, box.Height + 1));
            context.FillRectangle(MacLook.White, box);
            MacLook.Frame(context, box);
            for (var i = 0; i < menu.Items.Count; i++)
            {
                var item = menu.Items[i];
                var y = box.Y + 1 + i * Row;
                if (item.IsDivider)
                {
                    context.DrawLine(MacLook.Dotted, new Point(box.X + 1, y + Row / 2 - 0.5), new Point(box.Right - 1, y + Row / 2 - 0.5));
                    continue;
                }
                var brush = item.Enabled && menu.Enabled ? MacLook.Black : MacLook.Grey;
                var text = MacLook.Text(item.Text, brush, Face(item.Face));
                var baseline = y + (Row - text.Height) / 2;
                if (item.Mark != 0 && item.Submenu is null && item.KeyEquivalent != 0x1A)
                    context.DrawText(MacLook.Text(InterfaceNames.Chicago(((char)item.Mark).ToString()), brush), new Point(box.X + 4, baseline));
                context.DrawText(text, new Point(box.X + Left, baseline));
                if (item.Submenu is not null)
                    context.DrawText(MacLook.Text("▶", brush), new Point(box.Right - 16, baseline));
                else if (KeyText(item) is { } key)
                {
                    var keyText = MacLook.Text(key, brush);
                    context.DrawText(keyText, new Point(box.Right - keyText.Width - 8, baseline));
                }
            }
        }
    }
}
