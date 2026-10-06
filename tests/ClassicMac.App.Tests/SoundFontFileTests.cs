using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;
using SkiaSharp;

namespace ClassicMac.App.Tests;

// Sound and font files previewed as what they hold: WAV files and System 7 sound files ('sfil') as sounds, font
// suitcases as their family, TrueType and OpenType files as a sample drawn in the font.
public class SoundFontFileTests : IDisposable
{
    private const string NoCode = "\0\0\0\0";

    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-sound-font-").FullName;

    private MainViewModel model = null!;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    // A WAV file: RIFF, WAVE, a fmt chunk, a data chunk (little-endian).
    private static byte[] Wav(ushort tag, ushort channels, uint rate, ushort bits, byte[] samples)
    {
        var block = (ushort)(channels * ((bits + 7) / 8));
        var fmt = new byte[24];
        "fmt "u8.CopyTo(fmt);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(8), tag);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(10), channels);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(12), rate);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(16), rate * block);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(20), block);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(22), bits);
        var data = new byte[8];
        "data"u8.CopyTo(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)samples.Length);
        byte[] body = [.. "WAVE"u8, .. fmt, .. data, .. samples];
        var riff = new byte[8];
        "RIFF"u8.CopyTo(riff);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Length);
        return [.. riff, .. body];
    }

    private async Task<PreviewViewModel> Preview(string name, byte[] data, byte[] fork, string type, string creator)
    {
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, name, data, fork, type, creator);
        var path = Path.Combine(folder, "files.img");
        File.WriteAllBytes(path, disk.Build("Files"));
        model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        model.Selected = input.Children.OfType<FileNode>().Single();
        await model.PreviewTask;
        return model.Preview;
    }

    [Fact]
    public async Task A_WAV_file_previews_as_a_sound()
    {
        var wav = Wav(1, 2, 22050, 16, new byte[400]);

        var preview = await Preview("beep.wav", wav, [], NoCode, NoCode);

        Assert.Equal(PreviewKind.Sound, preview.Kind);
        Assert.Contains(new SoundFact("Format", "WAV"), preview.SoundFacts);
        Assert.Contains(new SoundFact("Channels", "stereo"), preview.SoundFacts);
        Assert.Equal(100, preview.Sound!.Frames);
    }

    [Fact]
    public async Task A_WAV_file_ClassicMac_cannot_decode_says_why()
    {
        var wav = Wav(0x55, 1, 22050, 0, new byte[400]);

        var preview = await Preview("song.wav", wav, [], "WAVE", "TVOD");

        Assert.Equal(PreviewKind.SoundError, preview.Kind);
        Assert.Contains("is not PCM or float", preview.ErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_float_WAV_file_previews_as_16_bit()
    {
        var wav = Wav(3, 1, 8000, 32, new byte[40]);

        var preview = await Preview("float.wav", wav, [], NoCode, NoCode);

        Assert.Equal(PreviewKind.Sound, preview.Kind);
        Assert.Equal(10, preview.Sound!.Frames);
    }

    [Fact]
    public async Task A_System_7_sound_file_previews_its_sound()
    {
        var fork = PreviewTests.Fork(("snd ", 8192, "Quack", SoundPreviewTests.Sound()));

        var preview = await Preview("Quack", [], fork, "sfil", "movr");

        Assert.Equal(PreviewKind.Sound, preview.Kind);
        Assert.Equal(1000, preview.Sound!.Frames);
    }

    [Fact]
    public async Task A_font_suitcase_previews_its_family()
    {
        var preview = await Preview("Tester", [], FontFixtures.Fork(), "FFIL", "DMOV");

        Assert.Equal(PreviewKind.FontFamily, preview.Kind);
        Assert.Equal("Tester", preview.FontFamily!.Name);
    }

    [Fact]
    public async Task A_TrueType_file_previews_a_sample_in_its_font()
    {
        var font = File.ReadAllBytes(SampleFont());

        var preview = await Preview("Sample.ttf", font, [], NoCode, NoCode);

        Assert.Equal(PreviewKind.Image, preview.Kind);
        var image = Assert.Single(preview.Images);
        Assert.Matches("^(TrueType|OpenType)( collection)? · .+", image.Detail);
        using var drawn = SKBitmap.Decode(image.Png);
        Assert.Contains(Enumerable.Range(0, drawn.Height).SelectMany(y => Enumerable.Range(0, drawn.Width).Select(x => drawn.GetPixel(x, y))),
            c => c.Red < 128 && c.Alpha == 255);
    }

    [Fact]
    public async Task A_file_that_is_not_a_font_has_no_font_preview()
    {
        var preview = await Preview("broken.ttf", [0, 1, 0, 0, 9, 9], [], NoCode, NoCode);

        Assert.NotEqual(PreviewKind.Image, preview.Kind);
    }

    // A TrueType font the platform has, as any system carries one (its own UI font): written out by SkiaSharp.
    private string SampleFont()
    {
        using var typeface = SKTypeface.Default;
        using var stream = typeface.OpenStream(out _);
        var bytes = new byte[stream.Length];
        stream.Read(bytes, bytes.Length);
        var path = Path.Combine(folder, "sample.ttf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
