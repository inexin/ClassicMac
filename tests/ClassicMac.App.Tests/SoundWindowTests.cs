using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassicMac.App.ViewModels;
using ClassicMac.App.Views;
using ClassicMac.Files.Tests;

namespace ClassicMac.App.Tests;

using static Headless;

// The sound preview in the window (boards/sound.md, P3): the transport, the chips, the lanes with the loop band, a
// click on the waveform starting playback there, the playhead following the player, and the error card.
public sealed class SoundWindowTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("cm-sound-window").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private (MainWindow Window, MainViewModel Model, FakeAudioPlayer Player, List<ResourceNode> Sounds) Open()
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Sounds", [], PreviewTests.Fork(("snd ", 128, "Sine", SoundPreviewTests.Sound(20000)), ("snd ", 8192, null, [0, 3, 0, 0])));
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        var player = new FakeAudioPlayer();
        var model = new MainViewModel();
        model.SoundPlayback.AudioPlayer = player;
        var window = new MainWindow { DataContext = model };
        window.Show();
        Pump(model.OpenAsync(path));
        var file = model.Roots[0].Children.OfType<FileNode>().Single();
        Pump(file.EnsureLoadedAsync());
        file.IsExpanded = true;
        var sounds = file.Children.OfType<ResourceTypeNode>().Single().Children.OfType<ResourceNode>().ToList();
        return (window, model, player, sounds);
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public void The_sound_preview_plays_from_a_click_and_shows_the_playhead() => OnUiThread(() =>
    {
        var (window, model, player, sounds) = Open();
        var baselines = new List<string>();
        model.Selected = sounds[0];
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Named<Button>(window, "PlayButton").IsEffectivelyVisible);
        Assert.False(Named<Button>(window, "StopButton").IsEffectivelyVisible);
        Assert.True(Named<CheckBox>(window, "RepeatLoopBox").IsEffectivelyVisible);
        var chips = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("chip") && b.IsEffectivelyVisible).ToList();
        Assert.Equal(7, chips.Count);
        var wave = window.GetVisualDescendants().OfType<WaveformView>().Single();
        Assert.Equal(170 + WaveformView.RulerHeight, wave.Bounds.Height);   // one 170 px lane for a mono sound, then the ruler
        Assert.Equal((10, 19990), wave.LoopFrames);
        Baselines.Check(window, "sound-preview", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);

        // A click a quarter of the way along starts playback there.
        var point = wave.TranslatePoint(new Point(wave.Bounds.Width / 4, 60), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var duration = model.Preview.Sound!.Duration;
        Assert.Equal(duration / 4, player.Played.Single().Start, 3);
        Assert.True(model.SoundPlayback.IsPlaying);
        Assert.True(Named<Button>(window, "StopButton").IsEffectivelyVisible);

        Assert.True(window.IsFollowingPlayhead);                             // the view's timer follows the player
        player.Position = duration / 2;
        model.SoundPlayback.RefreshPlayhead();                                             // (what its tick does)
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(duration / 2, model.SoundPlayback.Playhead, 3);
        Assert.Equal(duration / 2, wave.Playhead, 3);

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");   // Space stops
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.SoundPlayback.IsPlaying);
        Assert.False(window.IsFollowingPlayhead);
        window.Close();
        Baselines.Verify(baselines);
    });

    [Fact]
    public void An_undecodable_sound_shows_the_error_card() => OnUiThread(() =>
    {
        var (window, model, _, sounds) = Open();
        var baselines = new List<string>();
        model.Selected = sounds[1];
        Pump(model.PreviewTask);
        Dispatcher.UIThread.RunJobs();
        var card = Named<Border>(window, "SoundErrorCard");
        Assert.True(card.IsEffectivelyVisible);
        var texts = card.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("No preview for 'snd ' 8192", texts);
        Assert.Contains(texts, t => t?.Contains("sound.unknown-format") == true);
        var buttons = card.GetVisualDescendants().OfType<Button>().ToList();
        Assert.Equal(["Show in Hex", "Save raw data…"], buttons.Select(b => (string)b.Content!));
        Baselines.Check(window, "sound-error", baselines, Baselines.Variant.Light, Baselines.Variant.Dark);
        buttons[0].Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, model.SelectedTab);
        window.Close();
        Baselines.Verify(baselines);
    });
}
