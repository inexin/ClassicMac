using System.Buffers.Binary;
using ClassicMac.Graphics;
using ClassicMac.Graphics.Pict;
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources.Decoders.Sound;

namespace ClassicMac.Resources.Decoders.Tests;

// Image and sound import: what the writers make reads back through the decoders.
public class ImportTests
{
    // A w × h image: a red disc on a transparent field with a black border ring and a white centre.
    private static RgbaBitmap Disc(int w, int h)
    {
        var image = new RgbaBitmap(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double dx = x + 0.5 - w / 2.0, dy = y + 0.5 - h / 2.0, r = Math.Sqrt(dx * dx + dy * dy) / (Math.Min(w, h) / 2.0);
                if (r > 1)
                {
                    continue;
                }

                var c = r > 0.8 ? new RgbaColor(0, 0, 0) : r < 0.3 ? new RgbaColor(255, 255, 255) : new RgbaColor(221, 8, 6);
                int i = (y * w + x) * 4;
                (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2], image.Pixels[i + 3]) = (c.R, c.G, c.B, 255);
            }
        }

        return image;
    }

    private static void AssertSame(RgbaBitmap expected, RgbaBitmap actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                var (e, a) = (expected[x, y], actual[x, y]);
                Assert.True(e.A < 128 ? a.A == 0 : a == e with { A = 255 }, $"({x}, {y}): {e} became {a}");
            }
        }
    }

    [Fact]
    public void Colour_icons_and_cursors_keep_exact_colours_and_the_mask()
    {
        var image = Disc(16, 16);
        AssertSame(image, QuickDrawResources.DecodeCicn(ImageImport.WriteCicn(image)));
        AssertSame(image, QuickDrawResources.DecodeColorCursor(ImageImport.WriteColorCursor(image, 3, 4)).Image);
        var cursor = QuickDrawResources.DecodeColorCursor(ImageImport.WriteColorCursor(image, 3, 4));
        Assert.Equal((3, 4), (cursor.HotspotH, cursor.HotspotV));
        Assert.DoesNotContain(true, cursor.Inverted);                                  // transparent pixels leave the screen

        var big = Disc(40, 24);                                                          // any size, odd widths padded
        AssertSame(big, QuickDrawResources.DecodeCicn(ImageImport.WriteCicn(big)));
    }

    [Fact]
    public void Standard_table_icons_use_the_nearest_colour()
    {
        var image = Disc(32, 32);
        var list = ImageImport.WriteIconList("ICN#", image);
        Assert.Equal(256, list.Length);
        var icl8 = QuickDrawResources.DecodeColorIcon("icl8", ImageImport.WriteColorIcon("icl8", image), list);
        var icl4 = QuickDrawResources.DecodeColorIcon("icl4", ImageImport.WriteColorIcon("icl4", image), list);
        Assert.Equal(new RgbaColor(0, 0, 0), icl8[1 + 16, 1]);                          // black ring (just inside the edge)
        Assert.Equal(new RgbaColor(221, 8, 6), icl4[16, 8]);                             // clut 4's red, exact
        Assert.Equal(0, icl4[0, 0].A);                                                   // masked by ICN#
        var mono = QuickDrawResources.DecodeIconList("ICN#", list);
        Assert.Equal(new RgbaColor(0, 0, 0), mono[16, 8]);                               // red is dark
        Assert.Equal(new RgbaColor(255, 255, 255), mono[16, 16]);                        // the white centre

        var family = ImageImport.WriteIconFamily(image);
        Assert.Equal(ImageImport.IconFamilyTypes, family.Select(f => f.Type));
        Assert.Equal(64, family.Single(f => f.Type == "ics#").Data.Length);            // scaled to 16 × 16
        Assert.Equal(192, ImageImport.Write("icm8", image).Length);                     // 16 × 12, aspect kept
    }

    [Fact]
    public void Cursors_and_plain_icons()
    {
        var image = Disc(16, 16);
        var curs = ImageImport.WriteCursor(image, 20, -1);
        Assert.Equal(68, curs.Length);
        var cursor = QuickDrawResources.DecodeCursor(curs);
        Assert.Equal((15, 0), (cursor.HotspotH, cursor.HotspotV));                     // clamped
        Assert.Equal(0, cursor.Image[0, 0].A);
        Assert.Equal(new RgbaColor(0, 0, 0), cursor.Image[8, 3]);
        Assert.Equal(128, ImageImport.Write("ICON", Disc(64, 64)).Length);
        Assert.Throws<ArgumentException>(() => ImageImport.Write("snd ", image));
    }

    [Fact]
    public void Pictures_are_flattened_on_white_at_the_smallest_depth()
    {
        var image = Disc(20, 10);
        var data = ImageImport.WritePicture(image);
        var bitmap = PictReader.Decode(data, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((20, 10), (bitmap.Width, bitmap.Height));
        Assert.Equal(new RgbaColor(255, 255, 255), bitmap[0, 0]);                        // transparent over white
        Assert.Equal(new RgbaColor(221, 8, 6), bitmap[10, 3]);
        Assert.True(FindOpcode(data) > 0);                                               // 3 colours: an indexed PixMap
    }

    // The offset of the first BitsRect or PackBitsRect ($0090, $0098) after the header.
    private static int FindOpcode(byte[] data)
    {
        for (int i = 40; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] is 0x90 or 0x98)
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void Wav_files_become_sampled_sounds()
    {
        // 16-bit stereo with a loop and a base note.
        var pcm = new byte[400 * 4];
        for (int i = 0; i < 800; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i * 40 - 16000));
        }

        var wav = WavWriter.Write(pcm, 2, 2, false, 22050, new SamplerInfo(72, 10, 99));
        var diagnostics = new List<ClassicMac.Core.Diagnostic>();
        var resource = SoundResource.Read(SoundImport.FromWav(wav), diagnostics)!;
        Assert.Empty(diagnostics);
        var sound = resource.Sound!;
        Assert.Equal((SoundHeaderKind.Extended, 2, 16, 400), (sound.Kind, sound.Channels, sound.SampleSize, sound.Frames));
        Assert.Equal((10u, 100u, (byte)72), (sound.LoopStart, sound.LoopEnd, sound.BaseNote));
        Assert.Equal(22050, sound.SampleRate);
        var decoded = SoundSamples.Decode(sound)!;
        Assert.Equal(-16000 / 32768f, decoded.Samples[0]);
        Assert.Equal((799 * 40 - 16000) / 32768f, decoded.Samples[799]);

        // 8-bit mono: a standard header, the bytes as they are.
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var mono = SoundResource.Read(SoundImport.FromWav(WavWriter.Write(bytes, 1, 1, false, 11025, null)), diagnostics)!.Sound!;
        Assert.Equal((SoundHeaderKind.Standard, 300, (byte)60), (mono.Kind, mono.Frames, mono.BaseNote));
        Assert.Equal(bytes, mono.Data.ToArray());

        // Float becomes 16-bit.
        var floats = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(floats, 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(floats.AsSpan(4), -2f);
        var deep = SoundResource.Read(SoundImport.FromWav(WavWriter.Write(floats, 1, 4, true, 44100, null)), diagnostics)!.Sound!;
        Assert.Equal(new byte[] { 0x40, 0x00, 0x80, 0x00 }, deep.Data.ToArray());
        Assert.Equal(44100, deep.SampleRate);

        Assert.Throws<InvalidDataException>(() => SoundImport.FromWav("RIFF\0\0\0\0AVI "u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => SoundImport.Write(bytes, 1, 8, 96000));
    }
}
