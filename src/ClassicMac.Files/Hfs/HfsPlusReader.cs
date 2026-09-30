using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// HFS Plus structures and offsets follow Apple Technical Note TN1150.
internal static class HfsPlusReader
{
    private const int HeaderOffset = 1024;
    private const int HeaderLength = 512;
    private const uint RootFolderId = 2;

    public static IReadOnlyList<MacFile> Read(ForkData image, ContainerContext context)
    {
        byte[] header = image.Slice(HeaderOffset, HeaderLength).ToArray();
        ushort signature = U16(header, 0);
        ushort version = U16(header, 2);
        if (signature is not (0x482B or 0x4858))
            throw new InvalidDataException($"Unknown HFS Plus volume signature 0x{signature:X4}.");
        if ((signature == 0x482B && version != 4) || (signature == 0x4858 && version != 5))
            throw new InvalidDataException($"Unsupported HFS Plus version {version}.");
        uint blockSize = U32(header, 40);
        uint totalBlocks = U32(header, 44);
        if (blockSize < 512 || (blockSize & (blockSize - 1)) != 0 ||
            totalBlocks == 0 || (ulong)blockSize * totalBlocks > (ulong)image.Length)
            throw new InvalidDataException("The HFS Plus allocation area is invalid.");

        var overflow = new Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>();
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(192, 8)) != 0)
        {
            ForkData extentsFork = ReadFork(image, header.AsSpan(192, 80), blockSize, totalBlocks);
            foreach (var (key, data) in LeafRecords(
                extentsFork.ToArray(context.Options.MaxExpandedBytesPerInput), "extents-overflow"))
            {
                if (key.Length != 12 || U16(key, 0) != 10 || data.Length < 64 || key[2] is not (0 or 0xFF))
                    throw new InvalidDataException("An HFS Plus extents-overflow record is invalid.");
                var id = (key[2], U32(key, 4));
                if (!overflow.TryGetValue(id, out var entries)) overflow[id] = entries = [];
                entries.Add((U32(key, 8), data.AsSpan(0, 64).ToArray()));
            }
        }

        var catalogFork = ReadFork(image, header.AsSpan(272, 80), blockSize, totalBlocks, overflow, 0, 4);
        byte[] catalog = catalogFork.ToArray();
        var records = LeafRecords(catalog, "catalog").ToArray();
        var folders = new Dictionary<uint, (uint Parent, string Name)>();
        foreach (var (key, data) in records)
        {
            if (key.Length < 8 || data.Length < 2)
                throw new InvalidDataException("An HFS Plus catalog record is truncated.");
            switch (U16(data, 0))
            {
                case 1:
                    if (data.Length < 88) throw new InvalidDataException("An HFS Plus folder record is truncated.");
                    uint id = U32(data, 8);
                    if (!folders.TryAdd(id, (U32(key, 2), Name(key))))
                        throw new InvalidDataException("Duplicate HFS Plus folder ID.");
                    break;
                case 2:
                    if (data.Length < 248) throw new InvalidDataException("An HFS Plus file record is truncated.");
                    _ = Name(key);
                    break;
                case 3 or 4:
                    if (data.Length < 10) throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    ushort threadNameLength = U16(data, 8);
                    if (threadNameLength > 255 || data.Length < 10 + 2 * threadNameLength)
                        throw new InvalidDataException("An HFS Plus catalog thread record is truncated.");
                    break;
                default:
                    throw new InvalidDataException($"Unknown HFS Plus catalog record type {U16(data, 0)}.");
            }
        }
        if (!folders.ContainsKey(RootFolderId))
            throw new InvalidDataException("The HFS Plus root folder is missing.");

        var result = new List<MacFile>();
        foreach (var (key, data) in records)
        {
            if (U16(data, 0) != 2) continue;
            string name = Name(key);
            uint parent = U32(key, 2);
            byte[] info = [.. data.AsSpan(48, 16), .. data.AsSpan(64, 16)];
            var path = FolderPath(parent, folders);
            result.Add(new MacFile
            {
                Name = LegacyName(name),
                UnicodeName = name,
                FolderPath = path.Select(LegacyName).ToArray(),
                UnicodeFolderPath = path,
                FinderInfo = FinderInfo.Read(info),
                Created = Date(U32(data, 12)),
                Modified = Date(U32(data, 16)),
                DataFork = ReadFork(image, data.AsSpan(88, 80), blockSize, totalBlocks, overflow, 0, U32(data, 8)),
                ResourceFork = ReadFork(image, data.AsSpan(168, 80), blockSize, totalBlocks, overflow, 0xFF, U32(data, 8)),
            });
        }
        uint expectedFiles = U32(header, 32);
        uint expectedFolders = U32(header, 36);
        if (result.Count != expectedFiles || folders.Count - 1 != expectedFolders)
            context.Report(DiagnosticSeverity.Info, "hfs.plus-counts",
                $"The HFS Plus catalog has {result.Count} files and {folders.Count - 1} folders; " +
                $"the volume header says {expectedFiles} and {expectedFolders}.");
        return result;
    }

    private static ForkData ReadFork(ForkData image, ReadOnlySpan<byte> fork, uint blockSize, uint totalBlocks,
        Dictionary<(byte Fork, uint File), List<(uint Start, byte[] Extents)>>? overflow = null,
        byte forkType = 0, uint fileId = 0)
    {
        ulong logical = BinaryPrimitives.ReadUInt64BigEndian(fork);
        if (logical == 0) return ForkData.Empty;
        if (logical > long.MaxValue) throw new InvalidDataException("An HFS Plus fork is too large.");
        uint allocatedBlocks = U32(fork, 12);
        if (allocatedBlocks == 0)
            throw new InvalidDataException("A nonempty HFS Plus fork has no allocated blocks.");
        var ranges = new List<(long Offset, long Length)>();
        uint coveredBlocks = 0;
        void AddExtents(ReadOnlySpan<byte> extents)
        {
            for (int index = 0; index < 8; index++)
            {
                uint start = U32(extents, index * 8);
                uint count = U32(extents, index * 8 + 4);
                if (count == 0) break;
                if (count > allocatedBlocks - coveredBlocks)
                    throw new InvalidDataException("An HFS Plus fork's extents exceed its allocated block count.");
                if ((ulong)start + count > totalBlocks)
                    throw new InvalidDataException("An HFS Plus extent lies outside the allocation area.");
                long offset = checked((long)start * blockSize);
                long length = checked((long)count * blockSize);
                if (offset > image.Length - length)
                    throw new InvalidDataException("An HFS Plus extent lies outside the image.");
                ranges.Add((offset, length));
                coveredBlocks = checked(coveredBlocks + count);
            }
        }
        AddExtents(fork.Slice(16, 64));
        if (coveredBlocks < allocatedBlocks && overflow is not null &&
            overflow.TryGetValue((forkType, fileId), out var entries))
            foreach (var entry in entries.OrderBy(e => e.Start))
            {
                if (coveredBlocks >= allocatedBlocks) break;
                if (entry.Start != coveredBlocks)
                    throw new InvalidDataException("An HFS Plus overflow extent is not contiguous with the fork.");
                AddExtents(entry.Extents);
            }
        if (coveredBlocks != allocatedBlocks)
            throw new InvalidDataException("An HFS Plus fork's extent count differs from its allocated block count.");
        if ((ulong)coveredBlocks * blockSize < logical)
            throw new InvalidDataException("An HFS Plus fork has insufficient extents for its logical length.");
        return new ExtentForkData(image, ranges, checked((long)logical));
    }

    private static IEnumerable<(byte[] Key, byte[] Data)> LeafRecords(byte[] tree, string name)
    {
        if (tree.Length < 512 || tree[8] != 1)
            throw new InvalidDataException($"The HFS Plus {name} tree has no B-tree header.");
        int nodeSize = U16(tree, 32);
        if (nodeSize < 512 || nodeSize > 32768 || (nodeSize & (nodeSize - 1)) != 0 || tree.Length % nodeSize != 0)
            throw new InvalidDataException($"The HFS Plus {name} B-tree node size is invalid.");
        uint totalNodes = U32(tree, 36);
        if (totalNodes == 0 || totalNodes > tree.Length / nodeSize)
            throw new InvalidDataException($"The HFS Plus {name} B-tree node count is invalid.");
        uint first = U32(tree, 24);
        uint last = U32(tree, 28);
        uint expectedRecords = U32(tree, 20);
        if (expectedRecords == 0)
        {
            if (first != 0 || last != 0)
                throw new InvalidDataException($"The empty HFS Plus {name} B-tree has leaf links.");
            yield break;
        }
        if (first == 0 || last == 0 || first >= totalNodes || last >= totalNodes)
            throw new InvalidDataException($"The HFS Plus {name} B-tree leaf endpoints are invalid.");
        uint readRecords = 0;
        uint previous = 0;
        uint finalLeaf = 0;
        var seen = new HashSet<uint>();
        for (uint node = first; node != 0; node = U32(tree, checked((int)node * nodeSize)))
        {
            if (!seen.Add(node) || node >= totalNodes)
                throw new InvalidDataException($"The HFS Plus {name} B-tree leaf chain is invalid.");
            int start = checked((int)node * nodeSize);
            if (tree[start + 8] != 0xFF || tree[start + 9] != 1)
                throw new InvalidDataException($"An HFS Plus {name} B-tree linked leaf has an invalid type.");
            if (U32(tree, start + 4) != previous)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has an invalid backward link.");
            int count = U16(tree, start + 10);
            if (count > (nodeSize - 14) / 2)
                throw new InvalidDataException($"An HFS Plus {name} B-tree leaf has too many records.");
            for (int index = 0; index < count; index++)
            {
                int begin = U16(tree, start + nodeSize - 2 * (index + 1));
                int end = U16(tree, start + nodeSize - 2 * (index + 2));
                if (begin < 14 || end <= begin || end > nodeSize - 2 * (count + 1))
                    throw new InvalidDataException($"An HFS Plus {name} B-tree record offset is invalid.");
                int offset = start + begin;
                int keyLength = U16(tree, offset);
                if (keyLength < 6 || 2 + keyLength > end - begin)
                    throw new InvalidDataException($"An HFS Plus {name} B-tree key is invalid.");
                int dataOffset = offset + 2 + keyLength;
                yield return (tree.AsSpan(offset, 2 + keyLength).ToArray(),
                    tree.AsSpan(dataOffset, start + end - dataOffset).ToArray());
                readRecords++;
            }
            finalLeaf = node;
            previous = node;
        }
        if (readRecords != expectedRecords)
            throw new InvalidDataException($"The HFS Plus {name} B-tree leaf-record count is inconsistent.");
        if (finalLeaf != last)
            throw new InvalidDataException($"The HFS Plus {name} B-tree ends at leaf {finalLeaf}, not {last}.");
    }

    private static string Name(ReadOnlySpan<byte> key)
    {
        if (key.Length < 8) throw new InvalidDataException("An HFS Plus catalog name is truncated.");
        int length = U16(key, 6);
        if (length > 255 || key.Length < 8 + 2 * length)
            throw new InvalidDataException("An HFS Plus catalog name is invalid.");
        return Encoding.BigEndianUnicode.GetString(key.Slice(8, 2 * length)).Normalize(NormalizationForm.FormC);
    }

    private static IReadOnlyList<string> FolderPath(uint parent,
        Dictionary<uint, (uint Parent, string Name)> folders)
    {
        var path = new List<string>();
        var seen = new HashSet<uint>();
        while (parent != RootFolderId)
        {
            if (!seen.Add(parent) || !folders.TryGetValue(parent, out var folder))
                throw new InvalidDataException("An HFS Plus folder path is missing or cyclic.");
            path.Insert(0, folder.Name);
            parent = folder.Parent;
        }
        return path;
    }

    private static MacDate? Date(uint seconds) => seconds == 0 ? null : new MacDate(seconds);
    private static MacString LegacyName(string name)
    {
        try { return MacString.FromMacRoman(name); }
        catch (ArgumentException) { return MacString.FromMacRoman("?"); }
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
