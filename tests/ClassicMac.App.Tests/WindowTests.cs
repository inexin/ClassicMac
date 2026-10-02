using Avalonia.Headless;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources.Decoders.Interface;
using MenuItem = Avalonia.Controls.MenuItem;
using MacMenuItem = ClassicMac.Resources.Decoders.Interface.MenuItem;

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
        if (baselines is not null) Baselines.Check(window, name, baselines);
    }

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

            model.SelectedTab = 2;
            Dispatcher.UIThread.RunJobs();
            Capture(window, "hex");

            // The tree's context menu, on a resource: the Resource commands and Save Resource As apply, not the file's.
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
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
    public void The_document_preview_draws_text_and_pictures() => OnUiThread(The_document_preview_draws_text_and_picturesBody);

    private static void The_document_preview_draws_text_and_picturesBody()
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
            var pictures = window.GetVisualDescendants().OfType<DocumentPictureView>().ToList();
            Assert.Equal(3, pictures.Count);
            Assert.Equal(new Size(260, 65), pictures[2].Bounds.Size); // scaled to the column
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
                model.SelectedTab = 3;
                Dispatcher.UIThread.RunJobs();
                Capture(window, "edit-" + type.TrimEnd('#'), type == "DITL" ? baselines : null);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), t => t.IsEffectivelyVisible);
            }

            // The template's count field is read only and shown muted by its class, not an opacity.
            var count = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsReadOnly && t.IsEffectivelyVisible);
            Assert.Contains("readonly", count.Classes);

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

        public Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew) => throw new NotSupportedException();
        public Task<SaveChanges> AskSaveChangesAsync(string fileName) => Task.FromResult(SaveChanges.Discard);
        public Task<bool> ConfirmAsync(string title, string message) => throw new NotSupportedException();
        public Task<byte[]?> EditHexAsync(string title, byte[] data) => throw new NotSupportedException();
        public Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial) => throw new NotSupportedException();
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
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
            Assert.Same(type.Children[0], tree.SelectedItem);
            var form = Assert.IsType<StringForm>(model.Form);
            form.Text = "edited";
            var details = model.Details;

            // A click on 'STR ' 129.
            var item = Assert.IsType<TreeViewItem>(tree.TreeContainerFromItem(type.Children[1]));
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
            Pump(model.SelectionTask);
            Assert.Same(type.Children[0], tree.SelectedItem);
            Assert.Equal("edited", form.Text);

            // The down arrow, from the focused 'STR ' 128.
            dialogs.Pending = new TaskCompletionSource<DraftChoice>();
            Assert.IsType<TreeViewItem>(tree.TreeContainerFromItem(type.Children[0])).Focus();
            window.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
            window.KeyRelease(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, dialogs.Asked);
            Assert.Same(type.Children[0], model.Selected);
            Assert.Same(type.Children[0], tree.SelectedItem);
            dialogs.Pending.SetResult(DraftChoice.Discard);
            Pump(model.SelectionTask);
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
            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(["Discard", "Cancel", "Apply"], buttons.Select(b => (string)b.Content!));
            Assert.True(buttons.Single(b => (string)b.Content! == "Apply").IsDefault);
            Assert.True(buttons.Single(b => (string)b.Content! == "Cancel").IsCancel);
            buttons.Single(b => (string)b.Content! == label).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
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
}
