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

namespace ClassicMac.App.Views;

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
        if (maxWidth is { } width)
        {
            formatted.MaxTextWidth = Math.Max(1, width);
        }

        return formatted;
    }

    // A 1-pixel frame inside a rectangle (QuickDraw's FrameRect).
    public static void Frame(DrawingContext context, Rect rect, Pen? pen = null) => context.DrawRectangle(null, pen ?? Line, rect.Deflate(0.5));
}

/// <summary>
/// A dialog or alert as Mac OS 9's Appearance (Platinum) draws it: the image <see cref="DialogPreview"/> holds,
/// drawn by ClassicMac's QuickDraw (docs/formats/resources/windows-dialogs.md §5.3), shown with a margin and zoomed
/// without smoothing.
/// </summary>
internal sealed class DialogView : PixelControl
{
    public static readonly StyledProperty<DialogPreview?> DialogProperty = AvaloniaProperty.Register<DialogView, DialogPreview?>(nameof(Dialog));

    public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<DialogView, double>(nameof(Scale), 1);

    /// <summary>The outlined item (an item list form's selected row), or -1; a click on an item selects it.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<DialogView, int>(nameof(SelectedIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private const double Gutter = 12;

    // The outline: 1 px dashed CmAccent, 3 px outside the item's bounds (boards/dialog-item-list.md).
    private const double OutlineGap = 3;

    private Bitmap? bitmap;

    static DialogView()
    {
        AffectsMeasure<DialogView>(DialogProperty, ScaleProperty);
        AffectsRender<DialogView>(DialogProperty, ScaleProperty, SelectedIndexProperty);
    }

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

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    // An item's rectangle in the control, from its bounds in the window's content.
    private Rect ItemRect(DialogPreview dialog, int index, double scale)
    {
        var b = dialog.Drawing.Items[index].Item.Bounds;
        return new Rect((Gutter + dialog.ContentLeft + b.Left) * scale, (Gutter + dialog.ContentTop + b.Top) * scale,
            (b.Right - b.Left) * scale, (b.Bottom - b.Top) * scale);
    }

    // A click selects the first item whose bounds hold the point, as the Dialog Manager's FindDialogItem finds it.
    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Dialog is not { } dialog)
        {
            return;
        }

        var scale = PixelScaling.Scale(Scale, RenderScaling);
        var point = e.GetPosition(this);
        for (var i = 0; i < dialog.Drawing.Items.Count; i++)
        {
            if (ItemRect(dialog, i, scale).Contains(point))
            {
                SelectedIndex = i;
                e.Handled = true;
                return;
            }
        }
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

    // The gutters are in Mac pixels too, so the dialog starts on a whole device pixel.
    protected override Size MeasureOverride(Size availableSize) => Dialog is { } d
        ? PixelScaling.Size(new Size(d.PixelWidth + 2 * Gutter, d.PixelHeight + 2 * Gutter), Scale, RenderScaling)
        : default;

    public override void Render(DrawingContext context)
    {
        if (Dialog is not { } dialog || bitmap is null)
        {
            return;
        }

        var scale = PixelScaling.Scale(Scale, RenderScaling);
        using var snap = PushSnap(context);
        context.DrawImage(bitmap, new Rect(Gutter * scale, Gutter * scale, dialog.PixelWidth * scale, dialog.PixelHeight * scale));
        if (SelectedIndex >= 0 && SelectedIndex < dialog.Drawing.Items.Count
            && this.TryFindResource("CmAccent", ActualThemeVariant, out var accent) && accent is IBrush brush)
        {
            var gap = OutlineGap * scale;
            var outline = ItemRect(dialog, SelectedIndex, scale).Inflate(gap - 0.5);
            context.DrawRectangle(null, new Pen(brush, 1, new DashStyle([3, 2], 0)), outline);
        }
    }
}

/// <summary>
/// A menu drawn pulled down in the System 7 style [ClassicMac: an approximation]: its title in a menu bar strip, then
/// the items with their marks, styles, Command keys, submenu arrows and dividers; disabled items grey.
/// </summary>
internal sealed class MenuView : PixelControl
{
    public static readonly StyledProperty<MenuResource?> MenuProperty = AvaloniaProperty.Register<MenuView, MenuResource?>(nameof(Menu));

