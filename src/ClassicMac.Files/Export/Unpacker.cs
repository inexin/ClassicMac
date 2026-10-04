using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Export;

namespace ClassicMac.Files.Export;

/// <summary>What an unpack wrote.</summary>
/// <param name="Files">Mac files written.</param>
/// <param name="Bytes">Their data and resource fork bytes.</param>
/// <param name="Failed">Paths that could not be written, with why.</param>
public sealed record UnpackResult(int Files, long Bytes, IReadOnlyList<string> Failed);

/// <summary>What a resource extraction wrote.</summary>
/// <param name="Resources">Resources exported.</param>
/// <param name="Files">Files whose resources were exported.</param>
/// <param name="Failed">Folders that could not be written, with why.</param>
public sealed record ExtractResult(int Resources, int Files, IReadOnlyList<string> Failed);

/// <summary>What a document conversion wrote.</summary>
/// <param name="Documents">The documents written, each as its manifest entry (paths relative to the output folder)
/// with the file's Mac path.</param>
/// <param name="Failed">Folders that could not be written, with why.</param>
public sealed record ConvertResult(IReadOnlyList<(string MacPath, ManifestDocument Document)> Documents, IReadOnlyList<string> Failed);

/// <summary>A resource fork found in an unwrapped input: the file it belongs to, the formats down to it, and the fork.</summary>
/// <param name="Node">The file's node in the tree.</param>
/// <param name="Chain">The formats from the input down to the file.</param>
/// <param name="Fork">Its resources.</param>
public sealed record ForkToExtract(ContainerNode Node, IReadOnlyList<string> Chain, ResourceFork Fork);

/// <summary>Writes unwrapped inputs to folders: every Mac file (<see cref="Unpack"/>), or every resource (<see cref="Extract"/>).</summary>
public static class Unpacker
{
    // Room kept in a path for a companion's prefix (".rsrc/" or "._").
    private const int CompanionRoom = 7;

    /// <summary>
    /// Writes every Mac file under <paramref name="root"/> into <paramref name="directory"/> with both forks and
    /// Finder info, placed by <see cref="OutputLayout"/>. Names SheepShaver's folders cannot hold and names that
    /// collide are reported.
    /// </summary>
    public static UnpackResult Unpack(ContainerNode root, string directory, HostWriteOptions? options = null,
        ICollection<Diagnostic>? diagnostics = null, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(directory);
        options ??= HostWriteOptions.Default;
        var layout = new OutputLayout(name => HostFiles.ToHostName(name, options.Layout));
        var failed = new List<string>();
        int files = 0;
        long bytes = 0;
        foreach (var (leaf, folder) in layout.Place(root))
        {
            var file = leaf.File;
            var target = Path.Combine([directory, .. folder]);
            var relative = string.Join('/', folder).Length + (folder.Count > 0 ? 1 : 0);
            var host = HostFiles.ToHostName(file.Name, options.Layout, Math.Max(8, options.MaxPathLength - relative - CompanionRoom));
            if (options.Layout == HostLayout.BasiliskII && HostFiles.ToMacName(host, basilisk: true, new ContainerContext()) != file.Name)
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "unpack.name-changed",
                    $"\"{file.MacPath}\" is written as \"{host}\": SheepShaver's shared folders cannot hold its name as it is."));
            }
            var name = layout.Unique(folder, host, out var changed);
            if (changed)
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Warning, "unpack.name-collision",
                    $"\"{file.MacPath}\" is written as \"{name}\": \"{host}\" is taken in that folder."));
            }
            try
            {
                HostFiles.Write(file, target, options, name);
                files++;
                bytes += file.DataFork.Length + file.ResourceFork.Length;
                progress?.Report(files);
            }
            catch (Exception e) when (ExceptionFilters.IsFileAccess(e) || e is InvalidDataException)
            {
                failed.Add($"{Path.Combine(target, name)}: {e.Message}");
            }
        }
        return new UnpackResult(files, bytes, failed);
    }

    /// <summary>
    /// Exports <paramref name="forks"/> (found under <paramref name="root"/>) into <paramref name="directory"/>: one
    /// fork straight into it, several into a folder each, placed by <see cref="OutputLayout"/>. Export problems
    /// beyond the forks' own go to <paramref name="diagnostics"/>, as (Mac path, diagnostic).
    /// </summary>
    public static ExtractResult Extract(ContainerNode root, IReadOnlyList<ForkToExtract> forks, string directory, ExportOptions? options = null,
        ICollection<(string Source, Diagnostic Diagnostic)>? diagnostics = null, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(forks);
        ArgumentNullException.ThrowIfNull(directory);
        options ??= ExportOptions.Default;
        var layout = new OutputLayout(name => HostNames.ToHostName(name));
        var folders = new Dictionary<ContainerNode, List<string>>(ReferenceEqualityComparer.Instance);
        foreach (var (leaf, folder) in layout.Place(root))
        {
            folders[leaf] = folder;
        }

        var failed = new List<string>();
        int resources = 0, files = 0;
        foreach (var entry in forks)
        {
            var file = entry.Node.File;
            var parts = new List<string>();
            if (forks.Count > 1)
            {
                var folder = folders.GetValueOrDefault(entry.Node) ?? [];
                parts = [.. folder, layout.Unique(folder, HostNames.ToHostName(file.Name), out _)];
            }
            var target = Path.Combine([directory, .. parts]);
            var relative = string.Join('/', parts).Length + (parts.Count > 0 ? 1 : 0);
            var source = new ExportSource(file.Name, entry.Chain, file.FinderInfo.Type, file.FinderInfo.Creator, (ushort)file.FinderInfo.Flags);
            try
            {
                var result = ResourceExporter.Export(entry.Fork, target, source,
                    options with { MaxPathLength = Math.Max(24, options.MaxPathLength - relative) },
                    () => file.DataFork.ToArray(options.ReadOptions.MaxResourceSize));
                // The fork's own diagnostics were reported when it was read.
                for (var i = entry.Fork.Diagnostics.Count; i < result.Diagnostics.Count; i++)
                {
                    diagnostics?.Add((file.MacPath, result.Diagnostics[i]));
                }

                resources += result.Manifest.Resources.Count;
                files++;
                progress?.Report(files);
            }
            catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
            {
                failed.Add($"{target}: {e.Message}");
            }
        }
        return new ExtractResult(resources, files, failed);
    }
}

