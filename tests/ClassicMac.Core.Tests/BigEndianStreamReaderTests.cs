using ClassicMac.Core;
using Xunit;

namespace ClassicMac.Core.Tests;

public class BigEndianStreamReaderTests
{
    // A stream that hands out at most `chunk` bytes per Read, cannot seek, and records whether it was disposed.
    private sealed class TrickleStream(byte[] data, int chunk = 1) : Stream
    {
        private int position;

        public bool Disposed { get; private set; }

        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            int n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - position);
            data.AsSpan(position, n).CopyTo(buffer);
            position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static readonly byte[] Values =
    [
        0xFE,                                                   // byte
        0xFF, 0xFE, 0x12, 0x34,                                 // Int16 -2, UInt16 $1234
        0xFF, 0xFF, 0xFF, 0xFD, 0x89, 0xAB, 0xCD, 0xEF,         // Int32 -3, UInt32
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFC,         // Int64 -4
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,         // UInt64
        (byte)'P', (byte)'I', (byte)'C', (byte)'T',             // FourCC
        0x00, 0x0A, 0xFF, 0xF6,                                 // Point v 10, h -10
        0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04,         // Rect
        0x00, 0x01, 0x80, 0x00, 0x80, 0x00, 0x00, 0x00,         // Fixed 1.5, UnsignedFixed 32768.0
    ];

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void Reads_every_primitive_whatever_the_stream_returns_per_read(int chunk)
    {
        var reader = new BigEndianStreamReader(new TrickleStream(Values, chunk));
        Assert.Equal(0xFE, reader.ReadByte());
        Assert.Equal(-2, reader.ReadInt16());
        Assert.Equal(0x1234, reader.ReadUInt16());
        Assert.Equal(-3, reader.ReadInt32());
        Assert.Equal(0x89ABCDEFu, reader.ReadUInt32());
        Assert.Equal(-4L, reader.ReadInt64());
        Assert.Equal(0x0102030405060708UL, reader.ReadUInt64());
        Assert.Equal(FourCC.FromString("PICT"), reader.ReadFourCC());
        Assert.Equal(new MacPoint(10, -10), reader.ReadMacPoint());
        Assert.Equal(new MacRect(1, 2, 3, 4), reader.ReadMacRect());
        Assert.Equal(new Fixed(0x00018000), reader.ReadFixed());
        Assert.Equal(new UnsignedFixed(0x80000000), reader.ReadUnsignedFixed());
        Assert.Equal(Values.Length, reader.Position);
        Assert.True(reader.IsAtEnd);
    }

    [Fact]
    public void Peek_looks_ahead_without_consuming()
    {
        var reader = new BigEndianStreamReader(new TrickleStream([1, 2, 3, 4, 5]));
        reader.ReadByte();
        var ahead = new byte[3];
        Assert.Equal(3, reader.Peek(ahead));
        Assert.Equal(new byte[] { 2, 3, 4 }, ahead);
        Assert.Equal(1, reader.Position);
        Assert.Equal(new byte[] { 2, 3 }, reader.ReadBytes(2));       // the peeked bytes come back first
        var more = new byte[8];
        Assert.Equal(2, reader.Peek(more));                           // fewer at the end
        Assert.Equal(new byte[] { 4, 5 }, more[..2]);
        Assert.Equal(0x0405, reader.ReadUInt16());
        Assert.Equal(0, reader.Peek(more));
    }

    [Fact]
    public void Reads_to_the_exact_end_and_reports_truncation()
    {
        var reader = new BigEndianStreamReader(new TrickleStream([1, 2, 3]));
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.ReadBytes(3));
        Assert.True(reader.IsAtEnd);
        Assert.Empty(reader.ReadBytes(0));
        Assert.Throws<EndOfStreamException>(() => reader.ReadByte());

        var short16 = new BigEndianStreamReader(new TrickleStream([1]));
        Assert.Throws<EndOfStreamException>(() => short16.ReadUInt16());
        Assert.Equal(1, short16.Position);                            // what was there is consumed

        var shortBytes = new BigEndianStreamReader(new TrickleStream([1, 2]));
        Assert.Throws<EndOfStreamException>(() => shortBytes.ReadBytes(int.MaxValue));   // no 2 GB allocation first

        var skip = new BigEndianStreamReader(new TrickleStream([1, 2, 3, 4]));
        skip.Skip(1);
        Assert.Equal(3, skip.SkipAtMost(10));
        Assert.Throws<EndOfStreamException>(() => skip.Skip(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => skip.ReadBytes(-1));
    }

    [Fact]
    public void Starts_at_the_streams_position_and_leaves_it_open()
    {
        var stream = new MemoryStream([9, 9, 0x12, 0x34, 0x56]) { Position = 2 };
        var reader = new BigEndianStreamReader(stream);
        Assert.Equal(0, reader.Position);
        Assert.Equal(0x1234, reader.ReadUInt16());
        Assert.Equal(2, reader.Position);
        Assert.Equal(4, stream.Position);
        Assert.True(stream.CanRead);                                  // not disposed

        var trickle = new TrickleStream([1, 2]);
        new BigEndianStreamReader(trickle).ReadUInt16();
        Assert.False(trickle.Disposed);
        var closed = new MemoryStream();
        closed.Dispose();
        Assert.Throws<ArgumentException>(() => new BigEndianStreamReader(closed));
    }

    [Fact]
    public void Returning_the_lookahead_moves_a_seekable_stream_back_to_the_position()
    {
        var stream = new MemoryStream([1, 2, 3, 4, 5, 6]);
        var reader = new BigEndianStreamReader(stream);
        reader.ReadByte();
        reader.Peek(new byte[4]);
        Assert.Equal(5, stream.Position);
        reader.ReturnLookahead();
        Assert.Equal(1, stream.Position);
    }
}
