using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// "Checking for locked volume name." (hfs.md §5.6): the catalog's first leaf record, the root folder's, gives the volume
// its name, and the root's name lock is a problem repair clears [Code: Disk First Aid 8.5.5, CODE 1 $1D4B2]
// [Verified: root frFlags | $1000 → "Directory name locked", needs repair].
internal static class LockedNameCheck
{
    private const ushort NameLocked = 0x1000;

    public static bool Run(FirstAidRun run)
    {
        run.Stage(FirstAidMessages.CheckingLockedName);
        var tree = run.Catalog!;
        uint node = tree.File.FirstLeaf;
        if (node == 0 || node >= tree.TotalNodes || !tree.File.TryRecord(node, 0, out var key, out var data) || key.Length < 7)
        {
            return run.Fatal(56);                                                // its text is not what it means
        }

        run.RootName = key.Slice(7, System.Math.Min(key.Span[6], key.Length - 7)).ToArray();
        if (data.Length >= 0x46 && data.Span[0] == 1 && (new BigEndianReader(data).ReadUInt16At(0x1E) & NameLocked) != 0)
        {
            run.Flag(55, FirstAidRepairs.FinderFlags, new BigEndianReader(data).ReadUInt32At(6), node);
        }

        return true;
    }
}
