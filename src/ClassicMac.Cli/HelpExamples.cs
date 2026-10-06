using System.Collections.Generic;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine;
using System.Linq;
using System.Text;
using System;

namespace ClassicMac.Cli;

/// <summary>An example in a command's help: what it does, then the command line.</summary>
internal sealed record HelpExample(string Description, string CommandLine);

/// <summary>
/// The examples each command's help ends with (docs/cli.md §6), and the <c>help</c> command. The root's examples (under
/// the empty name) are a tour of the common commands.
/// </summary>
internal static class HelpExamples
{
    public static IReadOnlyDictionary<string, IReadOnlyList<HelpExample>> All { get; } = new Dictionary<string, IReadOnlyList<HelpExample>>(StringComparer.Ordinal)
    {
        [""] =
        [
            new("List what a disk image holds", "classicmac ls \"Mac OS 9.hfv:System Folder\""),
            new("Extract an application's resources as modern files", "classicmac extract \"App.rsrc\" -o out"),
            new("Convert the documents in an archive to HTML", "classicmac convert \"Manuals.sit\" -o manuals"),
            new("Check a volume as Disk First Aid does", "classicmac check \"disk.img\""),
            new("Show a command's help", "classicmac help extract"),
        ],
        ["info"] =
        [
            new("The container chain, Finder info and forks of a file", "classicmac info \"Game.sit\""),
        ],
        ["list"] =
        [
            new("The files and resources inside a disk image", "classicmac list \"System 7.5.img\""),
            new("The same as JSON", "classicmac list \"App.bin\" --format json"),
        ],
        ["unpack"] =
        [
            new("Every file in an archive, with AppleDouble companions", "classicmac unpack \"Game.sit\" -o files"),
            new("As a Basilisk II shared folder", "classicmac unpack \"disk.img\" -o shared --layout basilisk"),
        ],
        ["extract"] =
        [
            new("Every resource, decoded, with a manifest", "classicmac extract \"App.rsrc\" -o out"),
            new("Only pictures and sounds, as on an 8-bit screen", "classicmac extract \"Game.sit\" -o out -t PICT -t \"snd \" --screen-depth 8"),
            new("Images as WebP, fonts as loadable TrueType", "classicmac extract \"Fonts.bin\" -o out --image-format webp --loadable-fonts"),
        ],
        ["convert"] =
        [
            new("A SimpleText or DOCMaker document to HTML", "classicmac convert \"Read Me\" -o readme"),
            new("Every document on a CD", "classicmac convert \"CD.iso\" -o docs"),
        ],
        ["disasm"] =
        [
            new("An application's code, 68k and PowerPC", "classicmac disasm \"MyApp.bin\" -o listing"),
            new("Only the PowerPC fragment", "classicmac disasm \"MyApp.bin\" -o listing --cpu ppc"),
        ],
        ["pack"] =
        [
            new("Rebuild a resource fork from an extract folder", "classicmac pack out -o \"App.rsrc\""),
            new("As a MacBinary file, with the original's data fork", "classicmac pack out -o \"App.bin\" --container macbinary --base \"App.bin\""),
        ],
        ["ls"] =
        [
            new("A folder on a disk image", "classicmac ls \"Mac OS 9.hfv:System Folder\""),
            new("A file's resource types", "classicmac ls \"Mac OS 9.hfv:System Folder:Finder:#rsrc\""),
        ],
        ["stat"] =
        [
            new("Everything about a file", "classicmac stat \"disk.img:Applications:SimpleText\""),
            new("The same as JSON", "classicmac stat \"disk.img:Applications:SimpleText\" --json"),
        ],
        ["cat"] =
        [
            new("A text file", "classicmac cat \"disk.img:Read Me\""),
            new("A resource, decoded", "classicmac cat \"disk.img:SimpleText:#rsrc:'vers':1\""),
            new("A resource fork as hex", "classicmac cat \"disk.img:SimpleText\" --fork rsrc --hex"),
        ],
        ["derez"] =
        [
            new("A resource fork as Rez source", "classicmac derez \"App.rsrc\" -o App.r"),
        ],
        ["rez"] =
        [
            new("Compile Rez source into a resource fork", "classicmac rez App.r -o \"App.rsrc\""),
        ],
        ["find"] =
        [
            new("Applications anywhere on a disk", "classicmac find \"Mac OS 9.hfv\" --type APPL"),
            new("Text files by name", "classicmac find \"disk.img:Documents\" --name \"*.txt\""),
            new("Files with sound resources", "classicmac find \"Games.sit\" --resource-type \"snd \""),
        ],
        ["get"] =
        [
            new("Copy a file out, with AppleDouble companions", "classicmac get \"disk.img:Applications:SimpleText\" -o ."),
            new("As a MacBinary file", "classicmac get \"disk.img:Applications:SimpleText\" -o . --as macbinary"),
        ],
        ["check"] =
        [
            new("Check a disk image", "classicmac check \"disk.img\""),
            new("The same as JSON", "classicmac check \"disk.img\" --json"),
        ],
        ["put"] =
        [
            new("Add a text file to a folder, in place", "classicmac put notes.txt \"disk.img:Documents\" --text --in-place"),
            new("Add a file as a new image", "classicmac put game.bin \"disk.img:Games\" -o new.img"),
        ],
        ["mkdir"] =
        [
            new("Make a folder", "classicmac mkdir \"disk.img:Documents:Letters\" --in-place"),
        ],
        ["rm"] =
        [
            new("See what deleting a folder would do", "classicmac rm \"disk.img:Old Stuff\" -r --dry-run"),
            new("Delete it", "classicmac rm \"disk.img:Old Stuff\" -r --in-place"),
        ],
        ["rename"] =
        [
            new("Rename a folder", "classicmac rename \"disk.img:Untitled folder\" Letters --in-place"),
        ],
        ["mv"] =
        [
            new("Move a folder to the top level", "classicmac mv \"disk.img:Documents:Letters\" \"disk.img:\" --in-place"),
        ],
        ["lock"] =
        [
            new("Lock a file", "classicmac lock \"disk.img:Documents:Letter\" --in-place"),
        ],
        ["unlock"] =
        [
            new("Unlock a file", "classicmac unlock \"disk.img:Documents:Letter\" --in-place"),
        ],
        ["bless"] =
        [
            new("Make a folder the System Folder", "classicmac bless \"disk.img:System Folder\" --in-place"),
        ],
        ["format"] =
        [
            new("A blank 1.4 MB floppy image", "classicmac format blank.img --size 1440K --name Untitled"),
            new("A 100 MB volume", "classicmac format big.img --size 100M --name Work"),
        ],
        ["ndif"] =
        [
            new("A Disk Copy 6 image of a disk image", "classicmac ndif disk.img \"Disk.smi\""),
            new("Compressed, in three parts", "classicmac ndif disk.img \"Disk.img\" --format adc --segments 3"),
        ],
        ["resize"] =
        [
            new("Grow a volume to 100 MB", "classicmac resize disk.img --size 100M --in-place"),
        ],
        ["repair"] =
        [
            new("See what a repair would change", "classicmac repair disk.img --dry-run"),
            new("Repair into a new image", "classicmac repair disk.img -o fixed.img"),
        ],
        ["defrag"] =
        [
            new("Defragment into a new image", "classicmac defrag disk.img -o tidy.img"),
        ],
        ["set"] =
        [
            new("Make a file a SimpleText document", "classicmac set \"disk.img:Read Me\" --type TEXT --creator ttxt --in-place"),
            new("Make a file invisible", "classicmac set \"disk.img:Desktop DB\" --flags Invisible --in-place"),
        ],
        ["res-add"] =
        [
            new("Add or replace a picture", "classicmac res-add \"App.rsrc:#rsrc:'PICT':128\" picture.pict --replace --in-place"),
        ],
        ["res-rm"] =
        [
            new("Delete a resource", "classicmac res-rm \"App.rsrc:#rsrc:'PICT':128\" --in-place"),
        ],
        ["mcp"] =
        [
            new("Serve the file commands to an MCP client (it starts this itself)", "classicmac mcp"),
        ],
        ["shell"] =
        [
            new("A shell on a disk image", "classicmac shell \"Mac OS 9.hfv\""),
            new("Run commands from a file", "classicmac shell \"disk.img\" --script commands.txt"),
        ],
        ["help"] =
        [
            new("The commands", "classicmac help"),
            new("One command's help", "classicmac help extract"),
        ],
    };

