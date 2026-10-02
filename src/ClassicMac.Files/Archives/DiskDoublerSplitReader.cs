using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Archives;

/// <summary>Reassembles a file that DiskDoubler's Split command cut into parts (<c>.1</c>, <c>.2</c> …).</summary>
/// <remarks>
/// [Fitted] to DiskDoubler 3.7.7 and Pro 4.1.1 split sets of the CC0 DiskDoubler test corpus; no specification or code
/// was read. Each part is a 94-byte header, then a slice of the source's data fork followed by its resource fork:
/// <code>
///  0 'SPLT'              4 u32 set identifier (Pro 4.1.1: $0002xxxx; 3.7.7: $0000xxxx)
///  8 u32 data length    12 u32 resource length
/// 16 FInfo (16 bytes)   32 u32 created   36 u32 modified
/// 40 u16 part count     42 u16 part index (from 0)   44 u32 payload length
/// 48 u16 CRC-16/XMODEM of the payload   50 zeros   90 'SPLT'
/// </code>
/// </remarks>
public sealed class DiskDoublerSplitReader : IContainerReader
{
    private const int HeaderLength = 94;
    private const uint Magic = 0x53504C54; // 'SPLT'
    private const int SharedStart = 4;
    private const int SharedLength = 38; // bytes 4–41: identifier, fork lengths, Finder info, dates, part count

    /// <summary>The built-in reader.</summary>
    public static DiskDoublerSplitReader Instance { get; } = new();

    private DiskDoublerSplitReader()
    {
    }

    /// <inheritdoc/>
    public string FormatName => "DiskDoubler split file";

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
            return TryReadHeader(input.Slice(0, HeaderLength).ToArray(HeaderLength), input.Length, out _);
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
            throw new InvalidDataException("The DiskDoubler split file exceeds the configured input-size limit.");
        }

        byte[] inputBytes = input.ToArray(maximumBytes);
        if (!TryReadHeader(inputBytes, inputBytes.Length, out PartHeader? first) || first is null)
        {
            throw new InvalidDataException("Not a DiskDoubler split file.");
        }

        // The parts carry no name: the set is named by its host files, "name.1", "name.2" …
        string? setName = hostName is { } name ? StripPartNumber(name.ToMacRoman()) : null;
        var parts = new byte[]?[first.PartCount];
        parts[first.PartIndex] = inputBytes;
        long totalBytes = inputBytes.Length;
        foreach (MacFile sibling in context.Siblings?.Invoke() ?? [])
        {
            if (setName is { Length: > 0 } &&
                !string.Equals(StripPartNumber(sibling.Name.ToMacRoman()), setName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ForkData fork = sibling.DataFork;
            if (fork.Length < HeaderLength)
            {
                continue;
            }

            if (!TryReadHeader(fork.Slice(0, HeaderLength).ToArray(HeaderLength), fork.Length,
                    out PartHeader? header) || header is null || !first.SharedBytes.AsSpan().SequenceEqual(header.SharedBytes))
            {
                continue;
            }

            if (parts[header.PartIndex] is not null)
            {
                throw new InvalidDataException(
                    $"More than one DiskDoubler split part is part {header.PartIndex + 1}.");
            }

            if (fork.Length > maximumBytes - totalBytes)
            {
                throw new InvalidDataException("The DiskDoubler split set exceeds the configured input-size limit.");
            }

            parts[header.PartIndex] = fork.ToArray(maximumBytes);
            totalBytes += fork.Length;
        }

        for (int index = 0; index < parts.Length; index++)
        {
            if (parts[index] is not null)
            {
                continue;
            }

            context.Report(DiagnosticSeverity.Warning, "archive.missing-volume",
                $"The DiskDoubler split file is missing part {index + 1} of {parts.Length}.");
            return [];
        }

        long forkTotal = (long)first.DataLength + first.ResourceLength;
        if (forkTotal > maximumBytes || forkTotal > int.MaxValue)
        {
            throw new InvalidDataException("The DiskDoubler split file exceeds the configured expanded-size limit.");
        }

        byte[] forks = new byte[forkTotal];
        int written = 0;
        for (int index = 0; index < parts.Length; index++)
        {
            byte[] part = parts[index]!;
            var header = new BigEndianReader(part);
            int payloadLength = checked((int)header.ReadUInt32At(44));
            ReadOnlySpan<byte> payload = part.AsSpan(HeaderLength, payloadLength);
            if (header.ReadUInt16At(48) != Crc16Xmodem(payload))
            {
                context.Report(DiagnosticSeverity.Error, "archive.fork-checksum",
                    $"DiskDoubler split part {index + 1} has a CRC-16 mismatch; its data is retained.");
            }

            if (payloadLength > forks.Length - written)
            {
                throw new InvalidDataException("The DiskDoubler split parts hold more than the declared forks.");
            }

            payload.CopyTo(forks.AsSpan(written));
            written += payloadLength;
        }
        if (written != forks.Length)
        {
            context.Report(DiagnosticSeverity.Warning, "archive.missing-volume",
                "The DiskDoubler split parts end before the declared forks are complete.");
            return [];
        }

        int dataLength = (int)first.DataLength;
        var shared = new BigEndianReader(inputBytes);
        return
        [
            new MacFile
            {
                Name = MacString.FromMacRoman(setName is { Length: > 0 } ? setName : "Untitled"),
                FinderInfo = FinderInfo.Read(inputBytes.AsSpan(16, 16)),
                Created = Date(shared.ReadUInt32At(32)),
                Modified = Date(shared.ReadUInt32At(36)),
                DataFork = ForkData.FromBytes(forks.AsMemory(0, dataLength)),
                ResourceFork = ForkData.FromBytes(forks.AsMemory(dataLength)),
            },
        ];
    }

    private static bool TryReadHeader(byte[] bytes, long inputLength, out PartHeader? header)
    {
        header = null;
        if (bytes.Length < HeaderLength)
        {
            return false;
        }

        var reader = new BigEndianReader(bytes);
        if (reader.ReadUInt32At(0) != Magic || reader.ReadUInt32At(90) != Magic)
        {
            return false;
        }

        ushort partCount = reader.ReadUInt16At(40);
        ushort partIndex = reader.ReadUInt16At(42);
        uint payloadLength = reader.ReadUInt32At(44);
        if (partCount == 0 || partIndex >= partCount || payloadLength > inputLength - HeaderLength)
        {
            return false;
        }

        header = new PartHeader(bytes.AsSpan(SharedStart, SharedLength).ToArray(), partCount, partIndex,
            reader.ReadUInt32At(8), reader.ReadUInt32At(12));
        return true;
    }

    // "name.2" → "name"; a name without a numeric extension is kept.
    private static string StripPartNumber(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return name;
        }

        for (int index = dot + 1; index < name.Length; index++)
        {
            if (!char.IsAsciiDigit(name[index]))
            {
                return name;
            }
        }

        return name[..dot];
    }

    private static ushort Crc16Xmodem(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (byte value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) == 0 ? crc << 1 : (crc << 1) ^ 0x1021);
            }
        }
        return crc;
    }

    private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);

    private sealed record PartHeader(byte[] SharedBytes, int PartCount, int PartIndex, uint DataLength,
        uint ResourceLength);
}
