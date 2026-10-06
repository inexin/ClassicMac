using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Containers;
using ClassicMac.Files.Hfs;
using ClassicMac.Resources;
using ClassicMac.Resources.Editing;

namespace ClassicMac.Files.Editing;

/// <summary>Where an edited resource fork is saved back to.</summary>
public enum SaveTarget
{
    /// <summary>A plain file that is the fork itself (a <c>.rsrc</c> file, or a data file holding resources).</summary>
    RawFork,

    /// <summary>The AppleDouble header file (<c>._name</c>, or <c>name.rsrc</c>) beside the data file.</summary>
    AppleDoubleHeader,

    /// <summary>The Basilisk II / SheepShaver <c>.rsrc/name</c> file.</summary>
    BasiliskResourceFork,

    /// <summary>A MacBinary file, rewritten as MacBinary III.</summary>
    MacBinary,

    /// <summary>A BinHex 4.0 file.</summary>
    BinHex,

    /// <summary>An AppleSingle file.</summary>
    AppleSingle,
}

/// <summary>The forms <see cref="ForkSaver.SaveAs"/> writes.</summary>
public enum SaveAsFormat
{
    /// <summary>The resource fork alone.</summary>
    RawFork,

    /// <summary>The data fork as the file and an AppleDouble <c>._</c> header file.</summary>
    AppleDoublePair,

    /// <summary>An AppleSingle file.</summary>
    AppleSingle,

    /// <summary>MacBinary III.</summary>
    MacBinary,

    /// <summary>BinHex 4.0.</summary>
    BinHex,

    /// <summary>A Basilisk II / SheepShaver folder entry: the data file, <c>.rsrc/</c> and <c>.finf/</c>.</summary>
    BasiliskEntry,

    /// <summary>A copy of a plain HFS image with the selected file's edited fork replaced.</summary>
    HfsImage,
}

/// <summary>A file's size and time when it was read, to notice a change on disk before overwriting it.</summary>
public readonly record struct FileStamp(string Path, long Length, DateTime LastWriteUtc)
{
    /// <summary>The file as it is now (length −1 when it does not exist).</summary>
    public static FileStamp Of(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(path, info.Length, info.LastWriteTimeUtc) : new FileStamp(path, -1, default);
    }

    /// <summary>Whether the file differs from this stamp now.</summary>
    public bool HasChanged => Of(Path) != this;
}

/// <summary>
/// A save location: the host file written, how, the Mac file it holds, and whether its resources are in its resource
/// fork or its data fork.
/// </summary>
public sealed record SaveLocation(SaveTarget Target, string Path, MacFile File, bool ForkInDataFork, FileStamp Stamp);

/// <summary>A file changed on disk since it was opened; saving would lose that change.</summary>
public sealed class FileChangedException(string path)
    : IOException($"{path} has changed on disk since it was opened.")
{
    /// <summary>The file.</summary>
    public string FilePath { get; } = path;
}

/// <summary>A save that failed verification: the original is left as it was.</summary>
public sealed class SaveVerificationException(IReadOnlyList<string> differences)
    : IOException("The saved file did not read back the same: " + string.Join("; ", differences.Take(5)) + ".")
{
    /// <summary>What differed.</summary>
    public IReadOnlyList<string> Differences { get; } = differences;
}

/// <summary>An edited fork to write into an HFS image, by its Mac path (folders and name joined with ':').</summary>
public sealed record HfsForkReplacement(string MacPath, ResourceFork Fork, bool ForkInDataFork = false);

/// <summary>
/// Saves an edited resource fork back into the container it came from, or into a new one. A save writes beside the
/// original under a temporary name, reads it back with the same reader and compares it with what was meant (the fork's
/// content, and the name, Finder info and data fork where the container has them), and only then replaces the
/// original. The first save keeps the original as <c>&lt;name&gt;.orig</c> unless one exists. Only the resource fork
/// changes; everything else is written back as read [ClassicMac].
/// </summary>
public static class ForkSaver
{
    /// <summary>
    /// Where a file's edited resources can be saved back to, or null when they cannot (a file inside a disk image or
    /// archive, a PC Exchange folder, a macOS named fork): then only <see cref="SaveAs"/>.
    /// </summary>
    /// <param name="inputPath">The host file opened.</param>
    /// <param name="host">How it was read.</param>
    /// <param name="root">The container tree of the input.</param>
    /// <param name="file">The Mac file whose resources were edited: the input itself, or the one file a MacBinary, BinHex or AppleSingle input holds.</param>
    /// <param name="forkInDataFork">Whether its resources were read from its data fork.</param>
    public static SaveLocation? Locate(string inputPath, HostFile host, ContainerNode root, ContainerNode file, bool forkInDataFork)
    {
        ArgumentNullException.ThrowIfNull(inputPath);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(file);
        var full = System.IO.Path.GetFullPath(inputPath);
        if (file == root)
        {
            if (forkInDataFork)
            {
                return host.Layout == HostLayout.Plain ? Location(SaveTarget.RawFork, full, file.File, true) : null;
            }

            return host.Layout switch
            {
                HostLayout.AppleDouble => Location(SaveTarget.AppleDoubleHeader, host.Companions[0], file.File, false),
                HostLayout.BasiliskII => Location(SaveTarget.BasiliskResourceFork,
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, ".rsrc", System.IO.Path.GetFileName(full)), file.File, false),
                _ => null,
            };
        }
        if (host.Layout != HostLayout.Plain || root.Children.Count != 1 || root.Children[0] != file || file.Children.Count > 0)
        {
            return null;
        }

