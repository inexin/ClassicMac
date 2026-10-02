using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Resources.Decoders.Sound;

namespace ClassicMac.App.Tests;

using static Headless;

// The design tokens (design/TOKENS.md): every colour resolves to its light or dark value as the theme variant flips,
// Fluent's selection keys and accent follow them, and the custom-drawn views redraw from them.
public class ThemeTests
{
    // TOKENS.md, Colour: token, light, dark.
    public static TheoryData<string, uint, uint> Colours => new()
    {
        { "CmWindowBackground", 0xF3F3F0, 0x1E1E20 },
        { "CmTitleBarBackground", 0xE6E6E1, 0x202023 },
        { "CmChromeBackground", 0xECECE8, 0x252528 },
        { "CmPaneBackground", 0xFFFFFF, 0x19191B },
        { "CmSidebarBackground", 0xF7F7F4, 0x1F1F22 },
        { "CmBorder", 0xD9D8D2, 0x38383C },
        { "CmDivider", 0xE6E5E0, 0x2C2C30 },
        { "CmCardBorder", 0xDEDDD7, 0x3A3A3E },
        { "CmText", 0x1D1D1B, 0xEDEDEA },
        { "CmTextMuted", 0x5D5C57, 0xA9A8A3 },
        { "CmAccent", 0x2E4EC2, 0x8FA3FF },
        { "CmOnAccent", 0xFFFFFF, 0x0F1530 },
        { "CmSelection", 0x2E4EC2, 0x33417A },
        { "CmSelectionText", 0xFFFFFF, 0xF2F4FF },
        { "CmSelectionInactive", 0xDCE3FA, 0x2C3354 },
        { "CmRowHighlight", 0xEEF2FD, 0x24283A },
        { "CmMatch", 0xFFE58A, 0x6B5714 },
        { "CmMatchSoft", 0xFFF3C4, 0x3F3618 },
        { "CmHexCursor", 0x2E4EC2, 0x8FA3FF },
        { "CmControlBackground", 0xFFFFFF, 0x2C2C30 },
        { "CmControlBorder", 0xCFCEC8, 0x45454A },
        { "CmSegmentTrack", 0xE2E1DC, 0x2C2C30 },
        { "CmSegmentOn", 0xFFFFFF, 0x45454B },
        { "CmError", 0xB42318, 0xFF8A7A },
        { "CmErrorTint", 0xFCE9E7, 0x3A1D1A },
        { "CmWarning", 0x8A5300, 0xF2B45A },
        { "CmWarningTint", 0xFDF1DA, 0x3A2C14 },
        { "CmInfo", 0x245C9E, 0x8CBEF5 },
        { "CmInfoTint", 0xE6EFF9, 0x18283C },
        { "CmCheckerLight", 0xFFFFFF, 0x2A2A2D },
        { "CmCheckerDark", 0xECECE8, 0x323236 },
        { "CmLoopRegion", 0xE5EAFB, 0x263058 },
        { "CmPlayhead", 0xB42318, 0xFF8A7A },
    };

    private static Color Rgb(uint rgb) => Color.FromUInt32(0xFF000000 | rgb);

    // Runs the body in the light theme, then in the dark one, then puts the default back.
    private static void InEachTheme(Action<ThemeVariant> body)
    {
        var app = Application.Current!;
        try
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                app.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                body(variant);
            }
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Color Expected(string token, ThemeVariant variant)
    {
        var row = Colours.Select(r => r.Data).Single(r => r.Item1 == token);
        return Rgb(variant == ThemeVariant.Dark ? row.Item3 : row.Item2);
    }

