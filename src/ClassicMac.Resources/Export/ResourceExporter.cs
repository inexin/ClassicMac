using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Compression;

namespace ClassicMac.Resources.Export
{
    /// <summary>What an export wrote.</summary>
    /// <param name="Manifest">The manifest, as written to <c>manifest.json</c>.</param>
    /// <param name="Files">The files written, the manifest included.</param>
    /// <param name="Diagnostics">Problems found while exporting (also in the manifest).</param>
    public sealed record ExportResult(ExportManifest Manifest, IReadOnlyList<string> Files, IReadOnlyList<Diagnostic> Diagnostics);

    /// <summary>
    /// Exports a resource fork to a folder: one subfolder per type (<see cref="HostNames.TypeFolder"/>), each resource as
    /// <c>&lt;id&gt; &lt;name&gt;.bin</c> (<c>&lt;id&gt;.bin</c> when unnamed) holding its data as applications see it —
    /// decompressed when compressed — and a <c>manifest.json</c> (<see cref="ExportManifest"/>) recording types, IDs,
    /// names, attributes, sizes, hashes and the <c>dcmp</c> used. With <see cref="ExportOptions.KeepRaw"/> the stored
    /// bytes are also kept in <c>raw/</c>, so a fork can be rebuilt byte for byte.
    /// </summary>
    public static class ResourceExporter
    {
        private const string Extension = ".bin";
        private const string RawFolder = "raw";

        /// <summary>
        /// Writes <paramref name="fork"/>'s resources into <paramref name="directory"/>. Throws <see cref="IOException"/>
        /// when the folder already holds files and <see cref="ExportOptions.Overwrite"/> is off.
        /// </summary>
        public static ExportResult Export(ResourceFork fork, string directory, ExportSource source, ExportOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(fork);
            ArgumentNullException.ThrowIfNull(directory);
            ArgumentNullException.ThrowIfNull(source);
            options ??= ExportOptions.Default;
            if (!options.Overwrite && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException($"{directory} is not empty.");

            var diagnostics = new List<Diagnostic>(fork.Diagnostics);
            var resources = fork.Resources.Where(r => options.Types is null || options.Types.Contains(r.Type)).ToList();
            // Types that differ only in case get their bytes appended, on disks that ignore case.
            var folded = resources.Select(r => r.Type).Distinct()
                .GroupBy(t => HostNames.TypeFolder(t, collides: false), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
            var taken = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            var entries = new List<ManifestResource>();

            Directory.CreateDirectory(directory);
            foreach (var resource in resources)
            {
                var warnings = new List<Diagnostic>();
                var data = ResourceDecompression.Default.GetData(resource, fork, options.ReadOptions, warnings);
                diagnostics.AddRange(warnings);
                var stored = resource.GetData();

                var folder = HostNames.TypeFolder(resource.Type, folded.Contains(resource.Type));
                var budget = Math.Max(8, options.MaxPathLength - folder.Length - 1 - Extension.Length);
                var stem = HostNames.ToHostName(FileStem(resource), budget);
                if (!taken.TryGetValue(folder, out var names)) taken[folder] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fileName = HostNames.MakeUnique(stem + Extension, names);
                var path = Path.Combine(directory, folder, fileName);
                Directory.CreateDirectory(Path.Combine(directory, folder));
                File.WriteAllBytes(path, data.ToArray());
                files.Add(path);

                string? rawPath = null;
                if (options.KeepRaw)
                {
                    rawPath = $"{RawFolder}/{folder}/{resource.Id.ToString(CultureInfo.InvariantCulture)}{Extension}";
                    var full = Path.Combine(directory, RawFolder, folder, $"{resource.Id.ToString(CultureInfo.InvariantCulture)}{Extension}");
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, stored.ToArray());
                    files.Add(full);
                }

                var typeBytes = new byte[4];
                resource.Type.CopyTo(typeBytes);
                entries.Add(new ManifestResource(
                    resource.Type.ToString(), Convert.ToHexString(typeBytes), resource.Id, resource.Name?.ToString(),
                    (int)resource.Attributes, data.Length, stored.Length, Dcmp(resource),
                    "raw", 1, $"{folder}/{fileName}", Hash(data.Span), Hash(stored.Span), rawPath,
                    warnings.Select(w => w.Message).ToList()));
            }

            var manifest = new ExportManifest(
                ExportManifest.SchemaUrl, ExportManifest.CurrentVersion,
                new ManifestSource(source.Name.ToString(), source.Formats, source.Type.ToString(), source.Creator.ToString(), source.Flags),
                new ManifestFork((int)fork.Attributes, (int)fork.MapFlags),
                entries,
                diagnostics.Select(d => new ManifestDiagnostic(d.Severity.ToString().ToLowerInvariant(), d.Code, d.Message)).ToList());
            var manifestPath = Path.Combine(directory, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ExportManifestJson.Default.ExportManifest), new UTF8Encoding(false));
            files.Add(manifestPath);
            return new ExportResult(manifest, files, diagnostics);
        }

        // "<id> <name>" in Mac Roman bytes, or "<id>" when unnamed, for HostNames to turn into a host name.
        private static MacString FileStem(Resource resource)
        {
            var id = Encoding.ASCII.GetBytes(resource.Id.ToString(CultureInfo.InvariantCulture));
            return resource.Name is { } name && name.Bytes.Length > 0
                ? new MacString([.. id, (byte)' ', .. name.Bytes])
                : new MacString(id);
        }

        private static short? Dcmp(Resource resource)
        {
            if ((resource.Attributes & ResourceAttributes.Compressed) == 0) return null;
            return CompressedResourceHeader.TryRead(resource.GetData().Span, out var header) && header.IsCompressed
                ? header.DecompressorId
                : null;
        }

        private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
    }
}
