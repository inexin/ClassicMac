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

        /// <summary>The most files and folders read from one volume, a guard against damaged catalogs. Default 1,000,000.</summary>
        public int MaxVolumeEntries { get; init; } = 1_000_000;

        /// <summary>
        /// The zone of the Mac that reads the files, used where a container stores absolute (UTC) dates — AppleSingle
        /// and AppleDouble version 2 — since Mac dates are local time. Default: this machine's zone.
        /// </summary>
        public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

        /// <summary>
        /// The extension map File Exchange applies when it shows files on DOS disks, or null (the default) for the type
        /// and creator stored on the disk.
        /// </summary>
        public Containers.ExtensionMap? ExtensionMap { get; init; }

        /// <summary>
        /// Whether whole-image checksums that cost a full read (NDIF's CRC-32) are verified. Default false: Disk Copy's
        /// driver never checks them, only its "Verify checksum" setting does.
        /// </summary>
        public bool VerifyChecksums { get; init; }

        /// <summary>The MacRoman password used by encrypted archive formats that accept a password; null by default.</summary>
        public string? ArchivePassword { get; init; }
    }
}
