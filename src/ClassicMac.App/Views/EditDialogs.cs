using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassicMac.App.ViewModels;
using ClassicMac.Resources;

namespace ClassicMac.App.Views
{
    // The editing dialogs, built in code: Get Info / New Resource, save changes, yes/no, and hex editing.
    internal sealed class EditDialogs(Window owner) : IEditDialogs
    {
        private static readonly (ResourceAttributes Flag, string Label)[] Flags =
        [
            (ResourceAttributes.SystemHeap, "System heap"), (ResourceAttributes.Purgeable, "Purgeable"), (ResourceAttributes.Locked, "Locked"),
            (ResourceAttributes.Protected, "Protected"), (ResourceAttributes.Preload, "Preload"),
        ];

        public async Task<ResourceInfo?> ResourceInfoAsync(string title, ResourceInfo initial, bool isNew)
        {
            var type = new TextBox { Text = initial.Type, IsReadOnly = !isNew, MaxLength = 16, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            var id = new NumericUpDown { Value = initial.Id, Minimum = short.MinValue, Maximum = short.MaxValue, Increment = 1, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            var name = new TextBox { Text = initial.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            var boxes = Flags.Select(f => new CheckBox { Content = f.Label, IsChecked = (initial.Attributes & f.Flag) != 0 }).ToList();
            var compressed = (initial.Attributes & ResourceAttributes.Compressed) != 0;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), RowSpacing = 8 };
            void Row(int row, string label, Control control)
            {
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(text, row);
                Grid.SetRow(control, row);
                Grid.SetColumn(control, 1);
                grid.Children.Add(text);
                grid.Children.Add(control);
            }
            Row(0, "Type", type);
            Row(1, "ID", id);
            Row(2, "Name", name);
            var flags = new WrapPanel();
            foreach (var box in boxes) flags.Children.Add(new Border { Child = box, Margin = new Thickness(0, 0, 12, 0) });
            if (compressed) flags.Children.Add(new TextBlock { Text = "Compressed", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
            Row(3, "Attributes", flags);
            var ok = await Show(title, grid, "OK");
            if (!ok) return null;
            var attributes = compressed ? ResourceAttributes.Compressed : ResourceAttributes.None;
            for (var i = 0; i < Flags.Length; i++)
                if (boxes[i].IsChecked == true) attributes |= Flags[i].Flag;
            return new ResourceInfo(type.Text ?? "", (short)Math.Clamp(id.Value ?? 0, short.MinValue, short.MaxValue), name.Text ?? "", attributes);
        }

        public async Task<ImportChoice?> ImportAsync(string fileName, IReadOnlyList<string> types, ImportChoice initial)
        {
            var type = new ComboBox { ItemsSource = types, SelectedItem = initial.Type, MinWidth = 300 };
            var id = new NumericUpDown { Value = initial.Id, Minimum = short.MinValue, Maximum = short.MaxValue, Increment = 1, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
            var name = new TextBox { Text = initial.Name, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 8 };
            var rows = new (string Label, Control Control)[] { ("Make", type), ("ID", id), ("Name", name) };
            for (var row = 0; row < rows.Length; row++)
            {
                var text = new TextBlock { Text = rows[row].Label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(text, row);
                Grid.SetRow(rows[row].Control, row);
                Grid.SetColumn(rows[row].Control, 1);
                grid.Children.Add(text);
                grid.Children.Add(rows[row].Control);
            }
            var note = new TextBlock { Classes = { "muted" }, Text = "A resource of that type and ID has its data replaced.", TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
            if (!await Show($"Import “{fileName}”", new StackPanel { Spacing = 10, Children = { grid, note } }, "Import")) return null;
            return new ImportChoice(type.SelectedItem as string ?? initial.Type, (short)Math.Clamp(id.Value ?? 0, short.MinValue, short.MaxValue), name.Text ?? "");
        }

        public async Task<NewFileChoice?> NewFileAsync(string title, NewFileChoice initial)
        {
            var name = new TextBox { Text = initial.Name, MaxLength = 31, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            var type = new TextBox { Text = initial.Type, MaxLength = 4, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            var creator = new TextBox { Text = initial.Creator, MaxLength = 4, Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 8 };
            var rows = new (string Label, Control Control)[] { ("Name", name), ("Type", type), ("Creator", creator) };
            for (var row = 0; row < rows.Length; row++)
            {
                var text = new TextBlock { Text = rows[row].Label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(text, row);
                Grid.SetRow(rows[row].Control, row);
                Grid.SetColumn(rows[row].Control, 1);
                grid.Children.Add(text);
                grid.Children.Add(rows[row].Control);
            }
            var note = new TextBlock { Classes = { "muted" }, Text = "Written to the image with File ▸ Save As ▸ HFS Volume Image.", TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
            if (!await Show(title, new StackPanel { Spacing = 10, Children = { grid, note } }, "Create")) return null;
            return new NewFileChoice(name.Text ?? "", type.Text ?? "", creator.Text ?? "");
        }

        public async Task<string?> NewFolderAsync(string initial)
        {
            var name = new TextBox { Text = initial, MaxLength = 31, Width = 300 };
            var body = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Name" }, name } };
            return await Show("New Folder", body, "Create") ? name.Text ?? "" : null;
        }

        public async Task<SaveChanges> AskSaveChangesAsync(string fileName)
        {
            var result = SaveChanges.Cancel;
            var dialog = MakeWindow("Unsaved changes");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var (label, choice) in new[] { ("Save", SaveChanges.Save), ("Don't Save", SaveChanges.Discard), ("Cancel", SaveChanges.Cancel) })
            {
                var button = new Button { Content = label, IsDefault = choice == SaveChanges.Save, IsCancel = choice == SaveChanges.Cancel };
                button.Click += (_, _) => { result = choice; dialog.Close(); };
                buttons.Children.Add(button);
            }
            dialog.Content = Layout(new TextBlock { Text = $"Save the changes to “{fileName}”?", TextWrapping = TextWrapping.Wrap }, buttons);
            await dialog.ShowDialog(owner);
            return result;
        }

        // An alert: the question and its detail; Discard on the left, Cancel (Esc) and Apply (Enter) on the right. With an
        // error Apply is unavailable and the detail names it.
        public async Task<DraftChoice> AskApplyDraftAsync(string what, string? error)
        {
            var result = DraftChoice.Cancel;
            var dialog = MakeWindow("Unapplied changes");
            Button Choice(string label, DraftChoice choice)
            {
                var button = new Button { Content = label };
                button.Click += (_, _) => { result = choice; dialog.Close(); };
                return button;
            }
            var discard = Choice("Discard", DraftChoice.Discard);
            var cancel = Choice("Cancel", DraftChoice.Cancel);
            var apply = Choice("Apply", DraftChoice.Apply);
            cancel.IsCancel = true;
            apply.IsDefault = error is null;
            apply.IsEnabled = error is null;
            var buttons = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(discard, Dock.Left);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, apply } };
            DockPanel.SetDock(right, Dock.Right);
            buttons.Children.Add(discard);
            buttons.Children.Add(right);
            var detail = error is null
                ? "You edited this resource but haven't applied the changes."
                : $"You edited this resource but the changes can't be applied: {error}";
            dialog.Content = Layout(new StackPanel
            {
                Spacing = 6, MaxWidth = 420,
                Children =
                {
                    new TextBlock { Text = $"Apply your changes to {what}?", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = detail, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
                },
            }, buttons);
            await dialog.ShowDialog(owner);
            return result;
        }

        public Task<bool> ConfirmAsync(string title, string message) =>
            Show(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, "Yes", "No");

        public async Task<byte[]?> EditHexAsync(string title, byte[] data)
        {
            var box = new TextBox
            {
                Text = Format(data), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = AppFonts.Mono,
                Width = 560, Height = 360,
            };
            var note = new TextBlock { Classes = { "muted" }, Text = "Hex bytes; spaces and line breaks are ignored." };
            while (await Show(title, new StackPanel { Spacing = 6, Children = { note, box } }, "OK"))
            {
                if (Parse(box.Text ?? "") is { } bytes) return bytes;
                note.Text = "That is not whole hex bytes (0-9, A-F, two digits a byte).";
            }
            return null;
        }

        private static string Format(byte[] data)
        {
            var text = new StringBuilder();
            for (var i = 0; i < data.Length; i++)
            {
                text.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
                text.Append(i % 16 == 15 ? '\n' : ' ');
            }
            return text.ToString().TrimEnd();
        }

        private static byte[]? Parse(string text)
        {
            var digits = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (digits.Length % 2 != 0) return null;
            var bytes = new byte[digits.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                if (!byte.TryParse(digits.AsSpan(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return null;
            return bytes;
        }

        private async Task<bool> Show(string title, Control body, string ok, string cancel = "Cancel")
        {
            var result = false;
            var dialog = MakeWindow(title);
            var okButton = new Button { Content = ok, IsDefault = true };
            var cancelButton = new Button { Content = cancel, IsCancel = true };
            okButton.Click += (_, _) => { result = true; dialog.Close(); };
            cancelButton.Click += (_, _) => dialog.Close();
            dialog.Content = Layout(body, new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { okButton, cancelButton },
            });
            await dialog.ShowDialog(owner);
            return result;
        }

        private static Window MakeWindow(string title) => new()
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            MinWidth = 320,
        };

        private static Control Layout(Control body, Control buttons) =>
            new StackPanel { Margin = new Thickness(16), Spacing = 16, Children = { body, buttons } };
    }
}
