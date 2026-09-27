using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Resources.Compression;

namespace ClassicMac.Resources
{
    /// <summary>
    /// Turns a resource's stored data into what the Resource Manager would hand an application, decompressing it
    /// when the map entry's compressed bit (<c>resExtended</c>, later <c>resCompressed</c>) is set. Follows the Mac OS 9
    /// Resource Manager by default and the 68k ROM one when <see cref="ReadOptions.ResourceManager"/> says so; both
    /// were read in disassembly (Mac OS 9.0 'Resources', ROM $077D CheckLoad $FFC7A186–$FFC7A414).
    /// </summary>
    public sealed class ResourceDecompression
    {
        private static readonly FourCC Dcmp = FourCC.FromString("dcmp");
        private readonly Dictionary<short, IResourceDecompressor> decompressors;

        /// <summary>The built-in decompressors: System <c>'dcmp'</c> 0, 1, 2 and 3.</summary>
        public static ResourceDecompression Default { get; } = new([]);

        /// <summary>
        /// The built-in decompressors plus <paramref name="extra"/>; an extra one with a built-in ID replaces it, as a
        /// file's own <c>'dcmp'</c> would on the Mac.
        /// </summary>
        public ResourceDecompression(IEnumerable<IResourceDecompressor> extra)
        {
            ArgumentNullException.ThrowIfNull(extra);
            decompressors = new IResourceDecompressor[] { new Dcmp01(0), new Dcmp01(1), new Dcmp2(), new Dcmp3() }
                .Concat(extra)
                .GroupBy(d => d.Id)
                .ToDictionary(g => g.Key, g => g.Last());
        }

        /// <summary>
        /// The resource's data as an application would see it: decompressed when compressed, otherwise as stored. When
        /// decompression is impossible the stored data comes back and the reason goes to <paramref name="diagnostics"/>.
        /// <paramref name="fork"/>, if given, is checked for a <c>'dcmp'</c> of its own that the Mac would run instead.
        /// </summary>
        public ReadOnlyMemory<byte> GetData(
            Resource resource, ResourceFork? fork = null, ReadOptions? options = null,
            ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(resource);
            options ??= ReadOptions.Default;
            var stored = resource.GetData();
            if ((resource.Attributes & ResourceAttributes.Compressed) == 0) return stored;

            var label = resource.ToString();
            void Report(DiagnosticSeverity severity, string code, string message) =>
                diagnostics?.Add(new Diagnostic(severity, code, $"{label}: {message}"));

            var bytes = stored.Span;
            if (!CompressedResourceHeader.HasSignature(bytes))
            {
                // Both Resource Managers load such a resource as is.
                Report(DiagnosticSeverity.Info, "resource.not-compressed",
                    "marked compressed but has no compressed-resource header; used as stored.");
                return stored;
            }
            if (!CompressedResourceHeader.TryRead(bytes, out var header))
            {
                Report(DiagnosticSeverity.Error, "resource.dcmp-header", "the compressed-resource header is truncated.");
                return stored;
            }

            var rom = options.ResourceManager == ResourceManagerModel.Rom68k;
            if (!header.IsCompressed)
            {
                // "Extended, uncompressed": ROM strips the 12-byte extended header; OS 9 reads from the start instead,
                // keeping the header and losing the last 12 bytes.
                var length = Math.Max(0, stored.Length - 12);
                if (rom) return stored[12..];
                Report(DiagnosticSeverity.Warning, "resource.extended-uncompressed",
                    "extended but not compressed; Mac OS 9 keeps the header and drops the last 12 bytes.");
                return stored[..length];
            }
            if (header.Version is not (8 or 9))
            {
                if (rom)
                {
                    Report(DiagnosticSeverity.Error, "resource.dcmp-version",
                        $"header version {header.Version} is not 8 or 9 (CantDecompress).");
                    return stored;
                }
                Report(DiagnosticSeverity.Info, "resource.dcmp-version",
                    $"header version {header.Version} is not 8 or 9; read as version 9, as Mac OS 9 does.");
            }
            if (header.IsVersion8 && header.Reserved != 0)
            {
                Report(DiagnosticSeverity.Error, "resource.dcmp-header",
                    "the version-8 header's reserved word is not zero (CantDecompress).");
                return stored;
            }
            if (header.DecompressedSize > options.MaxResourceSize)
            {
                Report(DiagnosticSeverity.Error, "resource.too-large",
                    $"decompresses to {header.DecompressedSize} bytes, over the {options.MaxResourceSize}-byte limit.");
                return stored;
            }
            if (!decompressors.TryGetValue(header.DecompressorId, out var decompressor))
            {
                Report(DiagnosticSeverity.Warning, "resource.dcmp-unknown",
                    $"needs 'dcmp' {header.DecompressorId}, which is not available; kept compressed.");
                return stored;
            }
            if (fork?.Find(Dcmp, header.DecompressorId) is not null)
            {
                Report(DiagnosticSeverity.Info, "resource.dcmp-overridden",
                    $"the file carries its own 'dcmp' {header.DecompressorId}, which the Mac would run; the built-in one was used.");
            }

            // The block is the decompressed size plus the expansion bytes; the compressed bytes go at its tail and are
            // decompressed in place (ROM $FFC7A318–$FFC7A32A; OS 9 $100019E0).
            var blockLength = (long)header.DecompressedSize + header.ExpansionBytes;
            var sourceLength = stored.Length - CompressedResourceHeader.Length;
            if (sourceLength > blockLength)
            {
                Report(DiagnosticSeverity.Error, "resource.dcmp-overrun",
                    $"the {sourceLength} compressed bytes do not fit the {blockLength}-byte block.");
                return stored;
            }
            var block = new byte[blockLength];
            var source = (int)(blockLength - sourceLength);
            bytes[CompressedResourceHeader.Length..].CopyTo(block.AsSpan(source));

            int written;
            try
            {
                written = decompressor.Decompress(new DecompressionContext(
                    header, block, source, options, d => diagnostics?.Add(d with { Message = $"{label}: {d.Message}" })));
            }
            catch (DecompressionOverrunException e)
            {
                Report(DiagnosticSeverity.Error, "resource.dcmp-overrun", $"{e.Message} Kept compressed.");
                return stored;
            }
            catch (DecompressionFormException e)
            {
                Report(DiagnosticSeverity.Error, "resource.dcmp-form", $"{e.Message} Kept compressed.");
                return stored;
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                Report(DiagnosticSeverity.Error, "resource.dcmp-failed", $"{e.Message} Kept compressed.");
                return stored;
            }

            // The Mac then sets the handle to the declared size, whatever was written (error ignored).
            if (written != header.DecompressedSize)
            {
                Report(DiagnosticSeverity.Warning, "resource.dcmp-size",
                    $"'dcmp' {header.DecompressorId} wrote {written} bytes for a declared {header.DecompressedSize}; " +
                    "the result is cut or padded with the block's contents, as on the Mac.");
            }
            return block.AsMemory(0, (int)header.DecompressedSize);
        }
    }
}
