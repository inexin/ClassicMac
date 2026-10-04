using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels;

// Strings, string lists, text and version on the read-then-edit host (design/boards/read-then-edit.md, E5): each reads
// as text first (strings numbered, text with its styles, version as labelled rows) and edits with today's inputs.

/// <summary><c>'STR '</c>: one string.</summary>
public sealed partial class StringForm(Resource resource, string text) : ResourceForm(resource)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string text = text;

    /// <summary>Whether the string is empty (the read-only view says so).</summary>
    public bool IsEmpty => Text.Length == 0;

    public override bool HasReadOnlyView => true;

    protected override bool IsValue(string? propertyName) => propertyName is not nameof(IsEmpty);

    public override byte[] BuildData() => TextResources.WriteString(Text);
}

/// <summary>One string of a <c>'STR#'</c>.</summary>
public sealed partial class StringItem(string text) : ObservableObject
{
    [ObservableProperty]
    private string text = text;

    /// <summary>Its number in the list, from 1.</summary>
    [ObservableProperty]
    private int number;

    /// <summary>Whether it is the selected row.</summary>
    [ObservableProperty]
    private bool isSelected;
}

/// <summary><c>'STR#'</c>: a list of strings, read as a numbered list.</summary>
public sealed partial class StringListForm : ResourceForm
{
    public StringListForm(Resource resource, IReadOnlyList<string> strings) : base(resource)
    {
        foreach (var s in strings)
        {
            Strings.Add(new StringItem(s));
        }

        Renumber();
        Strings.CollectionChanged += (_, _) =>
        {
            Renumber();
            OnPropertyChanged(nameof(Summary));
        };
        Watch(Strings, name => name is nameof(StringItem.Text));
    }

    public ObservableCollection<StringItem> Strings { get; } = [];

    /// <summary>The selected string (a double-click edits with it selected; a new string is selected), or null.</summary>
    [ObservableProperty]
    private StringItem? selectedItem;

    /// <summary>"3 strings".</summary>
    public string Summary => Strings.Count switch
    {
        0 => "No strings",
        1 => "1 string",
        var n => string.Create(CultureInfo.InvariantCulture, $"{n} strings"),
    };

    public override bool HasReadOnlyView => true;

    public override void SelectRow(object? row)
    {
        if (row is StringItem item && Strings.Contains(item))
        {
            SelectedItem = item;
        }
    }

    protected override bool IsValue(string? propertyName) => propertyName is not (nameof(SelectedItem) or nameof(Summary));

    partial void OnSelectedItemChanged(StringItem? oldValue, StringItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    [RelayCommand]
    private void Add()
    {
        var item = new StringItem("");
        Strings.Add(item);
        SelectedItem = item;
    }

    [RelayCommand]
    private void Remove(StringItem item)
    {
        if (ReferenceEquals(item, SelectedItem))
        {
            SelectedItem = null;
        }

        Strings.Remove(item);
    }

    [RelayCommand]
    private void MoveUp(StringItem item)
    {
        var i = Strings.IndexOf(item);
        if (i > 0)
        {
            Strings.Move(i, i - 1);
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < Strings.Count; i++)
        {
            Strings[i].Number = i + 1;
        }
    }

    public override byte[] BuildData() => TextResources.WriteStringList(Strings.Select(s => s.Text).ToList());
}

/// <summary><c>'TEXT'</c>, with its <c>'styl'</c> kept in step when there is one; read with its styles applied.</summary>
public sealed partial class TextForm : ResourceForm
{
    private readonly byte[] data;
    private readonly (Resource Resource, byte[] Data)? styl;

    public TextForm(Resource resource, byte[] data, (Resource Resource, byte[] Data)? styl) : base(resource)
    {
        this.data = data;
        this.styl = styl;
        text = TextResources.ReadText(data);
        UpdateStyled();
    }

    [ObservableProperty]
    private string text;