    public static readonly StyledProperty<double> ScaleProperty = AvaloniaProperty.Register<MenuView, double>(nameof(Scale), 1);

    /// <summary>The highlighted item (drawn inverted, as the Menu Manager highlights it), or -1; a click on an item sets it.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<MenuView, int>(nameof(SelectedIndex), -1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private const double Bar = 20, Row = 16, Left = 16, Gutter = 12;

    static MenuView()
    {
        AffectsMeasure<MenuView>(MenuProperty, ScaleProperty);
        AffectsRender<MenuView>(MenuProperty, ScaleProperty, SelectedIndexProperty);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The item at a point in the control (in its own units), or -1 outside the items.</summary>
    public int ItemAt(Point point)
    {
        if (Menu is not { } menu)
        {
            return -1;
        }

        var pixel = PixelScaling.Scale(Scale, RenderScaling);
        var (x, y) = (point.X / pixel, point.Y / pixel);              // DIPs to Mac pixels
        var top = Gutter + Bar;
        if (x < Gutter || x > Gutter + MenuWidth(menu) + 2 || y < top)
        {
            return -1;
        }

        var index = (int)Math.Floor((y - top) / Row);
        return index < menu.Items.Count ? index : -1;
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ItemAt(e.GetPosition(this)) is var index and >= 0)
        {
            SelectedIndex = index;
            e.Handled = true;
        }
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
        ? PixelScaling.Size(new Size(MenuWidth(menu) + 2 * Gutter + 2, Bar + menu.Items.Count * Row + 2 * Gutter + 3), Scale, RenderScaling)
        : default;

    private static string Title(MenuResource menu) => InterfaceNames.Chicago(menu.Title);

    private static Typeface Face(byte face) => (face & 1) != 0 ? MacLook.SystemBold : MacLook.System;

    private static string? KeyText(ClassicMac.Resources.Decoders.Interface.MenuItem item) => item.KeyKind is null && item.KeyEquivalent is > 0x20 and < 0x7F ? $"⌘{(char)item.KeyEquivalent}" : null;

    public override void Render(DrawingContext context)
    {
        if (Menu is not { } menu)
        {
            return;
        }

        var pixel = PixelScaling.Scale(Scale, RenderScaling);
        using var snap = PushSnap(context);
        using var scale = context.PushTransform(Matrix.CreateScale(pixel, pixel));
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
            // The selected item inverted, as the Menu Manager highlights the item under the pointer.
            var selected = i == SelectedIndex;
            if (selected)
            {
                context.FillRectangle(MacLook.Black, new Rect(box.X + 1, y, box.Width - 2, Row));
            }

            if (item.IsDivider)
            {
                context.DrawLine(selected ? new Pen(MacLook.White, 1, DashStyle.Dot) : MacLook.Dotted,
                    new Point(box.X + 1, y + Row / 2 - 0.5), new Point(box.Right - 1, y + Row / 2 - 0.5));
                continue;
            }
            var brush = selected ? MacLook.White : item.Enabled && menu.Enabled ? MacLook.Black : MacLook.Grey;
            var text = MacLook.Text(item.Text, brush, Face(item.Face));
            var baseline = y + (Row - text.Height) / 2;
            if (item.Mark != 0 && item.Submenu is null && item.KeyEquivalent != 0x1A)
            {
                context.DrawText(MacLook.Text(InterfaceNames.Chicago(((char)item.Mark).ToString()), brush), new Point(box.X + 4, baseline));
            }

            context.DrawText(text, new Point(box.X + Left, baseline));
            if (item.Submenu is not null)
            {
                context.DrawText(MacLook.Text("▶", brush), new Point(box.Right - 16, baseline));
            }
            else if (KeyText(item) is { } key)
            {
                var keyText = MacLook.Text(key, brush);
                context.DrawText(keyText, new Point(box.Right - keyText.Width - 8, baseline));
            }
        }
    }
}
