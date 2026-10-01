using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClassicMac.Files.Iso
{
    /// <summary>
    /// A whole CD as a run of 2048-byte blocks numbered by absolute sector (logical block address), built from the
    /// data tracks of a cue sheet or the sessions of a raw image, with the start of the last session: what the Mac's
    /// CD driver hands the file systems for a multisession disc (ISO9660.md section 15). Blocks no data track covers
    /// (audio, the gap between sessions, tracks not read) read as zeros.
    /// </summary>
    internal sealed class CdDisc : ForkData
    {
        private const int Block = 2048;
        private readonly Segment[] segments;

        /// <summary>A run of <paramref name="Blocks"/> blocks from block <paramref name="Lba"/>, read from <paramref name="Data"/>.</summary>
        internal readonly record struct Segment(long Lba, long Blocks, ForkData Data);

        public CdDisc(IEnumerable<Segment> runs, long lastSession)
        {
            segments = runs.Where(s => s.Blocks > 0).OrderBy(s => s.Lba).ToArray();
            Length = segments.Length == 0 ? 0 : segments.Max(s => s.Lba + s.Blocks) * Block;
            LastSession = lastSession;
        }

        /// <summary>D: the block where the last session's first track starts, counted from track 1.</summary>
        public long LastSession { get; }

        public override long Length { get; }

        public override Stream Open() => new DiscStream(this);

        private sealed class DiscStream(CdDisc disc) : Stream
        {
            private readonly Stream?[] open = new Stream?[disc.segments.Length];
            private long position;

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => disc.Length;

            public override long Position
            {
                get => position;
                set => position = value >= 0 ? value : throw new IOException("Cannot seek before the start of the stream.");
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var total = 0;
                while (buffer.Length > 0 && position < disc.Length)
                {
                    var block = position / Block;
                    var index = Array.FindLastIndex(disc.segments, s => s.Lba <= block);
                    int take;
                    if (index >= 0 && block < disc.segments[index].Lba + disc.segments[index].Blocks)
                    {
                        var segment = disc.segments[index];
                        var end = (segment.Lba + segment.Blocks) * Block;
                        take = (int)Math.Min(buffer.Length, end - position);
                        var stream = open[index] ??= segment.Data.Open();
                        stream.Position = position - segment.Lba * Block;
                        stream.ReadExactly(buffer[..take]);
                    }
                    else
                    {
                        // Up to the next segment, or the end: zeros.
                        var next = index + 1 < disc.segments.Length ? disc.segments[index + 1].Lba * Block : disc.Length;
                        take = (int)Math.Min(buffer.Length, next - position);
                        buffer[..take].Clear();
                    }
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
                    SeekOrigin.End => disc.Length + offset,
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
                if (disposing)
                {
                    foreach (var stream in open) stream?.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
