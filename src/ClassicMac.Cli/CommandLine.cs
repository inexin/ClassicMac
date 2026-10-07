using System.CommandLine.Parsing;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System;
using ClassicMac.Core;
using ClassicMac.Files.Commands;
using ClassicMac.Files;
using ClassicMac.Resources;

namespace ClassicMac.Cli;

/// <summary>
/// The <c>classicmac</c> command tree. It has <c>info</c>, <c>list</c>, <c>unpack</c>, <c>extract</c>, <c>convert</c>, <c>disasm</c> and <c>pack</c>,
/// the read commands on Mac paths (<c>ls</c>, <c>stat</c>, <c>cat</c>, <c>find</c>, <c>get</c>) and the write commands (<c>put</c>, <c>mkdir</c>,
/// <c>rm</c>, <c>rename</c>, <c>set</c>, <c>res-add</c>, <c>res-rm</c>) and <c>mcp</c>, the MCP server (docs/cli.md). Every limit option maps onto <see cref="ReadOptions"/> or
/// <see cref="ContainerReadOptions"/>. <paramref name="binary"/> takes <c>cat --raw</c>'s bytes (standard output).
/// </summary>
internal sealed class CommandLine(TextWriter output, TextWriter error, Stream? binary = null)
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
        Description = "Most bytes decompression and decoding may make from one input; partitions and files read where they lie do not count (bytes, or KiB/MiB/GiB)",
        DefaultValueFactory = _ => ContainerReadOptions.Default.MaxExpandedBytesPerInput,
        CustomParser = ParseSize,
        Recursive = true,
    };

    private readonly Option<MacTextEncoding> encoding = new("--encoding")
    {
        Description = "The Mac encoding of text: roman (the default), japanese, chinese-traditional, chinese-simplified, korean, "
            + "arabic, hebrew, greek, cyrillic, ukrainian, thai, central-european, croatian, romanian, icelandic or turkish (or an IANA name, x-mac-japanese)",
        DefaultValueFactory = _ => MacTextEncoding.Roman,
        CustomParser = ParseEncoding,
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
            foreach (var parseError in result.Errors)
            {
                error.WriteLine(parseError.Message);
            }

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
        NameEncoding = result.GetValue(encoding),
    };

    internal RootCommand BuildRoot()
    {
        var root = new RootCommand("Reads, lists and extracts classic Mac OS files and their resource forks.");
        root.Options.Add(maxResourceSize);
        root.Options.Add(maxNestingDepth);
        root.Options.Add(maxExpandedBytes);
        root.Options.Add(verify);
        root.Options.Add(encoding);
        root.Options.Add(strict);
        root.Options.Add(quiet);
        root.Subcommands.Add(InfoCommand());
        root.Subcommands.Add(ListCommand());
        root.Subcommands.Add(UnpackCommand());
        root.Subcommands.Add(ExtractCommand());
        root.Subcommands.Add(ConvertCommand());
        root.Subcommands.Add(DisasmCommand());
        root.Subcommands.Add(PackCommand());
        root.Subcommands.Add(LsCommand());
        root.Subcommands.Add(StatCommand());
        root.Subcommands.Add(CatCommand());
        root.Subcommands.Add(DerezCommand());
        root.Subcommands.Add(RezCommand());
        root.Subcommands.Add(FindCommand());
        root.Subcommands.Add(GetCommand());
        root.Subcommands.Add(CheckCommand());
        foreach (var write in new WriteCommands(output, error, this).Commands())
        {
            root.Subcommands.Add(write);
        }

        root.Subcommands.Add(McpCommand());
        root.Subcommands.Add(ShellCommand());
        root.Subcommands.Add(HelpCommand(root));
        HelpExamples.AddTo(root);
        return root;
    }

    // help [command] (docs/cli.md §6): the root's help, or a command's, as --help gives them.
    private Command HelpCommand(RootCommand root)
    {
        var name = new Argument<string?>("command") { Description = "The command to show the help of", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("help", "Show the help of a command, with examples, or the list of commands") { name };
        command.SetAction(result =>
        {
            var typed = result.GetValue(name);
            if (typed is not null && !root.Subcommands.Any(c => string.Equals(c.Name, typed, StringComparison.Ordinal)))
            {
                error.WriteLine($"{typed}: no such command (classicmac help lists them).");
                return ExitCodes.Usage;
            }

            string[] args = typed is null ? ["--help"] : [typed, "--help"];
            return root.Parse(args).Invoke(result.InvocationConfiguration);
        });
        return command;
    }

    private Command ShellCommand()
    {
        var path = new Argument<string>("input") { Description = "The host file, or a Mac path inside it to start at" };
        var script = new Option<FileInfo>("--script") { Description = "Run the commands in this file, one per line, instead of asking" }.AcceptExistingOnly();
        var json = new Option<bool>("--json") { Description = "Print each command's result as one line of JSON" };
        var command = new Command("shell", "A DOS-like shell on one input: cd into disk images and archives, dir, type, copy, del, md, save (docs/cli.md §5)")
        {
            path, script, json,
        };
        command.SetAction(result =>
        {
            var typed = result.GetRequiredValue(path);
            if (MacPaths.SplitHost(typed) is not var (host, rest))
            {
                error.WriteLine($"{typed}: no host file (the path starts with no existing file).");
                return ExitCodes.NotFound;
            }

            using var session = PathSession.Open("shell", host, ContainerOptionsFrom(result), ReadOptionsFrom(result));
            using var reader = result.GetValue(script) is { } file ? new StreamReader(file.FullName) : null;
            Shell.IShellConsole console = reader is not null ? new Shell.ScriptConsole(reader, output, error)
                : ShellInput is { } input ? new Shell.ScriptConsole(input, output, error)
                : Console.IsInputRedirected ? new Shell.ScriptConsole(Console.In, output, error)
                : new Shell.InteractiveConsole(new Shell.ConsoleTerminal(), output, error);
            var shell = new Shell.MacShell(console, session, ContainerOptionsFrom(result), ReadOptionsFrom(result), result.GetValue(json));
            try
            {
                shell.Enter(rest);
            }
            catch (PathNotFound)
            {
                error.WriteLine($"{typed}: names nothing.");
                return ExitCodes.NotFound;
            }

            return shell.Run();
        });
        return command;
    }

    /// <summary>The shell's commands when no script is given (tests; otherwise standard input).</summary>
    internal TextReader? ShellInput { get; init; }

    private Command McpCommand()
    {
        var command = new Command("mcp", "Serve the file commands as MCP tools over standard input and output (docs/cli.md §4)");
        command.SetAction(async (result, cancellationToken) =>
        {
            using var server = new Mcp.MacMcpServer(ContainerOptionsFrom(result), ReadOptionsFrom(result));
            await server.RunStdioAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static Argument<FileInfo> InputArgument() => new Argument<FileInfo>("input")
    {
        Description = "A Mac file or a container holding them: AppleSingle, MacBinary, BinHex, uuencode, a file in a " +
            "Basilisk II folder or with an AppleDouble ._ file, a raw resource fork, an archive (StuffIt, Compact Pro, " +
            "DiskDoubler, PackIt, LHA, zip, tar, gzip), a disk or CD image, or a Mac ROM image or NewWorld Mac OS ROM file",
    }.AcceptExistingOnly();

    private Command InfoCommand()
    {
        var input = InputArgument();
        var database = new Option<FileInfo>("--type-creator-db")
        {
            Description = "Your copy of TCDB's spreadsheet (xlsx), asked for kinds after the volume's and ClassicMac's own",
        }.AcceptExistingOnly();
        var command = new Command("info", "Show the container chain, Finder info, kinds and fork sizes") { input, database };
        command.SetAction(result => new InfoCommand(output, error).Run(
            result.GetRequiredValue(input), ContainerOptionsFrom(result), result.GetValue(strict), result.GetValue(quiet),
            result.GetValue(database)));
        return command;
    }

    private Command CheckCommand()
    {
        // The input, or a partitioned disk and a partition's name (disk.img:Two).
        var input = new Argument<string>("input")
        {
            Description = "A Mac file or a container holding them, as for info; or a partitioned disk and the name of one of its partitions (disk.img:Two)",
        };
        var json = JsonOption();
        var deep = new Option<bool>("--deep") { Description = "Also open the archives and disk images stored in the input, and check inside them" };
        var command = new Command("check", "Check the input's own structures and report their damage; a volume ClassicMac writes also gets the writer's checks")
        {
            input, json, deep,
        };
        command.SetAction(result =>
        {
            var path = result.GetRequiredValue(input);
            if (File.Exists(path))
            {
                return new CheckCommand(output, error).Run(new FileInfo(path), ContainerOptionsFrom(result), ReadOptionsFrom(result),
                    result.GetValue(strict), result.GetValue(quiet), result.GetValue(json), result.GetValue(deep));
            }

            if (MacPaths.SplitHost(path) is not var (host, rest) || MacPaths.Split(rest) is not [var partition])
            {
                error.WriteLine($"{path}: no such file, nor a partitioned disk and a partition's name.");
                return ExitCodes.NotFound;
            }

            return new CheckCommand(output, error).Run(new FileInfo(host), ContainerOptionsFrom(result), ReadOptionsFrom(result),
                result.GetValue(strict), result.GetValue(quiet), result.GetValue(json), result.GetValue(deep), partition);
        });
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
                NameEncoding = result.GetValue(encoding),
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
            Description = "Do not convert documents (DOCMaker, SimpleText and Word to HTML, AIFF sounds to WAV, PDFs as they are; in document/)",
        };
        var types = new Option<string[]>("--type", "-t")
        {
            Description = "Only resources of this type (repeatable; four characters, e.g. \"snd \", or \\xHH escapes)",
            AllowMultipleArgumentsPerToken = true,
        };
        types.Validators.Add(r =>
        {
            foreach (var type in r.GetValueOrDefault<string[]>() ?? [])
            {
                if (!FourCC.TryParse(type, out _))
                {
                    r.AddError($"'{type}' is not a four-character type.");
                }
            }
        });
        var overwrite = new Option<bool>("--overwrite") { Description = "Write into output folders that already hold files" };
        var imageFormat = ImageFormatOption();
        var loadableFonts = new Option<bool>("--loadable-fonts")
        {
            Description = "Write TrueType fonts so Windows and other modern systems load them (a Windows cmap, names, OS/2 and post added where missing)",
        };
        var command = new Command("extract", "Extract resources into a folder with a manifest")
        {
            input, outputDir, raw, keepRaw, types, overwrite, screenDepth, noDocuments, loadableFonts, imageFormat,
        };
        command.SetAction(result =>
        {
            var chosen = result.GetValue(types) is { Length: > 0 } list
                ? list.Select(FourCC.FromString).ToHashSet()                  // the validator above refused any other
                : null;
            var decodeOptions = DecodeOptionsFrom(result, screenDepth, imageFormat) with { LoadableFonts = result.GetValue(loadableFonts) };
            var decode = !result.GetValue(raw);
            return new ExtractCommand(output, error).Run(
                result.GetRequiredValue(input), result.GetValue(outputDir),
                ClassicMac.Resources.Export.ExportOptions.Default with
                {
                    NameEncoding = result.GetValue(encoding),
                    KeepRaw = result.GetValue(keepRaw),
                    Decoders = decode ? ClassicMac.Resources.Decoders.ResourceDecoders.Create(decodeOptions) : [],
                    Documents = decode && !result.GetValue(noDocuments) ? ClassicMac.Resources.Decoders.ResourceDecoders.CreateDocumentConverters(decodeOptions) : [],
                    // A file whose volume says its encoding is decoded in it (text-encodings.md §5).
                    DecodersFor = decode ? ClassicMac.Resources.Decoders.ResourceDecoders.CreateFor(decodeOptions) : null,
                    DocumentsFor = decode && !result.GetValue(noDocuments) ? ClassicMac.Resources.Decoders.ResourceDecoders.CreateDocumentConvertersFor(decodeOptions) : null,
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
        var imageFormat = ImageFormatOption();
        var command = new Command("convert", "Convert the documents inside the input: DOCMaker, SimpleText and Word to HTML folders, AIFF sounds to WAV, PDFs written out as .pdf")
        {
            input, outputDir, overwrite, screenDepth, imageFormat,
        };
        command.SetAction(result => new ConvertCommand(output, error).Run(
            result.GetRequiredValue(input), result.GetValue(outputDir),
            ClassicMac.Resources.Decoders.ResourceDecoders.CreateDocumentConverters(DecodeOptionsFrom(result, screenDepth, imageFormat)), ReadOptionsFrom(result),
            ContainerOptionsFrom(result), result.GetValue(overwrite), result.GetValue(strict), result.GetValue(quiet),
            ClassicMac.Resources.Decoders.ResourceDecoders.CreateDocumentConvertersFor(DecodeOptionsFrom(result, screenDepth, imageFormat))));
        return command;
    }

    private Command DisasmCommand()
    {
        var input = InputArgument();
        var outputDir = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "Output folder (default: \"<input> code\" next to the input)",
        };
        var cpu = new Option<string>("--cpu")
        {
            Description = "Which code: 68k (segments and 68k code resources), ppc (native code and fragments) or both",
            DefaultValueFactory = _ => "both",
        };
        cpu.AcceptOnlyFromAmong("68k", "ppc", "both");
        var overwrite = new Option<bool>("--overwrite") { Description = "Write into an output folder that already holds files" };
        var command = new Command("disasm", "Disassemble the code inside the input: a listing per segment, code resource and fragment, and code.json")
        {
            input, outputDir, cpu, overwrite,
        };
        command.SetAction(result => new DisasmCommand(output, error).Run(
            result.GetRequiredValue(input), result.GetValue(outputDir),
            result.GetValue(cpu) switch
            {
                "68k" => ClassicMac.Resources.Decoders.Code.CodeCpu.M68k,
                "ppc" => ClassicMac.Resources.Decoders.Code.CodeCpu.PowerPC,
                _ => ClassicMac.Resources.Decoders.Code.CodeCpu.Both,
            },
            result.GetValue(overwrite), ReadOptionsFrom(result), ContainerOptionsFrom(result), result.GetValue(strict), result.GetValue(quiet)));
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

    // The file commands on Mac paths (docs/cli.md §2).
    private static Argument<string> MacPathArgument() => new("path")
    {
        Description = "A Mac path: a host file, then Mac names joined by ':' or '/' (\\ escapes them), through disk images and archives, " +
            "and on to #rsrc:'TYPE':ID (docs/cli.md)",
    };

    private static Option<bool> JsonOption() => new("--json") { Description = "Write JSON (the schemas are in docs/cli.md)" };

    private PathCommands Paths(ParseResult result, Option<bool>? follow = null) => new(output, error, binary ?? Stream.Null, ContainerOptionsFrom(result), ReadOptionsFrom(result),
        result.GetValue(strict), result.GetValue(quiet))
    {
        Follow = follow is not null && result.GetValue(follow),
        TextEncoding = result.GetValue(encoding),
    };

    private static Option<bool> FollowOption() => new("--follow") { Description = "An alias file stands for its original, a symbolic link for its target (through aliases and links of each other)" };

    private Command LsCommand()
    {
        var path = MacPathArgument();
        var json = JsonOption();
        var follow = FollowOption();
        var command = new Command("ls", "List what a Mac path holds: a folder's or container's files and folders, a fork's types, a type's resources") { path, json, follow };
        command.SetAction(result => Paths(result, follow).Ls(result.GetRequiredValue(path), result.GetValue(json)));
        return command;
    }

    private Command StatCommand()
    {
        var path = MacPathArgument();
        var json = JsonOption();
        var command = new Command("stat", "Show everything about a Mac path: kind, type and creator, Finder kind, an alias's original, forks, dates, flags, how it was read") { path, json };
        command.SetAction(result => Paths(result).Stat(result.GetRequiredValue(path), result.GetValue(json)));
        return command;
    }

    internal enum ForkChoice
    {
        Data,
        Rsrc,
    }

    private Command DerezCommand()
    {
        var path = MacPathArgument();
        var outputFile = new Option<FileInfo>("--output", "-o") { Description = "Write the source to this file (default: standard output)" };
        var portable = new Option<bool>("--portable") { Description = "Source both MPW's and Retro68's Rez compile alike (ASCII, LF), instead of MPW DeRez's" };
        var rawNames = new Option<bool>("-e") { Description = "Names and types unescaped, as DeRez -e prints them (MPW only)" };
        var command = new Command("derez", "Write a file's resource fork as Rez source, as MPW's DeRez does")
        {
            path, outputFile, portable, rawNames,
        };
        command.SetAction(result => Paths(result).Derez(result.GetRequiredValue(path), new ClassicMac.Resources.Rez.RezOptions
        {
            Dialect = result.GetValue(portable) ? ClassicMac.Resources.Rez.RezDialect.Portable : ClassicMac.Resources.Rez.RezDialect.Mpw,
            RawNames = result.GetValue(rawNames),
        }, result.GetValue(outputFile)?.FullName));
        return command;
    }

    private Command RezCommand()
    {
        var path = MacPathArgument();
        var outputFile = new Option<FileInfo>("--output", "-o") { Description = "The resource fork to write (raw, as a .rsrc file)", Required = true };
        var retro68 = new Option<bool>("--retro68") { Description = "Retro68's escapes: \\n is $0A and \\r $0D (MPW's is the other way round)" };
        var command = new Command("rez", "Compile Rez source (data, read and include statements) into a resource fork, as MPW's Rez does")
        {
            path, outputFile, retro68,
        };
        command.SetAction(result => Paths(result).Rez(result.GetRequiredValue(path), result.GetRequiredValue(outputFile).FullName, result.GetValue(retro68)));
        return command;
    }

    private Command CatCommand()
    {
        var path = MacPathArgument();
        var json = JsonOption();
        var hex = new Option<bool>("--hex") { Description = "A hex dump instead of text (or a decoded resource)" };
        var raw = new Option<bool>("--raw") { Description = "The bytes themselves to standard output" };
        var fork = new Option<ForkChoice>("--fork") { Description = "Which fork of a file", DefaultValueFactory = _ => ForkChoice.Data };
        var follow = FollowOption();
        var maxBytes = new Option<long>("--max-bytes")
        {
            Description = "The most bytes shown (bytes, or with KiB/MiB/GiB)",
            DefaultValueFactory = _ => 16L << 20,
            CustomParser = ParseSize,
        };
        var command = new Command("cat", "Show a file's text (Mac OS Roman as UTF-8), a hex dump, its raw bytes, or a resource decoded")
        {
            path, json, hex, raw, fork, maxBytes, follow,
        };
        command.Validators.Add(r =>
        {
            if (r.GetValue(raw) && (r.GetValue(json) || r.GetValue(hex)))
            {
                r.AddError("--raw writes the bytes themselves; it goes with neither --json nor --hex.");
            }
        });
        command.SetAction(result => Paths(result, follow).Cat(result.GetRequiredValue(path), result.GetValue(hex), result.GetValue(raw),
            result.GetValue(fork) == ForkChoice.Rsrc ? MacFork.Resource : MacFork.Data, result.GetValue(maxBytes), result.GetValue(json)));
        return command;
    }

    internal enum KindChoice
    {
        Folder,
        File,
        Container,
    }

    private Command FindCommand()
    {
        var path = MacPathArgument();
        var json = JsonOption();
        var name = new Option<string>("--name") { Description = "A name pattern: * any characters, ? one; case ignored as HFS ignores it" };
        var type = FourCCOption("--type", "The file type");
        var creator = FourCCOption("--creator", "The file creator");
        var kind = new Option<KindChoice?>("--kind") { Description = "Folders, files or containers only" };
        var resourceType = FourCCOption("--resource-type", "Files whose resource fork holds this type");
        var contains = new Option<string>("--contains") { Description = "Files either of whose forks holds this text (Mac OS Roman)" };
        var containsHex = new Option<string>("--contains-hex") { Description = "Files either of whose forks holds these bytes (hex, spaces allowed)" };
        containsHex.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<string>() is { } text && !TryHex(text, out _))
            {
                r.AddError($"'{text}' is not hex.");
            }
        });
        var maxDepth = new Option<int>("--max-depth") { Description = "How many levels of containers are entered", DefaultValueFactory = _ => 8 };
        var limit = new Option<int>("--limit") { Description = "The most matches listed", DefaultValueFactory = _ => 1000 };
        var command = new Command("find", "Find folders and files below a Mac path, through containers")
        {
            path, json, name, type, creator, kind, resourceType, contains, containsHex, maxDepth, limit,
        };
        command.SetAction(result =>
        {
            FourCC? Code(Option<string> option) => result.GetValue(option) is { } text && FourCC.TryParse(text, out var code) ? code : null;
            byte[]? needle = result.GetValue(contains) is { } c ? MacEncodings.Encode(c, result.GetValue(encoding))
                : result.GetValue(containsHex) is { } h && TryHex(h, out var bytes) ? bytes : null;
            var query = new MacFindQuery
            {
                Name = result.GetValue(name),
                Type = Code(type),
                Creator = Code(creator),
                Kind = result.GetValue(kind) switch
                {
                    KindChoice.Folder => MacPathKind.Folder,
                    KindChoice.File => MacPathKind.File,
                    KindChoice.Container => MacPathKind.Container,
                    _ => null,
                },
                ResourceType = Code(resourceType),
                Contains = needle,
                MaxDepth = result.GetValue(maxDepth),
            };
            return Paths(result).Find(result.GetRequiredValue(path), query, result.GetValue(limit), result.GetValue(json));
        });
        return command;
    }

    private static Option<string> FourCCOption(string name, string description)
    {
        var option = new Option<string>(name) { Description = description + " (four characters, or \\xHH escapes)" };
        option.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<string>() is { } text && !FourCC.TryParse(text, out _))
            {
                r.AddError($"'{text}' is not a four-character code.");
            }
        });
        return option;
    }

    internal static bool TryHex(string text, out byte[] bytes)
    {
        bytes = [];
        var digits = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (digits.Length == 0 || digits.Length % 2 != 0 || !digits.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        bytes = Convert.FromHexString(digits);
        return true;
    }

    internal enum GetFormatChoice
    {
        AppleDouble,
        Basilisk,
        MacBinary,
        Raw,
        BinHex,
        Text,
    }

    private Command GetCommand()
    {
        var path = MacPathArgument();
        var json = JsonOption();
        var outputDir = new Option<DirectoryInfo>("--output", "-o") { Description = "The host folder written to (default: the current folder)" };
        var format = new Option<GetFormatChoice>("--as")
        {
            Description = "How a file is written: an AppleDouble pair, Basilisk II folders, a MacBinary III file, the forks raw (.rsrc), a BinHex file (.hqx), or the data fork as UTF-8 text (.txt)",
            DefaultValueFactory = _ => GetFormatChoice.AppleDouble,
        };
        var overwrite = new Option<bool>("--overwrite") { Description = "Replace existing files" };
        var enter = new Option<bool>("--enter") { Description = "Write a container's contents as a folder instead of the container file" };
        var follow = FollowOption();
        var command = new Command("get", "Copy a file (both forks), folder or resource from a Mac path to the host") { path, json, outputDir, format, overwrite, enter, follow };
        command.SetAction(result => Paths(result, follow).Get(result.GetRequiredValue(path), result.GetValue(outputDir)?.FullName ?? Directory.GetCurrentDirectory(),
            result.GetValue(format) switch
            {
                GetFormatChoice.Basilisk => MacGetFormat.Basilisk,
                GetFormatChoice.MacBinary => MacGetFormat.MacBinary,
                GetFormatChoice.Raw => MacGetFormat.Raw,
                GetFormatChoice.BinHex => MacGetFormat.BinHex,
                GetFormatChoice.Text => MacGetFormat.Text,
                _ => MacGetFormat.AppleDouble,
            }, result.GetValue(overwrite), result.GetValue(enter), result.GetValue(json)));
        return command;
    }

    private static Option<int> ScreenDepthOption()
    {
        var screenDepth = new Option<int>("--screen-depth")
        {
            Description = "Screen depth pictures are drawn at: 32 (full colour), or 1, 2, 4, 8 or 16 bits with QuickDraw's colour matching and dithering",
            DefaultValueFactory = _ => ClassicMac.Resources.Decoders.DecodeOptions.Default.ScreenDepth,
        };
        screenDepth.AcceptOnlyFromAmong("1", "2", "4", "8", "16", "32");
        return screenDepth;
    }

    private static Option<string> ImageFormatOption()
    {
        var format = new Option<string>("--image-format")
        {
            Description = "How images are written: png (the default) or webp (lossless WebP, usually smaller)",
            DefaultValueFactory = _ => "png",
        };
        format.AcceptOnlyFromAmong("png", "webp");
        return format;
    }

    private ClassicMac.Resources.Decoders.DecodeOptions DecodeOptionsFrom(ParseResult result, Option<int> screenDepth, Option<string>? imageFormat = null) => ClassicMac.Resources.Decoders.DecodeOptions.Default with
    {
        ImageEncoder = imageFormat is not null && result.GetValue(imageFormat) == "webp"
            ? ClassicMac.Resources.Decoders.Images.WebPEncoder.Instance
            : ClassicMac.Resources.Decoders.Images.PngEncoder.Instance,
        ScreenDepth = result.GetValue(screenDepth),
        QuickDraw = ReadOptionsFrom(result).ResourceManager,
        TextEncoding = result.GetValue(encoding),
    };

    // --encoding: an encoding's name, as MacEncodings.TryParse reads it.
    internal static MacTextEncoding ParseEncoding(ArgumentResult result)
    {
        var text = result.Tokens.Count == 1 ? result.Tokens[0].Value : string.Empty;
        if (MacEncodings.TryParse(text, out var parsed))
        {
            return parsed;
        }

        result.AddError($"'{text}' is not a Mac encoding: use roman, japanese, chinese-traditional, chinese-simplified, korean, arabic, hebrew, "
            + "greek, cyrillic, ukrainian, thai, central-european, croatian, romanian, icelandic or turkish.");
        return MacTextEncoding.Roman;
    }

    // Sizes: plain bytes, or a number with KiB, MiB or GiB (also K, M, G), case-insensitive.
    internal static long ParseSize(ArgumentResult result)
    {
        var text = result.Tokens.Count == 1 ? result.Tokens[0].Value : string.Empty;
        if (ByteSize.TryParse(text, out var size))
        {
            return size;
        }

        result.AddError($"'{text}' is not a size: use bytes, or a number with KiB, MiB or GiB.");
        return 0;
    }
}
