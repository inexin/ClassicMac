using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// The repairs a verify found needed, as Disk First Aid's status words record them (hfs.md §5.6); any set makes the
/// volume "need to be repaired".
/// </summary>
[Flags]
public enum FirstAidRepairs
{
    /// <summary>No repair: the volume appears to be OK.</summary>
    None = 0,

    /// <summary>The MDB is written from the values the check computed (#58).</summary>
    Mdb = 1 << 0,

    /// <summary>The volume bitmap is written from the blocks the check found in use (#60).</summary>
    Bitmap = 1 << 1,

    /// <summary>Folder valences and the volume's counts are set to the ones counted (#3, #42–#45).</summary>
    Valences = 1 << 2,

    /// <summary>A B-tree is rebuilt from its records.</summary>
    RebuildBTree = 1 << 3,

    /// <summary>A B-tree's header record is written from the values the walk computed (#54).</summary>
    BTreeHeader = 1 << 4,

    /// <summary>A B-tree's node map is written from the nodes the walk reached.</summary>
    BTreeMap = 1 << 5,

    /// <summary>Reserved fields of catalog records are cleared (#64).</summary>
    ReservedFields = 1 << 6,

    /// <summary>A file thread whose file is missing is deleted (#6).</summary>
    FileThreads = 1 << 7,

    /// <summary>A missing directory record is made again (#37).</summary>
    MissingFolder = 1 << 8,

    /// <summary>Missing thread records are made.</summary>
    MissingThreads = 1 << 9,

    /// <summary>A Finder flag is cleared: the root's name lock (#55), a folder's custom icon (#57).</summary>
    FinderFlags = 1 << 10,

    /// <summary>Files that share blocks get copies (#12).</summary>
    OverlappingExtents = 1 << 11,

    /// <summary>Overflow extents records of files not in the catalog are deleted.</summary>
    OrphanedExtents = 1 << 12,

    /// <summary>MountCheck's minor findings: MountCheck runs again and the MDB is written.</summary>
    MountCheck = 1 << 13,

    /// <summary>The alternate MDB is written from the primary.</summary>
    AlternateMdb = 1 << 14,

    /// <summary>Overflow extents records' start blocks (<c>xkrFABN</c>) are renumbered.</summary>
    ExtentStarts = 1 << 15,

    /// <summary>A fork's physical length short of its blocks is set to them.</summary>
    ForkLengths = 1 << 16,
}

// The state of one First Aid verify: the volume, the MDBs it reads, the stage lines shown, the problems found, the
// repairs needed, and how it ended.
internal sealed class FirstAidRun(HfsVolume volume)
{
    public const int SectorSize = 512;

    public HfsVolume Volume { get; } = volume;

    /// <summary>The device's size in 512-byte sectors, Disk First Aid's S.</summary>
    public long Sectors { get; } = volume.Length / SectorSize;

    /// <summary>The alternate MDB, at sector S − 2: Disk First Aid builds its view of the volume from it.</summary>
    public byte[] Alternate { get; set; } = [];

    /// <summary>The primary MDB, at sector 2.</summary>
    public byte[] Primary { get; set; } = [];

    // The volume's geometry, from the alternate MDB as Disk First Aid takes it (its CVCB).
    public uint BlockSize { get; set; }

    public uint BlockCount { get; set; }

    public int AllocationStart { get; set; }

    public int BitmapStart { get; set; }

    /// <summary>The extents overflow file's B-tree, once set up.</summary>
    public FirstAidTree? Extents { get; set; }

    /// <summary>The catalog's B-tree, once set up.</summary>
    public FirstAidTree? Catalog { get; set; }

    /// <summary>The root folder's name, from its catalog key: the name the MDB's <c>drVN</c> must match.</summary>
    public byte[] RootName { get; set; } = [];

    // What the catalog scan counts (Disk First Aid's CVCB): the volume's counts and the next CNID.
    public int DirCount { get; set; }

    public int FileCount { get; set; }

    public int RootDirCount { get; set; }

    public int RootFileCount { get; set; }

    public uint NextCnid { get; set; }

    /// <summary>The B-trees (by file ID, 3 or 4) whose header, map or nodes repair writes again.</summary>
    public HashSet<int> TreesToRebuild { get; } = [];

    /// <summary>The bitmap built from the extents (the bitmap stage's), which repair writes.</summary>
    public byte[] ComputedBitmap { get; set; } = [];

