using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ClassicMac.Core;

namespace ClassicMac.Resources.Export
{
    /// <summary>
    /// Turns a whole file that is a document (a DOCMaker or SimpleText document, …) into a folder of modern files, for
    /// <see cref="ResourceExporter"/> and the <c>convert</c> command. The built-in one lives in
    /// <c>ClassicMac.Resources.Decoders</c>.
    /// </summary>
    public interface IDocumentConverter
    {
        /// <summary>The converter's name, recorded in the manifest.</summary>
        string Name { get; }

        /// <summary>The converter's version, recorded in the manifest; raised when its output changes.</summary>
        int Version { get; }

        /// <summary>
        /// The document's files, the entry page first; empty when the file holds no document this converter knows.
        /// Problems go to <see cref="DocumentInput.Diagnostics"/>.
        /// </summary>
        IReadOnlyList<DocumentFile> Convert(DocumentInput input);
    }

    /// <summary>One file of a converted document: its path in the document's folder ('/'-separated) and its content.</summary>
    /// <param name="Path">The path, relative to the document's folder.</param>
    /// <param name="Content">The bytes.</param>
    public sealed record DocumentFile(string Path, ReadOnlyMemory<byte> Content);

    /// <summary>What a document converter works with: a Mac file's resource fork, its data fork on demand, and its Finder info.</summary>
    /// <param name="Fork">The resource fork.</param>
    /// <param name="ReadDataFork">Reads the data fork (it may be large, so only when the converter needs it); it may throw
    /// <see cref="IOException"/> or <see cref="InvalidDataException"/>.</param>
    /// <param name="Type">The file type.</param>
    /// <param name="Creator">The creator.</param>
    /// <param name="Title">The file's name.</param>
    /// <param name="ReadOptions">Limits for decompressing resources.</param>
    /// <param name="Diagnostics">Where problems go.</param>
    public sealed record DocumentInput(ResourceFork Fork, Func<ReadOnlyMemory<byte>> ReadDataFork, FourCC Type, FourCC Creator, string Title,
        ReadOptions ReadOptions, ICollection<Diagnostic> Diagnostics);

    /// <summary>Converts a file with the first converter that knows it and writes the document's files.</summary>
    public static class DocumentExport
    {
        /// <summary>
        /// Converts <paramref name="input"/> with the first of <paramref name="converters"/> that gives files, and writes
        /// them into <paramref name="directory"/>; null when none does. The manifest entry's paths start with
        /// <paramref name="prefix"/> (the folder's path relative to the manifest, with a trailing '/', or empty).
        /// </summary>
        public static ManifestDocument? Write(IReadOnlyList<IDocumentConverter> converters, DocumentInput input, string directory, string prefix = "") =>
            Convert(converters, input) is var (converter, files) ? Write(converter, files, directory, prefix) : null;

        /// <summary>The converter that finds a document in <paramref name="input"/> and its files, or null.</summary>
        public static (IDocumentConverter Converter, IReadOnlyList<DocumentFile> Files)? Convert(IReadOnlyList<IDocumentConverter> converters, DocumentInput input)
        {
            ArgumentNullException.ThrowIfNull(converters);
            ArgumentNullException.ThrowIfNull(input);
            foreach (var converter in converters)
            {
                try
                {
                    if (converter.Convert(input) is { Count: > 0 } files)
                    {
                        return (converter, files);
                    }
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or IndexOutOfRangeException
                    or FormatException or OverflowException or NotSupportedException)
                {
                    input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "export.converter-failed",
                        $"The {converter.Name} converter failed ({e.Message}); no document written."));
                }
            }
            return null;
        }

        /// <summary>Writes a converted document's files into <paramref name="directory"/>, returning its manifest entry.</summary>
        public static ManifestDocument Write(IDocumentConverter converter, IReadOnlyList<DocumentFile> files, string directory, string prefix = "")
        {
            ArgumentNullException.ThrowIfNull(converter);
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(directory);
            var written = new List<ManifestFile>();
            foreach (var file in files)
            {
                var full = Path.Combine([directory, .. file.Path.Split('/')]);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, file.Content.ToArray());
                written.Add(new ManifestFile(prefix + file.Path, System.Convert.ToHexStringLower(SHA256.HashData(file.Content.Span))));
            }
            return new ManifestDocument(converter.Name, converter.Version, written[0].Path, written);
        }
    }
}
