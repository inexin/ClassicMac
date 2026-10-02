using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files.Rom
{
    /// <summary>One entry of a ROM's resource table.</summary>
    /// <param name="Type">The resource type.</param>
    /// <param name="Id">The resource ID.</param>
    /// <param name="Attributes">The attribute byte as stored (the Resource Manager does not use it).</param>
    /// <param name="Name">The name, or <see langword="null"/> when its length is 0.</param>
    /// <param name="ComboMask">
    /// The combination field: bit <c>i</c>, counted from the most significant bit of the first byte, set when the
    /// resource belongs to combination <c>i</c>.
    /// </param>
    /// <param name="EntryOffset">Where the entry starts, from the start of the ROM (its combination field).</param>
    /// <param name="DataOffset">Where the data starts, from the start of the ROM.</param>
    /// <param name="Data">The data, or empty when its block header put it outside the ROM.</param>
    public sealed record RomResourceEntry(
        FourCC Type, short Id, byte Attributes, MacString? Name, ReadOnlyMemory<byte> ComboMask, long EntryOffset,
        long DataOffset, ReadOnlyMemory<byte> Data)
    {
        /// <summary>Whether the resource belongs to combination <paramref name="index"/>.</summary>
        public bool IsInCombination(int index) =>
            index >= 0 && index < ComboMask.Length * 8 && ((ComboMask.Span[index >> 3] >> (7 - (index & 7))) & 1) != 0;

        /// <summary>The combination field in hex, as in diagnostics.</summary>
        public string ComboMaskHex => "$" + Convert.ToHexString(ComboMask.Span);
    }

    /// <summary>
    /// The resource table of a Macintosh ROM image (the ROM resources the Resource Manager adds to the System map): the
    /// pointer at ROMBase+$1A, the table header and the linked list of entries, each followed by data with a 32-bit
    /// Memory Manager block header in front. Layout and rules: docs/formats/disk-images/rom.md.
    /// </summary>
    public sealed class RomResourceTable
    {
        /// <summary>Where ROMBase+$1A holds the offset of the table header [Code: ROM $077D].</summary>
        public const int TablePointerOffset = 0x1A;

        private const int TableHeaderLength = 10, EntryFixedLength = 16, BlockHeaderLength = 12;

        private RomResourceTable(
            ushort romVersion, long tableOffset, int maxComboIndex, int comboFieldSize, ushort comboVersion,
            ushort headerSize, IReadOnlyList<RomResourceEntry> entries)
        {
            RomVersion = romVersion;
            TableOffset = tableOffset;
            MaxComboIndex = maxComboIndex;
            ComboFieldSize = comboFieldSize;
            ComboVersion = comboVersion;
            HeaderSize = headerSize;
            Entries = entries;
        }

        /// <summary>The ROM version word at ROMBase+8 (<c>$077D</c> for the PowerPC ROMs).</summary>
        public ushort RomVersion { get; }

        /// <summary>Where the table header is, from the start of the ROM.</summary>
        public long TableOffset { get; }

        /// <summary>The highest combination index a machine may select (combinations are 1 to this).</summary>
        public int MaxComboIndex { get; }

        /// <summary>The length of each entry's combination field in bytes.</summary>
        public int ComboFieldSize { get; }

        /// <summary>The table's version word (1).</summary>
        public ushort ComboVersion { get; }

        /// <summary>The length of the Memory Manager block header before each resource's data (12).</summary>
        public ushort HeaderSize { get; }

        /// <summary>The entries, in the order of the linked list.</summary>
        public IReadOnlyList<RomResourceEntry> Entries { get; }

        /// <summary>
        /// Reads the table, or returns <see langword="null"/> when <paramref name="rom"/> has no plausible one: the
        /// pointer and the first entry inside the image, version 1, a combination field of 1–8 bytes, 12-byte block
        /// headers, and a list that ends within the image with every type made of character codes ($20 or above,
        /// not $7F). An entry whose data falls outside the image is kept with no data and reported as
        /// <c>rom.bad-entry</c>.
        /// </summary>
        public static RomResourceTable? Read(ReadOnlyMemory<byte> rom, ICollection<Diagnostic>? diagnostics = null)
        {
            var reader = new BigEndianReader(rom);
            long length = rom.Length;
            if (length < TablePointerOffset + 4) return null;
            long tableOffset = reader.ReadUInt32At(TablePointerOffset);
            if (tableOffset > length - TableHeaderLength) return null;
            var at = (int)tableOffset;
            long first = reader.ReadUInt32At(at);
            int maxIndex = reader.ReadByteAt(at + 4), fieldSize = reader.ReadByteAt(at + 5);
            var version = reader.ReadUInt16At(at + 6);
            var headerSize = reader.ReadUInt16At(at + 8);
            if (version != 1 || fieldSize is < 1 or > 8 || maxIndex < 1 || maxIndex >= fieldSize * 8 ||
                headerSize != BlockHeaderLength || first == 0)
                return null;

            var entries = new List<RomResourceEntry>();
            var limit = length / (fieldSize + EntryFixedLength);
            for (var offset = first; offset != 0; )
            {
                if (entries.Count >= limit || offset > length - (fieldSize + EntryFixedLength)) return null;
                var e = (int)offset;
                var mask = rom.Slice(e, fieldSize);
                var fixedAt = e + fieldSize;
                long next = reader.ReadUInt32At(fixedAt);
                long dataOffset = reader.ReadUInt32At(fixedAt + 4);
                var typeBytes = reader.ReadBytesAt(fixedAt + 8, 4);
                foreach (var b in typeBytes)
                {
                    if (b < 0x20 || b == 0x7F) return null;
                }
                var type = new FourCC(typeBytes);
                var id = reader.ReadInt16At(fixedAt + 12);
                var attributes = reader.ReadByteAt(fixedAt + 14);
                int nameLength = reader.ReadByteAt(fixedAt + 15);
                if (nameLength > length - (fixedAt + EntryFixedLength)) return null;
                MacString? name = nameLength == 0 ? null : new MacString(reader.ReadBytesAt(fixedAt + 16, nameLength));

                var data = ReadOnlyMemory<byte>.Empty;
                if (dataOffset >= BlockHeaderLength && dataOffset <= length)
                {
                    // 32-bit block header: tag, flags, reserved, size correction; physical size; relative handle.
                    var header = (int)dataOffset - BlockHeaderLength;
                    long size = (long)reader.ReadUInt32At(header + 4) - BlockHeaderLength - reader.ReadByteAt(header + 3);
                    if (size >= 0 && size <= length - dataOffset) data = rom.Slice((int)dataOffset, (int)size);
                    else Report(diagnostics, type, id, offset, $"its block header gives {size} bytes, past the end of the image");
                }
                else Report(diagnostics, type, id, offset, $"its data offset ${dataOffset:X} is outside the image");

                entries.Add(new RomResourceEntry(type, id, attributes, name, mask, offset, dataOffset, data));
                offset = next;
            }
            if (entries.Count == 0) return null;
            return new RomResourceTable(reader.ReadUInt16At(8), tableOffset, maxIndex, fieldSize, version, headerSize, entries);
        }

        private static void Report(ICollection<Diagnostic>? diagnostics, FourCC type, short id, long offset, string why) =>
            diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Error, "rom.bad-entry",
                $"ROM resource '{type}' {id}: {why}; it is listed with no data.", offset));
    }
}
