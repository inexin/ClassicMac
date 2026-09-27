using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files
{
    /// <summary>How a host file's Mac parts were found.</summary>
    public enum HostLayout
    {
        /// <summary>A plain file: the data fork only, named after the host file.</summary>
        Plain,

        /// <summary>
        /// A Basilisk II / SheepShaver shared folder: the resource fork in <c>.rsrc/&lt;name&gt;</c>, Finder info in
        /// <c>.finf/&lt;name&gt;</c> beside the data file.
        /// </summary>
        BasiliskII,

        /// <summary>An AppleDouble pair: Finder info, dates and resource fork in the <c>._&lt;name&gt;</c> header file.</summary>
        AppleDouble,

        /// <summary>A macOS file whose resource fork is read from <c>&lt;path&gt;/..namedfork/rsrc</c>.</summary>
        MacOSNamedFork,

        /// <summary>
        /// A folder from a DOS disk written by PC Exchange or File Exchange: the resource fork in
        /// <c>RESOURCE.FRK/&lt;8.3 name&gt;</c>, Mac name, Finder info and dates in a <c>FINDER.DAT</c> record.
        /// </summary>
        PcExchange,
    }

    /// <summary>A host file read as a Mac file, with the companion files used.</summary>
    /// <param name="File">The Mac file.</param>
    /// <param name="Layout">How its parts were found.</param>
    /// <param name="Companions">The host paths besides the file itself that contributed.</param>
    public sealed record HostFile(MacFile File, HostLayout Layout, IReadOnlyList<string> Companions);

    /// <summary>
    /// Reads files on the host's disk as Mac files, joining the companions that carry their resource forks and Finder
    /// info: PC Exchange / File Exchange folders, Basilisk II / SheepShaver shared folders, AppleDouble <c>._</c> files,
    /// and macOS named forks.
    /// </summary>
    public static class HostFiles
    {
        /// <summary>Reads <paramref name="path"/> with whatever companions it has.</summary>
        public static HostFile Read(string path, ContainerReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(path);
            var context = new ContainerContext(options, diagnostics);
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full) ?? ".";
            var hostName = Path.GetFileName(full);
            var file = new MacFile { Name = ToMacName(hostName, basilisk: false, context), DataFork = ForkData.FromFile(full) };

            if (ReadPcExchange(full, directory, hostName, file, context) is { } pcExchange) return pcExchange;

            // Basilisk II: .rsrc and .finf beside the file, same host name.
            var rsrc = Path.Combine(directory, ".rsrc", hostName);
            var finf = Path.Combine(directory, ".finf", hostName);
            if (File.Exists(rsrc) || File.Exists(finf))
            {
                var companions = new List<string>();
                file = file with { Name = ToMacName(hostName, basilisk: true, context) };
                if (File.Exists(rsrc))
                {
                    companions.Add(rsrc);
                    var fork = ForkData.FromFile(rsrc);
                    if (fork.Length > 0) file = file with { ResourceFork = fork };
                }
                if (File.Exists(finf))
                {
                    companions.Add(finf);
                    var info = File.ReadAllBytes(finf);
                    if (info.Length != FinderInfo.Length)
                    {
                        context.Report(DiagnosticSeverity.Warning, "host.finf-length",
                            $"{finf} is {info.Length} bytes, not {FinderInfo.Length}.");
                    }
                    file = file with { FinderInfo = FinderInfo.Read(info) };
                }
                return new HostFile(file, HostLayout.BasiliskII, companions);
            }

            // AppleDouble: ._name beside the file.
            var header = Path.Combine(directory, "._" + hostName);
            if (File.Exists(header))
            {
                var input = ForkData.FromFile(header);
                var reader = AppleSingleReader.AppleDouble;
                if (reader.CanRead(input))
                {
                    var parts = reader.Read(input, context.WithHostName(file.Name))[0];
                    file = parts with { DataFork = file.DataFork };
                    return new HostFile(file, HostLayout.AppleDouble, [header]);
                }
                context.Report(DiagnosticSeverity.Warning, "host.appledouble-invalid",
                    $"{header} is not an AppleDouble header file; ignored.");
            }

            // macOS keeps the resource fork as a named fork of the file itself.
            if (OperatingSystem.IsMacOS())
            {
                var named = Path.Combine(full, "..namedfork", "rsrc");
                if (File.Exists(named) && new FileInfo(named).Length > 0)
                    return new HostFile(file with { ResourceFork = ForkData.FromFile(named) }, HostLayout.MacOSNamedFork, [named]);
            }

            return new HostFile(file, HostLayout.Plain, []);
        }

        /// <summary>
        /// The other files in <paramref name="path"/>'s folder (each read with its companions, when enumerated), for
        /// formats split across files. AppleDouble <c>._</c> files are companions, not files, and are left out.
        /// </summary>
        public static Func<IEnumerable<MacFile>> Siblings(string path, ContainerReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(path);
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full) ?? ".";
            return () => Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory)
                    .Where(f => !string.Equals(f, full, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
                    .Select(f => Read(f, options, diagnostics).File)
                : [];
        }

        /// <summary>
        /// Writes <paramref name="file"/> into <paramref name="directory"/> as <paramref name="hostName"/> (default: from
        /// its Mac name through <see cref="HostNames"/>), in the options' layout: the data fork as the file itself, the
        /// resource fork, Finder info and dates in an AppleDouble <c>._</c> file or in Basilisk II's <c>.rsrc/</c> (only
        /// when there is a resource fork) and <c>.finf/</c>. The data file's creation and modification times are set from
        /// the Mac dates. Throws <see cref="IOException"/> when a file exists and overwriting is off. Returns the paths
        /// written.
        /// </summary>
        public static IReadOnlyList<string> Write(MacFile file, string directory, HostWriteOptions? options = null, string? hostName = null)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(directory);
            options ??= HostWriteOptions.Default;
            if (options.Layout is not (HostLayout.AppleDouble or HostLayout.BasiliskII))
                throw new ArgumentException($"Files cannot be written as {options.Layout}.", nameof(options));
            var name = hostName ?? HostNames.ToHostName(file.Name);

            var data = Path.Combine(directory, name);
            var paths = new List<string> { data };
            if (options.Layout == HostLayout.AppleDouble) paths.Add(Path.Combine(directory, "._" + name));
            else
            {
                if (file.ResourceFork.Length > 0) paths.Add(Path.Combine(directory, ".rsrc", name));
                paths.Add(Path.Combine(directory, ".finf", name));
            }
            if (!options.Overwrite && paths.FirstOrDefault(File.Exists) is { } existing)
                throw new IOException($"{existing} exists.");

            Directory.CreateDirectory(directory);
            CopyTo(file.DataFork, data);
            if (options.Layout == HostLayout.AppleDouble)
            {
                using var header = new FileStream(paths[1], FileMode.Create, FileAccess.Write);
                AppleDoubleWriter.Write(file, header, options.TimeZone);
            }
            else
            {
                if (file.ResourceFork.Length > 0)
                {
                    Directory.CreateDirectory(Path.Combine(directory, ".rsrc"));
                    CopyTo(file.ResourceFork, paths[1]);
                }
                Directory.CreateDirectory(Path.Combine(directory, ".finf"));
                File.WriteAllBytes(paths[^1], file.FinderInfo.ToArray());
            }

            // Mac dates are local times.
            if (file.Created is { } created) File.SetCreationTime(data, created.ToDateTime());
            if (file.Modified is { } modified) File.SetLastWriteTime(data, modified.ToDateTime());
            return paths;
        }

        private static void CopyTo(ForkData fork, string path)
        {
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var input = fork.Open();
            input.CopyTo(output);
        }

        /// <summary>The name a layout is shown under in a container chain.</summary>
        public static string FormatName(HostLayout layout) => layout switch
        {
            HostLayout.BasiliskII => "Basilisk II folder",
            HostLayout.AppleDouble => "AppleDouble pair",
            HostLayout.MacOSNamedFork => "macOS named fork",
            HostLayout.PcExchange => "PC Exchange folder",
            _ => "host file",
        };

        // PC Exchange / File Exchange: RESOURCE.FRK and FINDER.DAT in the file's directory (FAT names, so matched
        // without regard to case). The record is found by the file's 8.3 name; a long host name has no 8.3 key on this
        // side, so it is matched against the records' Mac names instead (fitted: the Mac keyed by the 8.3 alias).
        private static HostFile? ReadPcExchange(string full, string directory, string hostName, MacFile file, ContainerContext context)
        {
            var rsrcFolder = FindEntry(directory, PcExchange.ResourceFolder, directories: true);
            var finderData = FindEntry(directory, PcExchange.FinderData, directories: false);
            if (rsrcFolder is null && finderData is null) return null;
            if (string.Equals(hostName, PcExchange.FinderData, StringComparison.OrdinalIgnoreCase)) return null;

            var companions = new List<string>();
            if (rsrcFolder is not null && FindEntry(rsrcFolder, hostName, directories: false) is { } rsrc)
            {
                companions.Add(rsrc);
                var fork = ForkData.FromFile(rsrc);
                if (fork.Length > 0) file = file with { ResourceFork = fork };
            }

            if (finderData is not null)
            {
                var records = PcExchange.ReadFinderData(File.ReadAllBytes(finderData));
                var key = PcExchange.DosKey(hostName);
                var record = key is not null
                    ? records.FirstOrDefault(r => r.DosName == key)
                    : records.FirstOrDefault(r => string.Equals(r.MacName.ToMacRoman(), hostName, StringComparison.OrdinalIgnoreCase));
                if (record is not null)
                {
                    companions.Add(finderData);
                    file = PcExchange.Apply(file, record,
                        DosTime.FromLocal(File.GetCreationTime(full)), DosTime.FromLocal(File.GetLastWriteTime(full)));
                    if (context.Options.ExtensionMap is { } map)
                        file = file with { FinderInfo = map.Apply(file.FinderInfo, hostName) };
                }
            }

            // DOS hidden or system makes the file invisible.
            if ((File.GetAttributes(full) & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                file = file with { FinderInfo = file.FinderInfo with { Flags = file.FinderInfo.Flags | FinderFlags.IsInvisible } };

            return companions.Count == 0 ? null : new HostFile(file, HostLayout.PcExchange, companions);
        }

        private static string? FindEntry(string directory, string name, bool directories)
        {
            var exact = Path.Combine(directory, name);
            if (directories ? Directory.Exists(exact) : File.Exists(exact)) return exact;
            if (!Directory.Exists(directory)) return null;
            var entries = directories ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFiles(directory);
            return entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));
        }


        /// <summary>
        /// A host file name as a Mac name: each character in Mac OS Roman; for Basilisk II names, <c>%XX</c> is the
        /// byte itself (it escapes control characters and characters the host forbids, e.g. <c>Icon%0D</c>,
        /// <c>%3F</c> for <c>?</c>; fitted to real shared folders). Characters without a Mac OS Roman byte become
        /// <c>?</c> and names are cut to 255 bytes, both reported.
        /// </summary>
        public static MacString ToMacName(string hostName, bool basilisk, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(hostName);
            ArgumentNullException.ThrowIfNull(context);
            var bytes = new List<byte>(hostName.Length);
            var replaced = false;
            for (var i = 0; i < hostName.Length; i++)
            {
                if (basilisk && hostName[i] == '%' && i + 2 < hostName.Length
                    && byte.TryParse(hostName.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var escaped))
                {
                    bytes.Add(escaped);
                    i += 2;
                }
                else if (MacRoman.TryGetByte(hostName[i], out var b)) bytes.Add(b);
                else
                {
                    bytes.Add((byte)'?');
                    replaced = true;
                }
            }
            if (replaced)
            {
                context.Report(DiagnosticSeverity.Info, "host.name-unmappable",
                    $"\"{hostName}\" has characters Mac OS Roman lacks; they became '?'.");
            }
            if (bytes.Count > 255)
            {
                context.Report(DiagnosticSeverity.Warning, "host.name-too-long",
                    $"\"{hostName}\" is longer than a Mac name can be; cut to 255 bytes.");
                bytes.RemoveRange(255, bytes.Count - 255);
            }
            return new MacString(bytes.ToArray());
        }
    }
}
