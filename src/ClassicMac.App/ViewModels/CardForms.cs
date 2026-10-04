using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Interface;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels;

/// <summary>A named definition ID in a form's select: "Document window · 0".</summary>
public sealed record DefinitionChoice(int Value, string Name)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} · {Value}");
}

/// <summary>A named positioning word: "Stagger on parent window’s screen" (0x780A).</summary>
public sealed record PositionChoice(ushort Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A screen size the window's bounds are shown on.</summary>
public sealed record ScreenChoice(int Width, int Height, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An alert's item list as its stages name their buttons: the DITL's name and its items' texts.</summary>
public sealed record ItemList(string? Name, IReadOnlyList<string> Texts);

/// <summary>
/// A form shown as property cards (design/boards/window-alert.md, E4): read only, each value as text; editing, inputs.
/// Its display texts follow the values and are not edits.
/// </summary>
public abstract class CardForm(Resource resource) : DataForm(resource)
{
    /// <summary>The automatic positions (Window Manager's and Dialog Manager's positioning words, MacWindows.h).</summary>
    public static IReadOnlyList<PositionChoice> Positions { get; } =
    [
        new(0x0000, "None"),
        new(0x280A, "Center on main screen"),
        new(0x300A, "Alert position on main screen"),
        new(0x380A, "Stagger on main screen"),
        new(0xA80A, "Center on parent window"),
        new(0xB00A, "Alert position on parent window"),
        new(0xB80A, "Stagger on parent window"),
        new(0x680A, "Center on parent window’s screen"),
        new(0x700A, "Alert position on parent window’s screen"),
        new(0x780A, "Stagger on parent window’s screen"),
    ];

    public override bool HasReadOnlyView => true;

    /// <summary>The properties that only show values (raised again whenever a value changes).</summary>
    protected abstract IReadOnlyList<string> DisplayNames { get; }

    protected override bool IsValue(string? propertyName) => propertyName is not null && !DisplayNames.Contains(propertyName);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (IsValue(e.PropertyName) && e.PropertyName != nameof(IsEditing))
        {
            foreach (var name in DisplayNames)
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(name));
            }
        }
    }

    protected static string Number(decimal value) => ((long)value).ToString(CultureInfo.InvariantCulture);

    protected static string YesNo(bool value) => value ? "Yes" : "No";

    protected static string Quoted(string text) => $"“{text}”";

    protected static string Pair(decimal a, decimal b) => $"{Number(a)}, {Number(b)}";

    protected static string Size(decimal top, decimal left, decimal bottom, decimal right) => $"{Number(right - left)} × {Number(bottom - top)}";

    protected static string PositionNameOf(bool has, decimal position) =>
        !has ? "No positioning word" : Positions.FirstOrDefault(p => p.Code == (ushort)position)?.Name ?? "Other";

    protected static string? PositionCodeOf(bool has, decimal position) =>
        has ? string.Create(CultureInfo.InvariantCulture, $"0x{(ushort)position:X4}") : null;

    // A definition ID's select: the standard ones, and the resource's own when it is another.
    protected static IReadOnlyList<DefinitionChoice> ChoicesWith(IReadOnlyList<DefinitionChoice> standard, int current, string other) =>
        standard.Any(d => d.Value == current) ? standard : [.. standard, new DefinitionChoice(current, other)];
}

/// <summary><c>'WIND'</c> and <c>'DLOG'</c> as property cards: Bounds, and the Window card.</summary>
public sealed partial class WindowForm : CardForm
{
    /// <summary>The standard window definitions by name (Window Manager, MacWindows.h).</summary>
    public static IReadOnlyList<DefinitionChoice> StandardDefinitions { get; } =
    [
        new(0, "Document window"), new(1, "Dialog box"), new(2, "Plain box"), new(3, "Alt dialog box"), new(4, "No grow document"),
        new(5, "Movable modal"), new(8, "Zoom document"), new(12, "Zoom no grow"), new(16, "Rounded"),
    ];

    public WindowForm(Resource resource, WindowTemplate window, bool dialog) : base(resource)
    {
        IsDialog = dialog;
        (top, left, bottom, right) = (window.Bounds.Top, window.Bounds.Left, window.Bounds.Bottom, window.Bounds.Right);
        (definition, visible, goAway, refCon, title, itemsId) = (window.Definition, window.Visible, window.GoAway, window.RefCon, window.Title, window.ItemsId ?? 0);
        (hasPosition, position) = (window.Position is not null, window.Position ?? 0);
        Definitions = ChoicesWith(StandardDefinitions, window.Definition, "Other WDEF");
    }

    public bool IsDialog { get; }

