using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// The browse tree's rows as one flat list (design/boards/browse-tree.md): the roots and, under each open row, its
    /// children that the filter keeps, depth first. The view shows it in one virtualized list of fixed-height rows, so the
    /// scroll extent is exactly the row count times the row height. The list follows the tree as it changes (a row opened
    /// or closed, filtered, its children added, removed or replaced, a root added or removed), each change as one
    /// replacement of the stretch of rows below the row that changed: one Remove and one Add of many items, never a
    /// Reset (which would lose the scroll position and the selection).
    /// </summary>
    public sealed class VisibleRows : IReadOnlyList<NodeViewModel>, IList, INotifyCollectionChanged
    {
        private readonly IReadOnlyList<NodeViewModel> roots;
        private readonly List<NodeViewModel> rows = [];
        private readonly HashSet<NodeViewModel> watched = [];
        private readonly Dictionary<object, NodeViewModel> owners = new(ReferenceEqualityComparer.Instance);

        public VisibleRows(IReadOnlyList<NodeViewModel> roots)
        {
            this.roots = roots;
            if (roots is INotifyCollectionChanged changing)
            {
                changing.CollectionChanged += (_, _) => OnRootsChanged();
            }

            foreach (var root in roots)
            {
                Watch(root);
            }

            rows.AddRange(Rows(roots));
        }

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public int Count => rows.Count;

        public NodeViewModel this[int index] => rows[index];

        public int IndexOf(NodeViewModel node) => rows.IndexOf(node);

        public IEnumerator<NodeViewModel> GetEnumerator() => rows.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => rows.GetEnumerator();

        // The rows of these nodes: each one the filter keeps, then the rows below it when it is open.
        private static IEnumerable<NodeViewModel> Rows(IEnumerable<NodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsFilteredOut)
                {
                    continue;
                }

                yield return node;
                if (node.IsExpanded)
                {
                    foreach (var below in Rows(node.Children))
                    {
                        yield return below;
                    }
                }
            }
        }

        // Every node of the tree is watched (opening, filtering, its children), from when it joins until it leaves.
        private void Watch(NodeViewModel node)
        {
            if (!watched.Add(node))
            {
                return;
            }

            node.PropertyChanged += OnNodeChanged;
            node.Children.CollectionChanged += OnChildrenChanged;
            owners[node.Children] = node;
            foreach (var child in node.Children)
            {
                Watch(child);
            }
        }

        private void Unwatch(NodeViewModel node)
        {
            if (!watched.Remove(node))
            {
                return;
            }

            node.PropertyChanged -= OnNodeChanged;
            node.Children.CollectionChanged -= OnChildrenChanged;
            owners.Remove(node.Children);
            foreach (var child in node.Children)
            {
                Unwatch(child);
            }
        }

        private void OnRootsChanged()
        {
            foreach (var gone in watched.Where(n => n.Parent is null && !roots.Contains(n)).ToList())
            {
                Unwatch(gone);
            }

            foreach (var root in roots)
            {
                Watch(root);
            }

            Replace(0, rows.Count, [.. Rows(roots)]);
        }

        private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not NodeViewModel node)
            {
                return;
            }

            if (e.PropertyName == nameof(NodeViewModel.IsExpanded))
            {
                Refresh(node);
            }
            else if (e.PropertyName == nameof(NodeViewModel.IsFilteredOut))
            {
                Refresh(node.Parent);
            }
        }

        private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (sender is null || !owners.TryGetValue(sender, out var parent))
            {
                return;
            }

            // The children that left stop being watched (on a Reset, whose old items are not told, every one no longer there).
            var current = parent.Children.ToHashSet();
            var gone = e.Action == NotifyCollectionChangedAction.Reset
                ? watched.Where(n => ReferenceEquals(n.Parent, parent) && !current.Contains(n)).ToList()
                : e.OldItems?.OfType<NodeViewModel>().Where(n => !current.Contains(n)).ToList() ?? [];
            foreach (var child in gone)
            {
                Unwatch(child);
            }

            foreach (var child in parent.Children)
            {
                Watch(child);
            }

            Refresh(parent);
        }

        // The rows below this row (or every row, for none) made again, when it shows and is open.
        private void Refresh(NodeViewModel? node)
        {
            if (node is null || !watched.Contains(node))
            {
                if (node is null)
                {
                    Replace(0, rows.Count, [.. Rows(roots)]);
                }

                return;
            }

            var at = rows.IndexOf(node);
            if (at < 0)
            {
                return;
            }

            var end = at + 1;
            while (end < rows.Count && IsBelow(rows[end], node))
            {
                end++;
            }

            Replace(at + 1, end - at - 1, node.IsExpanded ? [.. Rows(node.Children)] : []);
        }

        private static bool IsBelow(NodeViewModel row, NodeViewModel node)
        {
            for (var up = row.Parent; up is not null; up = up.Parent)
            {
                if (ReferenceEquals(up, node))
                {
                    return true;
                }
            }

            return false;
        }

        // Rows start..start+count become the new ones; the rows both have at their ends are kept.
        private void Replace(int start, int count, List<NodeViewModel> replacement)
        {
            var head = 0;
            while (head < count && head < replacement.Count && ReferenceEquals(rows[start + head], replacement[head]))
            {
                head++;
            }

            var tail = 0;
            while (tail < count - head && tail < replacement.Count - head
                && ReferenceEquals(rows[start + count - 1 - tail], replacement[replacement.Count - 1 - tail]))
            {
                tail++;
            }

            var removeAt = start + head;
            var removed = rows.GetRange(removeAt, count - head - tail);
            var added = replacement.GetRange(head, replacement.Count - head - tail);
            if (removed.Count > 0)
            {
                rows.RemoveRange(removeAt, removed.Count);
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, removeAt));
            }

            if (added.Count > 0)
            {
                rows.InsertRange(removeAt, added);
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, removeAt));
            }
        }

        // IList, read only (the view's items source).
        bool IList.IsFixedSize => false;

        bool IList.IsReadOnly => true;

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => rows;

        object? IList.this[int index]
        {
            get => rows[index];
            set => throw new NotSupportedException();
        }

        int IList.Add(object? value) => throw new NotSupportedException();

        void IList.Clear() => throw new NotSupportedException();

        bool IList.Contains(object? value) => value is NodeViewModel node && rows.Contains(node);

        int IList.IndexOf(object? value) => value is NodeViewModel node ? rows.IndexOf(node) : -1;

        void IList.Insert(int index, object? value) => throw new NotSupportedException();

        void IList.Remove(object? value) => throw new NotSupportedException();

        void IList.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection.CopyTo(Array array, int index) => ((ICollection)rows).CopyTo(array, index);
    }
}
