using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Editing;

namespace ClassicMac.Files.Hfs;

// Disk First Aid's repair list, applied to the catalog's leaf records before the tree is written again (hfs.md §5.6):
// reserved fields cleared (#64), the root's name lock and a custom icon without its icon file cleared (#55, #57), a
// file thread without its file deleted or its file's thread flag set (#6), a folder made again from its thread (#37),
// missing threads made, and every folder's valence set to its items [Verified: Disk First Aid 8.5.5's repairs of RC1,
// RC6, RC7]. Each change names the record's CNID.
internal sealed class CatalogRepair
{
    private const byte Folder = 1, File = 2, FolderThread = 3, FileThread = 4;
    private const ushort NameLocked = 0x1000, HasCustomIcon = 0x0400;
    private static readonly byte[] IconName = [(byte)'I', (byte)'c', (byte)'o', (byte)'n', 0x0D];
    private readonly List<(byte[] Key, byte[] Data)> records;
    private readonly List<PlannedChange> changes;

    private CatalogRepair(IEnumerable<(byte[] Key, byte[] Data, uint Node)> records, List<PlannedChange> changes)
    {
        this.records = records.Select(r => (r.Key.ToArray(), r.Data.ToArray())).ToList();
        this.changes = changes;
    }

    /// <summary>The catalog's records with the repairs made, in key order; the changes are added to <paramref name="changes"/>.</summary>
    public static List<(byte[] Key, byte[] Data)> Repair(FirstAidTree catalog, List<PlannedChange> changes)
    {
        var repair = new CatalogRepair(catalog.Records, changes);
        repair.Fields();
        repair.Threads();
        repair.MissingThreads();
        repair.Valences();
        return repair.records;
    }

    // Reserved fields and Finder flags, record by record.
    private void Fields()
    {
        foreach (var (key, data) in records)
        {
            var writer = new BigEndianWriter(data);
            var reader = new BigEndianReader(data);
            switch (data[0])
            {
                case FolderThread or FileThread when data.AsSpan(2, 8).IndexOfAnyExcept((byte)0) >= 0:
                    data.AsSpan(2, 8).Clear();
                    Add(Parent(key), "a thread record's reserved fields cleared");
                    break;
                case Folder:
                    FolderFields(key, data, reader, writer);
                    break;
                case File:
                    if ((data[2] & 0x7C) != 0 || reader.ReadUInt16At(0x18) != 0 || reader.ReadUInt16At(0x22) != 0 || reader.ReadUInt32At(0x62) != 0)
                    {
                        data[2] &= unchecked((byte)~0x7C);
                        writer.WriteUInt16At(0x18, (ushort)0);
                        writer.WriteUInt16At(0x22, (ushort)0);
                        writer.WriteUInt32At(0x62, 0u);
                        Add(reader.ReadUInt32At(0x14), "a file record's reserved fields cleared");
                    }

                    break;
            }
        }
    }

    private void FolderFields(byte[] key, byte[] data, BigEndianReader reader, BigEndianWriter writer)
    {
        uint id = reader.ReadUInt32At(6);
        if (reader.ReadUInt16At(2) != 0)
        {
            writer.WriteUInt16At(2, (ushort)0);
            Add(id, "a folder record's reserved field cleared");
        }

        ushort flags = reader.ReadUInt16At(0x1E);
        if (Parent(key) == 1 && (flags & NameLocked) != 0)
        {
            flags &= unchecked((ushort)~NameLocked);
            Add(id, "the root folder's name lock cleared");
        }

        if ((flags & HasCustomIcon) != 0 && !(Find(id, IconName) is { } icon && icon.Data[0] == File))
        {
            flags &= unchecked((ushort)~HasCustomIcon);
            Add(id, "the custom icon flag cleared (the folder has no Icon file)");
        }

        writer.WriteUInt16At(0x1E, flags);
    }

