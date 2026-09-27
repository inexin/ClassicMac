namespace ClassicMac.Resources
{
    /// <summary>
    /// Limits and choices for reading. Every tunable value lives here rather than in the readers; the CLI and the app
    /// map their settings onto this record.
    /// </summary>
    public sealed record ReadOptions
    {
        /// <summary>The defaults.</summary>
        public static ReadOptions Default { get; } = new();

        /// <summary>The largest a single resource may be once decompressed, in bytes. Default 64 MiB.</summary>
        public long MaxResourceSize { get; init; } = 64L * 1024 * 1024;

        /// <summary>How deep containers may nest (a BinHex file holding a disk image is depth 2). Default 8.</summary>
        public int MaxNestingDepth { get; init; } = 8;

        /// <summary>The most bytes decompression and unwrapping may produce from one input. Default 1 GiB.</summary>
        public long MaxExpandedBytesPerInput { get; init; } = 1024L * 1024 * 1024;
    }
}
