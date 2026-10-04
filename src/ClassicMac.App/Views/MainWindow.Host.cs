using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// The read-then-edit host's keys and double-click (FormHost.cs): Esc cancels and Ctrl+Enter applies while editing,
// before a text box can take the keys; a double-click on a read-only "form-row" edits with that row selected.
internal sealed partial class MainWindow
{
    private void BindHost()
    {
        FormHost.AddHandler(KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
        FormHost.AddHandler(DoubleTappedEvent, OnHostDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        FormHost.AddHandler(PointerPressedEvent, (_, e) => SelectFormRow(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        FormHost.AddHandler(GotFocusEvent, (_, e) => SelectFormRow(e.Source), RoutingStrategies.Bubble);
    }

    // A click on a "form-row", or the focus going into one of its inputs, selects its row (and its item in the preview).
    private void SelectFormRow(object? source)
    {
        if (DataContext is MainViewModel { Form: { } form } && FormRow(source) is { DataContext: { } item })
        {
            form.SelectRow(item);
        }
    }

    private static Control? FormRow(object? source)
    {
        var row = (source as Visual)?.FindAncestorOfType<Control>(includeSelf: true);
        while (row is not null && !row.Classes.Contains("form-row"))
        {
            row = row.FindAncestorOfType<Control>();
        }

        return row;
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

        var row = FormRow(source);
        if (row?.DataContext is { } item && model.EditFormCommand.CanExecute(item))
        {
            model.EditFormCommand.Execute(item);
            e.Handled = true;
        }
    }
}
