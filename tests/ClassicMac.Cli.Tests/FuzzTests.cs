using System.Runtime.CompilerServices;
using ClassicMac.Core;
using Fuzz;

namespace ClassicMac.Cli.Tests;

// The fuzz targets (tools/Fuzz, PLAN "Fuzzing"): each reader's entry point, which may refuse damaged input as malformed
// and nothing else, and the seed corpora they start from.
public sealed class FuzzTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-fuzz-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static string Repository([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    public static TheoryData<string> Targets => [.. FuzzTargets.All.Keys];

    [Fact]
    public void Every_reader_has_a_target()
    {
        Assert.Equal(["code", "container", "first-aid", "pef", "pict", "resource", "resource-fork"], FuzzTargets.All.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Malformed_input_is_refused_quietly_and_anything_else_escapes()
    {
        FuzzTargets.Run(_ => throw new InvalidDataException(), default);
        FuzzTargets.Run(_ => throw new EndOfStreamException(), default);

        Assert.Throws<NullReferenceException>(() => FuzzTargets.Run(_ => throw new NullReferenceException(), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => FuzzTargets.Run(_ => throw new ArgumentOutOfRangeException(), default));
    }

    // The unwrapper and the image decoders turn an unexpected exception into a diagnostic; the fuzzer must see it as a crash.
    [Fact]
    public void A_reader_fault_is_a_crash()
    {
        List<Diagnostic> fault = [new(DiagnosticSeverity.Error, "container.reader-fault", "StuffIt: NullReferenceException")];

        FuzzTargets.ThrowOnFault([new(DiagnosticSeverity.Warning, "container.truncated", "cut off")]);
        var e = Assert.Throws<InvalidOperationException>(() => FuzzTargets.ThrowOnFault(fault));
        Assert.Equal("StuffIt: NullReferenceException", e.Message);
        Assert.Throws<InvalidOperationException>(() => FuzzTargets.ThrowOnFault([new(DiagnosticSeverity.Error, "image.decoder-fault", "PICT 128")]));
    }

    // The resource target's input: four bytes of type, then the data.
    [Fact]
    public void A_resource_input_starts_with_its_type()
    {
        var (type, data) = FuzzTargets.ResourceInput("STR \u0002hi"u8.ToArray()) ?? throw new InvalidOperationException();

        Assert.Equal(FourCC.FromString("STR "), type);
        Assert.Equal("\u0002hi"u8.ToArray(), data.ToArray());
        Assert.Null(FuzzTargets.ResourceInput("STR"u8.ToArray()));
        Assert.Equal("STR \u0002hi"u8.ToArray(), FuzzTargets.ResourceInput(FourCC.FromString("STR "), "\u0002hi"u8.ToArray()));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Empty_and_random_input_is_read_or_refused(string name)
    {
        var target = FuzzTargets.All[name];
        FuzzTargets.Run(target, ReadOnlyMemory<byte>.Empty);
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var input = new byte[random.Next(1, 2000)];
            random.NextBytes(input);
            FuzzTargets.Run(target, input);
        }
    }

    [Fact]
    public void The_seeds_cover_every_target_and_read_cleanly()
    {
        var counts = FuzzSeeds.Write(folder, Repository());

        foreach (var name in FuzzTargets.All.Keys)
        {
            var files = Directory.GetFiles(Path.Combine(folder, name));
            Assert.True(files.Length > 0, name);
            Assert.Equal(files.Length, counts[name]);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, FuzzSeeds.MaxLength));
            foreach (var file in files)
            {
                FuzzTargets.Run(FuzzTargets.All[name], File.ReadAllBytes(file));
            }
        }

        Assert.Equal(counts, FuzzSeeds.Write(folder, Repository()));                     // the same names: no duplicates
    }

    [Fact]
    public void Replay_runs_files_and_folders_and_names_a_crash()
    {
        Directory.CreateDirectory(Path.Combine(folder, "inputs"));
        File.WriteAllBytes(Path.Combine(folder, "inputs", "a"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "b"), [4]);
        var output = new StringWriter();

        Assert.Equal(0, FuzzTargets.Replay(_ => { }, [Path.Combine(folder, "inputs"), Path.Combine(folder, "b")], output));
        Assert.Equal(1, FuzzTargets.Replay(_ => throw new NullReferenceException("boom"), [Path.Combine(folder, "b")], output));
        Assert.Contains("b: System.NullReferenceException: boom", output.ToString(), StringComparison.Ordinal);
    }
}
