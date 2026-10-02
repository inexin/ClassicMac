using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files.Rom
{
    /// <summary>
    /// Raw Macintosh ROM images (64 KiB to 4 MiB, a power of two) with a ROM resource table (<see cref="RomResourceTable"/>).
    /// The result is one file with no data fork whose resource fork holds the ROM's resources, every entry of the table
    /// whatever machine combination it belongs to; a type and ID listed again (for other combinations) keeps the first
    /// entry and reports the others. Layout and rules: docs/formats/disk-images/rom.md.
    /// </summary>
    public sealed class MacRomReader : IContainerReader
    {
        private const long MinLength = 64 * 1024, MaxLength = 4 * 1024 * 1024;

        /// <summary>The reader.</summary>
        public static MacRomReader Instance { get; } = new();

        private MacRomReader()
        {
        }

        /// <inheritdoc/>
        public string FormatName => "Mac ROM";

        /// <summary>
        /// A power-of-two length from 64 KiB to 4 MiB, the table pointer at $1A inside the image, a plausible table header
        /// there, and a list of entries that ends (<see cref="RomResourceTable.Read"/>).
        /// </summary>
        public bool CanRead(ForkData input)
        {
            var length = input.Length;
            if (length is < MinLength or > MaxLength || !BitOperations.IsPow2(length)) return false;
            var prefix = input.ReadPrefix(RomResourceTable.TablePointerOffset + 4);
            long table = new BigEndianReader(prefix).ReadUInt32At(RomResourceTable.TablePointerOffset);
            if (table > length - 10) return false;
            // The header alone first, so most files are turned away without reading the whole image.
            var header = new BigEndianReader(input.Slice(table, 10).ReadPrefix(10));
            if (header.ReadUInt16At(6) != 1 || header.ReadUInt16At(8) != 12 || header.ReadByteAt(5) is < 1 or > 8) return false;
            return RomResourceTable.Read(input.ToArray()) is not null;
        }

        /// <inheritdoc/>
        public IReadOnlyList<MacFile> Read(ForkData input, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(context);
            if (input.Length is < MinLength or > MaxLength) throw new InvalidDataException("Not a Mac ROM image.");
            var table = RomResourceTable.Read(input.ToArray(), context.Diagnostics)
                ?? throw new InvalidDataException("Not a Mac ROM image with a resource table.");
            var fork = ToResourceFork(table, context);
            var name = context.HostName ?? MacString.FromMacRoman($"ROM ${table.RomVersion:X4}");
            return [new MacFile { Name = name, ResourceFork = ForkData.FromBytes(fork.ToArray()) }];
        }

        /// <summary>
        /// The table's resources as a resource fork, in list order. Entries not in every combination 1 to
        /// <see cref="RomResourceTable.MaxComboIndex"/> are reported as <c>rom.combinations</c> (Info); a type and ID
        /// seen before is left out and reported as <c>rom.duplicate</c> (Warning).
        /// </summary>
        public static ResourceFork ToResourceFork(RomResourceTable table, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(table);
            ArgumentNullException.ThrowIfNull(context);
            var fork = new ResourceFork();
            var kept = new Dictionary<(FourCC, short), RomResourceEntry>();
            foreach (var entry in table.Entries)
            {
                var combos = Combinations(entry, table.MaxComboIndex);
                if (kept.TryGetValue((entry.Type, entry.Id), out var first))
                {
                    context.Report(DiagnosticSeverity.Warning, "rom.duplicate",
                        $"ROM resource '{entry.Type}' {entry.Id} is listed again for combinations {combos} " +
                        $"(mask {entry.ComboMaskHex}); the first entry, for combinations " +
                        $"{Combinations(first, table.MaxComboIndex)}, is kept.", entry.EntryOffset);
                    continue;
                }
                kept.Add((entry.Type, entry.Id), entry);
                if (!Enumerable.Range(1, table.MaxComboIndex).All(entry.IsInCombination))
                {
                    context.Report(DiagnosticSeverity.Info, "rom.combinations",
                        $"ROM resource '{entry.Type}' {entry.Id} is only in combinations {combos} (mask {entry.ComboMaskHex}).",
                        entry.EntryOffset);
                }
                fork.Add(new Resource(entry.Type, entry.Id, entry.Data)
                {
                    Name = entry.Name,
                    Attributes = (ResourceAttributes)entry.Attributes,
                });
            }
            return fork;
        }

        private static string Combinations(RomResourceEntry entry, int max)
        {
            var list = Enumerable.Range(1, max).Where(entry.IsInCombination).ToList();
            return list.Count == 0 ? "none" : string.Join(", ", list);
        }
    }
}