        SaveTarget? target = file.Format switch
        {
            var f when f.StartsWith("MacBinary", StringComparison.Ordinal) => SaveTarget.MacBinary,
            var f when f.StartsWith("BinHex", StringComparison.Ordinal) => SaveTarget.BinHex,
            "AppleSingle" => SaveTarget.AppleSingle,
            _ => null,
        };
        return target is { } t ? Location(t, full, file.File, forkInDataFork) : null;
    }

    private static SaveLocation Location(SaveTarget target, string path, MacFile file, bool inData) =>
        new(target, path, file, inData, FileStamp.Of(path));

    /// <summary>
    /// Saves <paramref name="fork"/> to <paramref name="location"/>, verified, keeping the original as <c>.orig</c> on
    /// the first save. Returns the new location (with the new stamp) for later saves.
    /// </summary>
    /// <exception cref="FileChangedException">The file changed on disk since it was read, and <paramref name="overwriteChanged"/> is false.</exception>
    /// <exception cref="SaveVerificationException">The written file did not read back the same; nothing was replaced.</exception>
    public static SaveLocation Save(SaveLocation location, ResourceFork fork, bool overwriteChanged = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(fork);
        if (!overwriteChanged && location.Stamp.HasChanged)
        {
            throw new FileChangedException(location.Path);
        }

        var file = WithFork(location.File, fork, location.ForkInDataFork);
        var bytes = Serialize(location.Target, file, fork);
        var directory = System.IO.Path.GetDirectoryName(location.Path)!;
        Directory.CreateDirectory(directory);
        var temp = System.IO.Path.Combine(directory, $"{System.IO.Path.GetFileName(location.Path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, bytes);
        try
        {
            var differences = Verify(location.Target, temp, file, fork, location.ForkInDataFork);
            if (differences.Count > 0)
            {
                throw new SaveVerificationException(differences);
            }

            if (File.Exists(location.Path))
            {
                var backup = location.Path + ".orig";
                if (!File.Exists(backup))
                {
                    File.Copy(location.Path, backup);
                }

                ForkData.CloseHostFile(temp);
                ForkData.CloseHostFile(location.Path);
                File.Replace(temp, location.Path, null);
            }
            else
            {
                ForkData.CloseHostFile(temp);
                File.Move(temp, location.Path);
            }
        }
        finally
        {
            ForkData.CloseHostFile(temp);
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        return location with { File = file, Stamp = FileStamp.Of(location.Path) };
    }

    /// <summary>
    /// Writes <paramref name="file"/> with <paramref name="fork"/> as its resources to <paramref name="path"/> in a
    /// new form, verified. Returns the paths written. Existing files are overwritten.
    /// </summary>
    public static IReadOnlyList<string> SaveAs(string path, SaveAsFormat format, MacFile file, ResourceFork fork, bool forkInDataFork = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fork);
        var full = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(full)!;
        var edited = WithFork(file, fork, forkInDataFork);
        if (format is SaveAsFormat.AppleDoublePair or SaveAsFormat.BasiliskEntry)
        {
            var layout = format == SaveAsFormat.AppleDoublePair ? HostLayout.AppleDouble : HostLayout.BasiliskII;
            var paths = HostFiles.Write(edited, directory, new HostWriteOptions { Layout = layout, Overwrite = true },
                System.IO.Path.GetFileName(full));
            var back = HostFiles.Read(full);
            var differences = Compare(edited, back.File, fork, forkInDataFork, name: false);
            if (differences.Count > 0)
            {
                throw new SaveVerificationException(differences);
            }

            return paths;
        }
        var target = format switch
        {
            SaveAsFormat.RawFork => SaveTarget.RawFork,
            SaveAsFormat.AppleSingle => SaveTarget.AppleSingle,
            SaveAsFormat.MacBinary => SaveTarget.MacBinary,
            SaveAsFormat.BinHex => SaveTarget.BinHex,
            SaveAsFormat.HfsImage => throw new ArgumentException("Use SaveHfsImageAs for an HFS volume image.", nameof(format)),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(full, Serialize(target, edited, fork));
        var found = Verify(target, full, edited, fork, format == SaveAsFormat.RawFork || forkInDataFork);
        if (found.Count > 0)
        {
            throw new SaveVerificationException(found);
        }

        return [full];
    }

    /// <summary>
    /// Writes an edited fork into a copy of a plain HFS image and atomically places the verified image at
    /// <paramref name="destinationPath"/>. The destination cannot be the source image.
    /// </summary>
    /// <returns>The full path of the saved HFS image.</returns>
    public static string SaveHfsImageAs(string sourcePath, string destinationPath, MacFile file, ResourceFork fork,
        bool forkInDataFork = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(fork);
        return SaveHfsImageAs(sourcePath, destinationPath, null, [new HfsForkReplacement(file.MacPath, fork, forkInDataFork)]);
    }

    /// <summary>
    /// Writes a copy of a plain HFS image: <paramref name="volume"/> (the source image with files and folders already
    /// created or deleted by <see cref="HfsWriter"/>), or the source image itself when null, with each of
    /// <paramref name="forks"/> replaced. The image is read back and every replaced fork compared before it is
    /// atomically placed at <paramref name="destinationPath"/>, which cannot be the source image. With
    /// <paramref name="region"/>, the volume is that range of the source (an HFS partition of a partitioned disk, a Disk
    /// Copy 4.2 image's disk): it is put back there, the rest of the source copied unchanged, and a Disk Copy 4.2
    /// image's data checksum made again.
    /// </summary>
    /// <returns>The full path of the saved HFS image.</returns>
    public static string SaveHfsImageAs(string sourcePath, string destinationPath, byte[]? volume,
        IReadOnlyList<HfsForkReplacement> forks, HfsImageRegion? region = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(forks);
        var source = System.IO.Path.GetFullPath(sourcePath);
        var destination = System.IO.Path.GetFullPath(destinationPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, destination, comparison))
        {
            throw new InvalidOperationException("Save As cannot overwrite the source HFS image.");
        }

        var directory = System.IO.Path.GetDirectoryName(destination)!;
        var temporary = System.IO.Path.Combine(directory, $".classicmac-{Guid.NewGuid():N}.tmp");
        try
        {
            var whole = region is null ? null : File.ReadAllBytes(source);
            var image = ApplyHfsForks(volume ?? (region is { } range ? whole.AsSpan((int)range.Offset, (int)range.Length).ToArray() : File.ReadAllBytes(source)), forks);
            if (region is { } at)
            {
                if (image.Length != at.Length)
                {
                    throw new InvalidOperationException("The edited partition changed size.");
                }

                image.CopyTo(whole.AsSpan((int)at.Offset));
                if (at.DiskCopy42)
                {
                    new BigEndianWriter(whole!).WriteUInt32At(0x48, DiskCopy42Reader.Sum(image));   // the data checksum
                }

                image = whole!;
            }

            File.WriteAllBytes(temporary, image);
            ForkData.CloseHostFile(destination);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// A copy of an HFS volume image with each of <paramref name="forks"/> replaced, read back and compared.
    /// </summary>
    public static byte[] ApplyHfsForks(byte[] volume, IReadOnlyList<HfsForkReplacement> forks)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return ApplyHfsForks(new HfsVolume(ForkData.FromBytes(volume)), forks).ToArray();
    }

    // The volume with each fork replaced (a fork of it: the volume itself is left as it was), read back and compared;
    // with evenIfLocked, a locked file's too (an edit session's edits, made before the file was locked).
    internal static HfsVolume ApplyHfsForks(HfsVolume volume, IReadOnlyList<HfsForkReplacement> forks, bool evenIfLocked = false)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(forks);
        var image = volume;
        var written = new List<(string MacPath, HfsFork Kind, byte[] Data)>();
        foreach (var replacement in forks)
        {
            var kind = replacement.ForkInDataFork ? HfsFork.Data : HfsFork.Resource;
            var data = replacement.Fork.ToArray();
            image = HfsWriter.ReplaceFork(image, replacement.MacPath, kind, data, evenIfLocked);
            written.Add((replacement.MacPath, kind, data));
        }

        VerifyHfsForks(image.AsForkData(), written);
        return image;
    }

    // Reads the written volume back and compares each replaced fork (HfsWriter checks the volume's own structures).
    private static void VerifyHfsForks(ForkData image, List<(string MacPath, HfsFork Kind, byte[] Data)> written)
    {
        if (written.Count == 0)
        {
            return;
        }

        var files = HfsReader.Instance.Read(image, new ContainerContext());
        var found = new List<string>();
        foreach (var (macPath, kind, data) in written)
        {
            var file = files.FirstOrDefault(f => f.MacPath == macPath);
            var fork = kind == HfsFork.Data ? file?.DataFork : file?.ResourceFork;
            if (fork is null)
            {
                found.Add($"{macPath} is missing");
            }
            else if (!fork.ToArray().AsSpan().SequenceEqual(data))
            {
                found.Add($"{macPath}'s {(kind == HfsFork.Data ? "data" : "resource")} fork differs");
            }
        }
        if (found.Count > 0)
        {
            throw new SaveVerificationException(found);
        }
    }

    private static MacFile WithFork(MacFile file, ResourceFork fork, bool inData)
    {
        var bytes = ForkData.FromBytes(fork.ToArray());
        return inData ? file with { DataFork = bytes } : file with { ResourceFork = bytes };
    }

    private static byte[] Serialize(SaveTarget target, MacFile file, ResourceFork fork)
    {
        using var output = new MemoryStream();
        switch (target)
        {
            case SaveTarget.RawFork:
                return fork.ToArray();
            case SaveTarget.BasiliskResourceFork:
                return file.ResourceFork.ToArray();
            case SaveTarget.AppleDoubleHeader:
                AppleDoubleWriter.Write(file, output);
                break;
            case SaveTarget.AppleSingle:
                AppleDoubleWriter.WriteAppleSingle(file, output);
                break;
            case SaveTarget.MacBinary:
                MacBinaryWriter.Write(file, output);
                break;
            case SaveTarget.BinHex:
                BinHexWriter.Write(file, output);
                break;
        }
        return output.ToArray();
    }

    // Reads the written file back with the reader that reads that container, and compares.
    private static IReadOnlyList<string> Verify(SaveTarget target, string path, MacFile expected, ResourceFork fork, bool inData)
    {
        var input = ForkData.FromFile(path);
        var context = new ContainerContext(hostName: expected.Name);
        try
        {
            switch (target)
            {
                case SaveTarget.RawFork:
                case SaveTarget.BasiliskResourceFork:
                    return ResourceForkComparison.Differences(fork, ResourceFork.Read(input.ToArray()));
                case SaveTarget.AppleDoubleHeader:
                    {
                        var back = AppleSingleReader.AppleDouble.Read(input, context)[0] with { DataFork = expected.DataFork };
                        return Compare(expected, back, fork, inData, name: true);
                    }
                default:
                    {
                        IContainerReader reader = target switch
                        {
                            SaveTarget.MacBinary => MacBinaryReader.III,
                            SaveTarget.BinHex => BinHexReader.Instance,
                            _ => AppleSingleReader.AppleSingle,
                        };
                        if (!reader.CanRead(input))
                        {
                            return [$"the written file is not {reader.FormatName}"];
                        }

                        return Compare(expected, reader.Read(input, context)[0], fork, inData, name: true);
                    }
            }
        }
        catch (InvalidDataException e)
        {
            return [$"it cannot be read back: {e.Message}"];
        }
    }

    private static List<string> Compare(MacFile expected, MacFile actual, ResourceFork fork, bool inData, bool name)
    {
        var differences = new List<string>();
        if (name && expected.Name != actual.Name)
        {
            differences.Add($"the name is {actual.Name}, not {expected.Name}");
        }

        if (!expected.FinderInfo.ToArray().AsSpan().SequenceEqual(actual.FinderInfo.ToArray()))
        {
            differences.Add("the Finder info differs");
        }

        var other = inData ? (expected.ResourceFork, actual.ResourceFork) : (expected.DataFork, actual.DataFork);
        if (!other.Item1.ToArray().AsSpan().SequenceEqual(other.Item2.ToArray()))
        {
            differences.Add(inData ? "the resource fork differs" : "the data fork differs");
        }

        var written = inData ? actual.DataFork : actual.ResourceFork;
        differences.AddRange(ResourceForkComparison.Differences(fork, ResourceFork.Read(written.ToArray())));
        return differences;
    }
}

/// <summary>
/// Where an HFS volume lies in an image that holds more than the volume: its offset and length, and whether it is a Disk
/// Copy 4.2 image's disk, whose data checksum is made again when it is saved.
/// </summary>
public sealed record HfsImageRegion(long Offset, long Length, bool DiskCopy42 = false);
