using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using ClassicMac.Core;
using static ClassicMac.Files.Hfs.HfsRecords;

namespace ClassicMac.Files.Hfs;

// Hard links on HFS Plus, in "Checking catalog hierarchy." (hfs-plus.md §1.8, §5.4): a file hard link (hlnk/hfs+)
// names the indirect file iNode<n> in the root's "\0\0\0\0HFS+ Private Data", a directory hard link (an alis/MACS alias
// with the link-chain flag) the folder dir_<CNID> in ".HFS+ Private Directory Data\r"; each must exist (not repaired:
// the link has nothing to name), and the indirect file's or folder's link count (its BSD special) is the number of its
// links (repaired) [Doc: TN1150; Code: Apple fsck_hfs dirhardlink.c, reference only].
internal static class PlusLinkCheck
{
    private const string PrivateFiles = "\0\0\0\0HFS+ Private Data", PrivateFolders = ".HFS+ Private Directory Data\r";
    private const ushort Folder = 1, File = 2, LinkChain = 0x0020, IsAlias = 0x8000;
    private static readonly uint HardLink = FourCC.FromString("hlnk").Value, HfsPlus = FourCC.FromString("hfs+").Value;
    private static readonly uint Alias = FourCC.FromString("alis").Value, Finder = FourCC.FromString("MACS").Value;

    /// <summary>The indirect files and folders by reference, with the links naming each; for the check and the repair.</summary>
    public static (Dictionary<uint, int> FileLinks, Dictionary<uint, int> FolderLinks, uint? PrivateFiles, uint? PrivateFolders) Links(
        IEnumerable<(byte[] Key, byte[] Data)> records)
    {
        var fileLinks = new Dictionary<uint, int>();
        var folderLinks = new Dictionary<uint, int>();
        uint? files = null, folders = null;
        foreach (var (key, data) in records)
        {
            var reader = new BigEndianReader(data);
            if (data.Length >= 88 && reader.ReadUInt16At(0) == Folder && KeyId(key) == 2)
            {
                string name = Name(key);
                files = name == PrivateFiles ? reader.ReadUInt32At(8) : files;
                folders = name == PrivateFolders ? reader.ReadUInt32At(8) : folders;
            }
            else if (data.Length >= 248 && reader.ReadUInt16At(0) == File)
            {
                uint type = reader.ReadUInt32At(48), creator = reader.ReadUInt32At(52), special = reader.ReadUInt32At(44);
                if (type == HardLink && creator == HfsPlus)
                {
                    fileLinks[special] = fileLinks.GetValueOrDefault(special) + 1;
                }
                else if ((reader.ReadUInt16At(2) & LinkChain) != 0 && type == Alias && creator == Finder && (reader.ReadUInt16At(56) & IsAlias) != 0)
                {
                    folderLinks[special] = folderLinks.GetValueOrDefault(special) + 1;
                }
            }
        }

        return (fileLinks, folderLinks, files, folders);
    }

    /// <summary>The indirect record a link count is checked on: iNode&lt;n&gt; for files, dir_&lt;CNID&gt; for folders.</summary>
    public static string IndirectName(uint reference, bool folder) =>
        (folder ? "dir_" : "iNode") + reference.ToString(CultureInfo.InvariantCulture);

    public static void Run(FirstAidRun run)
    {
        var records = run.Catalog!.Records.ConvertAll(r => (r.Key, r.Data));
        var (fileLinks, folderLinks, privateFiles, privateFolders) = Links(records);
        Check(run, records, fileLinks, privateFiles, folder: false);
        Check(run, records, folderLinks, privateFolders, folder: true);
    }

    private static void Check(FirstAidRun run, List<(byte[] Key, byte[] Data)> records, Dictionary<uint, int> links, uint? parent, bool folder)
    {
        var children = Children(records, parent);
        foreach (var (reference, count) in links)
        {
            var indirect = children.GetValueOrDefault(IndirectName(reference, folder));
            if (indirect is null || RecordType(indirect) != (folder ? Folder : File))
            {
                run.Problem(folder ? "A directory hard link's folder is missing" : "A hard link's indirect file is missing", "firstaid.link-target-missing",
                    FirstAidRepairs.None);
                continue;
            }

            if (new BigEndianReader(indirect).ReadUInt32At(44) != count)                 // its link count (bsdInfo.special)
            {
                run.Problem("A hard link's link count is not its number of links", "firstaid.link-count", FirstAidRepairs.LinkCounts);
            }
        }
    }

    /// <summary>A private folder's records by name (none when there is no such folder).</summary>
    internal static Dictionary<string, byte[]> Children(IEnumerable<(byte[] Key, byte[] Data)> records, uint? parent) =>
        parent is not { } p ? [] : records.Where(r => KeyId(r.Key) == p)
            .GroupBy(r => Name(r.Key), System.StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Data, System.StringComparer.Ordinal);

    internal static string Name(byte[] key)
    {
        var reader = new BigEndianReader(key);
        var chars = new char[reader.ReadUInt16At(6)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)reader.ReadUInt16At(8 + 2 * i);
        }

        return new string(chars);
    }
}
