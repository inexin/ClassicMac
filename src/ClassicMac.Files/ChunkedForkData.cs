using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Files;

/// <summary>
/// One stretch of a chunked disk image (NDIF chunks, UDIF runs): the disk bytes it covers, how it is stored, and where.
/// </summary>
/// <param name="Start">The first disk byte it covers.</param>
/// <param name="End">The disk byte after the last one it covers.</param>
/// <param name="Type">The format's own type code, for the decoder.</param>
/// <param name="Storage">Zeros (nothing stored), raw (the bytes as they are), or compressed.</param>
/// <param name="Offset">Where its stored bytes start in the image's data.</param>
/// <param name="Stored">How many bytes are stored.</param>
internal sealed record DiskChunk(long Start, long End, uint Type, ChunkStorage Storage, long Offset, long Stored);

/// <summary>How a chunk's bytes are kept.</summary>
internal enum ChunkStorage
{
    Zeros,
    Raw,
    Compressed,
}

/// <summary>
/// A disk read on demand from its chunks, in order: zero chunks read as zeros, raw ones straight from the image,
/// compressed ones through <paramref name="decode"/> (one decoded chunk is kept). Stretches no chunk covers read as
/// zeros. <paramref name="decode"/> fills the output from the stored bytes and returns a problem to report, or null;
/// each chunk's problem is reported once through <paramref name="report"/>, and the rest of that chunk reads as zeros.
/// </summary>
internal sealed class ChunkedForkData(ForkData data, IReadOnlyList<DiskChunk> chunks, long length,
    Func<DiskChunk, byte[], byte[], string?> decode, Action<DiskChunk, string> report) : ForkData
{
    private readonly ForkData data = data;
    private readonly IReadOnlyList<DiskChunk> chunks = chunks;
    private readonly HashSet<int> reported = [];
    private int cachedIndex = -1;
    private byte[] cached = [];

    public override long Length => length;

    public override Stream Open() => new ChunkStream(this);

    private ReadOnlySpan<byte> Decoded(int index)
    {
        lock (reported)
        {
            if (index == cachedIndex)
            {
                return cached;
            }

            var chunk = chunks[index];
            var bytes = new byte[chunk.End - chunk.Start];
            if (chunk.Stored > 0)
            {
                var stored = data.Slice(chunk.Offset, chunk.Stored).ToArray();
                if (decode(chunk, stored, bytes) is { } problem && reported.Add(index))
                {
                    report(chunk, problem);
                }
            }
            cachedIndex = index;
            cached = bytes;
            return cached;
        }
    }

    // The last chunk starting at or before the position, found by binary search (chunks are in order).
    private int ChunkAt(long position)
    {
        int low = 0, high = chunks.Count - 1, found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (chunks[middle].Start <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    private sealed class ChunkStream(ChunkedForkData fork) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => fork.Length;

        public override long Position
        {
            get => position;
            set => position = value >= 0 ? value : throw new IOException("Cannot seek before the start of the stream.");
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (buffer.Length > 0 && position < fork.Length)
            {
                var index = fork.ChunkAt(position);
                int take;
                if (index < 0 || position >= fork.chunks[index].End)
                {
                    var nextStart = index + 1 < fork.chunks.Count ? fork.chunks[index + 1].Start : fork.Length;
                    take = (int)Math.Min(buffer.Length, nextStart - position);
                    buffer[..take].Clear();
                }
                else
                {
                    var chunk = fork.chunks[index];
                    var within = position - chunk.Start;
                    take = (int)Math.Min(buffer.Length, chunk.End - position);
                    switch (chunk.Storage)
                    {
                        case ChunkStorage.Zeros:
                            buffer[..take].Clear();
                            break;
                        case ChunkStorage.Raw:
                            // Straight from the image; what the image does not store reads as zeros.
                            var stored = (int)Math.Clamp(chunk.Stored - within, 0, take);
                            if (stored > 0)
                            {
                                using var source = fork.data.Slice(chunk.Offset + within, stored).Open();
                                stored = source.ReadAtLeast(buffer[..stored], stored, throwOnEndOfStream: false);
                            }
                            buffer[stored..take].Clear();
                            break;
                        default:
                            fork.Decoded(index).Slice((int)within, take).CopyTo(buffer);
                            break;
                    }
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
                SeekOrigin.End => fork.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
