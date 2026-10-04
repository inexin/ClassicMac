using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

// The property view's Properties | JSON switch (design/boards/property-view.md, P2): Properties by default, the choice
// kept for the session.
public sealed partial class PropertyLinks(IAppSelection appSelection, IAppServices appServices, IAppView appView) : ObservableObject
{
    /// <summary>Whether a JSON preview shows its JSON rather than its properties.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProperties), nameof(ShowsJsonText), nameof(PropertyModeIndex), nameof(ShowsFontFamily), nameof(ShowsFontJson))]
    private bool showJson;

    /// <summary>The switch's segments, in <see cref="PropertyModeIndex"/> order.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> PropertyModes { get; } = ["Properties", "JSON"];

    /// <summary>The switch: 0 Properties, 1 JSON.</summary>
    public int PropertyModeIndex
    {
        get => ShowJson ? 1 : 0;
        set => ShowJson = value == 1;
    }

    /// <summary>Whether the preview shows property cards.</summary>
    public bool ShowsProperties => appView.Preview.IsJson && !ShowJson && appView.Preview.PropertyCards.Count > 0;

    /// <summary>Whether the preview shows the JSON text: on request, or when it has no properties to show.</summary>
    public bool ShowsJsonText => appView.Preview.IsJson && (ShowJson || appView.Preview.PropertyCards.Count == 0);

    /// <summary>Whether a font family shows its sample and tables (P6).</summary>
    public bool ShowsFontFamily => appView.Preview.IsFontFamily && !ShowJson;

    /// <summary>Whether a font family shows its JSON.</summary>
    public bool ShowsFontJson => appView.Preview.IsFontFamily && ShowJson;

    /// <summary>
    /// A font family's resource link (a matrix cell or the sample's source): selects that resource of the family's
    /// file in the tree.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSelectFontResource))]
    private void SelectFontResource(object? target)
    {
        if (Link(target) is not { } link || appSelection.Selected is not ResourceNode { Parent.Parent: { } owner })
        {
            return;
        }

        if (owner.Children.OfType<ResourceTypeNode>().FirstOrDefault(t => t.Type.ToString() == link.Type)?
            .Children.OfType<ResourceNode>().FirstOrDefault(r => r.Resource.Id == link.Id) is { } node)
        {
            appSelection.Selected = node;
        }
    }

    private static bool CanSelectFontResource(object? target) => Link(target) is not null;

    private static FontResourceLink? Link(object? target) => target switch
    {
        FontAssociationCell cell => cell.Link,
        FontResourceLink link => link,
        _ => null,
    };

    /// <summary>A row's right-click Copy as decimal, hex or JSON: <paramref name="text"/> on the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyProperty))]
    private Task CopyProperty(string? text) => text is null ? Task.CompletedTask : appServices.Shell?.CopyTextAsync(text) ?? Task.CompletedTask;

    private static bool CanCopyProperty(string? text) => text is not null;

    internal void OnPropertyPreviewChanged()
    {
        OnPropertyChanged(nameof(ShowsProperties));
        OnPropertyChanged(nameof(ShowsJsonText));
        OnPropertyChanged(nameof(ShowsFontFamily));
        OnPropertyChanged(nameof(ShowsFontJson));
    }
}
