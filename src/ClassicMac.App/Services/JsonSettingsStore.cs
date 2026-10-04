using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicMac.Core;

namespace ClassicMac.App.Services;

/// <summary>Settings kept as JSON in a file (by default <c>%AppData%/ClassicMac/settings.json</c>).</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassicMac", "settings.json");

    public string FilePath { get; } = path;

    /// <summary>The saved settings; the defaults when there are none or they cannot be read.</summary>
    public AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Saves the settings; a failure to write is ignored (they last for this session only).</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, SettingsJson.Default.AppSettings));
        }
        catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
