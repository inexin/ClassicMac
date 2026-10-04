using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;

namespace ClassicMac.Resources.Cli;

/// <summary>
/// An input opened by the MCP server or the shell (docs/cli.md §4, §5): a tree for reading its Mac paths and the
/// changes made to it.
/// Changes are made on one edit session of the input, kept for the session's life, and written to a working copy in a
/// temporary folder after each, so later reads show them; the input changes only when saved in place, and Save As writes
/// a new file, both the library's verified save of that edit session.
/// </summary>
internal sealed class PathSession : IDisposable
{
    private readonly ContainerReadOptions options;
    private readonly ReadOptions readOptions;
    private readonly string work;
    private readonly List<PlannedChange> changes = [];
    private InputEditSession? live;
    private int made;
    private int copies;
    private int saved;
    private string current;

    private PathSession(string id, string input, InputEditSession first, ContainerReadOptions options, ReadOptions readOptions)
    {
        Id = id;
        Input = input;
        Kind = first.Kind;
        live = first;
        this.options = options;
        this.readOptions = readOptions;
        current = input;
        Tree = MacPathTree.Open(input, options, readOptions);
        work = Path.Combine(Path.GetTempPath(), "classicmac-mcp-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>The session's name in tool calls.</summary>
    public string Id { get; }

    /// <summary>The input, as a full path.</summary>
    public string Input { get; }

    /// <summary>What the input lets the session change.</summary>
    public InputEditKind Kind { get; }

    /// <summary>The input as changed so far, for reading.</summary>
    public MacPathTree Tree { get; private set; }

    /// <summary>Every change made since the session opened (or since it was saved in place), in order.</summary>
    public IReadOnlyList<PlannedChange> Changes => changes;

    /// <summary>How many changes no save has written yet.</summary>
    public int Unsaved => made - saved;

    /// <summary>Opens a host file; refused (not found) when there is none.</summary>
    public static PathSession Open(string id, string path, ContainerReadOptions options, ReadOptions readOptions)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new PathNotFound($"{path}: no host file.");
        }

        return new PathSession(id, full, InputEditSession.Open(full, options, readOptions), options, readOptions);
    }

    /// <summary>
    /// Makes a change planned on the tree (or, with <paramref name="dryRun"/>, only checks it) and returns what it
    /// changed.
    /// </summary>
    public IReadOnlyList<PlannedChange> Apply(Func<InputEditKind, MacPathTree, string, Action<InputEditSession>> plan, string path, bool dryRun)
    {
        var change = plan(Kind, Tree, path);
        if (Kind == InputEditKind.ReadOnly)
        {
            throw new WriteRefused($"{Path.GetFileName(Input)} cannot be changed: ClassicMac writes plain HFS volume images and single Mac files.");
        }

        if (dryRun)
        {
            // Tried on the input as it stands: the changes made so far, in memory or in the working copy.
            var trial = live?.Current() is { } changed
                ? InputEditSession.Open(Input, changed, ContainerUnwrapper.Default.Unwrap(changed.File, HostFiles.FormatName(changed.Layout),
                    new ContainerContext(options)), options, readOptions)
                : InputEditSession.Open(current, options, readOptions);
            change(trial);
            return [.. trial.Changes];
        }

        live ??= InputEditSession.Open(Input, options, readOptions);
        var before = live.Changes.Count;
        change(live);
        var planned = live.Changes.Skip(before).ToList();
        Tree.Dispose();
        if (live.Current() is { } inMemory)
        {
            // A volume: read from memory, under the input's name, with no working copy written.
            Tree = MacPathTree.Open(Input, inMemory, options, readOptions);
            DeleteCopy(current);
            current = Input;
        }
        else
        {
            var folder = Path.Combine(work, (++copies).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(Input));
            live.SaveAs(copy);
            Tree = MacPathTree.Open(copy, options, readOptions);
            DeleteCopy(current);
            current = copy;
        }

        made++;
        changes.AddRange(planned);
        return planned;
    }

    /// <summary>Writes the input with every change to a new file (verified); returns the files written.</summary>
    public IReadOnlyList<string> SaveAs(string destination)
    {
        var written = Live().SaveAs(destination);
        saved = made;
        return written;
    }

    /// <summary>Writes the changes over the input (verified), keeping the original as <c>.orig</c> the first time.</summary>
    public void SaveInPlace()
    {
        Live().SaveInPlace();
        Tree.Dispose();
        Tree = MacPathTree.Open(Input, options, readOptions);
        DeleteCopy(current);
        current = Input;
        live = null;
        changes.Clear();
        made = 0;
        saved = 0;
    }

    // The edit session holding the changes (the input itself when none has been made).
    private InputEditSession Live()
    {
        if (Kind == InputEditKind.ReadOnly)
        {
            throw new WriteRefused($"{Path.GetFileName(Input)} cannot be changed: ClassicMac writes plain HFS volume images and single Mac files.");
        }

        return live ??= InputEditSession.Open(Input, options, readOptions);
    }

    // Deletes a working copy once nothing reads it (never the input).
    private void DeleteCopy(string path)
    {
        if (path == Input)
        {
            return;
        }

        try
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes the working copies.</summary>
    public void Dispose()
    {
        Tree.Dispose();
        if (Directory.Exists(work))
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A copy still held open elsewhere stays in the temporary folder.
            }
        }
    }
}