    /// <summary>The MDB the volume-info stage computes, which repair writes.</summary>
    public byte[] ComputedMdb { get; set; } = [];

    /// <summary>The file IDs the scan saw (for the orphaned-extents repair).</summary>
    public HashSet<uint> FileIds { get; } = [];

    /// <summary>Every fork's extents: (file ID, fork, extent), the catalog's and extents file's included.</summary>
    public List<(uint FileId, byte Fork, uint Start, uint Count)> ForkExtents { get; } = [];

    public List<string> Stages { get; } = [];

    public List<FirstAidProblem> Problems { get; } = [];

    public FirstAidRepairs Repairs { get; set; }

    /// <summary>A problem no repair fixes was found, though the check went on.</summary>
    public bool Unrepairable { get; set; }

    /// <summary>How the check ended early, or null while it runs or when every stage passed.</summary>
    public FirstAidVerdict? Ended { get; set; }

    private string stage = "";

    public void Stage(string line)
    {
        stage = line;
        Stages.Add(line);
    }

    public byte[] ReadSector(long sector)
    {
        var bytes = new byte[SectorSize];
        Volume.Read(sector * SectorSize, bytes);
        return bytes;
    }

    /// <summary>
    /// A fork's bytes from its extents (start block, block count), <paramref name="length"/> long; blocks past the end of
    /// the volume read as zeros.
    /// </summary>
    public byte[] ReadExtents(IEnumerable<(uint Start, uint Count)> extents, long length)
    {
        var bytes = new byte[length];
        long at = 0;
        foreach (var (start, count) in extents)
        {
            long offset = (long)AllocationStart * SectorSize + (long)start * BlockSize;
            long size = Math.Min((long)count * BlockSize, length - at);
            long available = Math.Clamp(Volume.Length - offset, 0, size);
            if (available > 0)
            {
                Volume.Read(offset, bytes.AsSpan(checked((int)at), checked((int)available)));
            }

            at += size;
            if (at >= length)
            {
                break;
            }
        }

        return bytes;
    }

    /// <summary>Records a problem that ends the check: Disk First Aid cannot repair it. Returns false, for the stage to return.</summary>
    public bool Fatal(int number, long arg2 = 0, long arg3 = 0)
    {
        Add(number, arg2, arg3, repairable: false);
        Ended = FirstAidVerdict.CannotRepair;
        return false;
    }

    /// <summary>Records a problem repair fixes, and the repair it needs; the check goes on.</summary>
    public void Flag(int number, FirstAidRepairs repairs, long arg2 = 0, long arg3 = 0)
    {
        Add(number, arg2, arg3, repairable: true);
        Repairs |= repairs;
    }

    /// <summary>
    /// Records a problem with no number (MountCheck's, and the checks Disk First Aid lacks) once per code, with the repair
    /// it needs; with none, no repair fixes it.
    /// </summary>
    public void Problem(string message, string code, FirstAidRepairs repairs)
    {
        if (!Problems.Exists(p => p.Code == code))
        {
            Problems.Add(new FirstAidProblem(0, message, 0, 0, stage, repairs != FirstAidRepairs.None, code));
        }

        Repairs |= repairs;
        Unrepairable |= repairs == FirstAidRepairs.None;
    }

    /// <summary>Records a problem that needs no repair: the volume can still appear to be OK.</summary>
    public void Note(int number, long arg2 = 0, long arg3 = 0) => Add(number, arg2, arg3, repairable: true);

    /// <summary>Records a repair Disk First Aid makes without printing a problem.</summary>
    public void Silent(FirstAidRepairs repairs) => Repairs |= repairs;

    /// <summary>Ends the check: the disk is not an HFS volume (or not one ClassicMac's First Aid checks).</summary>
    public bool End(FirstAidVerdict verdict)
    {
        Ended = verdict;
        return false;
    }

    private void Add(int number, long arg2, long arg3, bool repairable) =>
        Problems.Add(new FirstAidProblem(number, FirstAidMessages.Text(number) ?? $"Problem {number}", arg2, arg3, stage,
            repairable, FirstAidMessages.Code(number) ?? $"firstaid.problem-{number}"));

    /// <summary>The volume's name, from the primary MDB's <c>drVN</c>.</summary>
    public string VolumeName =>
        Primary.Length >= 0x40 ? MacRoman.Decode(Primary.AsSpan(0x25, Math.Min((int)Primary[0x24], 27))) : "";
}
