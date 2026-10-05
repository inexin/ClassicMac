using System.Globalization;
using System.Security.Cryptography;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;

namespace Fuzz;

/// <summary>
/// The seed corpora libFuzzer starts from, one folder per target: the repository's test files (the archives and images
/// in <c>tests/ClassicMac.Files.Tests/TestData</c>, the pictures in <c>tests/golden/pict</c>), the forks, resources,
/// pictures and PEF containers inside them, and small inputs made here (an HFS volume, a resource fork, 68k and PEF
/// code). Each seed is named by its hash, so writing again adds no duplicates.
/// </summary>
internal static class FuzzSeeds
{
    /// <summary>The largest seed kept; libFuzzer's inputs grow no larger than its seeds by default.</summary>
    public const int MaxLength = 1024 * 1024;

    private static readonly FourCC Pict = FourCC.FromString("PICT");

    /// <summary>Writes the seeds under <paramref name="output"/>; the number of seeds in each target's folder.</summary>
    public static IReadOnlyDictionary<string, int> Write(string output, string repository)
    {
        void Add(string target, ReadOnlyMemory<byte> bytes)
        {
            if (bytes.Length is 0 or > MaxLength)
            {
                return;
            }

            var folder = Directory.CreateDirectory(Path.Combine(output, target)).FullName;
            File.WriteAllBytes(Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(bytes.Span))[..16]), bytes.ToArray());
        }

        var testData = Path.Combine(repository, "tests", "ClassicMac.Files.Tests", "TestData");
        foreach (var path in Directory.EnumerateFiles(testData, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            Add("container", File.ReadAllBytes(path));
            try
            {
                foreach (var node in Nodes(ContainerUnwrapper.Default.Unwrap(path)))
                {
                    AddFile(node.File, Add);
                }
            }
            catch (Exception e) when (ExceptionFilters.IsMalformed(e) || ExceptionFilters.IsFileAccess(e))
            {
            }
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(repository, "tests", "golden", "pict"), "*.pict"))
        {
            Add("pict", File.ReadAllBytes(path));
        }

        var volume = Volume();
        Add("first-aid", volume);
        Add("container", volume);
        var fork = Fork();
        Add("resource-fork", fork.ToArray());
        Add("code", fork.ToArray());
        foreach (var resource in fork.Resources)
        {
            Add("resource", FuzzTargets.ResourceInput(resource.Type, resource.GetData()));
        }

        Add("pef", Pef());
        return FuzzTargets.All.Keys.ToDictionary(t => t,
            t => Directory.Exists(Path.Combine(output, t)) ? Directory.GetFiles(Path.Combine(output, t)).Length : 0, StringComparer.Ordinal);
    }

    private static IEnumerable<ContainerNode> Nodes(ContainerNode node) => [node, .. node.Children.SelectMany(Nodes)];

    // A file's resource fork (for the fork and code targets), its resources one by one, and its picture or PEF data.
    private static void AddFile(MacFile file, Action<string, ReadOnlyMemory<byte>> add)
    {
        var data = file.DataFork.Length <= MaxLength ? file.DataFork.ToArray() : [];
        if (file.FinderInfo.Type == Pict && data.Length > 512)
        {
            add("pict", data.AsMemory(512));                                         // a PICT file's 512-byte header
        }

        if (PefContainer(data))
        {
            add("pef", data);
        }

        if (file.ResourceFork.Length is 0 or > MaxLength)
        {
            return;
        }

        var bytes = file.ResourceFork.ToArray();
        add("resource-fork", bytes);
        add("code", bytes);
        foreach (var resource in ResourceFork.Read(bytes).Resources)
        {
            var resourceData = resource.GetData();
            add("resource", FuzzTargets.ResourceInput(resource.Type, resourceData));
            if (resource.Type == Pict)
            {
                add("pict", resourceData);
            }
        }
    }

    private static bool PefContainer(ReadOnlySpan<byte> data) => data.StartsWith("Joy!peff"u8);

    // An HFS volume with a folder, a file with both forks and a fragmented one.
    private static byte[] Volume()
    {
        var date = new MacDate(3_000_000_000);                                    // fixed, so the seed's name is too
        var volume = HfsWriter.Format(400 * 1024, "Seed", date);
        volume = HfsWriter.CreateFolder(ForkData.FromBytes(volume), "Docs", date, date);
        volume = HfsWriter.CreateFile(ForkData.FromBytes(volume), "Docs:Letter", "Dear sir"u8.ToArray(), Fork().ToArray(),
            FinderInfo.Empty with { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt") }, date, date);
        volume = HfsWriter.CreateFile(ForkData.FromBytes(volume), "Read Me", new byte[3000], ReadOnlyMemory<byte>.Empty, FinderInfo.Empty,
            date, date);
        new BigEndianWriter(volume).WriteUInt32At(1024 + 6, date.Seconds);           // the MDB's drLsMod, stamped now by the writer
        return volume;
    }

    // A resource fork of text, a version, and a 68k application's jump table and one segment.
    private static ResourceFork Fork()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("STR "), 128, "\u0005hello"u8.ToArray()) { Name = MacString.FromMacRoman("greeting") });
        fork.Add(new Resource(FourCC.FromString("TEXT"), 128, "Some text.\r"u8.ToArray()));
        fork.Add(new Resource(FourCC.FromString("vers"), 1, new byte[] { 1, 0, 0x80, 0, 0, 0, 3, (byte)'1', (byte)'.', (byte)'0', 3, (byte)'1', (byte)'.', (byte)'0' }));

        var jumpTable = new BigEndianWriter();
        jumpTable.WriteUInt32(0x100u);                                            // above A5
        jumpTable.WriteUInt32(0x100u);                                            // below A5
        jumpTable.WriteUInt32(8u);                                                // jump table size
        jumpTable.WriteUInt32(32u);                                               // its offset from A5
        jumpTable.WriteUInt16(0);                                                 // entry: offset in segment 1,
        jumpTable.WriteUInt16(0x3F3C);                                            // MOVE.W #1,-(SP)
        jumpTable.WriteUInt16(1);
        jumpTable.WriteUInt16(0xA9F0);                                            // _LoadSeg
        fork.Add(new Resource(FourCC.FromString("CODE"), 0, jumpTable.ToArray()));

        var segment = new BigEndianWriter();
        segment.WriteUInt16(0);                                                   // first jump table entry
        segment.WriteUInt16(1);                                                   // entries
        segment.WriteUInt16(0x4E56);                                              // LINK A6,#0
        segment.WriteUInt16(0);
        segment.WriteUInt16(0x4E5E);                                              // UNLK A6
        segment.WriteUInt16(0x4E75);                                              // RTS
        fork.Add(new Resource(FourCC.FromString("CODE"), 1, segment.ToArray()));
        return fork;
    }

    // A PEF container's header (Code Fragment Manager's PEFContainerHeader) with no sections.
    private static byte[] Pef()
    {
        var pef = new BigEndianWriter();
        pef.WriteBytes("Joy!peffpwpc"u8);
        pef.WriteUInt32(1u);                                                      // format version
        pef.WriteZeros(16);                                                       // date, old def, old imp, current version
        pef.WriteUInt16(0);                                                       // sections
        pef.WriteUInt16(0);                                                       // instantiated sections
        pef.WriteUInt32(0u);                                                      // reserved
        return pef.ToArray();
    }
}
