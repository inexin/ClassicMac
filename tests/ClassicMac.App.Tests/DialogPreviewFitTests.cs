using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

using static Headless;

// QA at 1200 × 780: a wide dialog's preview scrolls inside the inspector at 1:1 instead of being cut at its edge, and the
// item list's editing table gives the text column room.
public sealed class DialogPreviewFitTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-dialog-fit").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // The Find items in a dialog 400 wide, as wide as the System's alerts.
    private (MainWindow Window, MainViewModel Model) Open(string type)
    {
        var path = Path.Combine(folder, "Wide.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("DITL", 128, "Find", InterfaceWriter.WriteDialogItems(DialogItemsFormTests.Find())),
            ("DLOG", 128, null, InterfaceWriter.WriteWindow(DialogItemsFormTests.FindDialog() with { Bounds = new MacRect(40, 40, 150, 440) }, true))));
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        window.Show();
        Pump(model.OpenAsync(path));
        Pump(model.Roots[0].EnsureLoadedAsync());
        var node = model.Roots[0].Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type);
        node.IsExpanded = true;
        model.Selected = node.Children[0];
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        return (window, model);
    }

    // The visible dialog view, its scroller, and the inspector's right edge in window coordinates.
    private static (DialogView View, ScrollViewer Scroller) Shown(Window window)
    {
        var view = window.GetVisualDescendants().OfType<DialogView>().Single(v => v.IsEffectivelyVisible);
        return (view, view.FindAncestorOfType<ScrollViewer>()!);
    }

    private static double Right(Visual visual, Window window) => visual.TranslatePoint(new Point(visual.Bounds.Width, 0), window)!.Value.X;

    [Theory]
    [InlineData("DITL")]
    [InlineData("DLOG")]
    public void A_wide_dialog_scrolls_inside_the_inspector_at_1x(string type) => OnUiThread(() =>
    {
        var (window, model) = Open(type);
        Assert.Equal(1200, window.Bounds.Width);
        var (view, scroller) = Shown(window);
        var dialog = model.FormDialog!;
        Assert.Equal(dialog.PixelWidth + 24, view.Bounds.Width);                      // 1:1, with its gutters
        Assert.True(Right(scroller, window) <= window.Bounds.Width, $"scroller ends at {Right(scroller, window)}");
        Assert.False(scroller.AllowAutoHide);                                          // its scroll bars stay in view
        if (type == "DLOG")
        {
            Assert.True(Right(scroller, window) <= window.Bounds.Width - 16, "the dialog hugs the window edge");
        }
        Assert.True(scroller.Extent.Width >= view.Bounds.Width);                      // all of it can be scrolled to
        Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
        if (scroller.Extent.Width > scroller.Viewport.Width)
        {
            scroller.Offset = new Vector(scroller.Extent.Width - scroller.Viewport.Width, 0);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Right(view, window) <= Right(scroller, window) + 0.5);           // the right edge scrolls into view
        }

        window.Close();
    });

    [Fact]
    public void Editing_an_item_list_leaves_the_text_column_room() => OnUiThread(() =>
    {
        var (window, model) = Open("DITL");
        model.EditFormCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.IsEditingForm);
        var text = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "ItemText" && t.IsEffectivelyVisible);
        Assert.True(text.Bounds.Width >= 160, $"the text box is {text.Bounds.Width} wide");
        var caption = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ItemTextCaption" && t.IsEffectivelyVisible);
        var whole = new TextBlock { Text = caption.Text, FontFamily = caption.FontFamily, FontSize = caption.FontSize, FontWeight = caption.FontWeight, LetterSpacing = caption.LetterSpacing };
        whole.Measure(Size.Infinity);
        Assert.True(whole.DesiredSize.Width <= caption.Bounds.Width + 0.5, $"the caption needs {whole.DesiredSize.Width}, has {caption.Bounds.Width}");
        var bounds = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "RightBox" && t.IsEffectivelyVisible);
        Assert.True(Right(bounds, window) <= window.Bounds.Width - 12);
        var (view, scroller) = Shown(window);
        Assert.Equal(model.FormDialog!.PixelWidth + 24, view.Bounds.Width);
        Assert.True(Right(scroller, window) <= window.Bounds.Width);
        Assert.False(scroller.AllowAutoHide);
        window.Close();
    });
}
