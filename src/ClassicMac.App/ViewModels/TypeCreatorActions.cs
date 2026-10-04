using System;
using System.IO;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Finder;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

// View ▸ Type/Creator Database…: a type and creator database the user supplies (TCDB's spreadsheet), asked for kinds
// after the volume's and ClassicMac's own (docs/formats/resources/finder.md §2.6). Its path is kept in the settings.
public sealed partial class TypeCreatorActions(MainViewModel main) : ObservableObject
{
    /// <summary>Whether a type and creator database is loaded (View ▸ Forget Type/Creator Database is enabled).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForgetTypeCreatorDatabaseCommand))]
    private bool hasTypeCreatorDatabase;

    /// <summary>The kept database's reading at start (tests wait for it); done when there is none.</summary>
    public Task TypeCreatorDatabaseLoading { get; private set; } = Task.CompletedTask;

    internal void InitTypeCreatorDatabase(string? path)
    {
        _ = Task.Run(() => TypeCreatorDatabase.Shipped);    // the shipped data, read once, before the first kind needs it
        if (path is not null)
        {
            TypeCreatorDatabaseLoading = LoadKeptTypeCreatorDatabaseAsync(path);
        }
    }

    // A kept database that cannot be read stays in the settings (its drive may be back next time); the status says so.
    private async Task LoadKeptTypeCreatorDatabaseAsync(string path)
    {
        try
        {
            Use(await Task.Run(() => Read(path)));
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            main.Status = CannotRead(path, e);
        }
    }

    /// <summary>View ▸ Type/Creator Database…: reads the chosen spreadsheet, uses it and keeps its path.</summary>
    [RelayCommand]
    private async Task ChooseTypeCreatorDatabase()
    {
        if (main.FilePicker is null || await main.FilePicker.PickFileAsync("Type/Creator Database", [".xlsx"]) is not { } path)
        {
            return;
        }

        TypeCreatorDatabase database;
        try
        {
            database = await Task.Run(() => Read(path));
        }
        catch (Exception e) when (IsReadFailure(e))
        {
            main.Status = CannotRead(path, e);
            return;
        }

        Use(database);
        main.settings.Save(main.settings.Load() with { TypeCreatorDatabase = path });
        main.Status = $"Type/Creator database: {database.Count} kind{(database.Count == 1 ? "" : "s")} from “{Path.GetFileName(path)}”.";
    }

    /// <summary>View ▸ Forget Type/Creator Database: kinds come from the volume and ClassicMac's table only.</summary>
    [RelayCommand(CanExecute = nameof(HasTypeCreatorDatabase))]
    private void ForgetTypeCreatorDatabase()
    {
        Use(null);
        main.settings.Save(main.settings.Load() with { TypeCreatorDatabase = null });
    }

    private static TypeCreatorDatabase Read(string path)
    {
        using var stream = File.OpenRead(path);
        return TypeCreatorDatabase.Load(stream);
    }

    private static bool IsReadFailure(Exception e) => ExceptionFilters.IsFileAccess(e) || e is InvalidDataException;

    private static string CannotRead(string path, Exception e) =>
        $"“{Path.GetFileName(path)}” could not be read as a type and creator database: {e.Message}";

    // The tree's kinds ask the display options for it; the selection's details and header are shown again.
    private void Use(TypeCreatorDatabase? database)
    {
        main.TreeDisplay.KindDatabase = database;
        HasTypeCreatorDatabase = database is not null;
        main.Details = DetailsViewModel.For(main.Selected, main.DetailsActions.ProblemsIn(main.Selected), main.AliasActions.SelectedAlias);
        main.InspectorActions.NotifyHeader();
    }
}
