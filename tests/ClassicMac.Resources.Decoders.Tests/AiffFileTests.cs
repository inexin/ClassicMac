using ClassicMac.Core;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Sound;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

// AIFF and AIFF-C files (docs/formats/documents/aiff.md): the chunks Apple's specification defines, the samples through
// the snd codecs, the loop and base note from MARK and INST, and the WAV and JSON a conversion writes.
public sealed class AiffFileTests
{
    // An AIFF or AIFF-C file built chunk by chunk, each padded to an even length.
    private sealed class AiffBuilder(string form = "AIFF")
    {
        private readonly List<(string Id, byte[] Data)> chunks = [];

        public AiffBuilder Chunk(string id, byte[] data)
        {
            chunks.Add((id, data));
            return this;
        }

        public AiffBuilder Comm(short channels, uint frames, short sampleSize, double rate, string? compression = null, string name = "")
        {
            var w = new BigEndianWriter();
            w.WriteInt16(channels);
            w.WriteUInt32(frames);
            w.WriteInt16(sampleSize);
            w.WriteBytes(Extended(rate));
            if (compression is not null)
            {
                w.WriteFourCC(FourCC.FromString(compression));
                w.WriteByte((byte)name.Length);
                w.WriteBytes(System.Text.Encoding.ASCII.GetBytes(name));
                if (name.Length % 2 == 0)
                {
                    w.WriteByte(0);                                                  // the pstring padded to even
                }
            }

            return Chunk("COMM", w.ToArray());
        }

        public AiffBuilder Ssnd(byte[] samples, uint offset = 0)
        {
            var w = new BigEndianWriter();
            w.WriteUInt32(offset);
            w.WriteUInt32(0);
            w.WriteZeros((int)offset);
            w.WriteBytes(samples);
            return Chunk("SSND", w.ToArray());
        }

        public byte[] Build()
        {
            var w = new BigEndianWriter();
            w.WriteFourCC(FourCC.FromString("FORM"));
            w.WriteUInt32(0);
            w.WriteFourCC(FourCC.FromString(form));
            foreach (var (id, data) in chunks)
            {
                w.WriteFourCC(FourCC.FromString(id));
                w.WriteUInt32(data.Length);
                w.WriteBytes(data);
                if (data.Length % 2 == 1)
                {
                    w.WriteByte(0);
                }
            }

            w.WriteUInt32At(4, w.WrittenSpan.Length - 8);
            return w.ToArray();
        }
    }

    // An IEEE 754 80-bit extended number, big-endian (exact for the rates used here).
    private static byte[] Extended(double value)
    {
        int exponent = (int)Math.Floor(Math.Log2(value));
        ulong mantissa = (ulong)(value / Math.Pow(2, exponent - 63));
        var w = new BigEndianWriter();
        w.WriteUInt16(16383 + exponent);
        w.WriteUInt64(mantissa);
        return w.ToArray();
    }

    private static byte[] Shorts(params short[] samples)
    {
        var w = new BigEndianWriter();
        foreach (var s in samples)
        {
            w.WriteInt16(s);
        }

        return w.ToArray();
    }

    private static AiffFile Read(byte[] file, List<Diagnostic>? diagnostics = null) =>
        AiffFile.Read(file, diagnostics ?? [], "test.aiff") ?? throw new InvalidOperationException("not read");

    [Fact]
    public void An_AIFF_file_gives_its_frames_channels_size_and_rate()
    {
        var file = new AiffBuilder().Comm(2, 3, 16, 22050).Ssnd(Shorts(1, -1, 2, -2, 3, -3)).Build();

        var aiff = Read(file);

        Assert.False(aiff.IsCompressed);
        Assert.Equal((2, 16, 3, 22050.0), (aiff.Sound.Channels, aiff.Sound.SampleSize, aiff.Sound.Frames, aiff.SampleRate));
        Assert.Equal("twos", aiff.Sound.Format.ToString());
        Assert.Equal(Shorts(1, -1, 2, -2, 3, -3), aiff.Sound.Data.ToArray());
    }

    [Theory]
    [InlineData(44100.0)]
    [InlineData(22254.545454545454)]                                                 // the Mac's own rate, $56EE8BA3 in Fixed
    [InlineData(96000.0)]
    public void The_sample_rate_is_read_from_its_80_bit_extended_form(double rate)
    {
        var aiff = Read(new AiffBuilder().Comm(1, 0, 8, rate).Build());

        Assert.Equal(rate, aiff.SampleRate, 6);
    }

