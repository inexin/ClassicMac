using System;
using ClassicMac.Core;

namespace ClassicMac.Resources
{
    /// <summary>
    /// Decompresses resources for one <c>'dcmp'</c> ID. Applications with their own compressors register one with
    /// <see cref="ResourceDecompression"/>.
    /// </summary>
    public interface IResourceDecompressor
    {
        /// <summary>The <c>'dcmp'</c> ID handled.</summary>
        short Id { get; }

        /// <summary>
        /// Decompresses in place, as the Mac does: <see cref="DecompressionContext.Block"/> holds the compressed bytes at
        /// its tail, from <see cref="DecompressionContext.SourceOffset"/>, and the output is written from offset 0.
        /// Returns the number of bytes written. Throw <see cref="System.IO.InvalidDataException"/> for bad input.
        /// </summary>
        int Decompress(DecompressionContext context);
    }

    /// <summary>What a decompressor works on.</summary>
    public sealed class DecompressionContext
    {
        private readonly Action<Diagnostic> report;

        internal DecompressionContext(
            CompressedResourceHeader header, byte[] block, int sourceOffset, ReadOptions options, Action<Diagnostic> report)
        {
            Header = header;
            Block = block;
            SourceOffset = sourceOffset;
            Options = options;
            this.report = report;
        }

        /// <summary>The resource's compressed-resource header.</summary>
        public CompressedResourceHeader Header { get; }

        /// <summary>The block: decompressed size plus expansion bytes, compressed data at its tail.</summary>
        public byte[] Block { get; }

        /// <summary>Where the compressed data starts in <see cref="Block"/>.</summary>
        public int SourceOffset { get; }

        /// <summary>The reading options, including which Resource Manager is modelled.</summary>
        public ReadOptions Options { get; }

        /// <summary>Reports a problem that does not stop decompression.</summary>
        public void Report(DiagnosticSeverity severity, string code, string message) =>
            report(new Diagnostic(severity, code, message));
    }
}
