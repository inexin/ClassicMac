using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A new file's name, type and creator, as the New File dialog shows them.</summary>
    public sealed record NewFileChoice(string Name, string Type, string Creator);

    // The Volume menu: files and folders created and deleted in a plain HFS image through HfsWriter. Each change is made
    // at once on an in-memory copy of the volume (so a refused one says why straight away) and shown in the tree; the
    // image on disk is not touched, and the changes are written only by Save As ▸ HFS Volume Image, with the fork edits.
    public sealed partial class MainViewModel
    {
        // The folder node a new item goes into (the selected folder, the input's root, or the folder of the selected
        // file), when it is in a plain HFS image and not inside a container held in it.
        private static NodeViewModel? VolumeFolder(NodeViewModel? node)
        {
            if (node is ResourceTypeNode or ResourceNode or LoadingNode) return null;
            for (var at = node is ContainerFileNode ? node.Parent : node; at is not null; at = at.Parent)
            {
                if (at is FolderNode or InputNode) return at.Input.IsWritableHfs ? at : null;
                if (at is ContainerFileNode) return null;
            }
            return null;
        }

        // The selected file or folder, when it can be deleted from a plain HFS image.
        private static NodeViewModel? VolumeItem(NodeViewModel? node) =>
            node is FileNode or ContainerFileNode or FolderNode && Tree.FolderOf(node) is { } parent && VolumeFolder(parent) == parent ? node : null;

        // A folder node's Mac path below the volume's root ("" for the root).
        private static List<string> FolderNames(NodeViewModel folder)
        {
            var names = new List<string>();
            for (var at = folder; at is FolderNode; at = at.Parent!) names.Insert(0, at.BaseTitle);
            return names;
        }

        private static string ItemName(NodeViewModel item) => item switch
        {
            FileNode f => f.File.Name.ToMacRoman(),
            ContainerFileNode c => c.File.Name.ToMacRoman(),
            _ => item.BaseTitle,
        };

        private static string MacPathOf(NodeViewModel item) => string.Join(":", FolderNames(Tree.FolderOf(item)!).Append(ItemName(item)));

        private bool CanCreateInVolume() => !IsExporting && VolumeFolder(Selected) is not null;

        private bool CanDeleteFromVolume() => !IsExporting && VolumeItem(Selected) is not null;

        [RelayCommand(CanExecute = nameof(CanCreateInVolume))]
        private async Task NewFile()
        {
            if (VolumeFolder(Selected) is not { } folder || EditDialogs is null) return;
            if (await EditDialogs.NewFileAsync("New File", new NewFileChoice("untitled", "TEXT", "ttxt")) is not { } choice) return;
            if (FinderInfoFor(choice, FinderInfo.Empty) is not { } finder) return;
            AddFile(folder, new MacFile { Name = MacString.FromMacRoman(choice.Name), FinderInfo = finder });
        }

        /// <summary>
        /// Import File: a host file (its data fork, and its resource fork and Finder info when it has an AppleDouble or
        /// Basilisk II companion or is MacBinary or AppleSingle) made into a new file in the selected folder.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanCreateInVolume))]
        private async Task ImportFile()
        {
            if (VolumeFolder(Selected) is not { } folder || EditDialogs is null || FilePicker is null) return;
            if ((await FilePicker.PickFilesAsync()).FirstOrDefault() is not { } path) return;
            MacFile imported;
            try
            {
                imported = await Task.Run(() => ReadHostFile(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Status = $"{Path.GetFileName(path)} could not be read: {e.Message}";
                return;
            }
            var name = imported.Name.ToMacRoman();
            if (name.Length > 31) name = name[..31];
            var initial = new NewFileChoice(name, imported.FinderInfo.Type.ToString(), imported.FinderInfo.Creator.ToString());
            if (await EditDialogs.NewFileAsync($"Import “{Path.GetFileName(path)}”", initial) is not { } choice) return;
            if (FinderInfoFor(choice, imported.FinderInfo) is not { } finder) return;
            AddFile(folder, imported with { Name = MacString.FromMacRoman(choice.Name), FinderInfo = finder });
        }

        // A host file as a Mac file: with its companions, and unwrapped when it is a MacBinary or AppleSingle file.
        private MacFile ReadHostFile(string path)
        {
            var host = HostFiles.Read(path, ContainerOptions);
            if (host.Layout != HostLayout.Plain) return host.File;
            IContainerReader[] wrappers = [MacBinaryReader.III, MacBinaryReader.II, MacBinaryReader.I, AppleSingleReader.AppleSingle];
            if (wrappers.FirstOrDefault(r => r.CanRead(host.File)) is { } reader && reader.Read(host.File, new ContainerContext(ContainerOptions)) is [var inner])
                return inner;
            return host.File;
        }

        private FinderInfo? FinderInfoFor(NewFileChoice choice, FinderInfo initial)
        {
            if (!FourCC.TryParse(choice.Type.PadRight(4), out var type) || !FourCC.TryParse(choice.Creator.PadRight(4), out var creator))
            {
                Status = "The type and creator must each be four Mac OS Roman characters.";
                return null;
            }
            return initial with { Type = type, Creator = creator };
        }

        [RelayCommand(CanExecute = nameof(CanCreateInVolume))]
        private async Task NewFolder()
        {
            if (VolumeFolder(Selected) is not { } folder || EditDialogs is null) return;
            if (await EditDialogs.NewFolderAsync("untitled folder") is not { } name) return;
            var path = string.Join(":", FolderNames(folder).Append(name));
            if (!ChangeVolume(folder.Input, image => HfsWriter.CreateFolder(ForkData.FromBytes(image), path), $"create folder {name}")) return;
            var node = new FolderNode(folder, name);
            Insert(folder, node);
            Status = $"Created folder {name}; Save As ▸ HFS Volume Image writes it.";
        }

        // Deletes the selected file, or the selected folder with everything shown in it (after asking).
        [RelayCommand(CanExecute = nameof(CanDeleteFromVolume))]
        private async Task DeleteItem()
        {
            if (VolumeItem(Selected) is not { } item) return;
            var name = ItemName(item);
            var inside = item is FolderNode ? Descendants(item).Count(n => n is FileNode or ContainerFileNode or FolderNode) : 0;
            var question = inside > 0
                ? $"Delete the folder {name} and the {inside} item{(inside == 1 ? "" : "s")} in it from the volume?"
                : $"Delete {name} from the volume?";
            if (EditDialogs is not null && !await EditDialogs.ConfirmAsync("Delete", question)) return;
            // Contents first, deepest first; one failure leaves the volume as it was.
            var steps = new List<Func<byte[], byte[]>>();
            AddDeletes(item, steps);
            if (!ChangeVolume(item.Input, image => steps.Aggregate(image, (at, step) => step(at)), $"delete {name}")) return;
            // Shown or not (hidden, grouped), the item leaves its folder's items; the folder is laid out again.
            var parent = Tree.FolderOf(item)!;
            parent.Items!.Remove(item);
            Tree.Relayout(parent);
            Selected = parent;
            NotifyEditCommands();
            Status = $"Deleted {name}; Save As ▸ HFS Volume Image writes the change.";
        }

        private static void AddDeletes(NodeViewModel item, List<Func<byte[], byte[]>> steps)
        {
            var path = MacPathOf(item);
            if (item is FolderNode)
            {
                foreach (var child in Tree.Contents(item).Where(c => c is FileNode or ContainerFileNode or FolderNode)) AddDeletes(child, steps);
                steps.Add(image => HfsWriter.DeleteFolder(ForkData.FromBytes(image), path));
            }
            else
            {
                steps.Add(image => HfsWriter.DeleteFile(ForkData.FromBytes(image), path));
            }
        }

        private static IEnumerable<NodeViewModel> Descendants(NodeViewModel node) =>
            Tree.Contents(node).SelectMany(c => Descendants(c).Prepend(c));

        private void AddFile(NodeViewModel folder, MacFile file)
        {
            var folderPath = FolderNames(folder);
            var path = string.Join(":", folderPath.Append(file.Name.ToMacRoman()));
            var data = file.DataFork.ToArray();
            var resource = file.ResourceFork.ToArray();
            if (!ChangeVolume(folder.Input, image => HfsWriter.CreateFile(ForkData.FromBytes(image), path, data, resource, file.FinderInfo, file.Created, file.Modified),
                    $"create {file.Name.ToMacRoman()}")) return;
            var created = file with
            {
                FolderPath = folderPath.Select(MacString.FromMacRoman).ToList(),
                DataFork = ForkData.FromBytes(data),
                ResourceFork = ForkData.FromBytes(resource),
                UnicodeName = null,
                UnicodeFolderPath = null,
            };
            var node = ContainerUnwrapper.Default.Unwrap(created, HfsReader.Instance.FormatName, new ContainerContext(ContainerOptions));
            NodeViewModel item = node.Children.Count > 0 ? new ContainerFileNode(folder, node) : new FileNode(folder, node);
            // A file made without resources can be given some at once: its edits start from an empty fork.
            if (item is FileNode { Children.Count: 0 } empty) empty.Resources = new FileResources(null, ResourceForkSource.None);
            Insert(folder, item);
            Status = $"Created {file.Name.ToMacRoman()}; Save As ▸ HFS Volume Image writes it.";
        }

        // Applies a change to the input's volume (its edited copy, or the image as read); false, with the reason in the
        // status line, when HfsWriter refuses it.
        private bool ChangeVolume(InputNode input, Func<byte[], byte[]> change, string what)
        {
            try
            {
                var image = input.EditedVolume ?? input.Root.File.DataFork.ToArray();
                input.EditedVolume = change(image);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                Status = $"Could not {what}: {e.Message}";
                return false;
            }
            input.Title = input.BaseTitle + " •";
            NotifyEditCommands();
            return true;
        }

        // Adds a new item to its folder's items, keeping their order (folders and files by name, as the reader lists them),
        // lays the folder out again and selects the item: where it shows (in the "No name" group, opened), or its folder
        // when the tree hides it.
        private void Insert(NodeViewModel folder, NodeViewModel item)
        {
            var items = folder.Items!;
            var index = 0;
            while (index < items.Count && string.Compare(items[index].BaseTitle, item.BaseTitle, StringComparison.OrdinalIgnoreCase) < 0) index++;
            items.Insert(index, item);
            Tree.Relayout(folder);
            folder.IsExpanded = true;
            if (item.Parent is NoNameGroupNode group) group.IsExpanded = true;
            Selected = Tree.IsShown(item, Roots) ? item : folder;
        }
    }
}