    [Fact]
    public void Samples_start_at_the_SSND_offset_and_stop_at_the_frame_count()
    {
        var diagnostics = new List<Diagnostic>();
        var file = new AiffBuilder().Comm(1, 2, 16, 8000).Ssnd(Shorts(7, 8, 9), offset: 4).Build();

        var aiff = Read(file, diagnostics);

        Assert.Equal(Shorts(7, 8), aiff.Sound.Data.ToArray());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Unknown_and_odd_sized_chunks_are_skipped_with_their_pad_byte()
    {
        var file = new AiffBuilder()
            .Chunk("NAME", "Beep!"u8.ToArray())                                          // five bytes and a pad
            .Chunk("APPL", [1, 2, 3, 4])
            .Comm(1, 1, 8, 11025).Ssnd([0x40]).Chunk("ANNO", "made by hand"u8.ToArray()).Build();

        var aiff = Read(file);

        Assert.Equal(("Beep!", "made by hand"), (aiff.Name, aiff.Annotation));
        Assert.Equal([0x40], aiff.Sound.Data.ToArray());
    }

    [Theory]
    [InlineData("NONE", 16, "twos")]
    [InlineData("NONE", 24, "in24")]
    [InlineData("NONE", 32, "in32")]
    [InlineData("sowt", 16, "sowt")]
    [InlineData("fl32", 32, "fl32")]
    [InlineData("ulaw", 16, "ulaw")]
    [InlineData("MAC3", 8, "MAC3")]
    [InlineData("ima4", 16, "ima4")]
    public void AIFF_C_compression_types_become_the_snd_sample_formats(string compression, short size, string format)
    {
        var aiff = Read(new AiffBuilder("AIFC").Chunk("FVER", [0xA2, 0x80, 0x51, 0x40])
            .Comm(1, 0, size, 22050, compression, "not compressed").Build());

        Assert.True(aiff.IsCompressed);
        Assert.Equal(format, aiff.Sound.Format.ToString());
    }

    [Fact]
    public void Compressed_frames_count_packets()
    {
        // IMA 4:1: 34 bytes a packet a channel, 64 samples each; the frame count is the packet count.
        var packets = new byte[34 * 3];
        var aiff = Read(new AiffBuilder("AIFC").Comm(1, 2, 16, 22050, "ima4", "IMA 4:1").Ssnd(packets).Build());

        Assert.Equal(34 * 2, aiff.Sound.Data.Length);
    }

    [Fact]
    public void Eight_bit_samples_are_signed()
    {
        var aiff = Read(new AiffBuilder().Comm(1, 2, 8, 11025).Ssnd([0x7F, 0x80]).Build());

        var wav = AiffFile.ToWav(aiff)!;

        Assert.Equal([0xFF, 0x00], wav.AsSpan(wav.Length - 2).ToArray());           // WAV's 8-bit samples are unsigned
    }

    [Fact]
    public void The_sustain_loop_and_base_note_come_from_MARK_and_INST()
    {
        var mark = new BigEndianWriter();
        mark.WriteUInt16(2);
        foreach (var (id, position, name) in new[] { (1, 10u, "start"), (2, 90u, "end") })
        {
            mark.WriteInt16(id);
            mark.WriteUInt32(position);
            mark.WriteByte((byte)name.Length);
            mark.WriteBytes(System.Text.Encoding.ASCII.GetBytes(name));
            if (name.Length % 2 == 0)
            {
                mark.WriteByte(0);
            }
        }

        var inst = new BigEndianWriter();
        inst.WriteBytes([72, 0, 0, 127, 1, 127]);                                         // baseNote 72, detune, notes, velocities
        inst.WriteInt16(0);                                                               // gain
        inst.WriteInt16(1);                                                               // sustain loop: forward
        inst.WriteInt16(1);
        inst.WriteInt16(2);
        inst.WriteInt16(0);                                                               // release loop: none
        inst.WriteInt16(0);
        inst.WriteInt16(0);
        var file = new AiffBuilder().Comm(1, 100, 8, 11025).Chunk("MARK", mark.ToArray()).Chunk("INST", inst.ToArray())
            .Ssnd(new byte[100]).Build();

        var aiff = Read(file);

        Assert.Equal([(1, 10u, "start"), (2, 90u, "end")], aiff.Markers.Select(m => (m.Id, m.Position, m.Name)));
        Assert.Equal((10u, 90u, (byte)72), (aiff.Sound.LoopStart, aiff.Sound.LoopEnd, aiff.Sound.BaseNote));
    }

    [Fact]
    public void Damaged_files_are_reported()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(AiffFile.Read("RIFF0000WAVE"u8.ToArray(), diagnostics, "x"));
        Assert.Equal("aiff.header", Assert.Single(diagnostics).Code);

        diagnostics.Clear();
        Assert.Null(AiffFile.Read(new AiffBuilder().Ssnd([1, 2]).Build(), diagnostics, "x"));
        Assert.Equal("aiff.no-comm", Assert.Single(diagnostics).Code);

        diagnostics.Clear();
        var shorter = Read(new AiffBuilder().Comm(1, 10, 16, 8000).Ssnd(Shorts(1, 2, 3)).Build(), diagnostics);
        Assert.Equal(Shorts(1, 2, 3), shorter.Sound.Data.ToArray());
        Assert.Equal("aiff.short", Assert.Single(diagnostics).Code);

        diagnostics.Clear();
        var silent = Read(new AiffBuilder().Comm(1, 0, 16, 8000).Build(), diagnostics);
        Assert.Equal(0, silent.Sound.Data.Length);
        Assert.Empty(diagnostics);                                                         // no SSND is right for no frames
    }

    [Fact]
    public void A_conversion_writes_the_WAV_and_the_JSON()
    {
        var file = new AiffBuilder().Comm(1, 2, 16, 22050).Ssnd(Shorts(256, -256)).Build();
        var converter = ResourceDecoders.CreateDocumentConverters().Single(c => c.Name == "sound.aiff");
        var input = new DocumentInput(new ResourceFork(), () => file, FourCC.FromString("AIFF"), FourCC.FromString("SCPL"), "Beep",
            ReadOptions.Default, []);

        var files = converter.Convert(input);

        Assert.Equal(["sound.wav", "sound.json"], files.Select(f => f.Path));
        Assert.Equal("RIFF"u8.ToArray(), files[0].Content[..4].ToArray());
        var json = System.Text.Encoding.UTF8.GetString(files[1].Content.Span);
        Assert.Contains("\"form\": \"AIFF\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sampleRate\": 22050", json, StringComparison.Ordinal);

        var other = input with { Type = FourCC.FromString("TEXT"), ReadDataFork = () => "hello"u8.ToArray() };
        Assert.Empty(converter.Convert(other));
        Assert.True(DataForkDocuments.Applies(FourCC.FromString("AIFC")));
    }
}
