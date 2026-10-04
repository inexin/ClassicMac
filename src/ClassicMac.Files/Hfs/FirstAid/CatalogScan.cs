using System;
using System.Collections.Generic;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// "Checking catalog file." (hfs.md §5.6): every catalog leaf record in key order, threads paired with their records,
// each file's forks against their extents, the volume's counts taken; then MountCheck's view of the catalog and the
// bitmap [Code: Disk First Aid 8.5.5, CODE 1 $1AFAE scan, $1B8A4 threads, $1B77E folders, $1BB24 files, $1BD8E
// epilogue, $12D6A MountCheck]. Arguments: the record's CNID and its node.
internal sealed class CatalogScan
{
    private const ushort Folder = 0x0100, File = 0x0200, FolderThread = 0x0300, FileThread = 0x0400;
    private const byte DataFork = 0x00, ResourceFork = 0xFF;
    private readonly FirstAidRun run;
    private readonly FirstAidTree catalog;
    private readonly List<(byte[] Key, byte[] Data, uint Node)> records;
    private int dirPairs, filePairs;
    private uint maxId = 16, currentParent = 1;

    private CatalogScan(FirstAidRun run)
    {
        this.run = run;
        catalog = run.Catalog!;
        records = catalog.Records;
    }

    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingCatalogFile);
        var scan = new CatalogScan(run);
        if (!scan.Scan())
        {
            return false;
        }

        MountCheck.Run(run);
        return true;
    }

    private bool Scan()
    {
        run.DirCount = -1;                                                       // the root folder is not counted
        if (Find(2, []) is null)
        {
            return run.Fatal(35);
        }

        foreach (var (key, data, node) in records)
        {
            uint parent = KeyId(key);
            int type = data.Length >= 2 ? data[0] << 8 | data[1] : -1;
            bool done = type switch
            {
                FolderThread or FileThread => Thread(key, data, node, type == FolderThread),
                Folder => FolderRecord(key, data, node, parent),
                File => FileRecord(key, data, node, parent),
                _ => run.Fatal(31, parent, node),
            };
            if (!done)
            {
                return false;
            }
        }

        // A record without its thread shows only as unpaired counts: no problem is printed; repair makes the threads.
        if (dirPairs != 0 || filePairs != 0)
        {
            run.Silent(FirstAidRepairs.MissingThreads);
        }

        run.NextCnid = maxId;
        return true;
    }

    private bool Thread(byte[] key, byte[] data, uint node, bool folder)
    {
        uint cnid = KeyId(key);
        if (folder)
        {
            dirPairs++;
        }
        else
        {
            filePairs++;
        }

        if (data.Length != 46)
        {
            return run.Fatal(33, cnid, node);
        }

        if (key[6] != 0)
        {
            return run.Fatal(38, cnid, node);
        }

        if (!ValidName(data.AsSpan(0x0E)))
        {
            return run.Fatal(39, cnid, node);
        }

        if (data.AsSpan(2, 8).IndexOfAnyExcept((byte)0) >= 0)
        {
            run.Flag(64, FirstAidRepairs.ReservedFields, cnid, node);
        }

        if (InvalidId(cnid))
        {
            return run.Fatal(65, cnid, node);
        }

        currentParent = cnid;
        var target = Find(ThreadParentId(data), data.AsSpan(0x0F, data[0x0E]).ToArray());
        if (folder)
        {
            if (target is null)
            {
                run.Flag(37, FirstAidRepairs.MissingFolder, cnid, node);
            }
            else if (Type(target.Value.Data) != Folder)
            {
                return run.Fatal(37, cnid, node);
            }
        }
        else if (target is null)
        {
            run.Flag(6, FirstAidRepairs.FileThreads, cnid, node);
        }
        else if (Type(target.Value.Data) != File)
        {
            return run.Fatal(6, cnid, node);
        }
        else if ((target.Value.Data[2] & 0x02) == 0)
        {
            run.Flag(6, FirstAidRepairs.FileThreads, cnid, node);           // #50 "File thread flag not set" is never printed
        }

        return true;
    }

    private bool FolderRecord(byte[] key, byte[] data, uint node, uint parent)
    {
        var reader = new BigEndianReader(data);
        uint id = data.Length >= 10 ? reader.ReadUInt32At(6) : 0;
        if (data.Length != 70)
        {
            return run.Fatal(32, id, node);
        }

        if (parent != currentParent)
        {
            return run.Fatal(36, id, node);
        }

        if (reader.ReadUInt16At(2) != 0)
        {
            run.Flag(64, FirstAidRepairs.ReservedFields, id, node);
        }

        if (InvalidId(id))
        {
            return run.Fatal(65, id, node);
        }

        maxId = Math.Max(maxId, id + 1);
        dirPairs--;
        run.DirCount++;
        if (currentParent == 2)
        {
            run.RootDirCount++;
        }

        // A custom icon needs the folder's "Icon\r" file.
        if ((reader.ReadUInt16At(0x1E) & 0x0400) != 0 && !(Find(id, IconName) is { } icon && Type(icon.Data) == File))
        {
            run.Flag(57, FirstAidRepairs.FinderFlags, id, node);
        }

        return true;
    }

    private bool FileRecord(byte[] key, byte[] data, uint node, uint parent)
    {
        var reader = new BigEndianReader(data);
        uint id = data.Length >= 0x18 ? reader.ReadUInt32At(0x14) : 0;
        if (data.Length >= 3 && (data[2] & 0x02) != 0)
        {
            filePairs--;
        }

        if (data.Length != 102)
        {
            return run.Fatal(34, id, node);
        }

        if (parent != currentParent)
        {
            return run.Fatal(36, id, node);
        }

        if ((data[2] & 0x7C) != 0 || reader.ReadUInt16At(0x18) != 0 || reader.ReadUInt16At(0x22) != 0 || reader.ReadUInt32At(0x62) != 0)
        {
            run.Flag(64, FirstAidRepairs.ReservedFields, id, node);
        }

        if (InvalidId(id))
        {
            return run.Fatal(65, id, node);
        }

        if (!Fork(reader, DataFork, 0x4A, 0x1E, 0x1A, id, node) || !Fork(reader, ResourceFork, 0x56, 0x28, 0x24, id, node))
        {
            return false;
        }

        maxId = Math.Max(maxId, id + 1);
        run.FileIds.Add(id);
        run.FileCount++;
        if (currentParent == 2)
        {
            run.RootFileCount++;
        }

        return true;
    }

    // A fork: its extents must cover its PEOF (#1) and its LEOF must not pass its PEOF (#2); a PEOF short of the extents
    // is not this check's problem.
    private bool Fork(BigEndianReader record, byte fork, int extentRecord, int physical, int logical, uint id, uint node)
    {
        if (ExtentRecords.Fork(run, record.Source.Slice(extentRecord, 12), fork, id, node) is not { } extents)
        {
            return false;
        }

        long blocks = 0;
        foreach (var (start, count) in extents)
        {
            blocks += count;
            run.ForkExtents.Add((id, fork, start, count));
        }

        if (blocks * run.BlockSize < record.ReadUInt32At(physical))
        {
            return run.Fatal(1, id, node);
        }

        if (record.ReadInt32At(logical) > record.ReadInt32At(physical))
        {
            return run.Fatal(2, id, node);
        }

        return true;
    }

    // The name check: 1 to 31 bytes.
    private static bool ValidName(ReadOnlySpan<byte> name) => name.Length > 0 && name[0] is >= 1 and <= 31 && name.Length > name[0];

    // A CNID of 0 or 3 to 15: 1 and 2 are the root's parent and the root; 16 and up are the volume's own.
    private static bool InvalidId(uint id) => id == 0 || id is >= 3 and <= 15;

    private static readonly byte[] IconName = [(byte)'I', (byte)'c', (byte)'o', (byte)'n', 0x0D];

    private (byte[] Key, byte[] Data, uint Node)? Find(uint parent, byte[] name)
    {
        var search = new byte[7 + name.Length];
        search[0] = (byte)(6 + name.Length);
        new BigEndianWriter(search).WriteUInt32At(2, parent);
        search[6] = (byte)name.Length;
        name.CopyTo(search, 7);
        int low = 0, high = records.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            int order = HfsCatalogKeys.CompareCatalogKeys(records[middle].Key, search);
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

    private static int Type(byte[] data) => data.Length >= 2 ? data[0] << 8 | data[1] : -1;
}
