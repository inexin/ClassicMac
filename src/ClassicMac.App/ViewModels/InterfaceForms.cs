using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Interface;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>A form whose values write the whole resource (the interface templates): its bytes, for the edit and the live preview.</summary>
public abstract partial class DataForm(Resource resource) : ResourceForm(resource)
{
    protected static MacRect Rect(decimal top, decimal left, decimal bottom, decimal right) =>
        new((short)top, (short)left, (short)bottom, (short)right);

    protected static void Move<T>(ObservableCollection<T> list, T item, int by)
    {
        var i = list.IndexOf(item);
        if (i >= 0 && i + by >= 0 && i + by < list.Count)
        {
            list.Move(i, i + by);
        }
    }
}

/// <summary>An item kind a dialog item can be.</summary>
public sealed record ItemKind(int Type, string Name)
{
    /// <summary>The kind's name in the form (boards/dialog-item-list.md): "Static text", "User item".</summary>
    public string Label => Type switch
    {
        0 => "User item",
        1 => "Help item",
        4 => "Button",
        5 => "Check box",
        6 => "Radio button",
        7 => "Control",
        8 => "Static text",
        16 => "Edit text",
        32 => "Icon",
        64 => "Picture",
        _ => string.Create(CultureInfo.InvariantCulture, $"Item type {Type}"),
    };

    public override string ToString() => Label;
}

/// <summary>One item of a <c>'DITL'</c>.</summary>
public sealed partial class DialogItemRow : ObservableObject
{
    public static ItemKind[] Kinds { get; } =
        [.. new[] { 4, 5, 6, 7, 8, 16, 32, 64, 0, 1 }.Select(t => new ItemKind(t, InterfaceNames.Item(t)))];

    public DialogItemRow(DialogItem item)
    {
        kind = Kinds.FirstOrDefault(k => k.Type == item.Type) ?? new ItemKind(item.Type, InterfaceNames.Item(item.Type));
        (top, left, bottom, right) = (item.Bounds.Top, item.Bounds.Left, item.Bounds.Bottom, item.Bounds.Right);
        (topText, leftText, bottomText, rightText) = (Digits(top), Digits(left), Digits(bottom), Digits(right));
        (enabled, text, resourceId, Data) = (item.Enabled, item.Text ?? "", item.ResourceId ?? 0, item.Data);
    }

