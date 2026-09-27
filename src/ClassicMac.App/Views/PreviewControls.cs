using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

        static StyledTextView() => StyledProperty.Changed.AddClassHandler<StyledTextView>((view, _) => view.Rebuild());

        public StyledText? Styled
        {
            get => GetValue(StyledProperty);
            set => SetValue(StyledProperty, value);
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
                    FontSize = run.Size * 4.0 / 3.0, // points (72 per inch) to device-independent pixels (96)
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
            "Helvetica" or "Geneva" or "Chicago" => FontFamily.Default,
            _ => new FontFamily($"{name}, {FontFamily.Default.Name}"),
        };
    }
}
