using System.Collections.Generic;
using System.Text;

namespace ClassicMac.Files.Hfs;

/// <summary>
/// Disk First Aid 8.5.5's words (hfs.md §5.6): its problem texts by number (STR# 1202; error code −(499 + number)), its
/// stage lines (STR# 1200) and its results (STR# 131, STR# 1100) [Code: Disk First Aid 8.5.5].
/// </summary>
public static class FirstAidMessages
{
    private static readonly string?[] Problems =
    [
        null,
        "Invalid PEOF", "Invalid LEOF", "Invalid directory valence", "Invalid CName", "Invalid node height",
        "Missing file record for file thread", "Invalid allocation block size", "Invalid number of allocation blocks",
        "Invalid VBM start block", "Invalid allocation block start", "Invalid extent entry", "Overlapped extent allocation",
        "Invalid BTH length", "BT map too short during repair", "Invalid root node number", "Invalid node type", null,
        "Invalid record count", "Invalid index key", "Invalid index link", "Invalid sibling link", "Invalid node structure",
        "Overlapped node allocation", "Invalid map node linkage", "Invalid key length", "Keys out of order",
        "Invalid map node", "Invalid header node", "Exceeded maximum BTree depth", null, "Invalid catalog record type",
        "Invalid directory record length", "Invalid thread record length", "Invalid file record length",
        "Missing thread record for root dir", "Missing thread record", "Missing directory record",
        "Invalid key for thread record", "Invalid parent CName in thread record", "Invalid catalog record length",
        "Loop in directory hierarchy", "Invalid root directory count", "Invalid root file count",
        "Invalid volume directory count", "Invalid volume file count", "Invalid catalog PEOF", "Invalid extent file PEOF",
        "Nesting of folders has exceeded the recommended limit of 100 on this volume, and the Finder may have problems working with this disk",
        null, "File thread flag not set in file rec", "Missing folder detected", "Invalid file name",
        "Invalid file clump size", "Invalid BTree Header", "Directory name locked", "Catalog file entry not found for extent",
        "Custom icon missing", "Master Directory Block needs minor repair", "Volume Header needs minor repair",
        "Volume Bit Map needs minor repair", "Invalid BTree node size", "Invalid catalog record type found", null,
        "Reserved fields in the catalog record have incorrect data", "Invalid file or directory ID found",
        "The version in the VolumeHeader is not compatible with this version of Disk First Aid", "Disk full error",
        "Internal files overlap", "Invalid Volume Header",
        "The field in the Master Directory Block representing the first allocation block is wrong.",
        "The field in the Master Directory Block representing the first catalog extent is wrong.",
    ];

    // Codes for the long texts, which would make unwieldy ones.
    private static readonly Dictionary<int, string> ShortCodes = new()
    {
        [48] = "folder-nesting",
        [66] = "volume-header-version",
        [70] = "first-allocation-block",
        [71] = "first-catalog-extent",
    };

    /// <summary>Problem <paramref name="number"/>'s text (1–71), or null for the unused numbers.</summary>
    public static string? Text(int number) => number >= 1 && number < Problems.Length ? Problems[number] : null;

    /// <summary>The diagnostic code for problem <paramref name="number"/> (<c>firstaid.invalid-peof</c>), or null.</summary>
    public static string? Code(int number) =>
        Text(number) is { } text ? "firstaid." + (ShortCodes.TryGetValue(number, out var code) ? code : Slug(text)) : null;

    /// <summary>A text as a code: lowercase words joined with hyphens.</summary>
    internal static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }

    // STR# 1200: the stage lines, in the order they run.
    internal const string CheckingDiskVolume = "Checking disk volume.";
    internal const string CheckingStandardVolume = "Checking \"Mac OS Standard\" volume structures.";
    internal const string CheckingLockedName = "Checking for locked volume name.";
    internal const string CheckingExtentsBTree = "Checking extent BTree.";
    internal const string CheckingExtentsFile = "Checking extent file.";
    internal const string CheckingCatalogBTree = "Checking catalog BTree.";
    internal const string CheckingCatalogFile = "Checking catalog file.";
    internal const string CheckingHierarchy = "Checking catalog hierarchy.";
    internal const string CheckingBitmap = "Checking volume bit map.";
    internal const string CheckingVolumeInfo = "Checking volume info.";
    internal const string MountCheckSerious = "MountCheck found serious errors";
    internal const string MountCheckMinor = "MountCheck found minor errors";

    // STR# 131 and STR# 1100: the results.
    internal static string AppearsOk(string volume) => $"The volume “{volume}” appears to be OK.";

    internal static string NeedsRepair(string volume) => $"The volume “{volume}” needs to be repaired.";

    internal static string Repaired(string volume) => $"The volume “{volume}” was repaired successfully.";

    internal const string CannotRepair = "Test done. Problems were found, but Disk First Aid cannot repair them.";
    internal const string NotHfs = "This is not an HFS disk.";
    internal const string UnableToRead = "Unable to read from disk.";

    // ClassicMac's own: Disk First Aid 8.5.5 checks HFS Plus too, which ClassicMac's First Aid does not yet (hfs.md §5.6).
    internal const string NotChecked = "An HFS Plus volume: ClassicMac's First Aid checks Mac OS Standard (HFS) volumes.";
}
