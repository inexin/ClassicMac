using System.Diagnostics;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Files.Tests;

// ClassicMac's HFS writer checked against hfsutils (Robert Leslie's, GPL; run as a separate program, nothing taken from
// it) and, when present, hfsprogs' fsck.hfs (Apple's fsck_hfs): volumes ClassicMac makes and edits are read by them,
// and volumes hfsutils makes are read and edited by ClassicMac (docs/formats/file-systems/hfs.md §7).
//
// Gated by CLASSICMAC_HFSUTILS: a folder holding hformat, hmount, hls, hcopy, hmkdir (and optionally fsck.hfs), or
// "docker:<image>" to run them in a container with the work folder at /w (for example an image built
// FROM debian with `apt-get install -y hfsutils hfsprogs`).
public sealed class HfsUtilsInteropTests : IDisposable
{
    private static readonly string? Tools = Environment.GetEnvironmentVariable("CLASSICMAC_HFSUTILS");
    private readonly string work = Directory.CreateTempSubdirectory("cm-hfsutils").FullName;

    public void Dispose() => Directory.Delete(work, recursive: true);

    // Runs one of the tools in the work folder (HOME there too, where hmount keeps the current volume).
    private (int Code, string Output) Run(string tool, params string[] args)
    {
        ProcessStartInfo start;
        if (Tools!.StartsWith("docker:", StringComparison.Ordinal))
        {
            start = new ProcessStartInfo("docker") { ArgumentList = { "run", "--rm", "-v", $"{work}:/w", "-w", "/w", "-e", "HOME=/w", Tools["docker:".Length..], tool } };
        }
        else
        {
            start = new ProcessStartInfo(Path.Combine(Tools, tool)) { WorkingDirectory = work, Environment = { ["HOME"] = work } };
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error);
    }

    private void Ok(string tool, params string[] args)
    {
        var (code, output) = Run(tool, args);
        Assert.True(code == 0, $"{tool} {string.Join(' ', args)}: {output}");
    }

    // fsck.hfs -n (check only, no changes), when the tools have it.
    private void Fsck(string image)
    {
        if (Run("sh", "-c", "command -v fsck.hfs").Code == 0)
        {
            Ok("fsck.hfs", "-n", "-f", image);
        }
    }

    private string Host(string name) => Path.Combine(work, name);

    [Fact]
    public void A_volume_ClassicMac_formats_and_fills_is_read_by_hfsutils()
    {
        if (Tools is null)
        {
            Assert.Skip("Set CLASSICMAC_HFSUTILS to hfsutils' folder, or docker:<image>, to run this.");
        }

        var image = HfsWriter.Format(20 * 1024 * 1024, "Ours");
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "Docs");
        var note = Enumerable.Range(0, 5000).Select(i => (byte)('a' + i % 26)).ToArray();
        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Docs:Note", note, "rsrc"u8.ToArray(),
            new FinderInfo { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt") });
        for (var i = 0; i < 60; i++)
        {
            image = HfsWriter.CreateFile(ForkData.FromBytes(image), $"File {i:D2}", new byte[i * 100], Array.Empty<byte>(), FinderInfo.Empty);
        }

        image = HfsWriter.Delete(ForkData.FromBytes(image), "File 07", recursive: false);
        image = HfsWriter.Move(ForkData.FromBytes(image), "File 08", "Docs");
        File.WriteAllBytes(Host("ours.img"), image);

        Ok("hmount", "ours.img");
        var listing = Run("hls", "-a", ":Docs").Output;
        Assert.Contains("Note", listing);
        Assert.Contains("File 08", listing);
        Assert.DoesNotContain("File 07", Run("hls", "-a", ":").Output);
        Ok("hcopy", "-r", ":Docs:Note", "note.out");
        Assert.Equal(note, File.ReadAllBytes(Host("note.out")));
        Ok("humount");
        Fsck("ours.img");
    }

    [Fact]
    public void A_volume_hfsutils_formats_and_fills_is_read_and_edited_by_ClassicMac()
    {
        if (Tools is null)
        {
            Assert.Skip("Set CLASSICMAC_HFSUTILS to hfsutils' folder, or docker:<image>, to run this.");
        }

        File.WriteAllBytes(Host("theirs.img"), new byte[8 * 1024 * 1024]);
        File.WriteAllText(Host("in.txt"), "from hfsutils");
        Ok("hformat", "-l", "Theirs", "theirs.img");
        Ok("hmount", "theirs.img");
        Ok("hmkdir", ":Docs");
        Ok("hcopy", "-r", "in.txt", ":Docs:In");
        Ok("hcopy", "-r", "in.txt", ":Gone");
        Ok("humount");

        var image = File.ReadAllBytes(Host("theirs.img"));
        Assert.Null(HfsWriter.Check(ForkData.FromBytes(image)));
        var files = HfsReader.Instance.Read(ForkData.FromBytes(image), new ContainerContext());
        Assert.Equal("from hfsutils"u8.ToArray(), files.Single(f => f.MacPath == "Docs:In").DataFork.ToArray());

        image = HfsWriter.CreateFile(ForkData.FromBytes(image), "Docs:Ours", "from ClassicMac"u8.ToArray(), Array.Empty<byte>(), FinderInfo.Empty);
        image = HfsWriter.Rename(ForkData.FromBytes(image), "Docs:In", "Renamed");
        image = HfsWriter.Delete(ForkData.FromBytes(image), "Gone", recursive: false);
        image = HfsWriter.CreateFolder(ForkData.FromBytes(image), "New");
        image = HfsWriter.Move(ForkData.FromBytes(image), "Docs:Ours", "New");
        File.WriteAllBytes(Host("edited.img"), image);

        Ok("hmount", "edited.img");
        Assert.Contains("Renamed", Run("hls", "-a", ":Docs").Output);
        Assert.DoesNotContain("Gone", Run("hls", "-a", ":").Output);
        Ok("hcopy", "-r", ":New:Ours", "ours.out");
        Assert.Equal("from ClassicMac", File.ReadAllText(Host("ours.out")));
        Ok("humount");
        Fsck("edited.img");
    }
}
