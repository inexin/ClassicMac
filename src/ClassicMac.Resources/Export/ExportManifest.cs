using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ClassicMac.Resources.Export
{
    /// <summary>
    /// <c>manifest.json</c>, format 1: what an export holds and where each resource came from, so tools (and a later
    /// <c>pack</c>) can rebuild the fork. Described by <c>schemas/manifest-1.schema.json</c>; new optional fields are a
    /// minor version, anything else a new major one.
    /// </summary>
    /// <param name="Schema">The schema's URL.</param>
    /// <param name="FormatVersion">"major.minor".</param>
    /// <param name="Source">The file the fork came from.</param>
    /// <param name="Fork">The fork's own attributes.</param>
    /// <param name="Resources">The exported resources, in the fork's order.</param>
    /// <param name="Diagnostics">Problems found reading the fork and exporting it.</param>
    /// <param name="Document">The file converted as a whole document, or null (format 1.2).</param>
    public sealed record ExportManifest(
        [property: JsonPropertyName("$schema")] string Schema,
        string FormatVersion,
        ManifestSource Source,
        ManifestFork Fork,
        IReadOnlyList<ManifestResource> Resources,
        IReadOnlyList<ManifestDiagnostic> Diagnostics,
        ManifestDocument? Document = null)
    {
        /// <summary>The current format's version.</summary>
        public const string CurrentVersion = "1.2";

        /// <summary>Where the format's schema is published.</summary>
        public const string SchemaUrl = "https://raw.githubusercontent.com/inexin/ClassicMac/main/schemas/manifest-1.schema.json";
    }

    /// <summary>The file a fork came from.</summary>
    /// <param name="Name">The Mac name, bytes outside printable Mac Roman as <c>\xHH</c>.</param>
    /// <param name="Formats">The containers it was found through, outermost first.</param>
    /// <param name="Type">The file type (four characters, <c>\xHH</c> escapes).</param>
    /// <param name="Creator">The creator.</param>
    /// <param name="Flags">The Finder flags.</param>
    public sealed record ManifestSource(string Name, IReadOnlyList<string> Formats, string Type, string Creator, int Flags);

    /// <summary>The fork's attributes and map flags, as stored.</summary>
    /// <param name="Attributes">The resource map's attributes byte.</param>
    /// <param name="MapFlags">The map's in-memory flags byte.</param>
    public sealed record ManifestFork(int Attributes, int MapFlags);

    /// <summary>One exported resource.</summary>
    /// <param name="Type">The type as text (<c>\xHH</c> escapes).</param>
    /// <param name="TypeBytes">The type's four bytes in hex.</param>
    /// <param name="Id">The ID.</param>
    /// <param name="Name">The name, or null (<c>\xHH</c> escapes).</param>
    /// <param name="Attributes">The attributes byte, as stored.</param>
    /// <param name="Size">The main file's length.</param>
    /// <param name="StoredSize">The data's length as stored in the fork.</param>
    /// <param name="Dcmp">The decompressor's ID for a compressed resource, or null.</param>
    /// <param name="Decoder">What wrote the files: <c>raw</c> for the data itself (decompressed), or the decoder's name.</param>
    /// <param name="DecoderVersion">The decoder's version.</param>
    /// <param name="Path">The main file, relative to the manifest, <c>/</c>-separated.</param>
    /// <param name="Sha256">SHA-256 of the main file.</param>
    /// <param name="StoredSha256">SHA-256 of the data as stored.</param>
    /// <param name="RawPath">The stored bytes' copy in <c>raw/</c>, or null.</param>
    /// <param name="Warnings">Problems with this resource.</param>
    /// <param name="OtherFiles">Files the decoder wrote besides the main one (format 1.1).</param>
    /// <param name="Encoding">The text encoding the resource was read with, as an IANA name, or null (format 1.1).</param>
    public sealed record ManifestResource(
        string Type, string TypeBytes, short Id, string? Name, int Attributes, int Size, int StoredSize, short? Dcmp,
        string Decoder, int DecoderVersion, string Path, string Sha256, string StoredSha256, string? RawPath,
        IReadOnlyList<string> Warnings, IReadOnlyList<ManifestFile>? OtherFiles = null, string? Encoding = null);

    /// <summary>A document converted from the whole file (format 1.2).</summary>
    /// <param name="Converter">The converter's name.</param>
    /// <param name="ConverterVersion">The converter's version.</param>
    /// <param name="Path">The entry page, relative to the manifest, <c>/</c>-separated.</param>
    /// <param name="Files">Every file of the document, the entry page first.</param>
    public sealed record ManifestDocument(string Converter, int ConverterVersion, string Path, IReadOnlyList<ManifestFile> Files);

    /// <summary>A file written for a resource or a document.</summary>
    /// <param name="Path">Relative to the manifest, <c>/</c>-separated.</param>
    /// <param name="Sha256">SHA-256 of the file.</param>
    public sealed record ManifestFile(string Path, string Sha256);

    /// <summary>A problem found while reading or exporting.</summary>
    /// <param name="Severity">info, warning or error.</param>
    /// <param name="Code">The diagnostic's code.</param>
    /// <param name="Message">What happened.</param>
    public sealed record ManifestDiagnostic(string Severity, string Code, string Message);

    /// <summary>Source-generated JSON for the manifest (trimming- and AOT-safe).</summary>
    // LF on every platform, so manifests and their hashes are the same everywhere.
    [JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n", PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
    [JsonSerializable(typeof(ExportManifest))]
    public sealed partial class ExportManifestJson : JsonSerializerContext
    {
    }
}
