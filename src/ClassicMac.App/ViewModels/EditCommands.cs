using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;
using ClassicMac.Resources;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <summary>What to do with a form's or the hex view's unapplied edits before the selection moves.</summary>
    public enum DraftChoice
    {
        /// <summary>Make them one undoable edit, then go on.</summary>
        Apply,

        /// <summary>Drop them, then go on.</summary>
        Discard,

        /// <summary>Stay, with the edits and the selection as they are.</summary>
        Cancel,
    }

    /// <summary>The editing dialogs; the window provides them, tests replace them.</summary>
    public interface IEditDialogs
    {
        /// <summary>
        /// Get Info (or New Resource, when <paramref name="isNew"/>: the type can be typed): the new values, or null when
        /// cancelled. <paramref name="subject"/> is Get Info's icon tile, name and kind line; null for New Resource.
        /// </summary>
        Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew, DialogSubject? subject);

        /// <summary>
        /// Asks whether to save <paramref name="fileName"/>'s edits before it closes; <paramref name="edited"/> says what
        /// was edited ("3 resources in Finder were edited.").
        /// </summary>
        Task<SaveChanges> AskSaveChangesAsync(string fileName, string edited);

        /// <summary>
        /// Asks what to do with the unapplied edits to <paramref name="what"/> (<c>'STR#' 128</c>); <paramref name="error"/>,
        /// when not null, is why they cannot be applied, and Apply is not offered.
        /// </summary>
        Task<DraftChoice> AskApplyDraftAsync(string what, string? error);

        /// <summary>Asks a yes/no question; true for yes.</summary>
        Task<bool> ConfirmAsync(string title, string message);

        /// <summary>
        /// Import: the type (one of <paramref name="types"/>), ID and name to make from <paramref name="fileName"/>, or
        /// null when cancelled. <paramref name="source"/> describes the file and draws what each choice makes.
        /// </summary>
        Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial, ImportSource source);

        /// <summary>New File (or Import File): the name, type and creator, or null when cancelled.</summary>
        Task<NewFileChoice?> NewFileAsync(string title, NewFileChoice initial);

        /// <summary>New Folder: the name, or null when cancelled.</summary>
        Task<string?> NewFolderAsync(string initial);
    }

    /// <summary>Get Info's subject (design/boards/dialogs.md): the icon tile's PNG (null: a plain glyph), the name and "Icon family in Finder · 2,240 bytes".</summary>
    public sealed record DialogSubject(string Name, string Line, byte[]? IconPng);

    /// <summary>
    /// Import's source (design/boards/dialogs.md): the file's details ("32 × 32 · 24-bit") and, for a choice's type (or
    /// <see cref="MainViewModel.IconFamily"/>), the resources it makes, drawn; none for a choice the file cannot make.
    /// </summary>
    public sealed record ImportSource(string Details, Func<string, IReadOnlyList<PreviewImage>> Preview)
    {
        public static ImportSource None { get; } = new("", _ => []);
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

        // Each resource as last read or saved, which the tree's per-resource unsaved mark compares with.
        private Dictionary<(FourCC Type, short Id), (ReadOnlyMemory<byte> Data, MacString? Name, ResourceAttributes Attributes)> saved = Snapshot(session.Fork);

        /// <summary>Records the fork as it is now as the saved state (when the session is clean: read, saved, or undone back).</summary>
        internal void RecordSaved() => saved = Snapshot(Session.Fork);

        /// <summary>Whether a resource differs from the file as saved: new, or another name, attributes or data.</summary>
        internal bool IsUnsaved(Resource resource) =>
            !saved.TryGetValue((resource.Type, resource.Id), out var was)
            || was.Name != resource.Name
            || was.Attributes != resource.Attributes
            || !was.Data.Span.SequenceEqual(resource.GetData().Span);

        /// <summary>How many resources differ from the file as saved: new, changed or deleted.</summary>
        internal int UnsavedCount =>
            Session.Fork.Resources.Count(IsUnsaved) + saved.Keys.Count(key => Session.Fork.Find(key.Type, key.Id) is null);

        private static Dictionary<(FourCC, short), (ReadOnlyMemory<byte>, MacString?, ResourceAttributes)> Snapshot(ResourceFork fork) =>
            fork.Resources.ToDictionary(r => (r.Type, r.Id), r => (r.GetData(), r.Name, r.Attributes));
    }

    public sealed partial class MainViewModel
    {
        public IEditDialogs? EditDialogs { get; set; }

        /// <summary>The last save's task (tests wait for it).</summary>
        internal Task SaveTask { get; private set; } = Task.CompletedTask;

        /// <summary>Whether any open file has unsaved edits.</summary>
        public bool HasUnsavedChanges => Roots.Any(r => r.EditedVolume is not null) || Roots.SelectMany(EditedFiles).Any(e => e.State.Session.IsDirty);

        // The file node (a FileNode, or an input read as a fork) that the node belongs to, when its resources are loaded.
        private static NodeViewModel? FileOwner(NodeViewModel? node)
        {
            for (var at = node; at is not null; at = at.Parent)
            {
                if (at is FileNode { Resources: not null })
                {
                    return at;
                }

                if (at is InputNode { RawResources: not null })
                {
                    return at;
                }

                if (at is FileNode or InputNode or ContainerFileNode or FolderNode)
                {
                    return null;
                }
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
            if (EditingOf(root) is { } state)
            {
                yield return (root, state);
            }

            foreach (var child in Tree.Contents(root))
            {
                foreach (var found in EditedFiles(child))
                {
                    yield return found;
                }
            }
        }

        // The file's edit state, made on its first edit.
        private EditState StateFor(NodeViewModel owner)
        {
            if (EditingOf(owner) is { } existing)
            {
                return existing;
            }

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
                if (resources.Fork is null)
                {
                    file.Resources = resources with { Fork = fork, Source = ResourceForkSource.ResourceFork };
                }
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
            if (!state.Session.IsDirty)
            {
                state.RecordSaved();
            }

            owner.Title = state.Session.IsDirty ? owner.BaseTitle + " •" : owner.BaseTitle;
            foreach (var resource in owner.Children.OfType<ResourceTypeNode>().SelectMany(t => t.Children).OfType<ResourceNode>())
            {
                if (state.IsUnsaved(resource.Resource))
                {
                    resource.Title = resource.BaseTitle + " •";
                }
            }

            NotifyEditCommands();
        }

        private void NotifyEditCommands()
        {
            foreach (var command in new IRelayCommand[] { NewResourceCommand, DuplicateResourceCommand, DeleteResourceCommand, GetInfoCommand,
                ReplaceDataCommand, EditHexCommand, BeginHexEditCommand, ImportCommand, UndoCommand, RedoCommand, SaveCommand, SaveAsCommand, RevertCommand,
                NewFileCommand, ImportFileCommand, NewFolderCommand, DeleteItemCommand })
            {
                command.NotifyCanExecuteChanged();
            }

            OnPropertyChanged(nameof(UndoTitle));
            OnPropertyChanged(nameof(RedoTitle));
            NotifyTitle();
        }

        private void OnSelectedChanged(NodeViewModel? oldValue, NodeViewModel? newValue)
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
            if (typeNode is null)
            {
                return;
            }

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
            {
                if (EditDialogs is null || !await EditDialogs.ConfirmAsync("Resource ID", warning.Message + " Use it anyway?"))
                {
                    return null;
                }
            }

            return (type, name);
        }

        private bool CanEditResource() => !IsExporting && Selected is ResourceNode && FileOwner(Selected) is not null;

        private bool CanNewResource() => !IsExporting && FileOwner(Selected) is not null;

        [RelayCommand(CanExecute = nameof(CanNewResource))]
        private async Task NewResource()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (FileOwner(Selected) is not { } owner || EditDialogs is null)
            {
                return;
            }

            var fork = StateFor(owner).Session.Fork;
            var type = Selected switch { ResourceNode r => r.Resource.Type, ResourceTypeNode t => t.Type, _ => FourCC.FromString("STR ") };
            var initial = new ResourceInfo(type.ToString(), ResourceEditRules.NextFreeId(fork, type), "", ResourceAttributes.None);
            if (await EditDialogs.ResourceInfoAsync("New Resource", initial, isNew: true, subject: null) is not { } info)
            {
                return;
            }

            if (await Validate(fork, info, null) is not { } valid)
            {
                return;
            }

            var add = new AddResource(valid.Type, info.Id, valid.Name, ReadOnlyMemory<byte>.Empty, info.Attributes);
            Execute(owner, add, () => add.Added);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task DuplicateResource()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner)
            {
                return;
            }

            var duplicate = new DuplicateResource(node.Resource);
            Execute(owner, duplicate, () => duplicate.Copy);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task DeleteResource()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner)
            {
                return;
            }

            Execute(owner, new DeleteResource(node.Resource), () => null);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task GetInfo()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner || EditDialogs is null)
            {
                return;
            }

            var resource = node.Resource;
            var initial = new ResourceInfo(resource.Type.ToString(), resource.Id, resource.Name?.ToMacRoman() ?? "", resource.Attributes);
            var header = InspectorHeader.For(node)!;
            var subject = new DialogSubject(header.Name, string.Create(CultureInfo.InvariantCulture, $"{header.Kind} · {resource.Length:N0} bytes"),
                await Task.Run(() => NodeViewModel.LargeIcon(node)));
            if (await EditDialogs.ResourceInfoAsync($"Info for {resource}", initial, isNew: false, subject) is not { } info || info == initial)
            {
                return;
            }

            if (await Validate(StateFor(owner).Session.Fork, info with { Type = initial.Type }, resource) is not { } valid)
            {
                return;
            }

            Execute(owner, new SetResourceInfo(resource, info.Id, valid.Name, info.Attributes), () => resource);
        }

        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task ReplaceData()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner || FilePicker is null)
            {
                return;
            }

            if ((await FilePicker.PickFilesAsync()).FirstOrDefault() is not { } path)
            {
                return;
            }

            var data = await File.ReadAllBytesAsync(path);
            Execute(owner, new SetResourceData(node.Resource, data, $"Replace data of {node.Resource}"), () => node.Resource);
        }

        /// <summary>
        /// Edit Hex (Ctrl+H): the selected resource's bytes in the Hex tab, already in editing (design/boards/hex.md, E7);
        /// while they are, it only shows the tab. A form's unapplied values are asked about first.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanEditResource))]
        private async Task EditHex()
        {
            if (IsHexEditing)
            {
                SelectedTab = 2;
                return;
            }

            if (!await ResolveDraftAsync())
            {
                return;
            }

            BeginHexEdit();
        }

        /// <summary>
        /// The hex inspector's reading: the editor's at its cursor while editing, else the byte selected in the read-only
        /// view; null when there is neither.
        /// </summary>
        public HexInspection? HexInspection => HexEdit?.Inspector ?? readInspection;

        private HexInspection? readInspection;

        /// <summary>
        /// A click on a byte of the hex view: the cursor while editing; otherwise the byte is selected, its pair highlighted
        /// and inspected, its field (E8) highlighted.
        /// </summary>
        public void SelectHexByte(long offset)
        {
            if (HexEdit is { } editor)
            {
                editor.MoveTo((int)Math.Min(offset, int.MaxValue));
                return;
            }

            if (HexSource is not { } source || HexLines is not { } lines || offset < 0 || offset >= source.Data.Length)
            {
                return;
            }

            // Reads what is around the byte; a meaning needs the whole resource (the hex view shows resources only).
            var data = source.Data.Length <= ReadOptions.MaxResourceSize ? source.Data.ToArray() : null;
            var meanings = data is not null && Selected is ResourceNode node ? MeaningsFor(node) : null;
            readInspection = data is not null
                ? HexInspection.At(data, (int)offset, meanings)
                : HexInspection.At(source.Data.Slice(offset, Math.Min(4, source.Data.Length - offset)).ToArray(), 0, null) with
                {
                    Heading = string.Create(CultureInfo.InvariantCulture, $"At 0x{offset:X4}"),
                };
            hexSelectedOffset = offset;
            lines.Select(offset, readInspection.Meaning is { } field ? (field.Start, field.Length) : null);
            OnPropertyChanged(nameof(HexInspection));
        }

        // The read-only selection goes with the lines it was made on.
        partial void OnHexLinesChanged(HexLines? value)
        {
            readInspection = null;
            hexSelectedOffset = -1;
            FindStatus = null;
            FindFailed = false;
            OnPropertyChanged(nameof(HexInspection));
        }

        partial void OnHexEditChanged(HexEditor? oldValue, HexEditor? newValue)
        {
            if (oldValue is not null)
            {
                oldValue.PropertyChanged -= OnHexEditorChanged;
            }

            if (newValue is not null)
            {
                newValue.PropertyChanged += OnHexEditorChanged;
            }

            OnPropertyChanged(nameof(HexInspection));
        }

        private void OnHexEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(HexEditor.Inspector))
            {
                OnPropertyChanged(nameof(HexInspection));
            }
        }

        /// <summary>The Hex tab's Go to box: a hex offset.</summary>
        [ObservableProperty]
        private string goToText = "";

        /// <summary>Why the Go to box's text was refused, or null.</summary>
        [ObservableProperty]
        private string? goToError;

        /// <summary>Moves the hex cursor to the offset in <see cref="GoToText"/>.</summary>
        [RelayCommand]
        private void GoTo()
        {
            if (HexEdit is not { } editor)
            {
                return;
            }

            GoToError = editor.GoTo(GoToText) ? null : "Not a hex offset";
        }

        /// <summary>The bytes being edited in the hex view, or null.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsHexEditing))]
        [NotifyPropertyChangedFor(nameof(HasHex))]
        [NotifyCanExecuteChangedFor(nameof(BeginHexEditCommand), nameof(SaveCommand))]
        private HexEditor? hexEdit;

        private (Resource Resource, NodeViewModel Owner)? hexEditTarget;

        public bool IsHexEditing => HexEdit is not null;

        [RelayCommand(CanExecute = nameof(CanBeginHexEdit))]
        private void BeginHexEdit()
        {
            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner)
            {
                return;
            }

            hexEditTarget = (node.Resource, owner);
            HexEdit = new HexEditor(node.Resource.GetData(), MeaningsFor(node));
            GoToError = null;
            HexEdit.Edited += (_, _) => SaveCommand.NotifyCanExecuteChanged();
            HexLines = HexEdit.Lines;
            SelectedTab = 2;
        }

        private bool CanBeginHexEdit() => CanEditResource() && !IsHexEditing;

        [RelayCommand]
        private void ApplyHexEdit()
        {
            if (TakeHexEdit() is not var (resource, owner, data))
            {
                return;
            }

            Execute(owner, new SetResourceData(resource, data, $"Edit {resource}"), () => resource);
        }

        [RelayCommand]
        private void DiscardHexEdit()
        {
            TakeHexEdit();
            HexLines = HexSource is null ? null : new HexLines(HexSource.Data);
        }

        // Ends hex editing; the edited bytes when they differ from the resource's.
        private (Resource Resource, NodeViewModel Owner, byte[] Data)? TakeHexEdit()
        {
            if (HexEdit is not { } editor || hexEditTarget is not { } target)
            {
                return null;
            }

            HexEdit = null;
            hexEditTarget = null;
            // A resource with a preview has no Hex tab once its bytes are not being edited: back to the preview.
            if (SelectedTab == 2 && Hex.Sources.Count == 0)
            {
                SelectedTab = Preview.HasPreview ? 1 : 0;
            }

            return editor.IsModified ? (target.Resource, target.Owner, editor.ToArray()) : null;
        }

        private EditState? SelectedState => FileOwner(Selected) is { } owner ? EditingOf(owner) : null;

        public string UndoTitle => SelectedState?.Session.NextUndo is { } edit ? $"_Undo {edit.Description}" : "_Undo";

        public string RedoTitle => SelectedState?.Session.NextRedo is { } edit ? $"_Redo {edit.Description}" : "_Redo";

        private bool CanUndo() => SelectedState?.Session.NextUndo is not null;

        private bool CanRedo() => SelectedState?.Session.NextRedo is not null;

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private async Task Undo()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (SelectedState is { } state && FileOwner(Selected) is { } owner && state.Session.NextUndo is { } edit)
            {
                state.Session.Undo();
                Selected = owner;
                Status = $"Undid {edit.Description}.";
            }
        }

        [RelayCommand(CanExecute = nameof(CanRedo))]
        private async Task Redo()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (SelectedState is { } state && FileOwner(Selected) is { } owner && state.Session.NextRedo is { } edit)
            {
                state.Session.Redo();
                Selected = owner;
                Status = $"Redid {edit.Description}.";
            }
        }

        private bool CanSave() => !IsExporting && (SelectedState is { Session.IsDirty: true, Location: not null } || FileOwner(Selected) is not null && HasDraft);

        [RelayCommand(CanExecute = nameof(CanSave))]
        private Task Save() => SaveTask = SaveSelectedAsync();

        // Unapplied edits are applied (or discarded) first; cancelled, nothing is saved.
        private async Task SaveSelectedAsync()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (FileOwner(Selected) is { } owner)
            {
                await SaveAsync(owner);
            }
        }

        // Saves one file's edits back where they came from; true when saved (or nothing to save).
        private async Task<bool> SaveAsync(NodeViewModel owner)
        {
            if (EditingOf(owner) is not { Session.IsDirty: true } state)
            {
                return true;
            }

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
                    {
                        return false;
                    }

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

        private bool CanSaveAs(SaveAsFormat format) =>
            !IsExporting && (format == SaveAsFormat.HfsImage ? Selected?.Input.IsWritableHfs == true : FileOwner(Selected) is not null);

        [RelayCommand(CanExecute = nameof(CanSaveAs))]
        private async Task SaveAs(SaveAsFormat format)
        {
            if (format == SaveAsFormat.HfsImage)
            {
                await SaveHfsImageAs();
                return;
            }
            if (FileOwner(Selected) is not { } owner || FilePicker is null)
            {
                return;
            }

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
            if (path is null)
            {
                return;
            }

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

        // Save As ▸ HFS Volume Image: a copy of the image with the files and folders created and deleted, and every
        // edited fork in it (the selected file's always), written and verified; the image itself is not changed.
        private async Task SaveHfsImageAs()
        {
            if (Selected?.Input is not { IsWritableHfs: true } input || FilePicker is null)
            {
                return;
            }

            var selectedOwner = FileOwner(Selected);
            if (selectedOwner is not null)
            {
                StateFor(selectedOwner);
            }

            var forks = EditedFiles(input)
                .Where(e => e.Node is FileNode && VolumeItem(e.Node) is not null && (e.State.Session.IsDirty || ReferenceEquals(e.Node, selectedOwner)))
                .Select(e => new HfsForkReplacement(e.State.File.MacPath, e.State.Session.Fork, e.State.ForkInDataFork))
                .ToList();
            var extension = Path.GetExtension(input.Path) is { Length: > 0 } imageExtension ? imageExtension : ".img";
            var path = await FilePicker.PickSaveFileAsync($"Save {input.BaseTitle} As", Path.GetFileNameWithoutExtension(input.Path) + "-edited" + extension, [extension]);
            if (path is null)
            {
                return;
            }

            try
            {
                var volumeChanged = input.EditedVolume is not null;
                var written = await Task.Run(() => input.VolumeSession.SaveAs(path, forks));
                Status = $"Saved HFS image {written[0]} ({forks.Count} fork{(forks.Count == 1 ? "" : "s")}{(volumeChanged ? ", files and folders" : "")} changed).";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException)
            {
                Report(new DiagnosticEntry(new Diagnostic(DiagnosticSeverity.Error, "save.failed", e.Message), input.Source, input));
                Status = $"{input.BaseTitle} was not saved: {e.Message}";
            }
        }

        private bool CanRevert() => SelectedState is { Session.IsDirty: true } || Selected?.Input.EditedVolume is not null;

        /// <summary>Revert: the input read again from disk, discarding its edits.</summary>
        [RelayCommand(CanExecute = nameof(CanRevert))]
        private async Task Revert()
        {
            if (Selected?.Input is not { } input)
            {
                return;
            }

            if (EditDialogs is not null && !await EditDialogs.ConfirmAsync("Revert", $"Discard the edits to {input.BaseTitle} and read it again from disk?"))
            {
                return;
            }

            DiscardDraft();
            var index = Roots.IndexOf(input);
            RemoveInput(input);
            if (await OpenAsync(input.Path) is { } reopened && index >= 0 && index < Roots.Count - 1)
            {
                Roots.Move(Roots.IndexOf(reopened), index);
            }
        }

        /// <summary>
        /// Before an input closes (or the app quits): each file with unsaved edits asks to save, discard or cancel. False
        /// when cancelled or a save failed.
        /// </summary>
        internal async Task<bool> ConfirmCloseAsync(IEnumerable<InputNode> inputs)
        {
            inputs = inputs.ToList();
            // Unapplied edits in a closing file are applied or discarded first, then its saving is asked.
            if (Selected?.Input is { } selectedInput && inputs.Contains(selectedInput) && !await ResolveDraftAsync())
            {
                return false;
            }

            foreach (var input in inputs.Where(i => i.EditedVolume is not null))
            {
                var choice = EditDialogs is null ? SaveChanges.Discard : await EditDialogs.AskSaveChangesAsync(input.BaseTitle, "Files and folders were created or deleted.");
                if (choice == SaveChanges.Cancel)
                {
                    return false;
                }

                if (choice == SaveChanges.Save)
                {
                    Status = $"The files and folders created or deleted in {input.BaseTitle} are saved with Save As ▸ HFS Volume Image.";
                    return false;
                }
            }
            foreach (var (node, state) in inputs.SelectMany(EditedFiles).ToList())
            {
                if (!state.Session.IsDirty)
                {
                    continue;
                }

                var choice = EditDialogs is null ? SaveChanges.Discard : await EditDialogs.AskSaveChangesAsync(node.Input.BaseTitle,
                    EditedSummary(state.UnsavedCount, ReferenceEquals(node, node.Input) ? null : node.BaseTitle));
                if (choice == SaveChanges.Cancel)
                {
                    return false;
                }

                if (choice == SaveChanges.Save && !await SaveAsync(node))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>The save question's detail: "3 resources in Finder were edited." (no count when none is known).</summary>
        public static string EditedSummary(int count, string? file)
        {
            var where = file is null ? "" : $" in {file}";
            return count switch
            {
                <= 0 => $"Resources{where} were edited.",
                1 => $"1 resource{where} was edited.",
                _ => string.Create(CultureInfo.InvariantCulture, $"{count:N0} resources{where} were edited."),
            };
        }

        /// <summary>Whether the app may quit: unsaved edits are saved or discarded first.</summary>
        public Task<bool> ConfirmQuitAsync() => ConfirmCloseAsync(Roots.ToList());
    }
}
