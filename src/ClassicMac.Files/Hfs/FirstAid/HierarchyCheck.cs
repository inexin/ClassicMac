using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// "Checking catalog hierarchy." (hfs.md §5.6): a depth-first walk from the root, each folder's valence against the
// records under it, then the walk's counts against the scan's, which differ only when records cannot be reached from
// the root [Code: Disk First Aid 8.5.5, CODE 1 $1BEA8] [Verified: C6].
internal sealed class HierarchyCheck
{
    private const int MaxNesting = 100;
    private readonly FirstAidRun run;
    private readonly Dictionary<uint, List<(byte[] Key, byte[] Data, uint Node)>> children = [];
    private readonly HashSet<uint> path = [];
    private int dirs, rootDirs, files, rootFiles;
    private bool tooDeep;

    private HierarchyCheck(FirstAidRun run)
    {
        this.run = run;
        foreach (var record in run.Catalog!.Records)
        {
            uint parent = new BigEndianReader(record.Key).ReadUInt32At(2);
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(record);
        }
    }

    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingHierarchy);
        return new HierarchyCheck(run).Check();
    }

    private bool Check()
    {
        // The key (1, "") must not exist: the root's parent holds only the root folder.
        if (children.TryGetValue(1, out var top) && top.Exists(r => r.Key[6] == 0))
        {
            return run.Fatal(31, 1);
        }

        if (!Walk(1, 0))
        {
            return false;
        }

        if (tooDeep)
        {
            return true;                                                         // past the limit nothing more is checked
        }

        if (rootDirs != run.RootDirCount)
        {
            run.Flag(42, FirstAidRepairs.Valences, rootDirs, run.RootDirCount);
        }

        if (rootFiles != run.RootFileCount)
        {
            run.Flag(43, FirstAidRepairs.Valences, rootFiles, run.RootFileCount);
        }

        if (dirs != run.DirCount)
        {
            run.Flag(44, FirstAidRepairs.Valences, dirs, run.DirCount);
        }

        if (files != run.FileCount)
        {
            run.Flag(45, FirstAidRepairs.Valences, files, run.FileCount);
        }

        return true;
    }

    // A folder's records: its thread, folders and files; each folder walked in turn, then its valence checked.
    private bool Walk(uint folder, int depth)
    {
        if (!children.TryGetValue(folder, out var records))
        {
            return true;
        }

        path.Add(folder);
        foreach (var (_, data, node) in records)
        {
            if (data[0] == 1)
            {
                var reader = new BigEndianReader(data);
                uint id = reader.ReadUInt32At(6);
                int nesting = depth;
                if (folder > 1)
                {
                    nesting++;
                    dirs++;
                }

                if (folder == 2)
                {
                    rootDirs++;
                }

                if (nesting > MaxNesting)
                {
                    run.Note(48, id, node);
                    tooDeep = true;
                    return true;
                }

                if (path.Contains(id))
                {
                    return run.Fatal(41, id, node);
                }

                if (!Walk(id, nesting))
                {
                    return false;
                }

                if (tooDeep)
                {
                    return true;
                }

                // The items under it: every record with it as parent, its thread aside.
                int items = children.TryGetValue(id, out var under) ? under.Count - 1 : -1;
                if (reader.ReadUInt16At(4) != items)
                {
                    run.Flag(3, FirstAidRepairs.Valences, id, node);
                }
            }
            else if (data[0] == 2)
            {
                files++;
                if (folder == 2)
                {
                    rootFiles++;
                }
            }
        }

        path.Remove(folder);
        return true;
    }
}
