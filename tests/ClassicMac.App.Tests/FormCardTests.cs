using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

// The window, dialog, alert and control forms on the read-then-edit host as property cards (design/boards/window-alert.md,
// E4): read-only text for each value, named definitions and positions, and alert stages with their default button.
public sealed class FormCardTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-cards").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static Resource Res(string type, short id) => new(FourCC.FromString(type), id, Array.Empty<byte>());

    private static WindowForm Window(short definition = 0, ushort? position = 0x780A, bool dialog = false) =>
        new(Res(dialog ? "DLOG" : "WIND", 128), new WindowTemplate(new MacRect(40, 6, 322, 506), definition, false, true, 0, "Untitled",
            dialog ? (short)129 : null, position), dialog);

    [Fact]
    public void A_window_reads_as_property_cards()
    {
        var form = Window();
        Assert.True(form.HasReadOnlyView);
        Assert.Equal(("40, 6", "322, 506", "500 × 282 pixels"), (form.TopLeftText, form.BottomRightText, form.SizeText));
        Assert.Equal(("“Untitled”", "Document window", "documentProc · 0"), (form.TitleText, form.DefinitionName, form.DefinitionCode));
        Assert.Equal(("No", "Yes", "0"), (form.VisibleText, form.GoAwayText, form.RefConText));
        Assert.Equal(("Stagger on parent window’s screen", "0x780A"), (form.PositionName, form.PositionCode));
        Assert.False(form.IsDialog);
    }

    [Fact]
    public void Window_kinds_are_named()
    {
        var form = Window();
        Assert.Equal(["Document window · 0", "Dialog box · 1", "Plain box · 2", "Alt dialog box · 3", "No grow document · 4", "Movable modal · 5",
            "Zoom document · 8", "Zoom no grow · 12", "Rounded · 16"], form.Definitions.Select(d => d.ToString()));
        Assert.Equal(0, form.SelectedDefinition!.Value);
        form.SelectedDefinition = form.Definitions.Single(d => d.Value == 8);
        Assert.Equal(8, form.Definition);
        Assert.Equal(("Zoom document", "zoomDocProc · 8"), (form.DefinitionName, form.DefinitionCode));

        // Another WDEF: named by its ID and variation, kept as a choice.
        var other = Window(definition: 130);
        Assert.Equal(("WDEF 8, variation 2", "130"), (other.DefinitionName, other.DefinitionCode));
        Assert.Equal("Other WDEF · 130", other.SelectedDefinition!.ToString());
        Assert.Contains(other.SelectedDefinition, other.Definitions);
        var rounded = Window(definition: 18);
        Assert.Equal(("Rounded, variation 2", "rDocProc · 18"), (rounded.DefinitionName, rounded.DefinitionCode));
    }

    [Fact]
    public void Positions_are_named()
    {
        var form = Window();
        Assert.Equal(10, CardForm.Positions.Count);
        Assert.Equal("None", CardForm.Positions[0].Name);
        Assert.Equal(0x780A, form.SelectedPosition!.Code);
        form.SelectedPosition = CardForm.Positions.Single(p => p.Code == 0x280A);
        Assert.Equal(0x280A, form.Position);
        Assert.Equal(("Center on main screen", "0x280A"), (form.PositionName, form.PositionCode));

        var none = Window(position: null);
        Assert.False(none.HasPosition);
        Assert.Equal(("No positioning word", null), (none.PositionName, none.PositionCode));
        var odd = Window(position: 0x1234);
        Assert.Equal(("Other", "0x1234"), (odd.PositionName, odd.PositionCode));
        Assert.Null(odd.SelectedPosition);
    }

    // The right panel: the bounds on a screen of the chosen size (not an edit).
    [Fact]
    public void The_bounds_are_shown_on_a_screen()
    {
        var form = Window();
        var edits = 0;
        form.Edited += (_, _) => edits++;
        Assert.Equal(["512 × 342 Classic", "640 × 480", "832 × 624", "1024 × 768"], WindowForm.Screens.Select(s => s.Name));
        Assert.Same(WindowForm.Screens[0], form.SelectedScreen);
        Assert.Equal("Bounds on a 512 × 342 screen", form.ScreenTitle);
        form.SelectedScreen = WindowForm.Screens[2];
        Assert.Equal("Bounds on a 832 × 624 screen", form.ScreenTitle);
        Assert.Equal((832, 624), (form.SelectedScreen.Width, form.SelectedScreen.Height));
        Assert.Equal(0, edits);
    }

    [Fact]
    public void A_dialog_names_its_item_list()
    {
        var form = Window(dialog: true);
        Assert.True(form.IsDialog);
        Assert.Equal("'DITL' 129", form.ItemsText);
    }

    [Fact]
    public void Display_values_are_not_edits()
    {
        var form = Window();
        var edits = 0;
        form.Edited += (_, _) => edits++;
        form.Top = 41;
        Assert.Equal(1, edits);
        Assert.Equal("41, 6", form.TopLeftText);
        Assert.Equal(41, InterfaceResources.ReadWindow(form.BuildData(), false, ClassicMac.Resources.Decoders.DecodeOptions.Default, [], "").Bounds.Top);
    }

    // An alert with DITL 129 “Save Changes” of three items: Save, Cancel, a message.
    private static AlertForm Alert(ushort stages = 0x9C42)
    {
        string[] texts = ["Save", "Cancel", "Save changes?"];
        return new AlertForm(Res("ALRT", 128), new AlertTemplate(new MacRect(40, 40, 160, 380), 129, stages, 0x300A),
            id => id == 129 ? new ItemList("Save Changes", texts) : null);
    }

    [Fact]
    public void An_alert_reads_as_a_card_and_stages()
    {
        var form = Alert();
        Assert.True(form.HasReadOnlyView);
        Assert.Equal(("40, 40, 160, 380", "340 × 120"), (form.BoundsText, form.SizeText));
        Assert.Equal(("'DITL' 129 “Save Changes”", "3 items"), (form.ItemsText, form.ItemCountText));
        Assert.Equal(("Alert position on main screen", "0x300A"), (form.PositionName, form.PositionCode));

        // 0x9C42: stage 1 = 2 (item 1, not drawn, 2 beeps)… stage 4 = 9 (item 2, drawn?, …): read nibble by nibble.
        var stages = form.Stages;
        Assert.Equal(4, stages.Count);
        Assert.Equal(("1st", "Item 1 “Save”", "No", "2 beeps"), (stages[0].Ordinal, stages[0].DefaultText, stages[0].DrawnText, stages[0].SoundName));
        Assert.Equal(("Item 1 “Save”", "Yes", "Silent"), (stages[1].DefaultText, stages[1].DrawnText, stages[1].SoundName));
        Assert.Equal(("Item 2 “Cancel”", "Yes", "Silent"), (stages[2].DefaultText, stages[2].DrawnText, stages[2].SoundName));
        Assert.Equal(("Item 2 “Cancel”", "No", "1 beep"), (stages[3].DefaultText, stages[3].DrawnText, stages[3].SoundName));
    }

    [Fact]
    public void Stage_defaults_are_item_1_or_2_and_sounds_are_named()
    {
        var form = Alert();
        var stage = form.Stages[0];
        Assert.Equal(["Item 1 “Save”", "Item 2 “Cancel”"], stage.DefaultChoices);
        Assert.Equal(["Silent", "1 beep", "2 beeps", "3 beeps"], AlertStage.Sounds);
        stage.DefaultIndex = 1;
        Assert.Equal(2, stage.BoldItem);
        stage.SoundIndex = 3;
        Assert.Equal(3, stage.Sound);
        Assert.Equal(new AlertTemplate(new MacRect(40, 40, 160, 380), 129, 0x9C4B, 0x300A).Stages,
            InterfaceResources.ReadAlert(form.BuildData(), [], "").Stages);

        // Without the item list, the choices are plain.
        var bare = new AlertForm(Res("ALRT", 1), new AlertTemplate(new MacRect(0, 0, 10, 10), 5, 0, null), _ => null);
        Assert.Equal(["Item 1", "Item 2"], bare.Stages[0].DefaultChoices);
        Assert.Equal(("'DITL' 5", "not found"), (bare.ItemsText, bare.ItemCountText));
        Assert.Equal("No positioning word", bare.PositionName);
    }

    [Fact]
    public void Selecting_a_stage_describes_it_and_is_not_an_edit()
    {
        var form = Alert();
        var edits = 0;
        form.Edited += (_, _) => edits++;
        Assert.Same(form.Stages[0], form.SelectedStage);
        Assert.True(form.Stages[0].IsSelected);
        Assert.Equal("Stage 1: default button is Item 1 “Save”, plays 2 beeps, not drawn.", form.StageNote);

        form.SelectRow(form.Stages[2]);
        Assert.Same(form.Stages[2], form.SelectedStage);
        Assert.False(form.Stages[0].IsSelected);
        Assert.Equal("Stage 3: default button is Item 2 “Cancel”, plays no sound.", form.StageNote);
        form.SelectedStage = form.Stages[3];
        Assert.Equal("Stage 4: default button is Item 2 “Cancel”, plays 1 beep, not drawn.", form.StageNote);
        Assert.Equal(0, edits);
        form.Stages[3].SoundIndex = 0;
        Assert.Equal(1, edits);
        Assert.Equal("Stage 4: default button is Item 2 “Cancel”, plays no sound, not drawn.", form.StageNote);
    }

    [Fact]
    public void Controls_read_as_cards_with_named_kinds()
    {
        var form = new ControlForm(Res("CNTL", 128), new ControlTemplate(new MacRect(10, 20, 30, 100), 1, true, 1, 0, 9, 0, "Agree"));
        Assert.True(form.HasReadOnlyView);
        Assert.Equal(("10, 20", "30, 100", "80 × 20 pixels"), (form.TopLeftText, form.BottomRightText, form.SizeText));
        Assert.Equal(("Check box, window font", "checkBoxProc+useWFont · 9"), (form.DefinitionName, form.DefinitionCode));
        Assert.Equal(("“Agree”", "Yes", "1", "0", "1", "0"), (form.TitleText, form.VisibleText, form.ValueText, form.MinimumText, form.MaximumText, form.RefConText));
        Assert.Equal(["Push button · 0", "Check box · 1", "Radio button · 2", "Push button, window font · 8", "Check box, window font · 9",
            "Radio button, window font · 10", "Scroll bar · 16", "Pop-up menu · 1008"], form.Definitions.Select(d => d.ToString()));
        form.SelectedDefinition = form.Definitions.Single(d => d.Value == 16);
        Assert.Equal(("Scroll bar", "scrollBarProc · 16"), (form.DefinitionName, form.DefinitionCode));
        var other = new ControlForm(Res("CNTL", 1), new ControlTemplate(new MacRect(0, 0, 1, 1), 0, false, 0, 0, 1013, 0, ""));
        Assert.Equal(("Pop-up menu, variation 5", "popupMenuProc+popupFixedWidth+popupUseAddResMenu · 1013"), (other.DefinitionName, other.DefinitionCode));
        var cdef = new ControlForm(Res("CNTL", 2), new ControlTemplate(new MacRect(0, 0, 1, 1), 0, false, 0, 0, 2050, 0, ""));
        Assert.Equal(("CDEF 128, variation 2", "2050"), (cdef.DefinitionName, cdef.DefinitionCode));
        Assert.Equal("Other CDEF · 2050", cdef.SelectedDefinition!.ToString());
    }

    // In the model: the alert's item list comes from its fork, the preview draws the selected stage's default button,
    // and the item list's link selects it.
    [Fact]
    public async Task The_alert_form_in_the_model()
    {
        var items = InterfaceWriter.WriteDialogItems([
            new DialogItem(new MacRect(80, 250, 100, 320), 4, true, "Save", null, ReadOnlyMemory<byte>.Empty),
            new DialogItem(new MacRect(80, 160, 100, 230), 4, true, "Cancel", null, ReadOnlyMemory<byte>.Empty),
            new DialogItem(new MacRect(10, 60, 50, 320), 8, false, "Save changes?", null, ReadOnlyMemory<byte>.Empty)]);
        var path = Path.Combine(folder, "Alert.rsrc");
        File.WriteAllBytes(path, PreviewTests.Fork(("DITL", 129, "Save Changes", items),
            ("ALRT", 128, null, InterfaceWriter.WriteAlert(new AlertTemplate(new MacRect(40, 40, 160, 380), 129, 0x9C42, 0x300A)))));
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        await input.EnsureLoadedAsync();
        model.Selected = input.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == "ALRT").Children[0];
        await model.PreviewTask;

        var form = Assert.IsType<AlertForm>(model.Form);
        Assert.Equal("'DITL' 129 “Save Changes”", form.ItemsText);
        Assert.Equal(1, model.FormDialog!.Drawing.DefaultItem);
        form.SelectedStage = form.Stages[2];
        Assert.Equal(2, model.FormDialog!.Drawing.DefaultItem);

        model.ShowItemListCommand.Execute(form);
        Assert.Equal("DITL", Assert.IsType<ResourceNode>(model.Selected).Resource.Type.ToString());
        Assert.Equal(129, ((ResourceNode)model.Selected!).Resource.Id);
    }
}
