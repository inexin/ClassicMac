using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Editing;

namespace ClassicMac.Resources.Cli.Mcp
{
    /// <summary>
    /// An input opened by the MCP server (docs/cli.md §4): a tree for reading its Mac paths and the changes made to it.
    /// Each change is made on a working copy in a temporary folder, so later reads show it; the input changes only when
    /// saved in place, and Save As writes a new file. Both make the changes again on the input itself, so what is written
    /// is the library's verified save.
    /// </summary>
    internal sealed class MacMcpSession : IDisposable
    {
        private readonly ContainerReadOptions options;
        private readonly ReadOptions readOptions;
        private readonly string work;
        private readonly List<Action<InputEditSession>> edits = [];
        private readonly List<PlannedChange> changes = [];
        private int copies;
        private int saved;
        private string current;

        private MacMcpSession(string id, string input, InputEditKind kind, ContainerReadOptions options, ReadOptions readOptions)
        {
            Id = id;
            Input = input;
            Kind = kind;
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
        public int Unsaved => edits.Count - saved;

        /// <summary>Opens a host file; refused (not found) when there is none.</summary>
        public static MacMcpSession Open(string id, string path, ContainerReadOptions options, ReadOptions readOptions)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                throw new PathNotFound($"{path}: no host file.");
            }

            var kind = InputEditSession.Open(full, options, readOptions).Kind;
            return new MacMcpSession(id, full, kind, options, readOptions);
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

            var session = InputEditSession.Open(current, options, readOptions);
            change(session);
            var made = session.Changes.ToList();
            if (!dryRun)
            {
                var folder = Path.Combine(work, (++copies).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, Path.GetFileName(Input));
                session.SaveAs(copy);
                Tree = MacPathTree.Open(copy, options, readOptions);
                current = copy;
                edits.Add(change);
                changes.AddRange(made);
            }

            return made;
        }

        /// <summary>Writes the input with every change to a new file (verified); returns the files written.</summary>
        public IReadOnlyList<string> SaveAs(string destination)
        {
            var session = Replayed();
            var written = session.SaveAs(destination);
            saved = edits.Count;
            return written;
        }

        /// <summary>Writes the changes over the input (verified), keeping the original as <c>.orig</c> the first time.</summary>
        public void SaveInPlace()
        {
            Replayed().SaveInPlace();
            Tree = MacPathTree.Open(Input, options, readOptions);
            current = Input;
            edits.Clear();
            changes.Clear();
            saved = 0;
        }

        // A session of the input with the changes made again.
        private InputEditSession Replayed()
        {
            if (Kind == InputEditKind.ReadOnly)
            {
                throw new WriteRefused($"{Path.GetFileName(Input)} cannot be changed: ClassicMac writes plain HFS volume images and single Mac files.");
            }

            var session = InputEditSession.Open(Input, options, readOptions);
            foreach (var edit in edits)
            {
                edit(session);
            }

            return session;
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
}
