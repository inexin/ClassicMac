using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Interface;
using MacMenuItem = ClassicMac.Resources.Decoders.Interface.MenuItem;

namespace ClassicMac.App.Tests;

// The menu form (design/boards/read-then-edit.md, E2): its read-only table, the summary, the seven style bits, the
// marks, the duplicate Command-key error, adding dividers, the live preview and the selection it shares with the preview.
public class MenuFormTests
{
    // File: Open… ⌘O (bold), a divider, Save ⌘S (checked, italic, underline, bit 7 set), Save As… (no key, disabled,
    // icon 3), Quit ⌘Q.
    internal static MenuResource File() => new(129, 0, 0, 0, 0xFFFFFFFF & ~(1u << 4), "File",
    [
        new MacMenuItem("Open…", 0, (byte)'O', 0, 1, true),
        new MacMenuItem("-", 0, 0, 0, 0, false),
        new MacMenuItem("Save", 0, (byte)'S', 0x12, 0x86, true),
        new MacMenuItem("Save As…", 3, 0, 0, 0, false),
        new MacMenuItem("Quit", 0, (byte)'Q', 0, 0, true),
    ]);

    private static MenuForm Form(MenuResource? menu = null) =>
        new(new Resource(FourCC.FromString("MENU"), 129, InterfaceWriter.WriteMenu(menu ?? File())), menu ?? File());

    [Fact]
    public void The_summary_reads_the_menu()
    {
        var form = Form();
        Assert.Equal(("File", "Yes", "129", "0"), (form.Title, form.EnabledText, form.IdText, form.DefinitionText));
        form.Enabled = false;
        Assert.Equal("No", form.EnabledText);
        Assert.True(form.HasReadOnlyView);
        Assert.Equal("Text “-” makes a divider. Esc cancels, Ctrl+Enter applies.", form.EditHint);
    }

    [Fact]
    public void The_table_shows_each_item_s_values_with_dashes_for_none()
    {
        var rows = Form().Items;
        Assert.Equal([1, 2, 3, 4, 5], rows.Select(r => r.Number));
        Assert.Equal(("⌘O", "—", "—", "Bold", "Yes"), (rows[0].KeyText, rows[0].MarkText, rows[0].IconText, rows[0].StyleText, rows[0].EnabledText));
        Assert.True(rows[1].IsDivider);
        Assert.False(rows[0].IsDivider);
        Assert.Equal(("⌘S", "Check mark", "Italic, Underline"), (rows[2].KeyText, rows[2].MarkText, rows[2].StyleText));
        Assert.Equal(("—", "3", "No"), (rows[3].KeyText, rows[3].IconText, rows[3].EnabledText));
        Assert.True(rows[3].IsDimmed);                                     // disabled items in CmTextMuted
        Assert.False(rows[4].IsDimmed);
        var changed = new List<string?>();
        rows[4].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rows[4].Text = "-";
        Assert.False(rows[4].IsDivider);                                   // a Command key makes it an item
        rows[4].Key = "";
        Assert.True(rows[4].IsDivider);
        Assert.Contains(nameof(MenuItemRow.IsDivider), changed);
        rows[4].Enabled = false;
        Assert.True(rows[4].IsDimmed);
        Assert.Contains(nameof(MenuItemRow.EnabledText), changed);
    }

    [Fact]
    public void Codes_in_the_key_field_show_as_what_they_mean()
    {
        var form = Form(new MenuResource(1, 0, 0, 0, 0xFFFFFFFF, "M", [new MacMenuItem("Sub", 0, 0x1B, 200, 0, true)]));
        Assert.Equal(("submenu", "menu 200"), (form.Items[0].KeyText, form.Items[0].MarkText));
    }

