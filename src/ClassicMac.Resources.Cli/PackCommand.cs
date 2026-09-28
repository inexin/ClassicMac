using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Containers;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Cli
{
    // `pack`: an export folder back into a resource fork (ResourcePacker), written raw or in a container with the
    // manifest's name, type, creator and Finder flags. Nothing is written when the pack has errors.
    internal sealed class PackCommand(TextWriter output, TextWriter error)
    {
        internal enum Container
        {
            Raw,
            AppleDouble,
            AppleSingle,
            MacBinary,
            BinHex,
        }

        public int Run(DirectoryInfo directory, FileInfo target, FileInfo? baseFile, FileInfo? dataFile, Container container, bool allowDeletes,
            bool overwrite, ReadOptions readOptions, ContainerReadOptions containerOptions, bool strict, bool quiet)
        {
            var reporter = new Reporter(error, strict, quiet);
            if (target.Exists && !overwrite)
            {
                error.WriteLine($"{target.FullName} exists (use --overwrite to replace it).");
                return ExitCodes.IoError;
            }
            ResourceFork? baseFork = null;
            if (baseFile is not null)
            {
                var diagnostics = new List<Diagnostic>();
                var forks = Input.Open(baseFile, containerOptions, diagnostics).Forks(baseFile, readOptions, reporter).Where(f => f.Fork is not null).ToList();
                reporter.Write(baseFile.Name, diagnostics);
                if (forks.Count != 1)
                {
                    error.WriteLine($"{baseFile.Name}: the base must hold one resource fork; it holds {forks.Count}.");
                    return ExitCodes.Usage;
                }
                baseFork = forks[0].Fork;
            }

            PackResult result;
            try
            {
                result = ResourcePacker.Pack(directory.FullName, PackOptions.Default with { Base = baseFork, AllowDeletes = allowDeletes });
            }
            catch (Exception e) when (e is InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
            {
                error.WriteLine($"{directory.Name}: {e.Message}");
                return ExitCodes.Unreadable;
            }
            reporter.Write(directory.Name, result.Diagnostics);
            if (result.Failed)
            {
                error.WriteLine("Nothing written: the export cannot be packed as it is.");
                return ExitCodes.Damaged;
            }

            var fork = result.Fork.ToArray();
            try
            {
                var file = new MacFile
                {
                    Name = MacString.Parse(result.Source.Name),
                    FinderInfo = FinderInfo.Empty with
                    {
                        Type = FourCC.TryParse(result.Source.Type, out var type) ? type : default,
                        Creator = FourCC.TryParse(result.Source.Creator, out var creator) ? creator : default,
                        Flags = (FinderFlags)result.Source.Flags,
                    },
                    DataFork = dataFile is null ? ForkData.Empty : ForkData.FromFile(dataFile.FullName),
                    ResourceFork = ForkData.FromBytes(fork),
                };
                using var stream = File.Create(target.FullName);
                switch (container)
                {
                    case Container.AppleDouble:
                        AppleDoubleWriter.Write(file, stream);
                        break;
                    case Container.AppleSingle:
                        AppleDoubleWriter.WriteAppleSingle(file, stream);
                        break;
                    case Container.MacBinary:
                        MacBinaryWriter.Write(file, stream);
                        break;
                    case Container.BinHex:
                        BinHexWriter.Write(file, stream);
                        break;
                    default:
                        stream.Write(fork);
                        break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
            {
                error.WriteLine($"{target.FullName}: {e.Message}");
                return ExitCodes.IoError;
            }
            output.WriteLine($"{result.Fork.Resources.Count} resources ({fork.Length:N0} bytes of resource fork), to {target.FullName}");
            return reporter.ExitCode;
        }
    }
}
