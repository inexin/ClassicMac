using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.Resources.Decoders.Tests;

// The interface templates written back from what the readers give.
public class InterfaceWriterTests
{
    [Fact]
    public void Templates_round_trip()
    {
        var window = new WindowTemplate(new MacRect(40, 40, 140, 280), 4, true, true, 7, "Title", 128, 0x300A);
        var dlog = InterfaceWriter.WriteWindow(window, dialog: true);
        Assert.Equal(window, InterfaceResources.ReadWindow(dlog, true, DecodeOptions.Default, [], ""));
        Assert.Equal(0, dlog.Length % 2);                                    // the positioning word is word-aligned

        var alert = new AlertTemplate(new MacRect(10, 20, 110, 320), 129, 0x5555, null);
        Assert.Equal(alert, InterfaceResources.ReadAlert(InterfaceWriter.WriteAlert(alert), [], ""));

        var control = new ControlTemplate(new MacRect(1, 2, 3, 4), 1, true, 10, 0, 16, -1, "Scroll");
        Assert.Equal(control, InterfaceResources.ReadControl(InterfaceWriter.WriteControl(control), DecodeOptions.Default, [], ""));

        var items = InterfaceResources.ReadDialogItems(InterfaceWriter.WriteDialogItems([
            new DialogItem(new MacRect(70, 150, 90, 220), 4, true, "OK", null, ReadOnlyMemory<byte>.Empty),
            new DialogItem(new MacRect(10, 10, 42, 42), 32, false, null, 128, ReadOnlyMemory<byte>.Empty),
            new DialogItem(new MacRect(0, 0, 10, 10), 0, true, null, null, new byte[] { 1, 2, 3 })]), DecodeOptions.Default, [], "");
        Assert.Equal(("OK", 4, true), (items[0].Text, items[0].Type, items[0].Enabled));
        Assert.Equal(((short?)128, 32, false), (items[1].ResourceId, items[1].Type, items[1].Enabled));
        Assert.Equal(new byte[] { 1, 2, 3 }, items[2].Data.ToArray());   // a user item's data as stored
    }

    [Fact]
    public void Menus_keep_the_enable_bits_of_dividers_and_of_absent_items()
    {
        var menu = new MenuResource(128, 0, 0, 0, 0xFFFFFFFF, "File",
            [new MenuItem("Open…", 0, (byte)'O', 0, 0, false), new MenuItem("-", 0, 0, 0, 0, false), new MenuItem("Quit", 0, (byte)'Q', 0, 0, true)]);
        var data = InterfaceWriter.WriteMenu(menu);
        var read = InterfaceResources.ReadMenu(data, DecodeOptions.Default, [], "");
        Assert.Equal(0xFFFFFFFDu, read.EnableFlags);                      // item 1 off; the divider's and items 4-31's bits kept
        Assert.Equal(["Open…", "-", "Quit"], read.Items.Select(i => i.Text));
        Assert.Throws<ArgumentException>(() => InterfaceWriter.WriteMenu(menu with { Items = [new MenuItem("", 0, 0, 0, 0, true)] }));
    }
}