    /// <summary>Makes the help option (the root's, which every command shares) add the examples after the help.</summary>
    public static void AddTo(RootCommand root)
    {
        var option = root.Options.OfType<HelpOption>().First();
        option.Action = new WithExamples((HelpAction)option.Action!);
    }

    /// <summary>The examples block for <paramref name="examples"/>.</summary>
    public static string Format(IReadOnlyList<HelpExample> examples)
    {
        var text = new StringBuilder();
        text.AppendLine("Examples:");
        foreach (var example in examples)
        {
            text.Append("  ").Append(example.Description).AppendLine(":");
            text.Append("    ").AppendLine(example.CommandLine);
        }

        text.AppendLine();
        return text.ToString();
    }

    /// <summary>A command line split into words as a shell splits it: spaces separate, double quotes group.</summary>
    public static IEnumerable<string> Words(string commandLine)
    {
        var word = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (any)
                {
                    yield return word.ToString();
                }

                word.Clear();
                any = false;
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }

        if (any)
        {
            yield return word.ToString();
        }
    }

    // The help, then the examples of the command it is for.
    private sealed class WithExamples(HelpAction help) : SynchronousCommandLineAction
    {
        // As the help action it wraps: --help works whatever else the command line lacks.
        public override bool ClearsParseErrors => help.ClearsParseErrors;

        public override int Invoke(ParseResult parseResult)
        {
            var code = help.Invoke(parseResult);
            var command = parseResult.CommandResult.Command;
            var name = command is RootCommand ? "" : command.Name;
            if (All.TryGetValue(name, out var list))
            {
                parseResult.InvocationConfiguration.Output.Write(Format(list));
            }

            return code;
        }
    }
}
