using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;

namespace ClassicMac.Resources.Cli
{
    // The write commands on Mac paths (docs/cli.md §3): put, mkdir, rm, rename, set, res-add and res-rm. Each opens the
    // host file the path starts with in an InputEditSession, makes one change, prints the planned changes, and writes a
    // new file (-o) or, only with --in-place, the input itself; --dry-run writes nothing.
    internal sealed partial class CommandLine
    {
        private sealed record WriteOptions(Option<FileInfo> Output, Option<bool> InPlace, Option<bool> DryRun, Option<bool> Json);

        private static WriteOptions NewWriteOptions() => new(
            new Option<FileInfo>("--output", "-o") { Description = "The new file to write; the input is not changed" },
            new Option<bool>("--in-place") { Description = "Change the input itself, keeping the original as <input>.orig" },
            new Option<bool>("--dry-run") { Description = "Print the changes without writing anything" },
            new Option<bool>("--json") { Description = "Print the changes as JSON (docs/cli.md §3.3)" });

        private static void AddWriteOptions(Command command, WriteOptions options)
        {
            command.Options.Add(options.Output);
            command.Options.Add(options.InPlace);
            command.Options.Add(options.DryRun);
            command.Options.Add(options.Json);
        }

        private IEnumerable<Command> WriteCommands()
        {
            yield return PutCommand();
            yield return MkdirCommand();
            yield return RmCommand();
            yield return RenameCommand();
            yield return MvCommand();
            yield return LockCommand("lock", "Lock a file (an HFS folder has no lock)", true);
            yield return LockCommand("unlock", "Unlock a file", false);
            yield return BlessCommand();
            yield return SetCommand();
            yield return ResAddCommand();
            yield return ResRmCommand();
        }

        private static Argument<string> MacPathArgument(string name, string description) => new(name) { Description = description };

