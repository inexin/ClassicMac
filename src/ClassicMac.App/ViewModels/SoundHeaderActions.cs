using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Export;
using ClassicMac.Resources.Decoders.Sound;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

// The sound's header actions (design/boards/sound.md): Save as WAV… and Replace from WAV… in place of Export… and
// Edit, since sounds are replaced, not edited.
public sealed partial class SoundHeaderActions(MainViewModel main) : ObservableObject
{
    /// <summary>Whether the selection is a 'snd ' resource (the header shows the sound's actions).</summary>
    public bool IsSoundResource => main.Selected is ResourceNode { Resource.Type: var type } && type.ToString() == "snd ";

    private bool CanSaveAsWav() => !main.ExportActions.IsExporting && IsSoundResource;

    private bool CanReplaceFromWav() => CanSaveAsWav() && MainViewModel.FileOwner(main.Selected) is not null;

    /// <summary>Save as WAV…: the sound decoded as <c>extract</c> writes it.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAsWav))]
    private Task SaveAsWav() => main.ExportActions.Run(async () =>
    {
        if (main.Selected is not ResourceNode node || main.FilePicker is null)
        {
            return;
        }

        var diagnostics = new List<Diagnostic>();
        var (outputs, _) = await Task.Run(() => main.ExportActions.Decode(node, diagnostics));
        foreach (var d in diagnostics)
        {
            main.Report(new DiagnosticEntry(d, node.Source, node));
        }

        if (outputs.FirstOrDefault(o => o.Extension == ".wav") is not { } wav)
        {
            main.Status = $"{node.Resource} could not be decoded; see Diagnostics.";
            return;
        }

        var path = await main.FilePicker.PickSaveFileAsync($"Save {node.Resource} as WAV", HostNames.ToHostName(ExportActions.Stem(node.Resource), 200) + ".wav", [".wav"]);
        if (path is null)
        {
            return;
        }

        await File.WriteAllBytesAsync(path, wav.Content.ToArray());
        main.Status = $"Saved {node.Resource} to {path}.";
    });

    /// <summary>Replace from WAV…: the sound's data made from a WAV file, as one undoable edit.</summary>
    [RelayCommand(CanExecute = nameof(CanReplaceFromWav))]
    private async Task ReplaceFromWav()
    {
        if (!await main.Drafts.ResolveDraftAsync())
        {
            return;
        }

        if (main.Selected is not ResourceNode node || MainViewModel.FileOwner(node) is not { } owner || main.FilePicker is null)
        {
            return;
        }

        if ((await main.FilePicker.PickFilesAsync()).FirstOrDefault() is not { } path)
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        byte[] data;
        try
        {
            data = SoundImport.FromWav(await File.ReadAllBytesAsync(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            main.Status = $"“{fileName}” could not be read: {e.Message}";
            return;
        }

        var resource = node.Resource;
        main.Execute(owner, new SetResourceData(resource, data, $"Replace from {fileName}"), () => resource);
    }

    internal void OnSelectionChangedForSoundHeader()
    {
        OnPropertyChanged(nameof(IsSoundResource));
        SaveAsWavCommand.NotifyCanExecuteChanged();
        ReplaceFromWavCommand.NotifyCanExecuteChanged();
    }
}
