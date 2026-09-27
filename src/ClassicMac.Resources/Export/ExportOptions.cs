using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Resources.Export
{
    /// <summary>
    /// Choices for exporting resources to a folder (<see cref="ResourceExporter"/>). Every tunable value lives here; the
    /// CLI and the app map their settings onto this record.
    /// </summary>
    public sealed record ExportOptions
    {
        /// <summary>The defaults.</summary>
        public static ExportOptions Default { get; } = new();

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

        /// <summary>Limits and the Resource Manager model for decompressing resources.</summary>
        public ReadOptions ReadOptions { get; init; } = ReadOptions.Default;
    }

    /// <summary>Where an exported fork came from, as its manifest records it.</summary>
    /// <param name="Name">The Mac file's name.</param>
    /// <param name="Formats">The containers it was found through, outermost first.</param>
    /// <param name="Type">The file type.</param>
    /// <param name="Creator">The creator.</param>
    /// <param name="Flags">The Finder flags.</param>
    public sealed record ExportSource(MacString Name, IReadOnlyList<string> Formats, FourCC Type, FourCC Creator, ushort Flags);
}
