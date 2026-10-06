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
        Assert.Equal(["code", "container", "first-aid", "first-aid-repair", "hfs-edit", "ndif-write", "pef", "pict", "pict-write", "resource", "resource-fork", "tiff", "wav",
            "wrappers"], FuzzTargets.All.Keys.Order(StringComparer.Ordinal));
    }

    // A writer given any disk must make it: there, even a malformed-input refusal is a crash.
    [Fact]
    public void A_writer_s_refusal_is_a_crash()
    {
        var e = Assert.Throws<InvalidOperationException>(() => FuzzTargets.Run(FuzzTargets.Strict(_ => throw new InvalidDataException("no read-back")), default));
        Assert.IsType<InvalidDataException>(e.InnerException);
        FuzzTargets.Run(FuzzTargets.Strict(_ => { }), default);
    }

    // The NDIF writer target takes any input as a disk: whole sectors of it, nothing when it is shorter than one.
    [Fact]
    public void The_NDIF_writer_target_takes_whole_sectors_of_any_input()
    {
        var target = FuzzTargets.All["ndif-write"];
        var random = new Random(4);
        foreach (var length in new[] { 0, 100, 512, 1500, 4096 })
        {
            var input = new byte[length];
            random.NextBytes(input);
            target(input);                                                               // no refusal let through
        }
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

    // Seeds beyond the test files: HFS Plus volumes (plain and wrapped) for First Aid and the containers, PEF containers
    // with sections, and disks for the NDIF writer.
    [Fact]
    public void The_seeds_hold_HFS_Plus_volumes_and_PEF_sections()
    {
        FuzzSeeds.Write(folder, Repository());

        static bool HfsPlus(byte[] b, int at) => b.Length > at + 2 && b[at] == (byte)'H' && b[at + 1] == (byte)'+';
        var firstAid = Directory.GetFiles(Path.Combine(folder, "first-aid")).Select(File.ReadAllBytes).ToList();
        Assert.Contains(firstAid, b => HfsPlus(b, 1024));
        Assert.Contains(firstAid, b => b.Length > 1026 && b[1024] == (byte)'B' && b[1025] == (byte)'D' && b[1024 + 0x7C] == (byte)'H');   // wrapped
        Assert.Contains(Directory.GetFiles(Path.Combine(folder, "container")).Select(File.ReadAllBytes), b => HfsPlus(b, 1024));
        Assert.Contains(Directory.GetFiles(Path.Combine(folder, "pef")).Select(File.ReadAllBytes), b => b.Length > 0x28 && (b[0x20] << 8 | b[0x21]) > 0);
        Assert.True(Directory.GetFiles(Path.Combine(folder, "ndif-write")).Length >= 2);
    }

    // TIFF seeds in both byte orders and several compressions; WAV seeds of PCM and float samples.
    [Fact]
    public void The_seeds_hold_TIFFs_and_WAVs()
    {
        FuzzSeeds.Write(folder, Repository());

        var tiffs = Directory.GetFiles(Path.Combine(folder, "tiff")).Select(File.ReadAllBytes).ToList();
        Assert.Contains(tiffs, t => t[0] == (byte)'M');
        Assert.Contains(tiffs, t => t[0] == (byte)'I');
        Assert.All(tiffs, t => Assert.True(ClassicMac.Graphics.TiffFile.IsTiffFile(t)));
        Assert.True(tiffs.Count >= 5);
        var wavs = Directory.GetFiles(Path.Combine(folder, "wav")).Select(File.ReadAllBytes).ToList();
        Assert.True(wavs.Count >= 3);
        Assert.All(wavs, w => Assert.Equal("RIFF"u8.ToArray(), w[..4]));
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
