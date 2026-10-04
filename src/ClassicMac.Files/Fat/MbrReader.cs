using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Fat;

/// <summary>
/// DOS (MBR) partition tables, as on PC hard disks and removable media: 55AA at the end of sector 0 and four
/// 16-byte entries from offset 446 (status, type, start and length in 512-byte sectors). Each FAT partition comes
/// out as one file whose data fork is the volume, for <see cref="FatReader"/> to open next; other types are skipped.
/// </summary>
public sealed class MbrReader : IContainerReader
{
    private const int Sector = 512;
    private const int Table = 446;

    /// <summary>The reader.</summary>
    public static MbrReader Instance { get; } = new();

    private MbrReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "DOS partition table";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        var boot = input.ReadPrefix(Sector);
        if (boot.Length < Sector || boot[510] != 0x55 || boot[511] != 0xAA)
        {
            return false;
        }
        // A FAT volume without a table has a boot sector in its place.
        if (FatReader.Geometry.TryRead(boot, input.Length, out _))
        {
            return false;
        }

        var any = false;
        for (var i = 0; i < 4; i++)
        {
            var entry = boot.AsSpan(Table + i * 16, 16);
            if (entry[0] is not (0x00 or 0x80))
            {
                return false;
            }

            if (IsFat(entry[4]) && BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) > 0)
            {
                any = true;
            }
        }
        return any;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        if (!CanRead(input))
        {
            throw new InvalidDataException("Not a DOS partition table.");
        }

        var boot = input.ReadPrefix(Sector);
        var files = new List<MacFile>();
        for (var i = 0; i < 4; i++)
        {
            var entry = boot.AsSpan(Table + i * 16, 16);
            var type = entry[4];
            if (type == 0)
            {
                continue;
            }

            if (!IsFat(type))
            {
                context.Report(DiagnosticSeverity.Info, "mbr.skipped", $"Partition {i + 1} (type {type:X2}) is not FAT; skipped.", Table + i * 16);
                continue;
            }
            var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) * Sector;
            var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) * Sector;
            if (offset == 0 || offset >= input.Length)
            {
                context.Report(DiagnosticSeverity.Error, "mbr.outside", $"Partition {i + 1} starts outside the image.", Table + i * 16);
                continue;
            }
            if (offset + length > input.Length)
            {
                context.Report(DiagnosticSeverity.Error, "mbr.truncated", $"Partition {i + 1} runs past the end of the image.", Table + i * 16);
                length = input.Length - offset;
            }
            files.Add(new MacFile { Name = MacString.FromMacRoman($"Partition {i + 1}"), DataFork = input.Slice(offset, length) });
        }
        return files;
    }

    // FAT12, FAT16 (small, large, LBA) and FAT32 (CHS, LBA).
    private static bool IsFat(byte type) => type is 0x01 or 0x04 or 0x06 or 0x0B or 0x0C or 0x0E;
}