    /// <summary>The screens the bounds can be shown on (the right panel's select).</summary>
    public static IReadOnlyList<ScreenChoice> Screens { get; } =
        [new(512, 342, "512 × 342 Classic"), new(640, 480, "640 × 480"), new(832, 624, "832 × 624"), new(1024, 768, "1024 × 768")];

    /// <summary>The screen the right panel shows the bounds on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenTitle))]
    private ScreenChoice selectedScreen = Screens[0];

    public string ScreenTitle => string.Create(CultureInfo.InvariantCulture, $"Bounds on a {SelectedScreen.Width} × {SelectedScreen.Height} screen");

    [ObservableProperty] private decimal top;
    [ObservableProperty] private decimal left;
    [ObservableProperty] private decimal bottom;
    [ObservableProperty] private decimal right;
    [ObservableProperty] private decimal definition;
    [ObservableProperty] private bool visible;
    [ObservableProperty] private bool goAway;
    [ObservableProperty] private decimal refCon;
    [ObservableProperty] private string title;
    [ObservableProperty] private decimal itemsId;
    [ObservableProperty] private bool hasPosition;
    [ObservableProperty] private decimal position;

    /// <summary>The Definition select's choices.</summary>
    public IReadOnlyList<DefinitionChoice> Definitions { get; }

    public DefinitionChoice? SelectedDefinition
    {
        get => Definitions.FirstOrDefault(d => d.Value == Definition);
        set
        {
            if (value is not null)
            {
                Definition = value.Value;
            }
        }
    }

    public PositionChoice? SelectedPosition
    {
        get => HasPosition ? Positions.FirstOrDefault(p => p.Code == (ushort)Position) : null;
        set
        {
            if (value is not null)
            {
                HasPosition = true;
                Position = value.Code;
            }
        }
    }

    public string TopLeftText => Pair(Top, Left);

    public string BottomRightText => Pair(Bottom, Right);

    public string SizeText => Size(Top, Left, Bottom, Right) + " pixels";

    public string TitleText => Quoted(Title);

    public string DefinitionName
    {
        get
        {
            var d = (int)Definition;
            return StandardDefinitions.FirstOrDefault(s => s.Value == d)?.Name
                ?? (d is > 16 and <= 23 ? $"Rounded, variation {d - 16}" : $"WDEF {d >> 4}, variation {d & 15}");
        }
    }

    public string DefinitionCode => InterfaceNames.Window((short)Definition) is { } name ? $"{name} · {Number(Definition)}" : Number(Definition);

    public string VisibleText => YesNo(Visible);

    public string GoAwayText => YesNo(GoAway);

    public string RefConText => Number(RefCon);

    public string PositionName => PositionNameOf(HasPosition, Position);

    public string? PositionCode => PositionCodeOf(HasPosition, Position);

    public string ItemsText => $"'DITL' {Number(ItemsId)}";

    protected override IReadOnlyList<string> DisplayNames { get; } =
    [
        nameof(SelectedScreen), nameof(ScreenTitle),
        nameof(SelectedDefinition), nameof(SelectedPosition), nameof(TopLeftText), nameof(BottomRightText), nameof(SizeText), nameof(TitleText),
        nameof(DefinitionName), nameof(DefinitionCode), nameof(VisibleText), nameof(GoAwayText), nameof(RefConText), nameof(PositionName),
        nameof(PositionCode), nameof(ItemsText),
    ];

    public override byte[] BuildData() => InterfaceWriter.WriteWindow(new WindowTemplate(Rect(Top, Left, Bottom, Right), (short)Definition, Visible, GoAway,
        (int)RefCon, Title, IsDialog ? (short)ItemsId : null, HasPosition ? (ushort)Position : null), IsDialog);
}

/// <summary>
/// One stage of an alert (the 1st to 4th time it is shown in a row): its default button (item 1 or 2 only), whether it
/// is drawn, and its sound (0–3 beeps).
/// </summary>
public sealed partial class AlertStage : ObservableObject
{
    private static readonly string[] Ordinals = ["1st", "2nd", "3rd", "4th"];

    private readonly AlertForm form;

    public AlertStage(AlertForm form, int number, int boldItem, bool drawn, int sound)
    {
        this.form = form;
        Number = number;
        (this.boldItem, this.drawn, this.sound) = (boldItem, drawn, sound);
    }

    /// <summary>The sound select's choices, by beep count.</summary>
    public static IReadOnlyList<string> Sounds { get; } = ["Silent", "1 beep", "2 beeps", "3 beeps"];

    public int Number { get; }

