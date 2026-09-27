using System;
using System.IO;

namespace ClassicMac.Files
{
    /// <summary>
    /// The contents of one fork, opened on demand so browsing a large disk image does not read every file.
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

        private sealed class BytesForkData(ReadOnlyMemory<byte> bytes) : ForkData
        {
            public override long Length => bytes.Length;

            public override Stream Open() => new MemoryStream(bytes.ToArray(), writable: false);
        }
    }
}
