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
        /// <summary>Raised whenever a value changes (including in its lists), for the live preview.</summary>
        public event EventHandler? Edited;

        protected void RaiseEdited() => Edited?.Invoke(this, EventArgs.Empty);

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            RaiseEdited();
        }

        // A list whose items' and own changes count as edits.
        protected void Watch<T>(ObservableCollection<T> list) where T : INotifyPropertyChanged
        {
            foreach (var item in list) item.PropertyChanged += (_, _) => RaiseEdited();
            list.CollectionChanged += (_, e) =>
            {
                foreach (var item in e.NewItems?.OfType<T>() ?? []) item.PropertyChanged += (_, _) => RaiseEdited();
                RaiseEdited();
            };
        }

        protected static MacRect Rect(decimal top, decimal left, decimal bottom, decimal right) =>
            new((short)top, (short)left, (short)bottom, (short)right);

        protected static void Move<T>(ObservableCollection<T> list, T item, int by)
        {
            var i = list.IndexOf(item);
            if (i >= 0 && i + by >= 0 && i + by < list.Count) list.Move(i, i + by);
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
                stages |= ((s.BoldItem == 2 ? 8 : 0) | (s.Drawn ? 4 : 0) | ((int)s.Sound & 3)) << ((s.Number - 1) * 4);
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
            foreach (var item in items) Items.Add(new DialogItemRow(item));
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

    /// <summary>One item of a <c>'MENU'</c>.</summary>
    public sealed partial class MenuItemRow : ObservableObject
    {
        public MenuItemRow(MenuItem item)
        {
            (text, enabled, icon, face) = (item.Text, item.Enabled, item.Icon, item.Face);
            key = item.KeyEquivalent is 0 ? "" : item.KeyKind is null ? MacRoman.ToChar(item.KeyEquivalent).ToString() : $"${item.KeyEquivalent:X2}";
            mark = item.Submenu is { } submenu ? $"menu {submenu}" : item.Mark is 0 ? "" : MacRoman.ToChar(item.Mark).ToString();
            (KeyByte, MarkByte) = (item.KeyEquivalent, item.Mark);
        }

        private byte KeyByte { get; }
        private byte MarkByte { get; }

        [ObservableProperty] private string text;
        [ObservableProperty] private bool enabled;
        [ObservableProperty] private decimal icon;
        [ObservableProperty] private decimal face;

        /// <summary>The Command key (one character), or a code as $1B (submenu), $1C (script), $1A/$1D/$1E (icon forms).</summary>
        [ObservableProperty] private string key;

        /// <summary>The mark character; for a submenu (key $1B) "menu N".</summary>
        [ObservableProperty] private string mark;

        public MenuItem ToItem()
        {
            byte key = Key.Trim() switch
            {
                "" => 0,
                var k when k.StartsWith('$') && byte.TryParse(k[1..], System.Globalization.NumberStyles.HexNumber, null, out var code) => code,
                var k when k.Length == 1 && MacRoman.TryGetByte(k[0], out var b) => b,
                _ => throw new ArgumentException($"“{Key}” is not a Command key (one character, or a code like $1B)."),
            };
            byte mark = Mark.Trim() switch
            {
                "" => 0,
                var m when m.StartsWith("menu ", StringComparison.Ordinal) && byte.TryParse(m[5..], out var id) => id,
                var m when m.Length == 1 && MacRoman.TryGetByte(m[0], out var b) => b,
                _ => throw new ArgumentException($"“{Mark}” is not a mark (one character, or “menu N” for a submenu)."),
            };
            return new MenuItem(Text, (byte)Icon, key, mark, (byte)Face, Enabled);
        }
    }

    /// <summary><c>'MENU'</c>: its title, flags and items.</summary>
    public sealed partial class MenuForm : DataForm
    {
        private readonly MenuResource menu;

        public MenuForm(Resource resource, MenuResource menu) : base(resource)
        {
            this.menu = menu;
            (id, definition, title, enabled) = (menu.Id, menu.Definition, menu.Title, menu.Enabled);
            foreach (var item in menu.Items) Items.Add(new MenuItemRow(item));
            Watch(Items);
        }

        [ObservableProperty] private decimal id;
        [ObservableProperty] private decimal definition;
        [ObservableProperty] private string title;
        [ObservableProperty] private bool enabled;

        public ObservableCollection<MenuItemRow> Items { get; } = [];

        [RelayCommand]
        private void Add() => Items.Add(new MenuItemRow(new MenuItem("Item", 0, 0, 0, 0, true)));

        [RelayCommand]
        private void Remove(MenuItemRow row) => Items.Remove(row);

        [RelayCommand]
        private void MoveUp(MenuItemRow row) => Move(Items, row, -1);

        public MenuResource ToMenu() => menu with
        {
            Id = (short)Id, Definition = (short)Definition, Title = Title, EnableFlags = Enabled ? menu.EnableFlags | 1 : menu.EnableFlags & ~1u,
            Items = Items.Select(i => i.ToItem()).ToList(),
        };

        public override byte[] BuildData() => InterfaceWriter.WriteMenu(ToMenu());
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

        /// <summary>The live preview of the form's menu, or null.</summary>
        [ObservableProperty]
        private MenuResource? formMenu;

        /// <summary>Why the form's values cannot be written, or null.</summary>
        [ObservableProperty]
        private string? formError;

        private void WatchForm(ResourceForm? form, ResourceNode? node)
        {
            FormDialog = null;
            FormMenu = null;
            FormError = null;
            if (form is not DataForm data || node is null) return;
            DialogSources? sources = null;
            void Update()
            {
                try
                {
                    var bytes = data.BuildData();
                    FormError = null;
                    if (data is MenuForm menu) FormMenu = menu.ToMenu();
                    else
                        FormDialog = InterfacePreviews.Dialog(node.Resource, bytes, node.Fork, DecodeOptions.Default with { ScreenDepth = ScreenDepth }, ReadOptions, [],
                            sources ??= DialogSources.From(Roots));
                }
                catch (ArgumentException e)
                {
                    FormError = e.Message;
                }
            }
            data.Edited += (_, _) => Update();
            Update();
        }
    }
}
