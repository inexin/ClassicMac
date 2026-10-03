using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Tests;

// The browse tree's rows as one flat list (VisibleRows): the roots and, under each open row, its children not filtered
// out, depth first; kept in step with opening, closing, filtering and changes to the children, a change at a time.
public class VisibleRowsTests
{
    private static FolderNode Folder(NodeViewModel? parent, string name, params string[] children)
    {
        var folder = new FolderNode(parent!, name);
        foreach (var child in children)
        {
            folder.Children.Add(new FolderNode(folder, child));
        }

        return folder;
    }

    private static string Names(VisibleRows rows) => string.Join(" ", rows.Select(r => new string('.', r.Depth) + r.Name));

    [Fact]
    public void Open_rows_show_their_children_depth_first()
    {
        var a = Folder(null, "a", "a1", "a2");
        var b = Folder(null, "b", "b1");
        var roots = new ObservableCollection<NodeViewModel> { a, b };
        var rows = new VisibleRows(roots);
        Assert.Equal("a b", Names(rows));

        a.IsExpanded = true;
        Assert.Equal("a .a1 .a2 b", Names(rows));
        a.Children[0].Children.Add(new FolderNode(a.Children[0], "deep"));
        Assert.Equal("a .a1 .a2 b", Names(rows));                 // a1 is closed
        a.Children[0].IsExpanded = true;
        Assert.Equal("a .a1 ..deep .a2 b", Names(rows));
        a.IsExpanded = false;
        Assert.Equal("a b", Names(rows));
        a.IsExpanded = true;                                       // a1 stays open below
        Assert.Equal("a .a1 ..deep .a2 b", Names(rows));
        Assert.Equal(1, rows.IndexOf(a.Children[0]));
        Assert.Equal(-1, rows.IndexOf(b.Children[0]));
        Assert.True(a.HasChildren);
        Assert.False(a.Children[1].HasChildren);
    }

    [Fact]
    public void Children_and_roots_changing_change_the_rows()
    {
        var a = Folder(null, "a", "a1");
        var roots = new ObservableCollection<NodeViewModel> { a };
        var rows = new VisibleRows(roots);
        a.IsExpanded = true;
        a.Children.Add(new FolderNode(a, "a2"));
        Assert.Equal("a .a1 .a2", Names(rows));
        a.Children.RemoveAt(0);
        Assert.Equal("a .a2", Names(rows));
        a.Children[0] = new FolderNode(a, "a3");
        Assert.Equal("a .a3", Names(rows));
        roots.Add(Folder(null, "b"));
        roots.Insert(0, Folder(null, "z"));
        Assert.Equal("z a .a3 b", Names(rows));
        roots.Remove(a);
        Assert.Equal("z b", Names(rows));
        a.IsExpanded = false;                                      // no longer watched
        a.Children.Clear();
        Assert.Equal("z b", Names(rows));
        a.Children.Add(new FolderNode(a, "x"));
        var removed = (FolderNode)a.Children[0];
        roots.Add(a);
        a.IsExpanded = true;
        Assert.Equal("z b a .x", Names(rows));
        a.Children.Clear();
        removed.IsExpanded = true;                                 // a removed child is no longer watched
        Assert.Equal("z b a", Names(rows));
    }

    [Fact]
    public void Filtered_out_rows_are_left_out_with_their_children()
    {
        var a = Folder(null, "a", "a1", "a2");
        a.Children[1].Children.Add(new FolderNode(a.Children[1], "inner"));
        a.Children[1].IsExpanded = true;
        var rows = new VisibleRows(new ObservableCollection<NodeViewModel> { a });
        a.IsExpanded = true;
        Assert.Equal("a .a1 .a2 ..inner", Names(rows));
        a.Children[1].IsFilteredOut = true;
        Assert.Equal("a .a1", Names(rows));
        a.Children[1].IsFilteredOut = false;
        a.IsFilteredOut = true;
        Assert.Equal("", Names(rows));
        a.IsFilteredOut = false;
        Assert.Equal("a .a1 .a2 ..inner", Names(rows));
    }

    // Opening a folder of thousands of files is one change of the list, not one per file; closing it another.
    [Fact]
    public void Opening_and_closing_are_one_change_each()
    {
        var big = new FolderNode(null!, "big");
        for (var i = 0; i < 5000; i++)
        {
            big.Children.Add(new FolderNode(big, $"f{i}"));
        }

        var rows = new VisibleRows(new ObservableCollection<NodeViewModel> { big, Folder(null, "after") });
        var changes = new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, e) => changes.Add(e);
        big.IsExpanded = true;
        var opened = Assert.Single(changes);
        Assert.Equal((NotifyCollectionChangedAction.Add, 1, 5000), (opened.Action, opened.NewStartingIndex, opened.NewItems!.Count));
        Assert.Equal(5002, rows.Count);
        Assert.Same(big.Children[4999], rows[5000]);
        changes.Clear();
        big.IsExpanded = false;
        var closed = Assert.Single(changes);
        Assert.Equal((NotifyCollectionChangedAction.Remove, 1, 5000), (closed.Action, closed.OldStartingIndex, closed.OldItems!.Count));
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Depth_counts_the_rows_above()
    {
        var a = Folder(null, "a", "a1");
        Assert.Equal((0, 1), (a.Depth, a.Children[0].Depth));
    }
}
