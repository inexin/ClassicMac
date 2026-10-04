using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Files.Tests;
using ClassicMac.Graphics;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// What the app's editing tests share: a temporary folder, fake file pickers and dialogs, and a MacBinary file to open.
public abstract class EditTestsBase : IDisposable
{
    protected readonly string folder = Directory.CreateTempSubdirectory("cm-edit").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    protected internal sealed class Picker(string folder) : IFilePicker
    {
        public string? Open { get; set; }

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>(Open is null ? [] : [Open]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(folder);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) =>
            Task.FromResult<string?>(Path.Combine(folder, suggestedName));
    }

    internal sealed class Dialogs : IEditDialogs
    {
        public Func<ResourceInfo, ResourceInfo?> Info { get; set; } = i => i;
        public SaveChanges Choice { get; set; } = SaveChanges.Discard;
        public bool Confirm { get; set; } = true;
        public List<string> Asked { get; } = [];

        public List<DialogSubject?> Subjects { get; } = [];
        public List<string> Edited { get; } = [];

        public Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew, DialogSubject? subject)
        {
            Subjects.Add(subject);
            return Task.FromResult(Info(initial));
        }

        public Task<SaveChanges> AskSaveChangesAsync(string fileName, string edited)
        {
            Asked.Add(fileName);
            Edited.Add(edited);
            Log.Add("save " + fileName);
            return Task.FromResult(Choice);
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);

        // The unapplied-draft question: every ask is logged (with the save questions, in order); Pending, when set, is
        // the answer still to come.
        public DraftChoice Draft { get; set; } = DraftChoice.Discard;
        public TaskCompletionSource<DraftChoice>? Pending { get; set; }
        public List<(string What, string? Error)> DraftAsked { get; } = [];
        public List<string> Log { get; } = [];

        public Task<DraftChoice> AskApplyDraftAsync(string what, string? error)
        {
            DraftAsked.Add((what, error));
            Log.Add("draft " + what);
            return Pending?.Task ?? Task.FromResult(Draft);
        }

        public Func<ImportChoice, ImportChoice?> Import { get; set; } = c => c;
        public IReadOnlyList<string> ImportTypes { get; private set; } = [];

        public ImportSource? Source { get; private set; }

        public Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial, ImportSource source)
        {
            ImportTypes = types;
            Source = source;
            return Task.FromResult(Import(initial));
        }

        public Func<NewFileChoice, NewFileChoice?> NewFile { get; set; } = c => c;
        public string? FolderName { get; set; } = "New Folder";

        public Task<NewFileChoice?> NewFileAsync(string title, NewFileChoice initial) => Task.FromResult(NewFile(initial));

        public Task<string?> NewFolderAsync(string initial) => Task.FromResult(FolderName);

        // First Aid's window: each one shown, and whether Repair is clicked.
        public Func<FirstAidView, bool> FirstAid { get; set; } = _ => false;
        public List<FirstAidView> FirstAidShown { get; } = [];

        public Task<bool> FirstAidAsync(FirstAidView view)
        {
            FirstAidShown.Add(view);
            return Task.FromResult(FirstAid(view));
        }
    }

    protected static readonly FourCC Str = FourCC.FromString("STR ");

    // A MacBinary file holding "Prefs" with STR 128 "hello" and STR 129.
    protected string MacBinary()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(Str, 128, "\u0005hello"u8.ToArray()) { Name = MacString.FromMacRoman("greeting") });
        fork.Add(new Resource(Str, 129, "\u0002hi"u8.ToArray()));
        var file = new MacFile { Name = MacString.FromMacRoman("Prefs"), DataFork = ForkData.FromBytes(new byte[] { 7 }), ResourceFork = ForkData.FromBytes(fork.ToArray()) };
        var path = Path.Combine(folder, "Prefs.bin");
        File.WriteAllBytes(path, MacBinaryWriter.ToArray(file));
        return path;
    }

    private protected async Task<(MainViewModel Model, FileNode File, Dialogs Dialogs, Picker Picker, string Path)> Open()
    {
        var path = MacBinary();
        var dialogs = new Dialogs();
        var picker = new Picker(folder);
        var model = new MainViewModel { FilePicker = picker, EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        var file = input.Children.OfType<FileNode>().Single();
        await file.EnsureLoadedAsync();
        return (model, file, dialogs, picker, path);
    }

    // Edit Hex in place: the selected resource's bytes replaced by typing, then applied.
    internal static async Task EditBytes(MainViewModel model, byte[] data)
    {
        await model.EditActions.EditHexCommand.ExecuteAsync(null);
        var editor = model.EditActions.HexEdit!;
        editor.MoveTo(0);
        while (editor.Length > 0)
        {
            editor.Delete();
        }

        foreach (var b in data)
        {
            editor.TypeDigit(b >> 4);
            editor.TypeDigit(b & 0xF);
        }

        model.EditActions.ApplyHexEditCommand.Execute(null);
    }

    protected static ResourceNode Resource(FileNode file, short id) =>
        file.Children.OfType<ResourceTypeNode>().Single(t => t.Type == Str).Children.OfType<ResourceNode>().Single(r => r.Resource.Id == id);

    protected static ResourceFork Saved(string path) =>
        ResourceFork.Read(MacBinaryReader.III.Read(ForkData.FromFile(path), new ContainerContext())[0].ResourceFork.ToArray());
}