    [Theory]
    [InlineData(nameof(MenuItemRow.Bold), 0x01)]
    [InlineData(nameof(MenuItemRow.Italic), 0x02)]
    [InlineData(nameof(MenuItemRow.Underline), 0x04)]
    [InlineData(nameof(MenuItemRow.Outline), 0x08)]
    [InlineData(nameof(MenuItemRow.Shadow), 0x10)]
    [InlineData(nameof(MenuItemRow.Condense), 0x20)]
    [InlineData(nameof(MenuItemRow.Extend), 0x40)]
    public void Each_of_the_seven_style_bits_toggles_and_keeps_the_others(string bit, int mask)
    {
        var form = Form();
        var row = form.Items[2];                                           // italic, underline, and bit 7
        var property = typeof(MenuItemRow).GetProperty(bit)!;
        var before = (byte)row.Face;
        var was = (bool)property.GetValue(row)!;
        Assert.Equal((before & mask) != 0, was);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        property.SetValue(row, !was);
        Assert.Equal(before ^ mask, (int)row.Face);
        Assert.Contains(bit, changed);
        Assert.Contains(nameof(MenuItemRow.StyleText), changed);
        Assert.Equal((byte)(before ^ mask), form.ToMenu().Items[2].Face);  // bit 7 kept through the write
        property.SetValue(row, was);
        Assert.Equal(before, (byte)row.Face);
    }

    [Fact]
    public void All_seven_style_bits_are_named()
    {
        var row = Form().Items[4];
        row.Face = 0x7F;
        Assert.Equal("Bold, Italic, Underline, Outline, Shadow, Condense, Extend", row.StyleText);
        Assert.True(row.HasMoreStyles);
        row.Face = 0x07;
        Assert.False(row.HasMoreStyles);
    }

    [Fact]
    public void Marks_are_chosen_in_words_with_their_codes_and_others_by_code()
    {
        // The board's list: no symbol glyphs, the Mac character code beside each word.
        var form = Form();
        var row = form.Items[0];
        Assert.Equal([("None", ""), ("Check mark", "$12"), ("Diamond", "$13"), ("Bullet", "$A5"), ("Other…", "")],
            MenuItemRow.Marks.Select(m => (m.Label, m.Code)));
        Assert.Equal("Check mark $12", MenuItemRow.Marks[1].ToString());
        Assert.Equal(("Other", "Check mark"), (MenuItemRow.Marks[4].ShortLabel, MenuItemRow.Marks[1].ShortLabel));
        Assert.Equal(("None", "—", false), (row.MarkChoice.Label, row.MarkText, row.IsOtherMark));
        row.MarkChoice = MenuItemRow.Marks[2];
        Assert.Equal(0x13, form.ToMenu().Items[0].Mark);
        Assert.Equal("Diamond", row.MarkText);
        row.MarkChoice = MenuItemRow.Marks[3];
        Assert.Equal(0xA5, form.ToMenu().Items[0].Mark);
        Assert.Equal("Bullet", row.MarkText);
        Assert.Same(MenuItemRow.Marks[1], form.Items[2].MarkChoice);       // read from $12
        Assert.Equal("Check mark", form.Items[2].MarkText);

        // Other…: the code is typed ($2A, or the character itself); a bad one is the form's error.
        row.MarkChoice = MenuItemRow.Marks[4];
        Assert.True(row.IsOtherMark);
        Assert.Same(MenuItemRow.Marks[4], row.MarkChoice);
        row.MarkCode = "$2A";
        Assert.Equal(((byte)'*', "$2A", "* $2A"), (form.ToMenu().Items[0].Mark, row.MarkCode, row.MarkText));
        Assert.Same(MenuItemRow.Marks[4], row.MarkChoice);
        row.MarkCode = "$12";                                              // a known mark typed: its word
        Assert.Same(MenuItemRow.Marks[1], row.MarkChoice);
        Assert.False(row.IsOtherMark);
        row.MarkChoice = MenuItemRow.Marks[4];
        row.MarkCode = "$ZZ";
        Assert.Contains("not a mark", form.Error);
        row.MarkChoice = MenuItemRow.Marks[0];
        Assert.Null(form.Error);

        var other = Form(new MenuResource(1, 0, 0, 0, 0xFFFFFFFF, "M", [new MacMenuItem("Odd", 0, 0, (byte)'*', 0, true)])).Items[0];
        Assert.Same(MenuItemRow.Marks[4], other.MarkChoice);              // read as Other…, with its code
        Assert.Equal(("$2A", "* $2A"), (other.MarkCode, other.MarkText));
        Assert.Equal((byte)'*', other.ToItem().Mark);
    }

