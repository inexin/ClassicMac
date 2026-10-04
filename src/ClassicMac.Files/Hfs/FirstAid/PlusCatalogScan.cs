using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// "Checking catalog file." and "Checking catalog hierarchy." on HFS Plus (hfs.md §5.6, hfs-plus.md §1.5, §2.4): every
// leaf record in key order: folders (88 bytes), files (248) and threads (10 + 2n), each folder and file with its thread
// and each thread with its record, file forks against their extents; then each folder's valence, every item's parent,
// and loops. Disk First Aid's numbers and words are used where they mean the same; arguments are the CNID and node.
internal sealed class PlusCatalogScan
{
    private const ushort Folder = 1, File = 2, FolderThread = 3, FileThread = 4;
    private const int FolderLength = 88, FileLength = 248;
    private readonly FirstAidRun run;
    private readonly FirstAidTree catalog;
    private readonly List<(byte[] Key, byte[] Data, uint Node)> records;
    private readonly Dictionary<uint, (uint Parent, uint Valence, uint Node)> folders = [];
    private readonly Dictionary<uint, int> items = [];
    private readonly List<(uint Id, uint Parent, uint Node)> parents = [];
    private uint maxId = 15;

    private PlusCatalogScan(FirstAidRun run)
    {
        this.run = run;
        catalog = run.Catalog!;
        records = catalog.Records;
    }

    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingCatalogFile);
        var scan = new PlusCatalogScan(run);
        if (!scan.Scan())
        {
            return false;
        }

        run.Stage(FirstAidMessages.CheckingHierarchy);
        if (!scan.Hierarchy())
        {
            return false;
        }

        PlusLinkCheck.Run(run);
        return true;
    }

    private bool Scan()
    {
        run.DirCount = 0;
        if (Find(2, []) is not { } rootThread || Type(rootThread.Data) != FolderThread)
        {
            return run.Fatal(35);
        }

        foreach (var (key, data, node) in records)
        {
            var reader = new BigEndianReader(key);
            uint parent = reader.ReadUInt32At(2);
            bool done = Type(data) switch
            {
                FolderThread or FileThread => Thread(key, data, node),
                Folder => FolderRecord(key, data, node, parent),
                File => FileRecord(key, data, node, parent),
                _ => run.Fatal(31, parent, node),
            };
            if (!done)
            {
                return false;
            }
        }

        run.NextCnid = maxId + 1;
        return true;
    }

    private bool Thread(byte[] key, byte[] data, uint node)
    {
        var keyReader = new BigEndianReader(key);
        uint cnid = keyReader.ReadUInt32At(2);
        if (data.Length < 10 || data.Length > 520 || 10 + 2 * new BigEndianReader(data).ReadUInt16At(8) != data.Length)
        {
            return run.Fatal(33, cnid, node);
        }

        if (keyReader.ReadUInt16At(6) != 0)
        {
            return run.Fatal(38, cnid, node);
        }

        var reader = new BigEndianReader(data);
        int nameLength = reader.ReadUInt16At(8);
        if (nameLength is 0 or > 255)
        {
            return run.Fatal(39, cnid, node);
        }

        if (InvalidId(cnid) && cnid != 2)
        {
            return run.Fatal(65, cnid, node);
        }

        var target = Find(reader.ReadUInt32At(4), data.AsSpan(10, 2 * nameLength).ToArray());
        bool folder = Type(data) == FolderThread;
        if (target is null)
        {
            run.Flag(folder ? 37 : 6, folder ? FirstAidRepairs.MissingFolder : FirstAidRepairs.FileThreads, cnid, node);
        }
        else if (Type(target.Value.Data) != (folder ? Folder : File) || Id(target.Value.Data) != cnid)
        {
            run.Flag(36, FirstAidRepairs.MissingThreads, cnid, node);           // the record's own thread is the wrong one
        }

        return true;
    }

    private bool FolderRecord(byte[] key, byte[] data, uint node, uint parent)
    {
        uint id = data.Length >= 12 ? Id(data) : 0;
        if (data.Length != FolderLength)
        {
            return run.Fatal(32, id, node);
        }

        if (id == 2 ? parent != 1 : InvalidId(id))
        {
            return run.Fatal(65, id, node);
        }

        if (id == 2)
        {
            run.PlusVolumeName = Name(key);
        }
        else
        {
            run.DirCount++;
            Child(id, parent, node);
        }

        folders[id] = (parent, new BigEndianReader(data).ReadUInt32At(4), node);
        maxId = Math.Max(maxId, id);
        HasThread(key, id, FolderThread, node);
        return true;
    }

    private bool FileRecord(byte[] key, byte[] data, uint node, uint parent)
    {
        uint id = data.Length >= 12 ? Id(data) : 0;
        if (data.Length != FileLength)
        {
            return run.Fatal(34, id, node);
        }

        if (InvalidId(id))
        {
            return run.Fatal(65, id, node);
        }

        if (!Fork(data, 88, 0x00, id, node) || !Fork(data, 168, 0xFF, id, node))
        {
            return false;
        }

        maxId = Math.Max(maxId, id);
        run.FileIds.Add(id);
        run.FileCount++;
        Child(id, parent, node);
        HasThread(key, id, FileThread, node);
        return true;
    }

    // A fork: its extents cover its block count (#1), and its logical size is within its blocks (#2); a block count
    // short of its extents is repaired.
    private bool Fork(byte[] data, int at, byte fork, uint id, uint node)
    {
        var forkData = data.AsMemory(at, PlusExtentRecords.ForkDataLength);
        if (PlusExtentRecords.Fork(run, forkData, fork, id, node) is not { } extents)
        {
            return false;
        }

        long blocks = 0;
        foreach (var (start, count) in extents)
        {
            blocks += count;
            run.ForkExtents.Add((id, fork, start, count));
        }

        var reader = new BigEndianReader(forkData);
        uint declared = reader.ReadUInt32At(12);
        if (blocks < declared)
        {
            return run.Fatal(1, id, node);
        }

        if (reader.ReadUInt64At(0) > (ulong)declared * run.BlockSize)
        {
            return run.Fatal(2, id, node);
        }

        if (blocks > declared)
        {
            run.Problem("A fork's block count is short of its extents", "firstaid.short-peof", FirstAidRepairs.ForkLengths);
        }

        return true;
    }

    // A folder or file needs its thread: (id, "") of the right kind, naming its parent and name (#36).
    private void HasThread(byte[] key, uint id, ushort kind, uint node)
    {
        if (Find(id, []) is not { } thread || Type(thread.Data) != kind || thread.Data.Length < 10
            || !thread.Data.AsSpan(4, 4).SequenceEqual(key.AsSpan(2, 4)) || !thread.Data.AsSpan(8).SequenceEqual(key.AsSpan(6)))
        {
            run.Flag(36, FirstAidRepairs.MissingThreads, id, node);
        }
    }

    private void Child(uint id, uint parent, uint node)
    {
        items[parent] = items.GetValueOrDefault(parent) + 1;
        parents.Add((id, parent, node));
    }

    // Each folder's valence; every item's parent a folder; no folder its own ancestor.
    private bool Hierarchy()
    {
        foreach (var (id, parent, node) in parents)
        {
            if (!folders.ContainsKey(parent) && !run.Problems.Exists(p => p.Number == 37 && p.Arg2 == parent))
            {
                run.Problem("A file or folder's parent folder is missing", "firstaid.missing-parent", FirstAidRepairs.None);
            }

            if (folders.ContainsKey(id))
            {
                var seen = new HashSet<uint> { id };
                for (uint at = parent; at != 1 && folders.TryGetValue(at, out var up); at = up.Parent)
                {
                    if (!seen.Add(at))
                    {
                        return run.Fatal(41, id, node);
                    }
                }
            }
        }

        foreach (var (id, (_, valence, node)) in folders)
        {
            if (items.GetValueOrDefault(id) != valence)
            {
                run.Flag(3, FirstAidRepairs.Valences, id, node);
            }
        }

        return true;
    }

    // 0, and 3 to 15 but the special files' CNIDs that never have records, are not a folder's or file's.
    private static bool InvalidId(uint id) => id < 16;

    private static ushort Type(byte[] data) => data.Length >= 2 ? (ushort)(data[0] << 8 | data[1]) : (ushort)0;

    private static uint Id(byte[] data) => new BigEndianReader(data).ReadUInt32At(8);

    private static string Name(byte[] key)
    {
        var reader = new BigEndianReader(key);
        var chars = new char[reader.ReadUInt16At(6)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)reader.ReadUInt16At(8 + 2 * i);
        }

        return new string(chars);
    }

    // The record with key (parent, name), the name in UTF-16 big-endian units.
    private (byte[] Key, byte[] Data, uint Node)? Find(uint parent, byte[] name)
    {
        var search = new byte[8 + name.Length];
        var writer = new BigEndianWriter(search);
        writer.WriteUInt16At(0, 6 + name.Length);
        writer.WriteUInt32At(2, parent);
        writer.WriteUInt16At(6, name.Length / 2);
        name.CopyTo(search, 8);
        int low = 0, high = records.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            int order = catalog.Compare(records[middle].Key, search);
            if (order == 0)
            {
                return records[middle];
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }
}
