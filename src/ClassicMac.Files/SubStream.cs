using System;
using System.IO;

namespace ClassicMac.Files;

// A read-only window onto part of another stream, which it owns. Seekable when the inner stream is; otherwise it
// skips to the start by reading.
internal sealed class SubStream : Stream
{
    private readonly Stream inner;
    private readonly long start;
    private readonly long length;
    private long position;

    public SubStream(Stream inner, long start, long length)
    {
        this.inner = inner;
        this.start = start;
        this.length = length;
        if (inner.CanSeek)
        {
            inner.Seek(start, SeekOrigin.Begin);
        }
        else
        {
            Skip(inner, start);
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = length - position;
        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        var read = inner.Read(buffer);
        position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (!inner.CanSeek)
        {
            throw new NotSupportedException();
        }

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("Cannot seek before the start of the stream.");
        }

        inner.Seek(start + target, SeekOrigin.Begin);
        position = target;
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void Skip(Stream stream, long count)
    {
        var scratch = new byte[81920];
        while (count > 0)
        {
            var read = stream.Read(scratch, 0, (int)Math.Min(scratch.Length, count));
            if (read == 0)
            {
                break;
            }

            count -= read;
        }
    }
}
