using System;

namespace ClassicMac.Resources.Compression
{
    // Thrown when a decompressor would read or write outside its block or working buffer. The Mac has no such check
    // (it would read or overwrite the heap); we stop instead.
    internal sealed class DecompressionOverrunException(string message) : Exception(message);

    // Thrown when a resource's header form does not match the decompressor's calling convention (the Mac would jump
    // into the wrong entry point).
    internal sealed class DecompressionFormException(string message) : Exception(message);

    // Source and destination cursors over one in-place block: the compressed bytes sit at the block's tail and the
    // output grows from offset 0, so output that overtakes the input corrupts it exactly as on the Mac.
    internal sealed class BlockCursor(byte[] block, int source)
    {
        public int Source { get; private set; } = source;

        public int Written { get; private set; }

        public byte[] Block => block;

        public byte ReadByte()
        {
            if (Source >= block.Length) throw new DecompressionOverrunException("The decompressor read past the end of its input.");
            return block[Source++];
        }

        public ushort ReadU16() => (ushort)(ReadByte() << 8 | ReadByte());

        public uint ReadU32() => (uint)ReadU16() << 16 | ReadU16();

        // A source byte ahead of the cursor, without consuming it.
        public byte PeekByte(int ahead)
        {
            var at = (long)Source + ahead;
            if (at >= block.Length) throw new DecompressionOverrunException("The decompressor read past the end of its input.");
            return block[at];
        }

        public void WriteByte(byte value)
        {
            if (Written >= block.Length) throw new DecompressionOverrunException("The decompressor wrote past the end of its block.");
            block[Written++] = value;
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

        // Copies from earlier output; offset counts back from the current end, and the ranges may overlap.
        public void CopyBack(int offset, int count)
        {
            if (offset <= 0 || offset > Written) throw new DecompressionOverrunException("A back-reference points before the output.");
            for (var i = 0; i < count; i++) WriteByte(block[Written - offset]);
        }
    }
}
