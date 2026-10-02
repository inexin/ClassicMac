using ClassicMac.Code.Ppc;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests;

// Traceback tables built by hand from the AIX layout the Mac OS PowerPC compilers emit after a function's final blr
// [Doc: Mac OS Runtime Architectures; AIX tbtable.h]: a zero word, 8 flag bytes, then the optional fields in order.
public class TracebackTableTests
{
    private const uint Blr = 0x4E800020;

    private static byte[] Words(params uint[] words)
    {
        var w = new BigEndianWriter();
        foreach (var word in words)
        {
            w.WriteUInt32(word);
        }

        return w.ToArray();
    }

    // The zero word and the 8 flag bytes: version, language, b2, b3, b4, b5, fixed parms, float parms << 1 | parmsonstk.
    private static byte[] Header(byte b2 = 0, byte b3 = 0, byte fixedParms = 0, byte floatParms = 0, byte language = 0,
        byte b4 = 0, byte b5 = 0, byte b7 = 0) =>
        [0, 0, 0, 0, 0, language, b2, b3, b4, b5, fixedParms, (byte)(floatParms << 1 | b7)];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static TracebackTable Read(byte[] code, int offset = 0)
    {
        var diagnostics = new List<Diagnostic>();
        var table = TracebackTable.Read(code, offset, diagnostics);
        Assert.Empty(diagnostics);
        return Assert.IsType<TracebackTable>(table);
    }

    [Fact]
    public void A_table_with_no_optional_fields()
    {
        var t = Read(Header(language: 9));
        Assert.Equal(0, t.Offset);
        Assert.Equal(12, t.Length);
        Assert.Equal(0, t.Version);
        Assert.Equal(9, t.Language);
        Assert.Equal(0x0009000000000000ul, t.Flags);
        Assert.False(t.HasTbOffset);
        Assert.False(t.HasControlledStorage);
        Assert.False(t.IsInterruptHandler);
        Assert.False(t.HasName);
        Assert.False(t.UsesAlloca);
        Assert.Equal(0, t.FixedParameterCount);
        Assert.Equal(0, t.FloatParameterCount);
        Assert.Null(t.ParameterInfo);
        Assert.Null(t.TbOffset);
        Assert.Null(t.HandlerMask);
        Assert.Empty(t.ControlledStorage);
        Assert.Null(t.Name);
        Assert.Null(t.AllocaRegister);
        Assert.Null(t.FunctionStart);
    }

    [Fact]
    public void Tb_offset_gives_the_function_start()
    {
        // The shape of the first table in Disk Copy 6.5: a 0x28-byte function at 0x1C, its table at 0x44.
        var code = Concat(new byte[0x44], Header(0x20, 0x41), Words(0x28), [0x00, 0x16], ".TradHighestUnitNumber"u8.ToArray());
        var t = Read(code, 0x44);
        Assert.True(t.HasTbOffset);
        Assert.True(t.HasName);
        Assert.Equal(0x28u, t.TbOffset);
        Assert.Equal(0x1C, t.FunctionStart);
        Assert.Equal(".TradHighestUnitNumber", t.Name);
        Assert.Equal(12 + 4 + 2 + 22, t.Length);
        Assert.Equal(0x0000204100000000ul, t.Flags);
    }

    [Fact]
    public void Fixed_parameters_bring_parminfo_before_tb_offset()
    {
        var t = Read(Concat(new byte[16], Header(0x20, fixedParms: 2), Words(0xC0000000, 0x10)), 16);
        Assert.Equal(2, t.FixedParameterCount);
        Assert.Equal(0xC0000000u, t.ParameterInfo);
        Assert.Equal(0x10u, t.TbOffset);
        Assert.Equal(0, t.FunctionStart);
        Assert.Equal(20, t.Length);
    }

    [Fact]
    public void Float_parameters_alone_bring_parminfo()
    {
        var t = Read(Concat(Header(floatParms: 3), Words(0xA8000000)));
        Assert.Equal(0, t.FixedParameterCount);
        Assert.Equal(3, t.FloatParameterCount);
        Assert.Equal(0xA8000000u, t.ParameterInfo);
        Assert.Equal(16, t.Length);
    }

