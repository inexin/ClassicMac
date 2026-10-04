using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Finder;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>One fact in the inspector's header: a label and its value, the value in mono for codes.</summary>
public sealed record InspectorFact(string Label, string Value, bool IsMono);

/// <summary>
/// The inspector's header for a node (design/boards/main-window.md, S3): its name, its kind and owner ("String list
/// in Prefs") and a row of facts.
/// </summary>
public sealed record InspectorHeader(NodeViewModel Node, string Name, string Kind, IReadOnlyList<InspectorFact> Facts)
{
    /// <summary>For an alias file, where its original is ("Mac OS 9: System Folder: Note Pad"); null otherwise.</summary>
    public string? Original { get; init; }

    public bool HasOriginal => Original is not null;

    /// <summary>
    /// An alias's kind line: "Alias to Note Pad · application program"; when not found, "· original missing", "· on
    /// another disk" or "· on a network volume".
    /// </summary>
    internal static string AliasKind(AliasLink link) => $"Alias to {link.TargetName} · " + link.Target switch
    {
        FileNode or ContainerFileNode => FileKinds.Of(link.Target).Text,
        FolderNode => "folder",
        InputNode or null when link.Resolution.Found => "disk",
        _ => link.Resolution.State switch
        {
            AliasState.Missing => "original missing",
            AliasState.Network => "on a network volume",
            _ => "on another disk",
        },
    };

    /// <summary>What a resource of <paramref name="type"/> is called ("Text style"), else the type in quotes.</summary>
    internal static string TypeName(string type) => KnownKinds.ResourceType(FourCC.FromString(type)) is { } names ? names.One : $"'{type}'";

    /// <summary>The header for <paramref name="node"/>; <paramref name="draftSize"/>, while a form is edited, is its resource's Size.</summary>
    public static InspectorHeader? For(NodeViewModel? node, long? draftSize = null) => node switch
    {
        ResourceNode resource => Resource(resource, draftSize),
        ResourceTypeNode type => Type(type),
        FileNode file => new InspectorHeader(file, file.Name, $"{FileKinds.Capitalized(FileKinds.Of(file).Text)} in {OwnerName(file)}",
            [TypeCreator(file.File), Size(file.File.DataFork.Length + file.File.ResourceFork.Length), Resources(file)]),
        ContainerFileNode container => new InspectorHeader(container, container.File.Name.ToMacRoman(), $"{container.ContentFormat} in {OwnerName(container)}",
            [TypeCreator(container.File), Size(container.File.DataFork.Length + container.File.ResourceFork.Length)]),
        FolderNode folder => new InspectorHeader(folder, folder.Name, $"Folder in {OwnerName(folder)}", [Items(folder)]),
        NoNameGroupNode group => new InspectorHeader(group, group.Name, $"Files with no name in {OwnerName(group)}",
            [new("Items", group.Children.Count.ToString(CultureInfo.InvariantCulture), false)]),
        InputNode input => new InspectorHeader(input, input.Name, input.Root.Children.Count > 0 ? input.Root.Children[0].Format : "Resource fork",
            [new("Files", input.Root.Leaves().Count().ToString("N0", CultureInfo.InvariantCulture), false), new("Size", NodeFormat.FormatSize(NodeFormat.HostSize(input)), false)]),
        _ => null,
    };

    private static InspectorHeader Resource(ResourceNode node, long? draftSize)
    {
        var resource = node.Resource;
        var type = resource.Type.ToString();
        var kind = KnownKinds.ResourceType(resource.Type) is { } names ? names.One : $"'{type}' resource";
        if (type == "FOND" && FamilyFacts(node) is { } family)
        {
            return new InspectorHeader(node, node.Name, $"{kind} in {OwnerName(node)}", family);
        }

        var facts = new List<InspectorFact>
        {
            new("Type", $"'{type}'", true),
            new("ID", resource.Id.ToString(CultureInfo.InvariantCulture), false),
        };
        if (Members(node) is { } members)
        {
            facts.Add(new("Members", members, true));
        }

        if (type == "DITL")
        {
            facts.AddRange(ItemListFacts(node));
        }

        facts.Add(Bytes(draftSize ?? resource.Length));
        facts.Add(new("Attributes", resource.Attributes == ResourceAttributes.None ? "none" : resource.Attributes.ToString(), false));
        return new InspectorHeader(node, node.Name, $"{kind} in {OwnerName(node)}", facts);
    }

