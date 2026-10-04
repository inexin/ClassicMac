using ClassicMac.Core;
using static ClassicMac.Files.Tests.Fixtures;

namespace ClassicMac.Files.Tests;

// Damaged input (PLAN "Hostile input"): the test inputs, each mutated in a few seeded ways (bytes flipped, a run zeroed,
// the end cut off), unwrapped as deep as they go with every fork read. A reader may report the damage or refuse the input
// with InvalidDataException or EndOfStreamException; any other exception, or a reader fault the unwrapper reports
// (container.reader-fault), is a bug.
public sealed class MutationTests
{
    // 100 each in the suite (about 7 s); CLASSICMAC_MUTANTS sets more for a deeper run (1,500 took 100 s).
    private static int MutantsPerInput => int.TryParse(Environment.GetEnvironmentVariable("CLASSICMAC_MUTANTS"), out var count) ? count : 100;

    private static IEnumerable<(string Name, byte[] Bytes)> Inputs()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "TestData");
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is > 0 and <= 4 * 1024 * 1024)
            {
                yield return (Path.GetRelativePath(folder, path), bytes);
            }
        }

        var volume = new HfsBuilder();
        var docs = volume.Folder(HfsBuilder.Root, "Docs");
        volume.File(docs, "Letter", "data"u8.ToArray(), [0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0], fragments: 2);
        volume.File(HfsBuilder.Root, "Read Me", "hello"u8.ToArray(), []);
        yield return ("HFS volume", volume.Build("Disk"));
        yield return ("MacBinary", MacBinary(2, "Inner", "data"u8.ToArray(), [1, 2]));
        yield return ("BinHex", System.Text.Encoding.ASCII.GetBytes(BinHex("Inner", "data"u8.ToArray(), [3, 4])));
    }

    private static byte[] Mutate(byte[] bytes, Random random)
    {
        var mutant = bytes.ToArray();
        switch (random.Next(3))
        {
            case 0:
                for (var n = random.Next(1, 9); n > 0; n--)
                {
                    mutant[random.Next(mutant.Length)] ^= (byte)random.Next(1, 256);
                }

                return mutant;
            case 1:
                int at = random.Next(mutant.Length), length = Math.Min(mutant.Length - at, random.Next(1, 64));
                mutant.AsSpan(at, length).Fill((byte)(random.Next(2) == 0 ? 0 : 0xFF));
                return mutant;
            default:
                return mutant[..random.Next(mutant.Length)];
        }
    }

    // Reads every fork of every file the tree holds, as far down as it goes.
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
    public void Damaged_inputs_are_reported_or_refused_as_malformed()
    {
        var failures = new List<string>();
        var index = 0;
        foreach (var (name, bytes) in Inputs())
        {
            for (var i = 0; i < MutantsPerInput; i++)
            {
                var seed = index * 1000 + i;
                var mutant = Mutate(bytes, new Random(seed));
                try
                {
                    var file = new MacFile { Name = MacString.FromMacRoman("input"), DataFork = ForkData.FromBytes(mutant) };
                    var diagnostics = new List<Diagnostic>();
                    ReadAll(ContainerUnwrapper.Default.Unwrap(file, "host file", new ContainerContext(null, diagnostics)));
                    // A reader fault the unwrapper caught is still a bug.
                    failures.AddRange(diagnostics.Where(d => d.Code == "container.reader-fault").Select(d => $"{name} (seed {seed}): {d.Message}"));
                }
                catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
                {
                }
                catch (Exception e)
                {
                    failures.Add($"{name} (seed {seed}): {e.GetType().Name}: {e.Message} at {e.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                }
            }

            index++;
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
