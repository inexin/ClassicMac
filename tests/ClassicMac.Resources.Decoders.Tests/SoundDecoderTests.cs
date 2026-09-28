using System.Buffers.Binary;
using System.Text.Json;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Sound;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Tests;

public class SoundDecoderTests
{
    private const uint Rate22k = 0x56EE8BA3; // 22254.545 Hz

    private static readonly IResourceDecoder Decoder = ResourceDecoders.Create().Single(d => d.CanDecode(FourCC.FromString("snd ")));

    private static (IReadOnlyList<DecodedFile> Files, List<Diagnostic> Diagnostics) Decode(byte[] data)
    {
        var resource = new Resource(FourCC.FromString("snd "), 128, data);
        var fork = new ResourceFork();
        fork.Add(resource);
        var diagnostics = new List<Diagnostic>();
        return (Decoder.Decode(new DecodeInput(resource, data, fork, diagnostics: diagnostics)), diagnostics);
    }

    private static byte[] BE(params object[] fields)
    {
        var bytes = new List<byte>();
        foreach (var f in fields)
        {
            switch (f)
            {
                case byte b: bytes.Add(b); break;
                case ushort s: bytes.AddRange([(byte)(s >> 8), (byte)s]); break;
                case short s: bytes.AddRange([(byte)(s >> 8), (byte)s]); break;
                case uint u: bytes.AddRange([(byte)(u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)u]); break;
                case string t: bytes.AddRange(FourCC.FromString(t).ToString().Select(c => (byte)c)); break;
                case byte[] a: bytes.AddRange(a); break;
                default: throw new ArgumentException(f.GetType().Name);
            }
        }
        return [.. bytes];
    }

    // Format 1, one sampled-sound synth, one bufferCmd pointing at offset 20.
    private static byte[] Format1(byte[] header) => [.. BE((ushort)1, (ushort)1, (ushort)5, 0x80u, (ushort)1, (ushort)0x8051, (short)0, 20u), .. header];

    private static byte[] Standard(byte[] samples, uint loopStart = 0, uint loopEnd = 0, byte note = 60) =>
        BE(0u, (uint)samples.Length, Rate22k, loopStart, loopEnd, (byte)0x00, note, samples);