    // A font family's facts (boards/font-family.md): its ID, version, strikes and whether it is fixed width; null when
    // it cannot be read.
    private static List<InspectorFact>? FamilyFacts(ResourceNode node)
    {
        ClassicMac.Graphics.Fonts.FontFamily family;
        try
        {
            var data = ResourceDecompression.Default.GetData(node.Resource, node.Fork, node.Input.Options, []);
            family = ClassicMac.Graphics.Fonts.FontFamily.Read(data, "");
        }
        catch (Exception e) when (ExceptionFilters.IsMalformedOrOutOfRange(e))
        {
            return null;
        }

        var strikes = new List<string> { $"{family.Fonts.Count(f => f.Size > 0)} bitmap" };
        if (family.Fonts.Count(f => f.Size == 0) is > 0 and var trueType)
        {
            strikes.Add($"{trueType} TrueType");
        }

        if (family.Fonts.Count(f => f.Size < 0) is > 0 and var type1)
        {
            strikes.Add($"{type1} Type 1");
        }

        return
        [
            new("Type", "'FOND'", true),
            new("Family ID", family.FamilyId.ToString(CultureInfo.InvariantCulture), false),
            new("Version", family.Version.ToString(CultureInfo.InvariantCulture), false),
            new("Strikes", string.Join(", ", strikes), false),
            new("Fixed width", (family.Flags & 0x8000) != 0 ? "Yes" : "No", false),
        ];
    }

    // An item list's items, and the dialog or alert that uses it (boards/dialog-item-list.md).
    private static IEnumerable<InspectorFact> ItemListFacts(ResourceNode node)
    {
        IReadOnlyList<ClassicMac.Resources.Decoders.Interface.DialogItem> items;
        try
        {
            var data = ResourceDecompression.Default.GetData(node.Resource, node.Fork, node.Input.Options, []);
            items = ClassicMac.Resources.Decoders.Interface.InterfaceResources.ReadDialogItems(data, ClassicMac.Resources.Decoders.DecodeOptions.Default, [], "");
        }
        catch (Exception e) when (ExceptionFilters.IsMalformedOrOutOfRange(e))
        {
            yield break;
        }

        yield return new("Items", items.Count.ToString(CultureInfo.InvariantCulture), false);
        if (DialogItemsForm.FindUser(node.Fork, node.Resource.Id, node.Input.Options) is { } user)
        {
            yield return new("Used by", $"'{user.Type}' {user.Id}", true);
        }
    }

    private static readonly string[] SuiteTypes = ["ICN#", "icl4", "icl8", "ics#", "ics4", "ics8", "icm#", "icm4", "icm8"];

    // An icon family's members: the suite types with the resource's ID, or an icns's member types; null for others.
    private static string? Members(ResourceNode node)
    {
        var resource = node.Resource;
        var type = resource.Type.ToString();
        if (SuiteTypes.Contains(type))
        {
            return string.Join(" · ", SuiteTypes.Where(t => node.Fork.Find(FourCC.FromString(t), resource.Id) is not null));
        }

        if (type != "icns")
        {
            return null;
        }

        try
        {
            var data = ResourceDecompression.Default.GetData(resource, node.Fork, node.Input.Options, []);
            return string.Join(" · ", ClassicMac.Resources.Decoders.Images.IconFamily.ReadIcns(data, []).Members.Select(m => m.Key));
        }
        catch (Exception e) when (ExceptionFilters.IsMalformedOrOutOfRange(e))
        {
            return null;
        }
    }

    private static InspectorHeader Type(ResourceTypeNode node)
    {
        var type = node.Type.ToString();
        var resources = node.Fork.OfType(node.Type).ToList();
        var kind = KnownKinds.ResourceType(FourCC.FromString(type)) is { } names ? names.Many : $"'{type}' resources";
        return new InspectorHeader(node, $"'{type}'", $"{kind} in {OwnerName(node)}",
        [
            new("Type", $"'{type}'", true),
            new("Resources", resources.Count.ToString(CultureInfo.InvariantCulture), false),
            Bytes(resources.Sum(r => (long)r.Length)),
        ]);
    }