    // A file thread: deleted when its file is missing, its file's thread flag set otherwise; a folder thread whose
    // folder is missing makes the folder again, empty until the valences are counted.
    private void Threads()
    {
        var removed = new HashSet<byte[]>();
        var added = new List<(byte[] Key, byte[] Data)>();
        foreach (var (key, data) in records)
        {
            if (data[0] is not (FolderThread or FileThread))
            {
                continue;
            }

            uint id = Parent(key);
            var targetName = data.AsSpan(0x0F, data[0x0E]).ToArray();
            uint targetParent = new BigEndianReader(data).ReadUInt32At(0x0A);
            var target = Find(targetParent, targetName);
            if (data[0] == FileThread && target is null)
            {
                removed.Add(key);
                Add(id, "a file thread whose file is missing deleted");
            }
            else if (data[0] == FileThread && (target!.Value.Data[2] & 0x02) == 0)
            {
                target.Value.Data[2] |= 0x02;
                Add(id, "the file's thread flag set");
            }
            else if (data[0] == FolderThread && target is null)
            {
                var folder = new byte[70];
                folder[0] = Folder;
                new BigEndianWriter(folder).WriteUInt32At(6, id);
                added.Add((Key(targetParent, targetName), folder));
                Add(id, "a missing folder record made again from its thread");
            }
        }

        records.RemoveAll(r => removed.Contains(r.Key));
        records.AddRange(added);
        Sort();
    }

    // A thread for every folder (the root's included) and every file whose thread flag is set.
    private void MissingThreads()
    {
        var added = new List<(byte[] Key, byte[] Data)>();
        foreach (var (key, data) in records)
        {
            bool folder = data[0] == Folder;
            if (!folder && !(data[0] == File && (data[2] & 0x02) != 0))
            {
                continue;
            }

            uint id = new BigEndianReader(data).ReadUInt32At(folder ? 6 : 0x14);
            if (Find(id, []) is not null)
            {
                continue;
            }

            var thread = new byte[46];
            thread[0] = folder ? FolderThread : FileThread;
            new BigEndianWriter(thread).WriteUInt32At(0x0A, Parent(key));
            key.AsSpan(6, 1 + key[6]).CopyTo(thread.AsSpan(0x0E));
            added.Add((Key(id, []), thread));
            Add(id, folder ? "a missing folder thread made" : "a missing file thread made");
        }

        records.AddRange(added);
        Sort();
    }

    // Each folder's valence: the folders and files whose parent it is.
    private void Valences()
    {
        var items = new Dictionary<uint, int>();
        foreach (var (key, data) in records)
        {
            if (data[0] is Folder or File)
            {
                items[Parent(key)] = items.GetValueOrDefault(Parent(key)) + 1;
            }
        }

        foreach (var (_, data) in records.Where(r => r.Data[0] == Folder))
        {
            var reader = new BigEndianReader(data);
            uint id = reader.ReadUInt32At(6);
            int valence = items.GetValueOrDefault(id), old = reader.ReadUInt16At(4);
            if (valence != old)
            {
                new BigEndianWriter(data).WriteUInt16At(4, valence);
                Add(id, $"the folder's valence set from {old} to {valence}");
            }
        }
    }

    private void Add(uint cnid, string detail) => changes.Add(new PlannedChange("repair", "", $"catalog, CNID {cnid}: {detail}"));

    private static uint Parent(byte[] key) => new BigEndianReader(key).ReadUInt32At(2);

    private static byte[] Key(uint parent, byte[] name)
    {
        var key = new byte[7 + name.Length];
        key[0] = (byte)(6 + name.Length);
        new BigEndianWriter(key).WriteUInt32At(2, parent);
        key[6] = (byte)name.Length;
        name.CopyTo(key, 7);
        return key;
    }

    private void Sort() => records.Sort((a, b) => HfsCatalogKeys.CompareCatalogKeys(a.Key, b.Key));

    private (byte[] Key, byte[] Data)? Find(uint parent, byte[] name)
    {
        var search = Key(parent, name);
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
}
