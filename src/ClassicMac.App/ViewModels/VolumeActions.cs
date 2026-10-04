using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

/// <summary>A new file's name, type and creator, as the New File dialog shows them.</summary>
public sealed record NewFileChoice(string Name, string Type, string Creator);

// The Volume menu: files and folders created and deleted in a plain HFS image through HfsWriter. Each change is made
// at once on an in-memory copy of the volume (so a refused one says why straight away) and shown in the tree; the
// image on disk is not touched, and the changes are written only by Save As ▸ HFS Volume Image, with the fork edits.
public sealed partial class VolumeActions(MainViewModel main) : ObservableObject
{
    // The folder node a new item goes into (the selected folder, the volume's root, or the folder of the selected
    // file), when it is in the input's writable volume and not inside a container held in it.
    private static NodeViewModel? VolumeFolder(NodeViewModel? node)
    {
        if (node is null or ResourceTypeNode or ResourceNode or LoadingNode || node.Input.VolumeRoot is not { } root)
        {
            return null;
        }

        NodeViewModel? folder = null;
        for (var at = node is ContainerFileNode && node != root ? node.Parent : node; at is not null; at = at.Parent)
        {
            if (at == root)
            {
                return folder ?? at;
            }

            if (at is FolderNode)
            {
                folder ??= at;
            }
            else if (at is ContainerFileNode or InputNode)
            {
                return null;
            }
        }
        return null;
    }

    // The selected file or folder, when it can be deleted from a plain HFS image.
    internal static NodeViewModel? VolumeItem(NodeViewModel? node) =>
        node is FileNode or ContainerFileNode or FolderNode && Tree.FolderOf(node) is { } parent && VolumeFolder(parent) == parent ? node : null;

    // A folder node's Mac path below the volume's root ("" for the root).
    private static List<string> FolderNames(NodeViewModel folder)
    {
        var names = new List<string>();
        for (var at = folder; at is FolderNode; at = at.Parent!)
        {
            names.Insert(0, at.BaseTitle);
        }

        return names;
    }

    private static string ItemName(NodeViewModel item) => item switch
    {
        FileNode f => f.File.Name.ToMacRoman(),
        ContainerFileNode c => c.File.Name.ToMacRoman(),
        _ => item.BaseTitle,
    };

    private static string MacPathOf(NodeViewModel item) => string.Join(":", FolderNames(Tree.FolderOf(item)!).Append(ItemName(item)));

    private bool CanCreateInVolume() => !main.ExportActions.IsExporting && VolumeFolder(main.Selected) is not null;

    private bool CanDeleteFromVolume() => !main.ExportActions.IsExporting && VolumeItem(main.Selected) is not null;

    [RelayCommand(CanExecute = nameof(CanCreateInVolume))]
    private async Task NewFile()
    {
        if (VolumeFolder(main.Selected) is not { } folder || main.EditDialogs is null)
        {
            return;
        }

        if (await main.EditDialogs.NewFileAsync("New File", new NewFileChoice("untitled", "TEXT", "ttxt")) is not { } choice)
        {
            return;
        }

        if (FinderInfoFor(choice, FinderInfo.Empty) is not { } finder)
        {
            return;
        }

        AddFile(folder, new MacFile { Name = MacString.FromMacRoman(choice.Name), FinderInfo = finder });
    }

