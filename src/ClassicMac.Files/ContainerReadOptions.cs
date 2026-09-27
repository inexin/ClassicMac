using System;

namespace ClassicMac.Files
{
    /// <summary>
    /// Limits and choices for unwrapping containers. Every tunable value lives here rather than in the readers; the CLI
    /// and the app map their settings onto this record.
    /// </summary>
    public sealed record ContainerReadOptions
    {
        /// <summary>The defaults.</summary>
        public static ContainerReadOptions Default { get; } = new();

        /// <summary>How deep containers may nest (a BinHex file holding a disk image is depth 2). Default 8.</summary>
        public int MaxNestingDepth { get; init; } = 8;

        /// <summary>The most bytes unwrapping and decompression may produce from one input. Default 1 GiB.</summary>
        public long MaxExpandedBytesPerInput { get; init; } = 1024L * 1024 * 1024;

        /// <summary>
        /// The zone of the Mac that reads the files, used where a container stores absolute (UTC) dates — AppleSingle
        /// and AppleDouble version 2 — since Mac dates are local time. Default: this machine's zone.
        /// </summary>
        public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    }
}
