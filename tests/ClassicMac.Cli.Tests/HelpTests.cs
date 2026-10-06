namespace ClassicMac.Cli.Tests;

// classicmac help [command …] and the examples every command's help ends with (docs/cli.md §6).
public class HelpTests
{
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = new CommandLine(output, error).Run(args);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Help_alone_is_the_root_help()
    {
        var (code, output, error) = Run("help");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(error);
        Assert.Equal(Run("--help").Output, output);
        Assert.Contains("extract <input>", output, StringComparison.Ordinal);
        Assert.Contains("help <command>", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("res-add")]
    [InlineData("shell")]
    [InlineData("help")]
    public void Help_with_a_command_is_that_commands_help(string name)
    {
        var (code, output, error) = Run("help", name);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(error);
        Assert.Equal(Run(name, "--help").Output, output);
        Assert.Contains($"classicmac {name}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_with_an_unknown_command_is_a_usage_error()
    {
        var (code, output, error) = Run("help", "nope");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
        Assert.Equal("nope: no such command (classicmac help lists them)." + Environment.NewLine, error);
    }

    [Fact]
    public void Help_takes_one_command()
    {
        var (code, output, _) = Run("help", "extract", "list");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(output);
    }

    [Fact]
    public void Every_command_and_the_root_end_their_help_with_examples()
    {
        var root = new CommandLine(TextWriter.Null, TextWriter.Null).BuildRoot();
        var names = root.Subcommands.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        foreach (var name in names.Append(""))
        {
            var (code, output, _) = name.Length == 0 ? Run("--help") : Run(name, "--help");
            Assert.Equal(ExitCodes.Success, code);
            var at = output.IndexOf("Examples:", StringComparison.Ordinal);
            Assert.True(at > 0, $"{name}: no examples");
            var examples = output[at..].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            Assert.NotEmpty(examples);
            var lines = examples.Select(l => l.Trim()).Where(l => l.StartsWith("classicmac", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(lines);
            Assert.All(lines, l => Assert.StartsWith(name.Length == 0 ? "classicmac " : $"classicmac {name} ", l + " ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_example_parses()
    {
        var cli = new CommandLine(TextWriter.Null, TextWriter.Null);
        foreach (var (name, examples) in HelpExamples.All)
        {
            foreach (var example in examples)
            {
                var args = HelpExamples.Words(example.CommandLine).Skip(1).ToArray();
                var result = cli.BuildRoot().Parse(args);
                // A missing input file is the only error an example may have here.
                Assert.True(result.Errors.All(e => e.Message.Contains("does not exist", StringComparison.Ordinal)),
                    $"{example.CommandLine}: {string.Join("; ", result.Errors.Select(e => e.Message))}");
                if (name.Length > 0)
                {
                    Assert.Equal(name, result.CommandResult.Command.Name);
                }
            }
        }
    }
}
