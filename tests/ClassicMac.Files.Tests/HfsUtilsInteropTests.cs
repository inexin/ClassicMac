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

    // What fsck.hfs -n (check only) finds wrong, one message each; null when the tools have no fsck.hfs.
    private HashSet<string>? Fsck(string image)
    {
        if (Run("sh", "-c", "command -v fsck.hfs").Code != 0)
        {
            return null;
        }

        var (code, output) = Run("fsck.hfs", "-n", "-f", image);
        var problems = output.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("**", StringComparison.Ordinal) && !l.StartsWith('(') &&
                        !l.StartsWith("Executing", StringComparison.Ordinal) && !l.StartsWith("The volume name", StringComparison.Ordinal))
            .ToHashSet();
        Assert.True(code == 0 || problems.Count > 0, output);
        return problems;
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
        Assert.Empty(Fsck("ours.img") ?? []);                                                  // Apple's fsck_hfs finds nothing
    }

    // Each kind of volume ClassicMac writes passes Apple's fsck_hfs with nothing found.
    [Fact]
    public void Formats_catalog_growth_resizing_and_item_edits_pass_fsck_hfs()
    {
        if (Tools is null)
        {
            Assert.Skip("Set CLASSICMAC_HFSUTILS to hfsutils' folder, or docker:<image>, to run this.");
        }

        var images = new Dictionary<string, byte[]>
        {
            ["format-400k.img"] = HfsWriter.Format(400 * 1024, "Small"),
            ["format-100m.img"] = HfsWriter.Format(100 * 1024 * 1024, "Large"),
        };

        // A catalog grown past its first clump into index levels, then most of it deleted in one pass.
        var many = HfsWriter.Format(4 * 1024 * 1024, "Many");
        many = HfsWriter.CreateFolder(ForkData.FromBytes(many), "Docs");
        for (var i = 0; i < 400; i++)
        {
            many = HfsWriter.CreateFile(ForkData.FromBytes(many), $"Docs:A file with a longer name {i:D3}", new byte[i % 7 * 300], Array.Empty<byte>(), FinderInfo.Empty);
        }

        images["catalog-grown.img"] = many;
        images["tree-deleted.img"] = HfsWriter.Delete(ForkData.FromBytes(many), "Docs", recursive: true);

        // Grown past its bitmap's sector (the allocation area moved up), then a file locked, renamed, moved and the
        // System Folder blessed.
        var grown = HfsWriter.Format(400 * 1024, "Grown");
        grown = HfsWriter.CreateFolder(ForkData.FromBytes(grown), "System Folder");
        grown = HfsWriter.CreateFile(ForkData.FromBytes(grown), "System Folder:System", new byte[2000], new byte[300],
            new FinderInfo { Type = FourCC.FromString("zsys"), Creator = FourCC.FromString("MACS") });
        grown = HfsWriter.Resize(ForkData.FromBytes(grown), 4 * 1024 * 1024);
        grown = HfsWriter.CreateFile(ForkData.FromBytes(grown), "Big", new byte[3 * 1024 * 1024], Array.Empty<byte>(), FinderInfo.Empty);
        grown = HfsWriter.Rename(ForkData.FromBytes(grown), "Big", "Bigger");
        grown = HfsWriter.SetLocked(ForkData.FromBytes(grown), "Bigger", true);
        grown = HfsWriter.Move(ForkData.FromBytes(grown), "Bigger", "System Folder");
        images["grown-edited.img"] = HfsWriter.Bless(ForkData.FromBytes(grown), "System Folder");

        foreach (var (name, bytes) in images)
        {
            File.WriteAllBytes(Host(name), bytes);
            if (Fsck(name) is { } problems)
            {
                Assert.True(problems.Count == 0, $"{name}: {string.Join("; ", problems)}");
            }
        }
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
        // hcopy's file threads draw "Reserved fields in the catalog record have incorrect data" from fsck_hfs already;
        // ClassicMac's edits must add nothing to what it finds.
        var before = Fsck("theirs.img");

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
        if (Fsck("edited.img") is { } after)
        {
            Assert.Subset(before!, after);
        }
    }
}