    private static InspectorFact TypeCreator(ClassicMac.Files.MacFile file) =>
        new("Type / creator", $"{file.FinderInfo.Type} · {file.FinderInfo.Creator}", true);

    private static InspectorFact Size(long bytes) => new("Total size", bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes", false);

    private static InspectorFact Bytes(long bytes) => new("Size", bytes.ToString("N0", CultureInfo.InvariantCulture) + (bytes == 1 ? " byte" : " bytes"), false);

    private static InspectorFact Items(FolderNode folder) =>
        new("Items", (folder.Items?.Count ?? folder.Children.Count).ToString(CultureInfo.InvariantCulture), false);

    // A file's resources: their count once read, "none" when it has no fork, else "not read".
    private static InspectorFact Resources(FileNode file) => new("Resources", file.Resources is { } found
        ? (found.Fork?.Resources.Count ?? 0).ToString(CultureInfo.InvariantCulture)
        : file.Children.Count == 0 ? "none" : "not read", false);

    // The file, folder or input a node is in, by name.
    private static string OwnerName(NodeViewModel node)
    {
        var at = node.Parent;
        while (at is ResourceTypeNode or NoNameGroupNode)
        {
            at = at.Parent;
        }

        return at switch
        {
            FileNode file => file.File.Name.ToMacRoman(),
            ContainerFileNode container => container.File.Name.ToMacRoman(),
            InputNode input => input.BaseTitle,
            null => "",
            _ => at.Name,
        };
    }
}

// The inspector's header and the editing of forms in place (the Edit tab is gone: a form opens from the header's
// Edit button, in the Preview tab, until Apply or Cancel).
public sealed partial class InspectorActions(IAppSelection appSelection, IAppParts appParts) : ObservableObject
{
    /// <summary>The selection's header; null with nothing selected.</summary>
    public InspectorHeader? Header
    {
        get
        {
            var header = InspectorHeader.For(appSelection.Selected, appParts.FormEditing.IsEditingForm ? appParts.Forms.Form?.DraftLength : null);
            if (header is not null && appParts.AliasActions.SelectedAlias is { } alias && ReferenceEquals(alias.Alias, appSelection.Selected))
            {
                header = header with { Kind = InspectorHeader.AliasKind(alias), Original = alias.Path };
            }

            // A resource shown through a template says which (boards/template-form.md).
            return header is not null && appParts.Forms.Form is TemplateForm { ShownThrough: { } through }
                ? header with { Facts = [.. header.Facts, new InspectorFact("Shown through", through, false)] }
                : header;
        }
    }

    /// <summary>What the header's Export… does for the selection: save a resource, export a file's or type's resources, or extract all.</summary>
    public IRelayCommand HeaderExportCommand => appSelection.Selected switch
    {
        ResourceNode => appParts.ExportActions.SaveResourceAsCommand,
        FileNode or ResourceTypeNode or InputNode { Root.Children.Count: 0 } => appParts.ExportActions.ExportResourcesCommand,
        _ => appParts.ExportActions.ExtractAllCommand,
    };

    /// <summary>The selection's large icon for the header's tile (PNG), once loaded; null while loading or when it has none.</summary>
    [ObservableProperty]
    private byte[]? headerIconPng;

    /// <summary>The header icon's loading (tests wait for it).</summary>
    internal Task HeaderIconTask { get; private set; } = Task.CompletedTask;

    // Loads the selection's large icon off the UI thread; a newer selection wins.
    private async Task LoadHeaderIconAsync(NodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        var png = await Task.Run(() => NodeImages.LargeIcon(node));
        if (ReferenceEquals(appSelection.Selected, node))
        {
            HeaderIconPng = png;
        }
    }

    // The header changed for another reason than the selection (an edit, a draft, the type and creator database).
    internal void NotifyHeader() => OnPropertyChanged(nameof(Header));

    // The header, its Export… and editing follow the selection.
    internal void OnSelectionChangedForInspector()
    {
        appParts.FormEditing.IsEditingForm = false;
        appParts.FormEditing.LastApplied = null;
        HeaderIconPng = null;
        HeaderIconTask = LoadHeaderIconAsync(appSelection.Selected);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HeaderExportCommand));
        appParts.SoundHeaderActions.OnSelectionChangedForSoundHeader();
        appParts.FormEditing.EditFormCommand.NotifyCanExecuteChanged();
    }
}
