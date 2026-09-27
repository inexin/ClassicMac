using System;
using System.IO;

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
            return new FileForkData(Path.GetFullPath(path), new FileInfo(path).Length);
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
            using var stream = Open();
            var buffer = new byte[(int)Math.Min(count, Length)];
            stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer;
        }

        /// <summary>The whole fork in memory; throws <see cref="InvalidDataException"/> above <paramref name="maxLength"/>.</summary>
        public byte[] ToArray(long maxLength = int.MaxValue)
        {
            if (Length > maxLength || Length > int.MaxValue)
                throw new InvalidDataException($"The {Length}-byte fork exceeds the {maxLength}-byte limit.");
            using var stream = Open();
            var buffer = new byte[Length];
            stream.ReadExactly(buffer);
            return buffer;
        }

        private sealed class BytesForkData(ReadOnlyMemory<byte> bytes) : ForkData
        {
            public override long Length => bytes.Length;

            public override Stream Open() => new MemoryStream(bytes.ToArray(), writable: false);

            public override ForkData Slice(long offset, long length)
            {
                if (offset < 0 || length < 0 || offset > Length - length)
                    throw new ArgumentOutOfRangeException(nameof(offset), $"{offset}+{length} lies outside the {Length}-byte fork.");
                return new BytesForkData(bytes.Slice((int)offset, (int)length));
            }
        }

        private sealed class FileForkData(string path, long length) : ForkData
        {
            public override long Length => length;

            public override Stream Open() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private sealed class SliceForkData(ForkData parent, long offset, long length) : ForkData
        {
            public override long Length => length;

            public override Stream Open() => new SubStream(parent.Open(), offset, length);

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
