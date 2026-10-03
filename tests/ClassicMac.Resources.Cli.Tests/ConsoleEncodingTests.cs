using System.Diagnostics;
using System.Text;

namespace ClassicMac.Resources.Cli.Tests;

// The tool's text and --json output is UTF-8 when stdout is redirected (docs/cli.md §2), whatever the console's code
// page: a pipe on Windows otherwise gets the OEM code page, and Mac Roman names such as "§" come out as other bytes.
public sealed class ConsoleEncodingTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-encoding").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static byte[] RunRedirected(params string[] args)
    {
        // On Windows the tool runs under a console set to code page 850, as a Western European console starts.
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "cmd.exe" : "dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        if (windows)
        {
            foreach (var arg in new[] { "/c", "chcp", "850", ">nul", "&&", "dotnet" })
            {
                start.ArgumentList.Add(arg);
            }
        }

        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "classicmac.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        process.StandardError.ReadToEnd();
        copy.Wait();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redirected_output_is_UTF8_without_a_byte_order_mark(bool json)
    {
        var file = Path.Combine(folder, "Café §.txt");
        File.WriteAllText(file, "hello");

        var bytes = RunRedirected(json ? ["stat", file, "--json"] : ["stat", file]);

        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble), "no byte order mark");
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Café §")) >= 0, Encoding.Latin1.GetString(bytes));
    }
}
