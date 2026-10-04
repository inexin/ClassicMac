using ClassicMac.Core;
using ClassicMac.Files.Hfs;
using ClassicMac.Tests;

namespace ClassicMac.Files.Tests;

// More damaged input (PLAN "Hostile input"), beside MutationTests: NDIF images with either fork damaged (the chunk map
// is in the resource fork), and First Aid's check and repair on damaged HFS and HFS Plus volumes. Damage is reported or
// refused as malformed, never anything else.
public sealed class DeepMutationTests
{
    private static void ReadAll(ContainerNode node)
    {
        _ = node.File.DataFork.ToArray();
        _ = node.File.ResourceFork.ToArray();
        foreach (var child in node.Children)
        {
            ReadAll(child);
        }
    }

    [Fact]
    public void Damaged_NDIF_images_are_reported_or_refused()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        var volume = disk.Build("Disk");
        var sectors = volume.Length / 512;
        var (data, resource) = NdifBuilder.Build(volume, "Disk",
            (sectors / 3, NdifBuilder.Kind.Raw), (sectors / 3, NdifBuilder.Kind.Adc), (sectors - 2 * (sectors / 3), NdifBuilder.Kind.KenCode));
        var failures = new List<string>();
        for (var seed = 0; seed < Mutations.PerInput(); seed++)
        {
            var random = new Random(seed);
            bool fork = seed % 2 == 0;
            var file = new MacFile
            {
                Name = MacString.FromMacRoman("Disk.img"),
                DataFork = ForkData.FromBytes(fork ? data : Mutations.Mutate(data, random)),
                ResourceFork = ForkData.FromBytes(fork ? Mutations.Mutate(resource, random) : resource),
                FinderInfo = FinderInfo.Empty with { Type = FourCC.FromString("rohd"), Creator = FourCC.FromString("ddsk") },
            };
            Mutations.Run($"NDIF (seed {seed})", () =>
            {
                var diagnostics = new List<Diagnostic>();
                ReadAll(ContainerUnwrapper.Default.Unwrap(file, "host file", new ContainerContext(null, diagnostics)));
                if (diagnostics.Find(d => d.Code == "container.reader-fault") is { } fault)
                {
                    throw new InvalidOperationException(fault.Message);
                }
            }, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }

    public static TheoryData<string> Volumes => ["HFS", "HFS Plus", "HFS Plus wrapped", "HFS Plus journaled"];

    private static byte[] Volume(string kind)
    {
        if (kind == "HFS")
        {
            return FirstAidImages.Base();
        }

        var builder = new HfsPlusBuilder { JournalBlocks = kind.EndsWith("journaled", StringComparison.Ordinal) ? 4 : 0 };
        uint docs = builder.Folder(HfsPlusBuilder.Root, "Docs");
        builder.File(docs, "Letter", "dear sir"u8.ToArray(), new byte[300]);
        builder.File(HfsPlusBuilder.Root, "Fragmented", new byte[10 * HfsPlusBuilder.Block], [], fragments: 10);
        builder.HardLinks("shared"u8.ToArray(), (HfsPlusBuilder.Root, "Link"));
        builder.Attribute(docs, "com.example.tag", "red"u8.ToArray());
        return kind.EndsWith("wrapped", StringComparison.Ordinal) ? builder.BuildWrapped("Plus") : builder.Build("Plus");
    }

    // Mutants keep their length (a cut-off volume is the reader's concern), so the structures inside are what is damaged.
    [Theory]
    [MemberData(nameof(Volumes))]
    public void First_Aid_checks_and_repairs_damaged_volumes_without_failing(string kind)
    {
        var volume = Volume(kind);
        var failures = new List<string>();
        for (var seed = 0; seed < Mutations.PerInput(60); seed++)
        {
            var random = new Random(seed);
            var mutant = volume.ToArray();
            // Damage where the structures are: the headers and the first blocks (bitmap, B-trees).
            for (var n = random.Next(1, 12); n > 0; n--)
            {
                mutant[random.Next(Math.Min(mutant.Length, 64 * 1024))] ^= (byte)random.Next(1, 256);
            }

            Mutations.Run($"{kind} (seed {seed})", () =>
            {
                _ = HfsFirstAid.Verify(ForkData.FromBytes(mutant));
                _ = HfsFirstAid.Repair(ForkData.FromBytes(mutant)).Volume;
            }, failures, TimeSpan.FromSeconds(30));
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(40)));
    }
}
