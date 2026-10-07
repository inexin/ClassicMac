using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Editing;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// The repair list on an HFS Plus catalog's leaf records before the tree is written again (hfs-plus.md §5.4): a file
// thread whose file is missing deleted, a folder made again from its thread, a thread made for every folder and file
// without its right one, each file's thread flag set, a fork's block count short of its extents raised, and every
// folder's valence set to its items. Each change names the record's CNID.
internal sealed class PlusCatalogRepair
{
    private const ushort Folder = 1, File = 2, FolderThread = 3, FileThread = 4;
    private const ushort ThreadExists = 0x0002;
    private readonly FirstAidRun run;
    private readonly FirstAidTree catalog;
    private readonly List<(byte[] Key, byte[] Data)> records;
    private readonly List<PlannedChange> changes;

    private readonly Dictionary<(uint FileId, byte Fork), List<(uint Start, uint Count)>> relocated;

    private PlusCatalogRepair(FirstAidRun run, List<PlannedChange> changes, Dictionary<(uint FileId, byte Fork), List<(uint Start, uint Count)>> relocated)
    {
        this.relocated = relocated;
        this.run = run;
        catalog = run.Catalog!;
        records = catalog.Records.Select(r => (r.Key.ToArray(), r.Data.ToArray())).ToList();
        this.changes = changes;
    }

    /// <summary>The catalog's records with the repairs made, in key order.</summary>
    public static List<(byte[] Key, byte[] Data)> Repair(FirstAidRun run, List<PlannedChange> changes,
        Dictionary<(uint FileId, byte Fork), List<(uint Start, uint Count)>> relocated)
    {
        var repair = new PlusCatalogRepair(run, changes, relocated);
        repair.Threads();
        repair.MissingThreads();
        repair.Files();
        repair.Valences();
        repair.LinkCounts();
        repair.AttributeFlags();
        repair.FolderCounts();
        return repair.records;
    }

    // A file thread without its file is deleted; a folder thread without its folder makes it again; a thread whose
    // record is another's is deleted (made again below).
    private void Threads()
    {
        var removed = new HashSet<byte[]>();
        var added = new List<(byte[] Key, byte[] Data)>();
        foreach (var (key, data) in records)
        {
            ushort type = Type(data);
            if (type is not (FolderThread or FileThread) || data.Length < 10)
            {
                continue;
            }

            uint cnid = KeyId(key);
            var reader = new BigEndianReader(data);
            var name = data.AsSpan(10, 2 * reader.ReadUInt16At(8)).ToArray();
            var target = Find(reader.ReadUInt32At(4), name);
            if (target is null && type == FileThread)
            {
                removed.Add(key);
                Add(cnid, "a file thread whose file is missing deleted");
            }
            else if (target is null)
            {
                var folder = new byte[88];
                var writer = new BigEndianWriter(folder);
                writer.WriteUInt16At(0, Folder);
                writer.WriteUInt32At(8, cnid);
                added.Add((Key(reader.ReadUInt32At(4), name), folder));
                Add(cnid, "a missing folder record made again from its thread");
            }
            else if (Type(target.Value.Data) != (type == FolderThread ? Folder : File) || Id(target.Value.Data) != cnid)
            {
                removed.Add(key);
                Add(cnid, "a thread naming another record deleted");
            }
        }

        records.RemoveAll(r => removed.Contains(r.Key));
        records.AddRange(added);
        Sort();
    }

    // Every folder (the root's included) and file gets a thread naming its parent and name.
    private void MissingThreads()
    {
        var added = new List<(byte[] Key, byte[] Data)>();
        var removed = new HashSet<byte[]>();
        foreach (var (key, data) in records)
        {
            ushort type = Type(data);
            if (type is not (Folder or File))
            {
                continue;
            }

            uint id = Id(data);
            var thread = new BigEndianWriter();
            thread.WriteUInt16(type == Folder ? FolderThread : FileThread);
            thread.WriteUInt16((ushort)0);
            thread.WriteBytes(key.AsSpan(2, 4));
            thread.WriteBytes(key.AsSpan(6));
            var made = thread.ToArray();
            if (Find(id, []) is { } existing)
            {
                if (existing.Data.AsSpan().SequenceEqual(made))
                {
                    continue;
                }

                removed.Add(existing.Key);
            }

            added.Add((Key(id, []), made));
            Add(id, type == Folder ? "a missing folder thread made" : "a missing file thread made");
        }

        records.RemoveAll(r => removed.Contains(r.Key));
        records.AddRange(added);
        Sort();
    }

