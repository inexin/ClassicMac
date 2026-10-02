using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views
{
    // The read-then-edit host's keys and double-click (FormHost.cs): Esc cancels and Ctrl+Enter applies while editing,
    // before a text box can take the keys; a double-click on a read-only "form-row" edits with that row selected.
    internal sealed partial class MainWindow
    {
        private void BindHost()
        {
            FormHost.AddHandler(KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
            FormHost.AddHandler(DoubleTappedEvent, OnHostDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        private void OnHostKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not MainViewModel { IsEditingForm: true } model)
            {
                return;
            }

            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && model.CancelFormCommand.CanExecute(null))
            {
                model.CancelFormCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.Control)
            {
                // Handled either way, so a text box does not take Ctrl+Enter as a line break.
                if (model.ApplyFormCommand.CanExecute(null))
                {
                    model.ApplyFormCommand.Execute(null);
                }

                e.Handled = true;
            }
        }

        private void OnHostDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not MainViewModel { IsEditingForm: false } model || e.Source is not Visual source)
            {
                return;
            }

            var row = source.FindAncestorOfType<Control>(includeSelf: true);
            while (row is not null && !row.Classes.Contains("form-row"))
            {
                row = row.FindAncestorOfType<Control>();
            }

            if (row?.DataContext is { } item && model.EditFormCommand.CanExecute(item))
            {
                model.EditFormCommand.Execute(item);
                e.Handled = true;
            }
        }
    }
}