    public ReadOnlyMemory<byte> Data { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(HasResource), nameof(KindName), nameof(Content), nameof(HasNoContent))]
    private ItemKind kind;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(BoundsText))] private decimal top;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(BoundsText))] private decimal left;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(BoundsText))] private decimal bottom;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(BoundsText))] private decimal right;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(EnabledText), nameof(IsDimmed))] private bool enabled;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Content))] private string text;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Content))] private decimal resourceId;

    // The bounds as typed (the editing table's text boxes): each keystroke that makes a number moves the item; one
    // that does not leaves it and is the form's error until mended.
    [ObservableProperty] private string topText;
    [ObservableProperty] private string leftText;
    [ObservableProperty] private string bottomText;
    [ObservableProperty] private string rightText;

    private static string Digits(decimal value) => ((int)value).ToString(CultureInfo.InvariantCulture);

    partial void OnTopTextChanged(string value) => Top = Parse(value, Top);

    partial void OnLeftTextChanged(string value) => Left = Parse(value, Left);

    partial void OnBottomTextChanged(string value) => Bottom = Parse(value, Bottom);

    partial void OnRightTextChanged(string value) => Right = Parse(value, Right);

    partial void OnTopChanged(decimal value) => TopText = Same(TopText, value);

    partial void OnLeftChanged(decimal value) => LeftText = Same(LeftText, value);

    partial void OnBottomChanged(decimal value) => BottomText = Same(BottomText, value);

    partial void OnRightChanged(decimal value) => RightText = Same(RightText, value);

    private static decimal Parse(string text, decimal current) =>
        short.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : current;

    // The text for a bound: kept as typed when it already says the value ("+3" for 3), else the value.
    private static string Same(string text, decimal value) =>
        short.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var typed) && typed == value ? text : Digits(value);

    /// <summary>Why the typed bounds are not numbers, or null.</summary>
    public string? BoundsError => new[] { (TopText, "top"), (LeftText, "left"), (BottomText, "bottom"), (RightText, "right") }
        .Where(b => !short.TryParse(b.Item1.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
        .Select(b => $"Item {Number}'s {b.Item2} “{b.Item1}” is not a number from −32768 to 32767.")
        .FirstOrDefault();

    /// <summary>Its number in the list, from 1 (item 1 is the default button).</summary>
    [ObservableProperty] private int number;

    /// <summary>Whether it is the selected row (and the item outlined in the preview).</summary>
    [ObservableProperty] private bool isSelected;

    public bool HasText => Kind.Type is 4 or 5 or 6 or 8 or 16;

    public bool HasResource => Kind.Type is 7 or 32 or 64;

    public string KindName => Kind.Label;

    /// <summary>The read-only table's text or resource: “OK”, a resource ID, or "—" for none.</summary>
    public string Content => HasText ? $"“{Text}”" : HasResource ? ((int)ResourceId).ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>The item has neither text nor a resource (its "—" reads muted).</summary>
    public bool HasNoContent => !HasText && !HasResource;

    /// <summary>Top, left, bottom, right.</summary>
    public string BoundsText => string.Create(CultureInfo.InvariantCulture, $"{(int)Top}, {(int)Left}, {(int)Bottom}, {(int)Right}");

    public string EnabledText => Enabled ? "Yes" : "No";

    /// <summary>A disabled item's "No" reads muted.</summary>
    public bool IsDimmed => !Enabled;

    /// <summary>The properties that hold the item's values (the others show them, or the selection).</summary>
    internal static bool IsValue(string? propertyName) =>
        propertyName is nameof(Kind) or nameof(Top) or nameof(Left) or nameof(Bottom) or nameof(Right) or nameof(Enabled) or nameof(Text)
            or nameof(ResourceId) or nameof(TopText) or nameof(LeftText) or nameof(BottomText) or nameof(RightText);

    public DialogItem ToItem() => BoundsError is { } error
        ? throw new ArgumentException(error)
        : new(new MacRect((short)Top, (short)Left, (short)Bottom, (short)Right), Kind.Type, Enabled, HasText ? Text : null,
            HasResource ? (short)ResourceId : null, Data);
}

/// <summary>
/// <c>'DITL'</c> (design/boards/dialog-item-list.md, E3): its items, a read-only table and inputs on the host, and the
/// selection it shares with the preview (the dialog that uses the list, drawn live from the values).
/// </summary>
public sealed partial class DialogItemsForm : DataForm
{
    public DialogItemsForm(Resource resource, IReadOnlyList<DialogItem> items) : base(resource)
    {
        foreach (var item in items)
        {
            Items.Add(Watched(new DialogItemRow(item)));
        }

        Items.CollectionChanged += (_, _) =>
        {
            Renumber();
            OnPropertyChanged(nameof(PreviewIndex));
            RaiseEdited();
        };
        Renumber();
    }

    public ObservableCollection<DialogItemRow> Items { get; } = [];

    /// <summary>The <c>'DLOG'</c> or <c>'ALRT'</c> that uses the list ("'DLOG' 128"), or null; the preview is drawn in it.</summary>
    public string? UsedBy => User is { } user ? $"'{user.Type}' {user.Id}" : null;

    /// <summary>The <c>'DLOG'</c> or <c>'ALRT'</c> in the same file whose item list this is, or null.</summary>
    public Resource? User { get; init; }

    /// <summary>The selected row, outlined in the preview; null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewIndex))]
    private DialogItemRow? selectedItem;

    /// <summary>The preview's outlined item: the selected row's index, or -1; a click in the preview selects its row.</summary>
    public int PreviewIndex
    {
        get => SelectedItem is { } row ? Items.IndexOf(row) : -1;
        set => SelectedItem = value >= 0 && value < Items.Count ? Items[value] : null;
    }

    public override bool HasReadOnlyView => true;

    public override string EditHint => "Item 1 is the default button. Esc cancels, Ctrl+Enter applies.";

    public override void SelectRow(object? row)
    {
        if (row is DialogItemRow item && Items.Contains(item))
        {
            SelectedItem = item;
        }
    }

    protected override bool IsValue(string? propertyName) =>
        propertyName is not (nameof(SelectedItem) or nameof(PreviewIndex));

    partial void OnSelectedItemChanged(DialogItemRow? oldValue, DialogItemRow? newValue)
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

    /// <summary>The first <c>'DLOG'</c>, else <c>'ALRT'</c>, in <paramref name="fork"/> whose item list is <paramref name="id"/>.</summary>
    public static Resource? FindUser(ResourceFork fork, short id, ReadOptions readOptions)
    {
        ArgumentNullException.ThrowIfNull(fork);
        foreach (var (type, dialog) in new[] { ("DLOG", true), ("ALRT", false) })
        {
            foreach (var candidate in fork.OfType(FourCC.FromString(type)).OrderBy(r => r.Id))
            {
                try
                {
                    var data = ResourceDecompression.Default.GetData(candidate, fork, readOptions, []);
                    var items = dialog ? InterfaceResources.ReadWindow(data, true, DecodeOptions.Default, [], "").ItemsId
                        : InterfaceResources.ReadAlert(data, [], "").ItemsId;
                    if (items == id)
                    {
                        return candidate;
                    }
                }
                catch (Exception e) when (ExceptionFilters.IsMalformedOrOutOfRange(e))
                {
                    // Unreadable: not the user.
                }
            }
        }

        return null;
    }

    [RelayCommand]
    private void Add()
    {
        var row = Watched(new DialogItemRow(new DialogItem(new MacRect(10, 10, 30, 90), 8, false, "Text", null, ReadOnlyMemory<byte>.Empty)));
        Items.Add(row);
        SelectedItem = row;
    }

    [RelayCommand]
    private void Remove(DialogItemRow row)
    {
        if (ReferenceEquals(row, SelectedItem))
        {
            SelectedItem = null;
        }

        Items.Remove(row);
    }

    [RelayCommand]
    private void MoveUp(DialogItemRow row) => Move(Items, row, -1);

    // A row's value changes are the form's edits; its number and selection are not.
    private DialogItemRow Watched(DialogItemRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (DialogItemRow.IsValue(e.PropertyName))
            {
                RaiseEdited();
            }
        };
        return row;
    }

    private void Renumber()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].Number = i + 1;
        }
    }

    public override byte[] BuildData() => InterfaceWriter.WriteDialogItems(Items.Select(i => i.ToItem()).ToList());
}

