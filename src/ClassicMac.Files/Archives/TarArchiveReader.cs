using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicMac.Core;
using Tar = System.Formats.Tar;

namespace ClassicMac.Files.Archives;

/// <summary>
/// tar archives (POSIX ustar and pax, GNU long names, old V7), read with <see cref="Tar.TarReader"/>, with the Mac data
/// Mac OS X's tar writes: an AppleDouble <c>._name</c> entry beside each file holding its resource fork and Finder info.
/// </summary>
public sealed class TarArchiveReader : IContainerReader
{
    private const int BlockLength = 512;

    /// <summary>The built-in tar reader.</summary>
    public static TarArchiveReader Instance { get; } = new();

    private TarArchiveReader() { }

    /// <inheritdoc/>
    public string FormatName => "tar";

    /// <summary>
    /// A tar header block: its checksum (the byte sum of the block with the checksum field read as spaces, POSIX
    /// "ustar Interchange Format") matches, and it has the <c>ustar</c> magic or, for an old V7 archive, a name and a
    /// V7 type flag.
    /// </summary>
    public bool CanRead(ForkData input)
    {
        if (input.Length < BlockLength)
        {
            return false;
        }

        var block = input.ReadPrefix(BlockLength);
        if (block[0] == 0)
        {
            return false;
        }

        if (!TryParseOctal(block.AsSpan(148, 8), out var stored))
        {
            return false;
        }

        long sum = 0;
        for (var i = 0; i < BlockLength; i++)
        {
            sum += i is >= 148 and < 156 ? (byte)' ' : block[i];
        }

        if (sum != stored)
        {
            return false;
        }

        return block.AsSpan(257, 5).SequenceEqual("ustar"u8) || block[156] is 0 or (byte)'0' or (byte)'1' or (byte)'2' or (byte)'5';
    }

    /// <inheritdoc/>
    public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
    {
        if (!CanRead(input))
        {
            throw new InvalidDataException("Not a tar archive.");
        }

        var entries = new List<UnixArchiveEntry>();
        var seen = new Dictionary<string, UnixArchiveEntry>(StringComparer.Ordinal);
        long expanded = 0;
        var count = 0;
        using var stream = input.Open();
        try
        {
            using var reader = new Tar.TarReader(stream);
            while (reader.GetNextEntry() is { } entry)
            {
                if (++count > context.Options.MaxVolumeEntries)
                {
                    throw new InvalidDataException("The tar archive exceeds the configured entry limit.");
                }

                var path = UnixArchive.SplitPath(entry.Name, context, FormatName, 0);
                var modified = UnixArchive.FromUtc(entry.ModificationTime.UtcDateTime, context);
                switch (entry.EntryType)
                {
                    case Tar.TarEntryType.Directory:
                        entries.Add(new UnixArchiveEntry { Path = path, IsDirectory = true });
                        break;
                    case Tar.TarEntryType.RegularFile or Tar.TarEntryType.V7RegularFile or Tar.TarEntryType.ContiguousFile:
                        {
                            var length = entry.Length;
                            if (expanded > context.Options.MaxExpandedBytesPerInput - length || length > int.MaxValue)
                            {
                                throw new InvalidDataException("tar extraction exceeds the configured expanded-size limit.");
                            }

                            expanded += length;
                            var data = new byte[length];
                            var read = entry.DataStream?.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) ?? 0;
                            if (read < data.Length)
                            {
                                context.Report(DiagnosticSeverity.Error, "archive.truncated",
                                    $"The tar entry \"{entry.Name}\" ends after {read} of its {length} bytes; they are kept.");
                                data = data[..read];
                            }
                            var file = new UnixArchiveEntry { Path = path, Data = ForkData.FromBytes(data), Modified = modified };
                            seen[string.Join('/', path)] = file;
                            entries.Add(file);
                            break;
                        }
                    case Tar.TarEntryType.SymbolicLink:
                        entries.Add(new UnixArchiveEntry { Path = path, SymbolicLinkTarget = entry.LinkName, Modified = modified });
                        break;
                    case Tar.TarEntryType.HardLink:
                        // A hard link names an earlier entry; the file is copied.
                        if (seen.TryGetValue(string.Join('/', UnixArchive.SplitPath(entry.LinkName, context, FormatName, 0)), out var target))
                        {
                            entries.Add(new UnixArchiveEntry { Path = path, Data = target.Data, Modified = modified });
                        }
                        else
                        {
                            context.Report(DiagnosticSeverity.Warning, "archive.link-target-missing",
                                $"The tar hard link \"{entry.Name}\" names \"{entry.LinkName}\", which is not earlier in the archive; skipped.");
                        }
                        break;
                    case Tar.TarEntryType.GlobalExtendedAttributes:
                        break;
                    default:
                        context.Report(DiagnosticSeverity.Info, "archive.entry-skipped",
                            $"The tar entry \"{entry.Name}\" is a {entry.EntryType}, not a file or folder; skipped.");
                        break;
                }
            }
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"The tar archive is damaged: {e.Message}", e);
        }
        catch (EndOfStreamException e)
        {
            context.Report(DiagnosticSeverity.Error, "archive.truncated", $"The tar archive is truncated: {e.Message}");
        }
        return UnixArchive.ToMacFiles(entries, context, FormatName);
    }

    // A tar number field: octal digits, with leading spaces and a trailing NUL or space.
    private static bool TryParseOctal(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        var text = Encoding.ASCII.GetString(field).Trim(' ', '\0');
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '7')
            {
                return false;
            }

            value = value * 8 + (c - '0');
        }
        return true;
    }
}