    public string Ordinal => Ordinals[Number - 1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultText), nameof(DefaultIndex))]
    private int boldItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DrawnText))]
    private bool drawn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SoundName), nameof(SoundIndex))]
    private int sound;

    /// <summary>Whether this stage is the one selected (its row highlighted, the preview drawing it).</summary>
    [ObservableProperty] private bool isSelected;

    public string DefaultText => form.ItemLabel(BoldItem);

    public string DrawnText => Drawn ? "Yes" : "No";

    public string SoundName => Sounds[Math.Clamp(Sound, 0, 3)];

    /// <summary>The default button select's two choices, labelled with the items' text.</summary>
    public IReadOnlyList<string> DefaultChoices => [form.ItemLabel(1), form.ItemLabel(2)];

    public int DefaultIndex
    {
        get => BoldItem - 1;
        set => BoldItem = value is 1 ? 2 : 1;
    }

    public int SoundIndex
    {
        get => Sound;
        set => Sound = Math.Clamp(value, 0, 3);
    }

    // The item list changed: the labels follow.
    internal void Relabel()
    {
        OnPropertyChanged(nameof(DefaultText));
        OnPropertyChanged(nameof(DefaultChoices));
    }
}

/// <summary><c>'ALRT'</c> as a card (bounds, item list, position) and its four stages.</summary>
public sealed partial class AlertForm : CardForm
{
    private static readonly string[] StageValues = [nameof(AlertStage.BoldItem), nameof(AlertStage.Drawn), nameof(AlertStage.Sound)];
    private readonly Func<short, ItemList?> itemList;
    private ItemList? items;

    /// <summary>An alert's form; <paramref name="itemList"/> finds its item list by ID (for the stages' button labels).</summary>
    public AlertForm(Resource resource, AlertTemplate alert, Func<short, ItemList?> itemList) : base(resource)
    {
        this.itemList = itemList;
        (top, left, bottom, right) = (alert.Bounds.Top, alert.Bounds.Left, alert.Bounds.Bottom, alert.Bounds.Right);
        (itemsId, hasPosition, position) = (alert.ItemsId, alert.Position is not null, alert.Position ?? 0);
        items = itemList(alert.ItemsId);
        for (var n = 1; n <= 4; n++)
        {
            var (bold, drawn, sound) = alert.Stage(n);
            var stage = new AlertStage(this, n, bold, drawn, sound);
            stage.PropertyChanged += OnStageChanged;
            Stages.Add(stage);
        }

        selectedStage = Stages[0];
        Stages[0].IsSelected = true;
    }

    [ObservableProperty] private decimal top;
    [ObservableProperty] private decimal left;
    [ObservableProperty] private decimal bottom;
    [ObservableProperty] private decimal right;
    [ObservableProperty] private decimal itemsId;
    [ObservableProperty] private bool hasPosition;
    [ObservableProperty] private decimal position;

    public ObservableCollection<AlertStage> Stages { get; } = [];

    /// <summary>The stage selected: its row highlighted and the preview drawn with its default button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StageNote))]
    private AlertStage? selectedStage;

    public PositionChoice? SelectedPosition
    {
        get => HasPosition ? Positions.FirstOrDefault(p => p.Code == (ushort)Position) : null;
        set
        {
            if (value is not null)
            {
                HasPosition = true;
                Position = value.Code;
            }
        }
    }

    public string BoundsText => $"{Pair(Top, Left)}, {Pair(Bottom, Right)}";

    public string SizeText => Size(Top, Left, Bottom, Right);

    public string ItemsText => items?.Name is { } name ? $"'DITL' {Number(ItemsId)} {Quoted(name)}" : $"'DITL' {Number(ItemsId)}";

    public string ItemCountText => items is null ? "not found" : items.Texts.Count == 1 ? "1 item" : $"{items.Texts.Count} items";

    public string PositionName => PositionNameOf(HasPosition, Position);

    public string? PositionCode => PositionCodeOf(HasPosition, Position);

    /// <summary>"Stage 3: default button is Item 2 “Cancel”, plays 2 beeps." (", not drawn" when it is not).</summary>
    public string? StageNote => SelectedStage is { } s
        ? $"Stage {s.Number}: default button is {s.DefaultText}, plays {(s.Sound switch { 0 => "no sound", 1 => "1 beep", var n => $"{n} beeps" })}{(s.Drawn ? "" : ", not drawn")}."
        : null;

    public override string EditHint => "Each stage’s default button is item 1 or 2. Esc cancels, Ctrl+Enter applies.";

    protected override IReadOnlyList<string> DisplayNames { get; } =
    [
        nameof(SelectedStage), nameof(StageNote), nameof(SelectedPosition), nameof(BoundsText), nameof(SizeText), nameof(ItemsText),
        nameof(ItemCountText), nameof(PositionName), nameof(PositionCode),
    ];

    /// <summary>"Item 1 “Save”", from the item list (just "Item 1" without it).</summary>
    internal string ItemLabel(int item) =>
        items is { } list && item >= 1 && item <= list.Texts.Count && list.Texts[item - 1].Length > 0
            ? $"Item {item} {Quoted(list.Texts[item - 1])}"
            : $"Item {item}";

