using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Linq;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli
{
    /// <summary>
    /// The <c>classicmac</c> command tree. It has <c>info</c>, <c>list</c>, <c>unpack</c>, <c>extract</c>, <c>convert</c> and <c>pack</c>. Every limit option maps onto <see cref="ReadOptions"/> or <see cref="ContainerReadOptions"/>.
    /// </summary>
    internal sealed class CommandLine(TextWriter output, TextWriter error)
    {
        internal enum ListFormat
        {
            Text,
            Json,
        }

        private readonly Option<long> maxResourceSize = new("--max-resource-size")
        {
            Description = "Largest a resource may be once decompressed (bytes, or with KiB/MiB/GiB)",
            DefaultValueFactory = _ => ReadOptions.Default.MaxResourceSize,
            CustomParser = ParseSize,
            Recursive = true,
        };

        private readonly Option<int> maxNestingDepth = new("--max-nesting-depth")
        {
            Description = "How deep containers may nest",
            DefaultValueFactory = _ => ContainerReadOptions.Default.MaxNestingDepth,
            Recursive = true,
        };

        private readonly Option<long> maxExpandedBytes = new("--max-expanded-bytes")
        {
            Description = "Most bytes decompression and unwrapping may produce from one input (bytes, or KiB/MiB/GiB)",
            DefaultValueFactory = _ => ContainerReadOptions.Default.MaxExpandedBytesPerInput,
            CustomParser = ParseSize,
            Recursive = true,
        };

        private readonly Option<bool> verify = new("--verify")
        {
            Description = "Verify whole-image checksums that need a full read (NDIF's CRC-32, as Disk Copy's Verify checksum does)",
            Recursive = true,
        };

        private readonly Option<bool> strict = new("--strict")
        {
            Description = "Treat warnings as errors for the exit code",
            Recursive = true,
        };

        private readonly Option<bool> quiet = new("--quiet", "-q")
        {
            Description = "Print errors only",
            Recursive = true,
        };

        /// <summary>Parses and runs <paramref name="args"/>, returning an exit code from <see cref="ExitCodes"/>.</summary>
        public int Run(string[] args)
        {
            var result = BuildRoot().Parse(args);
            if (result.Errors.Count > 0)
            {
                foreach (var parseError in result.Errors) error.WriteLine(parseError.Message);
                return ExitCodes.Usage;
            }
            return result.Invoke(new InvocationConfiguration { Output = output, Error = error });
        }

        /// <summary>The <see cref="ReadOptions"/> the parsed limit options describe.</summary>
        internal ReadOptions ReadOptionsFrom(ParseResult result) => ReadOptions.Default with
        {
            MaxResourceSize = result.GetValue(maxResourceSize),
        };

        /// <summary>The <see cref="ContainerReadOptions"/> the parsed limit options describe.</summary>
        internal ContainerReadOptions ContainerOptionsFrom(ParseResult result) => ContainerReadOptions.Default with
        {
            MaxNestingDepth = result.GetValue(maxNestingDepth),
            MaxExpandedBytesPerInput = result.GetValue(maxExpandedBytes),
            VerifyChecksums = result.GetValue(verify),
        };

        internal RootCommand BuildRoot()
        {
            var root = new RootCommand("Reads, lists and extracts classic Mac OS files and their resource forks.");
            root.Options.Add(maxResourceSize);
            root.Options.Add(maxNestingDepth);
            root.Options.Add(maxExpandedBytes);
            root.Options.Add(verify);
            root.Options.Add(strict);
            root.Options.Add(quiet);
            root.Subcommands.Add(InfoCommand());
            root.Subcommands.Add(ListCommand());
            root.Subcommands.Add(UnpackCommand());
            root.Subcommands.Add(ExtractCommand());
            root.Subcommands.Add(ConvertCommand());
            root.Subcommands.Add(PackCommand());
            return root;
        }

        private static Argument<FileInfo> InputArgument() => new Argument<FileInfo>("input")
        {
            Description = "A Mac file or a container holding them: AppleSingle, MacBinary, BinHex, uuencode, a file in a " +
                "Basilisk II folder or with an AppleDouble ._ file, a raw resource fork, an archive (StuffIt, Compact Pro, " +
                "DiskDoubler, PackIt, LHA, zip, tar, gzip) or a disk or CD image",
        }.AcceptExistingOnly();

        private Command InfoCommand()
        {
            var input = InputArgument();
            var command = new Command("info", "Show the container chain, Finder info and fork sizes") { input };
            command.SetAction(result => new InfoCommand(output, error).Run(
                result.GetRequiredValue(input), ContainerOptionsFrom(result), result.GetValue(strict), result.GetValue(quiet)));
            return command;
        }

        private Command ListCommand()
        {
            var input = InputArgument();
            var format = new Option<ListFormat>("--format")
            {
                Description = "Output format",
                DefaultValueFactory = _ => ListFormat.Text,
            };
            var command = new Command("list", "List the files and resources inside the input") { input, format };
            command.SetAction(result => new ListCommand(output, error).Run(
                result.GetRequiredValue(input), result.GetValue(format), ReadOptionsFrom(result),
                ContainerOptionsFrom(result), result.GetValue(strict), result.GetValue(quiet)));
            return command;
        }

        internal enum UnpackLayout
        {
            AppleDouble,
            Basilisk,
        }

        private Command UnpackCommand()
        {
            var input = InputArgument();
            var outputDir = new Option<DirectoryInfo>("--output", "-o")
            {
                Description = "Output folder (default: \"<input> unpacked\" next to the input)",
            };
            var layout = new Option<UnpackLayout>("--layout")
            {
                Description = "How resource forks and Finder info are stored: AppleDouble ._ files, or Basilisk II / SheepShaver .rsrc and .finf folders",
                DefaultValueFactory = _ => UnpackLayout.AppleDouble,
            };
            var overwrite = new Option<bool>("--overwrite") { Description = "Replace existing files" };
            var command = new Command("unpack", "Write every Mac file inside the input, with both forks and Finder info, to a folder")
            {
                input, outputDir, layout, overwrite,
            };
            command.SetAction(result => new UnpackCommand(output, error).Run(
                result.GetRequiredValue(input), result.GetValue(outputDir),
                HostWriteOptions.Default with
                {
                    Layout = result.GetValue(layout) == UnpackLayout.Basilisk ? HostLayout.BasiliskII : HostLayout.AppleDouble,
                    Overwrite = result.GetValue(overwrite),
                },
                ContainerOptionsFrom(result), result.GetValue(strict), result.GetValue(quiet)));
            return command;
        }

        private Command ExtractCommand()
        {
            var input = InputArgument();
            var outputDir = new Option<DirectoryInfo>("--output", "-o")
            {
                Description = "Output folder (default: \"<input> resources\" next to the input)",
            };
            var raw = new Option<bool>("--raw") { Description = "Write each resource's data (decompressed) as .bin, without decoding" };
            var keepRaw = new Option<bool>("--keep-raw") { Description = "Also keep the raw data in raw/, for pack" };
            var screenDepth = ScreenDepthOption();
            var noDocuments = new Option<bool>("--no-documents")
            {
                Description = "Do not convert DOCMaker and SimpleText documents to HTML (in document/)",
            };
            var types = new Option<string[]>("--type", "-t")
            {
                Description = "Only resources of this type (repeatable; four characters, e.g. \"snd \", or \\xHH escapes)",
                AllowMultipleArgumentsPerToken = true,
            };
            types.Validators.Add(r =>
            {
                foreach (var type in r.GetValueOrDefault<string[]>() ?? [])
                    if (!FourCC.TryParse(type, out _)) r.AddError($"'{type}' is not a four-character type.");
            });
            var overwrite = new Option<bool>("--overwrite") { Description = "Write into output folders that already hold files" };
            var command = new Command("extract", "Extract resources into a folder with a manifest")
            {
                input, outputDir, raw, keepRaw, types, overwrite, screenDepth, noDocuments,
            };
            command.SetAction(result =>
            {
                var chosen = result.GetValue(types) is { Length: > 0 } list
                    ? list.Select(t => { FourCC.TryParse(t, out var type); return type; }).ToHashSet()
                    : null;
                var decodeOptions = DecodeOptionsFrom(result, screenDepth);
                var decode = !result.GetValue(raw);
                return new ExtractCommand(output, error).Run(
                    result.GetRequiredValue(input), result.GetValue(outputDir),
                    Export.ExportOptions.Default with
                    {
                        KeepRaw = result.GetValue(keepRaw),
                        Decoders = decode ? Decoders.ResourceDecoders.Create(decodeOptions) : [],
                        Documents = decode && !result.GetValue(noDocuments) ? Decoders.ResourceDecoders.CreateDocumentConverters(decodeOptions) : [],
                        Types = chosen,
                        Overwrite = result.GetValue(overwrite),
                        ReadOptions = ReadOptionsFrom(result),
                    },
                    ContainerOptionsFrom(result), result.GetValue(strict), result.GetValue(quiet));
            });
            return command;
        }

        private Command ConvertCommand()
        {
            var input = InputArgument();
            var outputDir = new Option<DirectoryInfo>("--output", "-o")
            {
                Description = "Output folder (default: \"<input> documents\" next to the input)",
            };
            var screenDepth = ScreenDepthOption();
            var overwrite = new Option<bool>("--overwrite") { Description = "Write into an output folder that already holds files" };
            var command = new Command("convert", "Convert the DOCMaker and SimpleText documents inside the input to HTML folders")
            {
                input, outputDir, overwrite, screenDepth,
            };
            command.SetAction(result => new ConvertCommand(output, error).Run(
                result.GetRequiredValue(input), result.GetValue(outputDir),
                Decoders.ResourceDecoders.CreateDocumentConverters(DecodeOptionsFrom(result, screenDepth)), ReadOptionsFrom(result),
                ContainerOptionsFrom(result), result.GetValue(overwrite), result.GetValue(strict), result.GetValue(quiet)));
            return command;
        }

        private Command PackCommand()
        {
            var folder = new Argument<DirectoryInfo>("folder") { Description = "An export folder made by extract (with its manifest.json)" }.AcceptExistingOnly();
            var outputFile = new Option<FileInfo>("--output", "-o") { Description = "The file to write", Required = true };
            var baseFork = new Option<FileInfo>("--base")
            {
                Description = "The file the export was made from, for unchanged resources' stored data when the export has no raw/ copies",
            }.AcceptExistingOnly();
            var dataFork = new Option<FileInfo>("--data") { Description = "A data fork for the container (default: empty)" }.AcceptExistingOnly();
            var container = new Option<PackCommand.Container>("--container")
            {
                Description = "What to write: the raw resource fork, or an AppleDouble header file, AppleSingle, MacBinary III or BinHex 4.0 file",
                DefaultValueFactory = _ => Cli.PackCommand.Container.Raw,
            };
            var allowDeletes = new Option<bool>("--allow-deletes") { Description = "Leave out resources whose files are gone, instead of failing" };
            var overwrite = new Option<bool>("--overwrite") { Description = "Replace the output file" };
            var command = new Command("pack", "Rebuild a resource fork (or a container holding it) from an export folder")
            {
                folder, outputFile, baseFork, dataFork, container, allowDeletes, overwrite,
            };
            command.SetAction(result => new PackCommand(output, error).Run(
                result.GetRequiredValue(folder), result.GetRequiredValue(outputFile), result.GetValue(baseFork), result.GetValue(dataFork),
                result.GetValue(container), result.GetValue(allowDeletes), result.GetValue(overwrite), ReadOptionsFrom(result),
                ContainerOptionsFrom(result), result.GetValue(strict), result.GetValue(quiet)));
            return command;
        }

        private static Option<int> ScreenDepthOption()
        {
            var screenDepth = new Option<int>("--screen-depth")
            {
                Description = "Screen depth pictures are drawn at: 32 (full colour), or 1, 2, 4, 8 or 16 bits with QuickDraw's colour matching and dithering",
                DefaultValueFactory = _ => Decoders.DecodeOptions.Default.ScreenDepth,
            };
            screenDepth.AcceptOnlyFromAmong("1", "2", "4", "8", "16", "32");
            return screenDepth;
        }

        private Decoders.DecodeOptions DecodeOptionsFrom(ParseResult result, Option<int> screenDepth) => Decoders.DecodeOptions.Default with
        {
            ScreenDepth = result.GetValue(screenDepth),
            QuickDraw = ReadOptionsFrom(result).ResourceManager,
        };

        // Sizes: plain bytes, or a number with KiB, MiB or GiB (also K, M, G), case-insensitive.
        internal static long ParseSize(ArgumentResult result)
        {
            var text = result.Tokens.Count == 1 ? result.Tokens[0].Value : string.Empty;
            if (TryParseSize(text, out var size)) return size;
            result.AddError($"'{text}' is not a size: use bytes, or a number with KiB, MiB or GiB.");
            return 0;
        }

        internal static bool TryParseSize(string text, out long size)
        {
            size = 0;
            var s = text.Trim();
            long unit = 1;
            foreach (var (suffix, value) in new[]
            {
                ("KiB", 1L << 10), ("MiB", 1L << 20), ("GiB", 1L << 30), ("K", 1L << 10), ("M", 1L << 20), ("G", 1L << 30),
            })
            {
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[..^suffix.Length].TrimEnd();
                    unit = value;
                    break;
                }
            }
            if (!long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
            if (number > long.MaxValue / unit) return false;
            size = number * unit;
            return size > 0;
        }
    }
}
