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
    public void Limit_options_map_onto_ReadOptions()
    {
        var cli = new CommandLine(TextWriter.Null, TextWriter.Null);
        var result = cli.BuildRoot().Parse(["list", "x", "--max-resource-size", "1MiB", "--max-nesting-depth", "3"]);
        var options = cli.ReadOptionsFrom(result);

        Assert.Equal(1L << 20, options.MaxResourceSize);
        Assert.Equal(3, options.MaxNestingDepth);
        Assert.Equal(ReadOptions.Default.MaxExpandedBytesPerInput, options.MaxExpandedBytesPerInput);
    }

    [Fact]
    public void Defaults_come_from_ReadOptions()
    {
        var cli = new CommandLine(TextWriter.Null, TextWriter.Null);
        Assert.Equal(ReadOptions.Default, cli.ReadOptionsFrom(cli.BuildRoot().Parse(["list", "x"])));
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

    [Fact]
    public void Unbuilt_commands_say_so()
    {
        var (code, _, error) = Run("info", Path.GetTempFileName());
        Assert.Equal(ExitCodes.NotImplemented, code);
        Assert.Contains("not implemented", error);
    }
}
