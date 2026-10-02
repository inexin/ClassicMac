using ClassicMac.Code.M68k;
using ClassicMac.Core;

namespace ClassicMac.Code.Tests.M68k;

// Retro68 'RELA': ULEB128 values ended by 0; pos starts at -1, pos += v >> 2, base = v & 3, patch at codeStart + pos.
public class Retro68RelocationsTests
{
    private static byte[] Uleb(params uint[] values)
    {
        var bytes = new List<byte>();
        foreach (var value in values)
        {
            var v = value;
            do
            {
                var b = (byte)(v & 0x7F);
                v >>= 7;
                bytes.Add(v != 0 ? (byte)(b | 0x80) : b);
            } while (v != 0);
        }
        return bytes.ToArray();
    }

    private static uint V(int delta, Retro68RelocationBase kind) => ((uint)delta << 2) | (uint)kind;

    [Fact]
    public void Code_starts_after_the_near_or_far_header()
    {
        Assert.Equal(4, Retro68Relocations.CodeStart([0x00, 0x00, 0x00, 0x01]));
        Assert.Equal(0x28, Retro68Relocations.CodeStart([0xFF, 0xFF, 0x00, 0x00]));
    }

    [Fact]
    public void Positions_start_at_minus_one_and_step_by_the_value_shifted_right_two()
    {
        var rela = Uleb(V(3, Retro68RelocationBase.Code), V(4, Retro68RelocationBase.A5),
            V(200, Retro68RelocationBase.UninitializedData), V(2, Retro68RelocationBase.InitializedData), 0).Append((byte)0).ToArray();
        // V(200, 2) = 802 = $322: two ULEB bytes, $A2 $06.
        Assert.Equal(new byte[] { 0xA2, 0x06 }, Uleb(802));
        var diagnostics = new List<Diagnostic>();
        var relocations = Retro68Relocations.Read(rela, 4, 0x200, diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(
        [
            new Retro68Relocation(4 + 2, Retro68RelocationBase.Code),
            new Retro68Relocation(4 + 6, Retro68RelocationBase.A5),
            new Retro68Relocation(4 + 206, Retro68RelocationBase.UninitializedData),
            new Retro68Relocation(4 + 208, Retro68RelocationBase.InitializedData),
        ], relocations);
    }

    [Fact]
    public void Data_relocations_count_from_the_start_of_the_data()
    {
        var relocations = Retro68Relocations.Read(Uleb(V(1, Retro68RelocationBase.InitializedData), 0), 0, 8, []);
        Assert.Equal([new Retro68Relocation(0, Retro68RelocationBase.InitializedData)], relocations);
    }

    [Fact]
    public void A_far_segment_counts_from_its_code_after_the_28_byte_header()
    {
        var relocations = Retro68Relocations.Read(Uleb(V(5, Retro68RelocationBase.A5), 0), 0x28, 0x100, []);
        Assert.Equal([new Retro68Relocation(0x28 + 4, Retro68RelocationBase.A5)], relocations);
    }

    [Fact]
    public void An_empty_list_is_its_terminator()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(Retro68Relocations.Read(new byte[] { 0, 0 }, 4, 0x50, diagnostics));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void A_missing_terminator_is_reported_and_the_relocations_read_are_kept()
    {
        var diagnostics = new List<Diagnostic>();
        var relocations = Retro68Relocations.Read(Uleb(V(3, Retro68RelocationBase.Code)), 4, 0x100, diagnostics);
        Assert.Single(relocations);
        Assert.Equal("m68k.rela-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_value_cut_off_mid_way_is_truncated()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(Retro68Relocations.Read(new byte[] { 0x80 }, 4, 0x100, diagnostics));
        Assert.Equal("m68k.rela-truncated", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_value_wider_than_32_bits_stops_reading()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(Retro68Relocations.Read(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x00 }, 4, 0x100, diagnostics));
        Assert.Equal("m68k.rela-value", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void An_odd_position_is_reported_but_kept()
    {
        var diagnostics = new List<Diagnostic>();
        var relocations = Retro68Relocations.Read(Uleb(V(2, Retro68RelocationBase.Code), 0), 4, 0x100, diagnostics);
        Assert.Equal([new Retro68Relocation(5, Retro68RelocationBase.Code)], relocations);
        Assert.Equal("m68k.rela-odd", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_long_outside_the_target_is_reported_and_left_out()
    {
        var diagnostics = new List<Diagnostic>();
        // pos 13 → offset 17; 17 + 4 > 20.
        var relocations = Retro68Relocations.Read(Uleb(V(3, Retro68RelocationBase.A5), V(12, Retro68RelocationBase.A5), 0), 4, 20, diagnostics);
        Assert.Equal([new Retro68Relocation(6, Retro68RelocationBase.A5)], relocations);
        Assert.Contains(diagnostics, d => d.Code == "m68k.rela-range");
    }

    [Fact]
    public void A_first_step_of_zero_points_before_the_code_and_is_out_of_range()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Empty(Retro68Relocations.Read(Uleb(V(0, Retro68RelocationBase.InitializedData), 0), 0, 0x100, diagnostics));
        Assert.Equal("m68k.rela-range", Assert.Single(diagnostics).Code);
    }
}