/// <summary>Converts the documents among Mac files to folders (the <c>convert</c> command).</summary>
public static class DocumentConverter
{
    /// <summary>
    /// Writes every document among <paramref name="forks"/>' files (found under <paramref name="root"/>) into
    /// <paramref name="directory"/>, converted by the first of <paramref name="converters"/> that knows it: one
    /// document straight into it, several into a folder each, placed by <see cref="OutputLayout"/>. Problems go to
    /// <paramref name="diagnostics"/>, as (Mac path, diagnostic). Throws <see cref="IOException"/> when the folder
    /// already holds files and <paramref name="overwrite"/> is off. <paramref name="progress"/> hears the count of
    /// files looked at so far, after each.
    /// </summary>
    public static ConvertResult Convert(ContainerNode root, IReadOnlyList<ForkToExtract> forks, string directory,
        IReadOnlyList<IDocumentConverter> converters, ReadOptions? readOptions = null, bool overwrite = false,
        ICollection<(string Source, Diagnostic Diagnostic)>? diagnostics = null, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(forks);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(converters);
        readOptions ??= ReadOptions.Default;
        if (!overwrite && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException($"{directory} is not empty.");
        }

        // Converted first, to know whether there are several.
        var converted = new List<(ForkToExtract Entry, IDocumentConverter Converter, IReadOnlyList<DocumentFile> Files)>();
        var looked = 0;
        foreach (var entry in forks)
        {
            var file = entry.Node.File;
            var found = new List<Diagnostic>();
            var input = new DocumentInput(entry.Fork, () => file.DataFork.ToArray(readOptions.MaxResourceSize), file.FinderInfo.Type,
                file.FinderInfo.Creator, file.Name.ToString(), readOptions, found);
            if (DocumentExport.Convert(converters, input) is var (converter, files))
            {
                converted.Add((entry, converter, files));
            }

            foreach (var d in found)
            {
                diagnostics?.Add((file.MacPath, d));
            }

            progress?.Report(++looked);
        }

        var layout = new OutputLayout(name => HostNames.ToHostName(name));
        var folders = new Dictionary<ContainerNode, List<string>>(ReferenceEqualityComparer.Instance);
        foreach (var (leaf, place) in layout.Place(root))
        {
            folders[leaf] = place;
        }

        var documents = new List<(string, ManifestDocument)>();
        var failed = new List<string>();
        foreach (var (entry, converter, files) in converted)
        {
            var parts = new List<string>();
            if (converted.Count > 1)
            {
                var place = folders.GetValueOrDefault(entry.Node) ?? [];
                parts = [.. place, layout.Unique(place, HostNames.ToHostName(entry.Node.File.Name), out _)];
            }
            var target = Path.Combine([directory, .. parts]);
            try
            {
                documents.Add((entry.Node.File.MacPath,
                    DocumentExport.Write(converter, files, target, parts.Count > 0 ? string.Join('/', parts) + "/" : "")));
            }
            catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
            {
                failed.Add($"{target}: {e.Message}");
            }
        }
        return new ConvertResult(documents, failed);
    }
}

/// <summary>New folders for exports, never overwriting.</summary>
public static class ExportFolders
{
    /// <summary>Creates <c>parent/name</c>, or <c>name 2</c>, <c>name 3</c> … when it exists, and returns its path.</summary>
    public static string CreateNew(string parent, string name)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(name);
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(parent, n == 1 ? name : $"{name} {n}");
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path);
            return path;
        }
    }
}
