using System;

namespace ClassicMac.Resources.Compression
{
    // Thrown when a decompressor would read or write outside the memory we model (the block and the zeroed memory after
    // it). The Mac has no such check (it would read or overwrite the heap); we stop instead.
    internal sealed class DecompressionOverrunException(string message) : Exception(message);

    // Thrown when a resource's header form does not match the decompressor's calling convention (the Mac would jump
    // into the wrong entry point).
    internal sealed class DecompressionFormException(string message) : Exception(message);

    // Source and destination cursors over one in-place block: the compressed bytes sit at the block's tail and the
    // output grows from offset 0, so output that overtakes the input corrupts it exactly as on the Mac. Past the block
    // lies the zeroed stand-in for the memory that follows it; using it is allowed but recorded in the context.
    internal sealed class BlockCursor
    {
        private readonly byte[] memory;
        private readonly int blockLength;
        private readonly DecompressionContext? context;

        public BlockCursor(DecompressionContext context)
            : this(context.Block, context.SourceOffset, context.BlockLength) => this.context = context;

        // For tests: a block with no memory after it.
        public BlockCursor(byte[] block, int source)
            : this(block, source, block.Length)
        {
        }

        private BlockCursor(byte[] memory, int source, int blockLength)
        {
            this.memory = memory;
            this.blockLength = blockLength;
            Source = source;
        }

        public int Source { get; private set; }

        public int Written { get; private set; }

        public byte ReadByte()
        {
            var value = Peek(Source);
            Source++;
            return value;
        }

        public ushort ReadU16() => (ushort)(ReadByte() << 8 | ReadByte());

        public uint ReadU32() => (uint)ReadU16() << 16 | ReadU16();

        // A source byte ahead of the cursor, without consuming it.
        public byte PeekByte(int ahead) => Peek((long)Source + ahead);

        public void WriteByte(byte value)
        {
            if (Written >= memory.Length)
                throw new DecompressionOverrunException("The decompressor wrote past the memory after its block.");
            if (Written >= blockLength && context is not null) context.WrotePastBlock = true;
            memory[Written++] = value;
        }

        public void WriteU16(int value)
        {
            WriteByte((byte)(value >> 8));
            WriteByte((byte)value);
        }

        public void WriteU32(uint value)
        {
            WriteU16((int)(value >> 16));
            WriteU16((int)value);
        }

        // Copies bytes from the source, one at a time, as the Mac's loops do.
        public void CopyLiteral(int count)
        {
            for (var i = 0; i < count; i++) WriteByte(ReadByte());
        }

        // Copies from earlier output, forward and a byte at a time, so an offset shorter than the length repeats the
        // pattern. An offset beyond the output would read the memory before the block on the Mac; we stop.
        public void CopyBack(int offset, int count)
        {
            if (offset <= 0 || offset > Written) throw new DecompressionOverrunException("A back-reference points before the output.");
            for (var i = 0; i < count; i++) WriteByte(memory[Written - offset]);
        }

        private byte Peek(long at)
        {
            if (at >= memory.Length)
                throw new DecompressionOverrunException("The decompressor read past the memory after its input.");
            if (at >= blockLength && context is not null) context.ReadPastInput = true;
            return memory[at];
        }
    }
}
