using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Interface;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

// The selection's form: the typed or template editor, Apply, and the template switch.
public sealed partial class Forms(MainViewModel main) : ObservableObject
{
    /// <summary>The typed editor for the selection, or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyFormCommand))]
    private ResourceForm? form;

    public bool HasForm => Form is not null;

    partial void OnFormChanged(ResourceForm? value)
    {
        main.FormEditing.EditFormCommand.NotifyCanExecuteChanged();
        main.EditActions.SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasForm));
        main.FormEditing.HostForm(value);
    }

    internal void UpdateForm(NodeViewModel? node)
    {
        ResourceForm? typed = null, template = null;
        if (node is ResourceNode r && EditActions.FileOwner(r) is { } owner)
        {
            typed = ResourceForm.For(r.Resource, r.Fork, main.ReadOptions);
            template = main.TemplateFinder.TemplateFormFor(r, owner);
        }
        HasTemplateChoice = typed is not null && template is not null;
        var form = UseTemplate ? template ?? typed : typed ?? template;
        form?.MarkClean();
        // A draft can be saved (Save applies it first): Save's state follows the form's values.
        if (form is not null)
        {
            form.Edited += (_, _) => main.EditActions.SaveCommand.NotifyCanExecuteChanged();
        }

        Form = form;
        main.FormLivePreview.WatchForm(Form, node as ResourceNode);
    }

    private bool useTemplate;

    /// <summary>
    /// Whether the Edit tab shows a resource through its <c>TMPL</c> even when it has a form of its own. With an
    /// unapplied draft, a change asks about it first and is made once it is applied or discarded.
    /// </summary>
    public bool UseTemplate
    {
        get => useTemplate;
        set
        {
            if (useTemplate == value)
            {
                return;
            }

            if (main.Drafts.askingDraft || main.Drafts.HasDraft)
            {
                if (!main.Drafts.askingDraft)
                {
                    main.DraftTask = UseTemplateAfterDraftAsync(value);
                }
                // The check box (bound two-way) already shows the new value: told again, it shows the kept one.
                if (useTemplate != value)
                {
                    OnPropertyChanged(nameof(UseTemplate));
                    main.Drafts.Refuse(nameof(UseTemplate));
                }

                return;
            }
            SetProperty(ref useTemplate, value);
            UpdateForm(main.Selected);
        }
    }

    private async Task UseTemplateAfterDraftAsync(bool value)
    {
        if (await main.Drafts.ResolveDraftAsync())
        {
            UseTemplate = value;
        }
    }

    /// <summary>Whether the selection has both a form of its own and a template to choose between.</summary>
    [ObservableProperty]
    private bool hasTemplateChoice;


    private bool CanApplyForm() => Form is not null && main.Selected is ResourceNode && main.FormLivePreview.FormError is null;

    [RelayCommand(CanExecute = nameof(CanApplyForm))]
    internal void ApplyForm()
    {
        if (Form is not { } form || main.Selected is not ResourceNode node || EditActions.FileOwner(node) is not { } owner)
        {
            return;
        }

        IResourceEdit edit;
        try
        {
            edit = form.BuildEdit(main.EditActions.StateFor(owner).Session.Fork);
        }
        catch (ArgumentException e)
        {
            main.Status = e.Message;
            return;
        }
        var resource = form.Resource;
        // Applied: no longer a draft, so the selection the edit moves to is not refused.
        form.MarkClean();
        main.EditActions.Execute(owner, edit, () => resource);
        main.FormEditing.IsEditingForm = false;
        main.FormEditing.LastApplied = $"Applied · {main.EditActions.UndoTitle.Replace("_", "", StringComparison.Ordinal)} (Ctrl+Z)";
    }
}
