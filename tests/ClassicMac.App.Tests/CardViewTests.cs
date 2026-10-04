using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Interface;

namespace ClassicMac.App.Tests;

// The window, alert and control forms in the window (design/boards/window-alert.md, E4): property cards read only,
// inputs after Edit; the alert's stages select the preview's default button.
public sealed class CardViewTests
{
    private static void Pump(Task task) => Headless.Pump(task);

    private static List<string?> Shown(Control root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];

    [Fact]
    public void Window_alert_and_control_forms_read_as_cards_then_edit() => Headless.OnUiThread(() =>
    {
        var folder = Directory.CreateTempSubdirectory("cm-cardview").FullName;
        try
        {
            var items = InterfaceWriter.WriteDialogItems([
                new DialogItem(new MacRect(80, 250, 100, 320), 4, true, "Save", null, ReadOnlyMemory<byte>.Empty),
                new DialogItem(new MacRect(80, 160, 100, 230), 4, true, "Cancel", null, ReadOnlyMemory<byte>.Empty)]);
            var path = Path.Combine(folder, "Cards.rsrc");
            File.WriteAllBytes(path, PreviewTests.Fork(
                ("WIND", 128, null, InterfaceWriter.WriteWindow(new WindowTemplate(new MacRect(40, 6, 322, 506), 0, false, true, 0, "Untitled", null, 0x780A), dialog: false)),
                ("ALRT", 128, null, InterfaceWriter.WriteAlert(new AlertTemplate(new MacRect(40, 40, 160, 380), 129, 0x9C42, 0x300A))),
                ("DITL", 129, "Save Changes", items),
                ("CNTL", 128, null, InterfaceWriter.WriteControl(new ControlTemplate(new MacRect(10, 20, 30, 100), 1, true, 1, 0, 1, 0, "Agree")))));
            var model = new MainViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            var open = model.OpenAsync(path);
            Pump(open);
            Pump(open.Result!.EnsureLoadedAsync());
            NodeViewModel Resource(string type) => open.Result!.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children[0];

            // The window: read only, then inputs.
            model.Selected = Resource("WIND");
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.FormEditing.ShowsForm);
            var cards = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "WindowCards");
            Assert.Contains("“Untitled”", Shown(cards));
            Assert.Contains("documentProc · 0", Shown(cards));
            Assert.Contains("Stagger on parent window’s screen", Shown(cards));
            Assert.Contains("Bounds on a 512 × 342 screen", Shown(cards));
            Assert.True(cards.GetVisualDescendants().OfType<ScreenBoundsView>().Single().IsEffectivelyVisible);
            Assert.DoesNotContain(cards.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible && c.ItemsSource == ((WindowForm)model.Forms.Form!).Definitions);
            model.FormEditing.EditFormCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(cards.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible && c.SelectedItem is DefinitionChoice { Value: 0 });
            Assert.DoesNotContain("“Untitled”", Shown(cards));
            model.FormEditing.CancelFormCommand.Execute(null);

            // The alert: the stages table; a stage chosen redraws the preview with its default button.
            model.Selected = Resource("ALRT");
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var alert = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "AlertCards");
            Assert.Contains("'DITL' 129 “Save Changes”", alert.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()));
            Assert.Contains("Item 2 “Cancel”", Shown(alert));
            Assert.Contains("4th", Shown(alert));
            // A click on the 3rd stage's row selects it (the host's row selection).
            var row = alert.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("form-row") && b.DataContext is AlertStage { Number: 3 });
            row.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var at = row.TranslatePoint(new Avalonia.Point(20, 10), window)!.Value;
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("selected", row.Classes);
            Assert.Equal(2, model.FormLivePreview.FormDialog!.Drawing.DefaultItem);
            Assert.Contains("Stage 3: default button is Item 2 “Cancel”, plays no sound.", Shown(alert));

            // The control.
            model.Selected = Resource("CNTL");
            Pump(model.PreviewTask);
            Dispatcher.UIThread.RunJobs();
            var control = window.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "ControlCards");
            Assert.Contains("checkBoxProc · 1", Shown(control));
            Assert.Contains("Check box", Shown(control));
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    });
}
