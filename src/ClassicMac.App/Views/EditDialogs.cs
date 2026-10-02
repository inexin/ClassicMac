using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views
{
    // The editing dialogs (DialogViews builds them in one frame), shown modal over the window.
    internal sealed class EditDialogs(Window owner) : IEditDialogs
    {
        private async Task<T> Show<T>(Dialog<T> dialog)
        {
            await dialog.Window.ShowDialog(owner);
            return dialog.Result;
        }

        public Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew) => Show(DialogViews.ResourceInfo(title, initial, isNew));

        public Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial) =>
            Show<ImportChoice?>(DialogViews.Import(fileName, types, initial));

        public Task<NewFileChoice?> NewFileAsync(string title, NewFileChoice initial) => Show(DialogViews.NewFile(title, initial));

        public Task<string?> NewFolderAsync(string initial) => Show(DialogViews.NewFolder(initial));

        public Task<SaveChanges> AskSaveChangesAsync(string fileName) => Show(DialogViews.SaveChanges(fileName));

        public Task<DraftChoice> AskApplyDraftAsync(string what, string? error) => Show(DialogViews.ApplyDraft(what, error));

        public Task<bool> ConfirmAsync(string title, string message) => Show(DialogViews.Confirm(title, message));
    }
}
