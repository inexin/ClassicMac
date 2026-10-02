using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Images;

namespace ClassicMac.App.Tests;

using static Headless;

// The dialogs (design/boards/dialogs.md, P5): one frame for all of them (a header with the title and ×, or an alert's
// icon and question; the body; a footer with Cancel then the primary action on the right and a destructive choice on the
// left), and each dialog's own fields.
public class DialogTests
{
    private static List<Button> FooterButtons(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DialogFooter").GetVisualDescendants().OfType<Button>().ToList();

    private static Button Footer(Window window, string label) => FooterButtons(window).Single(b => (string?)b.Content == label);

    private static T Show<T>(T built) where T : IDialog
    {
        built.Window.Show();
        Dispatcher.UIThread.RunJobs();
        return built;
    }

    // The frame every dialog shares: header (or none for an alert), body, footer with Cancel then the primary.
    private static void AssertFrame(Window window, string? title, string primary, string cancel, string? left = null)
    {
        var header = window.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "DialogHeader");
        if (title is null)
        {
            Assert.Null(header);
            Assert.Contains(window.GetVisualDescendants().OfType<Control>(), c => c.Name == "AlertIcon");
        }
        else
        {
            Assert.NotNull(header);
            Assert.Contains(header!.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == title);
            Assert.Contains(header.GetVisualDescendants().OfType<Button>(), b => b.Name == "DialogClose");
        }

        Assert.Equal(Avalonia.Controls.WindowDecorations.None, window.WindowDecorations);
        var buttons = FooterButtons(window);
        var right = buttons.Where(b => !b.Classes.Contains("destructive")).ToList();
        Assert.Equal([cancel, primary], right.Select(b => (string?)b.Content));
        Assert.True(right[0].IsCancel);
        Assert.True(right[1].IsDefault);
        Assert.Contains("accent", right[1].Classes);
        Assert.True(right[0].Bounds.Right <= right[1].Bounds.Left);
        if (left is not null)
        {
            var destructive = buttons.Single(b => b.Classes.Contains("destructive"));
            Assert.Equal(left, destructive.Content);
            double X(Control c) => c.TranslatePoint(default, window)!.Value.X;
            Assert.True(X(destructive) + destructive.Bounds.Width < X(right[0]));   // on the left, apart
        }
    }

    [Fact]
    public void Get_info_has_the_frame_mono_fields_and_two_columns_of_attributes() => OnUiThread(() =>
    {
        var dialog = Show(DialogViews.ResourceInfo("Get Info", new ResourceInfo("ICN#", 128, "Trash", ResourceAttributes.Purgeable | ResourceAttributes.Compressed), false));
        AssertFrame(dialog.Window, "Get Info", "OK", "Cancel");
        var inputs = dialog.Window.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Contains("mono", inputs.Single(t => t.Name == "Type").Classes);
        Assert.True(inputs.Single(t => t.Name == "Type").IsReadOnly);
        var labels = dialog.Window.GetVisualDescendants().OfType<Label>().ToList();
        Assert.Same(inputs.Single(t => t.Name == "Type"), labels.Single(l => (string?)l.Content == "Type").Target);
        var boxes = dialog.Window.GetVisualDescendants().OfType<CheckBox>().ToList();
        Assert.Equal(["System heap", "Purgeable", "Locked", "Protected", "Preload", "Compressed"], boxes.Select(b => (string?)b.Content));
        Assert.False(boxes[5].IsEnabled);                                    // can't be set by hand
        Assert.True(boxes[5].IsChecked);
        Assert.Equal(boxes[0].Bounds.X, boxes[2].Bounds.X);                  // two columns
        Assert.NotEqual(boxes[0].Bounds.X, boxes[1].Bounds.X);
        boxes[2].IsChecked = true;
        inputs.Single(t => t.Name == "Name").Text = "Bin";
        Footer(dialog.Window, "OK").Command!.Execute(null);
        Assert.Equal(new ResourceInfo("ICN#", 128, "Bin", ResourceAttributes.Purgeable | ResourceAttributes.Locked | ResourceAttributes.Compressed),
            dialog.Result);
        Assert.False(dialog.Window.IsVisible);
    });

