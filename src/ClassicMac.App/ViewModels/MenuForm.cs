using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Interface;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// A choice in the Mark select (design/boards/read-then-edit.md): the mark's character (as the item's text has it;
/// null for "Other…", whose code is typed), its name in words and its Mac character code ("$12"), so the list needs
/// no symbol glyphs.
/// </summary>
public sealed record MarkChoice(string? Mark, string Label, string Code = "")
{
    /// <summary>The label in the closed select, beside the code field for Other: without its ellipsis.</summary>
    public string ShortLabel => Label.TrimEnd('…');

    public override string ToString() => Code.Length == 0 ? Label : $"{Label} {Code}";
}

/// <summary>One item of a <c>'MENU'</c>: its values, and how the read-only table shows them.</summary>
public sealed partial class MenuItemRow : ObservableObject
{
    private const int StyleBits = 0x7F;

    private static readonly string[] StyleNames = ["Bold", "Italic", "Underline", "Outline", "Shadow", "Condense", "Extend"];

    /// <summary>
    /// The marks the Mark select offers: none, the check mark ($12 in the system font), the diamond ($13), the
    /// bullet ($A5), and Other…, whose code is typed.
    /// </summary>
    public static IReadOnlyList<MarkChoice> Marks { get; } =
    [
        new("", "None"), new(MacRoman.ToChar(0x12).ToString(), "Check mark", "$12"), new(MacRoman.ToChar(0x13).ToString(), "Diamond", "$13"),
        new(MacRoman.ToChar(0xA5).ToString(), "Bullet", "$A5"), new(null, "Other…"),
    ];

    private static MarkChoice Other => Marks[^1];

    // Other… chosen with no code typed yet: the select shows it while the mark is still the old one.
    private bool otherChosen;

    public MenuItemRow(MenuItem item)
    {
        (text, enabled, icon, face) = (item.Text, item.Enabled, item.Icon, item.Face);
        key = item.KeyEquivalent is 0 ? "" : item.KeyKind is null ? MacRoman.ToChar(item.KeyEquivalent).ToString() : $"${item.KeyEquivalent:X2}";
        mark = item.Submenu is { } submenu ? $"menu {submenu}" : item.Mark is 0 ? "" : MacRoman.ToChar(item.Mark).ToString();
        MarkChoices = item.Submenu is { } sub ? [.. Marks, new MarkChoice(mark, $"Submenu {sub}")] : Marks;
    }

