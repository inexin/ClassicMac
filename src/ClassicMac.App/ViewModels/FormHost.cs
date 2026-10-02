using System;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    // The read-then-edit host (design/boards/read-then-edit.md, E1): every resource form opens read only; Edit (the
    // header's button, or a double-click on one of the form's rows) switches it to inputs in place, in the Preview tab;
    // Cancel (Esc) drops the draft and Apply (Ctrl+Enter) makes one undoable edit, both returning to read only. Leaving
    // with unapplied values still asks (Drafts.cs).
    //
    // How a form plugs in (ResourceForm, ResourceForms.cs):
    //  - BuildData / BuildEdit: the bytes and the edit for its values (throw ArgumentException with a message for values
    //    the resource cannot hold: it becomes FormError, shown in the footer, and Apply is disabled until it is gone).
    //  - IsEditing (set by the host): its DataTemplate (in MainWindow.axaml, under the host's ContentControl) shows text
    //    when false and inputs when true.
    //  - HasReadOnlyView: true once its template has a read-only view; false keeps the resource's plain preview on show
    //    until Edit (the forms not yet moved to the host).
    //  - EditHint: the footer's hint while editing.
    //  - SelectRow(row): a double-click on a read-only row edits with that row selected; the view passes the row's
    //    DataContext as EditFormCommand's parameter (FormHostView marks rows with the "form-row" class).
    //  - A live preview of its own, if any, follows its Edited event (MenuForm.Preview).
    public sealed partial class MainViewModel
    {
        /// <summary>Whether the selection's form is open for editing (in the Preview tab).</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(EditFormCommand), nameof(CancelFormCommand))]
        [NotifyPropertyChangedFor(nameof(ShowsForm), nameof(FormPreviewTitle))]
        private bool isEditingForm;

        /// <summary>
        /// After an Apply, until the selection changes: "Applied · Undo Edit 'MENU' 129 (Ctrl+Z)", in the read-only footer.
        /// </summary>
        [ObservableProperty]
        private string? lastApplied;

        /// <summary>Whether the Preview tab shows the host (the form) rather than the resource's plain preview.</summary>
        public bool ShowsForm => Form is { } form && (IsEditingForm || form.HasReadOnlyView);

        /// <summary>The preview panel's title: "Preview", or "Live preview · unapplied changes" while editing.</summary>
        public string FormPreviewTitle => IsEditingForm ? "Live preview · unapplied changes" : "Preview";

        /// <summary>The read-only footer's note.</summary>
        public string FormReadOnlyNote => "Read only. Press Edit or double-click a row to change it.";

        /// <summary>The editing footer's hint: the form's own.</summary>
        public string FormHint => Form?.EditHint ?? "";

        private bool CanEditForm(object? row) => Form is not null && Selected is ResourceNode && !IsEditingForm;

        /// <summary>Opens the selection's form for editing in the Preview tab; with a row (a double-click), that row selected.</summary>
        [RelayCommand(CanExecute = nameof(CanEditForm))]
        private void EditForm(object? row)
        {
            if (!CanEditForm(row))
            {
                return;
            }

            Form!.SelectRow(row);
            IsEditingForm = true;
            LastApplied = null;
            SelectedTab = 1;
        }

        /// <summary>Drops the form's unapplied values and ends editing.</summary>
        [RelayCommand(CanExecute = nameof(IsEditingForm))]
        private void CancelForm()
        {
            DiscardDraft();
            IsEditingForm = false;
        }

        partial void OnIsEditingFormChanged(bool value)
        {
            if (Form is { } form)
            {
                form.IsEditing = value;
            }
        }

        // A new form (another selection, a discard, the template choice) takes the host's mode.
        private void HostForm(ResourceForm? form)
        {
            if (form is not null)
            {
                form.IsEditing = IsEditingForm;
            }

            OnPropertyChanged(nameof(ShowsForm));
            OnPropertyChanged(nameof(FormHint));
        }

        partial void OnFormErrorChanged(string? value) => ApplyFormCommand.NotifyCanExecuteChanged();

        /// <summary>A dialog's or alert's item list link: selects that 'DITL' among its file's resources.</summary>
        [RelayCommand]
        private void ShowItemList(object? form)
        {
            var id = form switch
            {
                AlertForm alert => (short)alert.ItemsId,
                WindowForm { IsDialog: true } dialog => (short)dialog.ItemsId,
                _ => (short?)null,
            };
            if (id is null || Selected is not ResourceNode { Parent.Parent: { } file })
            {
                return;
            }

            var target = file.Children.OfType<ResourceTypeNode>().FirstOrDefault(t => t.Type.ToString() == "DITL")?
                .Children.OfType<ResourceNode>().FirstOrDefault(r => r.Resource.Id == id);
            if (target is not null)
            {
                Selected = target;
            }
        }
    }
}
