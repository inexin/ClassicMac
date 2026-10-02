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
    /// A dialog or alert as Mac OS 9's Appearance (Platinum) draws it: the image <see cref="DialogPreview"/> holds,
    /// drawn by ClassicMac's QuickDraw (docs/formats/INTERFACE.md §12), shown with a margin and zoomed without smoothing.
    /// </summary>
    internal sealed class DialogView : Control
    {
        public static readonly StyledProperty<DialogPreview?> DialogProperty = AvaloniaProperty.Register<DialogView, DialogPreview?>(nameof(Dialog));

        public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<DialogView, double>(nameof(Scale), 1);

        private const double Gutter = 12;

        private Bitmap? bitmap;

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

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == DialogProperty)
            {
                bitmap?.Dispose();
                bitmap = Dialog is { } d ? new Bitmap(new MemoryStream(d.Png)) : null;
            }
        }

        protected override Size MeasureOverride(Size availableSize) => Dialog is { } d
            ? new Size((d.PixelWidth + 2 * Gutter) * Scale, (d.PixelHeight + 2 * Gutter) * Scale)
            : default;

        public override void Render(DrawingContext context)
        {
            if (Dialog is not { } dialog || bitmap is null) return;
            context.DrawImage(bitmap, new Rect(Gutter * Scale, Gutter * Scale, dialog.PixelWidth * Scale, dialog.PixelHeight * Scale));
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
