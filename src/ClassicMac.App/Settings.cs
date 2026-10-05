using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicMac.Core;

namespace ClassicMac.App;

/// <summary>The app's theme (View ▸ Theme): the system's light or dark, or one of them always.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>The app's settings that last between sessions.</summary>
/// <param name="GroupNoName">Whether the tree groups a folder's files with no name under one "No name" node.</param>
/// <param name="HideInvisible">Whether the tree leaves out files with the Finder's invisible flag.</param>
/// <param name="Theme">The theme chosen in View ▸ Theme.</param>
/// <param name="RecentFiles">The files opened last, newest first (the empty state's Recent list).</param>
/// <param name="ShowDetails">Whether the tree shows its rows' details column (type · creator, sizes).</param>
/// <param name="TypeCreatorDatabase">The type and creator database the user chose (View ▸ Type/Creator Database…), or null.</param>
/// <param name="TextEncoding">The encoding names and text are read in (View ▸ Text Encoding), by its IANA name.</param>
/// <param name="ImageFormat">How exports write images (Export ▸ Images as WebP): <c>png</c> or <c>webp</c>.</param>
/// <param name="LoadableFonts">Whether exports write TrueType fonts made loadable (Export ▸ Loadable Fonts).</param>
public sealed record AppSettings(bool GroupNoName = true, bool HideInvisible = true, AppTheme Theme = AppTheme.System,
    IReadOnlyList<string>? RecentFiles = null, bool ShowDetails = false, string? TypeCreatorDatabase = null, string TextEncoding = "macintosh",
    string ImageFormat = "png", bool LoadableFonts = false)
{
    /// <summary>The files opened last, newest first; empty when none.</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = RecentFiles ?? [];

    // The list compares by its paths, so equal settings are equal records.
    public bool Equals(AppSettings? other) =>
        other is not null && GroupNoName == other.GroupNoName && HideInvisible == other.HideInvisible && Theme == other.Theme
        && ShowDetails == other.ShowDetails && TypeCreatorDatabase == other.TypeCreatorDatabase && TextEncoding == other.TextEncoding
        && ImageFormat == other.ImageFormat && LoadableFonts == other.LoadableFonts
        && RecentFiles.SequenceEqual(other.RecentFiles);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GroupNoName);
        hash.Add(HideInvisible);
        hash.Add(Theme);
        hash.Add(ShowDetails);
        hash.Add(TypeCreatorDatabase);
        hash.Add(TextEncoding);
        hash.Add(ImageFormat);
        hash.Add(LoadableFonts);
        foreach (var path in RecentFiles)
        {
            hash.Add(path);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Where settings are kept: a JSON file for the app, memory for tests.</summary>
public interface ISettingsStore
{
    /// <summary>The settings saved last, or the defaults.</summary>
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>Settings kept in memory only.</summary>
public sealed class MemorySettingsStore(AppSettings? settings = null) : ISettingsStore
{
    public AppSettings Settings { get; private set; } = settings ?? new AppSettings();

    public AppSettings Load() => Settings;

    public void Save(AppSettings settings) => Settings = settings;
}