    [Fact]
    public void New_resource_lets_the_type_be_typed_and_cancel_or_close_gives_nothing() => OnUiThread(() =>
    {
        var dialog = Show(DialogViews.ResourceInfo("New Resource", new ResourceInfo("STR ", 128, "", ResourceAttributes.None), true));
        Assert.False(dialog.Window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "Type").IsReadOnly);
        Footer(dialog.Window, "Cancel").Command!.Execute(null);
        Assert.Null(dialog.Result);

        var closed = Show(DialogViews.ResourceInfo("New Resource", new ResourceInfo("STR ", 128, "", ResourceAttributes.None), true));
        closed.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DialogClose").Command!.Execute(null);
        Assert.Null(closed.Result);
        Assert.False(closed.Window.IsVisible);
    });

    [Fact]
    public void Import_offers_what_to_make_as_choices() => OnUiThread(() =>
    {
        IReadOnlyList<string> types = [.. ImageImport.Types, MainViewModel.IconFamily];
        var dialog = Show(DialogViews.Import("art.png", types, new ImportChoice("icl8", 128, "")));
        AssertFrame(dialog.Window, "Import “art.png”", "Import", "Cancel");
        var options = dialog.Options;
        Assert.Equal(["Picture", "Color icon", "Icon family", "One icon kind", "Cursor", "Color cursor", "Sound"], options.Options.Select(o => o.Label));
        Assert.Equal("One icon kind", options.Selected.Label);
        Assert.Equal("icl8", options.Kind);
        Assert.False(options.Options.Single(o => o.Label == "Sound").IsEnabled);
        Assert.Equal("needs an audio file", options.Options.Single(o => o.Label == "Sound").Note);
        var radios = dialog.Window.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(7, radios.Count);
        options.Selected = options.Options.Single(o => o.Label == "Icon family");
        Footer(dialog.Window, "Import").Command!.Execute(null);
        Assert.Equal(new ImportChoice(MainViewModel.IconFamily, 128, ""), dialog.Result);
    });

    [Fact]
    public void Import_options_follow_the_types_offered()
    {
        var sound = new ImportOptions(["snd "], "snd ");
        Assert.Equal("Sound", sound.Selected.Label);
        Assert.Equal("snd ", sound.Type);
        Assert.All(sound.Options.Where(o => o.Label != "Sound"), o => Assert.False(o.IsEnabled));
        Assert.Equal("needs an image", sound.Options[0].Note);

        var image = new ImportOptions([.. ImageImport.Types, MainViewModel.IconFamily], "PICT");
        Assert.Equal(("Picture", "PICT"), (image.Selected.Label, image.Type));
        Assert.Equal("PICT", image.Selected.Code);
        Assert.Equal(["ICN#", "icl8", "icl4", "ics#", "ics8", "ics4", "icm#", "icm8", "icm4", "ICON"], image.Kinds);
        image.Selected = image.Options.Single(o => o.Label == "One icon kind");
        Assert.Equal("ICN#", image.Type);
        image.Kind = "ics4";
        Assert.Equal("ics4", image.Type);
        image.Selected = image.Options.Single(o => o.Label == "Color cursor");
        Assert.Equal("crsr", image.Type);
        image.Selected = image.Options.Single(o => o.Label == "Sound");                // not offered: stays
        Assert.Equal("crsr", image.Type);
    }

    [Fact]
    public void New_file_and_new_folder_share_the_frame() => OnUiThread(() =>
    {
        var file = Show(DialogViews.NewFile("New File", new NewFileChoice("untitled", "TEXT", "ttxt")));
        AssertFrame(file.Window, "New File", "Create", "Cancel");
        var boxes = file.Window.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Contains("mono", boxes.Single(b => b.Name == "Type").Classes);
        Assert.Contains("mono", boxes.Single(b => b.Name == "Creator").Classes);
        boxes.Single(b => b.Name == "Name").Text = "Read Me";
        Footer(file.Window, "Create").Command!.Execute(null);
        Assert.Equal(new NewFileChoice("Read Me", "TEXT", "ttxt"), file.Result);

        var folder = Show(DialogViews.NewFolder("untitled folder"));
        AssertFrame(folder.Window, "New Folder", "Create", "Cancel");
        Footer(folder.Window, "Create").Command!.Execute(null);
        Assert.Equal("untitled folder", folder.Result);
    });

    [Theory]
    [InlineData(SaveChanges.Save, "Save")]
    [InlineData(SaveChanges.Discard, "Don’t Save")]
    [InlineData(SaveChanges.Cancel, "Cancel")]
    public void Unsaved_changes_is_an_alert_with_dont_save_on_the_left(SaveChanges choice, string button) => OnUiThread(() =>
    {
        var dialog = Show(DialogViews.SaveChanges("Mac OS 9.hfv"));
        AssertFrame(dialog.Window, null, "Save", "Cancel", "Don’t Save");
        Assert.Contains(dialog.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Save changes to “Mac OS 9.hfv” before closing?");
        Assert.Contains(dialog.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "If you don’t save, the changes are lost.");
        Assert.Equal(SaveChanges.Cancel, dialog.Result);                    // closed by other means: cancel
        FooterButtons(dialog.Window).Single(b => (string?)b.Content == button).Command!.Execute(null);
        Assert.Equal(choice, dialog.Result);
    });

    [Fact]
    public void Unapplied_changes_is_an_alert_with_discard_on_the_left_and_no_apply_on_an_error() => OnUiThread(() =>
    {
        var dialog = Show(DialogViews.ApplyDraft("'STR#' 128", null));
        AssertFrame(dialog.Window, null, "Apply", "Cancel", "Discard");
        Footer(dialog.Window, "Discard").Command!.Execute(null);
        Assert.Equal(DraftChoice.Discard, dialog.Result);

        var error = Show(DialogViews.ApplyDraft("'MENU' 129", "Item 4 uses ⌘S twice."));
        Assert.False(Footer(error.Window, "Apply").IsEffectivelyEnabled);
        Assert.False(Footer(error.Window, "Apply").IsDefault);
        Assert.Contains(error.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text!.Contains("Item 4 uses ⌘S twice.", StringComparison.Ordinal));
        Footer(error.Window, "Cancel").Command!.Execute(null);
        Assert.Equal(DraftChoice.Cancel, error.Result);
    });

    [Fact]
    public void A_confirmation_is_an_alert_with_no_then_yes() => OnUiThread(() =>
    {
        var dialog = Show(DialogViews.Confirm("Delete", "Delete “Read Me” from the volume?"));
        AssertFrame(dialog.Window, null, "Yes", "No");
        Assert.Contains(dialog.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Delete “Read Me” from the volume?");
        Footer(dialog.Window, "Yes").Command!.Execute(null);
        Assert.True(dialog.Result);
    });

    [Fact]
    public void Dialogs_draw_in_light_and_dark() => OnUiThread(() =>
    {
        var baselines = new List<string>();
        var info = Show(DialogViews.ResourceInfo("Get Info", new ResourceInfo("ICN#", 128, "Trash", ResourceAttributes.Purgeable), false));
        Baselines.Check(info.Window, "dialog-get-info", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
        info.Window.Close();
        var save = Show(DialogViews.SaveChanges("Mac OS 9.hfv"));
        Baselines.Check(save.Window, "dialog-unsaved", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
        save.Window.Close();
        var import = Show(DialogViews.Import("art.png", [.. ImageImport.Types, MainViewModel.IconFamily], new ImportChoice("PICT", 128, "")));
        Baselines.Check(import.Window, "dialog-import", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
        import.Window.Close();
        Baselines.Verify(baselines);
    });
}
