using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClassicMac.Core;

namespace ClassicMac.Resources.Export;

/// <summary>Choices for rebuilding a fork from an export folder (<see cref="ResourcePacker"/>).</summary>
public sealed record PackOptions
{
    /// <summary>The defaults.</summary>
    public static PackOptions Default { get; } = new();

    /// <summary>
    /// The fork the export was made from, for the stored data of unchanged resources when the export has no <c>raw/</c>
    /// copies. Default none.
    /// </summary>
    public ResourceFork? Base { get; init; }

    /// <summary>Whether a resource whose file is missing is left out (true) or is an error (false, the default).</summary>
    public bool AllowDeletes { get; init; }
}

/// <summary>What a pack rebuilt.</summary>
/// <param name="Fork">The fork. Only meaningful when <paramref name="Diagnostics"/> holds no error.</param>
/// <param name="Source">The export's source: the file's name, type, creator and Finder flags.</param>
/// <param name="Diagnostics">What was found: errors mean the fork is not what the export describes.</param>
public sealed record PackResult(ResourceFork Fork, ManifestSource Source, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Whether any diagnostic is an error.</summary>
    public bool Failed => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Rebuilds a resource fork from an export folder and its <c>manifest.json</c> (format 1): type, ID, name and
/// attributes from the manifest, the fork's attributes from its <c>fork</c>, the resources in its order. A resource
/// whose main file is unchanged (same SHA-256) takes its stored data, byte for byte, from its <c>raw/</c> copy or from
/// the base fork; a resource whose main file is its data (<c>raw</c>, or a decoder's <c>.bin</c>) can also take the
/// data from its file, changed or not. A changed decoded file needs an encoder,
/// which none of the built-in decoders has yet.
/// </summary>
public static class ResourcePacker
{
    /// <summary>
    /// Reads the export in <paramref name="directory"/>. Throws <see cref="InvalidDataException"/> when there is no
    /// readable manifest, or its major version is newer than 1.
    /// </summary>
    public static PackResult Pack(string directory, PackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        options ??= PackOptions.Default;
        var manifestPath = Path.Combine(directory, "manifest.json");
        ExportManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), ExportManifestJson.Default.ExportManifest)
                ?? throw new InvalidDataException("manifest.json is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"manifest.json is not a manifest: {e.Message}", e);
        }
        if (!manifest.FormatVersion.StartsWith("1.", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"manifest.json is format {manifest.FormatVersion}; this version reads format 1.");
        }

        var diagnostics = new List<Diagnostic>();
        var fork = new ResourceFork
        {
            Attributes = (ResourceForkAttributes)manifest.Fork.Attributes,
            MapFlags = (ResourceMapFlags)manifest.Fork.MapFlags,
        };
        foreach (var entry in manifest.Resources)
        {
            var type = new FourCC(Convert.FromHexString(entry.TypeBytes));
            var what = $"'{entry.Type}' {entry.Id}";
            void Report(DiagnosticSeverity severity, string code, string message) => diagnostics.Add(new Diagnostic(severity, code, $"{what}: {message}"));

            var main = Local(directory, entry.Path);
            if (!File.Exists(main))
            {
                if (options.AllowDeletes)
                {
                    Report(DiagnosticSeverity.Info, "pack.deleted", $"{entry.Path} is gone; the resource is left out.");
                }
                else
                {
                    Report(DiagnosticSeverity.Error, "pack.missing-file", $"{entry.Path} is gone (allow deletes to leave the resource out).");
                }

                continue;
            }
            var file = File.ReadAllBytes(main);
            var attributes = (ResourceAttributes)entry.Attributes;
            byte[]? stored = null;
            if (Hash(file) == entry.Sha256)
            {
                stored = StoredData(directory, entry, type, options.Base, Report);
                foreach (var other in entry.OtherFiles ?? [])
                {
                    var path = Local(directory, other.Path);
                    if (File.Exists(path) && Hash(File.ReadAllBytes(path)) != other.Sha256)
                    {
                        Report(DiagnosticSeverity.Warning, "pack.other-changed", $"{other.Path} changed, but only the main file is packed; the change is ignored.");
                    }
                }
            }
            else if (!IsData(entry))
            {
                Report(DiagnosticSeverity.Error, "pack.no-encoder", $"{entry.Path} changed, and the {entry.Decoder} decoder has no encoder to turn it back into resource data.");
                continue;
            }
            if (stored is null)
            {
                if (!IsData(entry))
                {
                    Report(DiagnosticSeverity.Error, "pack.no-stored-data",
                        "its stored data is neither in raw/ nor in the base fork (extract with --keep-raw, or give the original fork as the base).");
                    continue;
                }
                // A data file (.bin) is the resource's data after decompression: written as it is, no longer compressed.
                stored = file;
                if ((attributes & ResourceAttributes.Compressed) != 0)
                {
                    attributes &= ~ResourceAttributes.Compressed;
                    Report(DiagnosticSeverity.Warning, "pack.decompressed", "written from its decompressed file; its compressed attribute is cleared.");
                }
            }
            var resource = new Resource(type, entry.Id, stored) { Attributes = attributes };
            if (entry.Name is { } name)
            {
                resource.Name = MacString.Parse(name);
            }

            fork.Add(resource);
        }
        return new PackResult(fork, manifest.Source, diagnostics);
    }

    // The stored bytes of an unchanged resource: its raw/ copy, else the base fork's resource of the same type and ID
    // when it matches the stored hash; null when neither has them.
    private static byte[]? StoredData(string directory, ManifestResource entry, FourCC type, ResourceFork? baseFork,
        Action<DiagnosticSeverity, string, string> report)
    {
        if (entry.RawPath is { } rawPath && File.Exists(Local(directory, rawPath)))
        {
            var raw = File.ReadAllBytes(Local(directory, rawPath));
            if (Hash(raw) != entry.StoredSha256)
            {
                report(DiagnosticSeverity.Warning, "pack.raw-changed", $"{rawPath} differs from what was exported; it is packed as it is.");
            }

            return raw;
        }
        if (baseFork?.Find(type, entry.Id) is { } original)
        {
            var data = original.GetData().ToArray();
            if (Hash(data) == entry.StoredSha256)
            {
                return data;
            }

            report(DiagnosticSeverity.Warning, "pack.base-differs", "the base fork's resource is not the one exported; not used.");
        }
        return null;
    }

    // Whether the main file is the resource's data: a raw resource's, or a decoder's .bin (code decoders write the
    // data as their main file and the listings beside it), so it packs back as raw data does.
    private static bool IsData(ManifestResource entry) =>
        entry.Decoder == "raw" || entry.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    private static string Local(string directory, string path) => Path.Combine([directory, .. path.Split('/')]);

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
