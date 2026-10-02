using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// The pidata opcodes of Mac OS Runtime Architectures ch. 8, one instruction at a time.
public class PatternDataTests
{
    private static (byte[] Image, List<Diagnostic> Diagnostics) Unpack(byte[] packed, int maxLength = PatternData.DefaultMaxLength)
    {
        var diagnostics = new List<Diagnostic>();
        return (PatternData.Unpack(packed, diagnostics, maxLength), diagnostics);
    }

    private static void Clean(byte[] packed, byte[] expected)
    {
        var (image, diagnostics) = Unpack(packed);
        Assert.Empty(diagnostics);
        Assert.Equal(expected, image);
    }

    private static void Fails(byte[] packed, string code, byte[]? partial = null, int maxLength = PatternData.DefaultMaxLength)
    {
        var (image, diagnostics) = Unpack(packed, maxLength);
        var d = Assert.Single(diagnostics);
        Assert.Equal(code, d.Code);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        if (partial is not null) Assert.Equal(partial, image);
    }

    [Fact]
    public void Empty_input_unpacks_to_nothing() => Clean([], []);

    [Fact]
    public void Op0_zero_fills_count_bytes() => Clean([0x03], [0, 0, 0]);

    [Fact]
    public void Op1_copies_count_bytes() => Clean([0x23, 1, 2, 3], [1, 2, 3]);

    [Fact]
    public void Op2_repeats_a_block_argument_plus_one_times() =>
        Clean([0x42, 0x02, 0xAB, 0xCD], [0xAB, 0xCD, 0xAB, 0xCD, 0xAB, 0xCD]);

    [Fact]
    public void Op3_interleaves_the_common_block_with_custom_blocks() =>
        // common "C" (1 byte), customSize 2, repeat 2: C x1 x2 C y1 y2 C
        Clean([0x61, 0x02, 0x02, (byte)'C', 1, 2, 3, 4], [(byte)'C', 1, 2, (byte)'C', 3, 4, (byte)'C']);

    [Fact]
    public void Op4_interleaves_zeros_with_custom_blocks() =>
        // zeroSize 2, customSize 1, repeat 2: 0 0 a 0 0 b 0 0
        Clean([0x82, 0x01, 0x02, 0xAA, 0xBB], [0, 0, 0xAA, 0, 0, 0xBB, 0, 0]);

    [Fact]
    public void Op3_with_no_repeats_writes_the_common_block_once() => Clean([0x62, 0x05, 0x00, 7, 8], [7, 8]);

    [Fact]
    public void A_count_of_zero_reads_the_count_as_an_argument() =>
        // 200 = 0x81 0x48 in 7-bit groups.
        Clean([0x00, 0x81, 0x48], new byte[200]);

    [Fact]
    public void Arguments_are_big_endian_seven_bit_groups()
    {
        // Op2 block of 1 byte, repeat argument 0x82 0x00 = 256: 257 copies.
        var (image, diagnostics) = Unpack([0x41, 0x82, 0x00, 0x5A]);
        Assert.Empty(diagnostics);
        Assert.Equal(257, image.Length);
        Assert.All(image, b => Assert.Equal(0x5A, b));
    }

    [Fact]
    public void Op4_with_a_count_argument_puts_the_custom_block_between_zero_runs()
    {
        // Count 0, so the count is the argument $82 $00 = 256; customSize 1, repeat 1: 256 zeros, AA, 256 zeros.
        var (image, diagnostics) = Unpack([0x80, 0x82, 0x00, 0x01, 0x01, 0xAA]);
        Assert.Empty(diagnostics);
        Assert.Equal([.. new byte[256], 0xAA, .. new byte[256]], image);
    }

    [Fact]
    public void Op3_with_a_count_argument() =>
        // Count argument 2 (common 43 43), customSize 1, repeat 2.
        Clean([0x60, 0x02, 0x01, 0x02, 0x43, 0x43, 0x01, 0x02], [0x43, 0x43, 0x01, 0x43, 0x43, 0x02, 0x43, 0x43]);

    [Fact]
    public void A_three_byte_argument() =>
        // $81 $80 $00 = 1 << 14.
        Clean([0x00, 0x81, 0x80, 0x00], new byte[16384]);

