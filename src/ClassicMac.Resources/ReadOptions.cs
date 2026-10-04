namespace ClassicMac.Resources;

/// <summary>Which Resource Manager's behaviour to reproduce where the two differ (only on malformed data).</summary>
public enum ResourceManagerModel
{
    /// <summary>The native Resource Manager of Mac OS 9.</summary>
    MacOS9,

    /// <summary>The 68k Resource Manager in ROM (System 7 era).</summary>
    Rom68k,
}

/// <summary>
/// Limits and choices for reading resource forks. Every tunable value lives here rather than in the readers; the
/// CLI and the app map their settings onto this record. Container limits are in <c>ClassicMac.Files</c>.
/// </summary>
public sealed record ReadOptions
{
    /// <summary>The defaults.</summary>
    public static ReadOptions Default { get; } = new();

    /// <summary>The largest a single resource may be once decompressed, in bytes. Default 64 MiB.</summary>
    public long MaxResourceSize { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Whose behaviour to follow for malformed compressed resources. Default <see cref="ResourceManagerModel.MacOS9"/>.
    /// </summary>
    public ResourceManagerModel ResourceManager { get; init; } = ResourceManagerModel.MacOS9;
}
