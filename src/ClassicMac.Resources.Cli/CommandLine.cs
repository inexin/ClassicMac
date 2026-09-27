using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Files;

namespace ClassicMac.Resources.Cli
{
    /// <summary>
    /// The <c>classicmac</c> command tree. Phase 1 has <c>info</c>, <c>list</c> and <c>extract</c>; <c>pack</c> joins
    /// in phase 5. Every limit option maps onto <see cref="ReadOptions"/> or <see cref="ContainerReadOptions"/>.
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
        };

        internal RootCommand BuildRoot()
        {
            var root = new RootCommand("Reads, lists and extracts classic Mac OS files and their resource forks.");
            root.Options.Add(maxResourceSize);
            root.Options.Add(maxNestingDepth);
            root.Options.Add(maxExpandedBytes);
            root.Options.Add(strict);
            root.Options.Add(quiet);
            root.Subcommands.Add(InfoCommand());
            root.Subcommands.Add(ListCommand());
            root.Subcommands.Add(ExtractCommand());
            return root;
        }

        private static Argument<FileInfo> InputArgument() => new Argument<FileInfo>("input")
        {
            Description = "A Mac file: AppleSingle, MacBinary, BinHex, a file in a Basilisk II folder or with an " +
                "AppleDouble ._ file, or a raw resource fork",
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

        private Command ExtractCommand()
        {
            var input = InputArgument();
            var outputDir = new Option<DirectoryInfo>("--output", "-o")
            {
                Description = "Output folder (default: the input's name next to it)",
            };
            var raw = new Option<bool>("--raw") { Description = "Write every resource's data as stored, undecoded" };
            var keepRaw = new Option<bool>("--keep-raw") { Description = "Also keep the raw data in raw/, for pack" };
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
            var overwrite = new Option<bool>("--overwrite") { Description = "Replace an existing output folder" };
            var command = new Command("extract", "Extract resources into a folder with a manifest")
            {
                input, outputDir, raw, keepRaw, types, overwrite,
            };
            command.SetAction(NotImplemented);
            return command;
        }

        private int NotImplemented(ParseResult result)
        {
            _ = ReadOptionsFrom(result);
            _ = ContainerOptionsFrom(result);
            error.WriteLine($"'{result.CommandResult.Command.Name}' is not implemented yet.");
            return ExitCodes.NotImplemented;
        }

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
