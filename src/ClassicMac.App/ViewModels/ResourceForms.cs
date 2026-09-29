using System;
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

        /// <summary>The edit that stores the form's values (throws <see cref="ArgumentException"/> for values the resource cannot hold).</summary>
        public abstract IResourceEdit BuildEdit(ResourceFork fork);

        /// <summary>A form for the resource's type, or null when there is none.</summary>
        public static ResourceForm? For(Resource resource, ResourceFork fork, ReadOptions readOptions)
        {
            var data = ResourceDecompression.Default.GetData(resource, fork, readOptions, []).Span;
            return resource.Type.ToString() switch
            {
                "STR " => new StringForm(resource, TextResources.ReadString(data)),
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

        public override IResourceEdit BuildEdit(ResourceFork fork) => new SetResourceData(Resource, TextResources.WriteString(Text), $"Edit {Resource}");
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

        public override IResourceEdit BuildEdit(ResourceFork fork) =>
            new SetResourceData(Resource, TextResources.WriteStringList(Strings.Select(s => s.Text).ToList()), $"Edit {Resource}");
    }

    /// <summary><c>'TEXT'</c>, with its <c>'styl'</c> kept in step when there is one.</summary>
    public sealed partial class TextForm(Resource resource, byte[] data, (Resource Resource, byte[] Data)? styl) : ResourceForm(resource)
    {
        [ObservableProperty]
        private string text = TextResources.ReadText(data);

        public bool HasStyles => styl is not null;

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

        public override IResourceEdit BuildEdit(ResourceFork fork) =>
            new SetResourceData(Resource, new VersionResource((int)Major, (int)Minor, (int)BugFix, Stage.Value, (int)NonRelease, (short)Region,
                ShortVersion, LongVersion).Write(), $"Edit {Resource}");
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
            Form = node is ResourceNode r && FileOwner(r) is { } owner
                ? ResourceForm.For(r.Resource, r.Fork, ReadOptions) ?? TemplateFormFor(r, owner)
                : null;
            WatchForm(Form, node as ResourceNode);
        }

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
            Execute(owner, edit, () => resource);
        }
    }
}