    /// <summary>Its number in the menu, from 1.</summary>
    [ObservableProperty] private int number;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDivider))]
    private string text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnabledText), nameof(IsDimmed))]
    private bool enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconText), nameof(IsDivider))]
    private decimal icon;

    /// <summary>The QuickDraw style bits (all eight kept; the seven named ones have their own toggles).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Bold), nameof(Italic), nameof(Underline), nameof(Outline), nameof(Shadow), nameof(Condense), nameof(Extend),
        nameof(StyleText), nameof(HasMoreStyles))]
    private decimal face;

    /// <summary>The Command key (one character), or a code as $1B (submenu), $1C (script), $1A/$1D/$1E (icon forms).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText), nameof(IsDivider))]
    private string key;

    /// <summary>The mark character; for a submenu (key $1B) "menu N".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkText), nameof(MarkChoice), nameof(MarkCode), nameof(IsOtherMark))]
    private string mark;

    /// <summary>Whether its Command key is also another item's (both key fields show the error).</summary>
    [ObservableProperty] private bool hasKeyConflict;

    /// <summary>Whether it is the selected row (and the preview's highlighted item).</summary>
    [ObservableProperty] private bool isSelected;

    public bool Bold { get => Bit(0x01); set => SetBit(0x01, value); }

    public bool Italic { get => Bit(0x02); set => SetBit(0x02, value); }

    public bool Underline { get => Bit(0x04); set => SetBit(0x04, value); }

    public bool Outline { get => Bit(0x08); set => SetBit(0x08, value); }

    public bool Shadow { get => Bit(0x10); set => SetBit(0x10, value); }

    public bool Condense { get => Bit(0x20); set => SetBit(0x20, value); }

    public bool Extend { get => Bit(0x40); set => SetBit(0x40, value); }

    private bool Bit(int mask) => ((int)Face & mask) != 0;

    private void SetBit(int mask, bool on) => Face = on ? (int)Face | mask : (int)Face & ~mask;

    /// <summary>Whether a style past the first three (behind the "More" popover) is on.</summary>
    public bool HasMoreStyles => ((int)Face & 0x78) != 0;

    /// <summary>A divider line: text starting with "-", no icon, and no Command key (as <see cref="MenuItem.IsDivider"/>).</summary>
    public bool IsDivider => Text.StartsWith('-') && Icon == 0 && Key.Trim() is "" or "$1B" or "$1C";

    /// <summary>Disabled items read in CmTextMuted.</summary>
    public bool IsDimmed => !Enabled;

    public string KeyText => Key.Trim() switch
    {
        "" => "—",
        var k when k.StartsWith('$') && byte.TryParse(k[1..], NumberStyles.HexNumber, null, out var code) =>
            new MenuItem("", 0, code, 0, 0, true).KeyKind ?? $"⌘{(char)code}",
        var k => "⌘" + k.ToUpperInvariant(),
    };

    /// <summary>The table's Mark: the mark's name ("Check mark"), another one as its character and code ("* $2A"), "—" for none.</summary>
    public string MarkText => Mark.Length == 0
        ? "—"
        : Marks.FirstOrDefault(m => m.Mark == Mark) is { } known ? known.Label
        : MarkCode.Length > 0 ? $"{Mark} {MarkCode}"
        : Mark;

    public string IconText => Icon == 0 ? "—" : ((int)Icon).ToString(CultureInfo.InvariantCulture);

    public string StyleText => ((int)Face & StyleBits) == 0
        ? "—"
        : string.Join(", ", StyleNames.Where((_, bit) => ((int)Face & (1 << bit)) != 0));

    public string EnabledText => Enabled ? "Yes" : "No";

    /// <summary>The Mark select's choices: <see cref="Marks"/>, and for a submenu item its submenu.</summary>
    public IReadOnlyList<MarkChoice> MarkChoices { get; }

    /// <summary>The chosen mark: a named one, the submenu, or Other… for any other character (or when Other… was just chosen).</summary>
    public MarkChoice MarkChoice
    {
        get => otherChosen ? Other : MarkChoices.FirstOrDefault(m => m.Mark == Mark) ?? Other;
        set
        {
            if (value is null)
            {
                return;
            }

            otherChosen = value.Mark is null;
            if (value.Mark is { } chosen)
            {
                Mark = chosen;
            }

            OnPropertyChanged(nameof(MarkChoice));
            OnPropertyChanged(nameof(IsOtherMark));
        }
    }

    /// <summary>Whether the mark is Other… (its code field shows).</summary>
    public bool IsOtherMark => ReferenceEquals(MarkChoice, Other);

    /// <summary>
    /// The mark's Mac character code ("$2A"); typing one (or the character) sets the mark, and a code of a named mark
    /// chooses it. Empty for none or a submenu.
    /// </summary>
    public string MarkCode
    {
        get => Mark.Length == 1 && MacRoman.TryGetByte(Mark[0], out var b) ? $"${b:X2}" : "";
        set
        {
            var typed = (value ?? "").Trim();
            Mark = typed.StartsWith('$') && byte.TryParse(typed[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                ? MacRoman.ToChar(code).ToString()
                : typed;
            otherChosen = Marks.All(m => m.Mark != Mark || m.Mark.Length == 0);
            OnPropertyChanged(nameof(MarkChoice));
            OnPropertyChanged(nameof(IsOtherMark));
        }
    }

    /// <summary>The Command key's character for comparing items (⌘s is ⌘S), or null for none or a code.</summary>
    internal char? CommandKey => Key.Trim() is { Length: 1 } k ? char.ToUpperInvariant(k[0]) : null;

    public MenuItem ToItem()
    {
        byte key = Key.Trim() switch
        {
            "" => 0,
            var k when k.StartsWith('$') && byte.TryParse(k[1..], NumberStyles.HexNumber, null, out var code) => code,
            var k when k.Length == 1 && MacRoman.TryGetByte(k[0], out var b) => b,
            _ => throw new ArgumentException($"“{Key}” is not a Command key (one character, or a code like $1B)."),
        };
        byte mark = Mark.Trim() switch
        {
            "" => 0,
            var m when m.StartsWith("menu ", StringComparison.Ordinal) && byte.TryParse(m[5..], out var id) => id,
            var m when m.Length == 1 && MacRoman.TryGetByte(m[0], out var b) => b,
            _ => throw new ArgumentException($"“{Mark}” is not a mark (one character, or “menu N” for a submenu)."),
        };
        return new MenuItem(Text, (byte)Icon, key, mark, (byte)Face, Enabled);
    }

    /// <summary>The properties that hold the item's values (the others show them, or the selection).</summary>
    internal static bool IsValue(string? propertyName) =>
        propertyName is nameof(Text) or nameof(Enabled) or nameof(Icon) or nameof(Face) or nameof(Key) or nameof(Mark);
}

/// <summary>
/// <c>'MENU'</c> (design/boards/read-then-edit.md, E2): its title, flags and items, a read-only table and inputs, a
/// live preview whose highlighted item is the selected row, and an error for Command keys used twice.
/// </summary>
public sealed partial class MenuForm : DataForm
{
    private readonly MenuResource menu;

    public MenuForm(Resource resource, MenuResource menu) : base(resource)
    {
        this.menu = menu;
        (id, definition, title, enabled) = (menu.Id, menu.Definition, menu.Title, menu.Enabled);
        foreach (var item in menu.Items)
        {
            Items.Add(Watched(new MenuItemRow(item)));
        }

        Items.CollectionChanged += OnItemsChanged;
        Renumber();
        Edited += (_, _) => Check();
        Check();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdText))]
    private decimal id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefinitionText))]
    private decimal definition;

    [ObservableProperty] private string title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnabledText))]
    private bool enabled;

    public ObservableCollection<MenuItemRow> Items { get; } = [];

    /// <summary>The selected row, highlighted in the preview too; null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewIndex))]
    [NotifyCanExecuteChangedFor(nameof(MoveSelectedUpCommand), nameof(MoveSelectedDownCommand))]
    private MenuItemRow? selectedItem;

    /// <summary>The menu as the values make it, for the live preview (the last good one while they have an error).</summary>
    [ObservableProperty] private MenuResource? preview;

    /// <summary>Why the values cannot be written (a bad key or mark, or a Command key used twice), or null.</summary>
    [ObservableProperty] private string? error;

    /// <summary>The preview's highlighted item: the selected row's index, or -1; a click in the preview selects its row.</summary>
    public int PreviewIndex
    {
        get => SelectedItem is { } row ? Items.IndexOf(row) : -1;
        set => SelectedItem = value >= 0 && value < Items.Count ? Items[value] : null;
    }

    public string EnabledText => Enabled ? "Yes" : "No";

    public string IdText => ((int)Id).ToString(CultureInfo.InvariantCulture);

    public string DefinitionText => ((int)Definition).ToString(CultureInfo.InvariantCulture);

    public override bool HasReadOnlyView => true;

    public override string EditHint => "Text “-” makes a divider. Esc cancels, Ctrl+Enter applies.";

    public override void SelectRow(object? row)
    {
        if (row is MenuItemRow item && Items.Contains(item))
        {
            SelectedItem = item;
        }
    }

    protected override bool IsValue(string? propertyName) =>
        propertyName is not (nameof(SelectedItem) or nameof(PreviewIndex) or nameof(Preview) or nameof(Error) or nameof(EnabledText)
            or nameof(IdText) or nameof(DefinitionText));

    partial void OnSelectedItemChanged(MenuItemRow? oldValue, MenuItemRow? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    [RelayCommand]
    private void Add() => AddRow(new MenuItem("Item", 0, 0, 0, 0, true));

    [RelayCommand]
    private void AddDivider() => AddRow(new MenuItem("-", 0, 0, 0, 0, false));

    private void AddRow(MenuItem item)
    {
        var row = Watched(new MenuItemRow(item));
        Items.Add(row);
        SelectedItem = row;
    }

    [RelayCommand]
    private void Remove(MenuItemRow row)
    {
        if (ReferenceEquals(row, SelectedItem))
        {
            SelectedItem = null;
        }

        Items.Remove(row);
    }

    [RelayCommand]
    private void MoveUp(MenuItemRow row) => Move(Items, row, -1);

    [RelayCommand]
    private void MoveDown(MenuItemRow row) => Move(Items, row, 1);

    private bool CanMoveSelectedUp() => SelectedItem is { } row && Items.IndexOf(row) > 0;

    private bool CanMoveSelectedDown() => SelectedItem is { } row && Items.IndexOf(row) is var i && i >= 0 && i < Items.Count - 1;

    /// <summary>Alt+Up: the selected row up one place (it stays selected).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveSelectedUp))]
    private void MoveSelectedUp() => Move(Items, SelectedItem!, -1);

    /// <summary>Alt+Down: the selected row down one place.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveSelectedDown))]
    private void MoveSelectedDown() => Move(Items, SelectedItem!, 1);

    public MenuResource ToMenu() => menu with
    {
        Id = (short)Id,
        Definition = (short)Definition,
        Title = Title,
        EnableFlags = Enabled ? menu.EnableFlags | 1 : menu.EnableFlags & ~1u,
        Items = Items.Select(i => i.ToItem()).ToList(),
    };

    public override byte[] BuildData()
    {
        var data = InterfaceWriter.WriteMenu(ToMenu());
        if (Conflict() is { } conflict)
        {
            throw new ArgumentException(conflict.Message);
        }

        return data;
    }

    // A row's value changes are the form's edits; its selection and display are not.
    private MenuItemRow Watched(MenuItemRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (MenuItemRow.IsValue(e.PropertyName))
            {
                RaiseEdited();
            }
        };
        return row;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Renumber();
        OnPropertyChanged(nameof(PreviewIndex));
        MoveSelectedUpCommand.NotifyCanExecuteChanged();
        MoveSelectedDownCommand.NotifyCanExecuteChanged();
        RaiseEdited();
    }

    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Number = i + 1;
        }
    }

    // The first Command key used twice: the later item, the earlier one, and the message; and every row that shares a key.
    private (string Message, HashSet<MenuItemRow> Rows)? Conflict()
    {
        string? message = null;
        var rows = new HashSet<MenuItemRow>();
        var first = new Dictionary<char, MenuItemRow>();
        foreach (var row in Items)
        {
            if (row.CommandKey is not { } key)
            {
                continue;
            }

            if (first.TryGetValue(key, out var earlier))
            {
                message ??= $"Item {row.Number} (“{row.Text}”) uses ⌘{key}, already used by item {earlier.Number} (“{earlier.Text}”).";
                rows.Add(earlier);
                rows.Add(row);
            }
            else
            {
                first[key] = row;
            }
        }

        return message is null ? null : (message, rows);
    }

    // After each edit: the error, the rows whose keys clash, and the preview.
    private void Check()
    {
        var conflict = Conflict();
        foreach (var row in Items)
        {
            row.HasKeyConflict = conflict?.Rows.Contains(row) == true;
        }

        try
        {
            Preview = ToMenu();
            Error = conflict?.Message;
        }
        catch (ArgumentException e)
        {
            Error = e.Message;
        }
    }
}
