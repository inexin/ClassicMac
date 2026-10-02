using System;
using System.Linq;
using System.Threading.Tasks;

namespace ClassicMac.App.ViewModels
{
    // Unapplied edits (a form's values that differ from the resource's, or bytes changed in the hex view): before the
    // selection moves, an undo or redo, a resource command, or a close, the user applies, discards or keeps them.
    public sealed partial class MainViewModel
    {
        private bool askingDraft;

        /// <summary>
        /// Raised with the property's name when a change to <see cref="Selected"/> or <see cref="UseTemplate"/> is refused
        /// while a draft is asked about; the view sets its control back once it is done changing.
        /// </summary>
        public event EventHandler<string>? ChangeRefused;

        private void Refuse(string property)
        {
            OnPropertyChanged(property);
            ChangeRefused?.Invoke(this, property);
        }

        /// <summary>Whether a form or the hex view holds edits not yet applied.</summary>
        public bool HasDraft => CurrentDraft is not null;

        // The resource with unapplied edits ("'STR#' 128") and why they cannot be applied, or null when there are none.
        private (string What, string? Error)? CurrentDraft
        {
            get
            {
                if (HexEdit is { IsModified: true } && hexEditTarget is { } target)
                {
                    return (Name(target.Resource), null);
                }

                if (Form is { } form && form.Draft is (true, var error))
                {
                    return (Name(form.Resource), error);
                }

                return null;
            }
        }

        private static string Name(ClassicMac.Resources.Resource resource) => $"'{resource.Type}' {resource.Id}";

        /// <summary>
        /// Asks what to do with the unapplied edits, if any, and does it: true when there were none or they were applied
        /// or discarded, false when the user cancelled (or chose Apply for edits that cannot be applied). Without
        /// <see cref="EditDialogs"/> the edits are discarded.
        /// </summary>
        internal async Task<bool> ResolveDraftAsync()
        {
            if (askingDraft)
            {
                return false;
            }

            if (CurrentDraft is not { } draft)
            {
                return true;
            }

            var choice = DraftChoice.Discard;
            if (EditDialogs is not null)
            {
                askingDraft = true;
                try
                {
                    choice = await EditDialogs.AskApplyDraftAsync(draft.What, draft.Error);
                }
                finally
                {
                    askingDraft = false;
                }
            }
            switch (choice)
            {
                case DraftChoice.Apply when draft.Error is null:
                    if (HexEdit is { IsModified: true })
                    {
                        ApplyHexEdit();
                    }
                    else
                    {
                        ApplyForm();
                    }

                    return !HasDraft;
                case DraftChoice.Discard:
                    DiscardDraft();
                    return true;
                default:
                    return false;
            }
        }

        // Drops unapplied edits: the hex view's bytes, the form's values read again from the resource.
        private void DiscardDraft()
        {
            if (HexEdit is { IsModified: true })
            {
                DiscardHexEdit();
            }
            else if (Form is { Draft.IsDraft: true })
            {
                UpdateForm(Selected);
            }
        }

        // A selection refused while a draft was pending: made once the draft is applied or discarded.
        private async Task SelectAfterDraftAsync(NodeViewModel? target)
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            Selected = Current(target);
        }

        // An applied edit rebuilds its file's type nodes: a type or resource node chosen before it is found again.
        private static NodeViewModel? Current(NodeViewModel? node)
        {
            var typeNode = node switch
            {
                ResourceNode r => r.Parent as ResourceTypeNode,
                ResourceTypeNode t => t,
                _ => null,
            };
            if (typeNode?.Parent is not { } owner || owner.Children.Contains(typeNode))
            {
                return node;
            }

            var now = owner.Children.OfType<ResourceTypeNode>().FirstOrDefault(t => t.Type == typeNode.Type);
            if (node is ResourceNode resource && now is not null)
            {
                return (NodeViewModel?)now.Children.OfType<ResourceNode>().FirstOrDefault(n => n.Resource == resource.Resource) ?? now;
            }

            return now ?? owner;
        }
    }
}
