using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;
using static ClassicMac.Resources.Decoders.Tests.CodeFixtures;

namespace ClassicMac.Cli.Tests;

// `disasm`: every Mac file's code inside the input as listings and code.json, one file straight into the output
// folder, several into a folder each.
public class DisasmTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-disasm-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    private static string[] Files(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void A_raw_fork_disassembles_into_the_output_folder()
    {
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, Application().ToArray());
        var target = Path.Combine(folder, "out");

        var (code, output, error) = Run("disasm", input, "-o", target);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Equal(["CODE-1 Main.s", "code.json"], Files(target));
        Assert.StartsWith("; 'CODE' 1 \"Main\": 68k segment, near header\n", File.ReadAllText(Path.Combine(target, "CODE-1 Main.s")), StringComparison.Ordinal);
        using var model = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(target, "code.json")));
        Assert.Equal("unknown", model.RootElement.GetProperty("application").GetProperty("model").GetString());
        Assert.Contains("1 listing from 1 file", output, StringComparison.Ordinal);

        // Again: refused unless overwriting.
        Assert.Equal(ExitCodes.IoError, Run("disasm", input, "-o", target).Code);
        Assert.Equal(ExitCodes.Success, Run("disasm", input, "-o", target, "--overwrite").Code);
    }

    [Fact]
    public void A_disk_gives_a_folder_per_file_with_code_and_the_data_fork_fragment()
    {
        var (fork, data) = FatApplication();
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Fat App", data, fork.ToArray(), type: "APPL", creator: "FATA");
        disk.File(HfsBuilder.Root, "Old App", [], Application().ToArray(), type: "APPL", creator: "OLDA");
        disk.File(HfsBuilder.Root, "Read Me", "text"u8.ToArray(), [], type: "TEXT", creator: "ttxt");
        var input = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(input, disk.Build("Disk"));
        var target = Path.Combine(folder, "out");

        var (code, output, error) = Run("disasm", input, "-o", target);

        Assert.True(code == ExitCodes.Success, error);
        Assert.Equal([
            "Fat App/CODE-1 Main.s", "Fat App/DRVR-12 .D.s", "Fat App/code.json", "Fat App/fragment-0 App.s", "Fat App/ncod-5.s",
            "Old App/CODE-1 Main.s", "Old App/code.json"], Files(target));
        Assert.Contains("InterfaceLib::InitGraf", File.ReadAllText(Path.Combine(target, "Fat App", "fragment-0 App.s")), StringComparison.Ordinal);
        Assert.Contains("5 listings from 2 files", output, StringComparison.Ordinal);

        var ppc = Path.Combine(folder, "ppc");
        Assert.Equal(ExitCodes.Success, Run("disasm", input, "-o", ppc, "--cpu", "ppc").Code);
        Assert.Equal(["code.json", "fragment-0 App.s", "ncod-5.s"], Files(ppc)); // one file has PowerPC code: no folder

        var m68k = Path.Combine(folder, "68k");
        Assert.Equal(ExitCodes.Success, Run("disasm", input, "-o", m68k, "--cpu", "68k").Code);
        Assert.Equal(["Fat App/CODE-1 Main.s", "Fat App/DRVR-12 .D.s", "Fat App/code.json", "Old App/CODE-1 Main.s", "Old App/code.json"], Files(m68k));
    }

    [Fact]
    public void Damaged_code_is_reported_and_fails_the_exit_code()
    {
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, Fork(Res("CODE", 1, [0x4E, 0x75])).ToArray());
        var target = Path.Combine(folder, "out");

        var (code, _, error) = Run("disasm", input, "-o", target);

        Assert.Equal(ExitCodes.Damaged, code);
        Assert.Contains("[m68k.segment-header]", error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(target, "CODE-1.s")));
    }

    [Fact]
    public void Warnings_fail_only_when_strict()
    {
        var fork = Application();
        fork.Add(Res("cfrg", 0, Cfrg(("Lib", Code.Ppc.CfrgWhere.Resource, 0, 0, 0x61626364, 1))));
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, fork.ToArray());

        var (code, _, error) = Run("disasm", input, "-o", Path.Combine(folder, "a"));
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("[code.fragment-missing]", error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Damaged, Run("disasm", input, "-o", Path.Combine(folder, "b"), "--strict").Code);
    }

    [Fact]
    public void A_file_without_code_writes_nothing()
    {
        var input = Path.Combine(folder, "Strings.rsrc");
        File.WriteAllBytes(input, Fork(Res("STR ", 128, [2, 104, 105])).ToArray());
        var target = Path.Combine(folder, "out");

        var (code, output, _) = Run("disasm", input, "-o", target);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("No code in Strings.rsrc.", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void An_input_that_is_no_fork_is_unreadable()
    {
        var input = Path.Combine(folder, "junk.bin");
        File.WriteAllBytes(input, new byte[300]);

        Assert.Equal(ExitCodes.Unreadable, Run("disasm", input, "-o", Path.Combine(folder, "out")).Code);
    }

    [Fact]
    public void The_cpu_must_be_one_of_the_three()
    {
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, Application().ToArray());

        Assert.Equal(ExitCodes.Usage, Run("disasm", input, "--cpu", "x86").Code);
    }

    [Fact]
    public void The_default_output_folder_is_beside_the_input()
    {
        var input = Path.Combine(folder, "App.rsrc");
        File.WriteAllBytes(input, Application().ToArray());

        Assert.Equal(ExitCodes.Success, Run("disasm", input).Code);

        Assert.True(File.Exists(Path.Combine(folder, "App code", "CODE-1 Main.s")));
    }
}
