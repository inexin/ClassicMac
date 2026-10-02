using System;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Interface;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A typed editor for one resource: its fields, and the edit that writes them back.</summary>
    public abstract partial class ResourceForm : ObservableObject
    {
        protected ResourceForm(Resource resource) => Resource = resource;

        public Resource Resource { get; }

        /// <summary>The resource's bytes for the form's values (throws <see cref="ArgumentException"/> for values it cannot hold).</summary>
        public abstract byte[] BuildData();

        /// <summary>The edit that stores the form's values (throws <see cref="ArgumentException"/> for values the resource cannot hold).</summary>
        public virtual IResourceEdit BuildEdit(ResourceFork fork) => new SetResourceData(Resource, BuildData(), $"Edit {Resource}");

        private (byte[]? Data, string? Error) clean;

        /// <summary>Takes the values as they are as the unedited ones (when the form is made, and once they are applied).</summary>
        internal void MarkClean() => clean = Snapshot();

        /// <summary>
        /// Whether the values differ from the unedited ones (an unapplied draft), and why they cannot be written when so.
        /// Values changed back are no draft.
        /// </summary>
        internal (bool IsDraft, string? Error) Draft
        {
            get
            {
                var now = Snapshot();
                var isDraft = now.Data is { } data ? clean.Data is not { } old || !data.AsSpan().SequenceEqual(old) : now.Error != clean.Error;
                return (isDraft, now.Error);
            }
        }

        private (byte[]? Data, string? Error) Snapshot()
        {
            try
            {
                return (BuildData(), null);
            }
            catch (Exception e) when (e is ArgumentException or OverflowException)
            {
                return (null, e.Message);
            }
        }

        /// <summary>A form for the resource's type, or null when there is none.</summary>
        public static ResourceForm? For(Resource resource, ResourceFork fork, ReadOptions readOptions)
        {
            var data = ResourceDecompression.Default.GetData(resource, fork, readOptions, []);
            return resource.Type.ToString() switch
            {
                "STR " => new StringForm(resource, TextResources.ReadString(data.Span)),
                "STR#" => new StringListForm(resource, TextResources.ReadStringList(data)),
                "TEXT" => new TextForm(resource, data.ToArray(), fork.Find(FourCC.FromString("styl"), resource.Id) is { } styl
                    ? (styl, ResourceDecompression.Default.GetData(styl, fork, readOptions, []).ToArray())
                    : null),
                "vers" when VersionResource.Read(data) is { } version => new VersionForm(resource, version),
                "WIND" => new WindowForm(resource, InterfaceResources.ReadWindow(data, false, DecodeOptions.Default, [], ""), false),
                "DLOG" => new WindowForm(resource, InterfaceResources.ReadWindow(data, true, DecodeOptions.Default, [], ""), true),
                "ALRT" => new AlertForm(resource, InterfaceResources.ReadAlert(data, [], "")),
                "DITL" => new DialogItemsForm(resource, InterfaceResources.ReadDialogItems(data, DecodeOptions.Default, [], "")),
                "MENU" => new MenuForm(resource, InterfaceResources.ReadMenu(data, DecodeOptions.Default, [], "")),
                "CNTL" => new ControlForm(resource, InterfaceResources.ReadControl(data, DecodeOptions.Default, [], "")),
                _ => null,
            };
        }
    }

    /// <summary><c>'STR '</c>: one string.</summary>
    public sealed partial class StringForm(Resource resource, string text) : ResourceForm(resource)
    {
        [ObservableProperty]
        private string text = text;

        public override byte[] BuildData() => TextResources.WriteString(Text);
    }

    /// <summary>One string of a <c>'STR#'</c>.</summary>
    public sealed partial class StringItem(string text) : ObservableObject
    {
        [ObservableProperty]
        private string text = text;
    }

    /// <summary><c>'STR#'</c>: a list of strings.</summary>
    public sealed partial class StringListForm : ResourceForm
    {
        public StringListForm(Resource resource, System.Collections.Generic.IReadOnlyList<string> strings) : base(resource)
        {
            foreach (var s in strings) Strings.Add(new StringItem(s));
        }

        public ObservableCollection<StringItem> Strings { get; } = [];

        [RelayCommand]
        private void Add() => Strings.Add(new StringItem(""));

        [RelayCommand]
        private void Remove(StringItem item) => Strings.Remove(item);

        [RelayCommand]
        private void MoveUp(StringItem item)
        {
            var i = Strings.IndexOf(item);
            if (i > 0) Strings.Move(i, i - 1);
        }

        public override byte[] BuildData() => TextResources.WriteStringList(Strings.Select(s => s.Text).ToList());
    }

    /// <summary><c>'TEXT'</c>, with its <c>'styl'</c> kept in step when there is one.</summary>
    public sealed partial class TextForm(Resource resource, byte[] data, (Resource Resource, byte[] Data)? styl) : ResourceForm(resource)
    {
        [ObservableProperty]
        private string text = TextResources.ReadText(data);

        public bool HasStyles => styl is not null;

        // The 'styl' follows from the text, so the text's bytes are the draft's.
        public override byte[] BuildData() => TextResources.WriteText(data, styl?.Data ?? [], Text, styl is not null).Text;

        public override IResourceEdit BuildEdit(ResourceFork fork)
        {
            var (bytes, stylBytes) = TextResources.WriteText(data, styl?.Data ?? [], Text, styl is not null);
            var edit = new SetResourceData(Resource, bytes, $"Edit {Resource}");
            return styl is not { } s || stylBytes is null ? edit : new CompoundEdit($"Edit {Resource}", edit, new SetResourceData(s.Resource, stylBytes));
        }
    }

    /// <summary><c>'vers'</c>: the version, stage, region and the two strings.</summary>
    public sealed partial class VersionForm : ResourceForm
    {
        public VersionForm(Resource resource, VersionResource version) : base(resource)
        {
            (major, minor, bugFix, nonRelease, region, shortVersion, longVersion) =
                (version.Major, version.Minor, version.BugFix, version.NonRelease, version.Region, version.ShortVersion, version.LongVersion);
            stage = Stages.FirstOrDefault(s => s.Value == version.Stage) ?? Stages[^1];
        }

        public sealed record StageChoice(byte Value, string Name)
        {
            public override string ToString() => Name;
        }

        public static StageChoice[] Stages { get; } = [new(0x20, "development"), new(0x40, "alpha"), new(0x60, "beta"), new(0x80, "final")];

        [ObservableProperty] private decimal major;
        [ObservableProperty] private decimal minor;
        [ObservableProperty] private decimal bugFix;
        [ObservableProperty] private StageChoice stage;
        [ObservableProperty] private decimal nonRelease;
        [ObservableProperty] private decimal region;
        [ObservableProperty] private string shortVersion;
        [ObservableProperty] private string longVersion;

        public override byte[] BuildData() =>
            new VersionResource((int)Major, (int)Minor, (int)BugFix, Stage.Value, (int)NonRelease, (short)Region, ShortVersion, LongVersion).Write();
    }

    public sealed partial class MainViewModel
    {
        /// <summary>The typed editor for the selection, or null.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyFormCommand))]
        private ResourceForm? form;

        public bool HasForm => Form is not null;

        partial void OnFormChanged(ResourceForm? value) => OnPropertyChanged(nameof(HasForm));

        private void UpdateForm(NodeViewModel? node)
        {
            ResourceForm? typed = null, template = null;
            if (node is ResourceNode r && FileOwner(r) is { } owner)
            {
                typed = ResourceForm.For(r.Resource, r.Fork, ReadOptions);
                template = TemplateFormFor(r, owner);
            }
            HasTemplateChoice = typed is not null && template is not null;
            var form = UseTemplate ? template ?? typed : typed ?? template;
            form?.MarkClean();
            // A draft can be saved (Save applies it first): Save's state follows the form's values.
            if (form is not null) form.PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
            if (form is DataForm data) data.Edited += (_, _) => SaveCommand.NotifyCanExecuteChanged();
            Form = form;
            WatchForm(Form, node as ResourceNode);
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
                if (useTemplate == value) return;
                if (askingDraft || HasDraft)
                {
                    if (!askingDraft) DraftTask = UseTemplateAfterDraftAsync(value);
                    // The check box (bound two-way) already shows the new value: told again, it shows the kept one.
                    if (useTemplate != value) Refuse(nameof(UseTemplate));
                    return;
                }
                SetProperty(ref useTemplate, value);
                UpdateForm(Selected);
            }
        }

        private async Task UseTemplateAfterDraftAsync(bool value)
        {
            if (await ResolveDraftAsync()) UseTemplate = value;
        }

        /// <summary>Whether the selection has both a form of its own and a template to choose between.</summary>
        [ObservableProperty]
        private bool hasTemplateChoice;


        private bool CanApplyForm() => Form is not null && Selected is ResourceNode;

        [RelayCommand(CanExecute = nameof(CanApplyForm))]
        private void ApplyForm()
        {
            if (Form is not { } form || Selected is not ResourceNode node || FileOwner(node) is not { } owner) return;
            IResourceEdit edit;
            try
            {
                edit = form.BuildEdit(StateFor(owner).Session.Fork);
            }
            catch (ArgumentException e)
            {
                Status = e.Message;
                return;
            }
            var resource = form.Resource;
            // Applied: no longer a draft, so the selection the edit moves to is not refused.
            form.MarkClean();
            Execute(owner, edit, () => resource);
        }
    }
}
