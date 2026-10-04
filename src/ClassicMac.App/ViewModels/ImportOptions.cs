using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels;

/// <summary>One "Make" choice of the Import dialog: its label, the resource types it makes, and whether it is offered.</summary>
public sealed record ImportOption(string Key, string Label, string? Code, bool IsEnabled, string? Note);

/// <summary>
/// The Import dialog's "Make" choices (design/boards/dialogs.md): Picture, Color icon, Icon family, One icon kind
/// (with its kind), Cursor, Color cursor and Sound, those not offered for this file disabled with why.
/// </summary>
public sealed partial class ImportOptions : ObservableObject
{
    private const string KindKey = "kind";

    private static readonly string[] AllKinds = ["ICN#", "icl8", "icl4", "ics#", "ics8", "ics4", "icm#", "icm8", "icm4", "ICON"];

    public ImportOptions(IReadOnlyList<string> types, string initial)
    {
        Kinds = [.. AllKinds.Where(types.Contains)];
        ImportOption Option(string key, string label, string? code, bool sound = false)
        {
            var enabled = key == KindKey ? Kinds.Count > 0 : types.Contains(key);
            return new ImportOption(key, label, code, enabled, enabled ? null : sound ? "needs an audio file" : "needs an image");
        }

        Options =
        [
            Option("PICT", "Picture", "PICT"),
            Option("cicn", "Color icon", "cicn"),
            Option(ImportActions.IconFamily, "Icon family", "ICN# icl4 icl8 ics# ics4 ics8"),
            Option(KindKey, "One icon kind", null),
            Option("CURS", "Cursor", "CURS"),
            Option("crsr", "Color cursor", "crsr"),
            Option("snd ", "Sound", "snd ", sound: true),
        ];
        kind = Kinds.Count > 0 ? Kinds[0] : "";
        if (Kinds.Contains(initial))
        {
            selected = Options.Single(o => o.Key == KindKey);
            kind = initial;
        }
        else
        {
            selected = Options.FirstOrDefault(o => o.Key == initial && o.IsEnabled) ?? Options.First(o => o.IsEnabled);
        }
    }

    public IReadOnlyList<ImportOption> Options { get; }

    /// <summary>The single icon kinds offered (for "One icon kind").</summary>
    public IReadOnlyList<string> Kinds { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Type), nameof(IsKind))]
    private ImportOption selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Type))]
    private string kind;

    /// <summary>Whether "One icon kind" is chosen (its kind select is enabled).</summary>
    public bool IsKind => Selected.Key == KindKey;

    /// <summary>The type to make: the choice's own, or the chosen icon kind.</summary>
    public string Type => IsKind ? Kind : Selected.Key;

    // A choice not offered for this file is not taken.
    partial void OnSelectedChanged(ImportOption? oldValue, ImportOption newValue)
    {
        if (!newValue.IsEnabled && oldValue is not null)
        {
            Selected = oldValue;
        }
    }
}
