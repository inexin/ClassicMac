using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>One fact in the inspector's header: a label and its value, the value in mono for codes.</summary>
    public sealed record InspectorFact(string Label, string Value, bool IsMono);

    /// <summary>
    /// The inspector's header for a node (design/boards/main-window.md, S3): its name, its kind and owner ("String list
    /// in Prefs") and a row of facts.
    /// </summary>
    public sealed record InspectorHeader(NodeViewModel Node, string Name, string Kind, IReadOnlyList<InspectorFact> Facts)
    {
        private static readonly HashSet<string> ApplicationTypes = ["APPL", "APPC", "APPD", "appe"];

        // What resource types are called (singular, plural).
        private static readonly Dictionary<string, (string One, string Many)> TypeNames = new()
        {
            ["STR "] = ("String", "Strings"), ["STR#"] = ("String list", "String lists"), ["TEXT"] = ("Text", "Texts"),
            ["styl"] = ("Text style", "Text styles"), ["PICT"] = ("Picture", "Pictures"), ["ICON"] = ("Icon", "Icons"),
            ["ICN#"] = ("Icon list", "Icon lists"), ["icl4"] = ("4-bit icon", "4-bit icons"), ["icl8"] = ("8-bit icon", "8-bit icons"),
            ["ics#"] = ("Small icon list", "Small icon lists"), ["ics4"] = ("Small 4-bit icon", "Small 4-bit icons"),
            ["ics8"] = ("Small 8-bit icon", "Small 8-bit icons"), ["icns"] = ("Icon family", "Icon families"),
            ["cicn"] = ("Colour icon", "Colour icons"), ["SICN"] = ("Small icons", "Small icons"), ["CURS"] = ("Cursor", "Cursors"),
            ["crsr"] = ("Colour cursor", "Colour cursors"), ["snd "] = ("Sound", "Sounds"), ["MENU"] = ("Menu", "Menus"),
            ["DLOG"] = ("Dialog", "Dialogs"), ["DITL"] = ("Dialog item list", "Dialog item lists"), ["ALRT"] = ("Alert", "Alerts"),
            ["WIND"] = ("Window", "Windows"), ["CNTL"] = ("Control", "Controls"), ["vers"] = ("Version", "Versions"),
            ["PAT "] = ("Pattern", "Patterns"), ["PAT#"] = ("Pattern list", "Pattern lists"), ["ppat"] = ("Pixel pattern", "Pixel patterns"),
            ["FOND"] = ("Font family", "Font families"), ["NFNT"] = ("Bitmap font", "Bitmap fonts"), ["FONT"] = ("Font", "Fonts"),
            ["sfnt"] = ("TrueType font", "TrueType fonts"), ["CODE"] = ("Code segment", "Code segments"), ["TMPL"] = ("Template", "Templates"),
            ["BNDL"] = ("Bundle", "Bundles"), ["FREF"] = ("File reference", "File references"), ["clut"] = ("Colour table", "Colour tables"),
        };

        /// <summary>The header for <paramref name="node"/>; null for none and the loading placeholder.</summary>
        public static InspectorHeader? For(NodeViewModel? node) => node switch
        {
            ResourceNode resource => Resource(resource),
            ResourceTypeNode type => Type(type),
            FileNode file => new InspectorHeader(file, file.Name, $"{(ApplicationTypes.Contains(file.File.FinderInfo.Type.ToString()) ? "Application" : "Document")} in {OwnerName(file)}",
                [TypeCreator(file.File), Size(file.File.DataFork.Length + file.File.ResourceFork.Length), Resources(file)]),
            ContainerFileNode container => new InspectorHeader(container, container.File.Name.ToMacRoman(), $"{container.ContentFormat} in {OwnerName(container)}",
                [TypeCreator(container.File), Size(container.File.DataFork.Length + container.File.ResourceFork.Length)]),
            FolderNode folder => new InspectorHeader(folder, folder.Name, $"Folder in {OwnerName(folder)}", [Items(folder)]),
            NoNameGroupNode group => new InspectorHeader(group, group.Name, $"Files with no name in {OwnerName(group)}",
                [new("Items", group.Children.Count.ToString(CultureInfo.InvariantCulture), false)]),
            InputNode input => new InspectorHeader(input, input.Name, input.Root.Children.Count > 0 ? input.Root.Children[0].Format : "Resource fork",
                [new("Files", input.Root.Leaves().Count().ToString("N0", CultureInfo.InvariantCulture), false), new("Size", NodeViewModel.FormatSize(NodeViewModel.HostSize(input)), false)]),
            _ => null,
        };

        private static InspectorHeader Resource(ResourceNode node)
        {
            var resource = node.Resource;
            var type = resource.Type.ToString();
            var kind = TypeNames.TryGetValue(type, out var names) ? names.One : $"'{type}' resource";
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

            facts.Add(Bytes(resource.Length));
            facts.Add(new("Attributes", resource.Attributes == ResourceAttributes.None ? "none" : resource.Attributes.ToString(), false));
            return new InspectorHeader(node, node.Name, $"{kind} in {OwnerName(node)}", facts);
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
            catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.EndOfStreamException or ArgumentException)
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
            catch (Exception e) when (e is System.IO.InvalidDataException or System.IO.EndOfStreamException or ArgumentException)
            {
                return null;
            }
        }

        private static InspectorHeader Type(ResourceTypeNode node)
        {
            var type = node.Type.ToString();
            var resources = node.Fork.OfType(node.Type).ToList();
            var kind = TypeNames.TryGetValue(type, out var names) ? names.Many : $"'{type}' resources";
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
    public sealed partial class MainViewModel
    {
        /// <summary>The selection's header; null with nothing selected.</summary>
        public InspectorHeader? Header => InspectorHeader.For(Selected);

        /// <summary>What the header's Export… does for the selection: save a resource, export a file's or type's resources, or extract all.</summary>
        public IRelayCommand HeaderExportCommand => Selected switch
        {
            ResourceNode => SaveResourceAsCommand,
            FileNode or ResourceTypeNode or InputNode { Root.Children.Count: 0 } => ExportResourcesCommand,
            _ => ExtractAllCommand,
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

            var png = await Task.Run(() => NodeViewModel.LargeIcon(node));
            if (ReferenceEquals(Selected, node))
            {
                HeaderIconPng = png;
            }
        }

        // The header, its Export… and editing follow the selection.
        private void OnSelectionChangedForInspector()
        {
            IsEditingForm = false;
            LastApplied = null;
            HeaderIconPng = null;
            HeaderIconTask = LoadHeaderIconAsync(Selected);
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(HeaderExportCommand));
            EditFormCommand.NotifyCanExecuteChanged();
        }
    }
}
