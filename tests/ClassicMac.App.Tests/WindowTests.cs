using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Decoders.Interface;
using MacMenuItem = ClassicMac.Resources.Decoders.Interface.MenuItem;
using MenuItem = Avalonia.Controls.MenuItem;

namespace ClassicMac.App.Tests;

using static Headless;

// The main window loads its XAML and draws, headless (Skia, no screen): the tree, an image preview, a styled-text
// preview, the hex view, the tree's context menu and a sound's waveform. With CLASSICMAC_SCREENSHOT set to a path,
// the frames are saved beside it for a look.
public class WindowTests
{
    private static void OnUiThread(Action test) => Headless.OnUiThread(test);

    private static void Pump(Task task) => Headless.Pump(task);

    // Draws the window; with a list, also compares the frame with its baselines in light, dark and at 150% (Baselines).
    private static void Capture(MainWindow window, string name, List<string>? baselines = null)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 600);
        if (Environment.GetEnvironmentVariable("CLASSICMAC_SCREENSHOT") is { Length: > 0 } shot)
        {
#pragma warning disable CS0618 // the simple overload is enough for a test snapshot
            frame.Save(Path.Combine(Path.GetDirectoryName(shot)!, $"{Path.GetFileNameWithoutExtension(shot)}-{name}.png"));
#pragma warning restore CS0618
        }
        if (baselines is not null)
        {
            Baselines.Check(window, name, baselines);
        }
    }

    [Fact]
    public void The_image_grid_draws_a_family_and_virtualises_hundreds_of_cards() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-grid-").FullName;
        try
        {
            var path = Path.Combine(folder, "Icons.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(
                ("ICN#", 128, null, [.. Enumerable.Range(0, 128).Select(i => (byte)(i % 8 < 4 ? 0xF0 : 0x0F)), .. Enumerable.Repeat((byte)0xFF, 128)]),
                ("icl8", 128, null, Enumerable.Range(0, 1024).Select(i => (byte)(i / 32 * 8)).ToArray()),
                ("ics#", 128, null, [.. Enumerable.Repeat((byte)0x3C, 32), .. Enumerable.Repeat((byte)0xFF, 32)]),
                ("SICN", 128, null, Enumerable.Range(0, 500 * 32).Select(i => (byte)(i * 7)).ToArray())));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            var types = open.Result!.Children.OfType<ResourceTypeNode>().ToList();
            model.Selected = types.Single(t => t.Type.ToString() == "ICN#").Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("image-card")).ToList();
            Assert.Equal(3, cards.Count);
            Assert.Equal(model.ImageCardWidth, cards[0].Bounds.Width);
            Capture(window, "image-family", baselines);

            window.FindControl<CheckBox>("ShowMasksBox")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(5, window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("image-card")));
            window.FindControl<ScrollViewer>("ImageScroller")!.ScrollToEnd();       // the Finder states, wrapped to the width
            Dispatcher.UIThread.RunJobs();
            Capture(window, "image-states", baselines);

            // 500 icons: only the rows on screen are realised, and scrolling realises the later ones.
            model.Selected = types.Single(t => t.Type.ToString() == "SICN").Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(500, model.Images.Count);
            List<string?> Shown() => window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("image-card") && b.IsEffectivelyVisible)
                .Select(b => ((ImageItem)b.DataContext!).Image.Title).ToList();
            var shown = Shown();
            Assert.InRange(shown.Count, 1, 100);
            Assert.Contains("'SICN' #1", shown);
            Assert.DoesNotContain("'SICN' #500", shown);
            var scroller = window.FindControl<ScrollViewer>("ImageScroller")!;
            scroller.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("'SICN' #500", Shown());
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void A_JSON_preview_draws_as_property_cards_with_a_JSON_switch() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-props-").FullName;
        try
        {
            var path = Path.Combine(folder, "Styles.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("styl", 128, null, PreviewTests.Styl((0, 20, 1, 18, 0, 0, 0), (6, 4, 0, 10, 0xFFFF, 0, 0)))));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single().Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var cards = window.FindControl<ScrollViewer>("PropertyCards")!;
            Assert.True(cards.IsEffectivelyVisible);
            var rows = cards.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("property-row")).ToList();
            Assert.Equal(14, rows.Count);                                              // two runs of seven values
            var font = rows.Single(r => r.DataContext is PropertyRow { Label: "Font", Raw: "20" });
            Assert.Equal(["Copy as _Decimal", "Copy as _Hex", "Copy as _JSON"], font.ContextMenu!.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
            Capture(window, "properties", baselines);

            window.FindControl<ListBox>("PropertyMode")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.ShowJson);
            Assert.False(cards.IsEffectivelyVisible);
            Capture(window, "properties-json");
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void The_main_window_draws_the_tree_and_previews() => OnUiThread(The_main_window_draws_the_tree_and_previewsBody);

    private static void The_main_window_draws_the_tree_and_previewsBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var text = MacRoman.Encode("Divinity\rThis is NOT the full version of the manual.\rCafé ƒ™");
            var disk = new HfsBuilder();
            var games = disk.Folder(HfsBuilder.Root, "Games");
            disk.File(games, "Realmz", [1, 2, 3], [], type: "APPL", creator: "RLMZ");
            disk.File(HfsBuilder.Root, "Manual", [], PreviewTests.Fork(
                ("ICN#", 128, null, [.. Enumerable.Range(0, 128).Select(i => (byte)(i % 8 < 4 ? 0xF0 : 0x0F)), .. Enumerable.Repeat((byte)0xFF, 128)]),
                ("TEXT", 128, null, text),
                ("snd ", 128, "Sine", SoundPreviewTests.Sound(20000)),
                ("styl", 128, null, PreviewTests.Styl((0, 20, 1, 24, 0, 0, 0), (9, 3, 0, 12, 0, 0, 0), (17, 3, 1, 12, 0xFFFF, 0, 0), (20, 3, 0, 12, 0, 0, 0)))));
            var path = Path.Combine(folder, "disk.img");
            File.WriteAllBytes(path, disk.Build("Disk"));

            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            Pump(model.OpenAsync(path));
            var manual = model.Roots[0].Children.OfType<FileNode>().Single(f => f.Title == "Manual");
            manual.IsExpanded = true;
            Pump(manual.EnsureLoadedAsync());
            Capture(window, "tree", baselines);

            var types = manual.Children.OfType<ResourceTypeNode>().ToList();
            model.Selected = types.Single(t => t.Type.ToString() == "ICN#").Children[0];
            Pump(model.PreviewTask);
            Capture(window, "icon", baselines);

            model.Selected = types.Single(t => t.Type.ToString() == "TEXT").Children[0];
            Pump(model.PreviewTask);
            Capture(window, "text", baselines);
            // The text keeps clear of the overlay scroll bar on the right.
            var styledScroller = window.FindControl<ScrollViewer>("StyledTextScroller")!;
            Assert.True(styledScroller.Padding.Right >= 24);

            model.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            Capture(window, "hex");

            // The tree's context menu, on a resource: the Resource commands and Save Resource As apply, not the file's.
            var tree = window.FindControl<BrowseTree>("Tree")!;
            var menu = tree.ContextMenu!;
            menu.Open(tree);
            Dispatcher.UIThread.RunJobs();
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["_New Resource…", "_Duplicate", "De_lete", "Get _Info…", "Edit _Hex…", "_Replace Data from File…", "I_mport Image or Sound…",
                "_Save Resource As…"],
                items.Where(i => i.Command?.CanExecute(null) == true).Select(i => (string)i.Header!));
            Capture(window, "context-menu");
            menu.Close();

            model.Selected = types.Single(t => t.Type.ToString() == "snd ").Children[0];
            Pump(model.PreviewTask);
            Assert.True(model.Preview.IsSound);
            Assert.True(window.FindControl<Button>("SaveAsWavButton")!.IsEffectivelyVisible);     // the sound's header actions
            Assert.True(window.FindControl<Button>("ReplaceFromWavButton")!.IsEffectivelyVisible);
            Capture(window, "sound", baselines);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_document_preview_shows_its_text_without_a_web_view() => OnUiThread(The_document_preview_shows_its_text_without_a_web_viewBody);

    private static void The_document_preview_shows_its_text_without_a_web_viewBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            Pump(model.OpenAsync(DocumentTests.Disk(folder)));
            model.Selected = model.Roots[0].Children.Single(c => c.Title == "Manual");
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "document", baselines);
            // Headless there is no native web view: the chapter list and Back stay, and the text shows with why.
            Assert.Contains("no native web view", model.WebEngineMessage!, StringComparison.Ordinal);
            var chapters = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "DocumentChapters");
            Assert.True(chapters.IsEffectivelyVisible);
            Assert.Equal(1, chapters.SelectedIndex);
            var text = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "HelpSource");
            Assert.True(text.IsEffectivelyVisible);
            Assert.Contains("The game begins here.", text.Text, StringComparison.Ordinal);
            Assert.Contains("IBM Plex Sans", text.FontFamily.ToString(), StringComparison.Ordinal); // a document's text, not mono
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Dialogs_and_menus_draw() => OnUiThread(Dialogs_and_menus_drawBody);

    private static void Dialogs_and_menus_drawBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            Pump(model.OpenAsync(InterfacePreviewTests.Disk(folder)));
            var file = model.Roots[0].Children.OfType<FileNode>().Single();
            Pump(file.EnsureLoadedAsync());
            model.Selected = InterfacePreviewTests.Resource(file, "DLOG", 128);
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "dialog", baselines);
            var dialog = new DialogView { Dialog = model.Preview.Dialog, Scale = 2 };
            dialog.Measure(Size.Infinity);
            Assert.Equal((2 * (280 + 13 + 24), 2 * (120 + 34 + 24)), (dialog.DesiredSize.Width, dialog.DesiredSize.Height)); // the frame, gutters
            model.Selected = InterfacePreviewTests.Resource(file, "MENU", 128);
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Capture(window, "menu", baselines);
            var menu = new MenuView { Menu = model.Preview.Menu, Scale = 1 };
            menu.Measure(Size.Infinity);
            Assert.Equal(20 + 3 * 16 + 24 + 3, menu.DesiredSize.Height); // the bar, three rows, gutters and frame
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Imported_images_are_read_as_unpremultiplied_rgba() => OnUiThread(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"cm-import-{Guid.NewGuid():N}.png");
        byte[] rgba = [255, 0, 0, 255, 0, 0, 255, 128, 9, 9, 9, 0, 10, 200, 30, 255];
        File.WriteAllBytes(path, ClassicMac.Resources.Decoders.Images.PngEncoder.Instance.Encode(2, 2, rgba));
        try
        {
            var image = new MainViewModel().LoadImage(path);
            Assert.Equal((2, 2), (image.Width, image.Height));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Pixels[..4]);
            Assert.Equal(new byte[] { 10, 200, 30, 255 }, image.Pixels[12..]);
            Assert.Equal(128, image.Pixels[7]);
            Assert.InRange(image.Pixels[6], 253, 255);                                  // premultiplied and back
            Assert.Equal(0, image.Pixels[11]);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void The_edit_tab_shows_a_form_for_text_resources() => OnUiThread(() =>
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("cm-forms-").FullName, "Forms.rsrc"); // a fixed name, as it is drawn
        byte[] vers = [0x01, 0x20, 0x60, 0x03, 0, 0, 5, .. "1.2b3"u8, 9, .. "1.2b3 (c)"u8];
        var items = InterfaceWriter.WriteDialogItems([
            new DialogItem(new MacRect(70, 150, 90, 220), 4, true, "OK", null, ReadOnlyMemory<byte>.Empty),
            new DialogItem(new MacRect(10, 10, 50, 220), 8, false, "Hello there", null, ReadOnlyMemory<byte>.Empty)]);
        var dialog = InterfaceWriter.WriteWindow(new WindowTemplate(new MacRect(40, 40, 140, 280), 1, true, false, 0, "", 128, null), dialog: true);
        var menu = InterfaceWriter.WriteMenu(new MenuResource(128, 0, 0, 0, 0xFFFFFFFF, "File",
            [new MacMenuItem("Open…", 0, (byte)'O', 0, 0, true), new MacMenuItem("-", 0, 0, 0, 0, false), new MacMenuItem("Quit", 0, (byte)'Q', 0, 0, true)]));
        File.WriteAllBytes(path, PreviewTests.Fork(("STR#", 128, null, [0, 2, 3, .. "one"u8, 3, .. "two"u8]), ("vers", 1, null, vers),
            ("TEXT", 128, null, "Some text"u8.ToArray()), ("DITL", 128, null, items), ("DLOG", 128, null, dialog), ("MENU", 128, null, menu),
            ("TMPL", 128, "Rsrc", EditTests.Tmpl(("Name", "PSTR"), ("Count", "OCNT"), ("*****", "LSTC"), ("Flag", "BOOL"), ("*****", "LSTE"))),
            ("Rsrc", 128, null, [2, .. "ab"u8, 0, 1, 1, 0])));
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            var input = open.Result!;
            Pump(input.EnsureLoadedAsync());
            foreach (var (type, form) in new[] { ("STR#", typeof(StringListForm)), ("vers", typeof(VersionForm)), ("TEXT", typeof(TextForm)),
                ("DLOG", typeof(WindowForm)), ("DITL", typeof(DialogItemsForm)), ("MENU", typeof(MenuForm)),
                ("Rsrc", typeof(TemplateForm)) })
            {
                model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];
                Pump(model.PreviewTask);
                Assert.IsType(form, model.Form);
                model.EditFormCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Capture(window, "edit-" + type.TrimEnd('#'), type is "DITL" or "Rsrc" ? baselines : null);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
            }

            // The template's count field stays text while editing (no input), with its note (E6).
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsReadOnly && t.IsEffectivelyVisible);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "kept in step with the list");

            // The item list's preview follows its form before Apply.
            model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "DITL").Children[0];
            Pump(model.PreviewTask);
            var ditl = Assert.IsType<DialogItemsForm>(model.Form);
            ditl.Items[1].Text = "Changed";
            Assert.Equal("Changed", model.FormDialog!.Drawing.Items[1].Item.Text);
            ditl.Items[0].Text = "日本";
            Assert.Contains("Mac OS Roman", model.FormError);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    });

    [Fact]
    public void The_hex_tab_edits_bytes_with_the_keyboard() => OnUiThread(() =>
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("cm-hexedit-").FullName, "Hex.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("TEXT", 128, null, "Hello, hex editing works across lines"u8.ToArray())));
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            var input = open.Result!;
            Pump(input.EnsureLoadedAsync());
            model.Selected = input.Children.OfType<ResourceTypeNode>().Single().Children[0];
            Pump(model.PreviewTask);
            model.BeginHexEditCommand.Execute(null);
            model.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "HexList");
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.D4 });
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.D8 });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((byte)'H', model.HexEdit!.ToArray()[0]);
            Assert.Equal(1, model.HexEdit.Cursor);
            Capture(window, "hex-edit", baselines);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    });

    // Leaving a large fork's hex view must not walk its lines: an items control told its source is now null walks the
    // old one (Avalonia's ItemCollection raises a Remove of every item), which for a disk image was every line of it.
    [Fact]
    public void Leaving_a_large_fork_s_hex_view_does_not_read_it() => OnUiThread(Leaving_a_large_fork_s_hex_view_does_not_read_itBody);

    private static void Leaving_a_large_fork_s_hex_view_does_not_read_itBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var disk = new HfsBuilder();
            disk.File(disk.Folder(HfsBuilder.Root, "Empty"), "Note", [1], []);
            var path = Path.Combine(folder, "disk.img");
            File.WriteAllBytes(path, disk.Build("Disk"));
            // A fork with an 8 MB resource of a type nothing previews: shown in hex.
            var big = Path.Combine(folder, "big.rsrc");
            File.WriteAllBytes(big, PreviewTests.Fork(("ZZZZ", 128, null, [.. Enumerable.Range(0, 8 << 20).Select(i => (byte)(i % 251))])));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            Pump(model.OpenAsync(path));
            Pump(model.OpenAsync(big));
            Pump(model.Roots[1].EnsureLoadedAsync());
            model.Selected = model.Roots[1].Children.OfType<ResourceTypeNode>().Single().Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var lines = model.HexLines!;
            Assert.Equal(1 << 19, lines.Count);

            model.Selected = model.Roots[0].Children.OfType<FolderNode>().Single();
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(model.HexLines);
            Assert.True(lines.BytesRead <= 1 << 20, $"{lines.BytesRead} bytes read");
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // The unapplied-draft question, answered when the test says so.
    private sealed class DraftDialogs : IEditDialogs
    {
        public TaskCompletionSource<DraftChoice> Pending { get; set; } = new();
        public int Asked { get; private set; }

        public Task<DraftChoice> AskApplyDraftAsync(string what, string? error)
        {
            Asked++;
            return Pending.Task;
        }

        public Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew, DialogSubject? subject) => throw new NotSupportedException();
        public Task<SaveChanges> AskSaveChangesAsync(string fileName, string edited) => Task.FromResult(SaveChanges.Discard);
        public Task<bool> ConfirmAsync(string title, string message) => throw new NotSupportedException();
        public Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial, ImportSource source) => throw new NotSupportedException();
        public Task<NewFileChoice?> NewFileAsync(string title, NewFileChoice initial) => throw new NotSupportedException();
        public Task<string?> NewFolderAsync(string initial) => throw new NotSupportedException();
    }

    // Clicking or arrowing to another tree node with an unapplied form: the tree keeps the old node selected and nothing
    // changes until the question is answered.
    [Fact]
    public void The_tree_keeps_its_selection_while_a_draft_is_asked_about() => OnUiThread(The_tree_keeps_its_selection_while_a_draft_is_asked_aboutBody);

    private static void The_tree_keeps_its_selection_while_a_draft_is_asked_aboutBody()
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var path = Path.Combine(folder, "strings.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("STR ", 128, null, [5, .. "hello"u8]), ("STR ", 129, null, [2, .. "hi"u8])));
            var dialogs = new DraftDialogs();
            var model = new MainViewModel { EditDialogs = dialogs };
            var window = new MainWindow { DataContext = model, Width = 1200, Height = 800 };
            window.Show();
            Pump(model.OpenAsync(path));
            Pump(model.Roots[0].EnsureLoadedAsync());
            var type = model.Roots[0].Children.OfType<ResourceTypeNode>().Single();
            type.IsExpanded = true;
            model.Selected = type.Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var tree = window.FindControl<BrowseTree>("Tree")!;
            Assert.Same(type.Children[0], tree.SelectedItem);
            var form = Assert.IsType<StringForm>(model.Form);
            form.Text = "edited";
            var details = model.Details;

            // A click on 'STR ' 129.
            var item = Assert.IsType<BrowseRow>(tree.ContainerFromItem(type.Children[1]));
            var header = item.GetVisualDescendants().OfType<Control>().First(c => c.Bounds.Height > 0);
            var point = header.TranslatePoint(new Point(10, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, dialogs.Asked);
            Assert.Same(type.Children[0], model.Selected);
            Assert.Same(type.Children[0], tree.SelectedItem);
            Assert.Same(form, model.Form);
            Assert.Same(details, model.Details);
            dialogs.Pending.SetResult(DraftChoice.Cancel);
            Pump(model.DraftTask);
            Assert.Same(type.Children[0], tree.SelectedItem);
            Assert.Equal("edited", form.Text);

            // The down arrow, from the focused 'STR ' 128.
            dialogs.Pending = new TaskCompletionSource<DraftChoice>();
            Assert.IsType<BrowseRow>(tree.ContainerFromItem(type.Children[0])).Focus();
            window.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
            window.KeyRelease(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, dialogs.Asked);
            Assert.Same(type.Children[0], model.Selected);
            Assert.Same(type.Children[0], tree.SelectedItem);
            dialogs.Pending.SetResult(DraftChoice.Discard);
            Pump(model.DraftTask);
            Assert.Same(type.Children[1], model.Selected);
            Assert.Same(type.Children[1], tree.SelectedItem);
            Assert.Equal("hi", Assert.IsType<StringForm>(model.Form).Text);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // The question as the window asks it: Apply (Enter), Discard, Cancel (Esc); no Apply when the draft has an error.
    [Fact]
    public void The_draft_question_offers_apply_discard_and_cancel() => OnUiThread(() =>
    {
        var owner = new Window();
        owner.Show();
        var dialogs = new EditDialogs(owner);
        foreach (var (label, expected) in new[] { ("Apply", DraftChoice.Apply), ("Discard", DraftChoice.Discard), ("Cancel", DraftChoice.Cancel) })
        {
            var asked = dialogs.AskApplyDraftAsync("'STR#' 128", null);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Apply your changes to 'STR#' 128?", texts);
            Assert.Contains("You edited this resource but haven't applied the changes.", texts);
            var buttons = dialog.GetVisualDescendants().OfType<Button>().OrderBy(b => b.TranslatePoint(default, dialog)!.Value.X).ToList();
            Assert.Equal(["Discard", "Cancel", "Apply"], buttons.Select(b => (string)b.Content!));       // left to right
            Assert.True(buttons.Single(b => (string)b.Content! == "Apply").IsDefault);
            Assert.True(buttons.Single(b => (string)b.Content! == "Cancel").IsCancel);
            buttons.Single(b => (string)b.Content! == label).Command!.Execute(null);
            Pump(asked);
            Assert.Equal(expected, asked.Result);
        }

        var withError = dialogs.AskApplyDraftAsync("'STR ' 128", "The text must be Mac OS Roman.");
        Dispatcher.UIThread.RunJobs();
        var errorDialog = Assert.Single(owner.OwnedWindows);
        Assert.Contains(errorDialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("The text must be Mac OS Roman.") == true);
        var apply = errorDialog.GetVisualDescendants().OfType<Button>().Single(b => (string)b.Content! == "Apply");
        Assert.False(apply.IsEnabled);
        Assert.False(apply.IsDefault);
        errorDialog.Close();
        Pump(withError);
        Assert.Equal(DraftChoice.Cancel, withError.Result);
        owner.Close();
    });

    // S1 on Windows: an extended client area makes Avalonia draw its own title bar (PlatformRequestedDrawnDecoration
    // .TitleBar from the Win32 backend), whose overlay holds the window's Title and the caption buttons. The window's
    // decorations theme hides that title, so only the app's own title bar text shows, and keeps the caption buttons.
    // (The headless platform draws no decorations, so the theme is applied here to decorations made by hand.)
    [Fact]
    public void The_drawn_decorations_show_the_caption_buttons_but_not_a_second_title() => OnUiThread(() =>
    {
        var window = new MainWindow { DataContext = new MainViewModel() };
        window.Show();
        var theme = window.WindowDecorationsTheme;
        Assert.NotNull(theme);
        Assert.Equal(typeof(Avalonia.Controls.Chrome.WindowDrawnDecorations), theme!.TargetType);
        Assert.NotNull(theme.BasedOn);                                       // Fluent's: its template and caption buttons

        // Decorations as the Win32 backend has Window make them (a drawn title bar), hosted for styling in the window.
        List<Control> Overlay(Avalonia.Styling.ControlTheme with)
        {
            var decorations = new Avalonia.Controls.Chrome.WindowDrawnDecorations { Theme = with, Title = "Shell.rsrc — ClassicMac" };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(Avalonia.Controls.Chrome.WindowDrawnDecorations).GetProperty("EnabledParts", flags)!
                .SetValue(decorations, Enum.ToObject(typeof(Avalonia.Controls.Chrome.WindowDrawnDecorations).Assembly.GetType("Avalonia.Controls.Chrome.DrawnWindowDecorationParts")!, 4)); // TitleBar
            var host = new Panel();
            ((Avalonia.Controls.ISetLogicalParent)decorations).SetParent(host);
            window.Content = host;
            decorations.ApplyStyling();
            typeof(Avalonia.Controls.Chrome.WindowDrawnDecorations).GetMethod("ApplyTemplate", flags)!.Invoke(decorations, null);
            host.Children.Add(decorations.Content!.Overlay!);
            Dispatcher.UIThread.RunJobs();
            Assert.True(decorations.HasTitleBar);
            return decorations.Content.Overlay!.GetSelfAndVisualDescendants().OfType<Control>().ToList();
        }

        static bool ShowsTitle(List<Control> overlay) =>
            overlay.OfType<TextBlock>().Any(t => t.Text == "Shell.rsrc — ClassicMac" && t.IsEffectivelyVisible);

        Assert.True(ShowsTitle(Overlay(theme.BasedOn!)));                     // Fluent's draws the title over ours
        var all = Overlay(theme);
        Assert.False(ShowsTitle(all));                                         // ours does not
        // The caption buttons are Fluent's: close always shows; minimize and maximize show by the window's allowed
        // actions (pseudo-classes Window sets when it attaches the decorations, which these hand-made ones lack).
        Assert.True(all.Single(c => c.Name == "PART_CloseButton").IsVisible);
        Assert.Equal(Avalonia.Input.WindowDecorationsElementRole.CloseButton,
            Avalonia.Controls.Chrome.WindowDecorationProperties.GetElementRole(all.Single(c => c.Name == "PART_CloseButton")));
        Assert.Equal(Avalonia.Input.WindowDecorationsElementRole.MaximizeButton,
            Avalonia.Controls.Chrome.WindowDecorationProperties.GetElementRole(all.Single(c => c.Name == "PART_MaximizeButton")));
        Assert.Single(all, c => c.Name == "PART_MinimizeButton");

        window.Close();
    });

    // The shell (design/boards/main-window.md): the title bar (S1), the toolbar bound to the commands (S2), the View,
    // Window and Help menus and the About box (S7).
    [Fact]
    public void The_title_bar_toolbar_menus_and_about_box_work() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var path = Path.Combine(folder, "Shell.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("ICN#", 128, null, new byte[256]), ("TEXT", 128, null, "hello"u8.ToArray())));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            var input = open.Result!;
            Pump(input.EnsureLoadedAsync());
            Dispatcher.UIThread.RunJobs();

            // S1: on Windows and macOS the window extends into its decorations and shows its own title bar.
            var titleBar = window.FindControl<Border>("TitleBar")!;
            Assert.Equal(!OperatingSystem.IsLinux(), window.ExtendClientAreaToDecorationsHint);
            Assert.Equal(!OperatingSystem.IsLinux(), titleBar.IsVisible);
            Assert.Equal(Avalonia.Input.WindowDecorationsElementRole.TitleBar, Avalonia.Controls.Chrome.WindowDecorationProperties.GetElementRole(titleBar));
            Assert.Equal("Shell.rsrc — ClassicMac", window.Title);
            Assert.Contains(titleBar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Shell.rsrc" && t.IsEffectivelyVisible);

            // S2: each toolbar button runs its command and follows its enabled state.
            var tools = window.FindControl<Border>("Toolbar")!.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tool"))
                .ToDictionary(b => Avalonia.Automation.AutomationProperties.GetName(b)!);
            Assert.Equal(["Open", "Save", "Get Info", "Edit Hex", "Export", "Extract All", "Play"], tools.Keys);
            Assert.Same(model.OpenCommand, tools["Open"].Command);
            Assert.Same(model.SaveCommand, tools["Save"].Command);
            Assert.Same(model.GetInfoCommand, tools["Get Info"].Command);
            Assert.Same(model.EditHexCommand, tools["Edit Hex"].Command);
            Assert.Same(model.ExportResourcesCommand, tools["Export"].Command);
            Assert.Same(model.ExtractAllCommand, tools["Extract All"].Command);
            Assert.Same(model.PlaySoundCommand, tools["Play"].Command);
            void EnabledFollowCommands()
            {
                Dispatcher.UIThread.RunJobs();
                Assert.All(tools.Values, b => Assert.Equal(b.Command!.CanExecute(null), b.IsEffectivelyEnabled));
            }

            EnabledFollowCommands();
            Assert.False(tools["Edit Hex"].IsEffectivelyEnabled);              // the input: no resource
            var zoom = window.FindControl<ListBox>("ZoomChoice")!;
            var depth = window.FindControl<ComboBox>("DepthChoice")!;
            model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "ICN#").Children[0];
            Pump(model.PreviewTask);
            EnabledFollowCommands();
            Assert.True(tools["Edit Hex"].IsEffectivelyEnabled);
            Assert.True(zoom.IsEffectivelyEnabled);
            Assert.True(depth.IsEffectivelyEnabled);
            zoom.SelectedIndex = 3;
            Assert.Equal(8, model.Zoom);
            depth.SelectedIndex = 3;
            Assert.Equal(8, model.ScreenDepth);
            Pump(model.PreviewTask);
            Capture(window, "shell", baselines);
            model.SetScreenDepthCommand.Execute(32);
            Pump(model.PreviewTask);
            model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "TEXT").Children[0];
            Pump(model.PreviewTask);
            EnabledFollowCommands();
            Assert.False(zoom.IsEffectivelyEnabled);
            Assert.False(depth.IsEffectivelyEnabled);

            // S7: the menus, in the board's order; the View menu's theme and depth items; the Window menu's inputs.
            var menu = window.GetVisualDescendants().OfType<Menu>().First();
            var top = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["_File", "_Edit", "_View", "_Resource", "_Volume", "E_xport", "_Window", "_Help"], top.Select(m => (string)m.Header!));
            MenuItem Item(MenuItem parent, string header) => parent.Items.OfType<MenuItem>().Single(m => (string?)m.Header == header);
            var view = top[2];
            Assert.Same(model.ZoomInCommand, Item(view, "Zoom _In").Command);
            Assert.Same(model.ZoomOutCommand, Item(view, "Zoom _Out").Command);
            Assert.Same(model.ActualSizeCommand, Item(view, "_Actual Size").Command);
            var dark = Item(Item(view, "_Theme"), "_Dark");
            dark.Command!.Execute(dark.CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.True(dark.IsChecked);
            var system = Item(Item(view, "_Theme"), "_System");
            system.Command!.Execute(system.CommandParameter);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
            Item(view, "Show _Diagnostics").IsChecked = false;
            Assert.False(model.DiagnosticsPanel.IsExpanded);
            model.DiagnosticsPanel.IsExpanded = true;
            Item(view, "_Hide Invisible Files").IsChecked = false;
            Assert.False(model.TreeDisplay.HideInvisible);
            view.Open();
            Dispatcher.UIThread.RunJobs();
            var depths = Item(view, "Screen _Depth");
            depths.Open();
            Dispatcher.UIThread.RunJobs();
            var depthItems = Avalonia.LogicalTree.LogicalExtensions.GetLogicalChildren(depths).OfType<MenuItem>().ToList();
            Assert.Equal(["1-bit", "2-bit", "4-bit", "8-bit (256)", "16-bit", "32-bit"], depthItems.Select(m => (string)m.Header!));
            Assert.True(depthItems[5].IsChecked);
            depthItems[2].Command!.Execute(depthItems[2].CommandParameter);
            Assert.Equal(4, model.ScreenDepth);
            Dispatcher.UIThread.RunJobs();
            Assert.True(depthItems[2].IsChecked);
            depths.Close();
            view.Close();

            var windowMenu = top[6].Items.OfType<MenuItem>().ToList();
            Assert.Equal(["_Minimize", "_Zoom", "Shell.rsrc"], windowMenu.Select(m => (string)m.Header!));
            Assert.True(windowMenu[2].IsChecked);
            windowMenu[1].Command!.Execute(null);
            Assert.Equal(WindowState.Maximized, window.WindowState);
            windowMenu[1].Command!.Execute(null);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Same(model.ShowInputCommand, windowMenu[2].Command);

            var help = top[7].Items.OfType<MenuItem>().ToList();
            Assert.Same(model.OpenHelpCommand, help[0].Command);
            Assert.Same(model.ReportProblemCommand, help[1].Command);
            Assert.Same(model.AboutCommand, help[2].Command);
            var about = model.AboutCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var box = window.About!;
            Assert.Contains(box.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == $"Version {AboutInfo.Current.Version}");
            Assert.Contains("IBM Plex", box.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "Notices").Text);
            Baselines.Check(box, "about", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
            box.Close();
            Pump(about);
            Assert.Null(window.About);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // The read-then-edit host with the menu form (design/boards/read-then-edit.md, E1 and E2): read only first, a
    // double-click on a row edits with it selected, rows and the preview select each other, a duplicate Command key shows
    // on both key fields with Apply disabled, Esc cancels and Ctrl+Enter applies.
    [Fact]
    public void The_menu_form_reads_then_edits_in_place() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var path = Path.Combine(folder, "Menus.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("MENU", 129, null, InterfaceWriter.WriteMenu(MenuFormTests.File()))));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single().Children[0];
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.IsType<MenuForm>(model.Form);
            var host = window.FindControl<DockPanel>("FormHost")!;
            Assert.True(host.IsEffectivelyVisible);
            Assert.True(window.FindControl<StackPanel>("ReadOnlyFooter")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<DockPanel>("EditingFooter")!.IsEffectivelyVisible);
            var rows = host.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("table-row")).ToList();
            Assert.Equal(5, rows.Count);
            Assert.DoesNotContain(host.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);   // read only
            Assert.Contains(rows[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Open…" && t.IsEffectivelyVisible);
            Assert.Contains(rows[1].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "divider" && t.IsEffectivelyVisible);
            var checkMark = rows[2].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Check mark" && t.IsEffectivelyVisible);
            Assert.True(checkMark.TextLayout.WidthIncludingTrailingWhitespace <= checkMark.Bounds.Width + 0.5, "the Mark cell cuts “Check mark”");
            Capture(window, "menu-form", baselines);

            // A double-click on the third row: editing, with that row selected and highlighted in the preview.
            var cell = rows[2].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Save");
            cell.RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.InputElement.DoubleTappedEvent, null!));
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsEditingForm);
            Assert.Same(menu.Items[2], menu.SelectedItem);
            var preview = host.GetVisualDescendants().OfType<MenuView>().Single();
            Assert.Equal(2, preview.SelectedIndex);
            Assert.Contains("selected", rows[2].Classes);
            Assert.True(window.FindControl<DockPanel>("EditingFooter")!.IsEffectivelyVisible);
            Assert.Equal("Live preview · unapplied changes", host.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewTitle").Text);

            // The preview selects its row: an item under a click.
            preview.SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(menu.Items[4], menu.SelectedItem);
            Assert.Contains("selected", rows[4].Classes);
            Assert.DoesNotContain("selected", rows[2].Classes);

            // The move buttons are chevron icons, not glyphs; Alt+Up and Alt+Down move the selected row.
            var moves = host.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible
                && Avalonia.Automation.AutomationProperties.GetName(b) is "Move up" or "Move down").ToList();
            Assert.Equal(10, moves.Count);
            Assert.All(moves, b => Assert.IsType<PathIcon>(b.Content));
            void AltKey(Avalonia.Input.Key key) => host.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible).RaiseEvent(
                new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key, KeyModifiers = Avalonia.Input.KeyModifiers.Alt });
            AltKey(Avalonia.Input.Key.Up);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(("Quit", "Save As…"), (menu.Items[3].Text, menu.Items[4].Text));
            Assert.Same(menu.Items[3], menu.SelectedItem);
            AltKey(Avalonia.Input.Key.Down);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Quit", menu.Items[4].Text);

            // The Mark select: words, its code in the list, the word fitting the select; Other… shows the code field.
            var marks = host.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && c.SelectedItem is MarkChoice).ToList();
            Assert.Equal(5, marks.Count);
            Assert.NotNull(marks[0].ItemTemplate);
            Assert.All(marks, c => Assert.All(c.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
                t => Assert.True(t.TextLayout.WidthIncludingTrailingWhitespace <= t.Bounds.Width + 0.5, $"“{t.Text}” is cut ({t.TextLayout.WidthIncludingTrailingWhitespace} in {t.Bounds.Width})")));
            Assert.Contains(marks[2].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Check mark" && t.IsEffectivelyVisible);
            var codes = host.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("mark-code")).ToList();
            Assert.DoesNotContain(codes, t => t.IsEffectivelyVisible);
            menu.Items[0].MarkChoice = MenuItemRow.Marks[^1];
            Dispatcher.UIThread.RunJobs();
            Assert.Single(codes, t => t.IsEffectivelyVisible);
            Assert.All(marks[0].GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible),
                t => Assert.True(t.TextLayout.WidthIncludingTrailingWhitespace <= t.Bounds.Width + 0.5, $"“{t.Text}” is cut beside the code ({t.TextLayout.WidthIncludingTrailingWhitespace} in {t.Bounds.Width})"));
            menu.Items[0].MarkChoice = MenuItemRow.Marks[0];
            Dispatcher.UIThread.RunJobs();

            // ⌘S twice: both key fields marked, the error shown, Apply disabled.
            menu.Items[3].Key = "S";
            Dispatcher.UIThread.RunJobs();
            var keys = host.GetVisualDescendants().OfType<TextBox>().Where(t => t.Classes.Contains("key")).ToList();
            Assert.Equal([false, false, true, true, false], keys.Select(k => k.Classes.Contains("conflict")));
            Assert.True(window.FindControl<StackPanel>("FormErrorLine")!.IsEffectivelyVisible);
            var apply = window.FindControl<DockPanel>("EditingFooter")!.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == "Apply");
            Assert.False(apply.IsEffectivelyEnabled);
            Capture(window, "menu-form-editing", baselines);

            // Esc in a text box cancels: read only again, the draft gone.
            var text = host.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
            text.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.IsEditingForm);
            Assert.False(model.HasDraft);

            // Edit again, change the title, Ctrl+Enter applies one undoable edit.
            model.EditFormCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<MenuForm>(model.Form).Title = "Fichier";
            text = host.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
            text.RaiseEvent(new Avalonia.Input.KeyEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter, KeyModifiers = Avalonia.Input.KeyModifiers.Control,
            });
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.IsEditingForm);
            Assert.Equal("Applied · Undo Edit 'MENU' 129 (Ctrl+Z)", model.LastApplied);
            Assert.Contains(window.FindControl<StackPanel>("ReadOnlyFooter")!.GetVisualDescendants().OfType<TextBlock>(),
                t => t.Text == model.LastApplied && t.IsEffectivelyVisible);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // Strings, string lists, text and version (E5) read as text on the host, and a double-click on a string edits with
    // it selected.
    [Fact]
    public void Strings_text_and_version_read_first_on_the_host() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            byte[] vers = [0x01, 0x20, 0x60, 0x03, 0, 0, 5, .. "1.2b3"u8, 9, .. "1.2b3 (c)"u8];
            var path = Path.Combine(folder, "Texts.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("STR ", 128, null, [5, .. "Hello"u8]), ("STR#", 128, null, [0, 2, 3, .. "one"u8, 3, .. "two"u8]),
                ("TEXT", 128, null, "Some text"u8.ToArray()), ("vers", 1, null, vers)));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            var host = window.FindControl<DockPanel>("FormHost")!;
            void Select(string type)
            {
                model.Selected = open.Result!.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];
                Pump(model.PreviewTask);
                Dispatcher.UIThread.RunJobs();
                Assert.True(host.IsEffectivelyVisible, type);
                Assert.DoesNotContain(host.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
            }

            Select("STR ");
            Assert.Contains(host.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == "Hello" && t.IsEffectivelyVisible);
            Select("TEXT");
            Assert.Contains(host.GetVisualDescendants().OfType<StyledTextView>(), t => t.IsEffectivelyVisible);
            Select("vers");
            Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "1.2b3" && t.IsEffectivelyVisible);
            Capture(window, "vers-read", baselines);
            Select("STR#");
            var rows = host.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("table-row") && b.IsEffectivelyVisible).ToList();
            Assert.Equal(2, rows.Count);
            Capture(window, "strings-read", baselines);

            rows[1].GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "two")
                .RaiseEvent(new Avalonia.Input.TappedEventArgs(Avalonia.Input.InputElement.DoubleTappedEvent, null!));
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsEditingForm);
            var list = Assert.IsType<StringListForm>(model.Form);
            Assert.Same(list.Strings[1], list.SelectedItem);
            Assert.Contains(host.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "two" && t.IsEffectivelyVisible);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public void The_menu_preview_finds_the_item_under_a_point() => OnUiThread(() =>
    {
        var view = new MenuView { Menu = MenuFormTests.File(), Scale = 2 };
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // Items start below the 12-pixel gutter and the 20-pixel title strip, 16 pixels each, at 2 DIPs per Mac pixel.
        Assert.Equal(-1, view.ItemAt(new Point(40, 30)));
        Assert.Equal(0, view.ItemAt(new Point(40, 2 * (32 + 1))));
        Assert.Equal(2, view.ItemAt(new Point(40, 2 * (32 + 33))));
        Assert.Equal(-1, view.ItemAt(new Point(40, 2 * (32 + 5 * 16 + 1))));
        Assert.Equal(-1, view.ItemAt(new Point(2, 2 * (32 + 1))));
        Assert.Equal(-1, new MenuView().ItemAt(default));
        window.Close();
    });

    // "Show item" on the selected diagnostic (design/boards/diagnostics.md) scrolls the tree to its node and focuses it;
    // the link shows on the selected row only, and the panel has its drag handle.
    [Fact]
    public void Show_item_brings_the_diagnostic_s_node_into_view() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            // A fork with 40 types: the last type's resource is far below the tree's view.
            var path = Path.Combine(folder, "Many.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork([.. Enumerable.Range(0, 40).Select(i => ($"Z{i:000}", (short)128, (string?)null, new byte[] { (byte)i }))]));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            Dispatcher.UIThread.RunJobs();
            var last = open.Result!.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "Z039").Children[0];
            Assert.False(last.Parent!.IsExpanded);
            var panel = model.DiagnosticsPanel;
            panel.Add(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Warning, "file.odd-dates", "Created after it was modified."), last.Source, last));
            panel.Add(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "input.unreadable", "No such file."), "Gone.img", null));
            panel.ByFile = false;
            Dispatcher.UIThread.RunJobs();
            var tree = window.FindControl<BrowseTree>("Tree")!;
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DiagnosticList");
            Button[] Links() => list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("show-item") && b.IsEffectivelyVisible).ToArray();
            Assert.Empty(Links());                                            // nothing selected

            panel.SelectedRow = panel.Rows[1];                                // no node: no link
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(Links());
            panel.SelectedRow = panel.Rows[0];
            Dispatcher.UIThread.RunJobs();
            Assert.Same(last, model.Selected);
            var link = Assert.Single(Links());
            bool InView()
            {
                // The node's own row (its header), within the tree's scroll viewport.
                var row = tree.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext == last);
                var header = row;
                var viewer = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
                if (header?.TranslatePoint(default, viewer) is not { } at)
                {
                    return false;
                }

                return at.Y >= 0 && at.Y + header.Bounds.Height <= viewer.Viewport.Height + 0.5;
            }
            // The tree scrolled back to its top, away from the node.
            tree.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = default;
            window.UpdateLayout();
            Assert.False(InView());

            link.Command!.Execute(link.CommandParameter);
            Pump(model.DraftTask);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(InView());
            Assert.True(tree.IsKeyboardFocusWithin);

            // The selected row and its link, and the drag handle (the focus moved to the tree, as Show item leaves it).
            var handle = window.GetVisualDescendants().OfType<GridSplitter>().Single(s => s.Classes.Contains("handle"));
            Assert.Equal(6, handle.Bounds.Height);
            Assert.True(Assert.Single(Links()).IsEffectivelyVisible);
            Capture(window, "diagnostics-show-item", null);
            Baselines.Check(window, "diagnostics-show-item", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
            panel.ToggleCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(handle.IsVisible);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // The diagnostics panel (design/boards/diagnostics.md): grouped by file, flat with a selected row, and collapsed
    // to its header by Ctrl+Shift+D, the splitter's height coming back when it opens.
    [Fact]
    public void The_diagnostics_panel_groups_selects_and_collapses() => OnUiThread(() =>
    {
        static DiagnosticEntry Entry(DiagnosticSeverity severity, string source, string code, string message) =>
            new(new Diagnostic(severity, code, message), source, null);
        var model = new MainViewModel();
        var window = new MainWindow { DataContext = model };
        var baselines = new List<string>();
        window.Show();
        var panel = model.DiagnosticsPanel;
        panel.Add(Entry(DiagnosticSeverity.Error, "Mac OS 9.hfv › System Folder:Finder", "archive.fork-crc", "The resource fork's CRC does not match."));
        panel.Add(Entry(DiagnosticSeverity.Warning, "Mac OS 9.hfv › System Folder:Finder", "resource.name-overlap", "Two names share their bytes."));
        panel.Add(Entry(DiagnosticSeverity.Warning, "Mac OS 9.hfv › System Folder:System", "sound.unknown-format", "Unknown sound format 3."));
        panel.Add(Entry(DiagnosticSeverity.Info, "Mac OS 9.hfv › Desktop DB", "hfs.desktop-db", "The desktop database is not read."));
        panel.Add(Entry(DiagnosticSeverity.Info, "Mac OS 9.hfv › Desktop DB", "hfs.desktop-df", "The desktop file is not read."));
        Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "DiagnosticList");
        Assert.Equal(6, list.GetVisualDescendants().OfType<ListBoxItem>().Count()); // three headers, the info group closed
        Capture(window, "diagnostics-grouped", baselines);

        panel.ByFile = false;
        panel.SelectedRow = panel.Rows[1];
        Dispatcher.UIThread.RunJobs();
        var selected = list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsSelected);
        var presenter = selected.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        Assert.Equal(Avalonia.Media.Color.Parse("#EEF2FD"), ((Avalonia.Media.ISolidColorBrush)presenter.Background!).Color); // CmRowHighlight
        Assert.Equal(new Thickness(3, 0, 0, 0), presenter.BorderThickness);
        Capture(window, "diagnostics-flat", baselines);

        var body = window.FindControl<Grid>("Body")!;
        body.RowDefinitions[2].Height = new GridLength(240);          // the splitter dragged
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.D, "D");
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsExpanded);
        Assert.Equal(34, body.RowDefinitions[2].Height.Value);
        Assert.Equal(0, body.RowDefinitions[1].Height.Value);
        Assert.False(list.IsVisible);
        Capture(window, "diagnostics-collapsed", baselines);

        panel.ToggleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(240, body.RowDefinitions[2].Height.Value);
        window.Close();
        Baselines.Verify(baselines);
    });

    // Ticking "Edit with template" with an unapplied form: the box stays unticked until the question is answered.
    [Fact]
    public void The_template_box_stays_while_a_draft_is_asked_about() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var path = Path.Combine(folder, "both.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(("STR ", 128, null, [2, .. "hi"u8]), ("TMPL", 1000, "STR ", EditTests.Tmpl(("Text", "PSTR")))));
            var dialogs = new DraftDialogs();
            var model = new MainViewModel { EditDialogs = dialogs };
            var window = new MainWindow { DataContext = model, Width = 1200, Height = 800 };
            window.Show();
            Pump(model.OpenAsync(path));
            Pump(model.Roots[0].EnsureLoadedAsync());
            model.Selected = model.Roots[0].Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "STR ").Children[0];
            Pump(model.PreviewTask);
            model.EditFormCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<StringForm>(model.Form).Text = "edited";
            var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Edit with template");
            box.IsChecked = true;                                           // as a click does
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, dialogs.Asked);
            Assert.False(model.UseTemplate);
            Assert.False(box.IsChecked);
            dialogs.Pending.SetResult(DraftChoice.Discard);
            Pump(model.DraftTask);
            Assert.True(model.UseTemplate);
            Assert.True(box.IsChecked);
            Assert.IsType<TemplateForm>(model.Form);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });

    // The tree's footer says how many invisible files are hidden and Show brings them back; the Tree display popover's
    // check boxes switch the options; the "No name" group row shows its count.
    [Fact]
    public void The_tree_footer_and_display_options_work() => OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("classicmac-window-").FullName;
        try
        {
            var model = new MainViewModel();
            model.TreeDisplay.ShowDetails = true;                            // the group's count is in the details column
            var window = new MainWindow { DataContext = model };
            var baselines = new List<string>();
            window.Show();
            Pump(model.OpenAsync(TreeDisplayTests.Disk(folder)));
            Pump(model.PreviewTask);
            var realmz = model.Roots[0].Children.OfType<FolderNode>().Single(f => f.Title == "Realmz");
            model.Roots[0].IsExpanded = realmz.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var footer = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HiddenFooter");
            Assert.True(footer.IsVisible);
            Assert.Contains(footer.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "2 invisible items hidden");
            var texts = window.FindControl<BrowseTree>("Tree")!.GetVisualDescendants().OfType<TextBlock>().ToList();
            // A row's name is in runs (before, matched letters, after) for the search highlight.
            var group = texts.Single(t => string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []) == "No name");
            Assert.Equal(Avalonia.Media.FontStyle.Italic, group.FontStyle);
            Assert.Contains(texts, t => t.Text == "4 files" && t.FontSize == 11);
            // Light and dark: the header's Tree display button, the italic "No name" row with its muted count, the
            // group open with its names in mono, and the footer.
            var noName = realmz.Children.OfType<NoNameGroupNode>().Single();
            noName.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            // Whitespace names as ASCII token chips (mono 10 SemiBold on CmSegmentTrack), the bytes on hover; inside the
            // selected row, CmSelectionText on CmTokenOnSelection.
            var chips = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("name-token") && b.IsEffectivelyVisible).ToList();
            Assert.Equal(new[] { "sp", "sp×2", "tab", "nbsp", "tab" }.Order(), chips.Select(c => ((TextBlock)c.Child!).Text).Order());
            Assert.All(chips, c => Assert.Equal(10, ((TextBlock)c.Child!).FontSize));
            var tokens = window.GetVisualDescendants().OfType<ItemsControl>().First(i => i.Classes.Contains("name-tokens") && i.IsEffectivelyVisible);
            Assert.Matches("^[0-9A-F]{2}( [0-9A-F]{2})*$", (string)ToolTip.GetTip(tokens)!);
            model.Selected = noName.Children.Single(c => c.Name == "nbsp tab");
            Pump(model.PreviewTask);                                         // the preview and header icon load in the
            Pump(model.HeaderIconTask);                                      // background: settle before the captures
            Dispatcher.UIThread.RunJobs();
            window.FindControl<BrowseTree>("Tree")!.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsSelected).Focus();
            Dispatcher.UIThread.RunJobs();
            var selected = window.FindControl<BrowseTree>("Tree")!.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsSelected)
                .GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("name-token"));
            Assert.Equal(Application.Current!.FindResource(window.ActualThemeVariant, "CmTokenOnSelection"), selected.Background);
            Assert.Equal(Application.Current!.FindResource(window.ActualThemeVariant, "CmSelectionText"), ((TextBlock)selected.Child!).Foreground);
            // The inspector header names it with the same chips, in their plain colours, the bytes on hover.
            var headerTokens = window.FindControl<ItemsControl>("HeaderNameTokens")!;
            Assert.True(headerTokens.IsEffectivelyVisible);
            Assert.False(window.FindControl<TextBlock>("HeaderName")!.IsEffectivelyVisible);
            var headerChips = headerTokens.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("name-token")).ToList();
            Assert.Equal(["nbsp", "tab"], headerChips.Select(c => ((TextBlock)c.Child!).Text));
            Assert.Equal(Application.Current!.FindResource(window.ActualThemeVariant, "CmSegmentTrack"), headerChips[0].Background);
            Assert.Equal("CA 09", ToolTip.GetTip(headerTokens));
            Capture(window, "tree-no-name");
            Baselines.Check(window, "tree-no-name", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);

            footer.GetVisualDescendants().OfType<Button>().Single().Command!.Execute(null);   // Show
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.TreeDisplay.HideInvisible);
            Assert.False(footer.IsVisible);

            var options = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TreeDisplayButton");
            options.Flyout!.ShowAt(options);
            Dispatcher.UIThread.RunJobs();
            var panel = (StackPanel)((Flyout)options.Flyout).Content!;
            var boxes = panel.Children.OfType<CheckBox>().ToList();
            Assert.Equal(["Group files with no name", "Hide invisible files", "Show details column"], boxes.Select(b => (string)b.Content!));
            Assert.Equal([true, false, true], boxes.Select(b => b.IsChecked == true));
            boxes[0].IsChecked = false;
            boxes[1].IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.TreeDisplay.GroupNoName);
            Assert.True(model.TreeDisplay.HideInvisible);
            Assert.DoesNotContain(realmz.Children, c => c is NoNameGroupNode);
            options.Flyout.Hide();

            // Any other file: its name as text.
            model.Selected = realmz.Children.Single(c => c.Title == "Realmz");
            Pump(model.PreviewTask);
            Pump(model.HeaderIconTask);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<TextBlock>("HeaderName")!.IsEffectivelyVisible);
            Assert.Equal("Realmz", window.FindControl<TextBlock>("HeaderName")!.Text);
            Assert.False(window.FindControl<ItemsControl>("HeaderNameTokens")!.IsEffectivelyVisible);
            window.Close();
            Baselines.Verify(baselines);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
