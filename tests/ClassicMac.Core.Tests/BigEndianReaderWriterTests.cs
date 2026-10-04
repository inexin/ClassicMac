using System.IO;

namespace ClassicMac.Core.Tests;

public class BigEndianReaderWriterTests
{
    [Fact]
    public void Writer_and_reader_round_trip_big_endian_scalars_and_mac_values()
    {
        var writer = new BigEndianWriter(capacity: 0);
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

        var bytes = writer.ToArray();
        Assert.Equal(4 + 2 + 2 + 4 + 4 + 8 + 8 + 4 + 4 + 8, writer.Length);
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
        var writer = new BigEndianWriter();
        writer.WriteFixed(new Fixed(unchecked((int)0xFFFF8000)));
        writer.WriteUnsignedFixed(new UnsignedFixed(0xAC440000));

        var reader = new BigEndianReader(writer.WrittenMemory);
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
    public void Read_sub_reader_is_bounded_and_advances_the_parent()
    {
        var parent = new BigEndianReader(new byte[] { 0xAA, 0x01, 0x02, 0x03, 0x04, 0xBB });
        Assert.Equal((byte)0xAA, parent.ReadByte());

        var section = parent.ReadSubReader(4);

        Assert.Equal(5, parent.Position);
        Assert.Equal(1, parent.Remaining);
        Assert.Equal(0x01020304, section.ReadInt32());
        Assert.Equal(0, section.Remaining);
        Assert.Equal((byte)0xBB, parent.ReadByte());
        Assert.Equal(6, parent.Position);

        bool childThrew = false;
        try
        { section.ReadByte(); }
        catch (EndOfStreamException) { childThrew = true; }
        Assert.True(childThrew);
        Assert.Equal(4, section.Position);
    }

    [Fact]
    public void Failed_sub_reader_creation_leaves_parent_cursor_unchanged()
    {
        var reader = new BigEndianReader(new byte[] { 0x12, 0x34 }) { Position = 1 };

        bool threw = false;
        try
        { reader.ReadSubReader(2); }
        catch (EndOfStreamException) { threw = true; }

        Assert.True(threw);
        Assert.Equal(1, reader.Position);
    }

    // A sub-reader at an absolute offset: a structure inside the buffer read by its own offsets, the parent untouched.
    [Fact]
    public void A_sub_reader_at_an_offset_reads_from_its_start_and_leaves_the_parent()
    {
        var parent = new BigEndianReader(new byte[] { 0xAA, 0x01, 0x02, 0x03, 0x04, 0xBB }) { Position = 5 };

        var section = parent.ReadSubReaderAt(1, 4);
        var rest = parent.ReadSubReaderAt(4);

        Assert.Equal(5, parent.Position);
        Assert.Equal((0, 4), (section.Position, section.Length));
        Assert.Equal(0x0304, section.ReadUInt16At(2));
        Assert.Equal(0x01020304, section.ReadInt32());
        Assert.Throws<EndOfStreamException>(() => section.ReadByte());
        Assert.Equal(new byte[] { 0x04, 0xBB }, rest.Source.ToArray());
        Assert.Equal(0, parent.ReadSubReaderAt(6).Length);                              // at the end: empty
        Assert.Throws<EndOfStreamException>(() => parent.ReadSubReaderAt(3, 4));
        Assert.Throws<EndOfStreamException>(() => parent.ReadSubReaderAt(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.ReadSubReaderAt(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => parent.ReadSubReaderAt(0, -1));
        Assert.Equal(5, parent.Position);
    }

    [Fact]
    public void Failed_try_reads_leave_the_position_unchanged()
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

    }

    [Fact]
    public void Try_reads_succeed_at_the_exact_end()
    {
        var writer = new BigEndianWriter();
        writer.WriteInt16(-2);

        var reader = new BigEndianReader(writer.ToArray());
        Assert.True(reader.TryReadInt16(out short value));
        Assert.Equal((short)-2, value);
        Assert.Equal(2, reader.Position);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Throwing_reads_report_exhausted_data()
    {
        var reader = new BigEndianReader(new byte[] { 0x12 });
        bool readThrew = false;
        try
        { reader.ReadUInt16(); }
        catch (EndOfStreamException) { readThrew = true; }
        Assert.True(readThrew);
        Assert.Equal(0, reader.Position);
    }

    [Fact]
    public void Position_is_bounded_by_the_span()
    {
        var reader = new BigEndianReader(new byte[2]);
        reader.Position = 2;
        Assert.Equal(0, reader.Remaining);

        bool threw = false;
        try
        { reader.Position = 3; }
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
    }

    [Fact]
    public void A_reader_over_a_stream_reads_from_its_position_to_its_end_and_leaves_it_open()
    {
        var stream = new MemoryStream([9, 9, 0x12, 0x34, 0x56]) { Position = 2 };
        var reader = new BigEndianReader(stream);
        Assert.Equal(3, reader.Length);
        Assert.Equal(0x1234, reader.ReadUInt16());
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, reader.Source.ToArray());
        Assert.Equal(5, stream.Position);
        Assert.True(stream.CanRead);
        Assert.Throws<ArgumentNullException>(() => new BigEndianReader((Stream)null!));
    }

    [Fact]
    public void A_reader_works_across_iterators_and_as_a_field()
    {
        static IEnumerable<ushort> Words(BigEndianReader reader)
        {
            while (reader.Remaining >= 2)
            {
                yield return reader.ReadUInt16();
            }
        }
        Assert.Equal(new ushort[] { 1, 2 }, Words(new BigEndianReader(new byte[] { 0, 1, 0, 2, 9 })));
        var sub = new BigEndianReader(new byte[] { 1, 2, 3, 4 }).ReadSubReader(2);
        Assert.Equal(new byte[] { 1, 2 }, sub.Source.ToArray());
    }

    [Fact]
    public void The_writer_grows_and_patches_what_it_has_written()
    {
        var writer = new BigEndianWriter(capacity: 2);
        writer.WriteUInt32(0);                                       // placeholder for a length
        for (int i = 0; i < 1000; i++)
        {
            writer.WriteUInt16((ushort)i);
        }

        writer.WriteZeros(3);
        writer.WriteUInt32At(0, (uint)(writer.Length - 4));
        Assert.Equal(4 + 2000 + 3, writer.Length);
        var bytes = writer.ToArray();
        Assert.Equal(2003u, new BigEndianReader(bytes).ReadUInt32());
        Assert.Equal(999, new BigEndianReader(bytes).ReadUInt16At(4 + 2 * 999));
        Assert.Equal(new byte[3], bytes[^3..]);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteUInt16At(writer.Length - 1, 1));

        var stream = new MemoryStream();
        writer.WriteTo(stream);
        Assert.Equal(bytes, stream.ToArray());
        writer.Clear();
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void A_writer_over_an_array_patches_it_in_place_until_it_grows()
    {
        var block = new byte[12];
        var writer = new BigEndianWriter(block);
        Assert.Equal(12, writer.Length);
        writer.WriteUInt16At(0, 0x1234);
        writer.WriteMacRectAt(2, new MacRect(1, 2, 3, 4));
        writer.WriteFourCCAt(8, FourCC.FromString("TEXT"));
        Assert.Equal(new byte[] { 0x12, 0x34, 0, 1, 0, 2, 0, 3, (byte)'T', (byte)'E', (byte)'X', (byte)'T' }, block);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteUInt32At(10, 0));

        writer.WriteByte(0xFF);                                      // grows into a new array
        Assert.Equal(13, writer.Length);
        writer.WriteUInt16At(0, 0);
        Assert.Equal(0x12, block[0]);
    }

    [Fact]
    public void Any_number_is_written_as_the_field_when_it_fits()
    {
        var writer = new BigEndianWriter();
        long length = 0x12345678;
        int count = 3;
        writer.WriteUInt32(length);
        writer.WriteUInt16(count);
        writer.WriteByte(count);
        writer.WriteInt16(-2L);
        writer.WriteUInt16((ushort)(count - 4));                      // an explicit cast still wraps on purpose
        writer.WriteUInt32At(0, 7L);
        Assert.Equal(new byte[] { 0, 0, 0, 7, 0, 3, 3, 0xFF, 0xFE, 0xFF, 0xFF }, writer.ToArray());

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteUInt32(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteUInt32(0x1_0000_0000L));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteUInt16(70000));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteByte(256));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteInt16At(0, 40000));
        Assert.Equal(11, writer.Length);
    }
}
