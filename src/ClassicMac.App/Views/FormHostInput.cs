using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views;

// The read-then-edit host's keys and double-click (FormEditing.cs): Esc cancels and Ctrl+Enter applies while editing,
// before a text box can take the keys; a double-click on a read-only "form-row" edits with that row selected.
internal sealed class FormHostInput
{
    private readonly Control host;

    public FormHostInput(Control host)
    {
        this.host = host;
        host.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        host.AddHandler(InputElement.PointerPressedEvent, (_, e) => SelectFormRow(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.GotFocusEvent, (_, e) => SelectFormRow(e.Source), RoutingStrategies.Bubble);
    }

    // A click on a "form-row", or the focus going into one of its inputs, selects its row (and its item in the preview).
    private void SelectFormRow(object? source)
    {
        if (host.DataContext is MainViewModel { Forms.Form: { } form } && FormRow(source) is { DataContext: { } item })
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (host.DataContext is not MainViewModel { FormEditing.IsEditingForm: true } model)
        {
            return;
        }

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && model.FormEditing.CancelFormCommand.CanExecute(null))
        {
            model.FormEditing.CancelFormCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return && e.KeyModifiers == KeyModifiers.Control)
        {
            // Handled either way, so a text box does not take Ctrl+Enter as a line break.
            if (model.Forms.ApplyFormCommand.CanExecute(null))
            {
                model.Forms.ApplyFormCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (host.DataContext is not MainViewModel { FormEditing.IsEditingForm: false } model || e.Source is not Visual source)
        {
            return;
        }

        var row = FormRow(source);
        if (row?.DataContext is { } item && model.FormEditing.EditFormCommand.CanExecute(item))
        {
            model.FormEditing.EditFormCommand.Execute(item);
            e.Handled = true;
        }
    }
}
