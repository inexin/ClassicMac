using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;

namespace ClassicMac.App.Tests;

// The font family preview in the window (design/boards/font-family.md, P6).
public sealed class FontFamilyViewTests
{
    private static Color Token(string key) =>
        ((ISolidColorBrush)Application.Current!.FindResource(Application.Current!.ActualThemeVariant, key)!).Color;

    private static Color At(WriteableBitmap frame, Control control, Point point, MainWindow window)
    {
        var at = control.TranslatePoint(point, window)!.Value;
        using var buffer = frame.Lock();
        Assert.True(at.X >= 0 && at.Y >= 0 && at.X < buffer.Size.Width && at.Y < buffer.Size.Height, $"{at} outside the frame {buffer.Size}");
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + (int)at.Y * buffer.RowBytes + (int)at.X * 4, bytes, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    [Fact]
    public void The_family_shows_its_sample_matrix_and_cards() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-fondview").FullName;
        try
        {
            var path = Path.Combine(folder, "Fonts.rsrc");
            File.WriteAllBytes(path, FontFixtures.Fork());
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Headless.Pump(open);
            var input = open.Result!;
            Headless.Pump(input.EnsureLoadedAsync());
            var fond = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "FOND").Children.OfType<ResourceNode>().Single();
            input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "FOND").IsExpanded = true;
            model.Selected = fond;
            Headless.Pump(model.PreviewTask);
            Headless.Pump(model.InspectorActions.HeaderIconTask);
            Dispatcher.UIThread.RunJobs();
            var font = model.Preview.FontFamily!;
            font.SampleText = "ToAV";
            font.SelectedPair = font.KernPairs.Single(p => p.Pair == "AV");
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            window.FindControl<ScrollViewer>("FontFamilyView")!.Offset = default;   // the pair's row scrolled into view
            Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame()!;
            Dispatcher.UIThread.RunJobs();

            var view = window.FindControl<ScrollViewer>("FontFamilyView")!;
            Assert.True(view.IsEffectivelyVisible);
            Assert.True(window.FindControl<ListBox>("FontMode")!.IsEffectivelyVisible);
            Assert.Equal(["9", "12", "14", "TrueType"], window.FindControl<ListBox>("FontSizes")!.Items.OfType<FontSizeChoice>().Select(s => s.Label));
            Assert.Equal(1, window.FindControl<ListBox>("FontSizes")!.SelectedIndex);

            // The sample: black glyphs on white, the selected pair on CmMatchSoft, at the zoom (1).
            var sample = window.FindControl<FontSampleView>("FontSample")!;
            Assert.Equal(new Size(24, 12), sample.Bounds.Size);
            Assert.Equal(Colors.Black, At(frame, sample, new Point(0.5, 0.5), window));          // T's first column
            Assert.Equal(Colors.White, At(frame, sample, new Point(5.5, 0.5), window));          // T's blank last column
            Assert.Equal(Token("CmMatchSoft"), At(frame, sample, new Point(17.5, 0.5), window)); // A's blank last column, highlighted
            Assert.Equal(Token("CmMatchSoft"), At(frame, sample, new Point(12.5, 10.5), window)); // under A's baseline, highlighted
            Assert.Equal(Colors.White, At(frame, sample, new Point(11.5, 10.5), window));        // under o's
            model.Zoom = 2;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            Assert.Equal(new Size(48, 24), sample.Bounds.Size);
            model.Zoom = 1;
            font.SampleText = FontFamilyPreview.DefaultSampleText;
            font.SelectedPair = null;
            Dispatcher.UIThread.RunJobs();

            // The matrix: a header of styles, a row per size; a found cell selects its resource, a missing one is muted.
            var matrix = window.FindControl<Border>("FontMatrix")!;
            var links = matrix.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("link") && b.IsVisible).Select(b => b.Content as string).ToList();
            Assert.Equal(["NFNT 1001", "NFNT 1002", "NFNT 1003", "sfnt 1005"], links);
            var missing = matrix.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("missing") && t.IsVisible).Select(t => t.Text).ToList();
            Assert.Equal(["NFNT 1006", "NFNT 1004"], missing);
            Assert.Contains("4-bit", matrix.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("depth") && t.IsEffectivelyVisible).Select(t => t.Text));
            Capture(window, "font-family", baselines);
            var bold = matrix.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "NFNT 1003");
            bold.Command!.Execute(bold.CommandParameter);
            Headless.Pump(model.PreviewTask);
            Assert.Equal("NFNT", ((ResourceNode)model.Selected!).Resource.Type.ToString());
            Assert.Equal(1003, ((ResourceNode)model.Selected!).Resource.Id);

            // JSON is one click away.
            model.Selected = fond;
            Headless.Pump(model.PreviewTask);
            model.PropertyLinks.PropertyModeIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.IsEffectivelyVisible);
            Assert.True(window.FindControl<ScrollViewer>("FontJson")!.IsEffectivelyVisible);
            model.PropertyLinks.PropertyModeIndex = 0;
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    private static void Capture(MainWindow window, string name, List<string> baselines)
    {
        window.CaptureRenderedFrame();
        Baselines.Check(window, name, baselines);
    }
}