    [Fact]
    public void An_interrupt_handler_has_a_handler_mask()
    {
        var t = Read(Concat(Header(b3: 0x80), Words(0x12345678)));
        Assert.True(t.IsInterruptHandler);
        Assert.Equal(0x12345678u, t.HandlerMask);
        Assert.Equal(16, t.Length);
    }

    [Fact]
    public void Controlled_storage_is_a_count_and_displacements()
    {
        var t = Read(Concat(Header(0x08), Words(2, 0x40, 0x48)));
        Assert.True(t.HasControlledStorage);
        Assert.Equal([0x40u, 0x48u], t.ControlledStorage);
        Assert.Equal(24, t.Length);
    }

    [Fact]
    public void The_name_is_a_length_word_and_Mac_Roman_characters()
    {
        var t = Read(Concat(Header(b3: 0x40), [0x00, 0x03, (byte)'a', 0xA5, (byte)'b']));
        Assert.Equal("a•b", t.Name);
        Assert.Equal(17, t.Length);
    }

    [Fact]
    public void Alloca_register_is_the_last_byte()
    {
        var t = Read(Concat(Header(b3: 0x20), [31]));
        Assert.True(t.UsesAlloca);
        Assert.Equal((byte)31, t.AllocaRegister);
        Assert.Equal(13, t.Length);
    }

    [Fact]
    public void Every_optional_field_in_order()
    {
        var code = Concat(new byte[0x100], Header(0x28, 0xE0, fixedParms: 1, floatParms: 1),
            Words(0x80000000, 0x100, 0xFF, 1, 0x20), [0x00, 0x01, (byte)'f'], [30]);
        var t = Read(code, 0x100);
        Assert.Equal(0x80000000u, t.ParameterInfo);
        Assert.Equal(0x100u, t.TbOffset);
        Assert.Equal(0, t.FunctionStart);
        Assert.Equal(0xFFu, t.HandlerMask);
        Assert.Equal([0x20u], t.ControlledStorage);
        Assert.Equal("f", t.Name);
        Assert.Equal((byte)30, t.AllocaRegister);
        Assert.Equal(12 + 20 + 3 + 1, t.Length);
    }

