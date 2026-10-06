using System.Buffers.Binary;
using System.Text;
using ClassicMac.App.ViewModels;
using ClassicMac.Files.Tests;
using SkiaSharp;

namespace ClassicMac.App.Tests;

// Files previewed as what they are: pictures (JPEG, PNG, GIF and BMP by the platform's decoders, MacPaint and QuickTime
// image files by ClassicMac's), and text files that have no Mac type, by their extension.
public class FilePreviewTests : IDisposable
{
    private const string NoCode = "\0\0\0\0";

    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-file-preview-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static byte[] Encoded(SKEncodedImageFormat format, int width = 3, int height = 2)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(0x20, 0x80, 0xE0));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    // A MacPaint document: a 512-byte header (version 0), then 720 rows of 72 white bytes, each one PackBits run.
    private static byte[] MacPaint()
    {
        var rows = Enumerable.Range(0, 720).SelectMany(_ => new byte[] { 0xB9, 0x00 });
        return [.. new byte[512], .. rows];
    }

    // A QuickTime image file: an 'idsc' atom with a 2×1 'raw ' 32-bit description, an 'idat' atom with its pixels.
    private static byte[] QuickTimeImage()
    {
        var description = new byte[86];
        BinaryPrimitives.WriteInt32BigEndian(description, 86);
        "raw "u8.CopyTo(description.AsSpan(4));
        BinaryPrimitives.WriteInt16BigEndian(description.AsSpan(32), 2);       // width
        BinaryPrimitives.WriteInt16BigEndian(description.AsSpan(34), 1);       // height
        BinaryPrimitives.WriteInt32BigEndian(description.AsSpan(36), 72 << 16);
        BinaryPrimitives.WriteInt32BigEndian(description.AsSpan(40), 72 << 16);
        BinaryPrimitives.WriteInt16BigEndian(description.AsSpan(48), 1);       // frame count
        BinaryPrimitives.WriteInt16BigEndian(description.AsSpan(82), 32);      // depth
        BinaryPrimitives.WriteInt16BigEndian(description.AsSpan(84), -1);      // clut ID
        byte[] pixels = [0xFF, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF];
        byte[] Atom(string type, byte[] body)
        {
            var atom = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32BigEndian(atom, atom.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(atom, 4);
            body.CopyTo(atom, 8);
            return atom;
        }

        return [.. Atom("idsc", description), .. Atom("idat", pixels)];
    }

    // The files the tests read, each on a disk of its own.
    private static readonly Dictionary<string, (byte[] Data, string Type, string Creator)> Files = new()
    {
        ["Photo"] = (Encoded(SKEncodedImageFormat.Png), "PNGf", "8BIM"),
        ["photo.jpg"] = (Encoded(SKEncodedImageFormat.Jpeg, 4, 3), NoCode, NoCode),
        ["Not a picture.jpg"] = ("hello"u8.ToArray(), NoCode, NoCode),
        ["Painting"] = (MacPaint(), "PNTG", "MPNT"),
        ["Still"] = (QuickTimeImage(), "qtif", "ogle"),
        ["readme.txt"] = (Encoding.UTF8.GetBytes("Café au lait\n"), NoCode, NoCode),
        ["notes.txt"] = ([(byte)'C', (byte)'a', (byte)'f', 0x8E], "????", "????"),
    };

    private MainViewModel model = null!;

    private async Task<PreviewViewModel> Preview(string name)
    {
        var (data, type, creator) = Files[name];
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, name, data, [], type, creator);
        var path = Path.Combine(folder, "files.img");
        File.WriteAllBytes(path, disk.Build("Files"));
        model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        model.Selected = input.Children.OfType<FileNode>().Single();
        await model.PreviewTask;
        return model.Preview;
    }

    [Theory]
    [InlineData("Photo", 3, 2, "PNG")]
    [InlineData("photo.jpg", 4, 3, "JPEG")]
    [InlineData("Painting", 576, 720, "MacPaint")]
    [InlineData("Still", 2, 1, "QuickTime image")]
    public async Task A_picture_file_previews_as_its_image(string name, int width, int height, string format)
    {
        var preview = await Preview(name);

        Assert.Equal(PreviewKind.Image, preview.Kind);
        var image = Assert.Single(preview.Images);
        Assert.Equal((width, height), (image.Width, image.Height));
        using var decoded = SKBitmap.Decode(image.Png);
        Assert.Equal((width, height), (decoded.Width, decoded.Height));
        Assert.Equal($"{width}×{height} · {format}", image.Detail);
        Assert.Equal(1, model.SelectedTab);
    }

    [Fact]
    public async Task A_QuickTime_image_keeps_its_pixels()
    {
        var preview = await Preview("Still");

        using var decoded = SKBitmap.Decode(preview.Images[0].Png);
        Assert.Equal(new SKColor(0xFF, 0x00, 0x00), decoded.GetPixel(0, 0));
        Assert.Equal(new SKColor(0x00, 0x00, 0xFF), decoded.GetPixel(1, 0));
    }

    [Fact]
    public async Task A_file_that_only_looks_like_a_picture_has_no_preview()
    {
        var preview = await Preview("Not a picture.jpg");

        Assert.NotEqual(PreviewKind.Image, preview.Kind);
    }

    [Theory]
    [InlineData("readme.txt", "Café au lait")]
    [InlineData("notes.txt", "Café")]
    public async Task A_text_file_without_a_type_previews_its_text(string name, string text)
    {
        var preview = await Preview(name);

        Assert.Equal(PreviewKind.Text, preview.Kind);
        Assert.Equal(text, preview.Text.TrimEnd('\n'));
    }
}
