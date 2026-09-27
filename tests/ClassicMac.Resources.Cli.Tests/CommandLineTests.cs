using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli.Tests;

public class CommandLineTests
{
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("1024", 1024L)]
    [InlineData("64MiB", 64L << 20)]
    [InlineData("64 mib", 64L << 20)]
    [InlineData("2G", 2L << 30)]
    [InlineData("8KiB", 8L << 10)]
    public void Sizes_accept_bytes_and_binary_units(string text, long expected)
    {
        Assert.True(CommandLine.TryParseSize(text, out var size));
        Assert.Equal(expected, size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1.5MiB")]
    [InlineData("64MB")]
    [InlineData("9999999999GiB")]
    public void Sizes_reject_everything_else(string text)
    {
        Assert.False(CommandLine.TryParseSize(text, out _));
    }

    [Fact]
    public void Limit_options_map_onto_the_options_records()
    {
        var cli = new CommandLine(TextWriter.Null, TextWriter.Null);
        var result = cli.BuildRoot().Parse(["list", "x", "--max-resource-size", "1MiB", "--max-nesting-depth", "3"]);
        var options = cli.ReadOptionsFrom(result);
        var containerOptions = cli.ContainerOptionsFrom(result);

        Assert.Equal(1L << 20, options.MaxResourceSize);
        Assert.Equal(3, containerOptions.MaxNestingDepth);
        Assert.Equal(ContainerReadOptions.Default.MaxExpandedBytesPerInput, containerOptions.MaxExpandedBytesPerInput);
    }

    [Fact]
    public void Defaults_come_from_the_options_records()
    {
        var cli = new CommandLine(TextWriter.Null, TextWriter.Null);
        var result = cli.BuildRoot().Parse(["list", "x"]);
        Assert.Equal(ReadOptions.Default, cli.ReadOptionsFrom(result));
        Assert.Equal(ContainerReadOptions.Default, cli.ContainerOptionsFrom(result));
    }

    [Fact]
    public void Usage_errors_exit_with_2()
    {
        Assert.Equal(ExitCodes.Usage, Run("list").Code);
        Assert.Equal(ExitCodes.Usage, Run("frobnicate").Code);
        Assert.Equal(ExitCodes.Usage, Run("list", "does-not-exist.rsrc").Code);
        Assert.Equal(ExitCodes.Usage, Run("list", "--max-resource-size", "lots", Path.GetTempFileName()).Code);
    }

    [Fact]
    public void Extract_rejects_types_that_are_not_four_characters()
    {
        var (code, _, error) = Run("extract", Path.GetTempFileName(), "--type", "PIC");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("'PIC'", error);
    }

    private static string ForkFile(Action<byte[]>? damage = null)
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("PICT"), 128, new byte[] { 1, 2, 3 })
        {
            Name = new MacString("Title"u8),
            Attributes = ResourceAttributes.Purgeable | ResourceAttributes.Preload,
        });
        fork.Add(new Resource(FourCC.FromString("snd "), -4, new byte[10]));
        var bytes = fork.ToArray();
        damage?.Invoke(bytes);
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void List_prints_each_resource()
    {
        var (code, output, error) = Run("list", ForkFile());

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(error);
        Assert.Contains("'PICT'      128         3  Preload,Purgeable        \"Title\"", output);
        Assert.Contains("'snd '       -4        10", output);
        Assert.Contains("2 resources in 2 types", output);
    }

    [Fact]
    public void List_writes_json()
    {
        var (code, output, _) = Run("list", ForkFile(), "--format", "json");

        Assert.Equal(ExitCodes.Success, code);
        using var json = System.Text.Json.JsonDocument.Parse(output);
        var file = json.RootElement.GetProperty("files")[0];
        Assert.Equal("raw resource fork", file.GetProperty("formats")[0].GetString());
        var first = file.GetProperty("resources")[0];
        Assert.Equal("PICT", first.GetProperty("type").GetString());
        Assert.Equal(128, first.GetProperty("id").GetInt32());
        Assert.Equal("Title", first.GetProperty("name").GetString());
        Assert.Equal(3, first.GetProperty("size").GetInt32());
    }

    [Fact]
    public void List_reports_damage_on_stderr_and_exits_with_1()
    {
        // Point the first resource's data far outside the data area (its reference entry's 24-bit offset).
        var path = ForkFile(bytes =>
        {
            var map = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4));
            bytes[map + 28 + 2 + 16 + 5] = 0xFF;
        });
        var (code, output, error) = Run("list", path);

        Assert.Equal(ExitCodes.Damaged, code);
        Assert.Contains("resource.data-out-of-range", error);
        Assert.Contains("1 resources", output);
    }

    [Fact]
    public void List_rejects_input_that_is_not_a_fork()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a fork");
        Assert.Equal(ExitCodes.Unreadable, Run("list", path).Code);
    }

    // An AppleSingle (version 2) holding "Game Data" with Finder info and the test fork as its resource fork.
    private static string AppleSingleFile()
    {
        var fork = File.ReadAllBytes(ForkFile());
        byte[] name = "Game Data"u8.ToArray();
        byte[] finderInfo = [.. "scenRLMZ"u8, .. new byte[24]];
        (uint Id, byte[] Data)[] entries = [(3, name), (9, finderInfo), (2, fork)];
        var header = 26 + entries.Length * 12;
        var bytes = new List<byte>();
        void U32(uint v) => bytes.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U32(0x00051600);
        U32(0x00020000);
        bytes.AddRange(new byte[16]);
        bytes.AddRange([0, (byte)entries.Length]);
        var offset = (uint)header;
        foreach (var (id, data) in entries)
        {
            U32(id);
            U32(offset);
            U32((uint)data.Length);
            offset += (uint)data.Length;
        }
        foreach (var (_, data) in entries) bytes.AddRange(data);
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    [Fact]
    public void List_reads_through_containers()
    {
        var (code, output, error) = Run("list", AppleSingleFile());

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(error);
        Assert.Contains("\"Game Data\" (host file > AppleSingle)", output);
        Assert.Contains("\"Title\"", output);
        Assert.Contains("2 resources in 2 types", output);
    }

    [Fact]
    public void Info_shows_the_chain_and_Finder_info()
    {
        var (code, output, error) = Run("info", AppleSingleFile());

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(error);
        Assert.Contains("host file: ", output);
        Assert.Contains("  AppleSingle: \"Game Data\"", output);
        Assert.Contains("type 'scen'  creator 'RLMZ'", output);
        Assert.Contains("resource fork", output);
    }

    [Fact]
    public void Containers_without_a_resource_fork_say_so()
    {
        var path = Path.GetTempFileName();
        byte[] bytes = [0x00, 0x05, 0x16, 0x00, 0x00, 0x02, 0x00, 0x00, .. new byte[16], 0, 1, 0, 0, 0, 1, 0, 0, 0, 38, 0, 0, 0, 2, (byte)'h', (byte)'i'];
        File.WriteAllBytes(path, bytes);

        var (code, output, _) = Run("list", path);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("no resource fork", output);
    }

    [Fact]
    public void A_data_file_holding_a_resource_fork_is_listed()
    {
        // A Basilisk II folder with a Realmz-style data file whose data fork is a resource fork.
        var folder = Directory.CreateTempSubdirectory("classicmac-cli-").FullName;
        var path = Path.Combine(folder, "Scenario.rsf");
        File.Copy(ForkFile(), path);
        Directory.CreateDirectory(Path.Combine(folder, ".finf"));
        File.WriteAllBytes(Path.Combine(folder, ".finf", "Scenario.rsf"), [.. "BINA????"u8, .. new byte[24]]);

        var (code, output, _) = Run("list", path);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("(Basilisk II folder > data fork as resource fork)", output);
        Assert.Contains("2 resources in 2 types", output);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void Unbuilt_commands_say_so()
    {
        var (code, _, error) = Run("extract", Path.GetTempFileName());
        Assert.Equal(ExitCodes.NotImplemented, code);
        Assert.Contains("not implemented", error);
    }
}
