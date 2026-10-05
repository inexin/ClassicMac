using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Resources.Export;

/// <summary>
/// Choices for exporting resources to a folder (<see cref="ResourceExporter"/>). Every tunable value lives here; the
/// CLI and the app map their settings onto this record.
/// </summary>
public sealed record ExportOptions
{
    /// <summary>The defaults.</summary>
    public static ExportOptions Default { get; } = new();

    /// <summary>The encoding of Mac names (files' and resources') as host names (text-encodings.md §5). Default Mac OS Roman.</summary>
    public ClassicMac.Core.MacTextEncoding NameEncoding { get; init; }

    /// <summary>The longest a written path may be, counted from the export folder. Default 200.</summary>
    public int MaxPathLength { get; init; } = 200;

    /// <summary>Whether the stored bytes of every resource are also kept in <c>raw/</c>, for packing. Default false.</summary>
    public bool KeepRaw { get; init; }

    /// <summary>Only resources of these types, or null (the default) for all.</summary>
    public IReadOnlySet<FourCC>? Types { get; init; }

    /// <summary>Whether an export folder that already has files in it may be written into. Default false.</summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// The decoders tried for each resource, in order (the first that handles its type is used); empty (the default)
    /// exports every resource raw. <c>ClassicMac.Resources.Decoders</c> provides the built-in ones.
    /// </summary>
    public IReadOnlyList<IResourceDecoder> Decoders { get; init; } = [];

    /// <summary>
    /// The converters tried for the whole file, in order (the first that finds a document in it is used); the document
    /// goes to <c>document/</c> beside the resources. Empty (the default) converts none. Used only when every type is
    /// exported (<see cref="Types"/> is null).
    /// </summary>
    public IReadOnlyList<IDocumentConverter> Documents { get; init; } = [];

    /// <summary>
    /// The decoders for a file whose volume says its text is in an encoding (<see cref="ExportSource.TextEncoding"/>,
    /// text-encodings.md §5), in place of <see cref="Decoders"/>; null (the default) uses <see cref="Decoders"/> for every
    /// file. <c>ClassicMac.Resources.Decoders</c>' <c>ResourceDecoders.CreateFor</c> makes it.
    /// </summary>
    public Func<MacTextEncoding, IReadOnlyList<IResourceDecoder>>? DecodersFor { get; init; }

    /// <summary>The document converters for such a file, in place of <see cref="Documents"/>, as <see cref="DecodersFor"/>.</summary>
    public Func<MacTextEncoding, IReadOnlyList<IDocumentConverter>>? DocumentsFor { get; init; }

    /// <summary>Limits and the Resource Manager model for decompressing resources.</summary>
    public ReadOptions ReadOptions { get; init; } = ReadOptions.Default;
}

/// <summary>Where an exported fork came from, as its manifest records it.</summary>
/// <param name="Name">The Mac file's name.</param>
/// <param name="Formats">The containers it was found through, outermost first.</param>
/// <param name="Type">The file type.</param>
/// <param name="Creator">The creator.</param>
/// <param name="Flags">The Finder flags.</param>
public sealed record ExportSource(MacString Name, IReadOnlyList<string> Formats, FourCC Type, FourCC Creator, ushort Flags)
{
    /// <summary>The encoding the file's volume says its text is in (text-encodings.md §5), or null.</summary>
    public MacTextEncoding? TextEncoding { get; init; }
}
