using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files.Containers
{
    /// <summary>
    /// AppleSingle and AppleDouble, from Apple's <i>AppleSingle/AppleDouble Formats for Foreign Files</i> Developer Note
    /// (version 1, 1990, and version 2; RFC 1740). A header (magic, version, 16 filler bytes, entry count) and a table of
    /// entries (ID, offset, length). AppleSingle holds a whole file; an AppleDouble header file holds everything but the
    /// data fork, which is the separate data file, so its <see cref="MacFile.DataFork"/> is empty.
    /// </summary>
    public sealed class AppleSingleReader : IContainerReader
    {
        private const uint SingleMagic = 0x00051600;
        private const uint DoubleMagic = 0x00051607;
        private const uint Version1 = 0x00010000;
        private const uint Version2 = 0x00020000;
        private const int HeaderLength = 26;
        private const int EntryLength = 12;

        // Entry IDs (Developer Note, "Entry IDs").
        private const uint DataFork = 1, ResourceFork = 2, RealName = 3, Comment = 4, IconBW = 5, IconColor = 6,
            FileInfo = 7, FileDates = 8, FinderInfoEntry = 9, MacFileInfo = 10, ProDosFileInfo = 11,
            MsDosFileInfo = 12, AfpShortName = 13, AfpFileInfo = 14, AfpDirectoryId = 15;

        // Version 2 File Dates Info: signed seconds from 2000-01-01 00:00 UTC; this value means "unknown".
        private const int UnknownDate = int.MinValue;
        private static readonly DateTime DateEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly uint magic;

        private AppleSingleReader(uint magic) => this.magic = magic;

        /// <summary>The AppleSingle reader.</summary>
        public static AppleSingleReader AppleSingle { get; } = new(SingleMagic);

        /// <summary>The AppleDouble header-file reader.</summary>
        public static AppleSingleReader AppleDouble { get; } = new(DoubleMagic);

        /// <inheritdoc/>
        public string FormatName => magic == SingleMagic ? "AppleSingle" : "AppleDouble";

        /// <inheritdoc/>
        public bool CanRead(ForkData input)
        {
            var header = input.ReadPrefix(HeaderLength);
            var reader = new BigEndianReader(header);
            return header.Length == HeaderLength
                && reader.ReadUInt32At(0) == magic
                && reader.ReadUInt32At(4) is Version1 or Version2;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            if (!CanRead(input)) throw new InvalidDataException($"Not an {FormatName} file.");
            var header = input.ReadPrefix(HeaderLength);
            var reader = new BigEndianReader(header);
            var version = reader.ReadUInt32At(4);
            // Version 1 names the home file system in the filler ("Macintosh", "ProDOS", …); version 2 zeroes it.
            var homeFileSystem = System.Text.Encoding.ASCII.GetString(header, 8, 16).TrimEnd(' ', '\0');
            var count = reader.ReadUInt16At(24);
            var table = input.ReadPrefix(HeaderLength + count * EntryLength).AsMemory(HeaderLength);
            if (table.Length < count * EntryLength)
            {
                context.Report(DiagnosticSeverity.Error, "applesingle.entries-truncated",
                    $"The header lists {count} entries but the file ends inside the entry table.", HeaderLength);
                count = (ushort)(table.Length / EntryLength);
            }

            var file = new MacFile { Name = context.HostName ?? default };
            var hasName = false;
            for (var i = 0; i < count; i++)
            {
                var e = new BigEndianReader(table.Slice(i * EntryLength, EntryLength));
                var id = e.ReadUInt32();
                long offset = e.ReadUInt32();
                long length = e.ReadUInt32();
                var at = HeaderLength + (long)i * EntryLength;
                if (offset + length > input.Length)
                {
                    context.Report(DiagnosticSeverity.Error, "applesingle.entry-out-of-range",
                        $"Entry {id} ({length} bytes at {offset}) runs past the end of the {input.Length}-byte file; skipped.", at);
                    continue;
                }
                var entry = input.Slice(offset, length);
                switch (id)
                {
                    case DataFork:
                        if (magic == DoubleMagic)
                            context.Report(DiagnosticSeverity.Warning, "applesingle.double-data-fork",
                                "An AppleDouble header file contains a data fork; it is used.", at);
                        file = file with { DataFork = entry };
                        break;
                    case ResourceFork:
                        file = file with { ResourceFork = entry };
                        break;
                    case RealName:
                        var name = entry.ToArray();
                        if (name.Length > 255)
                        {
                            context.Report(DiagnosticSeverity.Warning, "applesingle.name-too-long",
                                $"The {name.Length}-byte real name is cut to 255 bytes.", offset);
                            name = name[..255];
                        }
                        file = file with { Name = new MacString(name) };
                        hasName = true;
                        break;
                    case FinderInfoEntry:
                        file = file with { FinderInfo = FinderInfo.Read(entry.ToArray()) };
                        break;
                    case FileDates:
                        file = ReadFileDates(file, entry.ToArray(), context, offset);
                        break;
                    case FileInfo when version == Version1 && homeFileSystem == "Macintosh":
                        file = ReadMacFileInfoV1(file, entry.ToArray(), context, offset);
                        break;
                    case Comment or IconBW or IconColor or FileInfo or MacFileInfo or ProDosFileInfo or MsDosFileInfo
                        or AfpShortName or AfpFileInfo or AfpDirectoryId:
                        break; // Known entries a Mac file does not need.
                    default:
                        context.Report(DiagnosticSeverity.Info, "applesingle.unknown-entry",
                            $"Entry ID {id} is not defined by the format; skipped.", at);
                        break;
                }
            }
            if (!hasName && context.HostName is null)
            {
                context.Report(DiagnosticSeverity.Info, "applesingle.no-name",
                    "The file has no Real Name entry and no host name.");
            }
            return [file];
        }

        // Version 2 entry 8: creation, modification, backup and access dates, signed seconds from 2000 UTC. Mac dates
        // are local, so they are converted with the reading Mac's zone.
        private static MacFile ReadFileDates(MacFile file, byte[] entry, ContainerContext context, long offset)
        {
            if (entry.Length < 8)
            {
                context.Report(DiagnosticSeverity.Warning, "applesingle.dates-truncated",
                    $"The File Dates entry is {entry.Length} bytes; dates ignored.", offset);
                return file;
            }
            var reader = new BigEndianReader(entry);
            return file with
            {
                Created = ToMacDate(reader.ReadInt32(), context, offset),
                Modified = ToMacDate(reader.ReadInt32(), context, offset),
            };
        }

        private static MacDate? ToMacDate(int seconds, ContainerContext context, long offset)
        {
            if (seconds == UnknownDate) return null;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateEpoch.AddSeconds(seconds), context.Options.TimeZone);
            try
            {
                return MacDate.FromDateTime(local);
            }
            catch (ArgumentOutOfRangeException)
            {
                context.Report(DiagnosticSeverity.Warning, "applesingle.date-out-of-range",
                    $"The date {local:yyyy-MM-dd} is outside what a Mac date can hold (1904–2040); ignored.", offset);
                return null;
            }
        }

        // Version 1 entry 7 with home file system "Macintosh": creation, modification and backup dates as Mac dates
        // (seconds since 1904, local), then attributes.
        private static MacFile ReadMacFileInfoV1(MacFile file, byte[] entry, ContainerContext context, long offset)
        {
            if (entry.Length < 8)
            {
                context.Report(DiagnosticSeverity.Warning, "applesingle.dates-truncated",
                    $"The File Info entry is {entry.Length} bytes; dates ignored.", offset);
                return file;
            }
            var reader = new BigEndianReader(entry);
            return file with
            {
                Created = new MacDate(reader.ReadUInt32()),
                Modified = new MacDate(reader.ReadUInt32()),
            };
        }
    }
}