    private static object Resource(string key)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var value), key);
        return value!;
    }

    private static Color BrushColour(object? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [Theory]
    [MemberData(nameof(Colours))]
    public void Each_colour_token_follows_the_theme(string token, uint light, uint dark) => OnUiThread(() =>
        InEachTheme(variant =>
        {
            var expected = Rgb(variant == ThemeVariant.Dark ? dark : light);
            Assert.Equal(expected, (Color)Resource(token + "Color"));
            Assert.Equal(expected, BrushColour(Resource(token))); // the one shared brush follows the theme
        }));

    [Fact]
    public void Fluent_s_accent_is_the_token_accent_with_its_shades() => OnUiThread(() =>
        InEachTheme(variant =>
        {
            var accent = Expected("CmAccent", variant);
            Assert.Equal(accent, (Color)Resource("SystemAccentColor"));
            var palette = new ColorPaletteResources { Accent = accent };
            foreach (var shade in new[] { "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
                "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            {
                Assert.True(palette.TryGetResource(shade, variant, out var expected));
                Assert.Equal((Color)expected!, (Color)Resource(shade));
            }
        }));

    // Fluent's own resource keys (checked against Avalonia.Themes.Fluent 12.1.3) point at the selection tokens.
    [Theory]
    [InlineData("TreeViewItemBackgroundSelected", "CmSelection")]
    [InlineData("TreeViewItemBackgroundSelectedPointerOver", "CmSelection")]
    [InlineData("TreeViewItemBackgroundSelectedPressed", "CmSelection")]
    [InlineData("TreeViewItemForegroundSelected", "CmSelectionText")]
    [InlineData("TreeViewItemForegroundSelectedPointerOver", "CmSelectionText")]
    [InlineData("TreeViewItemForegroundSelectedPressed", "CmSelectionText")]
    [InlineData("TabItemHeaderSelectedPipeFill", "CmAccent")]
    [InlineData("TabItemHeaderForegroundSelected", "CmText")]
    [InlineData("TabItemHeaderForegroundSelectedPointerOver", "CmText")]
    [InlineData("TabItemHeaderForegroundUnselected", "CmTextMuted")]
    [InlineData("TabItemHeaderForegroundUnselectedPointerOver", "CmText")]
    public void Fluent_selection_keys_use_the_tokens(string key, string token) => OnUiThread(() =>
        InEachTheme(variant => Assert.Equal(Expected(token, variant), BrushColour(Resource(key)))));

    private static T Show<T>(T content, double width = 300, double height = 200) where T : Control
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return content;
    }

    private static Color Pixel(WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var offset = y * buffer.RowBytes + x * 4;
        var bytes = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address + offset, bytes, 0, 4);
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    private static WriteableBitmap Frame(Control control)
    {
        var window = (Window)TopLevel.GetTopLevel(control)!;
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame!;
    }

    [Fact]
    public void The_checkerboard_is_drawn_from_the_checker_tokens() => OnUiThread(() =>
    {
        var border = new Border { Width = 32, Height = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("CmCheckerboard"));
        Show(border);
        InEachTheme(variant =>
        {
            var frame = Frame(border);
            Assert.Equal(Expected("CmCheckerDark", variant), Pixel(frame, 2, 2));   // 8 DIP squares
            Assert.Equal(Expected("CmCheckerLight", variant), Pixel(frame, 10, 2));
            Assert.Equal(Expected("CmCheckerLight", variant), Pixel(frame, 2, 10));
            Assert.Equal(Expected("CmCheckerDark", variant), Pixel(frame, 10, 10));
            Assert.Equal(Expected("CmCheckerDark", variant), Pixel(frame, 18, 18)); // tiled
        });
        ((Window)TopLevel.GetTopLevel(border)!).Close();
    });

    [Fact]
    public void The_waveform_is_drawn_from_the_tokens_and_redrawn_when_the_theme_changes() => OnUiThread(() =>
    {
        // One channel at a constant +0.5 over the 180 px above the 20 px ruler: the wave is one row at 46 (middle 90 less
        // half of 88), the centre line row 90.
        var view = Show(new WaveformView { Sound = new DecodedSound(Enumerable.Repeat(0.5f, 1000).ToArray(), 1, 22050) });
        InEachTheme(variant =>
        {
            var frame = Frame(view);
            Assert.Equal(Expected("CmPaneBackground", variant), Pixel(frame, 20, 10));
            Assert.Equal(Expected("CmAccent", variant), Pixel(frame, 20, 46));
            Assert.Equal(Expected("CmDivider", variant), Pixel(frame, 20, 90));
        });
        ((Window)TopLevel.GetTopLevel(view)!).Close();
    });

    [Fact]
    public void A_sound_shorter_than_the_lanes_is_drawn_as_a_line_not_dots() => OnUiThread(() =>
    {
        // Four frames over 300 px: each column joins its sample to the next, so between +0.5 (row 46) and -0.5 (row
        // 134) the wave passes the rows in between.
        var view = Show(new WaveformView { Sound = new DecodedSound([0.5f, -0.5f, 0.5f, -0.5f], 1, 22050) });
        var frame = Frame(view);
        Assert.Equal(Expected("CmAccent", ThemeVariant.Light), Pixel(frame, 30, 70));
        Assert.Equal(Expected("CmAccent", ThemeVariant.Light), Pixel(frame, 30, 110));
        ((Window)TopLevel.GetTopLevel(view)!).Close();
    });

    [Theory]
    [InlineData(0.02, 4, "0.000 s|0.005 s|0.010 s|0.015 s|0.020 s")]
    [InlineData(2.0, 4, "0.00 s|0.50 s|1.00 s|1.50 s|2.00 s")]
    [InlineData(120.0, 2, "0.00 s|60.00 s|120.00 s")]
    public void The_ruler_labels_are_precise_enough_to_differ(double duration, int steps, string expected) =>
        Assert.Equal(expected, string.Join("|", WaveformView.RulerLabels(duration, steps)));

    // Muted text is a colour, not an opacity; inside a selected row of a focused list it reads in CmSelectionText.
    [Fact]
    public void Muted_text_uses_its_token_and_the_selection_text_in_a_selected_row() => OnUiThread(() =>
    {
        var list = new ListBox
        {
            ItemsSource = new[] { "one", "two" },
            ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, Classes = { "muted" } }),
        };
        Show(list);
        list.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        TextBlock Text(int i) => items[i].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("muted"));
        Color Fill(int i) => BrushColour(items[i].GetVisualDescendants().OfType<ContentPresenter>().First().Background);
        InEachTheme(variant =>
        {
            Assert.Equal(Expected("CmTextMuted", variant), BrushColour(Text(0).Foreground)); // focus elsewhere
            Assert.Equal(Expected("CmSelectionInactive", variant), Fill(0));
            Assert.Equal(Expected("CmTextMuted", variant), BrushColour(Text(1).Foreground));
        });
        items[0].Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsKeyboardFocusWithin);
        InEachTheme(variant =>
        {
            Assert.Equal(Expected("CmSelectionText", variant), BrushColour(Text(0).Foreground));
            Assert.Equal(Expected("CmSelection", variant), Fill(0));
            Assert.Equal(Expected("CmTextMuted", variant), BrushColour(Text(1).Foreground));
        });
        ((Window)TopLevel.GetTopLevel(list)!).Close();
    });

    [Fact]
    public void A_tree_s_selected_row_is_inactive_until_the_tree_has_focus() => OnUiThread(() =>
    {
        var tree = new TreeView { ItemsSource = new[] { "one", "two" } };
        Show(tree);
        tree.SelectedItem = "one";
        Dispatcher.UIThread.RunJobs();
        var item = tree.GetVisualDescendants().OfType<TreeViewItem>().First();
        Color Fill() => BrushColour(item.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_LayoutRoot").Background);
        Color Text() => BrushColour(item.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_HeaderPresenter").Foreground);
        InEachTheme(variant =>
        {
            Assert.Equal(Expected("CmSelectionInactive", variant), Fill());
            Assert.Equal(Expected("CmText", variant), Text());
        });
        item.Focus();
        Dispatcher.UIThread.RunJobs();
        InEachTheme(variant =>
        {
            Assert.Equal(Expected("CmSelection", variant), Fill());
            Assert.Equal(Expected("CmSelectionText", variant), Text());
        });
        ((Window)TopLevel.GetTopLevel(tree)!).Close();
    });

    [Theory]
    [InlineData("muted", "CmTextMuted")]
    [InlineData("caption", "CmTextMuted")]
    public void Text_classes_use_their_tokens(string @class, string token) => OnUiThread(() =>
    {
        var text = Show(new TextBlock { Text = "x", Classes = { @class } });
        InEachTheme(variant => Assert.Equal(Expected(token, variant), BrushColour(text.Foreground)));
        ((Window)TopLevel.GetTopLevel(text)!).Close();
    });

    [Fact]
    public void A_caption_is_small_strong_and_spaced() => OnUiThread(() =>
    {
        var text = Show(new TextBlock { Text = "x", Classes = { "caption" } });
        Assert.Equal((11.0, FontWeight.SemiBold, 0.66), (text.FontSize, text.FontWeight, text.LetterSpacing));
        ((Window)TopLevel.GetTopLevel(text)!).Close();
    });

    [Fact]
    public void A_read_only_text_box_is_muted() => OnUiThread(() =>
    {
        var box = Show(new TextBox { Text = "3", IsReadOnly = true, Classes = { "readonly" } });
        InEachTheme(variant => Assert.Equal(Expected("CmTextMuted", variant), BrushColour(box.Foreground)));
        ((Window)TopLevel.GetTopLevel(box)!).Close();
    });

    [Theory]
    [InlineData("error", "CmError", "CmErrorTint")]
    [InlineData("warning", "CmWarning", "CmWarningTint")]
    [InlineData("info", "CmInfo", "CmInfoTint")]
    public void Badges_use_their_severity_colours(string severity, string text, string tint) => OnUiThread(() =>
    {
        var label = new TextBlock { Text = "2 errors" };
        var badge = Show(new Border { Classes = { "badge", severity }, Child = label });
        InEachTheme(variant =>
        {
            Assert.Equal(Expected(tint, variant), BrushColour(badge.Background));
            Assert.Equal(Expected(text, variant), BrushColour(label.Foreground));
        });
        Assert.Equal(FontWeight.SemiBold, label.FontWeight);
        ((Window)TopLevel.GetTopLevel(badge)!).Close();
    });

    [Theory]
    [InlineData("chrome", "CmChromeBackground")]
    [InlineData("pane", "CmPaneBackground")]
    [InlineData("sidebar", "CmSidebarBackground")]
    [InlineData("readonly-bar", "CmSidebarBackground")]
    [InlineData("editing-badge", "CmRowHighlight")]
    public void Surface_classes_use_their_tokens(string @class, string token) => OnUiThread(() =>
    {
        var border = Show(new Border { Classes = { @class }, Child = new TextBlock { Text = "x" } });
        var panel = Show(new DockPanel { Classes = { @class } });
        InEachTheme(variant =>
        {
            Assert.Equal(Expected(token, variant), BrushColour(border.Background));
            Assert.Equal(Expected(token, variant), BrushColour(panel.Background));
        });
        ((Window)TopLevel.GetTopLevel(border)!).Close();
        ((Window)TopLevel.GetTopLevel(panel)!).Close();
    });

    [Fact]
    public void A_segmented_list_shows_its_chosen_segment_on_the_track() => OnUiThread(() =>
    {
        var list = Show(new ListBox { Classes = { "segmented" }, ItemsSource = new[] { "1×", "2×" }, SelectedIndex = 1 });
        Dispatcher.UIThread.RunJobs();
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(2, items.Count);
        Assert.True(items[1].Bounds.X > items[0].Bounds.X); // side by side
        InEachTheme(variant =>
        {
            Assert.Equal(Expected("CmSegmentTrack", variant), BrushColour(list.Background));
            Assert.Equal(Expected("CmSegmentOn", variant), BrushColour(items[1].GetVisualDescendants().OfType<ContentPresenter>().First().Background));
        });
        ((Window)TopLevel.GetTopLevel(list)!).Close();
    });

    [Fact]
    public void The_main_window_s_surfaces_and_hex_cursor_follow_the_theme() => OnUiThread(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"cm-theme-{Guid.NewGuid():N}.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("ZZZZ", 128, null, [1, 2, 3, 4]),
            ("ICN#", 128, null, [.. Enumerable.Range(0, 128).Select(i => (byte)(i % 8 < 4 ? 0xF0 : 0x00)), .. Enumerable.Range(0, 128).Select(i => (byte)(i % 8 < 4 ? 0xFF : 0x00))]),
            ("snd ", 128, "Sine", SoundPreviewTests.Sound(20000))));
        try
        {
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            open.Result!.IsExpanded = true;
            NodeViewModel Resource(string type) => open.Result!.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];
            // The icon (on the checkerboard) and the sound, in each theme, for a look.
            foreach (var type in new[] { "ICN#", "snd " })
            {
                model.Selected = Resource(type);
                Pump(model.PreviewTask);
                InEachTheme(variant => Capture(window, $"theme-{type.Trim().TrimEnd('#').ToLowerInvariant()}-{variant.Key.ToString()!.ToLowerInvariant()}"));
            }
            model.Selected = Resource("ZZZZ");
            Pump(model.PreviewTask);
            model.BeginHexEditCommand.Execute(null);   // the cursor shows while editing
            Dispatcher.UIThread.RunJobs();
            var cursor = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("hex-cell") && b.Classes.Contains("cursor"));
            var cursorText = cursor.GetVisualDescendants().OfType<TextBlock>().Single();
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single();
            InEachTheme(variant =>
            {
                Assert.Equal(Expected("CmWindowBackground", variant), BrushColour(window.Background));
                Assert.Equal(Expected("CmSidebarBackground", variant), BrushColour(tree.Background));
                Assert.Equal(Expected("CmHexCursor", variant), BrushColour(cursor.Background));
                Assert.Equal(Expected("CmOnAccent", variant), BrushColour(cursorText.Foreground));
                Capture(window, "theme-" + variant.Key.ToString()!.ToLowerInvariant());
            });
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    });

    private static string SourceFile(string relative, [CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", relative));

    // F1, F3, F4: the views take colours from tokens, never literals, opacity or Fluent's base brushes.
    [Theory]
    [InlineData("src/ClassicMac.App/Views/MainWindow.axaml")]
    [InlineData("src/ClassicMac.App/Views/EditDialogs.cs")]
    [InlineData("src/ClassicMac.App/Views/PreviewControls.cs")]
    public void Views_hold_no_colour_literals_or_text_opacity(string file)
    {
        var text = File.ReadAllText(SourceFile(file));
        if (file.EndsWith(".axaml", StringComparison.Ordinal))
        {
            Assert.DoesNotMatch(new Regex("#[0-9A-Fa-f]{3,8}\\b"), text);
        }

        Assert.DoesNotMatch(new Regex("Opacity\\s*=\\s*\"?0\\."), text);
        Assert.DoesNotContain("SystemControlForegroundBaseLowBrush", text);
    }
}