    [Fact]
    public void The_selected_row_moves_up_and_down()
    {
        var form = Form();
        Assert.False(form.MoveSelectedUpCommand.CanExecute(null));          // nothing selected
        form.SelectedItem = form.Items[1];
        Assert.True(form.MoveSelectedUpCommand.CanExecute(null));
        form.MoveSelectedUpCommand.Execute(null);
        Assert.Equal(("-", 1), (form.Items[0].Text, form.SelectedItem!.Number));
        Assert.False(form.MoveSelectedUpCommand.CanExecute(null));          // already first
        form.MoveSelectedDownCommand.Execute(null);
        form.MoveSelectedDownCommand.Execute(null);
        Assert.Same(form.Items[2], form.SelectedItem);
        form.SelectedItem = form.Items[^1];
        Assert.False(form.MoveSelectedDownCommand.CanExecute(null));        // already last
    }

    [Fact]
    public void A_duplicate_command_key_is_an_error_on_both_rows()
    {
        var form = Form();
        Assert.Null(form.Error);
        form.Items[3].Key = "s";                                           // ⌘S is ⌘s
        Assert.Equal("Item 4 (“Save As…”) uses ⌘S, already used by item 3 (“Save”).", form.Error);
        Assert.True(form.Items[2].HasKeyConflict);
        Assert.True(form.Items[3].HasKeyConflict);
        Assert.False(form.Items[0].HasKeyConflict);
        var e = Assert.Throws<ArgumentException>(() => form.BuildData());
        Assert.Equal(form.Error, e.Message);
        Assert.Equal((true, form.Error), form.Draft);

        form.Items[3].Key = "A";
        Assert.Null(form.Error);
        Assert.False(form.Items[2].HasKeyConflict);
        Assert.False(form.Items[3].HasKeyConflict);
    }

    [Fact]
    public void A_bad_key_or_mark_is_the_form_s_error()
    {
        var form = Form();
        form.Items[0].Key = "ab";
        Assert.Contains("not a Command key", form.Error);
    }

    [Fact]
    public void Add_item_and_add_divider_append_rows_and_remove_and_move_change_the_list()
    {
        var form = Form();
        form.AddCommand.Execute(null);
        form.AddDividerCommand.Execute(null);
        Assert.Equal(7, form.Items.Count);
        Assert.Equal("Item", form.Items[5].Text);
        Assert.True(form.Items[6].IsDivider);
        Assert.Equal([6, 7], form.Items.Skip(5).Select(r => r.Number));
        Assert.Same(form.Items[6], form.SelectedItem);                     // a new row is selected
        form.RemoveCommand.Execute(form.Items[0]);
        Assert.Equal("-", form.Items[0].Text);
        Assert.Equal(1, form.Items[0].Number);
        form.MoveUpCommand.Execute(form.Items[1]);
        Assert.Equal("Save", form.Items[0].Text);
        form.MoveDownCommand.Execute(form.Items[0]);
        Assert.Equal("Save", form.Items[1].Text);
    }

    [Fact]
    public void The_preview_follows_the_values_and_keeps_the_last_good_one_on_an_error()
    {
        var form = Form();
        Assert.Equal("File", form.Preview!.Title);
        form.Title = "Fichier";
        Assert.Equal("Fichier", form.Preview!.Title);
        form.Items[0].Key = "xx";
        Assert.Equal("Fichier", form.Preview!.Title);
        Assert.NotNull(form.Error);
    }

    [Fact]
    public void Rows_and_the_preview_select_each_other()
    {
        var form = Form();
        Assert.Null(form.SelectedItem);
        Assert.Equal(-1, form.PreviewIndex);
        form.SelectedItem = form.Items[2];
        Assert.Equal(2, form.PreviewIndex);
        Assert.True(form.Items[2].IsSelected);
        form.PreviewIndex = 4;
        Assert.Same(form.Items[4], form.SelectedItem);
        Assert.False(form.Items[2].IsSelected);
        form.PreviewIndex = 9;                                             // outside the menu: nothing
        Assert.Null(form.SelectedItem);
        form.SelectRow(form.Items[1]);
        Assert.Same(form.Items[1], form.SelectedItem);
        form.SelectRow("not a row");
        Assert.Same(form.Items[1], form.SelectedItem);
        form.SelectedItem = form.Items[1];
        form.RemoveCommand.Execute(form.Items[1]);
        Assert.Null(form.SelectedItem);
        Assert.Equal(-1, form.PreviewIndex);
    }
}
