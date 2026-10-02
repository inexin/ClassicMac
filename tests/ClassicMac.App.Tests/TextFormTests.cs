using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Text;

namespace ClassicMac.App.Tests;

// Strings, string lists, text and version on the read-then-edit host (design/boards/read-then-edit.md, E5): each has
// a read-only view of its values as text.
public class TextFormTests
{
    private static Resource Resource(string type) => new(FourCC.FromString(type), 128, Array.Empty<byte>());

    [Fact]
    public void A_string_reads_as_its_text()
    {
        var form = new StringForm(Resource("STR "), "Hello");
        Assert.True(form.HasReadOnlyView);
        Assert.False(form.IsEmpty);
        form.Text = "";
        Assert.True(form.IsEmpty);
        var changed = new List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        form.Text = "x";
        Assert.Contains(nameof(StringForm.IsEmpty), changed);
    }

    [Fact]
    public void A_string_list_reads_as_a_numbered_list_and_a_row_can_be_selected()
    {
        var form = new StringListForm(Resource("STR#"), ["one", "two", "three"]);
        Assert.True(form.HasReadOnlyView);
        Assert.Equal([1, 2, 3], form.Strings.Select(s => s.Number));
        Assert.Equal("3 strings", form.Summary);
        form.SelectRow(form.Strings[1]);
        Assert.Same(form.Strings[1], form.SelectedItem);
        Assert.True(form.Strings[1].IsSelected);
        form.SelectRow("not a row");
        Assert.Same(form.Strings[1], form.SelectedItem);
        form.SelectedItem = form.Strings[2];
        Assert.False(form.Strings[1].IsSelected);

        form.AddCommand.Execute(null);
        Assert.Equal([1, 2, 3, 4], form.Strings.Select(s => s.Number));
        Assert.Same(form.Strings[3], form.SelectedItem);                     // a new string is selected
        Assert.Equal("4 strings", form.Summary);
        form.MoveUpCommand.Execute(form.Strings[3]);
        Assert.Equal(["one", "two", "", "three"], form.Strings.Select(s => s.Text));
        Assert.Equal([1, 2, 3, 4], form.Strings.Select(s => s.Number));
        form.RemoveCommand.Execute(form.Strings[2]);
        Assert.Null(form.SelectedItem);                                      // the selected string removed
        Assert.Equal("3 strings", form.Summary);
        form.RemoveCommand.Execute(form.Strings[0]);
        form.RemoveCommand.Execute(form.Strings[0]);
        Assert.Equal("1 string", form.Summary);
        form.RemoveCommand.Execute(form.Strings[0]);
        Assert.Equal("No strings", form.Summary);
    }

    [Fact]
    public void Text_reads_with_its_styles_applied_and_follows_edits()
    {
        var styl = PreviewTests.Styl((0, 20, 1, 18, 0, 0, 0), (6, 4, 0, 10, 0xFFFF, 0, 0));
        var data = "Hello world"u8.ToArray();
        var form = new TextForm(Resource("TEXT"), data, (new Resource(FourCC.FromString("styl"), 128, styl), styl));
        Assert.True(form.HasReadOnlyView);
        Assert.Equal("Hello world", form.Styled!.Text);
        Assert.Equal(2, form.Styled.Runs.Count);
        Assert.True(form.Styled.Runs[0].Bold);
        form.Text = "Hello there world";
        Assert.Equal("Hello there world", form.Styled!.Text);

        var plain = new TextForm(Resource("TEXT"), "Plain"u8.ToArray(), null);
        Assert.Equal("Plain", plain.Styled!.Text);
        plain.Text = "Still plain";
        Assert.Equal("Still plain", plain.Styled!.Text);
        plain.Text = "日本";                                                  // not Mac OS Roman: the last good text stays
        Assert.Equal("Still plain", plain.Styled!.Text);
    }

    [Theory]
    [InlineData(1, 2, 0, 0x60, 3, "1.2b3")]
    [InlineData(1, 2, 1, 0x80, 0, "1.2.1")]
    [InlineData(7, 5, 5, 0x80, 2, "7.5.5f2")]
    [InlineData(2, 0, 0, 0x20, 1, "2.0d1")]
    [InlineData(3, 1, 0, 0x40, 9, "3.1a9")]
    public void A_version_reads_as_labelled_rows(int major, int minor, int bugFix, byte stage, int nonRelease, string number)
    {
        var form = new VersionForm(Resource("vers"), new VersionResource(major, minor, bugFix, stage, nonRelease, 0, "short", "long"));
        Assert.True(form.HasReadOnlyView);
        Assert.Equal(number, form.NumberText);
        Assert.Equal(form.Stage.Name, form.StageText);
    }

    [Fact]
    public void A_version_s_rows_follow_its_values()
    {
        var form = new VersionForm(Resource("vers"), new VersionResource(1, 2, 0, 0x60, 3, 0, "1.2b3", "1.2b3, © 2026"));
        Assert.Equal("0", form.RegionText);
        var changed = new List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        form.BugFix = 4;
        form.Stage = VersionForm.Stages[3];
        form.Region = 33;
        Assert.Equal("1.2.4b3".Replace("b3", "f3"), form.NumberText);
        Assert.Equal("final", form.StageText);
        Assert.Equal("33", form.RegionText);
        Assert.Contains(nameof(VersionForm.NumberText), changed);
        Assert.Contains(nameof(VersionForm.StageText), changed);
        Assert.Contains(nameof(VersionForm.RegionText), changed);
    }
}
