using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

// The tree's display options: files with the invisible flag hidden (T1), files with no name grouped (T2), both
// switchable and remembered (T3). Only the tree changes: exports, previews and the volume commands see every file.
public sealed class TreeDisplayTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-tree").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static FinderInfo Info(FinderFlags flags = FinderFlags.HasBeenInited) =>
        new() { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt"), Flags = flags };

    private static byte[] Str(string text) => PreviewTests.Fork(("STR ", 128, null, [(byte)text.Length, .. MacRoman.Encode(text)]));

    // Folder art, as on a real volume: the Realmz folder holds its Icon\r (invisible), four files named only with
    // whitespace (space, two spaces, tab, option-space and tab), the application and a subfolder with one file named "\r";
    // at the root an invisible "Desktop DB" and the invisible "Desktop Folder" with a note in it.
    internal static string Disk(string folder)
    {
        var disk = new HfsBuilder { CatalogLeaves = 6 };
        var desktop = disk.Folder(HfsBuilder.Root, "Desktop Folder", new FolderFinderInfo { Flags = FinderFlags.IsInvisible });
        disk.File(desktop, "Note", "on the desktop"u8.ToArray(), []);
        disk.File(HfsBuilder.Root, "Desktop DB", [], [], info: Info(FinderFlags.IsInvisible));
        var realmz = disk.Folder(HfsBuilder.Root, "Realmz");
        disk.File(realmz, "Icon\r", [], PreviewTests.Fork(("ICN#", -16455, null, new byte[256])), info: Info(FinderFlags.IsInvisible));
        disk.File(realmz, " ", [], Str("one"), info: Info());
        disk.File(realmz, "  ", [], Str("two"), info: Info());
        disk.File(realmz, "\t", [], Str("tab"), info: Info());
        disk.File(realmz, " 	", [], Str("option"), info: Info());   // HFS sorts option-space as a space: " " is taken
        disk.File(realmz, "Realmz", [1, 2, 3], Str("app"), type: "APPL", creator: "RLMZ");
        var sub = disk.Folder(realmz, "Sub");
        disk.File(sub, "\r", [], Str("alone"), info: Info());
        var path = Path.Combine(folder, "Realmz.img");
        File.WriteAllBytes(path, EditTests.WithFreeSpace(disk.Build("Realmz Disk")));
        return path;
    }

    private async Task<(MainViewModel Model, InputNode Input, MemorySettingsStore Settings)> Open(AppSettings? settings = null)
    {
        var store = new MemorySettingsStore(settings ?? new AppSettings());
        var model = new MainViewModel(store);
        var input = (await model.OpenAsync(Disk(folder)))!;
        return (model, input, store);
    }

    private static FolderNode Folder(NodeViewModel parent, string name) => parent.Children.OfType<FolderNode>().Single(f => f.Title == name);

    private static IEnumerable<string> Names(NodeViewModel node) => node.Children.Select(c => c.Name);

    [Fact]
    public async Task Invisible_files_are_hidden_but_invisible_folders_show()
    {
        var (model, input, _) = await Open();
        Assert.Contains("Desktop Folder", Names(input));                   // the desktop, as the Finder shows it
        Assert.DoesNotContain("Desktop DB", Names(input));
        var realmz = Folder(input, "Realmz");
        Assert.DoesNotContain(realmz.Children, c => c is FileNode { File.Name: var n } && n.ToMacRoman() == "Icon\r");
        Assert.Equal(2, model.HiddenCount);
        Assert.Equal("2 invisible items hidden", model.HiddenSummary);

        model.ShowHiddenCommand.Execute(null);                              // the footer's Show
        Assert.False(model.TreeDisplay.HideInvisible);
        Assert.Contains("Desktop DB", Names(input));
        Assert.Contains(realmz.Children, c => c is FileNode { File.Name: var n } && n.ToMacRoman() == "Icon\r");
        Assert.Equal(0, model.HiddenCount);
        Assert.Null(model.HiddenSummary);
    }

    [Fact]
    public async Task Files_with_no_name_are_grouped_first_among_the_files()
    {
        var (model, input, _) = await Open();
        model.TreeDisplay.ShowDetails = true;                                // the count is in the details column
        var realmz = Folder(input, "Realmz");
        var group = Assert.IsType<NoNameGroupNode>(realmz.Children.First(c => c is not FolderNode));
        Assert.Equal(NodeKind.NoNameGroup, group.Kind);
        Assert.Equal(TreeIconKind.NoNameGroup, group.IconKind);
        Assert.Equal(("No name", true, "4 files"), (group.Name, group.IsItalic, group.Meta));
        Assert.False(group.IsExpanded);
        Assert.Equal(new[] { "sp", "sp×2", "tab", "nbsp tab" }.Order(), Names(group).Order());
        Assert.Equal(["nbsp", "tab"], group.Children.Single(c => c.Name == "nbsp tab").NameTokens!.Select(t => t.Label));
        Assert.Equal("CA 09", group.Children.Single(c => c.Name == "nbsp tab").NameBytes);  // the bytes on hover
        Assert.All(group.Children, c => Assert.True(c.HasNameTokens));
        Assert.All(group.Children, c => Assert.Same(group, c.Parent));
        Assert.Contains("Realmz", Names(realmz));
        Assert.Equal(3, realmz.Children.Count);                             // Sub, the group, Realmz

        // One file with no name in a folder stays a row of its own.
        var alone = Assert.IsType<FileNode>(Assert.Single(Folder(realmz, "Sub").Children));
        Assert.Equal(("(no name)", true, false), (alone.Name, alone.IsItalic, alone.HasNameTokens));
        Assert.Equal("\r", alone.File.Name.ToMacRoman());
    }

    [Fact]
    public void Whitespace_names_are_shown_visibly()
    {
        Assert.True(TreeLayout.HasNoName(MacString.FromMacRoman("")));
        Assert.True(TreeLayout.HasNoName(new MacString([0x20, 0xCA, 0x09, 0x0D, 0x01, 0x7F])));
        Assert.False(TreeLayout.HasNoName(MacString.FromMacRoman(" a ")));
        Assert.Empty(TreeLayout.NameTokens(MacString.FromMacRoman("")));                // shown as "(empty)"
        Assert.Equal(["sp", "nbsp", "cr", "lf", "tab", "^A", "^?"],
            TreeLayout.NameTokens(new MacString([0x20, 0xCA, 0x0D, 0x0A, 0x09, 0x01, 0x7F])).Select(t => t.Label));
        Assert.Equal(["sp×3", "nbsp", "sp×2"], TreeLayout.NameTokens(new MacString([0x20, 0x20, 0x20, 0xCA, 0x20, 0x20])).Select(t => t.Label));
        Assert.Equal("20 20 20 CA", TreeLayout.NameBytes(new MacString([0x20, 0x20, 0x20, 0xCA])));
        Assert.Equal("", TreeLayout.NameBytes(MacString.FromMacRoman("")));
    }

    [Fact]
    public async Task Grouped_files_open_preview_and_drag_like_any_file()
    {
        var (model, input, _) = await Open();
        var realmz = Folder(input, "Realmz");
        var group = realmz.Children.OfType<NoNameGroupNode>().Single();
        var tab = (FileNode)group.Children.Single(c => c.Name == "tab");
        Assert.Equal("Realmz.img:Realmz:\t", tab.Source);
        Assert.True(DragOut.CanDragOut(tab));
        await tab.EnsureLoadedAsync();
        var str = (ResourceNode)tab.Children.Single().Children.Single();
        model.Selected = str;
        await model.PreviewTask;
        Assert.Equal("tab", Assert.IsType<StringForm>(model.Forms.Form).Text);

        // The group row previews its folder.
        model.Selected = group;
        await model.PreviewTask;
        Assert.Equal(PreviewKind.Folder, model.Preview.Kind);
        Assert.Equal("No name", model.Details.Heading);
        Assert.False(model.ExportActions.ExtractAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Options_rebuild_the_tree_keeping_expansion_and_selection()
    {
        var (model, input, store) = await Open();
        var realmz = Folder(input, "Realmz");
        realmz.IsExpanded = true;
        var group = realmz.Children.OfType<NoNameGroupNode>().Single();
        group.IsExpanded = true;
        var space = group.Children.Single(c => c.Name == "sp");
        model.Selected = space;

        model.TreeDisplay.GroupNoName = false;
        Assert.Equal((false, true), (store.Settings.GroupNoName, store.Settings.HideInvisible));
        Assert.DoesNotContain(realmz.Children, c => c is NoNameGroupNode);
        Assert.Same(space, model.Selected);
        Assert.Same(realmz, space.Parent);
        Assert.Equal(("(no name)", true, false), (space.Name, space.IsItalic, space.HasNameTokens));
        Assert.Equal(6, realmz.Children.Count);
        Assert.True(realmz.IsExpanded);

        model.TreeDisplay.GroupNoName = true;
        var regrouped = realmz.Children.OfType<NoNameGroupNode>().Single();
        Assert.Same(regrouped, space.Parent);
        Assert.Same(space, model.Selected);
        Assert.Equal("sp", space.Name);

        // A shown invisible file selected, then hidden: its folder is selected.
        model.TreeDisplay.HideInvisible = false;
        var icon = realmz.Children.OfType<FileNode>().Single(f => f.File.Name.ToMacRoman() == "Icon\r");
        model.Selected = icon;
        model.TreeDisplay.HideInvisible = true;
        Assert.Same(realmz, model.Selected);
        Assert.Equal((true, true), (store.Settings.GroupNoName, store.Settings.HideInvisible));
    }

    [Fact]
    public async Task Options_are_read_from_the_settings_and_apply_to_files_opened_later()
    {
        var (model, input, _) = await Open(new AppSettings(GroupNoName: false, HideInvisible: false));
        Assert.False(model.TreeDisplay.GroupNoName);
        Assert.Contains("Desktop DB", Names(input));
        Assert.DoesNotContain(Folder(input, "Realmz").Children, c => c is NoNameGroupNode);
        Assert.Equal(0, model.HiddenCount);
    }

    [Fact]
    public async Task Exports_and_details_count_every_file()
    {
        var (model, input, _) = await Open();
        var realmz = Folder(input, "Realmz");
        model.Selected = realmz;
        Assert.Contains(model.Details.Rows, r => r.Label == "Items" && r.Value == "7");
        var output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        model.FilePicker = new FolderPicker(output);
        await model.ExportActions.ExtractAllCommand.ExecuteAsync(null);
        Assert.StartsWith("7 resources from 7 files", model.Status, StringComparison.Ordinal);
    }

    private sealed class FolderPicker(string output) : IFilePicker
    {
        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(output);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<string> extensions) => Task.FromResult<string?>(null);
    }

    [Fact]
    public void Settings_are_kept_as_json()
    {
        var path = Path.Combine(folder, "ClassicMac", "settings.json");
        var store = new JsonSettingsStore(path);
        Assert.Equal(new AppSettings(), store.Load());                      // none yet: the defaults
        store.Save(new AppSettings(GroupNoName: false, HideInvisible: true));
        Assert.Equal(new AppSettings(GroupNoName: false), new JsonSettingsStore(path).Load());
        store.Save(new AppSettings(TypeCreatorDatabase: @"D:\TCDB\data.xlsx"));
        Assert.Contains("\"typeCreatorDatabase\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(@"D:\TCDB\data.xlsx", new JsonSettingsStore(path).Load().TypeCreatorDatabase);
        Assert.NotEqual(new AppSettings(), new AppSettings(TypeCreatorDatabase: "x"));
        File.WriteAllText(path, "{ not json");
        Assert.Equal(new AppSettings(), store.Load());                      // unreadable: the defaults
        Assert.EndsWith(Path.Combine("ClassicMac", "settings.json"), JsonSettingsStore.DefaultPath, StringComparison.Ordinal);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), JsonSettingsStore.DefaultPath, StringComparison.Ordinal);
    }
}

