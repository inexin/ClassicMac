using System;
using ClassicMac.Core;

namespace ClassicMac.Resources;

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
        CompressedResourceHeader header, byte[] block, int blockLength, int sourceOffset, ReadOptions options,
        Action<Diagnostic> report)
    {
        Header = header;
        Block = block;
        BlockLength = blockLength;
        SourceOffset = sourceOffset;
        Options = options;
        this.report = report;
    }

    /// <summary>The resource's compressed-resource header.</summary>
    public CompressedResourceHeader Header { get; }

    /// <summary>
    /// The block (decompressed size plus expansion bytes, compressed data at its tail, <see cref="BlockLength"/>
    /// bytes) followed by zeroed memory standing in for what follows the block on the Mac, where decompressors may
    /// overshoot or read past their input.
    /// </summary>
    public byte[] Block { get; }

    /// <summary>The length of the block proper within <see cref="Block"/>.</summary>
    public int BlockLength { get; }

    /// <summary>Where the compressed data starts in <see cref="Block"/>; it ends at <see cref="BlockLength"/>.</summary>
    public int SourceOffset { get; }

    // Set by the built-in decompressors when they use the memory after the block.
    internal bool ReadPastInput { get; set; }

    internal bool WrotePastBlock { get; set; }

    /// <summary>The reading options, including which Resource Manager is modelled.</summary>
    public ReadOptions Options { get; }

    /// <summary>Reports a problem that does not stop decompression.</summary>
    public void Report(DiagnosticSeverity severity, string code, string message) =>
        report(new Diagnostic(severity, code, message));
}
