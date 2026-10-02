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
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    // The sound's header actions (design/boards/sound.md): Save as WAV… and Replace from WAV… in place of Export… and
    // Edit, since sounds are replaced, not edited.
    public sealed partial class MainViewModel
    {
        /// <summary>Whether the selection is a 'snd ' resource (the header shows the sound's actions).</summary>
        public bool IsSoundResource => Selected is ResourceNode { Resource.Type: var type } && type.ToString() == "snd ";

        private bool CanSaveAsWav() => !IsExporting && IsSoundResource;

        private bool CanReplaceFromWav() => CanSaveAsWav() && FileOwner(Selected) is not null;

        /// <summary>Save as WAV…: the sound decoded as <c>extract</c> writes it.</summary>
        [RelayCommand(CanExecute = nameof(CanSaveAsWav))]
        private Task SaveAsWav() => Run(async () =>
        {
            if (Selected is not ResourceNode node || FilePicker is null)
            {
                return;
            }

            var diagnostics = new List<Diagnostic>();
            var (outputs, _) = await Task.Run(() => Decode(node, diagnostics));
            foreach (var d in diagnostics)
            {
                Report(new DiagnosticEntry(d, node.Source, node));
            }

            if (outputs.FirstOrDefault(o => o.Extension == ".wav") is not { } wav)
            {
                Status = $"{node.Resource} could not be decoded; see Diagnostics.";
                return;
            }

            var path = await FilePicker.PickSaveFileAsync($"Save {node.Resource} as WAV", HostNames.ToHostName(Stem(node.Resource), 200) + ".wav", [".wav"]);
            if (path is null)
            {
                return;
            }

            await File.WriteAllBytesAsync(path, wav.Content.ToArray());
            Status = $"Saved {node.Resource} to {path}.";
        });

        /// <summary>Replace from WAV…: the sound's data made from a WAV file, as one undoable edit.</summary>
        [RelayCommand(CanExecute = nameof(CanReplaceFromWav))]
        private async Task ReplaceFromWav()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (Selected is not ResourceNode node || FileOwner(node) is not { } owner || FilePicker is null)
            {
                return;
            }

            if ((await FilePicker.PickFilesAsync()).FirstOrDefault() is not { } path)
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
                Status = $"“{fileName}” could not be read: {e.Message}";
                return;
            }

            var resource = node.Resource;
            Execute(owner, new SetResourceData(resource, data, $"Replace from {fileName}"), () => resource);
        }

        private void OnSelectionChangedForSoundHeader()
        {
            OnPropertyChanged(nameof(IsSoundResource));
            SaveAsWavCommand.NotifyCanExecuteChanged();
            ReplaceFromWavCommand.NotifyCanExecuteChanged();
        }
    }
}