    // Each file's thread flag, and its forks' block counts raised to their extents.
    private void Files()
    {
        var blocks = new Dictionary<(uint, byte), long>();
        foreach (var (fileId, fork, _, count) in run.ForkExtents)
        {
            blocks[(fileId, fork)] = blocks.GetValueOrDefault((fileId, fork)) + count;
        }

        foreach (var (_, data) in records.Where(r => Type(r.Data) == File && r.Data.Length >= 248))
        {
            var reader = new BigEndianReader(data);
            var writer = new BigEndianWriter(data);
            uint id = Id(data);
            if ((reader.ReadUInt16At(2) & ThreadExists) == 0)
            {
                writer.WriteUInt16At(2, (ushort)(reader.ReadUInt16At(2) | ThreadExists));
                Add(id, "the file's thread flag set");
            }

            foreach (var (at, fork, name) in new[] { (88, (byte)0x00, "data"), (168, (byte)0xFF, "resource") })
            {
                // A fork given its own copy (#12): its first eight extents; the rest are in the extents tree.
                if (relocated.TryGetValue((id, fork), out var moved))
                {
                    writer.WriteBytesAt(at + 16, OverlapRepair.PlusRecords(id, fork, moved).First);
                }

                uint declared = reader.ReadUInt32At(at + 12);
                long extents = blocks.GetValueOrDefault((id, fork));
                if (declared < extents)
                {
                    writer.WriteUInt32At(at + 12, extents);
                    Add(id, $"the {name} fork's block count set from {declared} to {extents}, its extents");
                }
            }
        }
    }

    // Each folder's valence: the folders and files whose parent it is.
    private void Valences()
    {
        var items = new Dictionary<uint, uint>();
        foreach (var (key, data) in records.Where(r => Type(r.Data) is Folder or File))
        {
            items[KeyId(key)] = items.GetValueOrDefault(KeyId(key)) + 1;
        }

        foreach (var (_, data) in records.Where(r => Type(r.Data) == Folder && r.Data.Length >= 12))
        {
            var reader = new BigEndianReader(data);
            uint id = Id(data), valence = items.GetValueOrDefault(id), old = reader.ReadUInt32At(4);
            if (valence != old)
            {
                new BigEndianWriter(data).WriteUInt32At(4, valence);
                Add(id, $"the folder's valence set from {old} to {valence}");
            }
        }
    }

    // HFSX: each folder with the has-folder-count flag gets its folders and directory hard links as its folder count; one
    // without it stays so, as Mac OS X 10.4 would not keep the count up (hfs-plus.md §2.4).
    private void FolderCounts()
    {
        if (!run.Hfsx)
        {
            return;
        }

        var counts = PlusCatalogScan.FolderCounts(records);
        foreach (var (_, data) in records.Where(r => Type(r.Data) == Folder && r.Data.Length >= 88))
        {
            uint id = Id(data), count = counts.GetValueOrDefault(id), old = new BigEndianReader(data).ReadUInt32At(84);
            if ((data[3] & 0x10) != 0 && old != count)
            {
                new BigEndianWriter(data).WriteUInt32At(84, count);
                Add(id, $"the folder count set from {old} to {count}");
            }
        }
    }

    // Each file's and folder's has-attributes flag: set when it keeps attributes.
    private void AttributeFlags()
    {
        foreach (var (_, data) in records.Where(r => Type(r.Data) is Folder or File && r.Data.Length >= 12))
        {
            uint id = Id(data);
            bool has = run.AttributeOwners.Contains(id), flagged = (data[3] & 0x04) != 0;
            if (has != flagged)
            {
                data[3] ^= 0x04;
                Add(id, has ? "the has-attributes flag set" : "the has-attributes flag cleared");
            }
        }
    }

    // Each indirect file's and folder's link count: the links naming it.
    private void LinkCounts()
    {
        var (fileLinks, folderLinks, privateFiles, privateFolders) = PlusLinkCheck.Links(records);
        foreach (var (links, parent, folder) in new[] { (fileLinks, privateFiles, false), (folderLinks, privateFolders, true) })
        {
            var children = PlusLinkCheck.Children(records, parent);
            foreach (var (reference, count) in links)
            {
                if (children.GetValueOrDefault(PlusLinkCheck.IndirectName(reference, folder)) is { Length: >= 88 } data)
                {
                    var reader = new BigEndianReader(data);
                    uint old = reader.ReadUInt32At(44);
                    if (old != count)
                    {
                        new BigEndianWriter(data).WriteUInt32At(44, count);
                        Add(Id(data), $"the link count set from {old} to {count}, its links");
                    }
                }
            }
        }
    }

    private void Add(uint cnid, string detail) => changes.Add(new PlannedChange("repair", "", $"catalog, CNID {cnid}: {detail}"));

    private static ushort Type(byte[] data) => data.Length >= 2 ? RecordType(data) : (ushort)0;

    private static uint Id(byte[] data) => data.Length >= 12 ? PlusRecordId(data) : 0;


    // A catalog key (parent, name), the name in UTF-16 big-endian units.
    private static byte[] Key(uint parent, byte[] name)
    {
        var key = new byte[8 + name.Length];
        var writer = new BigEndianWriter(key);
        writer.WriteUInt16At(0, 6 + name.Length);
        writer.WriteUInt32At(2, parent);
        writer.WriteUInt16At(6, name.Length / 2);
        name.CopyTo(key, 8);
        return key;
    }

    private void Sort() => records.Sort((a, b) => catalog.Compare(a.Key, b.Key));

    private (byte[] Key, byte[] Data)? Find(uint parent, byte[] name)
    {
        var search = Key(parent, name);
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