    [Fact]
    public void Op4_with_no_repeats_writes_the_zeros_once_and_reads_no_custom_block() => Clean([0x83, 0x05, 0x00], [0, 0, 0]);

    [Fact]
    public void The_largest_count_in_the_opcode_byte_is_31() => Clean([0x1F], new byte[31]);

    [Fact]
    public void A_count_argument_of_0_writes_nothing() => Clean([0x00, 0x00], []);

    [Fact]
    public void Instructions_follow_each_other() => Clean([0x02, 0x22, 9, 8, 0x01], [0, 0, 9, 8, 0]);

    [Theory]
    [InlineData(0xA0)]
    [InlineData(0xC1)]
    [InlineData(0xFF)]
    public void Opcodes_5_to_7_are_errors_and_stop(byte opcode) =>
        Fails([0x21, 0x11, opcode, 0x22, 1, 2], "pef.pidata-bad-opcode", [0x11]);

    [Fact]
    public void A_count_argument_cut_short_is_truncated() => Fails([0x21, 7, 0x00, 0x81], "pef.pidata-truncated", [7]);

    // The Code Fragment Manager reads at most 5 argument bytes, the fifth whole, and keeps the low 32 bits [Code: the
    // Code Fragment Manager in the Mac OS ROM]; ClassicMac reports a wider argument as damage.
    [Fact]
    public void An_argument_longer_than_32_bits_is_damage() => Fails([0x00, 0x9F, 0xFF, 0xFF, 0xFF, 0x7F], "pef.pidata-truncated");

    [Fact]
    public void An_argument_of_exactly_32_bits_reads() =>
        Fails([0x00, 0x8F, 0xFF, 0xFF, 0xFF, 0x7F], "pef.pidata-too-long", [], maxLength: 16);

    [Fact]
    public void A_block_copy_past_the_end_is_truncated() => Fails([0x24, 1, 2], "pef.pidata-truncated", []);

    [Fact]
    public void A_repeat_without_its_argument_is_truncated() => Fails([0x42], "pef.pidata-truncated");

    [Fact]
    public void A_repeated_block_past_the_end_is_truncated() => Fails([0x42, 0x01, 0xAB], "pef.pidata-truncated");

    [Fact]
    public void An_interleave_without_its_arguments_is_truncated() => Fails([0x61, 0x02], "pef.pidata-truncated");

    [Fact]
    public void An_interleave_common_block_past_the_end_is_truncated() => Fails([0x62, 0x01, 0x01], "pef.pidata-truncated");

    [Fact]
    public void Interleave_custom_blocks_past_the_end_are_truncated() => Fails([0x81, 0x02, 0x02, 1, 2, 3], "pef.pidata-truncated");

    [Fact]
    public void A_zero_run_past_the_limit_is_too_long() => Fails([0x21, 1, 0x00, 0x88, 0x00], "pef.pidata-too-long", [1], maxLength: 100);

    [Fact]
    public void A_block_copy_past_the_limit_is_too_long() => Fails([0x23, 1, 2, 3], "pef.pidata-too-long", [], maxLength: 2);

    [Fact]
    public void A_repeat_past_the_limit_is_too_long() =>
        Fails([0x41, 0x8F, 0xFF, 0xFF, 0x7F, 0xAA], "pef.pidata-too-long", [], maxLength: 1000);

    [Fact]
    public void The_largest_repeat_is_too_long_without_allocating_it() =>
        Fails([0x5F, 0x8F, 0xFF, 0xFF, 0xFF, 0x7F, .. new byte[31]], "pef.pidata-too-long");

    [Fact]
    public void An_interleave_past_the_limit_is_too_long() =>
        Fails([0x9F, 0x00, 0x8F, 0xFF, 0xFF, 0x7F], "pef.pidata-too-long", [], maxLength: 1000);

    [Fact]
    public void An_empty_repeated_block_writes_nothing() => Clean([0x40, 0x00, 0x8F, 0xFF, 0xFF, 0x7F], []);

    [Fact]
    public void An_empty_interleave_writes_nothing() => Clean([0x80, 0x00, 0x00, 0x8F, 0xFF, 0xFF, 0x7F], []);

    [Fact]
    public void Diagnostics_are_required() => Assert.Throws<ArgumentNullException>(() => PatternData.Unpack([], null!));
}
