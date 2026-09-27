using System.Buffers.Binary;
using ClassicMac.App.ViewModels;
using ClassicMac.Core;
using ClassicMac.Files;
using ClassicMac.Files.Tests;
using ClassicMac.Resources;

namespace ClassicMac.App.Tests;

public class PreviewTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("classicmac-preview-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    internal static byte[] Fork(params (string Type, short Id, string? Name, byte[] Data)[] resources)
    {
        var fork = new ResourceFork();
        foreach (var (type, id, name, data) in resources)
        {
            var r = new Resource(FourCC.FromString(type), id, data);
            if (name is not null) r.Name = MacString.FromMacRoman(name);
            fork.Add(r);
        }
        return fork.ToArray();
    }

    // A styl with one run per (start, font, face, size, r, g, b).
    internal static byte[] Styl(params (int Start, short Font, byte Face, short Size, ushort R, ushort G, ushort B)[] runs)
    {
        var data = new byte[2 + runs.Length * 20];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)runs.Length);
        for (var i = 0; i < runs.Length; i++)
        {
            var e = data.AsSpan(2 + i * 20);
            var (start, font, face, size, r, g, b) = runs[i];
            BinaryPrimitives.WriteInt32BigEndian(e, start);
            BinaryPrimitives.WriteInt16BigEndian(e[8..], font);
            e[10] = face;
            BinaryPrimitives.WriteInt16BigEndian(e[12..], size);
            BinaryPrimitives.WriteUInt16BigEndian(e[14..], r);
            BinaryPrimitives.WriteUInt16BigEndian(e[16..], g);
            BinaryPrimitives.WriteUInt16BigEndian(e[18..], b);
        }
        return data;
    }

    // A version 1 picture, 4×4, with a black 2×2 square.
    internal static readonly byte[] Picture = [0, 0, 0, 0, 0, 0, 0, 4, 0, 4, 0x11, 0x01, 0x31, 0, 0, 0, 0, 0, 2, 0, 2, 0xFF];

    internal static readonly byte[] Pascal = [2, (byte)'h', (byte)'i'];

    // An HFS disk with one file holding a resource of each kind the preview shows.
    private string Disk()
    {
        var text = MacRoman.Encode("Title\rBody");
        var disk = new HfsBuilder();
        disk.File(HfsBuilder.Root, "Resources", [], Fork(
            ("ICN#", 128, null, [.. Enumerable.Repeat((byte)0xFF, 4), .. new byte[124], .. Enumerable.Repeat((byte)0xFF, 128)]),
            ("PICT", 128, null, Picture),
            ("STR#", 128, null, [0, 2, .. Pascal, .. Pascal]),
            ("TEXT", 128, null, text),
            ("styl", 128, null, Styl((0, 20, 1, 18, 0, 0, 0), (6, 4, 0, 10, 0xFFFF, 0, 0))),
            ("vers", 1, null, [1, 0x20, 0x80, 0, 0, 0, .. Pascal, .. Pascal]),
            ("CODE", 1, null, [0x4E, 0x75])));
        var pict = new byte[512 + Picture.Length];
        Picture.CopyTo(pict, 512);
        disk.File(HfsBuilder.Root, "Picture", pict, [], type: "PICT", creator: "ttxt");
        var path = Path.Combine(folder, "disk.img");
        File.WriteAllBytes(path, disk.Build("Disk"));
        return path;
    }

    private static async Task<(MainViewModel Model, FileNode File)> Open(string path)
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(path))!;
        var file = input.Children.OfType<FileNode>().Single(f => f.Title == "Resources");
        await file.EnsureLoadedAsync();
        return (model, file);
    }

    private static async Task<PreviewViewModel> Select(MainViewModel model, NodeViewModel node)
    {
        model.Selected = node;
        await model.PreviewTask;
        return model.Preview;
    }

    private static ResourceNode Resource(FileNode file, string type, short id) =>
        file.Children.OfType<ResourceTypeNode>().Single(t => t.Type.ToString() == type).Children.OfType<ResourceNode>().Single(r => r.Resource.Id == id);

    [Fact]
    public async Task Resources_preview_as_images_text_or_JSON()
    {
        var (model, file) = await Open(Disk());

        var icon = await Select(model, Resource(file, "ICN#", 128));
        Assert.Equal(PreviewKind.Image, icon.Kind);
        Assert.Equal((32, 32), (icon.Images[0].Width, icon.Images[0].Height));
        Assert.Equal(4, model.Zoom); // small images open enlarged
        Assert.Equal(128, model.Images[0].Width);
        Assert.Equal(1, model.SelectedTab);

        var picture = await Select(model, Resource(file, "PICT", 128));
        Assert.Equal((4, 4), (picture.Images[0].Width, picture.Images[0].Height));

        var strings = await Select(model, Resource(file, "STR#", 128));
        Assert.Equal((PreviewKind.Text, "   1  hi\n   2  hi"), (strings.Kind, strings.Text));

        var text = await Select(model, Resource(file, "TEXT", 128));
        Assert.True(text.IsStyledText);
        Assert.Equal(["Times", "Monaco"], text.Styled!.Runs.Select(r => r.FontName));

        var version = await Select(model, Resource(file, "vers", 1));
        Assert.Equal(PreviewKind.Json, version.Kind);
        Assert.Contains("\"display\": \"1.2\"", version.Text);

        var code = await Select(model, Resource(file, "CODE", 1));
        Assert.Equal(PreviewKind.None, code.Kind);
        Assert.Equal(0, model.SelectedTab); // back to details
    }

    [Fact]
    public async Task Changing_the_screen_depth_redraws()
    {
        var (model, file) = await Open(Disk());
        var first = await Select(model, Resource(file, "PICT", 128));

        model.ScreenDepth = 1;
        await model.PreviewTask;

        Assert.NotSame(first, model.Preview);
        Assert.Equal(PreviewKind.Image, model.Preview.Kind);
    }

    [Fact]
    public async Task Picture_files_preview_after_their_header()
    {
        var model = new MainViewModel();
        var input = (await model.OpenAsync(Disk()))!;

        var preview = await Select(model, input.Children.OfType<FileNode>().Single(f => f.Title == "Picture"));

        Assert.Equal(PreviewKind.Image, preview.Kind);
        Assert.Equal(4, preview.Images[0].Width);
    }

    [Fact]
    public async Task Hex_lines_show_offsets_bytes_and_characters_and_read_lazily()
    {
        var (model, file) = await Open(Disk());
        model.Selected = Resource(file, "STR#", 128);

        var line = Assert.Single(model.HexLines!);
        Assert.Equal(("00000000", "00 02 02 68 69 02 68 69", "···hi·hi"), (line.Offset, line.Hex, line.Characters));

        var big = new HexLines(ForkData.FromBytes(new byte[10 * 1024 * 1024]));
        Assert.Equal(10 * 1024 * 1024 / 16, big.Count);
        _ = big[big.Count - 1];
        Assert.Equal(64 * 1024, big.BytesRead);

        model.Selected = file;
        Assert.Equal(["Resource fork"], model.Hex.Sources.Select(s => s.Label.Split(" (")[0]));
    }
}
