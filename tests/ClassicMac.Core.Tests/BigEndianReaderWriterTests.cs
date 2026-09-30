using System.IO;

namespace ClassicMac.Core.Tests;

public class BigEndianReaderWriterTests
{
    [Fact]
    public void Writer_and_reader_round_trip_big_endian_scalars_and_mac_values()
    {
        byte[] bytes = new byte[4 + 2 + 2 + 4 + 4 + 8 + 8 + 4 + 4 + 8];
        var writer = new BigEndianWriter(bytes);
        writer.WriteFourCC(FourCC.FromString("PICT"));
        writer.WriteInt16(-2);
        writer.WriteUInt16(0xABCD);
        writer.WriteInt32(-2);
        writer.WriteUInt32(0x89ABCDEF);
        writer.WriteInt64(-2);
        writer.WriteUInt64(0x0123456789ABCDEF);
        writer.WriteUnsignedFixed(new UnsignedFixed(0xFEDCBA98));
        writer.WriteMacPoint(new MacPoint(-2, 0x1234));
        writer.WriteMacRect(new MacRect(-1, 2, 3, -4));

        Assert.Equal(bytes.Length, writer.Position);
        Assert.Equal(0, writer.Remaining);
        Assert.Equal(new byte[]
        {
            0x50, 0x49, 0x43, 0x54,
            0xFF, 0xFE, 0xAB, 0xCD,
            0xFF, 0xFF, 0xFF, 0xFE,
            0x89, 0xAB, 0xCD, 0xEF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE,
            0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
            0xFE, 0xDC, 0xBA, 0x98,
            0xFF, 0xFE, 0x12, 0x34,
            0xFF, 0xFF, 0x00, 0x02, 0x00, 0x03, 0xFF, 0xFC
        }, bytes);

        var reader = new BigEndianReader(bytes);
        Assert.Equal(new FourCC("PICT"u8), reader.ReadFourCC());
        Assert.Equal((short)-2, reader.ReadInt16());
        Assert.Equal((ushort)0xABCD, reader.ReadUInt16());
        Assert.Equal(-2, reader.ReadInt32());
        Assert.Equal(0x89ABCDEFu, reader.ReadUInt32());
        Assert.Equal(-2L, reader.ReadInt64());
        Assert.Equal(0x0123456789ABCDEFul, reader.ReadUInt64());
        Assert.Equal(0xFEDCBA98u, reader.ReadUnsignedFixed().Raw);
        Assert.Equal(new MacPoint(-2, 0x1234), reader.ReadMacPoint());
        Assert.Equal(new MacRect(-1, 2, 3, -4), reader.ReadMacRect());
        Assert.Equal(bytes.Length, reader.Position);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Fixed_values_round_trip_through_cursor_methods()
    {
        byte[] bytes = new byte[8];
        var writer = new BigEndianWriter(bytes);
        writer.WriteFixed(new Fixed(unchecked((int)0xFFFF8000)));
        writer.WriteUnsignedFixed(new UnsignedFixed(0xAC440000));

        var reader = new BigEndianReader(bytes);
        Assert.Equal(new Fixed(unchecked((int)0xFFFF8000)), reader.ReadFixed());
        Assert.Equal(new UnsignedFixed(0xAC440000), reader.ReadUnsignedFixed());
    }

    [Fact]
    public void Read_bytes_returns_a_borrowed_slice_and_updates_cursor()
    {
        byte[] source = [1, 2, 3, 4];
        var reader = new BigEndianReader(source);

        ReadOnlySpan<byte> borrowed = reader.ReadBytes(2);
        Assert.Equal(2, reader.Position);
        Assert.Equal(2, reader.Remaining);

        source[0] = 9;
        Assert.Equal((byte)9, borrowed[0]);
    }

    [Fact]
    public void Try_operations_leave_state_and_output_clear_when_the_span_is_too_short()
    {
        byte[] source = [0x12];
        var reader = new BigEndianReader(source);
        Assert.False(reader.TryReadUInt16(out ushort word));
        Assert.Equal((ushort)0, word);
        Assert.Equal(0, reader.Position);

        Assert.False(reader.TryReadMacPoint(out MacPoint point));
        Assert.Equal(default, point);
        Assert.Equal(0, reader.Position);

        Assert.False(reader.TryReadBytes(2, out ReadOnlySpan<byte> bytes));
        Assert.True(bytes.IsEmpty);
        Assert.Equal(0, reader.Position);
        Assert.False(reader.TrySkip(-1));
        Assert.Equal(0, reader.Position);

        byte[] destination = [0xAA];
        var writer = new BigEndianWriter(destination);
        Assert.False(writer.TryWriteUInt16(0x1234));
        Assert.Equal(0, writer.Position);
        Assert.Equal(new byte[] { 0xAA }, destination);

        Assert.False(writer.TryWriteMacRect(new MacRect(1, 2, 3, 4)));
        Assert.Equal(0, writer.Position);
        Assert.Equal(new byte[] { 0xAA }, destination);
        Assert.False(writer.TryWriteBytes([1, 2]));
        Assert.Equal(0, writer.Position);
        Assert.Equal(new byte[] { 0xAA }, destination);
        Assert.False(writer.TrySkip(2));
        Assert.Equal(0, writer.Position);
    }

    [Fact]
    public void Try_operations_succeed_at_exact_buffer_end()
    {
        byte[] destination = new byte[2];
        var writer = new BigEndianWriter(destination);
        Assert.True(writer.TryWriteInt16(-2));
        Assert.Equal(2, writer.Position);
        Assert.Equal(0, writer.Remaining);

        var reader = new BigEndianReader(destination);
        Assert.True(reader.TryReadInt16(out short value));
        Assert.Equal((short)-2, value);
        Assert.Equal(2, reader.Position);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Throwing_reads_and_writes_report_exhausted_spans()
    {
        var reader = new BigEndianReader([0x12]);
        bool readThrew = false;
        try { reader.ReadUInt16(); }
        catch (EndOfStreamException) { readThrew = true; }
        Assert.True(readThrew);
        Assert.Equal(0, reader.Position);

        var writer = new BigEndianWriter(new byte[1]);
        bool writeThrew = false;
        try { writer.WriteUInt16(0x1234); }
        catch (ArgumentException) { writeThrew = true; }
        Assert.True(writeThrew);
        Assert.Equal(0, writer.Position);
    }

    [Fact]
    public void Position_is_bounded_by_the_span()
    {
        var reader = new BigEndianReader(new byte[2]);
        reader.Position = 2;
        Assert.Equal(0, reader.Remaining);

        bool threw = false;
        try { reader.Position = 3; }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Assert.True(threw);
        Assert.Equal(2, reader.Position);
    }

    [Fact]
    public void At_methods_use_absolute_offsets_without_moving_the_cursor()
    {
        byte[] source = [0xAA, 0x01, 0x02, 0x03, 0x04, 0xBB];
        var reader = new BigEndianReader(source) { Position = 5 };

        Assert.Equal(0x01020304, reader.ReadInt32At(1));
        Assert.Equal(5, reader.Position);
        Assert.True(reader.TryReadUInt16At(3, out ushort word));
        Assert.Equal((ushort)0x0304, word);
        Assert.Equal(5, reader.Position);
        Assert.False(reader.TryReadUInt32At(3, out uint missing));
        Assert.Equal(0u, missing);
        Assert.False(reader.TryReadBytesAt(-1, 1, out ReadOnlySpan<byte> missingBytes));
        Assert.True(missingBytes.IsEmpty);
        Assert.Equal(5, reader.Position);

        byte[] destination = [0xAA, 0, 0, 0, 0, 0xBB];
        var writer = new BigEndianWriter(destination) { Position = 5 };
        Assert.True(writer.TryWriteUInt32At(1, 0x01020304));
        Assert.Equal(5, writer.Position);
        Assert.Equal(new byte[] { 0xAA, 1, 2, 3, 4, 0xBB }, destination);

        Assert.False(writer.TryWriteMacRectAt(1, new MacRect(1, 2, 3, 4)));
        Assert.Equal(5, writer.Position);
        Assert.Equal(new byte[] { 0xAA, 1, 2, 3, 4, 0xBB }, destination);
    }
}
