using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reassembles files split across StuffIt volumes.</summary>
public sealed class StuffItSplitReader : IContainerReader
{
    private const int HeaderLength = 100;
    private const int SharedMetadataOffset = 68;
    private const int SharedMetadataLength = 26;

    /// <summary>The built-in reader.</summary>
    public static StuffItSplitReader Instance { get; } = new();

    private StuffItSplitReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "StuffIt split file";

    /// <inheritdoc/>
    public bool CanRead(ForkData input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length < HeaderLength)
        {
            return false;
        }

        try
        {
            return TryReadHeader(input.Slice(0, HeaderLength).ToArray(HeaderLength), out _);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
            OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        return ReadCore(input, context, context.HostName);
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(MacFile file, ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(context);
        return ReadCore(file.DataFork, context, context.HostName ?? file.Name);
    }

    private static IReadOnlyList<MacFile> ReadCore(ForkData input, ContainerContext context, MacString? hostName)
    {
        long maximumBytes = context.Options.MaxExpandedBytesPerInput;
        if (input.Length > maximumBytes)
        {
            throw new InvalidDataException("The StuffIt split file exceeds the configured input-size limit.");
        }

        byte[] inputBytes = input.ToArray(maximumBytes);
        if (!TryReadHeader(inputBytes, out SplitHeader? parsedHeader) || parsedHeader is null)
        {
            throw new InvalidDataException("Not a StuffIt split file.");
        }

        SplitHeader firstHeader = parsedHeader;

        var volumes = new SortedDictionary<byte, byte[]> { [firstHeader.PartNumber] = inputBytes };
        long totalVolumeBytes = inputBytes.Length;
        string? basename = hostName is { } name ? Path.GetFileNameWithoutExtension(name.ToMacRoman()) : null;

        foreach (MacFile sibling in context.Siblings?.Invoke() ?? [])
        {
            if (basename is { Length: > 0 } &&
                !Path.GetFileName(sibling.Name.ToMacRoman()).StartsWith(basename, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ForkData siblingFork = sibling.DataFork;
            if (siblingFork.Length < HeaderLength)
            {
                continue;
            }

            byte[] headerBytes = siblingFork.Slice(0, HeaderLength).ToArray(HeaderLength);
            if (!TryReadHeader(headerBytes, out SplitHeader? siblingHeader) || siblingHeader is null ||
                !firstHeader.Matches(siblingHeader))
            {
                continue;
            }

            if (volumes.ContainsKey(siblingHeader.PartNumber))
            {
                throw new InvalidDataException(
                    $"More than one StuffIt split volume identifies part {siblingHeader.PartNumber}.");
            }

            if (siblingFork.Length > maximumBytes - totalVolumeBytes)
            {
                throw new InvalidDataException("The StuffIt split volume set exceeds the configured input-size limit.");
            }

            volumes.Add(siblingHeader.PartNumber, siblingFork.ToArray(maximumBytes));
            totalVolumeBytes = checked(totalVolumeBytes + siblingFork.Length);
        }

        byte highestPart = volumes.Keys.Max();
        for (int partNumber = 1; partNumber <= highestPart; partNumber++)
        {
            if (volumes.ContainsKey(checked((byte)partNumber)))
            {
                continue;
            }

            context.Report(DiagnosticSeverity.Warning, "archive.missing-volume",
                $"The StuffIt split file is missing volume {partNumber}.");
            return [];
        }

        uint resourceLengthRaw = firstHeader.ResourceLength;
        uint dataLengthRaw = firstHeader.DataLength;
        ulong totalForkLength = (ulong)resourceLengthRaw + dataLengthRaw;
        if (resourceLengthRaw > int.MaxValue || dataLengthRaw > int.MaxValue || totalForkLength > int.MaxValue)
        {
            throw new InvalidDataException("The StuffIt split file fork lengths are too large.");
        }

        if (totalForkLength > (ulong)maximumBytes)
        {
            throw new InvalidDataException("The StuffIt split file exceeds the configured expanded-size limit.");
        }

        int resourceLength = (int)resourceLengthRaw;
        int dataLength = (int)dataLengthRaw;
        byte[] combinedForks = new byte[(int)totalForkLength];
        int written = 0;
        foreach (byte[] volume in volumes.Values)
        {
            int payloadLength = volume.Length - HeaderLength;
            int count = Math.Min(payloadLength, combinedForks.Length - written);
            if (count > 0)
            {
                volume.AsSpan(HeaderLength, count).CopyTo(combinedForks.AsSpan(written));
                written += count;
            }
            if (written == combinedForks.Length)
            {
                break;
            }
        }

        if (written != combinedForks.Length)
        {
            context.Report(DiagnosticSeverity.Warning, "archive.missing-volume",
                "The StuffIt split volume set ends before the declared forks are complete.");
            return [];
        }

        var metadata = new BigEndianReader(firstHeader.Metadata);
        uint createdRaw = metadata.ReadUInt32At(10);
        uint modifiedRaw = metadata.ReadUInt32At(14);
        return
        [
            new MacFile
            {
                Name = new MacString(firstHeader.NameBytes),
                FinderInfo = new FinderInfo
                {
                    Type = new FourCC(firstHeader.Metadata.AsSpan(0, 4)),
                    Creator = new FourCC(firstHeader.Metadata.AsSpan(4, 4)),
                    Flags = (FinderFlags)metadata.ReadUInt16At(8),
                },
                Created = createdRaw == 0 ? null : new MacDate(createdRaw),
                Modified = modifiedRaw == 0 ? null : new MacDate(modifiedRaw),
                ResourceFork = ForkData.FromBytes(combinedForks.AsSpan(0, resourceLength).ToArray()),
                DataFork = ForkData.FromBytes(combinedForks.AsSpan(resourceLength, dataLength).ToArray()),
            }
        ];
    }

    private static bool TryReadHeader(ReadOnlyMemory<byte> data, out SplitHeader? header)
    {
        var bytes = data.Span;
        header = null;
        // $B056 is SegmentIt's magic; $41A7 is StuffIt 1.5.1's own Segment command. Both use the same layout. The
        // 1.5.1 headers leave the bytes after the name and after the metadata (94–99) uninitialised, so only the
        // name's length bytes and 68–93 are read. [Fitted] to StuffIt 1.5.1's segments.
        if (bytes.Length < HeaderLength || !IsMagic(bytes[0], bytes[1]) || bytes[2] != 0 || bytes[3] == 0)
        {
            return false;
        }

        int nameLength = bytes[4];
        if (nameLength is 0 or > 63)
        {
            return false;
        }

        ReadOnlySpan<byte> nameBytes = bytes.Slice(5, nameLength);
        foreach (byte value in nameBytes)
        {
            if (value == 0)
            {
                return false;
            }
        }

        byte[] metadata = bytes.Slice(SharedMetadataOffset, SharedMetadataLength).ToArray();
        var reader = new BigEndianReader(metadata);
        header = new SplitHeader(new BigEndianReader(data).ReadUInt16At(0), bytes[3], nameBytes.ToArray(), metadata,
            reader.ReadUInt32At(18), reader.ReadUInt32At(22));
        return true;
    }

    private static bool IsMagic(byte high, byte low) => (high, low) is (0xB0, 0x56) or (0x41, 0xA7);

    private sealed record SplitHeader(ushort Magic, byte PartNumber, byte[] NameBytes, byte[] Metadata,
        uint ResourceLength, uint DataLength)
    {
        public bool Matches(SplitHeader other) => Magic == other.Magic &&
            NameBytes.AsSpan().SequenceEqual(other.NameBytes) && Metadata.AsSpan().SequenceEqual(other.Metadata);
    }
}
