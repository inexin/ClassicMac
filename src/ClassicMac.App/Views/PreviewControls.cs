using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using ClassicMac.App.ViewModels;
using ClassicMac.Resources.Decoders.Sound;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.App.Views
{
    // PNG bytes from the decoders as a bitmap.
    internal static class Images
    {
        public static FuncValueConverter<byte[]?, Bitmap?> FromPng { get; } =
            new(png => png is null ? null : new Bitmap(new MemoryStream(png)));

        // A light checkerboard behind images, so transparent pixels show.
        public static IBrush Checkerboard { get; } = MakeCheckerboard();

        private static IBrush MakeCheckerboard()
        {
            var tile = new DrawingGroup();
            tile.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) });
            tile.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) });
            tile.Children.Add(new GeometryDrawing { Brush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)), Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) });
            return new DrawingBrush(tile)
            {
                TileMode = TileMode.Tile,
                DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
            };
        }
    }

    /// <summary>Styled text (<c>TEXT</c> + <c>styl</c>) drawn with its fonts, sizes, faces and colours.</summary>
    internal sealed class StyledTextView : SelectableTextBlock
    {
        public static readonly StyledProperty<StyledText?> StyledProperty =
            AvaloniaProperty.Register<StyledTextView, StyledText?>(nameof(Styled));

        /// <summary>Device-independent pixels per point: 4/3 (points at 96 dpi) by default; 1 in a document, at the Mac's 72 dpi.</summary>
        public static readonly StyledProperty<double> ScaleProperty =
            AvaloniaProperty.Register<StyledTextView, double>(nameof(Scale), 4.0 / 3.0);

        static StyledTextView()
        {
            StyledProperty.Changed.AddClassHandler<StyledTextView>((view, _) => view.Rebuild());
            ScaleProperty.Changed.AddClassHandler<StyledTextView>((view, _) => view.Rebuild());
        }

        public StyledText? Styled
        {
            get => GetValue(StyledProperty);
            set => SetValue(StyledProperty, value);
        }

        public double Scale
        {
            get => GetValue(ScaleProperty);
            set => SetValue(ScaleProperty, value);
        }

        protected override System.Type StyleKeyOverride => typeof(SelectableTextBlock);

        private void Rebuild()
        {
            Inlines ??= [];
            Inlines.Clear();
            if (Styled is not { } styled) return;
            foreach (var run in styled.Runs)
            {
                Inlines.Add(new Run(styled.Text.Substring(run.Start, run.Length).Replace('\r', '\n'))
                {
                    FontFamily = Family(run.FontName),
                    FontSize = run.Size * Scale,
                    FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal,
                    FontStyle = run.Italic ? FontStyle.Italic : FontStyle.Normal,
                    TextDecorations = run.Underline ? Avalonia.Media.TextDecorations.Underline : null,
                    Foreground = new SolidColorBrush(Color.FromRgb(run.Red, run.Green, run.Blue)),
                });
            }
        }

        // Classic Mac fonts rarely exist on the host: close relatives, then the default.
        private static FontFamily Family(string name) => name switch
        {
            "Monaco" or "Courier" => new FontFamily("Cascadia Mono, Consolas, Menlo, Courier New, monospace"),
            "Times" or "New York" => new FontFamily("Times New Roman, Times, serif"),
            "Palatino" => new FontFamily("Palatino, Palatino Linotype, Book Antiqua, serif"),
            "Bookman" => new FontFamily("Bookman, Bookman Old Style, serif"),
            "New Century Schoolbook" => new FontFamily("New Century Schoolbook, Century Schoolbook, serif"),
            "Avant Garde" => new FontFamily("Avant Garde, Century Gothic, sans-serif"),
            "Helvetica Narrow" => new FontFamily("Helvetica Narrow, Arial Narrow, sans-serif"),
            "Zapf Chancery" => new FontFamily("Zapf Chancery, Monotype Corsiva, cursive"),
            "Helvetica" or "Geneva" or "Chicago" => FontFamily.Default,
            _ => new FontFamily($"{name}, {FontFamily.Default.Name}"),
        };
    }

    /// <summary>A document's picture at its size, pixels unsmoothed; a dashed box when it was not drawn. A link opens on a click.</summary>
    internal sealed class DocumentPictureView : Control
    {
        public static readonly StyledProperty<DocumentPictureItem?> ItemProperty =
            AvaloniaProperty.Register<DocumentPictureView, DocumentPictureItem?>(nameof(Item));

        private static readonly Pen Missing = new(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), 1, DashStyle.Dash);

        private Bitmap? bitmap;

        static DocumentPictureView()
        {
            AffectsMeasure<DocumentPictureView>(ItemProperty);
            AffectsRender<DocumentPictureView>(ItemProperty);
            ItemProperty.Changed.AddClassHandler<DocumentPictureView>((view, _) => view.Load());
        }

        public DocumentPictureView() => RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.None);

        public DocumentPictureItem? Item
        {
            get => GetValue(ItemProperty);
            set => SetValue(ItemProperty, value);
        }

        private void Load()
        {
            bitmap?.Dispose();
            bitmap = Item?.Png is { } png ? new Bitmap(new MemoryStream(png)) : null;
            Cursor = Item?.IsLink == true ? new Cursor(StandardCursorType.Hand) : null;
            ToolTip.SetTip(this, string.IsNullOrEmpty(Item?.ToolTip) ? null : Item.ToolTip);
        }

        protected override Size MeasureOverride(Size availableSize) => Item is { } item ? new Size(item.Width, item.Height) : default;

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            if (bitmap is not null) context.DrawImage(bitmap, rect);
            else if (rect.Width > 0 && rect.Height > 0) context.DrawRectangle(null, Missing, rect.Deflate(0.5));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton == MouseButton.Left && Item?.Open is { } open && open.CanExecute(null)) open.Execute(null);
        }
    }

    /// <summary>A sound's waveform: one lane per channel, the lowest and highest sample under each pixel column.</summary>
    internal sealed class WaveformView : Control
    {
        public static readonly StyledProperty<DecodedSound?> SoundProperty =
            AvaloniaProperty.Register<WaveformView, DecodedSound?>(nameof(Sound));

        private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));
        private static readonly IBrush Wave = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xC4));
        private static readonly Pen Axis = new(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));

        static WaveformView() => AffectsRender<WaveformView>(SoundProperty);

        public DecodedSound? Sound
        {
            get => GetValue(SoundProperty);
            set => SetValue(SoundProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            context.FillRectangle(Background, new Rect(size));
            if (Sound is not { Frames: > 0 } sound || size.Width < 1) return;
            var lane = size.Height / sound.Channels;
            var columns = (int)size.Width;
            for (var c = 0; c < sound.Channels; c++)
            {
                var middle = lane * c + lane / 2;
                var half = Math.Max(1, lane / 2 - 2);
                context.DrawLine(Axis, new Point(0, middle), new Point(size.Width, middle));
                for (var x = 0; x < columns; x++)
                {
                    var first = (int)((long)x * sound.Frames / columns);
                    var last = Math.Min(sound.Frames, Math.Max(first + 1, (int)((long)(x + 1) * sound.Frames / columns)));
                    float low = 1, high = -1;
                    for (var f = first; f < last; f++)
                    {
                        var v = sound.Samples[f * sound.Channels + c];
                        low = Math.Min(low, v);
                        high = Math.Max(high, v);
                    }
                    var top = middle - Math.Clamp(high, -1, 1) * half;
                    var bottom = middle - Math.Clamp(low, -1, 1) * half;
                    context.FillRectangle(Wave, new Rect(x, top, 1, Math.Max(1, bottom - top)));
                }
            }
        }
    }
}
