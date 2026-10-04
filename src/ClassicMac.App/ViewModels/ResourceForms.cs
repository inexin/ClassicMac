using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Interface;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>A typed editor for one resource: its fields, and the edit that writes them back.</summary>
public abstract partial class ResourceForm : ObservableObject
{
    protected ResourceForm(Resource resource) => Resource = resource;

    public Resource Resource { get; }

    // ---- The read-then-edit host's side of a form (see FormEditing.cs) ----

    /// <summary>Whether the host is editing the form (inputs) or showing it read only. The host sets it.</summary>
    public bool IsEditing
    {
        get => isEditing;
        internal set
        {
            // Not an edit of the values: no Edited, so the draft and the preview are left alone.
            if (isEditing != value)
            {
                isEditing = value;
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsEditing)));
            }
        }
    }

    private bool isEditing;

    /// <summary>
    /// Whether the form has a read-only view of its own (its template shows values when <see cref="IsEditing"/> is
    /// false); without one the host shows the resource's plain preview until Edit.
    /// </summary>
    public virtual bool HasReadOnlyView => false;

    /// <summary>The hint in the host's footer while editing.</summary>
    public virtual string EditHint => "Esc cancels, Ctrl+Enter applies.";

    /// <summary>Selects the row a double-click started editing on (a no-op for forms without rows).</summary>
    public virtual void SelectRow(object? row)
    {
    }

    /// <summary>The resource's bytes for the form's values (throws <see cref="ArgumentException"/> for values it cannot hold).</summary>
    public abstract byte[] BuildData();

    /// <summary>
    /// Raised whenever a value changes, in the form or in its lists' items (for the live preview, and for Save and the
    /// draft guard, which compare the bytes).
    /// </summary>
    public event EventHandler? Edited;

    protected void RaiseEdited() => Edited?.Invoke(this, EventArgs.Empty);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (IsValue(e.PropertyName))
        {
            RaiseEdited();
        }
    }

    /// <summary>Whether a property holds one of the resource's values (an edit), not the view's state (a selection, a preview).</summary>
    protected virtual bool IsValue(string? propertyName) => true;

    // A list whose items' and own changes count as edits.
    // With isValue, only the items' value properties count (not a row's number or selection).
    protected void Watch<T>(ObservableCollection<T> list, Func<string?, bool>? isValue = null) where T : System.ComponentModel.INotifyPropertyChanged
    {
        void OnItem(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (isValue?.Invoke(e.PropertyName) != false)
            {
                RaiseEdited();
            }
        }

        foreach (var item in list)
        {
            item.PropertyChanged += OnItem;
        }

        list.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<T>() ?? [])
            {
                item.PropertyChanged += OnItem;
            }

            RaiseEdited();
        };
    }

    /// <summary>The edit that stores the form's values (throws <see cref="ArgumentException"/> for values the resource cannot hold).</summary>
    public virtual IResourceEdit BuildEdit(ResourceFork fork) => new SetResourceData(Resource, BuildData(), $"Edit {Resource}");

    private (byte[]? Data, string? Error) clean;

    /// <summary>Takes the values as they are as the unedited ones (when the form is made, and once they are applied).</summary>
    internal void MarkClean() => clean = Snapshot();

    /// <summary>
    /// Whether the values differ from the unedited ones (an unapplied draft), and why they cannot be written when so.
    /// Values changed back are no draft.
    /// </summary>
    internal (bool IsDraft, string? Error) Draft
    {
        get
        {
            var now = Snapshot();
            var isDraft = now.Data is { } data ? clean.Data is not { } old || !data.AsSpan().SequenceEqual(old) : now.Error != clean.Error;
            return (isDraft, now.Error);
        }
    }

    /// <summary>The length of the bytes the values make now; null while they have an error.</summary>
    internal int? DraftLength => Snapshot().Data?.Length;

    private (byte[]? Data, string? Error) Snapshot()
    {
        try
        {
            return (BuildData(), null);
        }
        catch (Exception e) when (e is ArgumentException or OverflowException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>A form for the resource's type, or null when there is none.</summary>
    public static ResourceForm? For(Resource resource, ResourceFork fork, ReadOptions readOptions)
    {
        var data = ResourceDecompression.Default.GetData(resource, fork, readOptions, []);
        return resource.Type.ToString() switch
        {
            "STR " => new StringForm(resource, TextResources.ReadString(data.Span)),
            "STR#" => new StringListForm(resource, TextResources.ReadStringList(data)),
            "TEXT" => new TextForm(resource, data.ToArray(), fork.Find(FourCC.FromString("styl"), resource.Id) is { } styl
                ? (styl, ResourceDecompression.Default.GetData(styl, fork, readOptions, []).ToArray())
                : null),
            "vers" when VersionResource.Read(data) is { } version => new VersionForm(resource, version),
            "WIND" => new WindowForm(resource, InterfaceResources.ReadWindow(data, false, DecodeOptions.Default, [], ""), false),
            "DLOG" => new WindowForm(resource, InterfaceResources.ReadWindow(data, true, DecodeOptions.Default, [], ""), true),
            "ALRT" => new AlertForm(resource, InterfaceResources.ReadAlert(data, [], ""), id => ItemListOf(fork, id, readOptions)),
            "DITL" => new DialogItemsForm(resource, InterfaceResources.ReadDialogItems(data, DecodeOptions.Default, [], ""))
            {
                User = DialogItemsForm.FindUser(fork, resource.Id, readOptions),
            },
            "MENU" => new MenuForm(resource, InterfaceResources.ReadMenu(data, DecodeOptions.Default, [], "")),
            "CNTL" => new ControlForm(resource, InterfaceResources.ReadControl(data, DecodeOptions.Default, [], "")),
            _ => null,
        };
    }

    // An alert's item list from its fork: the DITL's name and its items' texts; null when it is missing or unreadable.
    private static ItemList? ItemListOf(ResourceFork fork, short id, ReadOptions readOptions)
    {
        if (fork.Find(FourCC.FromString("DITL"), id) is not { } ditl)
        {
            return null;
        }

        try
        {
            var data = ResourceDecompression.Default.GetData(ditl, fork, readOptions, []);
            var items = InterfaceResources.ReadDialogItems(data, DecodeOptions.Default, [], "");
            return new ItemList(ditl.Name?.ToMacRoman(), [.. items.Select(i => i.Text ?? "")]);
        }
        catch (Exception e) when (ExceptionFilters.IsMalformedOrOutOfRange(e))
        {
            return null;
        }
    }
}
