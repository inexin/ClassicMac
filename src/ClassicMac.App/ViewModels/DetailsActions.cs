using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

// The Details tab's actions (design/boards/details.md, P4): Copy all, the File card's "In" link, and the chain card's
// link to the file's problems.
public sealed partial class DetailsActions(MainViewModel main) : ObservableObject
{
    // The errors and warnings reported on the node or anything under it.
    internal int ProblemsIn(NodeViewModel? node)
    {
        if (node is null)
        {
            return 0;
        }

        return main.DiagnosticsPanel.All.Count(e => e.Diagnostic.Severity != DiagnosticSeverity.Info && Under(e.Node, node));
    }

    private static bool Under(NodeViewModel? at, NodeViewModel node)
    {
        for (; at is not null; at = at.Parent)
        {
            if (ReferenceEquals(at, node))
            {
                return true;
            }
        }

        return false;
    }

    private bool CanCopyDetails() => main.Details.Groups.Count > 0;

    /// <summary>Copy all: the details as plain "Label: value" lines on the clipboard.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyDetails))]
    private Task CopyDetails() => main.ShellActions.Shell?.CopyTextAsync(main.Details.CopyText) ?? Task.CompletedTask;

    private bool CanGoToIn() => main.Details.InNode is not null;

    /// <summary>The File card's "In": the folder or volume holding the file is selected (asking about a draft first).</summary>
    [RelayCommand(CanExecute = nameof(CanGoToIn))]
    private async Task GoToIn()
    {
        if (main.Details.InNode is not { } node)
        {
            return;
        }

        main.Selected = node;
        await main.DraftTask;
    }

    private bool CanShowProblems() => main.Details.HasProblems;

    /// <summary>The chain card's problem count: the diagnostics panel opens, filtered to this file.</summary>
    [RelayCommand(CanExecute = nameof(CanShowProblems))]
    private void ShowProblems()
    {
        main.DiagnosticsPanel.IsExpanded = true;
        main.DiagnosticsPanel.Search = main.Selected?.Source ?? "";
    }
}
