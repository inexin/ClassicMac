using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassicMac.App.ViewModels;
using ClassicMac.Resources;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.Views;

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
