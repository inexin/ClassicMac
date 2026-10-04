using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassicMac.App.Controls;
using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.Dialogs;

/// <summary>A dialog window built by <see cref="DialogViews"/>.</summary>
internal interface IDialog
{
    Window Window { get; }
}

/// <summary>A dialog and what it ends with (its cancel value until a button sets another).</summary>
internal class Dialog<T>(Window window, T cancel) : IDialog
{
    public Window Window { get; } = window;

    public T Result { get; internal set; } = cancel;
}

/// <summary>The Import dialog, with its "Make" choices.</summary>
internal sealed class ImportDialog(Window window, ImportOptions options) : Dialog<ImportChoice?>(window, null)
{
    public ImportOptions Options { get; } = options;
}

/// <summary>
/// The dialogs in one frame (design/boards/dialogs.md, P5): CmPaneBackground with a CmRadiusDialog corner and
/// CmShadowDialog, drawn by the app (no system title bar). A dialog has a header (its title, 14 SemiBold, and a ×
/// that cancels; dragging it moves the window); an alert has none, but an icon, its question (14 SemiBold) and a
/// detail in CmTextMuted. The body is padded 16 with 14 between parts, labels 12 muted in a 90 px column, each a
/// Label for its input. The footer, on CmSidebarBackground under a CmDivider, has Cancel then the primary action
/// (accent) on the right, and a destructive third choice on the left. Enter is the primary, Esc Cancel.
/// </summary>
internal static class DialogViews
{
    private static readonly (ResourceAttributes Flag, string Label)[] Flags =
    [
        (ResourceAttributes.SystemHeap, "System heap"), (ResourceAttributes.Purgeable, "Purgeable"), (ResourceAttributes.Locked, "Locked"),
        (ResourceAttributes.Protected, "Protected"), (ResourceAttributes.Preload, "Preload"),
    ];

    public enum AlertIcon
    {
        Warning,
        Question,
    }

    // ---- The frame ----

    private static Window NewWindow(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        WindowDecorations = WindowDecorations.None,
        Background = Brushes.Transparent,
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
        MinWidth = 360,
        MaxWidth = 560,
    };

    private static Button Button(string label, Action click, params string[] classes)
    {
        var button = new Button { Content = label, Command = new RelayCommand(click), MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center };
        foreach (var c in classes)
        {
            button.Classes.Add(c);
        }

        return button;
    }

