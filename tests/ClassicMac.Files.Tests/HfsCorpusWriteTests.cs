using System.Diagnostics;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;
using ClassicMac.Tests;

namespace ClassicMac.Files.Tests;

// The writer on volumes Mac OS wrote (docs/formats/file-systems/hfs.md §7): copies of the real images under
// CLASSICMAC_CORPUS (plain, partitioned, Disk Copy 4.2 and NDIF) are edited the way people edit them, saved, and must
// pass the writer's checks and read back with every change; with CLASSICMAC_HFSUTILS set, hfsprogs' fsck.hfs must find
// nothing the source did not have (it flags an extent using a volume's last allocation block, which Mac OS 9 itself
// allocates; hfs.md §7). Images are never changed and never committed.
public sealed class HfsCorpusWriteTests : IDisposable
{
    private const long MaxImageBytes = 1L << 30;
    private readonly string work = Directory.CreateTempSubdirectory("cm-corpus-write").FullName;

    public void Dispose() => Directory.Delete(work, recursive: true);

    // The images the edit session writes, left out when damaged on purpose, too large, or failing the writer's checks as
    // they are (check reports those; there is nothing to edit).
    private static IEnumerable<string> Writable()
    {
        foreach (var path in CorpusFolders.EnumerateFiles("*", SearchOption.AllDirectories)
                     .Where(p => !CorpusFolders.IsDamageTest(p) && new FileInfo(p).Length is > 0 and <= MaxImageBytes))
        {
            InputEditSession session;
            try
            {
                session = InputEditSession.Open(path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }

            if (session.Kind == InputEditKind.HfsVolume && HfsWriter.Check(ForkData.FromBytes(session.Volume)) is null)
            {
                yield return path;
            }
        }
    }

    [Fact]
    public void Volumes_Mac_OS_wrote_take_edits_and_stay_sound()
    {
        if (!CorpusFolders.Any)
        {
            Assert.Skip("Set CLASSICMAC_CORPUS to a folder of Mac disk images (or one image) to run this.");
        }

        var images = Writable().ToList();
        Assert.NotEmpty(images);
        foreach (var path in images)
        {
            var name = Path.GetFileName(path);
            var session = InputEditSession.Open(path);
            var before = HfsReader.Instance.Read(ForkData.FromBytes(session.Volume), new ContainerContext());
            var problemsBefore = Fsck(session.Volume);

            // A folder, a file with both forks in it, renamed and moved to the top level; one of the volume's own files
            // (unlocked, at the top level or one folder down) renamed, and another deleted.
            session.AddFolder("ClassicMac Test");
            session.AddFile("ClassicMac Test:Note", new MacFile
            {
                Name = MacString.FromMacRoman("Note"),
                DataFork = ForkData.FromBytes(Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray()),
                ResourceFork = ForkData.FromBytes(new byte[300]),
                FinderInfo = new FinderInfo { Type = FourCC.FromString("TEXT"), Creator = FourCC.FromString("ttxt") },
            });
            session.Rename("ClassicMac Test:Note", "Renamed Note");
            session.Move("ClassicMac Test:Renamed Note", "");
            var own = before.Where(f => !f.IsLocked && f.FolderPath.Count <= 1 && f.Name.ToMacRoman().Length > 0 && !f.Name.ToMacRoman().Contains(':'))
                .Select(f => string.Join(":", f.FolderPath.Select(n => n.ToMacRoman()).Append(f.Name.ToMacRoman()))).ToList();
            string? renamed = null, deleted = null;
            if (own.Count >= 2)
            {
                session.Rename(own[0], "Renamed by ClassicMac");
                renamed = own[0];
                deleted = own[^1];
                session.Delete(deleted);
            }

            var output = Path.Combine(work, name);
            var written = session.SaveAs(output);

            var saved = InputEditSession.Open(written[0]);
            Assert.True(saved.Kind == InputEditKind.HfsVolume, name);
            var volume = saved.Volume;
            Assert.True(HfsWriter.Check(ForkData.FromBytes(volume)) is null, $"{name}: {HfsWriter.Check(ForkData.FromBytes(volume))}");
            var after = HfsReader.Instance.Read(ForkData.FromBytes(volume), new ContainerContext());
            var paths = after.Select(f => string.Join(":", f.FolderPath.Select(n => n.ToMacRoman()).Append(f.Name.ToMacRoman()))).ToHashSet();
            Assert.Contains("Renamed Note", paths);
            Assert.Equal(70_000, after.Single(f => f.FolderPath.Count == 0 && f.Name.ToMacRoman() == "Renamed Note").DataFork.Length);
            if (renamed is not null)
            {
                Assert.DoesNotContain(renamed, paths);
                Assert.DoesNotContain(deleted!, paths);
            }

            Assert.Equal(before.Count + 1 - (deleted is null ? 0 : 1), after.Count);
            if (Fsck(volume) is { } problemsAfter)
            {
                Assert.True(problemsAfter.IsSubsetOf(problemsBefore!),
                    $"{name}: fsck.hfs finds more after the edits: {string.Join("; ", problemsAfter.Except(problemsBefore!))}");
            }

            File.Delete(written[0]);
        }
    }

    // What fsck.hfs -n finds wrong in a volume, one message each (file IDs and record numbers left out); null without
    // CLASSICMAC_HFSUTILS (a folder or docker:<image>).
    private HashSet<string>? Fsck(byte[] volume)
    {
        if (Environment.GetEnvironmentVariable("CLASSICMAC_HFSUTILS") is not { } tools)
        {
            return null;
        }

        var image = Path.Combine(work, "fsck.img");
        File.WriteAllBytes(image, volume);
        var start = tools.StartsWith("docker:", StringComparison.Ordinal)
            ? new ProcessStartInfo("docker") { ArgumentList = { "run", "--rm", "-v", $"{work}:/w", "-w", "/w", tools["docker:".Length..], "fsck.hfs", "-n", "-f", "fsck.img" } }
            : new ProcessStartInfo(Path.Combine(tools, "fsck.hfs")) { ArgumentList = { "-n", "-f", image } };
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        File.Delete(image);
        return output.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("**", StringComparison.Ordinal) && !l.StartsWith('(') &&
                        !l.StartsWith("Executing", StringComparison.Ordinal) && !l.StartsWith("The volume name", StringComparison.Ordinal))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"id=\d+", "id=n"))
            .ToHashSet();
    }
}