    /// <summary>
    /// Import File: a host file (its data fork, and its resource fork and Finder info when it has an AppleDouble or
    /// Basilisk II companion or is MacBinary or AppleSingle) made into a new file in the selected folder.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateInVolume))]
    private async Task ImportFile()
    {
        if (VolumeFolder(main.Selected) is not { } folder || main.EditDialogs is null || main.FilePicker is null)
        {
            return;
        }

        if ((await main.FilePicker.PickFilesAsync()).FirstOrDefault() is not { } path)
        {
            return;
        }

        MacFile imported;
        try
        {
            imported = await Task.Run(() => HostImport.Read(path, main.ContainerOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            main.Status = $"{Path.GetFileName(path)} could not be read: {e.Message}";
            return;
        }
        var name = imported.Name.ToMacRoman();
        if (name.Length > 31)
        {
            name = name[..31];
        }

        var initial = new NewFileChoice(name, imported.FinderInfo.Type.ToString(), imported.FinderInfo.Creator.ToString());
        if (await main.EditDialogs.NewFileAsync($"Import “{Path.GetFileName(path)}”", initial) is not { } choice)
        {
            return;
        }

        if (FinderInfoFor(choice, imported.FinderInfo) is not { } finder)
        {
            return;
        }

        AddFile(folder, imported with { Name = MacString.FromMacRoman(choice.Name), FinderInfo = finder });
    }

    private FinderInfo? FinderInfoFor(NewFileChoice choice, FinderInfo initial)
    {
        if (!InputEditSession.TryParseCode(choice.Type, out var type) || !InputEditSession.TryParseCode(choice.Creator, out var creator))
        {
            main.Status = "The type and creator must each be four Mac OS Roman characters.";
            return null;
        }
        return initial with { Type = type, Creator = creator };
    }

    [RelayCommand(CanExecute = nameof(CanCreateInVolume))]
    private async Task NewFolder()
    {
        if (VolumeFolder(main.Selected) is not { } folder || main.EditDialogs is null)
        {
            return;
        }

        if (await main.EditDialogs.NewFolderAsync("untitled folder") is not { } name)
        {
            return;
        }

        var path = string.Join(":", FolderNames(folder).Append(name));
        if (!ChangeVolume(folder.Input, session => session.AddFolder(path), $"create folder {name}"))
        {
            return;
        }

        var node = new FolderNode(folder, name);
        Insert(folder, node);
        main.Status = $"Created folder {name}; Save As ▸ HFS Volume Image writes it.";
    }

    // Deletes the selected file, or the selected folder with everything shown in it (after asking).
    [RelayCommand(CanExecute = nameof(CanDeleteFromVolume))]
    private async Task DeleteItem()
    {
        if (VolumeItem(main.Selected) is not { } item)
        {
            return;
        }

        var name = ItemName(item);
        var inside = item is FolderNode ? Descendants(item).Count(n => n is FileNode or ContainerFileNode or FolderNode) : 0;
        var question = inside > 0
            ? $"Delete the folder {name} and the {inside} item{(inside == 1 ? "" : "s")} in it from the volume?"
            : $"Delete {name} from the volume?";
        if (main.EditDialogs is not null && !await main.EditDialogs.ConfirmAsync("Delete", question))
        {
            return;
        }
        // A folder with everything in it (deepest first); one failure leaves the volume as it was.
        var path = MacPathOf(item);
        if (!ChangeVolume(item.Input, session => session.Delete(path, recursive: item is FolderNode), $"delete {name}"))
        {
            return;
        }
        // Shown or not (hidden, grouped), the item leaves its folder's items; the folder is laid out again.
        var parent = Tree.FolderOf(item)!;
        parent.Items!.Remove(item);
        Tree.Relayout(parent);
        main.Selected = parent;
        main.EditActions.NotifyEditCommands();
        main.Status = $"Deleted {name}; Save As ▸ HFS Volume Image writes the change.";
    }

    private static IEnumerable<NodeViewModel> Descendants(NodeViewModel node) =>
        Tree.Contents(node).SelectMany(c => Descendants(c).Prepend(c));

    private void AddFile(NodeViewModel folder, MacFile file)
    {
        var folderPath = FolderNames(folder);
        var path = string.Join(":", folderPath.Append(file.Name.ToMacRoman()));
        var data = file.DataFork.ToArray();
        var resource = file.ResourceFork.ToArray();
        var added = file with { DataFork = ForkData.FromBytes(data), ResourceFork = ForkData.FromBytes(resource) };
        if (!ChangeVolume(folder.Input, session => session.AddFile(path, added), $"create {file.Name.ToMacRoman()}"))
        {
            return;
        }

        var created = file with
        {
            FolderPath = folderPath.Select(MacString.FromMacRoman).ToList(),
            DataFork = ForkData.FromBytes(data),
            ResourceFork = ForkData.FromBytes(resource),
            UnicodeName = null,
            UnicodeFolderPath = null,
        };
        var node = ContainerUnwrapper.Default.Unwrap(created, HfsReader.Instance.FormatName, new ContainerContext(main.ContainerOptions));
        NodeViewModel item = node.Children.Count > 0 ? new ContainerFileNode(folder, node) : new FileNode(folder, node);
        // A file made without resources can be given some at once: its edits start from an empty fork.
        if (item is FileNode { Children.Count: 0 } empty)
        {
            empty.Resources = new FileResources(null, ResourceForkSource.None);
        }

        Insert(folder, item);
        main.Status = $"Created {file.Name.ToMacRoman()}; Save As ▸ HFS Volume Image writes it.";
    }

    // Applies a change to the input's volume through its edit session; false, with the reason in the status line, when
    // the writer refuses it (the session is left as it was).
    private bool ChangeVolume(InputNode input, Action<InputEditSession> change, string what)
    {
        try
        {
            change(input.VolumeSession);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            main.Status = $"Could not {what}: {e.Message}";
            return false;
        }
        input.Title = input.BaseTitle + " •";
        main.EditActions.NotifyEditCommands();
        return true;
    }

    // Adds a new item to its folder's items, keeping their order (folders and files by name, as the reader lists them),
    // lays the folder out again and selects the item: where it shows (in the "No name" group, opened), or its folder
    // when the tree hides it.
    private void Insert(NodeViewModel folder, NodeViewModel item)
    {
        var items = folder.Items!;
        var index = 0;
        while (index < items.Count && string.Compare(items[index].BaseTitle, item.BaseTitle, StringComparison.OrdinalIgnoreCase) < 0)
        {
            index++;
        }

        items.Insert(index, item);
        Tree.Relayout(folder);
        folder.IsExpanded = true;
        if (item.Parent is NoNameGroupNode group)
        {
            group.IsExpanded = true;
        }

        main.Selected = Tree.IsShown(item, main.Roots) ? item : folder;
    }
}
