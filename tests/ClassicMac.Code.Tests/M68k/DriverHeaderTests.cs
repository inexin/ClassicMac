using ClassicMac.Code.M68k;
using ClassicMac.Core;
using static ClassicMac.Code.Tests.M68k.CodeBuilder;

namespace ClassicMac.Code.Tests.M68k;

// DRVR: flags, delay, emask, menu, open/prime/control/status/close offsets, Pascal name at $12.
public class DriverHeaderTests
{
    private static byte[] Driver(ushort flags, ushort[] offsets, string name, int codeLength = 0x40)
    {
        var w = new BigEndianWriter();
        w.WriteUInt16(flags);
        w.WriteUInt16(5);
        w.WriteUInt16(0x0102);
        w.WriteInt16(-3);
        foreach (var o in offsets)
        {
            w.WriteUInt16(o);
        }

        w.WriteByte(name.Length);
        w.WriteBytes(MacRoman.Encode(name));
        w.WriteZeros(codeLength);
        return w.ToArray();
    }

    [Fact]
    public void Reads_the_header()
    {
        var diagnostics = new List<Diagnostic>();
        var header = DriverHeader.Read(Driver(0x4F00, [0x20, 0x22, 0x24, 0x26, 0x28], ".Sony"), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.Equal(DriverFlags.NeedLock | DriverFlags.Status | DriverFlags.Control | DriverFlags.Write | DriverFlags.Read, header.Flags);
        Assert.Equal(((ushort)5, (ushort)0x0102, (short)-3), (header.Delay, header.EventMask, header.Menu));
        Assert.Equal(((ushort)0x20, (ushort)0x22, (ushort)0x24, (ushort)0x26, (ushort)0x28),
            (header.Open, header.Prime, header.Control, header.Status, header.Close));
        Assert.Equal(".Sony", header.Name);
        Assert.True(header.IsStandard);
    }

    [Fact]
    public void The_time_and_goodbye_flags()
    {
        var header = DriverHeader.Read(Driver(0x3000, [0x20, 0x20, 0x20, 0x20, 0x20], ".X"), [])!;
        Assert.Equal(DriverFlags.NeedTime | DriverFlags.NeedGoodbye, header.Flags);
    }

    [Fact]
    public void Flag_bits_outside_the_named_ones_are_kept()
    {
        // .PCCardSRAMDisk: 6F04 003C 0000 0000 0022 0028 002E 0034 003A 0F '.PCCardSRAMDisk'; .DSP's flags are $4440.
        var diagnostics = new List<Diagnostic>();
        var header = DriverHeader.Read(Driver(0x6F04, [0x22, 0x28, 0x2E, 0x34, 0x3A], ".PCCardSRAMDisk"), diagnostics)!;
        Assert.Empty(diagnostics);
        Assert.True(header.IsStandard);
        Assert.Equal((ushort)0x6F04, (ushort)header.Flags);
        Assert.Equal((DriverFlags)0x0004, header.Flags & ~(DriverFlags.Read | DriverFlags.Write | DriverFlags.Control
            | DriverFlags.Status | DriverFlags.NeedTime | DriverFlags.NeedLock));
        Assert.Equal((ushort)0x4440, (ushort)DriverHeader.Read(Driver(0x4440, [0x80, 0x82, 0x84, 0x86, 0x88], ".DSP", 0x90), [])!.Flags);
    }

    [Fact]
    public void A_routine_offset_of_0_is_standard()
    {
        // The System's video drivers have prime = 0.
        var diagnostics = new List<Diagnostic>();
        var header = DriverHeader.Read(Driver(0x4C00, [0x20, 0, 0x22, 0x24, 0x26], ".VSC_Video"), diagnostics)!;
        Assert.True(header.IsStandard);
        Assert.Equal((ushort)0, header.Prime);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void An_offset_at_the_last_byte_is_standard_and_one_at_the_length_is_not()
    {
        var data = Driver(0, [0x20, 0x20, 0x20, 0x20, 0x20], ".X", codeLength: 0x20);
        Assert.True(DriverHeader.Read(Driver(0, [0x20, 0x20, 0x20, 0x20, (ushort)(data.Length - 1)], ".X", codeLength: 0x20), [])!.IsStandard);
        var diagnostics = new List<Diagnostic>();
        Assert.False(DriverHeader.Read(Driver(0, [0x20, 0x20, 0x20, 0x20, (ushort)data.Length], ".X", codeLength: 0x20), diagnostics)!.IsStandard);
        Assert.Equal("m68k.drvr-nonstandard", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void Offsets_outside_the_resource_make_a_non_standard_DRVR()
    {
        // The shape of a driver that starts with code: 6000 0102 0000 0001 6000 0102 2324 ...
        var diagnostics = new List<Diagnostic>();
        var data = Words(0x6000, 0x0102, 0x0000, 0x0001, 0x6000, 0x0102, 0x2324, 0x0001, 0x0001, 0x0001, 0x0001, 0, 0, 0, 0, 0, 0, 0);
        var header = DriverHeader.Read(data, diagnostics)!;
        Assert.False(header.IsStandard);
        Assert.Equal("m68k.drvr-nonstandard", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_name_past_the_resource_makes_a_non_standard_DRVR()
    {
        var diagnostics = new List<Diagnostic>();
        var data = Driver(0, [0, 0, 0, 0, 0], ".Name", codeLength: 0);
        var header = DriverHeader.Read(data[..^2], diagnostics)!;
        Assert.False(header.IsStandard);
        Assert.Equal("m68k.drvr-nonstandard", Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void A_resource_shorter_than_the_fixed_fields_is_not_read()
    {
        var diagnostics = new List<Diagnostic>();
        Assert.Null(DriverHeader.Read(new byte[0x12], diagnostics));
        Assert.Equal("m68k.drvr-nonstandard", Assert.Single(diagnostics).Code);
    }
}