// The volume commands with hidden and grouped files.
public sealed class VolumeTreeEditTests : EditTestsBase
{
    [Fact]
    public async Task Volume_commands_see_hidden_and_grouped_files()
    {
        var path = TreeDisplayTests.Disk(folder);
        var dialogs = new Dialogs();
        var model = new MainViewModel { FilePicker = new Picker(folder), EditDialogs = dialogs };
        var input = (await model.OpenAsync(path))!;
        Assert.True(input.IsWritableHfs);
        var realmz = input.Children.OfType<FolderNode>().Single(f => f.Title == "Realmz");
        var group = realmz.Children.OfType<NoNameGroupNode>().Single();

        // A grouped file is deleted by its own name.
        model.Selected = group.Children.Single(c => c.Name == "tab");
        Assert.True(model.VolumeActions.DeleteItemCommand.CanExecute(null));
        await model.VolumeActions.DeleteItemCommand.ExecuteAsync(null);
        Assert.Same(realmz, model.Selected);
        Assert.Equal(3, group.Children.Count);
        Assert.DoesNotContain(Files(input), f => PathOf(f) == "Realmz:\t");

        // New files go into the folder (from the group row too); one with no name joins the group.
        model.Selected = group;
        Assert.True(model.VolumeActions.NewFileCommand.CanExecute(null));
        dialogs.NewFile = c => c with { Name = "\r\r" };
        await model.VolumeActions.NewFileCommand.ExecuteAsync(null);
        Assert.Contains(Files(input), f => PathOf(f) == "Realmz:\r\r");
        var made = Assert.IsType<FileNode>(model.Selected);
        Assert.Same(realmz.Children.OfType<NoNameGroupNode>().Single(), made.Parent);
        Assert.Equal("cr×2", made.Name);

        // Deleting the folder deletes the invisible Icon\r and the grouped files too.
        model.Selected = realmz;
        await model.VolumeActions.DeleteItemCommand.ExecuteAsync(null);
        Assert.DoesNotContain(input.Children, c => c == realmz);
        Assert.DoesNotContain(Files(input), f => PathOf(f).StartsWith("Realmz", StringComparison.Ordinal));
        Assert.Contains(Files(input), f => PathOf(f) == "Desktop DB");
    }

    // A file's Mac path as Mac OS Roman text (MacPath escapes control characters).
    private static string PathOf(MacFile file) => string.Join(":", file.FolderPath.Select(p => p.ToMacRoman()).Append(file.Name.ToMacRoman()));

    private static IReadOnlyList<MacFile> Files(InputNode input) =>
        HfsReader.Instance.Read(ForkData.FromBytes(input.EditedVolume!), new ContainerContext());
}
