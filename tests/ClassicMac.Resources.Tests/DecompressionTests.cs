using System.Buffers.Binary;
using ClassicMac.Resources.Compression;

namespace ClassicMac.Resources.Tests;

public class DecompressionTests
{
    private static readonly FourCC Test = FourCC.FromString("TEST");
    private static readonly ReadOptions Rom = ReadOptions.Default with { ResourceManager = ResourceManagerModel.Rom68k };

    // A compressed resource: an 18-byte header (version 8 or 9 layout) and the payload.
    private static Resource Compressed(
        string payload, uint size, short id, byte version = 8, byte fraction = 0xFF, ushort expansion = 0,
        byte param1 = 0, byte param2 = 0, byte headerAttributes = 1, ushort reserved = 0,
        ResourceAttributes attributes = ResourceAttributes.Compressed)
    {
        var body = Hex(payload);
        var data = new byte[CompressedResourceHeader.Length + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, CompressedResourceHeader.Signature);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), CompressedResourceHeader.Length);
        data[6] = version;
        data[7] = headerAttributes;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), size);
        if (version == 8)
        {
            data[12] = fraction;
            data[13] = (byte)expansion;
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(14), id);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), reserved);
        }
        else
        {
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(12), id);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), expansion);
            data[16] = param1;
            data[17] = param2;
        }
        body.CopyTo(data, CompressedResourceHeader.Length);
        return new Resource(Test, 128, data) { Attributes = attributes };
    }

    private static (string Hex, List<Diagnostic> Diagnostics) Decompress(
        Resource resource, ReadOptions? options = null, ResourceFork? fork = null,
        ResourceDecompression? decompression = null)
    {
        var diagnostics = new List<Diagnostic>();
        var data = (decompression ?? ResourceDecompression.Default).GetData(resource, fork, options, diagnostics);
        return (Convert.ToHexString(data.Span), diagnostics);
    }

    private static string Clean(Resource resource, ReadOptions? options = null)
    {
        var (hex, diagnostics) = Decompress(resource, options);
        Assert.Empty(diagnostics);
        return hex;
    }

    // --- Resource Manager behaviour ---

    [Fact]
    public void Uncompressed_resources_come_back_as_stored()
    {
        var resource = new Resource(Test, 1, Hex("A89F6572 0012"));
        Assert.Equal("A89F65720012", Clean(resource));
    }

    [Fact]
    public void The_compressed_bit_without_a_header_means_stored_data()
    {
        var (hex, diagnostics) = Decompress(new Resource(Test, 1, Hex("0102")) { Attributes = ResourceAttributes.Compressed });
        Assert.Equal("0102", hex);
        Assert.Equal("resource.not-compressed", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_truncated_header_is_an_error()
    {
        var (hex, diagnostics) = Decompress(new Resource(Test, 1, Hex("A89F6572 0012 08")) { Attributes = ResourceAttributes.Compressed });
        Assert.Equal("A89F6572001208", hex);
        Assert.Equal("resource.dcmp-header", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Extended_but_uncompressed_follows_the_chosen_model()
    {
        var resource = Compressed("AABBCCDD", size: 4, id: 0, headerAttributes: 0);
        var stored = Convert.ToHexString(resource.GetData().Span);

        Assert.Equal(stored[24..], Clean(resource, Rom)); // ROM: header stripped, bytes 12..22
        var (hex, diagnostics) = Decompress(resource);
        Assert.Equal(stored[..20], hex); // OS 9: bytes 0..10, header kept, last 12 lost
        Assert.Equal("resource.extended-uncompressed", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_unknown_version_is_version_9_on_OS_9_and_an_error_in_ROM()
    {
        var resource = Compressed("02", size: 2, id: 2, version: 7);

        var (hex, diagnostics) = Decompress(resource);
        Assert.Equal("4EBA", hex);
        Assert.Equal("resource.dcmp-version", Assert.Single(diagnostics).Code);

        var (romHex, romDiagnostics) = Decompress(resource, Rom);
        Assert.Equal(Convert.ToHexString(resource.GetData().Span), romHex);
        Assert.Contains(romDiagnostics, d => d.Code == "resource.dcmp-version" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void A_nonzero_reserved_word_in_a_version_8_header_is_an_error()
    {
        var (_, diagnostics) = Decompress(Compressed("01AABBFF", size: 2, id: 0, reserved: 1));
        Assert.Equal("resource.dcmp-header", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Sizes_over_the_limit_are_refused()
    {
        var (_, diagnostics) = Decompress(Compressed("FF", size: 1000, id: 0), ReadOptions.Default with { MaxResourceSize = 999 });
        Assert.Equal("resource.too-large", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Unknown_decompressors_keep_the_resource_compressed()
    {
        var resource = Compressed("FF", size: 2, id: 99);
        var (hex, diagnostics) = Decompress(resource);
        Assert.Equal(Convert.ToHexString(resource.GetData().Span), hex);
        Assert.Equal("resource.dcmp-unknown", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_fork_with_its_own_dcmp_is_noted()
    {
        var fork = new ResourceFork();
        fork.Add(new Resource(FourCC.FromString("dcmp"), 2, new byte[] { 0x4E, 0x75 }));
        var (hex, diagnostics) = Decompress(Compressed("02", size: 2, id: 2, version: 9), fork: fork);
        Assert.Equal("4EBA", hex);
        Assert.Equal("resource.dcmp-overridden", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Entry_form_follows_the_header_version()
    {
        var (_, diagnostics) = Decompress(Compressed("FF", size: 2, id: 0, version: 9));
        Assert.Equal("resource.dcmp-form", Assert.Single(diagnostics).Code);
        (_, diagnostics) = Decompress(Compressed("02", size: 2, id: 2, version: 8));
        Assert.Equal("resource.dcmp-form", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Short_output_is_padded_with_the_block_as_on_the_Mac()
    {
        // Declares 4 bytes, writes 2 (AA BB), then stops; the rest of the block still holds the compressed input.
        var (hex, diagnostics) = Decompress(Compressed("01AABBFF", size: 4, id: 0));
        Assert.Equal("AABBBBFF", hex); // block = 4 bytes, input "01 AA BB FF" at its tail, AA BB written over "01 AA"
        Assert.Equal("resource.dcmp-size", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Running_off_the_input_is_stopped()
    {
        var (_, diagnostics) = Decompress(Compressed("01AABB", size: 8, id: 0)); // no FF terminator
        Assert.Equal("resource.dcmp-overrun", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Applications_can_add_decompressors()
    {
        var decompression = new ResourceDecompression([new Reverse()]);
        var (hex, diagnostics) = Decompress(Compressed("010203", size: 3, id: 200, version: 9), decompression: decompression);
        Assert.Equal("030201", hex);
        Assert.Empty(diagnostics);
    }

    private sealed class Reverse : IResourceDecompressor
    {
        public short Id => 200;

        public int Decompress(DecompressionContext context)
        {
            var input = context.Block[context.SourceOffset..];
            Array.Reverse(input);
            input.CopyTo(context.Block, 0);
            return input.Length;
        }
    }

    // --- dcmp 0 and 1 ---

    [Theory]
    [InlineData("7F", 0x7F)]
    [InlineData("C000", 0)]
    [InlineData("FEFF", 0x3EFF)]
    [InlineData("8000", -0x4000)]
    [InlineData("BFFF", -1)]
    [InlineData("FF00010000", 0x10000)]
    public void Varints_decode_like_the_68k_code(string bytes, int expected)
    {
        var block = Hex(bytes);
        Assert.Equal(expected, Dcmp01.ReadVarint(new BlockCursor(block, 0)));
    }

    [Fact]
    public void Dcmp0_literals_and_constants()
    {
        // 01: 2 literal bytes; 00 varint 2: 4 bytes; 4B, 4C: constant words 0000, 4EBA; FF: end.
        Assert.Equal("AABB1122334400004EBA",
            Clean(Compressed("01AABB 0002 11223344 4B 4C FF", size: 10, id: 0, expansion: 16)));
    }

    [Fact]
    public void Dcmp0_remembers_and_recalls_strings()
    {
        // 11: 2 literal bytes, remembered as slot 0; 23: recall slot 0.
        Assert.Equal("ABCDABCD", Clean(Compressed("11ABCD 23 FF", size: 4, id: 0, expansion: 8)));
    }

    [Fact]
    public void Dcmp0_undefined_slots_read_the_working_buffer()
    {
        // Block 12 + 8 = 20, fraction $7F: n = 20·128 >> 8 = 10, so the OS 9 buffer is 12 bytes. Remembering "ABCD"
        // puts it at 10..12 (slot 0 start word = 000A). Slot 1 is undefined: its start word (offset 6) is zero and
        // its end is slot 0's start, so it copies buffer bytes 0..10: 0006 000C 000A 0000 0000.
        var (hex, diagnostics) = Decompress(Compressed("11ABCD 24 FF", size: 12, id: 0, fraction: 0x7F, expansion: 8));
        Assert.Equal("ABCD0006000C000A00000000", hex);
        Assert.Equal("resource.dcmp-undefined-slot", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData("FE024102", "414141")] // byte run
    [InlineData("FE03D23401", "12341234")] // word run
    [InlineData("FE04100201FF", "001000110010")] // words, signed-byte deltas
    [InlineData("FE05100105", "00100015")] // words, varint deltas
    [InlineData("FE06100105", "0000001000000015")] // longs, varint deltas
    [InlineData("FE0005020708", "3F3C0005A9F000073F3C0005A9F000093F3C0005A9F0")] // export table
    [InlineData("FE0120080110", "610000204EED0010610000184EED0018")] // jump table, a5 delta 8
    [InlineData("FE012000011030", "610000204EED0010610000184EED0030")] // jump table, explicit a5 offsets
    [InlineData("FE0701AABB", "AABB")] // unknown sub-op: only its byte is consumed
    public void Dcmp0_extensions(string stream, string expected)
    {
        Assert.Equal(expected, Clean(Compressed(stream + "FF", size: (uint)(expected.Length / 2), id: 0, expansion: 32)));
    }

    [Fact]
    public void Dcmp1_opcodes()
    {
        // 00: 1 literal; 10: 1 literal remembered; 20: slot 0; D5: 0000; D0 varint 3: 3 literals; FF.
        Assert.Equal("AABBBB0000112233",
            Clean(Compressed("00AA 10BB 20 D5 D003112233 FF", size: 8, id: 1, expansion: 16)));
    }

    // --- dcmp 2 ---

    [Fact]
    public void Dcmp2_plain_mode_uses_the_default_table()
    {
        Assert.Equal("00004EBA", Clean(Compressed("0002", size: 4, id: 2, version: 9)));
    }

    [Fact]
    public void Dcmp2_copies_an_odd_final_byte()
    {
        Assert.Equal("00004EBA7F", Clean(Compressed("00027F", size: 5, id: 2, version: 9)));
    }

    [Fact]
    public void Dcmp2_always_writes_one_word()
    {
        // Size 0 still writes a word (a do-while loop); the handle is then cut to 0.
        var (hex, diagnostics) = Decompress(Compressed("02", size: 0, id: 2, version: 9, expansion: 2));
        Assert.Equal("", hex);
        Assert.Equal("resource.dcmp-size", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Dcmp2_custom_table()
    {
        // param2 bit 0, param1 = 1: two words replace entries 0 and 1; entry 5 is past the custom table.
        var (hex, diagnostics) = Decompress(
            Compressed("11112222 0100 05", size: 6, id: 2, version: 9, expansion: 8, param1: 1, param2: 1));
        Assert.Equal("222211110000", hex);
        Assert.Equal("resource.dcmp2-stale-table", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Dcmp2_bitmap_mode()
    {
        // 10 words: a flag byte FF with 8 indexes, then a flag byte 40 for the 2 leftover words (literal, index).
        Assert.Equal(new string('0', 32) + "CCDD4EBA",
            Clean(Compressed("FF 0000000000000000 40 CCDD 02", size: 20, id: 2, version: 9, param2: 2)));
    }

    // --- dcmp 3 ---

    [Fact]
    public void Dcmp3_literals_and_overlapping_back_references()
    {
        // 00 (no copy) → literal run 101 (3 bytes) "ABC"; then 01 (length 1 + 3 after a short run = 4) with offset
        // code 10 + 01 (1 + 2 = 3): copies "ABCA" from three back.
        var stream = Bits("00 101 01000001 01000010 01000011 01 1001");
        Assert.Equal(Convert.ToHexString("ABCABCA"u8), Clean(Compressed(stream, size: 7, id: 3, version: 9)));
    }

    [Fact]
    public void Dcmp3_back_references_before_the_start_are_stopped()
    {
        // A back-reference as the first command.
        var (_, diagnostics) = Decompress(Compressed(Bits("01 0 00000000"), size: 4, id: 3, version: 9));
        Assert.Equal("resource.dcmp-overrun", Assert.Single(diagnostics).Code);
    }

    // --- corpus ---

    [Fact]
    public void Corpus_compressed_resources_decompress_to_their_declared_size()
    {
        // Corpora may hold deliberately malformed resources, or ones meant for a file's own 'dcmp': those must be
        // reported, never thrown; cleanly decompressed ones must have their declared size.
        int compressed = 0, clean = 0;
        foreach (var file in Corpus.ForkFiles())
        {
            var fork = ResourceFork.Read(File.ReadAllBytes(file));
            foreach (var resource in fork.Resources.Where(r => (r.Attributes & ResourceAttributes.Compressed) != 0))
            {
                if (!CompressedResourceHeader.TryRead(resource.GetData().Span, out var header) || !header.IsCompressed)
                    continue;
                compressed++;
                var diagnostics = new List<Diagnostic>();
                var data = ResourceDecompression.Default.GetData(resource, fork, null, diagnostics);
                if (diagnostics.Any(d => d.Severity != DiagnosticSeverity.Info)) continue;
                Assert.Equal((int)header.DecompressedSize, data.Length);
                clean++;
            }
        }
        TestContext.Current.SendDiagnosticMessage($"{compressed} compressed corpus resources; {clean} decompressed cleanly.");
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // Packs a string of bits, most significant first, into hex, padding the last byte with zeros.
    private static string Bits(string bits)
    {
        bits = bits.Replace(" ", "");
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i] == '1') bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return Convert.ToHexString(bytes);
    }
}
