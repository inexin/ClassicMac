using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClassicMac.Files
{
    // A fork stored in pieces (a volume's extents): ranges of a parent fork read one after another, cut to the fork's
    // logical length. Opened on demand like any fork; nothing is copied.
    internal sealed class ExtentForkData : ForkData
    {
        private readonly ForkData parent;
        private readonly (long Offset, long Length)[] ranges;
        private readonly long length;

        public ExtentForkData(ForkData parent, IEnumerable<(long Offset, long Length)> ranges, long length)
        {
            this.parent = parent;
            this.ranges = ranges.ToArray();
            var available = this.ranges.Sum(r => r.Length);
            if (length > available)
                throw new ArgumentOutOfRangeException(nameof(length), $"{length} bytes do not fit the {available} the extents hold.");
            foreach (var (offset, count) in this.ranges)
            {
                if (offset < 0 || count < 0 || offset > parent.Length - count)
                    throw new ArgumentOutOfRangeException(nameof(ranges), $"{offset}+{count} lies outside the {parent.Length}-byte image.");
            }
            this.length = length;
        }

        public override long Length => length;

        public override Stream Open() => new ExtentStream(parent.Open(), ranges, length);

        private sealed class ExtentStream(Stream inner, (long Offset, long Length)[] ranges, long length) : Stream
        {
            private long position;

            public override bool CanRead => true;

            public override bool CanSeek => inner.CanSeek;

            public override bool CanWrite => false;

            public override long Length => length;

            public override long Position
            {
                get => position;
                set => position = value >= 0 ? value : throw new IOException("Cannot seek before the start of the stream.");
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var total = 0;
                while (buffer.Length > 0 && position < length)
                {
                    // Find the range holding the current position.
                    long start = 0;
                    var index = 0;
                    while (start + ranges[index].Length <= position)
                    {
                        start += ranges[index].Length;
                        index++;
                    }
                    var within = position - start;
                    var available = Math.Min(ranges[index].Length - within, length - position);
                    var take = (int)Math.Min(available, buffer.Length);
                    inner.Seek(ranges[index].Offset + within, SeekOrigin.Begin);
                    inner.ReadExactly(buffer[..take]);
                    buffer = buffer[take..];
                    position += take;
                    total += take;
                }
                return total;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                Position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => position + offset,
                    SeekOrigin.End => length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
                return position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