    /// <summary>The text with its styles as the values make them (the last good one while the text cannot be written).</summary>
    [ObservableProperty]
    private StyledText? styled;

    public bool HasStyles => styl is not null;

    public override bool HasReadOnlyView => true;

    protected override bool IsValue(string? propertyName) => propertyName is not (nameof(Styled) or nameof(HasStyles));

    partial void OnTextChanged(string value) => UpdateStyled();

    private void UpdateStyled()
    {
        try
        {
            var (bytes, stylBytes) = TextResources.WriteText(data, styl?.Data ?? [], Text, styl is not null);
            Styled = StyledText.Read(bytes, stylBytes ?? []);
        }
        catch (ArgumentException)
        {
            // Kept: the error line says why the text cannot be written.
        }
    }

    // The 'styl' follows from the text, so the text's bytes are the draft's.
    public override byte[] BuildData() => TextResources.WriteText(data, styl?.Data ?? [], Text, styl is not null).Text;

    public override IResourceEdit BuildEdit(ResourceFork fork)
    {
        var (bytes, stylBytes) = TextResources.WriteText(data, styl?.Data ?? [], Text, styl is not null);
        var edit = new SetResourceData(Resource, bytes, $"Edit {Resource}");
        return styl is not { } s || stylBytes is null ? edit : new CompoundEdit($"Edit {Resource}", edit, new SetResourceData(s.Resource, stylBytes));
    }
}

/// <summary><c>'vers'</c>: the version, stage, region and the two strings; read as labelled rows.</summary>
public sealed partial class VersionForm : ResourceForm
{
    public VersionForm(Resource resource, VersionResource version) : base(resource)
    {
        (major, minor, bugFix, nonRelease, region, shortVersion, longVersion) =
            (version.Major, version.Minor, version.BugFix, version.NonRelease, version.Region, version.ShortVersion, version.LongVersion);
        stage = Stages.FirstOrDefault(s => s.Value == version.Stage) ?? Stages[^1];
    }

    public sealed record StageChoice(byte Value, string Name)
    {
        public override string ToString() => Name;
    }

    public static StageChoice[] Stages { get; } = [new(0x20, "development"), new(0x40, "alpha"), new(0x60, "beta"), new(0x80, "final")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    private decimal major;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    private decimal minor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    private decimal bugFix;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText), nameof(StageText))]
    private StageChoice stage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NumberText))]
    private decimal nonRelease;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegionText))]
    private decimal region;

    [ObservableProperty] private string shortVersion;
    [ObservableProperty] private string longVersion;

    /// <summary>
    /// The version as the Finder writes it: major.minor, .bug fix when not 0, then the stage's letter (d, a, b; f for
    /// a final release only when it has a non-release number) and that number: "1.2b3", "7.5.5", "7.5.5f2".
    /// </summary>
    public string NumberText
    {
        get
        {
            var number = string.Create(CultureInfo.InvariantCulture, $"{(int)Major}.{(int)Minor}");
            if (BugFix != 0)
            {
                number += string.Create(CultureInfo.InvariantCulture, $".{(int)BugFix}");
            }

            var letter = Stage.Value switch
            {
                0x20 => "d",
                0x40 => "a",
                0x60 => "b",
                _ => NonRelease != 0 ? "f" : "",
            };
            return letter.Length == 0 ? number : number + letter + (NonRelease != 0 ? ((int)NonRelease).ToString(CultureInfo.InvariantCulture) : "");
        }
    }

    public string StageText => Stage.Name;

    public string RegionText => ((int)Region).ToString(CultureInfo.InvariantCulture);

    public override bool HasReadOnlyView => true;

    protected override bool IsValue(string? propertyName) => propertyName is not (nameof(NumberText) or nameof(StageText) or nameof(RegionText));

    public override byte[] BuildData() =>
        new VersionResource((int)Major, (int)Minor, (int)BugFix, Stage.Value, (int)NonRelease, (short)Region, ShortVersion, LongVersion).Write();
}
