using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Graphics.Fonts;
using ClassicMac.Graphics.Tests.Fonts;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// DecodeOptions.LoadableFonts (outline-fonts.md §3, §5): an 'sfnt' exported as a .ttf that Windows and other modern
// loaders take, what was added listed in the JSON; off (the default), the data as it is.
public sealed class LoadableFontDecoderTests
{
    private static IReadOnlyList<DecodedFile> Decode(byte[] sfnt, DecodeOptions options)
    {
        var resource = new Resource(FourCC.FromString("sfnt"), 128, sfnt);
        var fork = new ResourceFork();
        fork.Add(resource);
        var decoder = ResourceDecoders.Create(options).First(d => d.CanDecode(resource.Type));
        return decoder.Decode(new DecodeInput(resource, sfnt, fork));
    }

    [Fact]
    public void A_TrueType_font_exports_loadable_when_asked()
    {
        var sfnt = TrueTypeBuilder.Mac().Build();

        var plain = Decode(sfnt, DecodeOptions.Default);
        var loadable = Decode(sfnt, DecodeOptions.Default with { LoadableFonts = true });

        Assert.False(DecodeOptions.Default.LoadableFonts);
        Assert.Equal(sfnt, plain[0].Content.ToArray());
        Assert.False(JsonDocument.Parse(plain[1].Content).RootElement.TryGetProperty("loadable", out _));
        Assert.Equal(".ttf", loadable[0].Extension);
        Assert.Equal(LoadableFont.Make(sfnt), loadable[0].Content.ToArray());
        Assert.Equal(["cmap (3,1)", "name (Windows)", "OS/2", "post"],
            JsonDocument.Parse(loadable[1].Content).RootElement.GetProperty("loadable").EnumerateArray().Select(e => e.GetString()));
    }

    // Mac OS 9's own TrueType fonts (suitcases as raw resource forks, in CLASSICMAC_MAC_FONTS; Apple's and others',
    // never committed): every one that Windows' GDI refuses is loaded once made loadable.
    [Fact]
    public void Mac_OS_fonts_load_in_Windows_once_made_loadable()
    {
        var folder = Environment.GetEnvironmentVariable("CLASSICMAC_MAC_FONTS");
        if (!OperatingSystem.IsWindows() || folder is null || !Directory.Exists(folder))
        {
            Assert.Skip("Set CLASSICMAC_MAC_FONTS to a folder of font suitcases (raw resource forks), on Windows.");
        }

        var failures = new List<string>();
        int fonts = 0, refused = 0;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            ResourceFork fork;
            try
            {
                fork = ResourceFork.Read(File.ReadAllBytes(path));
            }
            catch (InvalidDataException)
            {
                continue;
            }

            foreach (var sfnt in fork.OfType(FourCC.FromString("sfnt")))
            {
                var data = sfnt.GetData().ToArray();
                if (!OutlineFont.Read(data).IsTrueType)
                {
                    continue;
                }

                fonts++;
                refused += Gdi.Load(data) == 0 ? 1 : 0;
                if (Gdi.Load(LoadableFont.Make(data)) == 0)
                {
                    failures.Add($"{Path.GetFileName(path)} 'sfnt' {sfnt.Id}");
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage($"{fonts} TrueType fonts, {refused} refused as they are.");
        Assert.True(fonts > 0);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static class Gdi
    {
        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint AddFontMemResourceEx(byte[] font, uint size, nint reserved, ref uint fonts);

        [DllImport("gdi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveFontMemResourceEx(nint handle);

        public static int Load(byte[] font)
        {
            uint count = 0;
            var handle = AddFontMemResourceEx(font, (uint)font.Length, 0, ref count);
            if (handle == 0)
            {
                return 0;
            }

            RemoveFontMemResourceEx(handle);
            return (int)count;
        }
    }
}
