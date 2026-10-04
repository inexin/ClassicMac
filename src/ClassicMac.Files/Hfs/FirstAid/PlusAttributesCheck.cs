using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files.Hfs;

// The attributes tree's records on HFS Plus, in "Checking catalog hierarchy." (hfs-plus.md §1.7, §2.6, §5.4): each
// attribute belongs to a catalog file or folder (or a special file, CNIDs 3 to 8); inline data ($10) fits its record;
// a fork-data attribute ($20) has its extents add up to its block count, with extension records ($30) continuing it at
// their start blocks, and its blocks count as in use; other record types are ignored [Doc: TN1150]. A record that fails
// is deleted by repair (an attribute whose extents are wrong is not: its data would be lost). Each file's and
// folder's has-attributes flag matches whether it has attributes (repaired).
internal static class PlusAttributesCheck
{
    private const uint Inline = 0x10, ForkData = 0x20, Extents = 0x30;
    private const ushort HasAttributes = 0x0004;
    internal const byte AttributeFork = 0x10;

    public static void Run(FirstAidRun run)
    {
        var tree = run.AttributesTree;
        var catalog = new Dictionary<uint, bool>();
        foreach (var (_, data, _) in run.Catalog!.Records)
        {
            if (data.Length >= 12 && data[0] == 0 && data[1] is 1 or 2)
            {
                var reader = new BigEndianReader(data);
                catalog[reader.ReadUInt32At(8)] = (reader.ReadUInt16At(2) & HasAttributes) != 0;
            }
        }

        var owners = new HashSet<uint>();
        var forks = new Dictionary<string, (uint FileId, uint Declared, long Blocks, List<(uint Start, uint Count)> Extents)>(StringComparer.Ordinal);
        foreach (var (key, data, node) in tree?.Records ?? [])
        {
            var keyReader = new BigEndianReader(key);
            uint fileId = keyReader.ReadUInt32At(4), startBlock = keyReader.ReadUInt32At(8);
            string name = Convert.ToHexString(key.AsSpan(12)) + "@" + fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!catalog.ContainsKey(fileId) && fileId is not (>= 3 and <= 8))
            {
                Bad(run, key, "An attribute belongs to no file or folder", "firstaid.attribute-owner");
                continue;
            }

            var reader = new BigEndianReader(data);
            uint type = data.Length >= 4 ? reader.ReadUInt32At(0) : 0;
            if (type == Inline && (data.Length < 16 || 16L + reader.ReadUInt32At(12) > data.Length)
                || type == ForkData && data.Length < 88 || type == Extents && data.Length < 72)
            {
                Bad(run, key, "An attribute record's length is wrong", "firstaid.attribute-record");
                continue;
            }

            if (type == ForkData)
            {
                var record = data.AsMemory(8 + 16, PlusExtentRecords.RecordLength);
                if (!PlusExtentRecords.Check(run, record, fileId, node))
                {
                    return;
                }

                var extents = PlusExtentRecords.Of(record).ToList();
                var fork = new BigEndianReader(data.AsMemory(8, PlusExtentRecords.ForkDataLength));
                forks[name] = (fileId, fork.ReadUInt32At(12), extents.Sum(e => (long)e.Count), extents);
                if (fork.ReadUInt64At(0) > (ulong)fork.ReadUInt32At(12) * run.BlockSize)
                {
                    run.Problem("An attribute's size is past its blocks", "firstaid.attribute-extents", FirstAidRepairs.None);
                }
            }
            else if (type == Extents)
            {
                if (!forks.TryGetValue(name, out var fork) || fork.Blocks != startBlock)
                {
                    Bad(run, key, "An attribute's extents record continues no fork record", "firstaid.attribute-record");
                    continue;
                }

                var record = data.AsMemory(8, PlusExtentRecords.RecordLength);
                if (!PlusExtentRecords.Check(run, record, fileId, node))
                {
                    return;
                }

                foreach (var extent in PlusExtentRecords.Of(record))
                {
                    fork.Extents.Add(extent);
                    fork.Blocks += extent.Count;
                }

                forks[name] = fork;
            }

            owners.Add(fileId);
        }

        foreach (var (fileId, declared, blocks, extents) in forks.Values)
        {
            if (blocks != declared)
            {
                run.Problem("An attribute's extents are not its block count", "firstaid.attribute-extents", FirstAidRepairs.None);
            }

            run.ForkExtents.AddRange(extents.Select(e => (fileId, AttributeFork, e.Start, e.Count)));
        }

        run.AttributeOwners = owners;
        if (catalog.Any(c => c.Value != owners.Contains(c.Key)))
        {
            run.Problem("A file's or folder's has-attributes flag disagrees with its attributes", "firstaid.attribute-flag", FirstAidRepairs.AttributeRecords);
        }
    }

    private static void Bad(FirstAidRun run, byte[] key, string message, string code)
    {
        run.Problem(message, code, FirstAidRepairs.AttributeRecords);
        run.BadAttributes.Add(key);
    }
}
