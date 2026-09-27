using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClassicMac.Core;

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
    }

    /// <summary>A host file read as a Mac file, with the companion files used.</summary>
    /// <param name="File">The Mac file.</param>
    /// <param name="Layout">How its parts were found.</param>
    /// <param name="Companions">The host paths besides the file itself that contributed.</param>
    public sealed record HostFile(MacFile File, HostLayout Layout, IReadOnlyList<string> Companions);

    /// <summary>
    /// Reads files on the host's disk as Mac files, joining the companions that carry their resource forks and Finder
    /// info: Basilisk II / SheepShaver shared folders, AppleDouble <c>._</c> files, and macOS named forks.
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