        private Command PutCommand()
        {
            var source = new Argument<FileInfo>("source")
            {
                Description = "The host file to add: with its AppleDouble or Basilisk II companions, a MacBinary or AppleSingle file unwrapped, else its bytes as the data fork",
            }.AcceptExistingOnly();
            var destination = MacPathArgument("destination", "A folder to add it to (keeping its name), or the new file's Mac path");
            var name = new Option<string>("--name") { Description = "The Mac name to give it" };
            var type = new Option<string>("--type") { Description = "Its file type (four characters)" };
            var creator = new Option<string>("--creator") { Description = "Its creator (four characters)" };
            var options = NewWriteOptions();
            var command = new Command("put", "Add a host file to a volume image") { source, destination, name, type, creator };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(destination), (_, tree, rest) =>
                MacEdits.Put(tree, rest, HostImport.Read(result.GetRequiredValue(source).FullName, ContainerOptionsFrom(result)),
                    result.GetValue(name), result.GetValue(type), result.GetValue(creator))));
            return command;
        }

        private Command MkdirCommand()
        {
            var path = MacPathArgument("path", "The new folder's Mac path");
            var options = NewWriteOptions();
            var command = new Command("mkdir", "Make a folder in a volume image") { path };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (_, tree, rest) => MacEdits.Mkdir(tree, rest)));
            return command;
        }

        private Command RmCommand()
        {
            var path = MacPathArgument("path", "The file or folder to delete");
            var recursive = new Option<bool>("--recursive", "-r") { Description = "Delete a folder with everything in it" };
            var options = NewWriteOptions();
            var command = new Command("rm", "Delete a file or folder from a volume image") { path, recursive };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path),
                (_, tree, rest) => MacEdits.Rm(tree, rest, result.GetValue(recursive))));
            return command;
        }

        private Command RenameCommand()
        {
            var path = MacPathArgument("path", "The file or folder to rename");
            var newName = new Argument<string>("name") { Description = "Its new name" };
            var options = NewWriteOptions();
            var command = new Command("rename", "Rename a file or folder in its folder") { path, newName };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path),
                (kind, tree, rest) => MacEdits.Rename(kind, tree, rest, result.GetRequiredValue(newName))));
            return command;
        }

        private Command MvCommand()
        {
            var path = MacPathArgument("path", "The file or folder to move");
            var folder = new Argument<string>("folder")
            {
                Description = "The folder to move it into: a path inside the same input, or a Mac path starting with the input (disk.img: for the top level)",
            };
            var options = NewWriteOptions();
            var command = new Command("mv", "Move a file or folder into another folder of its volume") { path, folder };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path),
                (_, tree, rest) => MacEdits.Mv(tree, rest, InSameInput(result.GetRequiredValue(path), result.GetRequiredValue(folder)))));
            return command;
        }

        // A destination typed for mv: a Mac path starting with the same host file gives its path inside it; one starting
        // with another file is refused; anything else is already a path inside the input.
        private static string InSameInput(string source, string typed)
        {
            if (MacPaths.SplitHost(typed) is not var (host, rest))
            {
                return typed;
            }

            var (sourceHost, _) = MacPaths.SplitHost(source)!.Value;
            if (!string.Equals(Path.GetFullPath(host), Path.GetFullPath(sourceHost), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new WriteRefused($"{typed} is in another input; mv moves within one volume.");
            }

            return rest;
        }

        private Command LockCommand(string name, string description, bool locked)
        {
            var path = MacPathArgument("path", "The file");
            var options = NewWriteOptions();
            var command = new Command(name, description) { path };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (_, tree, rest) => MacEdits.Lock(tree, rest, locked)));
            return command;
        }

        private Command BlessCommand()
        {
            var path = MacPathArgument("folder", "The folder holding the System file");
            var options = NewWriteOptions();
            var command = new Command("bless", "Make a folder the volume's System Folder (it must hold a System file)") { path };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (_, tree, rest) => MacEdits.Bless(tree, rest)));
            return command;
        }

        private Command SetCommand()
        {
            var path = MacPathArgument("path", "The file or folder");
            var type = new Option<string>("--type") { Description = "A file's type (four characters)" };
            var creator = new Option<string>("--creator") { Description = "A file's creator (four characters)" };
            var flags = new Option<string>("--flags")
            {
                Description = "The Finder flags: a number (0x4000, $4000) or flag names joined with commas (Invisible,HasBundle); replaces them all",
            };
            var options = NewWriteOptions();
            var command = new Command("set", "Set a file's type, creator and Finder flags, or a folder's Finder flags") { path, type, creator, flags };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (kind, tree, rest) =>
                MacEdits.Set(kind, tree, rest, result.GetValue(type), result.GetValue(creator), result.GetValue(flags))));
            return command;
        }

        private Command ResAddCommand()
        {
            var path = MacPathArgument("path", "The resource's Mac path: the file, #rsrc, the type and the ID (disk.img:File:#rsrc:'STR ':128)");
            var data = new Argument<FileInfo>("data") { Description = "A host file holding the resource's data" }.AcceptExistingOnly();
            var name = new Option<string>("--name") { Description = "The resource's name" };
            var replace = new Option<bool>("--replace") { Description = "Replace the resource when it exists" };
            var options = NewWriteOptions();
            var command = new Command("res-add", "Add a resource to a file, or replace one") { path, data, name, replace };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (kind, tree, rest) =>
                MacEdits.ResAdd(kind, tree, rest, File.ReadAllBytes(result.GetRequiredValue(data).FullName), result.GetValue(name), result.GetValue(replace))));
            return command;
        }

        private Command ResRmCommand()
        {
            var path = MacPathArgument("path", "The resource's Mac path (disk.img:File:#rsrc:'STR ':128)");
            var options = NewWriteOptions();
            var command = new Command("res-rm", "Delete a resource from a file") { path };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (kind, tree, rest) => MacEdits.ResRm(kind, tree, rest)));
            return command;
        }

        // Opens the host file a path starts with, resolves the path, makes the change, prints it and saves.
        private int RunWrite(System.CommandLine.ParseResult result, WriteOptions options, string path,
            Func<InputEditKind, MacPathTree, string, Action<InputEditSession>> plan)
        {
            var target = result.GetValue(options.Output);
            var inPlace = result.GetValue(options.InPlace);
            var dryRun = result.GetValue(options.DryRun);
            var json = result.GetValue(options.Json);
            if (!dryRun && target is null && !inPlace)
            {
                error.WriteLine("Give -o <new file> to write a new file, or --in-place to change the input (or --dry-run to see the changes).");
                return ExitCodes.Usage;
            }

            if (target is not null && inPlace)
            {
                error.WriteLine("Give -o or --in-place, not both.");
                return ExitCodes.Usage;
            }

            if (MacPaths.SplitHost(path) is not var (host, rest))
            {
                error.WriteLine($"{path}: no host file (the path starts with no existing file).");
                return ExitCodes.NotFound;
            }

            var diagnostics = new List<Diagnostic>();
            InputEditSession session;
            IReadOnlyList<string> written = [];
            try
            {
                using var tree = MacPathTree.Open(host, ContainerOptionsFrom(result), ReadOptionsFrom(result), diagnostics);
                session = InputEditSession.Open(host, ContainerOptionsFrom(result), ReadOptionsFrom(result), diagnostics);
                var change = plan(session.Kind, tree, rest);
                if (session.Kind == InputEditKind.ReadOnly)
                {
                    throw new WriteRefused($"{Path.GetFileName(host)} cannot be changed: ClassicMac writes plain HFS volume images and single Mac files.");
                }

                change(session);
                if (!dryRun)
                {
                    if (inPlace)
                    {
                        session.SaveInPlace();
                        written = [session.Path];
                    }
                    else
                    {
                        written = session.SaveAs(target!.FullName);
                    }
                }
            }
            catch (PathNotFound e)
            {
                error.WriteLine($"{path}: {e.Message}");
                return ExitCodes.NotFound;
            }
            catch (Exception e) when (e is WriteRefused or InvalidOperationException or InvalidDataException or ArgumentException or EndOfStreamException)
            {
                error.WriteLine($"{path}: {e.Message}");
                return ExitCodes.Usage;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                error.WriteLine($"{path}: {e.Message}");
                return ExitCodes.IoError;
            }

            if (json)
            {
                WriteJson(session, dryRun, written);
            }
            else
            {
                foreach (var planned in session.Changes)
                {
                    output.WriteLine(planned.Detail.Length > 0 ? $"{planned.Action} {planned.Path} ({planned.Detail})" : $"{planned.Action} {planned.Path}");
                }

                foreach (var file in written)
                {
                    output.WriteLine($"Wrote {file}");
                }

                if (dryRun)
                {
                    output.WriteLine("Dry run: nothing written.");
                }
            }

            return ExitCodes.Success;
        }

        private void WriteJson(InputEditSession session, bool dryRun, IReadOnlyList<string> written) =>
            output.WriteLine(MacPathJson.Document(w => MacPathJson.Changes(w, session.Path, dryRun, written, session.Changes)));
    }
}
