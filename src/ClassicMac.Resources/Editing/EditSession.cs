using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Editing
{
    /// <summary>
    /// The rules an edit must meet [ClassicMac: as ResEdit enforces them, where the Resource Manager itself checks less]:
    /// a type and ID that are new to the fork (AddResource does not check; ResEdit does) and no compressed attribute set by
    /// hand (a name is at most 255 bytes of Mac OS Roman by being a <see cref="MacString"/>). IDs below 128 are reserved for the system: allowed, with a
    /// warning.
    /// </summary>
    public static class ResourceEditRules
    {
        /// <summary>The first ID not reserved for the system.</summary>
        public const short FirstApplicationId = 128;

        /// <summary>The problems with giving a resource of <paramref name="type"/> this ID, name and attributes; empty when it is fine.</summary>
        /// <param name="fork">The fork.</param>
        /// <param name="type">The resource type.</param>
        /// <param name="id">The ID wanted.</param>
        /// <param name="name">The name wanted.</param>
        /// <param name="attributes">The attributes wanted.</param>
        /// <param name="existing">The resource being changed, whose own ID is not a clash; null for a new one.</param>
        /// <param name="wasCompressed">Whether that resource is compressed now (its compressed bit may stay).</param>
        public static IReadOnlyList<Diagnostic> Check(ResourceFork fork, FourCC type, short id, MacString? name, ResourceAttributes attributes,
            Resource? existing = null, bool wasCompressed = false)
        {
            ArgumentNullException.ThrowIfNull(fork);
            var problems = new List<Diagnostic>();
            if (fork.Find(type, id) is { } other && other != existing)
            {
                problems.Add(new Diagnostic(DiagnosticSeverity.Error, "edit.duplicate-id", $"The file already has a '{type}' {id}."));
            }

            if ((attributes & ResourceAttributes.Compressed) != 0 && !wasCompressed)
            {
                problems.Add(new Diagnostic(DiagnosticSeverity.Error, "edit.compressed", "The compressed attribute is set only by compressing the data."));
            }

            if (id < FirstApplicationId)
            {
                problems.Add(new Diagnostic(DiagnosticSeverity.Warning, "edit.reserved-id", $"IDs below {FirstApplicationId} are reserved for the system."));
            }

            return problems;
        }

        /// <summary>The first ID from 128 up that <paramref name="type"/> does not use in the fork.</summary>
        public static short NextFreeId(ResourceFork fork, FourCC type)
        {
            ArgumentNullException.ThrowIfNull(fork);
            for (int id = FirstApplicationId; id <= short.MaxValue; id++)
            {
                if (fork.Find(type, (short)id) is null)
                {
                    return (short)id;
                }
            }

            for (int id = FirstApplicationId - 1; id >= short.MinValue; id--)
            {
                if (fork.Find(type, (short)id) is null)
                {
                    return (short)id;
                }
            }

            throw new InvalidOperationException($"Every '{type}' ID is taken.");
        }
    }

    /// <summary>
    /// A resource fork being edited: the edits made, with undo and redo, and whether it differs from what was last
    /// saved. Undo keeps working across saves.
    /// </summary>
    public sealed class EditSession(ResourceFork fork)
    {
        private readonly List<IResourceEdit> done = [];
        private readonly Stack<IResourceEdit> undone = new();
        private int savedAt;

        /// <summary>The fork being edited.</summary>
        public ResourceFork Fork { get; } = fork ?? throw new ArgumentNullException(nameof(fork));

        /// <summary>Raised after every change, undo, redo or save.</summary>
        public event EventHandler? Changed;

        /// <summary>Whether the fork differs from the last save (or from the file as opened).</summary>
        public bool IsDirty => savedAt != done.Count;

        /// <summary>The edit Undo would reverse, or null.</summary>
        public IResourceEdit? NextUndo => done.Count > 0 ? done[^1] : null;

        /// <summary>The edit Redo would repeat, or null.</summary>
        public IResourceEdit? NextRedo => undone.Count > 0 ? undone.Peek() : null;

        /// <summary>Applies an edit and records it; anything undone is no longer redoable.</summary>
        public void Execute(IResourceEdit edit)
        {
            ArgumentNullException.ThrowIfNull(edit);
            edit.Apply(Fork);
            if (savedAt > done.Count)
            {
                savedAt = -1;              // the saved state was undone past: unreachable now
            }

            done.Add(edit);
            undone.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Reverses the last edit; false when there is none.</summary>
        public bool Undo()
        {
            if (done.Count == 0)
            {
                return false;
            }

            var edit = done[^1];
            edit.Undo(Fork);
            done.RemoveAt(done.Count - 1);
            undone.Push(edit);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>Repeats the last edit undone; false when there is none.</summary>
        public bool Redo()
        {
            if (undone.Count == 0)
            {
                return false;
            }

            var edit = undone.Pop();
            edit.Apply(Fork);
            done.Add(edit);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>Records that the fork as it is now has been saved.</summary>
        public void MarkSaved()
        {
            savedAt = done.Count;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Compares two forks' content, as a save's verification does.</summary>
    public static class ResourceForkComparison
    {
        /// <summary>
        /// How <paramref name="expected"/> and <paramref name="actual"/> differ in content (every resource's type, ID, name,
        /// attributes other than the in-memory changed bit, and data; the fork's attributes); empty when they match.
        /// </summary>
        public static IReadOnlyList<string> Differences(ResourceFork expected, ResourceFork actual)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(actual);
            var differences = new List<string>();
            if (expected.Attributes != actual.Attributes)
            {
                differences.Add($"the file attributes are {actual.Attributes}, not {expected.Attributes}");
            }

            foreach (var e in expected.Resources)
            {
                if (actual.Find(e.Type, e.Id) is not { } a)
                {
                    differences.Add($"{e} is missing");
                    continue;
                }
                if (e.Name != a.Name)
                {
                    differences.Add($"{e}'s name is {a.Name?.ToString() ?? "none"}");
                }

                if ((e.Attributes & ~ResourceAttributes.Changed) != (a.Attributes & ~ResourceAttributes.Changed))
                {
                    differences.Add($"{e}'s attributes are {a.Attributes}, not {e.Attributes}");
                }

                if (!e.GetData().Span.SequenceEqual(a.GetData().Span))
                {
                    differences.Add($"{e}'s data differs");
                }
            }
            foreach (var a in actual.Resources)
            {
                if (expected.Find(a.Type, a.Id) is null)
                {
                    differences.Add($"{a} should not be there");
                }
            }

            return differences;
        }
    }
}
