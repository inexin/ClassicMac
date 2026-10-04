using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Dialogs;

// The editing dialogs (DialogViews builds them in one frame), shown modal over the window, or over the dialog that
// opened them (Resize's Defragment first…).
internal sealed class EditDialogs(Window owner) : IEditDialogs
{
    private readonly List<Window> open = [];

    private async Task<T> Show<T>(Dialog<T> dialog)
    {
        var parent = open.Count > 0 ? open[^1] : owner;
        open.Add(dialog.Window);
        try
        {
            await dialog.Window.ShowDialog(parent);
        }
        finally
        {
            open.Remove(dialog.Window);
        }

        return dialog.Result;
    }

    public Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew, DialogSubject? subject) =>
        Show(DialogViews.ResourceInfo(title, initial, isNew, subject));

    public Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial, ImportSource source) =>
        Show<ImportChoice?>(DialogViews.Import(fileName, types, initial, source));

    public Task<NewFileChoice?> NewFileAsync(string title, NewFileChoice initial) => Show(DialogViews.NewFile(title, initial));

    public Task<string?> NewFolderAsync(string initial) => Show(DialogViews.NewFolder(initial));

    public Task<SaveChanges> AskSaveChangesAsync(string fileName, string edited) => Show(DialogViews.SaveChanges(fileName, edited));

    public Task<DraftChoice> AskApplyDraftAsync(string what, string? error) => Show(DialogViews.ApplyDraft(what, error));

    public Task<bool> ConfirmAsync(string title, string message) => Show(DialogViews.Confirm(title, message));

    public Task FirstAidAsync(FirstAidViewModel model) => Show(DialogViews.FirstAid(model));

    public Task ResizeAsync(ResizeViewModel model) => Show(DialogViews.Resize(model));

    public Task DefragmentAsync(DefragmentViewModel model) => Show(DialogViews.Defragment(model));
}
