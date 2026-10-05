using System;

namespace ClassicMac.Files.Hfs;

/// <summary>The kinds of NDIF image Disk Copy 6.3.3 saves (docs/formats/disk-images/ndif.md §4.3).</summary>
public enum NdifFormat
{
    /// <summary>Read/Write (<c>'dimg'</c>): the whole disk as one raw chunk, no checksum.</summary>
    ReadWrite,

    /// <summary>Read-Only (<c>'rohd'</c>): the used area raw, free space left out, with a checksum.</summary>
    ReadOnly,

    /// <summary>Read-Only Compressed with ADC, Disk Copy's "Faster (ADC)" (map version 11).</summary>
    Adc,

    /// <summary>Read-Only Compressed with KenCode, Disk Copy's "Smaller (KC)" (map version 10).</summary>
    KenCode,
}

/// <summary>How <see cref="NdifWriter.Create"/> makes an image.</summary>
public sealed record NdifCreateOptions
{
    /// <summary>A compressed (ADC) image in chunks of 512 sectors, as Disk Copy 6.3.3 saves one by default.</summary>
    public static NdifCreateOptions Default { get; } = new();

    /// <summary>The kind of image. Default <see cref="NdifFormat.Adc"/>.</summary>
    public NdifFormat Format { get; init; } = NdifFormat.Adc;

    /// <summary>
    /// The sectors in each compressed chunk: 512 by default, as in Disk Copy 6.3.3 (6.1.2 used 32). At least 1.
    /// </summary>
    public int ChunkSectors { get; init; } = 512;

    /// <summary>When the image is made, for a segmented image's ID; null for now.</summary>
    public DateTime? Created { get; init; }
}
