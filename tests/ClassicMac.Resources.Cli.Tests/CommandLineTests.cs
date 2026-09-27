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
        var first = json.RootElement.GetProperty("resources")[0];
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

    [Fact]
    public void Unbuilt_commands_say_so()
    {
        var (code, _, error) = Run("info", Path.GetTempFileName());
        Assert.Equal(ExitCodes.NotImplemented, code);
        Assert.Contains("not implemented", error);
    }
}