    private static Border Header(Window window, string title, Action cancel)
    {
        var close = Button("×", () =>
        {
            cancel();
            window.Close();
        }, "flat", "dialog-close");
        close.Name = "DialogClose";
        Avalonia.Automation.AutomationProperties.SetName(close, "Close");
        DockPanel.SetDock(close, Dock.Right);
        var header = new Border
        {
            Name = "DialogHeader",
            Classes = { "dialog-header" },
            Child = new DockPanel { Children = { close, new TextBlock { Text = title, Classes = { "dialog-title" }, VerticalAlignment = VerticalAlignment.Center } } },
        };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                window.BeginMoveDrag(e);
            }
        };
        return header;
    }

    private static Border Footer(Button cancel, Button primary, Button? destructive)
    {
        cancel.IsCancel = true;
        cancel.Classes.Add("dialog-cancel");
        primary.IsDefault = primary.IsEnabled;
        primary.Classes.Add("accent");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, primary } };
        DockPanel.SetDock(right, Dock.Right);
        var panel = new DockPanel { LastChildFill = false, Children = { right } };
        if (destructive is not null)
        {
            destructive.Classes.Add("destructive");
            DockPanel.SetDock(destructive, Dock.Left);
            panel.Children.Add(destructive);
        }

        return new Border { Name = "DialogFooter", Classes = { "dialog-footer" }, Child = panel };
    }

    private static void Compose(Window window, Control? header, Control body, Control footer)
    {
        var content = new DockPanel { Children = { footer } };
        DockPanel.SetDock(footer, Dock.Bottom);
        if (header is not null)
        {
            DockPanel.SetDock(header, Dock.Top);
            content.Children.Add(header);
        }

        content.Children.Add(new Border { Name = "DialogBody", Classes = { "dialog-body" }, Child = body });
        window.Content = new Border { Classes = { "dialog-frame" }, Child = new Border { Classes = { "dialog-surface" }, Child = content } };
    }

    // An alert's body: its icon, the question and the detail.
    private static Grid Alert(AlertIcon icon, string question, string? detail)
    {
        // The severity glyph at twice its 14 px.
        var glyph = new LayoutTransformControl
        {
            Name = "AlertIcon",
            LayoutTransform = new ScaleTransform(2, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new SeverityIcon
            {
                Severity = icon == AlertIcon.Warning ? ClassicMac.Core.DiagnosticSeverity.Warning : ClassicMac.Core.DiagnosticSeverity.Info,
            },
        };
        var text = new StackPanel { Spacing = 6, MaxWidth = 380 };
        text.Children.Add(new TextBlock { Text = question, Classes = { "alert-question" }, TextWrapping = TextWrapping.Wrap });
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Text = detail, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*") };
        Grid.SetColumn(text, 1);
        grid.Children.Add(glyph);
        grid.Children.Add(text);
        return grid;
    }

    // Labels in a 90 px column, each a Label for its input.
    private static Grid Fields(params (string Label, Control Input)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowSpacing = 10 };
        for (var row = 0; row < rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new Label { Content = rows[row].Label, Target = rows[row].Input, Classes = { "field-label" } };
            Grid.SetRow(label, row);
            Grid.SetRow(rows[row].Input, row);
            Grid.SetColumn(rows[row].Input, 1);
            grid.Children.Add(label);
            grid.Children.Add(rows[row].Input);
        }

        return grid;
    }

    private static TextBox Input(string name, string? text, double width, bool mono = false, int maxLength = 0)
    {
        var box = new TextBox { Name = name, Text = text, Width = width, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = maxLength };
        if (mono)
        {
            box.Classes.Add("mono");
        }

        return box;
    }

    private static NumericUpDown Number(string name, decimal value)
    {
        var box = new NumericUpDown
        {
            Name = name,
            Value = value,
            Minimum = short.MinValue,
            Maximum = short.MaxValue,
            Increment = 1,
            FormatString = "0",
            Width = 156,                                  // "-32768" beside the two spinner buttons
            HorizontalAlignment = HorizontalAlignment.Left,
            Classes = { "mono" },
        };
        return box;
    }

    private static short Id(NumericUpDown box) => (short)Math.Clamp(box.Value ?? 0, short.MinValue, short.MaxValue);

    // ---- The dialogs ----

    // A document glyph (16 px) for a file or a resource with no icon of its own.
    private static PathIcon DocumentGlyph() => new()
    {
        Width = 16,
        Height = 16,
        Data = Geometry.Parse("M3,1 H10 L13,4 V15 H3 Z M10,1 V4 H13"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Get Info's subject: the icon on a 48 px checkerboard tile, the name and "Icon family in Finder · 2,240 bytes".
    private static Grid Subject(DialogSubject subject)
    {
        Control icon = subject.IconPng is { } png
            ? new PixelImage { Source = Images.FromPng.Convert(png, typeof(Bitmap), null, CultureInfo.InvariantCulture) as Bitmap, Zoom = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : DocumentGlyph();
        var tile = new Border { Name = "SubjectIcon", Classes = { "icon-tile" }, Width = 48, Height = 48, Child = icon, ClipToBounds = true };
        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = subject.Name, Classes = { "strong" }, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = subject.Line, Classes = { "muted" }, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*"), ColumnSpacing = 12 };
        Grid.SetColumn(text, 1);
        grid.Children.Add(tile);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>Get Info (with its <paramref name="subject"/>), or New Resource (none; the type can be typed).</summary>
    public static Dialog<ResourceInfo?> ResourceInfo(string title, ResourceInfo initial, bool isNew, DialogSubject? subject)
    {
        var window = NewWindow(title);
        var dialog = new Dialog<ResourceInfo?>(window, null);
        var type = Input("Type", initial.Type, 90, mono: true, maxLength: 16);
        type.IsReadOnly = !isNew;
        var id = Number("ID", initial.Id);
        var name = Input("Name", initial.Name, 300);
        var boxes = Flags.Select(f => new CheckBox { Content = f.Label, IsChecked = (initial.Attributes & f.Flag) != 0 }).ToList();
        var compressed = (initial.Attributes & ResourceAttributes.Compressed) != 0;
        var all = boxes.Append(new CheckBox { Content = "Compressed", IsChecked = compressed, IsEnabled = false }).ToList();
        var attributes = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2, Name = "Attributes" };
        foreach (var box in all)
        {
            attributes.Children.Add(box);
        }

        var fieldset = new StackPanel
        {
            Spacing = 6,
            Children = { new TextBlock { Text = "ATTRIBUTES", Classes = { "caption" } }, attributes },
        };
        var body = new StackPanel { Spacing = 14, Children = { Fields(("Type", type), ("ID", id), ("Name", name)), fieldset } };
        if (subject is not null)
        {
            body.Children.Insert(0, Subject(subject));
        }

        var cancel = Button("Cancel", window.Close);
        var ok = Button("OK", () =>
        {
            var result = compressed ? ResourceAttributes.Compressed : ResourceAttributes.None;
            for (var i = 0; i < Flags.Length; i++)
            {
                if (boxes[i].IsChecked == true)
                {
                    result |= Flags[i].Flag;
                }
            }

            dialog.Result = new ResourceInfo(type.Text ?? "", Id(id), name.Text ?? "", result);
            window.Close();
        });
        Compose(window, Header(window, title, () => { }), body, Footer(cancel, ok, null));
        return dialog;
    }

    /// <summary>Import Image or Sound: what to make, the first ID and the name.</summary>
    public static ImportDialog Import(string fileName, IReadOnlyList<string> types, ImportChoice initial, ImportSource made)
    {
        var title = $"Import “{fileName}”";
        var window = NewWindow(title);
        var options = new ImportOptions(types, initial.Type);
        var dialog = new ImportDialog(window, options);
        var list = new StackPanel { Spacing = 2, Name = "Make" };
        foreach (var option in options.Options)
        {
            var radio = new RadioButton
            {
                GroupName = "make",
                IsEnabled = option.IsEnabled,
                IsChecked = ReferenceEquals(option, options.Selected),
                Content = OptionContent(option, options),
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    options.Selected = option;
                }
            };
            var row = new Border { Classes = { "make-row" }, Child = radio };
            row.Classes.Set("chosen", ReferenceEquals(option, options.Selected));
            options.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportOptions.Selected))
                {
                    radio.IsChecked = ReferenceEquals(option, options.Selected);
                    row.Classes.Set("chosen", ReferenceEquals(option, options.Selected));
                }
            };
            list.Children.Add(row);
        }

        var source = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                DocumentGlyph(),
                new TextBlock { Text = fileName, Classes = { "strong" }, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Name = "SourceDetails",
                    Text = made.Details,
                    IsVisible = made.Details.Length > 0,
                    Classes = { "muted" },
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        var strip = MadeStrip(made, options);
        var id = Number("ID", initial.Id);
        var name = Input("Name", initial.Name, 300);
        var note = new TextBlock { Classes = { "muted" }, Text = "A resource of that type and ID has its data replaced.", TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                source,
                new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "MAKE", Classes = { "caption" } }, list } },
                Fields(("First ID", id), ("Name", name)),
                strip,
                note,
            },
        };
        var cancel = Button("Cancel", window.Close);
        var import = Button("Import", () =>
        {
            dialog.Result = new ImportChoice(options.Type, Id(id), name.Text ?? "");
            window.Close();
        });
        Compose(window, Header(window, title, () => { }), body, Footer(cancel, import, null));
        return dialog;
    }

    // What the chosen "Make" choice makes, at 2× on a checkerboard with each type under it, and how colours are mapped;
    // hidden when nothing is drawn (a sound, or a choice the file cannot make).
    private static StackPanel MadeStrip(ImportSource made, ImportOptions options)
    {
        var items = new ItemsControl
        {
            Name = "MadePreview",
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = new FuncDataTemplate<PreviewImage>((image, _) => image is null ? null : new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(0, 0, 12, 4),
                Children =
                {
                    new Border
                    {
                        Classes = { "checker" },
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new PixelImage { Source = Images.FromPng.Convert(image.Png, typeof(Bitmap), null, CultureInfo.InvariantCulture) as Bitmap, Zoom = 2 },
                    },
                    new TextBlock { Text = image.Caption, Classes = { "mono", "muted" }, FontSize = 11 },
                },
            }),
        };
        var note = new TextBlock
        {
            Name = "ColourNote",
            Classes = { "muted" },
            TextWrapping = TextWrapping.Wrap,
            Text = "Colours become the nearest in each kind’s standard colour table, without dithering; pixels less than half opaque are masked out.",
        };
        var strip = new StackPanel
        {
            Name = "MadeStrip",
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "PREVIEW", Classes = { "caption" } },
                new ScrollViewer { Content = items, MaxHeight = 180, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
                note,
            },
        };
        void Update()
        {
            var images = made.Preview(options.Type);
            items.ItemsSource = images;
            strip.IsVisible = images.Count > 0;
        }

        options.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ImportOptions.Selected) or nameof(ImportOptions.Kind))
            {
                Update();
            }
        };
        Update();
        return strip;
    }

    // A "Make" row: the label, its types in mono, why it is not offered, and for one icon kind the kind select.
    private static StackPanel OptionContent(ImportOption option, ImportOptions options)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = option.Label, VerticalAlignment = VerticalAlignment.Center });
        if (option.Code is { } code)
        {
            row.Children.Add(new TextBlock { Text = code, Classes = { "mono", "muted" }, VerticalAlignment = VerticalAlignment.Center });
        }

        if (option.Key == "kind" && options.Kinds.Count > 0)
        {
            var kinds = new ComboBox { ItemsSource = options.Kinds, SelectedItem = options.Kind, MinWidth = 90, Classes = { "mono" } };
            kinds.IsEnabled = options.IsKind;
            options.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportOptions.IsKind))
                {
                    kinds.IsEnabled = options.IsKind;
                }
            };
            kinds.SelectionChanged += (_, _) => options.Kind = kinds.SelectedItem as string ?? options.Kind;
            row.Children.Add(kinds);
        }

        if (option.Note is { } note)
        {
            row.Children.Add(new TextBlock { Text = note, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        }

        return row;
    }

    /// <summary>New File (or Import File): the name, type and creator.</summary>
    public static Dialog<NewFileChoice?> NewFile(string title, NewFileChoice initial)
    {
        var window = NewWindow(title);
        var dialog = new Dialog<NewFileChoice?>(window, null);
        var name = Input("Name", initial.Name, 300, maxLength: 31);
        var type = Input("Type", initial.Type, 90, mono: true, maxLength: 4);
        var creator = Input("Creator", initial.Creator, 90, mono: true, maxLength: 4);
        var note = new TextBlock { Classes = { "muted" }, Text = "Written to the image with File ▸ Save As ▸ HFS Volume Image.", TextWrapping = TextWrapping.Wrap };
        var body = new StackPanel { Spacing = 14, Children = { Fields(("Name", name), ("Type", type), ("Creator", creator)), note } };
        var cancel = Button("Cancel", window.Close);
        var create = Button("Create", () =>
        {
            dialog.Result = new NewFileChoice(name.Text ?? "", type.Text ?? "", creator.Text ?? "");
            window.Close();
        });
        Compose(window, Header(window, title, () => { }), body, Footer(cancel, create, null));
        return dialog;
    }

    /// <summary>New Folder: the name.</summary>
    public static Dialog<string?> NewFolder(string initial)
    {
        const string title = "New Folder";
        var window = NewWindow(title);
        var dialog = new Dialog<string?>(window, null);
        var name = Input("Name", initial, 300, maxLength: 31);
        var cancel = Button("Cancel", window.Close);
        var create = Button("Create", () =>
        {
            dialog.Result = name.Text ?? "";
            window.Close();
        });
        Compose(window, Header(window, title, () => { }), Fields(("Name", name)), Footer(cancel, create, null));
        return dialog;
    }

    /// <summary>Unsaved changes ("3 resources in Finder were edited."): Don’t Save on the left, Cancel and Save on the right.</summary>
    public static Dialog<SaveChanges> SaveChanges(string fileName, string edited)
    {
        var window = NewWindow("Unsaved changes");
        var dialog = new Dialog<SaveChanges>(window, ViewModels.SaveChanges.Cancel);
        Button Choice(string label, SaveChanges choice) => Button(label, () =>
        {
            dialog.Result = choice;
            window.Close();
        });
        var body = Alert(AlertIcon.Warning, $"Save changes to “{fileName}” before closing?", $"{edited} If you don’t save, the changes are lost.");
        Compose(window, null, body, Footer(Choice("Cancel", ViewModels.SaveChanges.Cancel), Choice("Save", ViewModels.SaveChanges.Save),
            Choice("Don’t Save", ViewModels.SaveChanges.Discard)));
        return dialog;
    }

    /// <summary>Unapplied changes: Discard on the left, Cancel and Apply on the right; with an error Apply is not offered.</summary>
    public static Dialog<DraftChoice> ApplyDraft(string what, string? error)
    {
        var window = NewWindow("Unapplied changes");
        var dialog = new Dialog<DraftChoice>(window, DraftChoice.Cancel);
        Button Choice(string label, DraftChoice choice) => Button(label, () =>
        {
            dialog.Result = choice;
            window.Close();
        });
        var detail = error is null
            ? "You edited this resource but haven't applied the changes."
            : $"You edited this resource but the changes can't be applied: {error}";
        var apply = Choice("Apply", DraftChoice.Apply);
        apply.IsEnabled = error is null;
        Compose(window, null, Alert(AlertIcon.Warning, $"Apply your changes to {what}?", detail),
            Footer(Choice("Cancel", DraftChoice.Cancel), apply, Choice("Discard", DraftChoice.Discard)));
        return dialog;
    }

    /// <summary>
    /// First Aid (volume-tools.md §5): it checks as it opens (the progress state), then shows the verdict banner (icon,
    /// headline, sentence; never colour alone) over one mono log with muted section headings; Copy report on the left,
    /// Done on the right with Repair when it can repair or Extract All… when it can't.
    /// </summary>
    public static Dialog<bool> FirstAid(FirstAidViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var window = NewWindow(model.Title);
        var dialog = new Dialog<bool>(window, false);

        var icon = new SeverityIcon { Name = "VerdictIcon", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        var headline = new TextBlock { Name = "VerdictHeadline", Classes = { "strong" }, TextWrapping = TextWrapping.Wrap };
        var sentence = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var words = new StackPanel { Spacing = 2, Children = { headline, sentence } };
        Grid.SetColumn(words, 1);
        var banner = new Border
        {
            Name = "VerdictBanner",
            Classes = { "verdict-banner" },
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Children = { icon, words } },
        };

        var lines = new StackPanel();
        var log = new Border { Name = "FirstAidLog", Classes = { "first-aid-log" }, Child = new ScrollViewer { Content = lines } };
        IReadOnlyList<FirstAidSection>? shown = null;
        void Fill()
        {
            lines.Children.Clear();
            foreach (var section in model.Sections)
            {
                lines.Children.Add(new TextBlock { Text = section.Heading, Classes = { "muted", "log-heading" } });
                foreach (var line in section.Lines)
                {
                    lines.Children.Add(new TextBlock { Text = line, Classes = { "mono", "log-line" }, TextWrapping = TextWrapping.Wrap });
                }
            }
        }

        var error = Line("error");
        var result = new StackPanel { Spacing = 10, Children = { banner, log, error } };
        var (running, updateRunning) = Running(model);
        var body = new StackPanel { Width = 360, Spacing = 10, Children = { result, running } };

        var copy = Button("Copy report", () =>
        {
            if (TopLevel.GetTopLevel(window)?.Clipboard is { } clipboard)
            {
                _ = clipboard.SetTextAsync(model.ReportText);
            }
        });
        copy.Name = "CopyReport";
        DockPanel.SetDock(copy, Dock.Left);
        var cancel = Button("Cancel", () => model.CancelCommand.Execute(null));
        var done = Button("Done", window.Close);
        var repair = new Button { Content = "Repair", MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" }, Command = model.RepairCommand };
        var extract = Button("Extract All…", () =>
        {
            window.Close();
            model.ExtractAllCommand.Execute(null);
        }, "accent");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, done, repair, extract } };
        DockPanel.SetDock(right, Dock.Right);
        var footer = new Border { Name = "DialogFooter", Classes = { "dialog-footer" }, Child = new DockPanel { LastChildFill = false, Children = { copy, right } } };

        void Update()
        {
            bool busy = model.IsRunning, known = model.HasOutcome;
            result.IsVisible = !busy && known;
            banner.Classes.Set("ok", model.Outcome is FirstAidOutcome.Ok or FirstAidOutcome.Repaired);
            banner.Classes.Set("warning", model.Outcome == FirstAidOutcome.NeedsRepair);
            banner.Classes.Set("error", model.Outcome == FirstAidOutcome.CannotRepair);
            icon.IsSuccess = model.Outcome is FirstAidOutcome.Ok or FirstAidOutcome.Repaired;
            icon.Severity = model.Outcome == FirstAidOutcome.CannotRepair ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
            headline.Text = model.Headline;
            sentence.Text = model.Sentence;
            Avalonia.Automation.AutomationProperties.SetName(banner, $"{model.Headline}. {model.Sentence}");
            if (!ReferenceEquals(shown, model.Sections))
            {
                shown = model.Sections;
                Fill();
            }

            log.IsVisible = model.HasLog;
            error.IsVisible = model.HasError;
            error.Text = model.Error;
            copy.IsVisible = !busy && known;
            cancel.IsVisible = cancel.IsCancel = busy;
            done.IsVisible = done.IsCancel = !busy;
            repair.IsVisible = repair.IsDefault = !busy && model.Outcome == FirstAidOutcome.NeedsRepair;
            extract.IsVisible = extract.IsDefault = !busy && model.Outcome == FirstAidOutcome.CannotRepair && model.ExtractAll is not null;
            done.IsDefault = !repair.IsVisible && !extract.IsVisible;
            done.Classes.Set("accent", done.IsDefault);
            updateRunning();
        }

        Update();
        model.PropertyChanged += (_, _) => Update();
        window.Opened += async (_, _) =>
        {
            if (model.CheckCommand.CanExecute(null))
            {
                await model.CheckCommand.ExecuteAsync(null);
                if (!model.HasOutcome && !model.HasError)
                {
                    window.Close();                                     // the check was cancelled
                }
            }
        };
        window.Closing += (_, _) => model.CancelCommand.Execute(null);
        Compose(window, Header(window, model.Title, () => model.CancelCommand.Execute(null)), body, footer);
        return dialog;
    }

    // ---- Volume operations (design/boards/volume-tools.md §2) ----

    private static TextBlock Line(params string[] classes)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (var c in classes)
        {
            text.Classes.Add(c);
        }

        return text;
    }

    // The progress state: the bold step line, a 6 px bar, the detail and what Cancel does; the returned action
    // brings it up to date with the model.
    private static (StackPanel Panel, Action Update) Running(VolumeOperation model)
    {
        var step = Line("strong");
        var bar = new ShareBar { Name = "OperationProgress", Classes = { "fork-bar" } };
        Avalonia.Automation.AutomationProperties.SetName(bar, "Progress");
        var detail = Line("muted");
        var note = Line("muted");
        var panel = new StackPanel { Spacing = 8, Children = { step, bar, detail, note } };
        return (panel, () =>
        {
            panel.IsVisible = model.IsRunning;
            note.Text = model.CancelNote;
            step.Text = model.StepText;
            bar.Value = model.Progress;
            detail.Text = model.DetailText;
        });
    }

    /// <summary>
    /// Defragment (volume-tools.md §3): a sentence, the allocation map ("Now", then "After") and the Now and After
    /// figures; the progress state while it runs; Cancel and Defragment before, Cancel while running, Done after (and
    /// only Done for a volume already in order).
    /// </summary>
    public static Dialog<bool> Defragment(DefragmentViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var window = NewWindow(model.Title);
        var dialog = new Dialog<bool>(window, false);
        var figures = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,*"), ColumnSpacing = 12, RowSpacing = 6, Name = "DefragmentFigures" };
        void Cell(string text, int row, int column, params string[] classes)
        {
            var cell = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            foreach (var c in classes)
            {
                cell.Classes.Add(c);
            }

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            figures.Children.Add(cell);
        }

        IReadOnlyList<DefragmentFigure>? shown = null;
        void Fill()
        {
            figures.Children.Clear();
            figures.RowDefinitions.Clear();
            for (var row = 0; row <= model.Figures.Count; row++)
            {
                figures.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            Cell("Now", 0, 1, "muted");
            Cell("After", 0, 2, "muted");
            for (var i = 0; i < model.Figures.Count; i++)
            {
                var figure = model.Figures[i];
                string kind = figure.Label == "Can shrink to" ? "mono" : "plain";
                Cell(figure.Label, i + 1, 0, "muted");
                Cell(figure.Now, i + 1, 1, kind);
                Cell(figure.After, i + 1, 2, kind, "strong");
            }
        }

        var lead = Line();
        var mapTitle = Line("muted");
        var map = new AllocationMap { Name = "DefragmentMap", Classes = { "volume-map" } };
        var sessionNote = new TextBlock { Text = "Changes stay in this session until you Save As.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var doneNote = Line("muted");
        var error = Line("error");
        var explained = new StackPanel { Spacing = 10, Children = { lead, mapTitle, map, figures, sessionNote, doneNote, error } };
        var (running, updateRunning) = Running(model);
        var body = new StackPanel { Width = 420, Spacing = 10, Children = { explained, running } };

        var cancel = Button("Cancel", () =>
        {
            if (model.IsRunning)
            {
                model.CancelCommand.Execute(null);
            }
            else
            {
                window.Close();
            }
        });
        cancel.IsCancel = true;
        cancel.Classes.Add("dialog-cancel");
        var start = new Button { Content = "Defragment", MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" }, IsDefault = true, Command = model.StartCommand };
        var done = Button("Done", () =>
        {
            dialog.Result = model.IsDone;
            window.Close();
        }, "accent");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, start, done } };
        DockPanel.SetDock(right, Dock.Right);
        var footer = new Border { Name = "DialogFooter", Classes = { "dialog-footer" }, Child = new DockPanel { LastChildFill = false, Children = { right } } };

        void Update()
        {
            explained.IsVisible = !model.IsRunning;
            lead.Text = model.Lead;
            mapTitle.Text = model.MapTitle;
            map.Layout = model.MapLayout;
            if (!ReferenceEquals(shown, model.Figures) && (shown is null || !shown.SequenceEqual(model.Figures)))
            {
                shown = model.Figures;
                Fill();
            }

            sessionNote.IsVisible = model.IsReady && !model.IsInOrder;
            doneNote.IsVisible = model.IsDone;
            doneNote.Text = model.DoneNote;
            error.IsVisible = model.HasError;
            error.Text = model.Error;
            cancel.IsVisible = !model.IsDone && !model.IsInOrder;
            start.IsVisible = model.IsReady && !model.IsInOrder;
            done.IsVisible = model.IsDone || model.IsInOrder;
            start.IsDefault = start.IsVisible;
            done.IsDefault = done.IsVisible;
            updateRunning();
        }

        Update();
        model.PropertyChanged += (_, _) => Update();
        window.Closing += (_, _) => model.CancelCommand.Execute(null);
        Compose(window, Header(window, model.Title, () => model.CancelCommand.Execute(null)), body, footer);
        return dialog;
    }

    // "Smallest 403K": the words muted, the size in mono.
    private static TextBlock ScaleLabel(string label, HorizontalAlignment alignment)
    {
        int space = label.IndexOf(' ', StringComparison.Ordinal);
        var text = new TextBlock { Classes = { "muted" }, HorizontalAlignment = alignment, FontSize = 12 };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(label[..(space + 1)]));
        var size = new Avalonia.Controls.Documents.Run(label[(space + 1)..]);
        size.Bind(Avalonia.Controls.Documents.TextElement.FontFamilyProperty, text.GetResourceObservable("CmFontMono"));
        text.Inlines.Add(size);
        return text;
    }

    /// <summary>
    /// Resize (volume-tools.md §4): the size box (mono 14) and its unit select, the note under them as you type (with
    /// Defragment first… when the free space is in the way), the slider with its scale line, and the block size select;
    /// the progress state while it runs. True when the volume was resized.
    /// </summary>
    public static Dialog<bool> Resize(ResizeViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var window = NewWindow(model.Title);
        var dialog = new Dialog<bool>(window, false);
        bool syncing = false;

        var box = new TextBox { Name = "Size", Width = 160, Classes = { "size-box" }, Text = model.Text };
        Avalonia.Automation.AutomationProperties.SetName(box, "New size");
        var unit = new ComboBox { Name = "Unit", ItemsSource = model.Units, SelectedItem = model.Unit, MinWidth = 84, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(unit, "Unit");
        var field = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, unit } };

        var noteText = new TextBlock { Name = "ResizeNote", TextWrapping = TextWrapping.Wrap };
        var defragmentFirst = new Button { Content = "Defragment first…", Command = model.DefragmentFirstCommand, HorizontalAlignment = HorizontalAlignment.Left };
        var note = new Border { Name = "ResizeNoteBox", Classes = { "inline-note" }, Child = new StackPanel { Spacing = 8, Children = { noteText, defragmentFirst } } };

        var nowLabel = ScaleLabel(model.NowLabel, HorizontalAlignment.Center);
        var largestLabel = ScaleLabel(model.LargestLabel, HorizontalAlignment.Right);
        Grid.SetColumn(nowLabel, 1);
        Grid.SetColumn(largestLabel, 2);
        var scaleLine = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), Children = { nowLabel, largestLabel } };
        TextBlock? smallestLabel = null;
        void SmallestLabel()
        {
            if (smallestLabel is not null)
            {
                scaleLine.Children.Remove(smallestLabel);
            }

            smallestLabel = ScaleLabel(model.SmallestLabel, HorizontalAlignment.Left);
            scaleLine.Children.Add(smallestLabel);
        }

        SmallestLabel();
        var slider = new SizeSlider { Name = "SizeSlider", Minimum = model.Smallest, Maximum = model.Largest, Current = model.Current, Value = model.Size ?? model.Current };

        var blockSize = new ComboBox
        {
            Name = "BlockSize",
            ItemTemplate = new FuncDataTemplate<BlockSizeChoice>((choice, _) => new TextBlock { Text = choice?.Label }),
            MinWidth = 200,
        };
        Avalonia.Automation.AutomationProperties.SetName(blockSize, "Block size");
        var blockRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { new TextBlock { Text = "Block size", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, blockSize },
        };

        var error = Line("error");
        var choosing = new StackPanel { Spacing = 10, Children = { field, note, scaleLine, slider, blockRow, error } };
        var (running, updateRunning) = Running(model);
        var body = new StackPanel { Width = 420, Spacing = 10, Children = { choosing, running } };

        var cancel = Button("Cancel", () =>
        {
            if (model.IsRunning)
            {
                model.CancelCommand.Execute(null);
            }
            else
            {
                window.Close();
            }
        });
        var resize = new Button { Content = "Resize", MinWidth = 76, HorizontalContentAlignment = HorizontalAlignment.Center, Command = model.StartCommand };
        var footer = Footer(cancel, resize, null);
        resize.IsDefault = true;

        void Update()
        {
            syncing = true;
            if (box.Text != model.Text)
            {
                box.Text = model.Text;
                box.ClearSelection();
                box.CaretIndex = model.Text.Length;
            }

            unit.SelectedItem = model.Unit;
            if (!ReferenceEquals(blockSize.ItemsSource, model.BlockSizes))
            {
                blockSize.ItemsSource = model.BlockSizes;
            }

            blockSize.SelectedItem = model.SelectedBlockSize;
            slider.Minimum = model.Smallest;
            slider.WarningEnd = model.SmallestNow > model.Smallest && model.SelectedBlockSize?.Size is null ? model.SmallestNow : 0;
            slider.Marks = model.Marks;
            slider.Severity = model.NoteSeverity;
            if (model.Size is { } size)
            {
                slider.Value = Math.Clamp(size, model.Smallest, model.Largest);
            }

            Avalonia.Automation.AutomationProperties.SetName(slider, model.ValueText);
            syncing = false;

            box.Classes.Set("invalid", model.IsInvalid);
            Avalonia.Automation.AutomationProperties.SetHelpText(box, model.IsInvalid ? model.NoteText : null);
            note.IsVisible = model.HasNote;
            note.Classes.Set("info", model.NoteSeverity == NoteSeverity.Info);
            note.Classes.Set("warning", model.NoteSeverity == NoteSeverity.Warning);
            note.Classes.Set("error", model.NoteSeverity == NoteSeverity.Error);
            noteText.Text = model.NoteText;
            defragmentFirst.IsVisible = model.NoteSeverity == NoteSeverity.Warning && model.DefragmentFirst is not null;
            error.IsVisible = model.HasError;
            error.Text = model.Error;
            choosing.IsVisible = !model.IsRunning;
            resize.IsVisible = !model.IsRunning;
            updateRunning();
            if (model.IsDone)
            {
                dialog.Result = true;
                window.Close();
            }
        }

        box.TextChanged += (_, _) =>
        {
            if (!syncing && box.Text is { } typed && typed != model.Text)
            {
                model.Text = typed;
            }
        };
        unit.SelectionChanged += (_, _) =>
        {
            if (!syncing && unit.SelectedItem is string chosen)
            {
                model.Unit = chosen;
            }
        };
        blockSize.SelectionChanged += (_, _) =>
        {
            if (!syncing && blockSize.SelectedItem is BlockSizeChoice chosen)
            {
                model.SelectedBlockSize = chosen;
            }
        };
        slider.PropertyChanged += (_, e) =>
        {
            if (!syncing && e.Property == SizeSlider.ValueProperty && slider.Value != model.Size)
            {
                model.SetSize(slider.Value);
            }
        };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ResizeViewModel.SmallestLabel))
            {
                SmallestLabel();
            }

            Update();
        };
        Update();
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        window.Closing += (_, _) => model.CancelCommand.Execute(null);
        Compose(window, Header(window, model.Title, () => model.CancelCommand.Execute(null)), body, footer);
        return dialog;
    }

    /// <summary>A yes/no question: No and Yes.</summary>
    public static Dialog<bool> Confirm(string title, string message)
    {
        var window = NewWindow(title);
        var dialog = new Dialog<bool>(window, false);
        var no = Button("No", window.Close);
        var yes = Button("Yes", () =>
        {
            dialog.Result = true;
            window.Close();
        });
        Compose(window, null, Alert(AlertIcon.Question, message, null), Footer(no, yes, null));
        return dialog;
    }
}