public sealed partial class FormLivePreview(MainViewModel main) : ObservableObject
{
    /// <summary>The live preview of the form's dialog, alert or item list, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHostDialog))]
    private DialogPreview? formDialog;

    /// <summary>Under an item list's preview: "Drawn from 'DLOG' 128 · 300 × 106", or "Drawn on its own · …"; null for others.</summary>
    [ObservableProperty]
    private string? formDialogNote;

    /// <summary>Whether the host shows the form's dialog beside it (an item list's form has a preview panel of its own).</summary>
    public bool ShowsHostDialog => FormDialog is not null && main.Forms.Form is not DialogItemsForm;

    /// <summary>Why the form's values cannot be written, or null (the host's error line; Apply waits for it to go).</summary>
    [ObservableProperty]
    private string? formError;

    partial void OnFormErrorChanged(string? value) => main.Forms.ApplyFormCommand.NotifyCanExecuteChanged();

    // The form's error follows its values; a dialog, alert or item list also gets a live preview of the dialog.
    internal void WatchForm(ResourceForm? form, ResourceNode? node)
    {
        FormDialog = null;
        FormDialogNote = null;
        FormError = null;
        OnPropertyChanged(nameof(ShowsHostDialog));
        if (form is null || node is null)
        {
            return;
        }

        DialogSources? sources = null;
        void Update()
        {
            try
            {
                var bytes = form.BuildData();
                FormError = null;
                if (form is DialogItemsForm items)
                {
                    // The item list in the window of the dialog that uses it, redrawn from the values on each change.
                    FormDialog = InterfacePreviews.ItemList(node.Resource, bytes, items.User, node.Fork, DecodeOptions.Default with { ScreenDepth = main.ScreenDepth },
                        main.ReadOptions, [], sources ??= DialogSources.From(main.Roots));
                    FormDialogNote = FormDialog is not { } drawn ? null
                        : string.Create(CultureInfo.InvariantCulture,
                            $"{(items.UsedBy is { } user ? "Drawn from " + user : "Drawn on its own")} · {drawn.Drawing.Width} × {drawn.Drawing.Height}");
                }
                else if (form is DataForm and not MenuForm)
                {
                    // An alert draws the selected stage's default button (E4).
                    FormDialog = InterfacePreviews.Dialog(node.Resource, bytes, node.Fork, DecodeOptions.Default with { ScreenDepth = main.ScreenDepth }, main.ReadOptions, [],
                        sources ??= DialogSources.From(main.Roots), (form as AlertForm)?.SelectedStage?.BoldItem);
                }
            }
            catch (Exception e) when (e is ArgumentException or OverflowException)
            {
                FormError = e.Message;
            }
        }
        form.Edited += (_, _) => Update();
        if (form is AlertForm alert)
        {
            alert.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AlertForm.SelectedStage))
                {
                    Update();
                }
            };
        }

        Update();
    }
}