    // An extended (0xFF) or compressed (0xFE) header: 22 common bytes, then the long fields.
    private static byte[] Long(byte encode, uint channels, uint frames, ushort sampleSize, string format, short compressionId, byte[] samples)
    {
        var header = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), channels);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), Rate22k);
        header[20] = encode;
        header[21] = 60;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(22), frames);
        if (encode == 0xFF)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(48), sampleSize);
        }
        else
        {
            FourCC.FromString(format).CopyTo(header.AsSpan(40));
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(56), compressionId);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(62), sampleSize);
        }
        return [.. header, .. samples];
    }

    private static (int Format, int Channels, int Rate, int Bits, byte[] Data, byte[]? Smpl) Wav(DecodedFile file)
    {
        Assert.Equal(".wav", file.Extension);
        var w = file.Content.ToArray();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(w, 0, 4));
        Assert.Equal(w.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(4)));
        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[] data = [];
        byte[]? smpl = null;
        for (var at = 12; at + 8 <= w.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(w, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(w.AsSpan(at + 4));
            var body = w.AsSpan(at + 8, size);
            switch (id)
            {
                case "fmt ":
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    if (format == 0xFFFE) format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                    rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                    break;
                case "data": data = body.ToArray(); break;
                case "smpl": smpl = body.ToArray(); break;
            }
            at += 8 + size + (size & 1);
        }
        return (format, channels, rate, bits, data, smpl);
    }

    private static JsonElement Json(DecodedFile file)
    {
        Assert.Equal(".json", file.Extension);
        return JsonDocument.Parse(file.Content).RootElement;
    }

    [Fact]
    public void A_standard_sound_becomes_an_8_bit_WAV_with_its_details_in_JSON()
    {
        var (files, diagnostics) = Decode(Format1(Standard([0x80, 0xFF, 0x00, 0x7F])));

        Assert.Empty(diagnostics);
        var wav = Wav(files[0]);
        Assert.Equal((1, 1, 22255, 8), (wav.Format, wav.Channels, wav.Rate, wav.Bits));
        Assert.Equal([0x80, 0xFF, 0x00, 0x7F], wav.Data); // offset binary, as WAV's 8-bit
        Assert.Null(wav.Smpl);

        var json = Json(files[1]);
        Assert.Equal(1, json.GetProperty("format").GetInt32());
        Assert.Equal(5, json.GetProperty("synthesizers")[0].GetProperty("id").GetInt32());
        var command = json.GetProperty("commands")[0];
        Assert.Equal(("bufferCmd", true, 20), (command.GetProperty("name").GetString(), command.GetProperty("dataOffset").GetBoolean(), command.GetProperty("param2").GetInt32()));
        var sound = json.GetProperty("sound");
        Assert.Equal("standard", sound.GetProperty("header").GetString());
        Assert.Equal(22254.545456, sound.GetProperty("sampleRate").GetDouble(), 5);
        Assert.Equal("$56EE8BA3", sound.GetProperty("sampleRateFixed").GetString());
        Assert.Equal(4, sound.GetProperty("frames").GetInt32());
    }

    [Fact]
    public void Loops_and_base_notes_go_in_the_smpl_chunk()
    {
        var (files, _) = Decode(Format1(Standard(new byte[100], loopStart: 10, loopEnd: 90, note: 72)));

        var smpl = Wav(files[0]).Smpl!;
        Assert.Equal(72, BinaryPrimitives.ReadInt32LittleEndian(smpl.AsSpan(12))); // unity note
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(smpl.AsSpan(28))); // one loop
        Assert.Equal((10, 89), (BinaryPrimitives.ReadInt32LittleEndian(smpl.AsSpan(44)), BinaryPrimitives.ReadInt32LittleEndian(smpl.AsSpan(48))));

        Assert.Null(Wav(Decode(Format1(Standard(new byte[100], note: 0))).Files[0]).Smpl); // 0 is middle C
    }

    [Fact]
    public void Format_2_sounds_decode()
    {
        byte[] data = [.. BE((ushort)2, (ushort)0, (ushort)1, (ushort)0x8051, (short)0, 14u), .. Standard([1, 2, 3])];

        var (files, diagnostics) = Decode(data);

        Assert.Empty(diagnostics);
        Assert.Equal([1, 2, 3], Wav(files[0]).Data);
        Assert.Equal(0, Json(files[1]).GetProperty("referenceCount").GetInt32());
    }

    // SndPlay reads a format 2 sound's header after the commands, whatever the offset says (Realmz's say 20).
    [Fact]
    public void Format_2_headers_follow_the_commands_whatever_the_offset()
    {
        byte[] data = [.. BE((ushort)2, (ushort)0, (ushort)1, (ushort)0x8051, (short)0, 20u), .. Standard([1, 2, 3, 4, 5, 6, 7, 8])];

        var (files, diagnostics) = Decode(data);

        Assert.Equal("sound.header-offset", Assert.Single(diagnostics).Code);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], Wav(files[0]).Data);
    }

    [Fact]
    public void Extended_16_bit_stereo_becomes_little_endian()
    {
        byte[] samples = [0x12, 0x34, 0xAB, 0xCD, 0x00, 0x01, 0xFF, 0xFE];

        var (files, diagnostics) = Decode(Format1(Long(0xFF, 2, 2, 16, "", 0, samples)));

        Assert.Empty(diagnostics);
        var wav = Wav(files[0]);
        Assert.Equal((1, 2, 16), (wav.Format, wav.Channels, wav.Bits));
        Assert.Equal([0x34, 0x12, 0xCD, 0xAB, 0x01, 0x00, 0xFE, 0xFF], wav.Data);
        Assert.Equal("extended", Json(files[1]).GetProperty("sound").GetProperty("header").GetString());
    }

    [Theory]
    [InlineData("twos", 8, new byte[] { 0x00, 0x7F, 0x80 }, new byte[] { 0x80, 0xFF, 0x00 }, 1)]
    [InlineData("sowt", 16, new byte[] { 0x34, 0x12 }, new byte[] { 0x34, 0x12 }, 1)]
    [InlineData("in24", 24, new byte[] { 0x01, 0x02, 0x03 }, new byte[] { 0x03, 0x02, 0x01 }, 1)]
    [InlineData("fl32", 32, new byte[] { 0x3F, 0x80, 0x00, 0x00 }, new byte[] { 0x00, 0x00, 0x80, 0x3F }, 3)]
    [InlineData("raw ", 16, new byte[] { 0x80, 0x00 }, new byte[] { 0x00, 0x00 }, 1)]
    public void Compressed_headers_with_PCM_formats_decode(string format, int size, byte[] samples, byte[] expected, int wavFormat)
    {
        var (files, diagnostics) = Decode(Format1(Long(0xFE, 1, (uint)(samples.Length / (size / 8)), (ushort)size, format, -1, samples)));

        Assert.Empty(diagnostics);
        var wav = Wav(files[0]);
        Assert.Equal((wavFormat, size), (wav.Format, wav.Bits));
        Assert.Equal(expected, wav.Data);
    }

    [Fact]
    public void Formats_not_read_are_exported_raw()
    {
        var (files, diagnostics) = Decode(Format1(Long(0xFE, 1, 10, 16, "QDM2", -2, new byte[20])));

        Assert.Empty(files);
        Assert.Contains(diagnostics, d => d.Code == "sound.codec" && d.Message.Contains("QDM2", StringComparison.Ordinal));
    }

    // compressionID 0 is PCM whatever format says; the Sound Manager refuses IDs other than 0, 3, 4, -1 and -2.
    [Fact]
    public void Compression_IDs_are_read_as_the_Sound_Manager_reads_them()
    {
        var (files, _) = Decode(Format1(Long(0xFE, 1, 2, 16, "MAC3", 0, [0x12, 0x34, 0x56, 0x78])));
        Assert.Equal([0x34, 0x12, 0x78, 0x56], Wav(files[0]).Data);

        Assert.Contains(Decode(Format1(Long(0xFE, 1, 2, 16, "twos", 5, new byte[4]))).Diagnostics, d => d.Code == "sound.bad-header");
    }

    // IMA4, worked by hand from the IMA tables: preamble 0, then nibbles 4 and 4 (step 7 → +7, index 2; step 9 →
    // +10, index 4), then zeros; a second packet in the same batch keeps the running state.
    [Fact]
    public void IMA4_packets_decode()
    {
        var packet = new byte[34];
        packet[2] = 0x44;
        byte[] second = [0x7F, 0x80, .. new byte[32]]; // a preamble the decoder ignores mid-batch

        var (files, diagnostics) = Decode(Format1(Long(0xFE, 1, 2, 16, "ima4", -1, [.. packet, .. second])));

        Assert.Empty(diagnostics);
        var samples = Wav(files[0]).Data;
        Assert.Equal(128 * 2, samples.Length);
        Assert.Equal((7, 17), (BinaryPrimitives.ReadInt16LittleEndian(samples), BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(2))));
        Assert.True(BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(128)) < 1000); // not reset to $7F80
    }

    // The harness's Sound Manager 3.5.1 samples (run22): MACE 3 and 6, IMA4 and µ-law, mono and stereo, against the
    // Sound Manager's own decoding (.p8 for MACE, which it gives 8-bit; .p16 big-endian for the rest).
    [Fact]
    public void Sound_Manager_samples_decode_as_the_Sound_Manager_does()
    {
        var corpus = Environment.GetEnvironmentVariable("CLASSICMAC_CORPUS");
        var folder = string.IsNullOrEmpty(corpus) || !Directory.Exists(corpus) ? null
            : Directory.EnumerateFiles(corpus, "mac3m8.p8", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
        if (folder is null) Assert.Skip("Set CLASSICMAC_CORPUS to a folder holding the harness's run22/out samples to run this.");

        foreach (var name in new[] { "mac3m8", "mac3s8", "mac6m8", "mac6s8", "mac3m16", "mac6m16", "ima4m", "ima4s", "ulawm" })
        {
            var fork = ResourceFork.Read(File.ReadAllBytes(Path.Combine(folder!, ".rsrc", name + ".snd")));
            var resource = fork.Find(FourCC.FromString("snd "), 128)!;
            var diagnostics = new List<Diagnostic>();
            var files = Decoder.Decode(new DecodeInput(resource, resource.GetData(), fork, diagnostics: diagnostics));
            Assert.Empty(diagnostics);
            var wav = Wav(files[0]);
            byte[] expected;
            if (wav.Bits == 8)
            {
                expected = File.ReadAllBytes(Path.Combine(folder!, name + ".p8"));
            }
            else
            {
                expected = File.ReadAllBytes(Path.Combine(folder!, name + ".p16"));
                for (var i = 0; i + 1 < expected.Length; i += 2) (expected[i], expected[i + 1]) = (expected[i + 1], expected[i]);
            }
            Assert.True(expected.AsSpan().SequenceEqual(wav.Data), name);
        }
    }

    [Fact]
    public void Sounds_without_samples_give_JSON_only()
    {
        byte[] data = BE((ushort)1, (ushort)0, (ushort)2, (ushort)40, (short)60, 1000u, (ushort)46, (short)0, 0x01000100u);

        var (files, diagnostics) = Decode(data);

        Assert.Empty(diagnostics);
        var json = Json(Assert.Single(files));
        Assert.Equal(["freqDurationCmd", "volumeCmd"], json.GetProperty("commands").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.False(json.TryGetProperty("sound", out _));
    }

    [Fact]
    public void Damage_is_reported_not_thrown()
    {
        Assert.Contains(Decode(BE((ushort)3, (ushort)0, (ushort)0)).Diagnostics, d => d.Code == "sound.unknown-format");
        Assert.Contains(Decode(Format1(Standard([1, 2, 3, 4]))[..^2]).Diagnostics, d => d.Code == "sound.short");
        Assert.Contains(Decode(BE((ushort)1, (ushort)0, (ushort)1, (ushort)0x8051, (short)0, 500u)).Diagnostics, d => d.Code == "sound.bad-offset");
        Assert.Contains(Decode(BE((ushort)1, (ushort)5)).Diagnostics, d => d.Code == "sound.short");
        var bad = Format1(Standard([1]));
        bad[40] = 0x42; // encode
        Assert.Contains(Decode(bad).Diagnostics, d => d.Code == "sound.bad-header");
        Assert.Null(SoundResource.Read(new byte[2], []));
    }
}
