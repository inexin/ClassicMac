using System;

namespace ClassicMac.Files
{
    /// <summary>
    /// Choices for writing Mac files to the host's disk (<see cref="HostFiles.Write"/>). Every tunable value lives here;
    /// the CLI and the app map their settings onto this record.
    /// </summary>
    public sealed record HostWriteOptions
    {
        /// <summary>The defaults.</summary>
        public static HostWriteOptions Default { get; } = new();

        /// <summary>
        /// How the resource fork and Finder info are stored: <see cref="HostLayout.AppleDouble"/> (the default: a
        /// <c>._</c> file beside the data) or <see cref="HostLayout.BasiliskII"/> (<c>.rsrc/</c> and <c>.finf/</c>).
        /// </summary>
        public HostLayout Layout { get; init; } = HostLayout.AppleDouble;

        /// <summary>The longest a written path may be, counted from the folder written into. Default 200.</summary>
        public int MaxPathLength { get; init; } = 200;

        /// <summary>Whether existing files may be replaced. Default false.</summary>
        public bool Overwrite { get; init; }

        /// <summary>The zone Mac dates (local time) are taken to be in when written as absolute times. Default: this machine's.</summary>
        public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;
    }
}
