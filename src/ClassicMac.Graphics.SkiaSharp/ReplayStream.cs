using System;
using System.IO;

namespace ClassicMac.Graphics.SkiaSharp
{
    // A read-only, forward-only stream that returns bytes already read from a stream (to probe its format) and then
    // the rest of it. The inner stream is not disposed.
    internal sealed class ReplayStream(byte[] prefix, int prefixLength, Stream inner) : Stream
    {
        private int prefixPosition;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (prefixPosition < prefixLength)
            {
                int count = Math.Min(buffer.Length, prefixLength - prefixPosition);
                prefix.AsSpan(prefixPosition, count).CopyTo(buffer);
                prefixPosition += count;
                return count;
            }
            return inner.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
