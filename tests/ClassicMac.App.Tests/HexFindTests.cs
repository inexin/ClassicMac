using ClassicMac.App.ViewModels;
using ClassicMac.Core;

namespace ClassicMac.App.Tests;

// Find in the hex view (design/boards/hex.md's follow-up to E7): bytes as hex or text as Mac OS Roman, next and
// previous with wrap-around, the match selected (read only) or under the cursor (editing) and highlighted.
public sealed class HexFindTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-hexfind").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Theory]
    [InlineData("4E 75", new byte[] { 0x4E, 0x75 })]
    [InlineData("4e75", new byte[] { 0x4E, 0x75 })]
    [InlineData(" 0x4E 0x75 ", new byte[] { 0x4E, 0x75 })]
    [InlineData("00", new byte[] { 0 })]
    public void Hex_patterns_are_whole_bytes(string text, byte[] bytes)
    {
        Assert.True(HexSearch.TryParse(text, HexFindMode.Hex, out var pattern, out var error));
        Assert.Equal(bytes, pattern);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("4E7", "Hex needs two digits a byte")]
    [InlineData("4G", "Not hex: G")]
    [InlineData("", "Type bytes to find")]
    [InlineData("   ", "Type bytes to find")]
    public void Bad_hex_patterns_say_why(string text, string message)
    {
        Assert.False(HexSearch.TryParse(text, HexFindMode.Hex, out _, out var error));
        Assert.Equal(message, error);
    }

    [Fact]
    public void Text_patterns_are_Mac_OS_Roman()
    {
        Assert.True(HexSearch.TryParse("Café", HexFindMode.Text, out var pattern, out _));
        Assert.Equal(MacRoman.Encode("Café"), pattern);
        Assert.False(HexSearch.TryParse("日本", HexFindMode.Text, out _, out var error));
        Assert.Equal("Not in Mac OS Roman: 日", error);
        Assert.False(HexSearch.TryParse("", HexFindMode.Text, out _, out error));
        Assert.Equal("Type text to find", error);
    }

    [Theory]
    [InlineData(0, true, 1)]
    [InlineData(1, true, 1)]
    [InlineData(2, true, 4)]
    [InlineData(5, true, 1)]     // wraps to the start
    [InlineData(4, false, 1)]
    [InlineData(1, false, 4)]    // wraps to the end
    [InlineData(0, false, 4)]
    public void Find_goes_forward_or_back_and_wraps(int start, bool forward, int found)
    {
        byte[] data = [0, 0xAB, 0xCD, 0, 0xAB, 0xCD];
        Assert.Equal(found, HexSearch.Find(data, [0xAB, 0xCD], start, forward));
    }

    [Fact]
    public void Find_reports_none_and_counts_matches()
    {
        byte[] data = [1, 1, 1, 2];
        Assert.Equal(-1, HexSearch.Find(data, [3], 0, true));
        Assert.Equal(-1, HexSearch.Find(data, [1, 2, 3], 0, true));
        Assert.Equal(-1, HexSearch.Find([], [1], 0, true));
        Assert.Equal(3, HexSearch.Count(data, [1]));
        Assert.Equal(2, HexSearch.Count(data, [1, 1]));                  // overlapping matches count
        Assert.Equal(1, HexSearch.Ordinal(data, [1, 1], 0));               // the 1st match starts at 0
        Assert.Equal(2, HexSearch.Ordinal(data, [1, 1], 1));
    }

    private async Task<(MainViewModel Model, ResourceNode Node)> Open(byte[] data)
    {
        var path = Path.Combine(folder, "Find.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("ZZZZ", 128, null, data)));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        var node = input.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().Single();
        model.Selected = node;
        await model.PreviewTask;
        return (model, node);
    }

    [Fact]
    public async Task Find_selects_the_match_read_only_and_highlights_it()
    {
        var (model, _) = await Open([.. "Hello, hello, HELLO"u8]);
        var scrolled = new List<int>();
        model.HexFind.HexLineShown += scrolled.Add;
        model.HexFind.FindMode = HexFindMode.Text;
        model.HexFind.FindText = "llo";

        model.HexFind.FindNextCommand.Execute(null);

        Assert.Equal("At 0x0002", model.HexInspection!.Heading);
        Assert.Equal("1 of 2", model.HexFind.FindStatus);
        Assert.Equal([false, false, true, true, true, false], model.HexLines![0].Cells.Take(6).Select(c => c.IsMatch));
        Assert.Equal([0], scrolled);
        model.HexFind.FindNextCommand.Execute(null);
        Assert.Equal(("At 0x0009", "2 of 2"), (model.HexInspection!.Heading, model.HexFind.FindStatus));
        model.HexFind.FindNextCommand.Execute(null);                                 // wraps
        Assert.Equal(("At 0x0002", "1 of 2"), (model.HexInspection!.Heading, model.HexFind.FindStatus));
        model.HexFind.FindPreviousCommand.Execute(null);                             // wraps back
        Assert.Equal("At 0x0009", model.HexInspection!.Heading);

        // Hex: the bytes of "HELLO".
        model.HexFind.FindMode = HexFindMode.Hex;
        model.HexFind.FindText = "48 45";
        model.HexFind.FindNextCommand.Execute(null);
        Assert.Equal(("At 0x000E", "1 of 1"), (model.HexInspection!.Heading, model.HexFind.FindStatus));
    }

    [Fact]
    public async Task Find_moves_the_cursor_while_editing()
    {
        var (model, _) = await Open([0, 1, 2, 3, 2, 3]);
        await model.EditHexCommand.ExecuteAsync(null);
        model.HexFind.FindMode = HexFindMode.Hex;
        model.HexFind.FindText = "02 03";
        model.HexFind.FindNextCommand.Execute(null);
        Assert.Equal(2, model.HexEdit!.Cursor);
        Assert.True(model.HexEdit.Lines[0].Cells[3].IsMatch);
        model.HexFind.FindNextCommand.Execute(null);
        Assert.Equal(4, model.HexEdit.Cursor);

        // The edited bytes are searched: 02 03 at 2 overwritten, one left.
        model.HexEdit.MoveTo(2);
        model.HexEdit.TypeDigit(0);
        model.HexEdit.TypeDigit(0);
        model.HexFind.FindPreviousCommand.Execute(null);
        Assert.Equal((4, "1 of 1"), (model.HexEdit.Cursor, model.HexFind.FindStatus));
    }

    [Fact]
    public async Task Nothing_found_or_a_bad_pattern_says_so_and_moves_nothing()
    {
        var (model, _) = await Open([1, 2, 3]);
        model.HexFind.FindText = "09";
        model.HexFind.FindNextCommand.Execute(null);
        Assert.Equal("Not found", model.HexFind.FindStatus);
        Assert.Null(model.HexInspection);
        model.HexFind.FindText = "zz";
        model.HexFind.FindNextCommand.Execute(null);
        Assert.Equal("Not hex: z", model.HexFind.FindStatus);
        Assert.True(model.HexFind.FindFailed);
        model.HexFind.FindText = "02";
        model.HexFind.FindNextCommand.Execute(null);
        Assert.False(model.HexFind.FindFailed);
    }

    [Fact]
    public async Task A_new_selection_drops_the_match()
    {
        var (model, node) = await Open([1, 2, 3]);
        model.HexFind.FindText = "02";
        model.HexFind.FindNextCommand.Execute(null);
        Assert.NotNull(model.HexFind.FindStatus);
        model.Selected = node.Parent;
        await model.PreviewTask;
        Assert.Null(model.HexFind.FindStatus);
        Assert.Equal("02", model.HexFind.FindText);                                  // the pattern stays for the next resource
    }
}
