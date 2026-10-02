using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
            if (Styled is not { } styled)
            {
                return;
            }

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
    internal sealed class DocumentPictureView : PixelControl
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

        // At its size, or zoomed by the column, in whole device pixels per Mac pixel (PixelScaling); shrunk below one
        // device pixel per Mac pixel, at the size the layout gave it.
        protected override Size MeasureOverride(Size availableSize)
        {
            if (Item is not { } item)
            {
                return default;
            }

            if (bitmap is null || bitmap.PixelSize.Width == 0)
            {
                return new Size(item.Width, item.Height);
            }

            var zoom = (double)item.Width / bitmap.PixelSize.Width;
            var scale = PixelScaling.Scale(zoom, RenderScaling);
            return scale == zoom ? new Size(item.Width, item.Height) : new Size(bitmap.PixelSize.Width * scale, bitmap.PixelSize.Height * scale);
        }

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            using var snap = PushSnap(context);
            if (bitmap is not null)
            {
                context.DrawImage(bitmap, rect);
            }
            else if (rect.Width > 0 && rect.Height > 0)
            {
                context.DrawRectangle(null, Missing, rect.Deflate(0.5));
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton == MouseButton.Left && Item?.Open is { } open && open.CanExecute(null))
            {
                open.Execute(null);
            }
        }
    }

    /// <summary>
    /// A sound's waveform: one lane per channel, the lowest and highest sample under each pixel column. Drawn from the
    /// theme's tokens (boards/sound.md): CmPaneBackground behind, CmDivider centre line, CmAccent wave; redrawn when the
    /// theme variant changes.
    /// </summary>
    /// <summary>
    /// A sound's channel lanes (boards/sound.md): one 170 px lane per channel, the centre line in CmDivider, the waveform in
    /// CmAccent, the loop as a CmLoopRegion band behind it, the playhead a 2 px CmPlayhead line; a time ruler in mono 11
    /// below. A click asks <see cref="SeekCommand"/> to play from there (in seconds).
    /// </summary>
    internal sealed class WaveformView : Control
    {
        public const double LaneHeight = 170;

        public const double RulerHeight = 20;

        public static readonly StyledProperty<DecodedSound?> SoundProperty =
            AvaloniaProperty.Register<WaveformView, DecodedSound?>(nameof(Sound));

        public static readonly StyledProperty<(int Start, int End)?> LoopFramesProperty =
            AvaloniaProperty.Register<WaveformView, (int Start, int End)?>(nameof(LoopFrames));

        public static readonly StyledProperty<double> PlayheadProperty = AvaloniaProperty.Register<WaveformView, double>(nameof(Playhead));

        public static readonly StyledProperty<bool> ShowsPlayheadProperty = AvaloniaProperty.Register<WaveformView, bool>(nameof(ShowsPlayhead));

        public static readonly StyledProperty<System.Windows.Input.ICommand?> SeekCommandProperty =
            AvaloniaProperty.Register<WaveformView, System.Windows.Input.ICommand?>(nameof(SeekCommand));

        static WaveformView()
        {
            AffectsMeasure<WaveformView>(SoundProperty);
            AffectsRender<WaveformView>(SoundProperty, LoopFramesProperty, PlayheadProperty, ShowsPlayheadProperty);
        }

        public WaveformView()
        {
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        }

        public DecodedSound? Sound
        {
            get => GetValue(SoundProperty);
            set => SetValue(SoundProperty, value);
        }

        /// <summary>The loop's frames, drawn as a band; null for none.</summary>
        public (int Start, int End)? LoopFrames
        {
            get => GetValue(LoopFramesProperty);
            set => SetValue(LoopFramesProperty, value);
        }

        /// <summary>The playhead, in seconds from the start.</summary>
        public double Playhead
        {
            get => GetValue(PlayheadProperty);
            set => SetValue(PlayheadProperty, value);
        }

        /// <summary>Whether the playhead is drawn (while playing, or once moved).</summary>
        public bool ShowsPlayhead
        {
            get => GetValue(ShowsPlayheadProperty);
            set => SetValue(ShowsPlayheadProperty, value);
        }

        public System.Windows.Input.ICommand? SeekCommand
        {
            get => GetValue(SeekCommandProperty);
            set => SetValue(SeekCommandProperty, value);
        }

        private IBrush? Token(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, LaneHeight * Math.Max(1, Sound?.Channels ?? 1) + RulerHeight);

        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (Sound is not { Frames: > 0 } sound || Bounds.Width < 1)
            {
                return;
            }

            var seconds = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1) * sound.Duration;
            if (SeekCommand?.CanExecute(seconds) == true)
            {
                SeekCommand.Execute(seconds);
                e.Handled = true;
            }
        }

        public override void Render(DrawingContext context)
        {
            var size = Bounds.Size;
            var lanes = new Rect(0, 0, size.Width, Math.Max(0, size.Height - RulerHeight));
            if (Token("CmPaneBackground") is { } background)
            {
                context.FillRectangle(background, new Rect(size));
            }

            if (Sound is not { Frames: > 0 } sound || size.Width < 1)
            {
                return;
            }

            var wave = Token("CmAccent") ?? Brushes.Gray;
            var axis = Token("CmDivider");
            if (LoopFrames is { } loop && Token("CmLoopRegion") is { } band && loop.End > loop.Start)
            {
                var x0 = Math.Floor((double)loop.Start / sound.Frames * size.Width);
                var x1 = Math.Ceiling((double)loop.End / sound.Frames * size.Width);
                context.FillRectangle(band, new Rect(x0, 0, Math.Max(1, x1 - x0), lanes.Height));
            }

            var lane = lanes.Height / sound.Channels;
            var columns = (int)size.Width;
            for (var c = 0; c < sound.Channels; c++)
            {
                var middle = lane * c + lane / 2;
                var half = Math.Max(1, lane / 2 - 2);
                if (axis is not null)
                {
                    context.FillRectangle(axis, new Rect(0, Math.Floor(middle), size.Width, 1)); // on whole pixels
                }

                for (var x = 0; x < columns; x++)
                {
                    var first = (int)((long)x * sound.Frames / columns);
                    var last = Math.Min(sound.Frames, Math.Max(first + 1, (int)((long)(x + 1) * sound.Frames / columns)));
                    float low = 1, high = -1;
                    // Up to the next column's first sample too, so a sound with fewer frames than columns is a line.
                    for (var f = first; f <= Math.Min(last, sound.Frames - 1); f++)
                    {
                        var v = sound.Samples[f * sound.Channels + c];
                        low = Math.Min(low, v);
                        high = Math.Max(high, v);
                    }
                    var top = middle - Math.Clamp(high, -1, 1) * half;
                    var bottom = middle - Math.Clamp(low, -1, 1) * half;
                    context.FillRectangle(wave, new Rect(x, top, 1, Math.Max(1, bottom - top)));
                }
            }

            DrawRuler(context, sound, lanes.Height, size.Width);
            if (ShowsPlayhead && sound.Duration > 0 && Token("CmPlayhead") is { } playhead)
            {
                var x = Math.Round(Math.Clamp(Playhead / sound.Duration, 0, 1) * (size.Width - 2));
                context.FillRectangle(playhead, new Rect(x, 0, 2, lanes.Height));
            }
        }

        /// <summary>The smallest step of 1, 2 or 5 × 10^n that is at least <paramref name="minimum"/>.</summary>
        internal static double NiceStep(double minimum)
        {
            var power = Math.Pow(10, Math.Floor(Math.Log10(minimum)));
            foreach (var factor in new[] { 1.0, 2.0, 5.0, 10.0 })
            {
                if (factor * power >= minimum * (1 - 1e-9))
                {
                    return factor * power;
                }
            }

            return 10 * power;
        }

        /// <summary>
        /// The ruler's ticks over <paramref name="duration"/> seconds drawn <paramref name="width"/> px wide: every nice step
        /// (<see cref="NiceStep"/>) at least <paramref name="spacing"/> px apart, from 0 s, labelled with the decimals the
        /// step needs ("0.002 s", "0.5 s", "50 s").
        /// </summary>
        internal static IReadOnlyList<(double Seconds, string Label)> RulerTicks(double duration, double width, double spacing = 80)
        {
            if (duration <= 0 || width <= 0)
            {
                return [(0, "0 s")];
            }

            var step = NiceStep(duration * spacing / width);
            var decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
            var format = "{0:F" + decimals.ToString(System.Globalization.CultureInfo.InvariantCulture) + "} s";
            var ticks = new List<(double, string)>();
            for (var i = 0; i * step <= duration * (1 + 1e-9); i++)
            {
                var seconds = i * step;
                ticks.Add((seconds, i == 0 ? "0 s" : string.Format(System.Globalization.CultureInfo.InvariantCulture, format, seconds)));
            }

            return ticks;
        }

        // The time ruler: a tick and a label in mono 11 at each nice step (at least 80 px apart), from 0 s.
        private void DrawRuler(DrawingContext context, DecodedSound sound, double top, double width)
        {
            var text = Token("CmTextMuted") ?? Brushes.Gray;
            var font = this.TryFindResource("CmFontMono", ActualThemeVariant, out var family) && family is FontFamily mono ? mono : FontFamily.Default;
            foreach (var (seconds, labelText) in RulerTicks(sound.Duration, width))
            {
                var x = sound.Duration > 0 ? Math.Round(width * seconds / sound.Duration) : 0;
                var label = new FormattedText(labelText, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(font), 11, text);
                context.FillRectangle(text, new Rect(Math.Min(x, width - 1), top, 1, 4));
                var at = Math.Clamp(x - label.Width / 2, 0, Math.Max(0, width - label.Width));
                context.DrawText(label, new Point(at, top + 5));
            }
        }
    }
}
