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
public sealed partial class Forms(IAppSelection appSelection, IAppServices appServices, IAppParts appParts) : ObservableObject
{
    /// <summary>The typed editor for the selection, or null.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyFormCommand))]
    private ResourceForm? form;

    public bool HasForm => Form is not null;

    partial void OnFormChanged(ResourceForm? value)
    {
        appParts.FormEditing.EditFormCommand.NotifyCanExecuteChanged();
        appParts.EditActions.SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasForm));
        appParts.FormEditing.HostForm(value);
    }

    internal void UpdateForm(NodeViewModel? node)
    {
        ResourceForm? typed = null, template = null;
        if (node is ResourceNode r && EditActions.FileOwner(r) is { } owner)
        {
            typed = ResourceForm.For(r.Resource, r.Fork, appServices.ReadOptions);
            template = appParts.TemplateFinder.TemplateFormFor(r, owner);
        }
        HasTemplateChoice = typed is not null && template is not null;
        var form = UseTemplate ? template ?? typed : typed ?? template;
        form?.MarkClean();
        // A draft can be saved (Save applies it first): Save's state follows the form's values.
        if (form is not null)
        {
            form.Edited += (_, _) => appParts.EditActions.SaveCommand.NotifyCanExecuteChanged();
        }

        Form = form;
        appParts.FormLivePreview.WatchForm(Form, node as ResourceNode);
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

            if (appParts.Drafts.askingDraft || appParts.Drafts.HasDraft)
            {
                if (!appParts.Drafts.askingDraft)
                {
                    appSelection.DraftTask = UseTemplateAfterDraftAsync(value);
                }
                // The check box (bound two-way) already shows the new value: told again, it shows the kept one.
                if (useTemplate != value)
                {
                    OnPropertyChanged(nameof(UseTemplate));
                    appParts.Drafts.Refuse(nameof(UseTemplate));
                }

                return;
            }
            SetProperty(ref useTemplate, value);
            UpdateForm(appSelection.Selected);
        }
    }

    private async Task UseTemplateAfterDraftAsync(bool value)
    {
        if (await appParts.Drafts.ResolveDraftAsync())
        {
            UseTemplate = value;
        }
    }

    /// <summary>Whether the selection has both a form of its own and a template to choose between.</summary>
    [ObservableProperty]
    private bool hasTemplateChoice;


    private bool CanApplyForm() => Form is not null && appSelection.Selected is ResourceNode && appParts.FormLivePreview.FormError is null;

    [RelayCommand(CanExecute = nameof(CanApplyForm))]
    internal void ApplyForm()
    {
        if (Form is not { } form || appSelection.Selected is not ResourceNode node || EditActions.FileOwner(node) is not { } owner)
        {
            return;
        }

        IResourceEdit edit;
        try
        {
            edit = form.BuildEdit(appParts.EditActions.StateFor(owner).Session.Fork);
        }
        catch (ArgumentException e)
        {
            appServices.Status = e.Message;
            return;
        }
        var resource = form.Resource;
        // Applied: no longer a draft, so the selection the edit moves to is not refused.
        form.MarkClean();
        appParts.EditActions.Execute(owner, edit, () => resource);
        appParts.FormEditing.IsEditingForm = false;
        appParts.FormEditing.LastApplied = $"Applied · {appParts.EditActions.UndoTitle.Replace("_", "", StringComparison.Ordinal)} (Ctrl+Z)";
    }
}
