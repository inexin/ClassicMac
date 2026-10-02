using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClassicMac.Files
{
    /// <summary>
    /// The contents of one fork, opened on demand so browsing a large disk image does not read every file. Containers
    /// read from a <see cref="ForkData"/> and hand out slices of it, so nested containers stay lazy too.
    /// </summary>
    public abstract class ForkData
    {
        /// <summary>An empty fork.</summary>
        public static ForkData Empty { get; } = new BytesForkData(ReadOnlyMemory<byte>.Empty);

        /// <summary>The fork's length in bytes.</summary>
        public abstract long Length { get; }

        /// <summary>Opens a new read-only stream over the fork; the caller disposes it.</summary>
        public abstract Stream Open();

        /// <summary>A fork held in memory.</summary>
        public static ForkData FromBytes(ReadOnlyMemory<byte> bytes) => new BytesForkData(bytes);

        /// <summary>A host file, opened for reading each time the fork is opened. Its length is taken now.</summary>
        public static ForkData FromFile(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return FromFile(path, TimeSpan.FromSeconds(2));
        }

        // A host file whose handle is closed once no read has used it for idle.
        internal static ForkData FromFile(string path, TimeSpan idle) =>
            new FileForkData(Path.GetFullPath(path), new FileInfo(path).Length, idle);

        // Whether a host file's fork holds its file open (for tests).
        internal static bool IsHostFileOpen(ForkData fork) => fork is FileForkData { IsOpen: true };

        // The host files' forks holding their file open.
        private static readonly ConcurrentDictionary<FileForkData, byte> OpenFiles = new();

        /// <summary>
        /// Closes the handles reads keep open on <paramref name="path"/>, so the file can be replaced; the next read opens
        /// it again.
        /// </summary>
        internal static void CloseHostFile(string path)
        {
            var full = Path.GetFullPath(path);
            foreach (var file in OpenFiles.Keys)
            {
                if (string.Equals(file.FilePath, full, StringComparison.OrdinalIgnoreCase)) file.Close();
            }
        }

        /// <summary>A range of this fork, opened through it; no bytes are copied.</summary>
        public virtual ForkData Slice(long offset, long length)
        {
            if (offset < 0 || length < 0 || offset > Length - length)
                throw new ArgumentOutOfRangeException(nameof(offset), $"{offset}+{length} lies outside the {Length}-byte fork.");
            return new SliceForkData(this, offset, length);
        }

        /// <summary>
        /// Up to <paramref name="count"/> bytes from the start, fewer if the fork is shorter; for probing headers.
        /// </summary>
        public byte[] ReadPrefix(int count)
        {
            var buffer = new byte[(int)Math.Min(count, Length)];
            ReadAt(0, buffer);
            return buffer;
        }

        /// <summary>
        /// Reads bytes from <paramref name="offset"/> into <paramref name="buffer"/> without opening a stream: as many as
        /// fit, fewer at the end of the fork, none past it. Returns how many were read.
        /// </summary>
        public int ReadAt(long offset, Span<byte> buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset >= Length || buffer.IsEmpty) return 0;
            if (buffer.Length > Length - offset) buffer = buffer[..(int)(Length - offset)];
            ReadAtCore(offset, buffer);
            return buffer.Length;
        }

        /// <summary>
        /// Fills <paramref name="buffer"/> from <paramref name="offset"/>, a range inside the fork. By default through
        /// <see cref="Open"/>; forks that can read in place override it.
        /// </summary>
        protected virtual void ReadAtCore(long offset, Span<byte> buffer)
        {
            using var stream = Open();
            if (stream.CanSeek)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }
            else
            {
                var skip = new byte[Math.Min(offset, 81920)];
                for (var left = offset; left > 0;) left -= stream.Read(skip, 0, (int)Math.Min(left, skip.Length)) is > 0 and var n ? n : throw new EndOfStreamException();
            }
            stream.ReadExactly(buffer);
        }

        /// <summary>The whole fork in memory; throws <see cref="InvalidDataException"/> above <paramref name="maxLength"/>.</summary>
        public byte[] ToArray(long maxLength = int.MaxValue)
        {
            if (Length > maxLength || Length > int.MaxValue)
                throw new InvalidDataException($"The {Length}-byte fork exceeds the {maxLength}-byte limit.");
            var buffer = new byte[Length];
            ReadAt(0, buffer);
            return buffer;
        }

        private sealed class BytesForkData(ReadOnlyMemory<byte> bytes) : ForkData
        {
            public override long Length => bytes.Length;

            // Over the bytes themselves when they are an array's, without copying.
            public override Stream Open() => MemoryMarshal.TryGetArray(bytes, out var array)
                ? new MemoryStream(array.Array!, array.Offset, array.Count, writable: false)
                : new MemoryStream(bytes.ToArray(), writable: false);

            protected override void ReadAtCore(long offset, Span<byte> buffer) =>
                bytes.Span.Slice((int)offset, buffer.Length).CopyTo(buffer);

            public override ForkData Slice(long offset, long length)
            {
                if (offset < 0 || length < 0 || offset > Length - length)
                    throw new ArgumentOutOfRangeException(nameof(offset), $"{offset}+{length} lies outside the {Length}-byte fork.");
                return new BytesForkData(bytes.Slice((int)offset, (int)length));
            }
        }

        // Reads go through one handle, kept open between them (opening the file costs far more than a small read, and
        // probing a volume's files makes many) and closed when no read has used it for idle.
        private sealed class FileForkData(string path, long length, TimeSpan idle) : ForkData
        {
            private readonly object gate = new();
            private SafeFileHandle? handle;
            private Timer? closer;
            private long lastUse;

            public override long Length => length;

            public string FilePath => path;

            public bool IsOpen
            {
                get { lock (gate) return handle is not null; }
            }

            // Read even while another program (an emulator with the image mounted) has the file open for writing.
            public override Stream Open() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            protected override void ReadAtCore(long offset, Span<byte> buffer)
            {
                var file = Handle();
                while (!buffer.IsEmpty)
                {
                    var read = RandomAccess.Read(file, buffer, offset);
                    if (read == 0) throw new EndOfStreamException($"{path} is shorter than when it was opened.");
                    buffer = buffer[read..];
                    offset += read;
                }
            }

            private SafeFileHandle Handle()
            {
                lock (gate)
                {
                    lastUse = Environment.TickCount64;
                    if (handle is null)
                    {
                        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        OpenFiles[this] = 0;
                        closer ??= new Timer(_ => CloseIfIdle());
                        closer.Change(idle, idle);
                    }
                    return handle;
                }
            }

            // A read that took the handle just before this closes it still completes: SafeHandle counts its users.
            private void CloseIfIdle()
            {
                lock (gate)
                {
                    if (Environment.TickCount64 - lastUse >= (long)idle.TotalMilliseconds) Close();
                }
            }

            public void Close()
            {
                lock (gate)
                {
                    if (handle is null) return;
                    handle.Dispose();
                    handle = null;
                    closer?.Change(Timeout.Infinite, Timeout.Infinite);
                    OpenFiles.TryRemove(this, out _);
                }
            }
        }

        private sealed class SliceForkData(ForkData parent, long offset, long length) : ForkData
        {
            public override long Length => length;

            public override Stream Open() => new SubStream(parent.Open(), offset, length);

            protected override void ReadAtCore(long start, Span<byte> buffer) => parent.ReadAtCore(offset + start, buffer);

            // A slice of a slice opens the original once.
            public override ForkData Slice(long start, long count)
            {
                if (start < 0 || count < 0 || start > Length - count)
                    throw new ArgumentOutOfRangeException(nameof(start), $"{start}+{count} lies outside the {Length}-byte fork.");
                return new SliceForkData(parent, offset + start, count);
            }
        }
    }
}
