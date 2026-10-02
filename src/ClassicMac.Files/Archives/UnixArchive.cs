using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files.Archives;

/// <summary>One entry of a zip or tar archive before Mac data is paired with it.</summary>
internal sealed class UnixArchiveEntry
{
    /// <summary>The path's components, with empty, "." and ".." components removed.</summary>
    public required string[] Path { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>The entry holds a resource fork (Info-ZIP's Macintosh extra fields), not a data fork.</summary>
    public bool IsResourceFork { get; init; }

    public ForkData Data { get; init; } = ForkData.Empty;
    public ForkData ResourceFork { get; init; } = ForkData.Empty;
    public FinderInfo? FinderInfo { get; init; }
    public MacDate? Created { get; init; }
    public MacDate? Modified { get; init; }
    public string? SymbolicLinkTarget { get; init; }
}

/// <summary>
/// What zip and tar share: Unix-style paths, Unix times, and the AppleDouble <c>._</c> files (beside the file or under
/// <c>__MACOSX/</c>) that carry each file's resource fork and Finder info.
/// </summary>
internal static class UnixArchive
{
    private const string MacOsXFolder = "__MACOSX";
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Splits a path on '/', dropping empty, "." and ".." components (reported).</summary>
    public static string[] SplitPath(string path, ContainerContext context, string format, long offset)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                context.Report(DiagnosticSeverity.Warning, "archive.path-unsafe",
                    $"The {format} entry \"{path}\" climbs out of its folder with \"..\"; that component is dropped.", offset);
                continue;
            }
            parts.Add(part);
        }
        return [.. parts];
    }

    /// <summary>A Unix time (UTC seconds) as a Mac date in the reading Mac's zone; null outside 1904–2040.</summary>
    public static MacDate? FromUnix(long seconds, ContainerContext context) =>
        seconds is < -62135596800L or > 253402300799L ? null : FromUtc(UnixEpoch.AddSeconds(seconds), context);

    /// <summary>A UTC time as a Mac date in the reading Mac's zone; null outside 1904–2040.</summary>
    public static MacDate? FromUtc(DateTime utc, ContainerContext context)
    {
        try
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), context.Options.TimeZone);
            return MacDate.FromDateTime(local);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A local DOS date and time (zip's fields) as a Mac date; null if the fields are not a real date.</summary>
    public static MacDate? FromDos(ushort date, ushort time)
    {
        int year = (date >> 9) + 1980, month = (date >> 5) & 15, day = date & 31;
        int hour = time >> 11, minute = (time >> 5) & 63, second = (time & 31) * 2;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        try
        {
            return MacDate.FromDateTime(new DateTime(year, month, day, hour, minute, second));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A Unicode name in Mac Roman, characters Mac Roman lacks becoming '?'.</summary>
    public static MacString ToMacString(string name)
    {
        if (MacRoman.TryEncode(name, out var bytes))
        {
            return new MacString(bytes);
        }

        var result = new List<byte>(name.Length);
        foreach (var c in name.Normalize(System.Text.NormalizationForm.FormC))
        {
            result.Add(MacRoman.TryGetByte(c, out var b) ? b : (byte)'?');
        }

        return new MacString(result.ToArray());
    }

    /// <summary>
    /// Turns the entries into Mac files: data entries become files; Info-ZIP resource-fork entries and AppleDouble
    /// <c>._name</c> entries (beside the file or in the same folder under <c>__MACOSX/</c>) give the file of the same
    /// path its resource fork, Finder info and dates. A <c>._</c> entry with no file becomes a file with only a resource
    /// fork; one for a folder is dropped. Folders appear only as the folder paths of their files.
    /// </summary>
    public static List<MacFile> ToMacFiles(IReadOnlyList<UnixArchiveEntry> entries, ContainerContext context, string format)
    {
        var files = new List<UnixArchiveEntry>();
        var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var forks = new List<UnixArchiveEntry>();
        var doubles = new List<(string[] Target, UnixArchiveEntry Entry)>();

        foreach (var entry in entries)
        {
            if (entry.Path.Length == 0)
            {
                continue;
            }

            for (var i = 1; i < entry.Path.Length; i++)
            {
                folders.Add(Key(entry.Path[..i]));
            }

            if (entry.IsDirectory)
            {
                folders.Add(Key(entry.Path));
                continue;
            }
            if (entry.IsResourceFork)
            {
                forks.Add(entry);
                continue;
            }
            var name = entry.Path[^1];
            if (name.Length > 2 && name.StartsWith("._", StringComparison.Ordinal) && entry.SymbolicLinkTarget is null)
            {
                var folder = entry.Path[..^1];
                if (folder.Length > 0 && folder[0] == MacOsXFolder)
                {
                    folder = folder[1..];
                }

                doubles.Add(([.. folder, name[2..]], entry));
                continue;
            }
            Add(entry);
        }
        // The __MACOSX folder itself is not a folder of the archive's files.
        folders.RemoveWhere(f => f == MacOsXFolder || f.StartsWith(MacOsXFolder + "/", StringComparison.Ordinal));

        foreach (var fork in forks)
        {
            var file = Find(fork.Path) ?? AddEmpty(fork.Path);
            files[byPath[Key(fork.Path)]] = Merge(file, fork, fork.Data);
        }

        foreach (var (target, entry) in doubles)
        {
            var header = entry.Data;
            if (!AppleSingleReader.AppleDouble.CanRead(header))
            {
                context.Report(DiagnosticSeverity.Warning, "archive.appledouble-invalid",
                    $"The {format} entry \"{string.Join('/', entry.Path)}\" is named like an AppleDouble header but is not one; kept as a file.");
                Add(entry);
                continue;
            }
            if (Find(target) is null && folders.Contains(Key(target)))
            {
                continue; // A folder's Finder info: not kept.
            }

            MacFile parts;
            try
            {
                parts = AppleSingleReader.AppleDouble.Read(header, context.WithHostName(ToMacString(target[^1])))[0];
            }
            catch (InvalidDataException e)
            {
                context.Report(DiagnosticSeverity.Warning, "archive.appledouble-invalid",
                    $"The AppleDouble header \"{string.Join('/', entry.Path)}\" cannot be read ({e.Message}); ignored.");
                continue;
            }
            var file = Find(target) ?? AddEmpty(target);
            files[byPath[Key(target)]] = new UnixArchiveEntry
            {
                Path = file.Path,
                Data = file.Data.Length == 0 && parts.DataFork.Length > 0 ? parts.DataFork : file.Data,
                IsResourceFork = false,
                FinderInfo = IsZero(parts.FinderInfo) ? file.FinderInfo : parts.FinderInfo,
                Created = parts.Created ?? file.Created,
                Modified = parts.Modified ?? file.Modified,
                SymbolicLinkTarget = file.SymbolicLinkTarget,
                ResourceFork = parts.ResourceFork.Length > 0 ? parts.ResourceFork : file.ResourceFork,
            };
        }

        return files.Select(f => new MacFile
        {
            Name = ToMacString(f.Path[^1]),
            UnicodeName = f.Path[^1],
            FolderPath = f.Path[..^1].Select(ToMacString).ToArray(),
            UnicodeFolderPath = f.Path[..^1],
            FinderInfo = f.FinderInfo ?? FinderInfo.Empty,
            Created = f.Created,
            Modified = f.Modified,
            DataFork = f.Data,
            ResourceFork = f.ResourceFork,
            SymbolicLinkTarget = f.SymbolicLinkTarget,
        }).ToList();

        void Add(UnixArchiveEntry entry)
        {
            var key = Key(entry.Path);
            if (byPath.TryGetValue(key, out var index))
            {
                context.Report(DiagnosticSeverity.Warning, "archive.duplicate-entry",
                    $"The {format} archive holds \"{key}\" more than once; the last one is kept.");
                files[index] = Merge(entry, files[index], files[index].ResourceFork);
                return;
            }
            byPath[key] = files.Count;
            files.Add(entry);
        }

        UnixArchiveEntry? Find(string[] path) => byPath.TryGetValue(Key(path), out var i) ? files[i] : null;

        UnixArchiveEntry AddEmpty(string[] path)
        {
            var entry = new UnixArchiveEntry { Path = path };
            byPath[Key(path)] = files.Count;
            files.Add(entry);
            return entry;
        }

        // The file with a resource fork and any Mac data the other entry has and it lacks.
        static UnixArchiveEntry Merge(UnixArchiveEntry file, UnixArchiveEntry other, ForkData resourceFork) => new()
        {
            Path = file.Path,
            Data = file.Data,
            FinderInfo = file.FinderInfo ?? other.FinderInfo,
            Created = file.Created ?? other.Created,
            Modified = file.Modified ?? other.Modified,
            SymbolicLinkTarget = file.SymbolicLinkTarget,
            ResourceFork = resourceFork.Length > 0 ? resourceFork : file.ResourceFork,
        };
    }

    private static bool IsZero(FinderInfo info) => info.ToArray().AsSpan().IndexOfAnyExcept((byte)0) < 0;

    private static string Key(string[] path) => string.Join('/', path);
}
