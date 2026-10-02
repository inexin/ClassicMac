using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Templates;

namespace ClassicMac.App.Tests;

// Hex editing in place (design/boards/hex.md, E7): Edit Hex opens the Hex tab in editing, no dialog; changed bytes,
// overwrite and insert, the byte inspector at the cursor, Go to, and the hook for E8's byte meanings.
public sealed class HexEditTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-hexedit").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A resource file: STR# 128 (has a preview) and 'ZZZZ' 1 (none).
    private async Task<(MainViewModel Model, InputNode Input)> Open()
    {
        var path = Path.Combine(folder, "Hex.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("STR#", 128, null, [0, 1, 8, .. "Untitled"u8]), ("ZZZZ", 1, null, [0x55, 0x6E, 0x74, 0x69, 0x00, 0xFF])));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        return (model, input);
    }

    private static ResourceNode Resource(InputNode input, string type) =>
        input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children.OfType<ResourceNode>().Single();

    [Fact]
    public async Task Edit_Hex_opens_the_Hex_tab_in_editing_without_a_dialog()
    {
        var (model, input) = await Open();
        model.Selected = Resource(input, "STR#");
        await model.PreviewTask;
        Assert.False(model.HasHex); // has a preview: no Hex tab
        Assert.Equal(1, model.SelectedTab);

        await model.EditHexCommand.ExecuteAsync(null);

        Assert.True(model.IsHexEditing);
        Assert.True(model.HasHex);
        Assert.Equal(2, model.SelectedTab);
        Assert.Equal(11, model.HexEdit!.Length);

        // Again while editing: stays, same editor.
        var editor = model.HexEdit;
        model.SelectedTab = 0;
        await model.EditHexCommand.ExecuteAsync(null);
        Assert.Same(editor, model.HexEdit);
        Assert.Equal(2, model.SelectedTab);

        // Discarding ends it; the previewed resource has no Hex tab again.
        model.DiscardHexEditCommand.Execute(null);
        Assert.False(model.HasHex);
        Assert.Equal(1, model.SelectedTab);  // back to its preview
    }

    [Fact]
    public async Task Apply_makes_one_undoable_edit()
    {
        var (model, input) = await Open();
        model.Selected = Resource(input, "ZZZZ");
        await model.PreviewTask;
        await model.EditHexCommand.ExecuteAsync(null);
        model.HexEdit!.TypeDigit(0);
        model.HexEdit.TypeDigit(1);

        model.ApplyHexEditCommand.Execute(null);

        Assert.False(model.IsHexEditing);
        Assert.Equal(0x01, Resource(input, "ZZZZ").Resource.GetData().Span[0]);
        Assert.Equal("_Undo Edit 'ZZZZ' 1", model.UndoTitle);
    }

    [Fact]
    public void Changed_bytes_are_marked_and_counted()
    {
        var editor = new HexEditor(new byte[] { 1, 2, 3, 4 });
        Assert.Equal(0, editor.ChangedCount);
        Assert.Equal("0x0000 = 1 · no changes · hex digits type, Insert toggles, Delete removes", editor.Status);

        editor.TypeDigit(0);
        editor.TypeDigit(9);                 // byte 0: 1 → 9
        Assert.True(editor.IsChanged(0));
        Assert.False(editor.IsChanged(1));
        Assert.Equal(1, editor.ChangedCount);
        Assert.Equal("0x0001 = 2 · 1 byte changed · hex digits type, Insert toggles, Delete removes", editor.Status);

        editor.InsertMode = true;            // inserted bytes shift the rest: all after differ
        editor.TypeDigit(0xF);
        editor.TypeDigit(0xF);
        Assert.Equal([9, 0xFF, 2, 3, 4], editor.ToArray());
        Assert.Equal(5, editor.ChangedCount);
        Assert.True(editor.IsChanged(4));    // past the original end
        Assert.Equal("0x0002 = 2 · 5 bytes changed · hex digits type, Insert toggles, Delete removes", editor.Status);
        editor.MoveTo(5);
        Assert.StartsWith("0x0005 = end · ", editor.Status);
    }

    [Fact]
    public void The_inspector_reads_the_bytes_at_the_cursor()
    {
        var editor = new HexEditor(new byte[] { 0x55, 0x6E, 0x74, 0x69, 0x00, 0xFF });
        Assert.Equal("At 0x0000", editor.Inspector.Heading);
        Assert.Equal(
        [
            ("UInt8", "85"), ("Int8", "85"), ("UInt16 BE", "21,870"), ("Int16 BE", "21,870"),
            ("UInt32 BE", "1,433,302,121"), ("OSType", "'Unti'"), ("Binary", "0101 0101"),
        ], editor.Inspector.Rows.Select(r => (r.Label, r.Value)));

        editor.MoveTo(4);
        Assert.Equal("At 0x0004", editor.Inspector.Heading);
        Assert.Equal(["0", "0", "255", "255", "—", "—", "0000 0000"], editor.Inspector.Rows.Select(r => r.Value));
        editor.MoveTo(5);
        Assert.Equal(["255", "-1", "—", "—", "—", "—", "1111 1111"], editor.Inspector.Rows.Select(r => r.Value));
        editor.MoveTo(6);                    // at the end: nothing to read
        Assert.All(editor.Inspector.Rows, r => Assert.Equal("—", r.Value));
    }

    [Fact]
    public void The_inspector_follows_the_cursor_and_edits()
    {
        var editor = new HexEditor(new byte[] { 1, 2 });
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        editor.MoveTo(1);
        Assert.Contains(nameof(HexEditor.Inspector), changed);
        Assert.Equal("2", editor.Inspector.Rows[0].Value);
        editor.TypeDigit(0xA);
        Assert.Equal("162", editor.Inspector.Rows[0].Value);
    }

    [Theory]
    [InlineData("0x0003", 3)]
    [InlineData("0X3", 3)]
    [InlineData("$3", 3)]
    [InlineData(" 3 ", 3)]
    [InlineData("a", 6)]       // past the end: the end
    [InlineData("0", 0)]
    public void Go_to_moves_to_a_hex_offset(string text, int cursor)
    {
        var editor = new HexEditor(new byte[] { 1, 2, 3, 4, 5, 6 });
        Assert.True(editor.GoTo(text));
        Assert.Equal(cursor, editor.Cursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("0x")]
    [InlineData("-1")]
    [InlineData("0x1FFFFFFFFF")]
    public void Go_to_refuses_what_is_not_an_offset(string text)
    {
        var editor = new HexEditor(new byte[] { 1, 2, 3 });
        editor.MoveTo(2);
        Assert.False(editor.GoTo(text));
        Assert.Equal(2, editor.Cursor);
    }

    [Fact]
    public async Task Go_to_in_the_model_reads_the_box()
    {
        var (model, input) = await Open();
        model.Selected = Resource(input, "ZZZZ");
        await model.PreviewTask;
        await model.EditHexCommand.ExecuteAsync(null);
        model.GoToText = "0x4";
        model.GoToCommand.Execute(null);
        Assert.Equal(4, model.HexEdit!.Cursor);
        Assert.Null(model.GoToError);
        model.GoToText = "nope";
        model.GoToCommand.Execute(null);
        Assert.Equal("Not a hex offset", model.GoToError);
        Assert.Equal(4, model.HexEdit.Cursor);
        model.GoToText = "2";
        model.GoToCommand.Execute(null);
        Assert.Null(model.GoToError);
    }

    [Fact]
    public void Lines_carry_cells_with_their_states()
    {
        var editor = new HexEditor((byte[])[0x41, 0x00, 0x42, .. new byte[14]]);
        editor.TypeDigit(0x4);
        editor.TypeDigit(0x3);               // byte 0 'A' → 'C', cursor on byte 1
        var line = editor.Lines[0];
        Assert.Equal(16, line.Cells.Count);
        var (first, second) = (line.Cells[0], line.Cells[1]);
        Assert.Equal((0L, "43", "C", false, true, false), (first.Offset, first.Hex, first.Character, first.IsZero, first.IsChanged, first.IsCursor));
        Assert.Equal(("00", "·", true, false, true), (second.Hex, second.Character, second.IsZero, second.IsChanged, second.IsCursor));
        Assert.True(line.Cells[7].IsGroupEnd);
        Assert.False(line.Cells[8].IsGroupEnd);

        // The last line holds the append position as a blank cursor cell.
        editor.MoveTo(17);
        var last = editor.Lines[1];
        Assert.Equal(2, last.Cells.Count);
        Assert.Equal(("", true), (last.Cells[1].Hex, last.Cells[1].IsCursor));
    }

    [Fact]
    public void The_mode_switch_is_overwrite_or_insert()
    {
        var editor = new HexEditor(new byte[] { 1 });
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal(0, editor.ModeIndex);
        editor.ModeIndex = 1;
        Assert.True(editor.InsertMode);
        Assert.Contains(nameof(HexEditor.ModeIndex), changed);
        editor.ToggleInsert();
        Assert.Equal(0, editor.ModeIndex);
        Assert.Equal(["Overwrite", "Insert"], HexEditor.Modes);
    }

    [Fact]
    public void The_column_header_names_the_sixteen_columns()
    {
        Assert.Equal(Enumerable.Range(0, 16).Select(i => i.ToString("X2")), HexLines.HeaderCells.Select(c => c.Hex));
        Assert.True(HexLines.HeaderCells[7].IsGroupEnd);
        Assert.All(HexLines.HeaderCells, c => Assert.False(c.IsCursor || c.IsChanged || c.IsZero));
    }

    [Theory]
    [InlineData("00000010", "0x000010")]
    [InlineData("01ABCDE0", "0x1ABCDE0")]
    public void Offsets_show_six_hex_digits_or_as_many_as_needed(string offset, string shown) =>
        Assert.Equal(shown, new HexLine(offset, "", "").DisplayOffset);

    [Fact]
    public void Read_only_lines_have_cells_without_states()
    {
        var lines = new HexLines(ClassicMac.Files.ForkData.FromBytes(new byte[] { 0x7F, 0x20 }));
        var cells = lines[0].Cells;
        Assert.Equal(2, cells.Count);
        Assert.Equal(("7F", "·", false, false), (cells[0].Hex, cells[0].Character, cells[0].IsCursor, cells[0].IsChanged));
        Assert.Equal(" ", cells[1].Character);
        Assert.Equal((true, false), (cells[0].IsPlaceholder, cells[1].IsPlaceholder));
    }

    // E8 (design/boards/hex.md): the inspector says what the byte at the cursor is, from the edited bytes, and the
    // field it belongs to is highlighted in the grid.
    [Fact]
    public async Task The_inspector_shows_the_meaning_and_the_grid_its_field()
    {
        var (model, input) = await Open();
        model.Selected = Resource(input, "STR#");
        await model.PreviewTask;
        await model.EditHexCommand.ExecuteAsync(null);
        var editor = model.HexEdit!;

        Assert.Equal(new ByteMeaning("Number of strings", 0, 2, "1"), editor.Inspector.Meaning);
        Assert.Equal([true, true, false], editor.Lines[0].Cells.Take(3).Select(c => c.IsInField));
        editor.MoveTo(4);
        Assert.Equal("Character 2 of string 1, “Untitled”", editor.Inspector.Meaning!.Text);
        Assert.Equal(Enumerable.Range(0, 11).Select(i => i is >= 3 and <= 10), editor.Lines[0].Cells.Take(11).Select(c => c.IsInField));

        // From the bytes as edited: 'U' → 'A'.
        editor.MoveTo(3);
        editor.TypeDigit(4);
        editor.TypeDigit(1);
        editor.MoveTo(3);
        Assert.Equal(("Character 1 of string 1, “Antitled”", "A"), (editor.Inspector.Meaning!.Text, editor.Inspector.Meaning.Value));
    }

    [Fact]
    public async Task Types_without_a_field_map_have_no_meaning()
    {
        var (model, input) = await Open();
        model.Selected = Resource(input, "ZZZZ");
        await model.PreviewTask;
        await model.EditHexCommand.ExecuteAsync(null);
        Assert.Null(model.HexEdit!.Inspector.Meaning);
        Assert.All(model.HexEdit.Lines[0].Cells, c => Assert.False(c.IsInField));
    }

    // Read only (the hex view of a resource with no preview): a click selects a byte; the pair highlights, and the
    // inspector reads it, with its meaning when a TMPL in an open file describes the type.
    [Fact]
    public async Task Selecting_a_byte_read_only_highlights_and_inspects_it()
    {
        var path = Path.Combine(folder, "Data.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("Rsrc", 128, null, [0, 7, 0xFF]),
            ("TMPL", 1000, "Rsrc", EditTests.Tmpl(("ID", "DWRD"), ("Flag", "DBYT")))));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = Resource(input, "Rsrc");
        await model.PreviewTask;
        Assert.True(model.HasHex);
        Assert.Null(model.HexInspection);
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        model.PropertyChanged += (_, e) => changed.Enqueue(e.PropertyName);

        model.SelectHexByte(1);

        Assert.Contains(nameof(MainViewModel.HexInspection), changed);
        Assert.Equal("At 0x0001", model.HexInspection!.Heading);
        Assert.Equal("2,047", model.HexInspection.Rows[2].Value);   // UInt16 BE at 1: 07 FF
        Assert.Equal(new ByteMeaning("ID", 0, 2, "7"), model.HexInspection.Meaning);
        var cells = model.HexLines![0].Cells;
        Assert.Equal([false, true, false], cells.Select(c => c.IsSelected));
        Assert.Equal([true, true, false], cells.Select(c => c.IsInField));
        Assert.All(cells, c => Assert.False(c.IsCursor));

        // Editing shows the editor's inspector; a new selection drops the read-only one.
        await model.EditHexCommand.ExecuteAsync(null);
        Assert.Same(model.HexEdit!.Inspector, model.HexInspection);
        model.HexEdit.MoveTo(2);
        Assert.Equal("At 0x0002", model.HexInspection!.Heading);
        model.DiscardHexEditCommand.Execute(null);
        model.Selected = input;
        Assert.Null(model.HexInspection);
    }
}
