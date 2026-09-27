using System.Collections.Generic;
using ClassicMac.Resources.Decoders.Text;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders
{
    /// <summary>The text encodings resources can be read with.</summary>
    public enum MacTextEncoding
    {
        /// <summary>Mac OS Roman (IANA <c>macintosh</c>).</summary>
        Roman,
    }

    /// <summary>How line breaks (CR on the Mac) are written in text output.</summary>
    public enum LineEndings
    {
        /// <summary>LF, as modern tools expect.</summary>
        Lf,

        /// <summary>As stored: CR.</summary>
        AsStored,
    }

    /// <summary>
    /// Choices for the built-in decoders. Every tunable value lives here; the CLI and the app map their settings onto
    /// this record.
    /// </summary>
    public sealed record DecodeOptions
    {
        /// <summary>The defaults.</summary>
        public static DecodeOptions Default { get; } = new();

        /// <summary>The encoding text resources are read with. Default Mac OS Roman.</summary>
        public MacTextEncoding TextEncoding { get; init; } = MacTextEncoding.Roman;

        /// <summary>How line breaks are written in <c>.txt</c> output. Default LF.</summary>
        public LineEndings LineEndings { get; init; } = LineEndings.Lf;
    }

    /// <summary>The built-in decoders.</summary>
    public static class ResourceDecoders
    {
        /// <summary>The built-in decoders with <paramref name="options"/>, for <see cref="ExportOptions.Decoders"/>.</summary>
        public static IReadOnlyList<IResourceDecoder> Create(DecodeOptions? options = null)
        {
            options ??= DecodeOptions.Default;
            return
            [
                new StringDecoder(options),
                new StringListDecoder(options),
                new TextDecoder(options),
                new StyleDecoder(),
                new VersionDecoder(options),
            ];
        }
    }
}