    public override void SelectRow(object? row)
    {
        if (row is AlertStage stage && Stages.Contains(stage))
        {
            SelectedStage = stage;
        }
    }

    partial void OnSelectedStageChanged(AlertStage? oldValue, AlertStage? newValue)
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

    partial void OnItemsIdChanged(decimal value)
    {
        items = itemList((short)value);
        foreach (var stage in Stages)
        {
            stage.Relabel();
        }
    }

    private void OnStageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (StageValues.Contains(e.PropertyName))
        {
            RaiseEdited();
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(StageNote)));
        }
    }

    public override byte[] BuildData()
    {
        var stages = 0;
        foreach (var s in Stages)
        {
            stages |= ((s.BoldItem == 2 ? 8 : 0) | (s.Drawn ? 4 : 0) | (s.Sound & 3)) << ((s.Number - 1) * 4);
        }

        return InterfaceWriter.WriteAlert(new AlertTemplate(Rect(Top, Left, Bottom, Right), (short)ItemsId, (ushort)stages, HasPosition ? (ushort)Position : null));
    }
}

/// <summary><c>'CNTL'</c> as property cards, like a window's.</summary>
public sealed partial class ControlForm : CardForm
{
    /// <summary>The standard control definitions by name (Control Manager, Controls.h).</summary>
    public static IReadOnlyList<DefinitionChoice> StandardDefinitions { get; } =
    [
        new(0, "Push button"), new(1, "Check box"), new(2, "Radio button"), new(8, "Push button, window font"), new(9, "Check box, window font"),
        new(10, "Radio button, window font"), new(16, "Scroll bar"), new(1008, "Pop-up menu"),
    ];

    public ControlForm(Resource resource, ControlTemplate control) : base(resource)
    {
        (top, left, bottom, right) = (control.Bounds.Top, control.Bounds.Left, control.Bounds.Bottom, control.Bounds.Right);
        (value, visible, maximum, minimum, definition, refCon, title) =
            (control.Value, control.Visible, control.Maximum, control.Minimum, control.Definition, control.RefCon, control.Title);
        Definitions = ChoicesWith(StandardDefinitions, control.Definition, "Other CDEF");
    }

    [ObservableProperty] private decimal top;
    [ObservableProperty] private decimal left;
    [ObservableProperty] private decimal bottom;
    [ObservableProperty] private decimal right;
    [ObservableProperty] private decimal value;
    [ObservableProperty] private bool visible;
    [ObservableProperty] private decimal maximum;
    [ObservableProperty] private decimal minimum;
    [ObservableProperty] private decimal definition;
    [ObservableProperty] private decimal refCon;
    [ObservableProperty] private string title;

    public IReadOnlyList<DefinitionChoice> Definitions { get; }

    public DefinitionChoice? SelectedDefinition
    {
        get => Definitions.FirstOrDefault(d => d.Value == Definition);
        set
        {
            if (value is not null)
            {
                Definition = value.Value;
            }
        }
    }

    public string TopLeftText => Pair(Top, Left);

    public string BottomRightText => Pair(Bottom, Right);

    public string SizeText => Size(Top, Left, Bottom, Right) + " pixels";

    public string TitleText => Quoted(Title);

    public string DefinitionName
    {
        get
        {
            var d = (int)Definition;
            return StandardDefinitions.FirstOrDefault(s => s.Value == d)?.Name
                ?? (d is > 1008 and <= 1023 ? $"Pop-up menu, variation {d - 1008}" : $"CDEF {d >> 4}, variation {d & 15}");
        }
    }

    public string DefinitionCode => InterfaceNames.Control((short)Definition) is { } name ? $"{name} · {Number(Definition)}" : Number(Definition);

    public string VisibleText => YesNo(Visible);

    public string ValueText => Number(Value);

    public string MinimumText => Number(Minimum);

    public string MaximumText => Number(Maximum);

    public string RefConText => Number(RefCon);

    protected override IReadOnlyList<string> DisplayNames { get; } =
    [
        nameof(SelectedDefinition), nameof(TopLeftText), nameof(BottomRightText), nameof(SizeText), nameof(TitleText), nameof(DefinitionName),
        nameof(DefinitionCode), nameof(VisibleText), nameof(ValueText), nameof(MinimumText), nameof(MaximumText), nameof(RefConText),
    ];

    public override byte[] BuildData() => InterfaceWriter.WriteControl(new ControlTemplate(Rect(Top, Left, Bottom, Right), (short)Value, Visible,
        (short)Maximum, (short)Minimum, (short)Definition, (int)RefCon, Title));
}