    [Fact]
    public void A_non_zero_word_is_not_a_table()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(TracebackTable.Read(Words(1, 0, 0), 0, diagnostics));
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(8)]     // in the flag bytes
    [InlineData(14)]    // in tb_offset
    [InlineData(18)]    // in the name
    public void A_truncated_table_is_reported(int length)
    {
        var code = Concat(Header(0x20, 0x40), Words(4), [0x00, 0x04], "name"u8.ToArray())[..length];
        var diagnostics = new List<Diagnostic>();
        Assert.Null(TracebackTable.Read(code, 0, diagnostics));
        var d = Assert.Single(diagnostics);
        Assert.Equal("traceback.truncated", d.Code);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(0, d.Offset);
    }

    [Fact]
    public void A_controlled_storage_count_past_the_end_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(TracebackTable.Read(Concat(Header(0x08), Words(0x40000000)), 0, diagnostics));
        Assert.Equal("traceback.truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_tb_offset_before_the_section_is_reported()
    {
        var diagnostics = new List<Diagnostic>();
        var t = TracebackTable.Read(Concat(new byte[8], Header(0x20), Words(12)), 8, diagnostics);
        Assert.NotNull(t);
        Assert.Equal(12u, t.TbOffset);
        Assert.Null(t.FunctionStart);
        var d = Assert.Single(diagnostics);
        Assert.Equal("traceback.bad-offset", d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(8, d.Offset);
    }

    // Byte 4 (stores_bc, fixup, fpr_saved) and byte 5's gpr_saved describe the frame; they add no field.
    [Fact]
    public void Saved_register_counts_add_no_field()
    {
        var t = Read(Concat(new byte[0x10], Header(0x20, 0x40, b4: 0x80 | 0x40 | 18, b5: 31), Words(0x10), [0x00, 0x01, (byte)'g']), 0x10);
        Assert.Equal(0, t.FunctionStart);
        Assert.Equal(0x0000_2040_D21F_0000ul, t.Flags);
        Assert.Equal(0x10u, t.TbOffset);
        Assert.Equal("g", t.Name);
        Assert.Null(t.ParameterInfo);
        Assert.False(t.HasVectorInfo);
        Assert.False(t.HasExtensionTable);
        Assert.Equal(12 + 4 + 3, t.Length);
    }

    // parmsonstk (byte 7 bit 0) is not a parameter count: no parminfo.
    [Fact]
    public void Parameters_on_the_stack_alone_bring_no_parminfo()
    {
        var t = Read(Concat(Header(b7: 0x01), Words(0x12345678)));
        Assert.Equal(0, t.FloatParameterCount);
        Assert.Equal(0, t.FixedParameterCount);
        Assert.Null(t.ParameterInfo);
        Assert.Equal(12, t.Length);
    }

    // has_vec (byte 5 bit $40) and has_ext_table (bit $80) add fields after alloca_reg that are not read: the table
    // is kept, its length stops before them, and it is reported.
    [Theory]
    [InlineData(0x40, true, false)]
    [InlineData(0x80, false, true)]
    [InlineData(0xC0, true, true)]
    public void Vector_and_extension_fields_are_reported(byte b5, bool vector, bool extension)
    {
        var diagnostics = new List<Diagnostic>();
        var t = TracebackTable.Read(Concat(Header(b3: 0x20, b5: b5), [29], new byte[8]), 0, diagnostics);
        Assert.NotNull(t);
        Assert.Equal(vector, t.HasVectorInfo);
        Assert.Equal(extension, t.HasExtensionTable);
        Assert.Equal((byte)29, t.AllocaRegister);
        Assert.Equal(13, t.Length);
        var d = Assert.Single(diagnostics);
        Assert.Equal("traceback.extension-unread", d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(0, d.Offset);
    }

    [Fact]
    public void An_offset_at_the_end_of_the_code_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(TracebackTable.Read(new byte[12], 12, diagnostics));
        Assert.Equal("traceback.truncated", Assert.Single(diagnostics).Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(13)]
    public void An_offset_outside_the_code_throws(int offset) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TracebackTable.Read(new byte[12], offset, []));

    [Fact]
    public void Find_takes_the_tables_after_each_blr()
    {
        var code = Concat(
            Words(0x7C0802A6, Blr),                                        // 0: function A, table at 8
            Header(0x20, 0x40), Words(8), [0x00, 0x01, (byte)'A'], [0],        // 8..26, padded to 28
            Words(0, 0x38600000),                                           // 28: a zero word not after blr
            Words(Blr), Words(0), [1, 0, 0, 0, 0, 0, 0, 0],                 // 36: version 1 is not a table
            Words(Blr), Header(0x20), Words(0x34));                         // 52: a table, function at 4
        var diagnostics = new List<Diagnostic>();
        var tables = TracebackTable.Find(code, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal([8, 56], tables.Select(t => t.Offset));
        Assert.Equal(["A", null], tables.Select(t => t.Name));
        Assert.Equal([0, 4], tables.Select(t => t.FunctionStart));
    }

    [Fact]
    public void Find_skips_a_truncated_table_and_reports_it()
    {
        var code = Concat(Words(Blr), Header(b3: 0x40), [0x00, 0x09, (byte)'x']);
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(TracebackTable.Find(code, diagnostics));
        Assert.Equal("traceback.truncated", Assert.Single(diagnostics).Code);
    }

    // The scan takes only a table after blr: one after a tail call (b) or bctr is not found [ClassicMac].
    [Fact]
    public void Find_skips_a_table_after_a_function_that_does_not_end_in_blr()
    {
        var code = Concat(Words(0x48000010), Header(0x20), Words(4), Words(0x4E800420), Header(0x20), Words(4));
        Assert.Empty(TracebackTable.Find(code, []));
    }

    [Fact]
    public void Find_needs_the_whole_flag_block()
    {
        // A blr then a zero word with fewer than 8 bytes after it is not taken for a table.
        Assert.Empty(TracebackTable.Find(Concat(Words(Blr, 0), [0, 0, 0, 0]), []));
        Assert.Empty(TracebackTable.Find(Array.Empty<byte>(), []));
    }
}
