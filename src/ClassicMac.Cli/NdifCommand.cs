using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;
using ClassicMac.Files.Hfs;

namespace ClassicMac.Cli;

// `ndif` (docs/cli.md §3.4): a new Disk Copy 6 image of a disk (docs/formats/disk-images/ndif.md §3.2), written as an
// AppleDouble pair or a Basilisk II entry, whole or as the parts of a segmented image (§3.3).
internal sealed class NdifCommand(TextWriter output, TextWriter error, CommandLine cli)
{
    private static readonly string[] Formats = ["adc", "kencode", "read-only", "read-write"];

    public Command Build()
    {
        var input = new Argument<FileInfo>("disk") { Description = "The disk: a volume or disk image, or any file taken as the disk's sectors" };
        var file = new Argument<FileInfo>("image") { Description = "The new image" };
        var format = new Option<string>("--format")
        {
            Description = "adc (Read-Only Compressed, the default), kencode (Disk Copy's \"Smaller (KC)\", not always smaller), read-only or read-write",
            DefaultValueFactory = _ => "adc",
        };
        format.AcceptOnlyFromAmong(Formats);
        var chunkSize = new Option<int>("--chunk-size") { Description = "Sectors per compressed chunk (default 512, as Disk Copy 6.3.3)", DefaultValueFactory = _ => 512 };
        var segments = new Option<int>("--segments") { Description = "Cut the image into this many parts, 2 to 128 (\"<name> 1of3\" …)" };
        var layout = new Option<CommandLine.UnpackLayout>("--layout")
        {
            Description = "Where the resource fork and Finder info go: an AppleDouble ._ file, or Basilisk II / SheepShaver .rsrc and .finf folders",
            DefaultValueFactory = _ => CommandLine.UnpackLayout.AppleDouble,
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace existing files" };
        var json = new Option<bool>("--json") { Description = "Print the result as JSON" };
        var command = new Command("ndif", "Make a new Disk Copy 6 (NDIF) image of a disk")
        {
            input, file, format, chunkSize, segments, layout, overwrite, json,
        };
        command.SetAction(result =>
        {
            var source = result.GetRequiredValue(input).FullName;
            var target = result.GetRequiredValue(file).FullName;
            var kind = result.GetRequiredValue(format);
            var parts = result.GetValue(segments);
            if (result.GetValue(chunkSize) < 1 || parts is 1 or < 0 or > 128)
            {
                error.WriteLine("--chunk-size is at least 1 sector; --segments is 2 to 128.");
                return ExitCodes.Usage;
            }

            byte[] disk;
            try
            {
                disk = Disk(source, result);
            }
            catch (Exception e) when (ExceptionFilters.IsFileAccess(e) || ExceptionFilters.IsMalformed(e))
            {
                error.WriteLine($"{source}: {e.Message}");
                return ExitCodes.IoError;
            }

            var options = new NdifCreateOptions
            {
                Format = kind switch
                {
                    "kencode" => NdifFormat.KenCode,
                    "read-only" => NdifFormat.ReadOnly,
                    "read-write" => NdifFormat.ReadWrite,
                    _ => NdifFormat.Adc,
                },
                ChunkSectors = result.GetValue(chunkSize),
            };
            MacFile image;
            IReadOnlyList<MacFile> files;
            try
            {
                image = NdifWriter.Create(disk, Path.GetFileName(target), options);
                files = parts == 0 ? [image] : NdifWriter.Split(image, parts, Path.GetFileNameWithoutExtension(target));
            }
            catch (ArgumentException e)
            {
                error.WriteLine($"{Path.GetFileName(source)}: {e.Message.Split(" (Parameter", 2)[0]}");
                return ExitCodes.Usage;
            }

            var directory = Path.GetDirectoryName(target)!;
            var hostOptions = HostWriteOptions.Default with
            {
                Layout = result.GetValue(layout) == CommandLine.UnpackLayout.Basilisk ? HostLayout.BasiliskII : HostLayout.AppleDouble,
                Overwrite = result.GetValue(overwrite),
            };
            // The whole image takes the name given; parts are named by their Mac names.
            var written = new List<string>();
            try
            {
                if (!hostOptions.Overwrite && files.Select(f => HostPath(f, directory, parts == 0 ? target : null, hostOptions)).FirstOrDefault(File.Exists) is { } existing)
                {
                    error.WriteLine($"{existing} exists (--overwrite to replace it).");
                    return ExitCodes.IoError;
                }

                foreach (var f in files)
                {
                    written.Add(HostFiles.Write(f, directory, hostOptions, parts == 0 ? Path.GetFileName(target) : null)[0]);
                }
            }
            catch (Exception e) when (ExceptionFilters.IsFileAccess(e))
            {
                error.WriteLine($"{target}: {e.Message}");
                return ExitCodes.IoError;
            }

            var map = new BigEndianReader(ClassicMac.Resources.ResourceFork.Read(image.ResourceFork.ToArray()).Find(FourCC.FromString("bcem"), 128)!.GetData());
            var volumeName = new MacString(map.Source.Span.Slice(5, map.ReadByteAt(4))).ToMacRoman();
            if (result.GetValue(json))
            {
                output.WriteLine(MacPathJson.Document(w =>
                {
                    MacPathJson.Strings(w, "written", written);
                    w.WriteString("format", kind);
                    w.WriteString("name", volumeName);
                    w.WriteNumber("diskSize", disk.Length);
                    w.WriteNumber("segments", Math.Max(1, parts));
                }));
            }
            else
            {
                var label = kind switch
                {
                    "adc" => "ADC",
                    "kencode" => "KenCode",
                    _ => kind,
                };
                for (var n = 0; n < written.Count; n++)
                {
                    var part = parts == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $", part {n + 1} of {parts}");
                    output.WriteLine($"Wrote {written[n]} (NDIF {label}, \"{volumeName}\", {disk.Length.ToString("N0", CultureInfo.InvariantCulture)}-byte disk{part})");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    // The disk: a volume or disk image the editor opens (a plain volume, Disk Copy 4.2, NDIF) gives its disk; any other
    // plain file is the disk's sectors as they are.
    private byte[] Disk(string path, ParseResult result)
    {
        var diagnostics = new List<Diagnostic>();
        if (InputEditSession.Open(path, cli.ContainerOptionsFrom(result), cli.ReadOptionsFrom(result), diagnostics) is
            { Kind: InputEditKind.HfsVolume or InputEditKind.HfsPlusVolume, Partition: null, PartitionNames: [] } session)
        {
            return session.Volume;
        }

        if (HostFiles.Read(path).Layout != HostLayout.Plain)
        {
            throw new InvalidDataException("not a disk: it has a resource fork or Finder info and holds no disk image ClassicMac reads.");
        }

        return File.ReadAllBytes(path);
    }

    private static string HostPath(MacFile file, string directory, string? target, HostWriteOptions options) =>
        target ?? Path.Combine(directory, HostFiles.ToHostName(file.Name, options.Layout, options.NameEncoding));
}
