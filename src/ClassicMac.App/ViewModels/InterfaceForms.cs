using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Interface;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A form whose values write the whole resource (the interface templates): its bytes, for the edit and the live preview.</summary>
    public abstract partial class DataForm(Resource resource) : ResourceForm(resource)
    {
        protected static MacRect Rect(decimal top, decimal left, decimal bottom, decimal right) =>
            new((short)top, (short)left, (short)bottom, (short)right);

        protected static void Move<T>(ObservableCollection<T> list, T item, int by)
        {
            var i = list.IndexOf(item);
            if (i >= 0 && i + by >= 0 && i + by < list.Count)
            {
                list.Move(i, i + by);
            }
        }
    }

    /// <summary><c>'WIND'</c> and <c>'DLOG'</c>.</summary>
    public sealed partial class WindowForm : DataForm
    {
        public WindowForm(Resource resource, WindowTemplate window, bool dialog) : base(resource)
        {
            IsDialog = dialog;
            (top, left, bottom, right) = (window.Bounds.Top, window.Bounds.Left, window.Bounds.Bottom, window.Bounds.Right);
            (definition, visible, goAway, refCon, title, itemsId) = (window.Definition, window.Visible, window.GoAway, window.RefCon, window.Title, window.ItemsId ?? 0);
            (hasPosition, position) = (window.Position is not null, window.Position ?? 0);
        }

        public bool IsDialog { get; }

        [ObservableProperty] private decimal top;
        [ObservableProperty] private decimal left;
        [ObservableProperty] private decimal bottom;
        [ObservableProperty] private decimal right;
        [ObservableProperty] private decimal definition;
        [ObservableProperty] private bool visible;
        [ObservableProperty] private bool goAway;
        [ObservableProperty] private decimal refCon;
        [ObservableProperty] private string title;
        [ObservableProperty] private decimal itemsId;
        [ObservableProperty] private bool hasPosition;
        [ObservableProperty] private decimal position;

        public override byte[] BuildData() => InterfaceWriter.WriteWindow(new WindowTemplate(Rect(Top, Left, Bottom, Right), (short)Definition, Visible, GoAway,
            (int)RefCon, Title, IsDialog ? (short)ItemsId : null, HasPosition ? (ushort)Position : null), IsDialog);
    }

    /// <summary>One stage of an alert.</summary>
    public sealed partial class AlertStage(int number, int boldItem, bool drawn, int sound) : ObservableObject
    {
        public int Number { get; } = number;
        [ObservableProperty] private decimal boldItem = boldItem;
        [ObservableProperty] private bool drawn = drawn;
        [ObservableProperty] private decimal sound = sound;
    }

    /// <summary><c>'ALRT'</c>.</summary>
    public sealed partial class AlertForm : DataForm
    {
        public AlertForm(Resource resource, AlertTemplate alert) : base(resource)
        {
            (top, left, bottom, right) = (alert.Bounds.Top, alert.Bounds.Left, alert.Bounds.Bottom, alert.Bounds.Right);
            (itemsId, hasPosition, position) = (alert.ItemsId, alert.Position is not null, alert.Position ?? 0);
            for (var n = 1; n <= 4; n++)
            {
                var (bold, drawn, sound) = alert.Stage(n);
                Stages.Add(new AlertStage(n, bold, drawn, sound));
            }
            Watch(Stages);
        }

        [ObservableProperty] private decimal top;
        [ObservableProperty] private decimal left;
        [ObservableProperty] private decimal bottom;
        [ObservableProperty] private decimal right;
        [ObservableProperty] private decimal itemsId;
        [ObservableProperty] private bool hasPosition;
        [ObservableProperty] private decimal position;

        public ObservableCollection<AlertStage> Stages { get; } = [];

        public override byte[] BuildData()
        {
            var stages = 0;
            foreach (var s in Stages)
            {
                stages |= ((s.BoldItem == 2 ? 8 : 0) | (s.Drawn ? 4 : 0) | ((int)s.Sound & 3)) << ((s.Number - 1) * 4);
            }

            return InterfaceWriter.WriteAlert(new AlertTemplate(Rect(Top, Left, Bottom, Right), (short)ItemsId, (ushort)stages, HasPosition ? (ushort)Position : null));
        }
    }

    /// <summary>An item kind a dialog item can be.</summary>
    public sealed record ItemKind(int Type, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>One item of a <c>'DITL'</c>.</summary>
    public sealed partial class DialogItemRow : ObservableObject
    {
        public static ItemKind[] Kinds { get; } =
            [.. new[] { 4, 5, 6, 7, 8, 16, 32, 64, 0, 1 }.Select(t => new ItemKind(t, InterfaceNames.Item(t)))];

        public DialogItemRow(DialogItem item)
        {
            kind = Kinds.FirstOrDefault(k => k.Type == item.Type) ?? new ItemKind(item.Type, InterfaceNames.Item(item.Type));
            (top, left, bottom, right) = (item.Bounds.Top, item.Bounds.Left, item.Bounds.Bottom, item.Bounds.Right);
            (enabled, text, resourceId, Data) = (item.Enabled, item.Text ?? "", item.ResourceId ?? 0, item.Data);
        }

        public ReadOnlyMemory<byte> Data { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasText), nameof(HasResource))]
        private ItemKind kind;

        [ObservableProperty] private decimal top;
        [ObservableProperty] private decimal left;
        [ObservableProperty] private decimal bottom;
        [ObservableProperty] private decimal right;
        [ObservableProperty] private bool enabled;
        [ObservableProperty] private string text;
        [ObservableProperty] private decimal resourceId;

        public bool HasText => Kind.Type is 4 or 5 or 6 or 8 or 16;

        public bool HasResource => Kind.Type is 7 or 32 or 64;

        public DialogItem ToItem() => new(new MacRect((short)Top, (short)Left, (short)Bottom, (short)Right), Kind.Type, Enabled,
            HasText ? Text : null, HasResource ? (short)ResourceId : null, Data);
    }

    /// <summary><c>'DITL'</c>: its items.</summary>
    public sealed partial class DialogItemsForm : DataForm
    {
        public DialogItemsForm(Resource resource, IReadOnlyList<DialogItem> items) : base(resource)
        {
            foreach (var item in items)
            {
                Items.Add(new DialogItemRow(item));
            }

            Watch(Items);
        }

        public ObservableCollection<DialogItemRow> Items { get; } = [];

        [RelayCommand]
        private void Add() =>
            Items.Add(new DialogItemRow(new DialogItem(new MacRect(10, 10, 30, 90), 8, false, "Text", null, ReadOnlyMemory<byte>.Empty)));

        [RelayCommand]
        private void Remove(DialogItemRow row) => Items.Remove(row);

        [RelayCommand]
        private void MoveUp(DialogItemRow row) => Move(Items, row, -1);

        public override byte[] BuildData() => InterfaceWriter.WriteDialogItems(Items.Select(i => i.ToItem()).ToList());
    }

    /// <summary><c>'CNTL'</c>.</summary>
    public sealed partial class ControlForm : DataForm
    {
        public ControlForm(Resource resource, ControlTemplate control) : base(resource)
        {
            (top, left, bottom, right) = (control.Bounds.Top, control.Bounds.Left, control.Bounds.Bottom, control.Bounds.Right);
            (value, visible, maximum, minimum, definition, refCon, title) =
                (control.Value, control.Visible, control.Maximum, control.Minimum, control.Definition, control.RefCon, control.Title);
        }

        [ObservableProperty] private decimal top;
        [ObservableProperty] private decimal left;
        [ObservableProperty] private decimal bottom;
        [ObservableProperty] private decimal right;
        [ObservableProperty] private decimal value;
        [ObservableProperty] private bool visible;
        [ObservableProperty] private decimal maximum;
        [ObservableProperty] private decimal minimum;
        [ObservableProperty] private decimal definition;
        [ObservableProperty] private decimal refCon;
        [ObservableProperty] private string title;

        public override byte[] BuildData() => InterfaceWriter.WriteControl(new ControlTemplate(Rect(Top, Left, Bottom, Right), (short)Value, Visible,
            (short)Maximum, (short)Minimum, (short)Definition, (int)RefCon, Title));
    }

    public sealed partial class MainViewModel
    {
        /// <summary>The live preview of the form's dialog, alert or item list, or null.</summary>
        [ObservableProperty]
        private DialogPreview? formDialog;

        /// <summary>Why the form's values cannot be written, or null (the host's error line; Apply waits for it to go).</summary>
        [ObservableProperty]
        private string? formError;

        // The form's error follows its values; a dialog, alert or item list also gets a live preview of the dialog.
        private void WatchForm(ResourceForm? form, ResourceNode? node)
        {
            FormDialog = null;
            FormError = null;
            if (form is null || node is null)
            {
                return;
            }

            DialogSources? sources = null;
            void Update()
            {
                try
                {
                    var bytes = form.BuildData();
                    FormError = null;
                    if (form is DataForm and not MenuForm)
                    {
                        FormDialog = InterfacePreviews.Dialog(node.Resource, bytes, node.Fork, DecodeOptions.Default with { ScreenDepth = ScreenDepth }, ReadOptions, [],
                            sources ??= DialogSources.From(Roots));
                    }
                }
                catch (Exception e) when (e is ArgumentException or OverflowException)
                {
                    FormError = e.Message;
                }
            }
            form.Edited += (_, _) => Update();
            Update();
        }
    }
}
