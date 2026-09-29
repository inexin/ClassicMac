using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;
using ClassicMac.Resources;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A resource's type, ID, name and attributes as the Get Info and New Resource dialogs show them.</summary>
    public sealed record ResourceInfo(string Type, short Id, string Name, ResourceAttributes Attributes);

    /// <summary>What to do with unsaved edits when a file closes.</summary>
    public enum SaveChanges
    {
        Save,
        Discard,
        Cancel,
    }

    /// <summary>The editing dialogs; the window provides them, tests replace them.</summary>
    public interface IEditDialogs
    {
        /// <summary>Get Info (or New Resource, when <paramref name="isNew"/>: the type can be typed): the new values, or null when cancelled.</summary>
        Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew);

        /// <summary>Asks whether to save <paramref name="fileName"/>'s edits before it closes.</summary>
        Task<SaveChanges> AskSaveChangesAsync(string fileName);

        /// <summary>Asks a yes/no question; true for yes.</summary>
        Task<bool> ConfirmAsync(string title, string message);

        /// <summary>Edits bytes as hex: the new bytes, or null when cancelled.</summary>
        Task<byte[]?> EditHexAsync(string title, byte[] data);
    }

    /// <summary>The edits made to one file's resources, and where they save to.</summary>
    public sealed class EditState(EditSession session, SaveLocation? location, MacFile file, bool forkInDataFork)
    {
        public EditSession Session { get; } = session;

        /// <summary>Where Save writes, or null when the file can only be saved with Save As (inside a disk image or archive).</summary>
        public SaveLocation? Location { get; internal set; } = location;

        /// <summary>The Mac file as last read or saved (its name, Finder info and other fork go into Save As).</summary>
        public MacFile File { get; internal set; } = file;

        public bool ForkInDataFork { get; } = forkInDataFork;
    }

    public sealed partial class MainViewModel
    {
        public IEditDialogs? EditDialogs { get; set; }

        /// <summary>The last save's task (tests wait for it).</summary>
        internal Task SaveTask { get; private set; } = Task.CompletedTask;

        /// <summary>Whether any open file has unsaved edits.</summary>
        public bool HasUnsavedChanges => Roots.SelectMany(EditedFiles).Any(e => e.State.Session.IsDirty);

        // The file node (a FileNode, or an input read as a fork) that the node belongs to, when its resources are loaded.
        private static NodeViewModel? FileOwner(NodeViewModel? node)
        {
            for (var at = node; at is not null; at = at.Parent)
            {
                if (at is FileNode { Resources: not null }) return at;
                if (at is InputNode { RawResources: not null }) return at;
                if (at is FileNode or InputNode or ContainerFileNode or FolderNode) return null;
            }
            return null;
        }

        private static EditState? EditingOf(NodeViewModel node) => node switch
        {
            FileNode f => f.Editing,
            InputNode i => i.Editing,
            _ => null,
        };

        private static IEnumerable<(NodeViewModel Node, EditState State)> EditedFiles(NodeViewModel root)
        {
            if (EditingOf(root) is { } state) yield return (root, state);
            foreach (var child in root.Children)
                foreach (var found in EditedFiles(child)) yield return found;
        }

        // The file's edit state, made on its first edit.
        private EditState StateFor(NodeViewModel owner)
        {
            if (EditingOf(owner) is { } existing) return existing;
            var input = owner.Input;
            var (resources, container) = owner switch
            {
                FileNode f => (f.Resources!, f.Node),
                _ => (input.RawResources!, input.Root),
            };
            var fork = resources.Fork ?? new ResourceFork();
            bool inData = resources.Source == ResourceForkSource.DataFork;
            var location = ForkSaver.Locate(input.Path, input.Host, input.Root, container, inData);
            var state = new EditState(new EditSession(fork), location, container.File, inData);
            if (owner is FileNode file)
            {
                file.Editing = state;
                if (resources.Fork is null) file.Resources = resources with { Fork = fork, Source = ResourceForkSource.ResourceFork };
            }
            else
            {
                input.Editing = state;
            }
            state.Session.Changed += (_, _) => Refresh(owner, state);
            return state;
        }

        // After an edit: the file's type nodes rebuilt, its title marked while dirty, the commands re-evaluated.
        private void Refresh(NodeViewModel owner, EditState state)
        {
            FileNode.ShowTypes(owner, state.Session.Fork);
            owner.Title = state.Session.IsDirty ? owner.BaseTitle + " •" : owner.BaseTitle;
            NotifyEditCommands();
        }

        private void NotifyEditCommands()
        {
            foreach (var command in new IRelayCommand[] { NewResourceCommand, DuplicateResourceCommand, DeleteResourceCommand, GetInfoCommand,
                ReplaceDataCommand, EditHexCommand, UndoCommand, RedoCommand, SaveCommand, SaveAsCommand, RevertCommand })
                command.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(UndoTitle));
            OnPropertyChanged(nameof(RedoTitle));
        }

        partial void OnSelectedChanged(NodeViewModel? oldValue, NodeViewModel? newValue)
        {
            NotifyEditCommands();
            UpdateForm(newValue);
        }

        // Makes an edit in the selection's file and selects the resource it concerns.
        private void Execute(NodeViewModel owner, IResourceEdit edit, Func<Resource?> select)
        {
            var state = StateFor(owner);
            state.Session.Execute(edit);
            SelectResource(owner, select());
            Status = edit.Description + ".";
        }

        private void SelectResource(NodeViewModel owner, Resource? resource)
        {
            if (resource is null)
            {
                Selected = owner;
                return;
            }
            var typeNode = owner.Children.OfType<ResourceTypeNode>().FirstOrDefault(t => t.Type == resource.Type);
            if (typeNode is null) return;
            owner.IsExpanded = true;
            typeNode.IsExpanded = true;
            Selected = typeNode.Children.OfType<ResourceNode>().FirstOrDefault(r => r.Resource == resource) ?? (NodeViewModel)typeNode;
        }

        // Checks an edit's type, ID, name and attributes: errors refuse it, warnings ask.
        private async Task<(FourCC Type, MacString? Name)?> Validate(ResourceFork fork, ResourceInfo info, Resource? existing)
        {
            if (!FourCC.TryParse(info.Type, out var type))
            {
                Status = $"'{info.Type}' is not a resource type (four Mac OS Roman characters).";
                return null;
            }
            MacString? name = null;
            if (info.Name.Length > 0)
            {
                if (!MacRoman.TryEncode(info.Name, out var bytes) || bytes.Length > 255)
                {
                    Status = "The name must be at most 255 Mac OS Roman characters.";
                    return null;
                }
                name = new MacString(bytes);
            }
            var problems = ResourceEditRules.Check(fork, type, info.Id, name, info.Attributes, existing,
                existing is not null && (existing.Attributes & ResourceAttributes.Compressed) != 0);
            if (problems.FirstOrDefault(p => p.Severity == DiagnosticSeverity.Error) is { } error)
            {
                Status = error.Message;
                return null;
            }
            foreach (var warning in problems)
                if (EditDialogs is null || !await EditDialogs.ConfirmAsync("Resource ID", warning.Message + " Use it anyway?")) return null;
            return (type, name);
        }

        private bool CanEditResource() => !IsExporting && Selected is ResourceNode && FileOwner(Selected) is not null;

        private bool CanNewResource() => !IsExporting && FileOwner(Selected) is not null;

        [RelayCommand(CanExecute = nameof(CanNewResource))]
        private async Task NewResource()
        {
            if (FileOwner(Selected) is not { } owner || EditDialogs is null) return;
            var fork = StateFor(owner).Session.Fork;
            var type = Selected switch { ResourceNode r => r.Resource.Type, ResourceTypeNode t => t.Type, _ => FourCC.FromString("STR ") };
            var initial = new ResourceInfo(type.ToString(), ResourceEditRules.NextFreeId(fork, type), "", ResourceAttributes.None);
            if (await EditDialogs.ResourceInfoAsync("New Resource", initial, isNew: true) is not { } info) return;
            if (await Validate(fork, info, null) is not { } valid) return;
            var add = new AddResource(valid.Type, info.Id, valid.Name, ReadOnlyMemory<byte>.Empty, info.Attributes);
            Execute(owner, add, () => add.Added);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private void DuplicateResource()
        {
            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner) return;
            var duplicate = new DuplicateResource(node.Resource);
            Execute(owner, duplicate, () => duplicate.Copy);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private void DeleteResource()
        {
            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner) return;
            Execute(owner, new DeleteResource(node.Resource), () => null);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task GetInfo()
        {
            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner || EditDialogs is null) return;
            var resource = node.Resource;
            var initial = new ResourceInfo(resource.Type.ToString(), resource.Id, resource.Name?.ToMacRoman() ?? "", resource.Attributes);
            if (await EditDialogs.ResourceInfoAsync($"Info for {resource}", initial, isNew: false) is not { } info || info == initial) return;
            if (await Validate(StateFor(owner).Session.Fork, info with { Type = initial.Type }, resource) is not { } valid) return;
            Execute(owner, new SetResourceInfo(resource, info.Id, valid.Name, info.Attributes), () => resource);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task ReplaceData()
        {
            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner || FilePicker is null) return;
            if ((await FilePicker.PickFilesAsync()).FirstOrDefault() is not { } path) return;
            var data = await File.ReadAllBytesAsync(path);
            Execute(owner, new SetResourceData(node.Resource, data, $"Replace data of {node.Resource}"), () => node.Resource);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task EditHex()
        {
            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner || EditDialogs is null) return;
            var resource = node.Resource;
            if (await EditDialogs.EditHexAsync($"Edit {resource}", resource.GetData().ToArray()) is not { } data) return;
            if (data.AsSpan().SequenceEqual(resource.GetData().Span)) return;
            Execute(owner, new SetResourceData(resource, data, $"Edit {resource}"), () => resource);
        }

        private EditState? SelectedState => FileOwner(Selected) is { } owner ? EditingOf(owner) : null;

        public string UndoTitle => SelectedState?.Session.NextUndo is { } edit ? $"_Undo {edit.Description}" : "_Undo";

        public string RedoTitle => SelectedState?.Session.NextRedo is { } edit ? $"_Redo {edit.Description}" : "_Redo";

        private bool CanUndo() => SelectedState?.Session.NextUndo is not null;

        private bool CanRedo() => SelectedState?.Session.NextRedo is not null;

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private void Undo()
        {
            if (SelectedState is { } state && FileOwner(Selected) is { } owner && state.Session.NextUndo is { } edit)
            {
                state.Session.Undo();
                Selected = owner;
                Status = $"Undid {edit.Description}.";
            }
        }

        [RelayCommand(CanExecute = nameof(CanRedo))]
        private void Redo()
        {
            if (SelectedState is { } state && FileOwner(Selected) is { } owner && state.Session.NextRedo is { } edit)
            {
                state.Session.Redo();
                Selected = owner;
                Status = $"Redid {edit.Description}.";
            }
        }

        private bool CanSave() => !IsExporting && SelectedState is { Session.IsDirty: true, Location: not null };

        [RelayCommand(CanExecute = nameof(CanSave))]
        private Task Save() => SaveTask = FileOwner(Selected) is { } owner ? SaveAsync(owner) : Task.CompletedTask;

        // Saves one file's edits back where they came from; true when saved (or nothing to save).
        private async Task<bool> SaveAsync(NodeViewModel owner)
        {
            if (EditingOf(owner) is not { Session.IsDirty: true } state) return true;
            if (state.Location is not { } location)
            {
                Status = $"{owner.BaseTitle} is inside a disk image or archive; use Save As.";
                return false;
            }
            try
            {
                SaveLocation saved;
                try
                {
                    saved = await Task.Run(() => ForkSaver.Save(location, state.Session.Fork));
                }
                catch (FileChangedException e)
                {
                    if (EditDialogs is null || !await EditDialogs.ConfirmAsync("File changed", $"{e.FilePath} has changed on disk since it was opened. Overwrite it?"))
                        return false;
                    saved = await Task.Run(() => ForkSaver.Save(location, state.Session.Fork, overwriteChanged: true));
                }
                state.Location = saved;
                state.File = saved.File;
                state.Session.MarkSaved();
                Status = $"Saved {owner.BaseTitle} ({saved.Target}); the original is kept as {Path.GetFileName(saved.Path)}.orig.";
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "save.failed", e.Message), owner.Source, owner));
                Status = $"{owner.BaseTitle} was not saved: {e.Message}";
                return false;
            }
        }

        private bool CanSaveAs(SaveAsFormat format) => !IsExporting && FileOwner(Selected) is not null;

        [RelayCommand(CanExecute = nameof(CanSaveAs))]
        private async Task SaveAs(SaveAsFormat format)
        {
            if (FileOwner(Selected) is not { } owner || FilePicker is null) return;
            var state = StateFor(owner);
            var extension = format switch
            {
                SaveAsFormat.MacBinary => ".bin",
                SaveAsFormat.BinHex => ".hqx",
                SaveAsFormat.AppleSingle => ".as",
                SaveAsFormat.RawFork => ".rsrc",
                _ => "",
            };
            var name = HostNames.ToHostName(state.File.Name, 200);
            var path = await FilePicker.PickSaveFileAsync($"Save {owner.BaseTitle} As", name + extension, extension.Length > 0 ? [extension] : []);
            if (path is null) return;
            try
            {
                var file = state.File;
                var written = await Task.Run(() => ForkSaver.SaveAs(path, format, file, state.Session.Fork, state.ForkInDataFork));
                Status = $"Saved {owner.BaseTitle} as {string.Join(", ", written.Select(Path.GetFileName))}.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "save.failed", e.Message), owner.Source, owner));
                Status = $"{owner.BaseTitle} was not saved: {e.Message}";
            }
        }

        private bool CanRevert() => SelectedState is { Session.IsDirty: true };

        /// <summary>Revert: the input read again from disk, discarding its edits.</summary>
        [RelayCommand(CanExecute = nameof(CanRevert))]
        private async Task Revert()
        {
            if (Selected?.Input is not { } input) return;
            if (EditDialogs is not null && !await EditDialogs.ConfirmAsync("Revert", $"Discard the edits to {input.BaseTitle} and read it again from disk?"))
                return;
            var index = Roots.IndexOf(input);
            RemoveInput(input);
            if (await OpenAsync(input.Path) is { } reopened && index >= 0 && index < Roots.Count - 1)
                Roots.Move(Roots.IndexOf(reopened), index);
        }

        /// <summary>
        /// Before an input closes (or the app quits): each file with unsaved edits asks to save, discard or cancel. False
        /// when cancelled or a save failed.
        /// </summary>
        internal async Task<bool> ConfirmCloseAsync(IEnumerable<InputNode> inputs)
        {
            foreach (var (node, state) in inputs.SelectMany(EditedFiles).ToList())
            {
                if (!state.Session.IsDirty) continue;
                var choice = EditDialogs is null ? SaveChanges.Discard : await EditDialogs.AskSaveChangesAsync(node.BaseTitle);
                if (choice == SaveChanges.Cancel) return false;
                if (choice == SaveChanges.Save && !await SaveAsync(node)) return false;
            }
            return true;
        }

        /// <summary>Whether the app may quit: unsaved edits are saved or discarded first.</summary>
        public Task<bool> ConfirmQuitAsync() => ConfirmCloseAsync(Roots.ToList());
    }
}
