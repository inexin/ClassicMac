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
        // A refused write: a usage error with its message.
        private sealed class WriteRefused(string message) : Exception(message);

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
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(destination), (session, tree, entry, path) =>
            {
                var file = HostImport.Read(result.GetRequiredValue(source).FullName, ContainerOptionsFrom(result));
                var (folder, newName) = entry is { Kind: MacPathKind.Folder or MacPathKind.Container } && IsVolumeFolder(tree, entry)
                    ? (VolumePath(tree, entry), result.GetValue(name) ?? file.Name.ToMacRoman())
                    : NewItem(tree, path);
                newName = result.GetValue(name) ?? newName;
                var info = file.FinderInfo with
                {
                    Type = Code(result.GetValue(type), "type") ?? file.FinderInfo.Type,
                    Creator = Code(result.GetValue(creator), "creator") ?? file.FinderInfo.Creator,
                };
                session.AddFile(Join(folder, newName), file with { Name = MacString.FromMacRoman(newName), FinderInfo = info });
            }, mustExist: false));
            return command;
        }

        private Command MkdirCommand()
        {
            var path = MacPathArgument("path", "The new folder's Mac path");
            var options = NewWriteOptions();
            var command = new Command("mkdir", "Make a folder in a volume image") { path };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (session, tree, _, full) =>
            {
                var (folder, name) = NewItem(tree, full);
                session.AddFolder(Join(folder, name));
            }, mustExist: false));
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
                (session, tree, entry, _) => session.Delete(VolumePath(tree, entry!), result.GetValue(recursive))));
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
                (session, tree, entry, _) => session.Rename(ItemPath(session, tree, entry!), result.GetRequiredValue(newName))));
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
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (session, tree, entry, _) =>
            {
                var setFlags = result.GetValue(flags) is { } text ? Flags(text) : (FinderFlags?)null;
                var setType = Code(result.GetValue(type), "type");
                var setCreator = Code(result.GetValue(creator), "creator");
                if (setFlags is null && setType is null && setCreator is null)
                {
                    throw new WriteRefused("Give --type, --creator or --flags.");
                }

                session.SetInfo(ItemPath(session, tree, entry!), setType, setCreator, setFlags);
            }));
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
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (session, tree, _, full) =>
            {
                var (file, type, id) = ResourcePath(session, tree, full);
                if (!result.GetValue(replace) && session.Resources(file).Find(type, id) is not null)
                {
                    throw new WriteRefused($"'{type}' {id} exists; give --replace to replace it.");
                }

                var resourceName = result.GetValue(name) is { } text ? MacString.FromMacRoman(text) : (MacString?)null;
                session.SetResource(file, type, id, File.ReadAllBytes(result.GetRequiredValue(data).FullName), resourceName);
            }, mustExist: false));
            return command;
        }

        private Command ResRmCommand()
        {
            var path = MacPathArgument("path", "The resource's Mac path (disk.img:File:#rsrc:'STR ':128)");
            var options = NewWriteOptions();
            var command = new Command("res-rm", "Delete a resource from a file") { path };
            AddWriteOptions(command, options);
            command.SetAction(result => RunWrite(result, options, result.GetRequiredValue(path), (session, tree, _, full) =>
            {
                var (file, type, id) = ResourcePath(session, tree, full);
                session.DeleteResource(file, type, id);
            }, mustExist: false));
            return command;
        }

        // Opens the host file a path starts with, resolves the path, makes the change, prints it and saves.
        private int RunWrite(System.CommandLine.ParseResult result, WriteOptions options, string path,
            Action<InputEditSession, MacPathTree, MacPathEntry?, string> change, bool mustExist = true)
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
                error.WriteLine($"{path}: no file starts this path.");
                return ExitCodes.Usage;
            }

            var diagnostics = new List<Diagnostic>();
            InputEditSession session;
            IReadOnlyList<string> written = [];
            try
            {
                using var tree = MacPathTree.Open(host, ContainerOptionsFrom(result), ReadOptionsFrom(result), diagnostics);
                var entry = tree.Resolve(rest);
                if (mustExist && entry is null)
                {
                    throw new WriteRefused($"{path} names nothing.");
                }

                session = InputEditSession.Open(host, ContainerOptionsFrom(result), ReadOptionsFrom(result), diagnostics);
                if (session.Kind == InputEditKind.ReadOnly)
                {
                    throw new WriteRefused($"{Path.GetFileName(host)} cannot be changed: ClassicMac writes plain HFS volume images and single Mac files.");
                }

                change(session, tree, entry, rest);
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

        private void WriteJson(InputEditSession session, bool dryRun, IReadOnlyList<string> written)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("input", session.Path);
                writer.WriteBoolean("dryRun", dryRun);
                writer.WriteStartArray("written");
                foreach (var file in written)
                {
                    writer.WriteStringValue(file);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("changes");
                foreach (var planned in session.Changes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("action", planned.Action);
                    writer.WriteString("path", planned.Path);
                    writer.WriteString("detail", planned.Detail);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }

        // Whether an entry is the volume's root or a folder in it, not in a container inside it.
        private static bool IsVolumeFolder(MacPathTree tree, MacPathEntry entry) =>
            (entry == tree.Root || entry.Kind == MacPathKind.Folder) && InVolume(tree, entry);

        // Whether every entry between the root and this one is a folder (the path does not go into a nested container).
        private static bool InVolume(MacPathTree tree, MacPathEntry entry)
        {
            for (var at = entry.Parent; at is not null && at != tree.Root; at = at.Parent)
            {
                if (at.Kind != MacPathKind.Folder)
                {
                    return false;
                }
            }

            return true;
        }

        // An item's path in the volume: its folders and its name, as Mac OS Roman text (empty for the root).
        private static string VolumePath(MacPathTree tree, MacPathEntry entry)
        {
            if (entry == tree.Root)
            {
                return "";
            }

            if (!InVolume(tree, entry) || entry.Kind is not (MacPathKind.Folder or MacPathKind.File or MacPathKind.Container))
            {
                throw new WriteRefused($"{entry.Path} is not a file or folder of the volume ClassicMac can change.");
            }

            var names = entry.Kind == MacPathKind.Folder
                ? entry.FolderPath!.Select(n => n.ToMacRoman())
                : entry.File!.FolderPath.Select(n => n.ToMacRoman()).Append(entry.File.Name.ToMacRoman());
            return string.Join(":", names);
        }

        // A file's or folder's path for a session: in a volume, its path; for a single-file input, "".
        private static string ItemPath(InputEditSession session, MacPathTree tree, MacPathEntry entry)
        {
            if (session.Kind == InputEditKind.SingleFile)
            {
                if (entry != tree.Root && entry.Parent != tree.Root)
                {
                    throw new WriteRefused($"{entry.Path} is not the file the input holds.");
                }

                return "";
            }

            return VolumePath(tree, entry);
        }

        // A new item: its folder's path in the volume (which must exist) and its name.
        private static (string Folder, string Name) NewItem(MacPathTree tree, string rest)
        {
            var names = MacPaths.Split(rest);
            if (names.Count == 0)
            {
                throw new WriteRefused("Give the new item's name after the volume.");
            }

            var parent = tree.Resolve(string.Join(":", names.Take(names.Count - 1).Select(MacPaths.Escape)));
            if (parent is null || !IsVolumeFolder(tree, parent))
            {
                throw new WriteRefused($"There is no folder to put {names[^1]} in.");
            }

            return (VolumePath(tree, parent), names[^1]);
        }

        private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + ":" + name;

        // A resource path: the file before #rsrc, the type and the ID.
        private static (string File, FourCC Type, short Id) ResourcePath(InputEditSession session, MacPathTree tree, string rest)
        {
            var names = MacPaths.Split(rest);
            var fork = names.Count >= 3 && names[^3] == MacPaths.ResourceFork ? names.Count - 3 : -1;
            if (fork < 0)
            {
                throw new WriteRefused("Give the resource as <file>:#rsrc:<type>:<ID>.");
            }

            var typeText = names[^2].Length >= 2 && names[^2][0] == '\'' && names[^2][^1] == '\'' ? names[^2][1..^1] : names[^2];
            if (!FourCC.TryParse(typeText, out var type))
            {
                throw new WriteRefused($"{names[^2]} is not a resource type (four Mac OS Roman characters).");
            }

            if (!short.TryParse(names[^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
            {
                throw new WriteRefused($"{names[^1]} is not a resource ID (-32768 to 32767).");
            }

            var file = tree.Resolve(string.Join(":", names.Take(fork).Select(MacPaths.Escape)));
            if (file is null || file.Kind is not (MacPathKind.File or MacPathKind.Container))
            {
                throw new WriteRefused("The path before #rsrc names no file.");
            }

            return (ItemPath(session, tree, file), type, id);
        }

        private static FourCC? Code(string? text, string what)
        {
            if (text is null)
            {
                return null;
            }

            return InputEditSession.TryParseCode(text, out var code) ? code : throw new WriteRefused($"'{text}' is not a {what}: up to four Mac OS Roman characters.");
        }

        // Finder flags: a number (0x…, $…, decimal), or names joined with commas (with or without their Is/Has).
        private static FinderFlags Flags(string text)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('$'))
            {
                var hex = trimmed.StartsWith('$') ? trimmed[1..] : trimmed[2..];
                if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    return (FinderFlags)value;
                }
            }
            else if (ushort.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return (FinderFlags)number;
            }
            else
            {
                var flags = FinderFlags.None;
                foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var found = new[] { part, "Is" + part, "Has" + part }
                        .Select(name => Enum.TryParse<FinderFlags>(name, ignoreCase: true, out var flag) && !int.TryParse(name, out _) ? flag : (FinderFlags?)null)
                        .FirstOrDefault(f => f is not null);
                    flags |= found ?? throw new WriteRefused($"'{part}' is not a Finder flag ({string.Join(", ", Enum.GetNames<FinderFlags>().Where(n => n is not "None" and not "ColorMask"))}).");
                }

                return flags;
            }

            throw new WriteRefused($"'{text}' is not a set of Finder flags.");
        }
    }
}
