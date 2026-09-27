using System;
using System.Collections.Generic;
using ClassicMac.Core;

namespace ClassicMac.Resources.Export
{
    /// <summary>
    /// Turns resources of some types into modern files (text, JSON, RTF, PNG, WAV, …) for <see cref="ResourceExporter"/>.
    /// The built-in decoders live in <c>ClassicMac.Resources.Decoders</c>; applications add their own for private types.
    /// </summary>
    public interface IResourceDecoder
    {
        /// <summary>The decoder's name, recorded in the manifest.</summary>
        string Name { get; }

        /// <summary>The decoder's version, recorded in the manifest; raised when its output changes.</summary>
        int Version { get; }

        /// <summary>Whether this decoder handles resources of <paramref name="type"/>.</summary>
        bool CanDecode(FourCC type);

        /// <summary>
        /// The files for one resource, the main one first; empty when the resource cannot be decoded (it is then exported
        /// raw). Problems go to <see cref="DecodeInput.Diagnostics"/>.
        /// </summary>
        IReadOnlyList<DecodedFile> Decode(DecodeInput input);
    }

    /// <summary>One file a decoder produced.</summary>
    /// <param name="Extension">The file's extension, with the dot (<c>.txt</c>).</param>
    /// <param name="Content">The file's bytes.</param>
    /// <param name="Encoding">The text encoding the resource was read with (an IANA name such as <c>macintosh</c>), if any.</param>
    public sealed record DecodedFile(string Extension, ReadOnlyMemory<byte> Content, string? Encoding = null);

    /// <summary>What a decoder works with.</summary>
    public sealed class DecodeInput
    {
        private readonly ResourceFork fork;
        private readonly ReadOptions options;

        /// <summary>An input for <paramref name="resource"/> of <paramref name="fork"/>, whose data is <paramref name="data"/>.</summary>
        public DecodeInput(Resource resource, ReadOnlyMemory<byte> data, ResourceFork fork, ReadOptions? options = null,
            ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentNullException.ThrowIfNull(fork);
            Resource = resource;
            Data = data;
            this.fork = fork;
            this.options = options ?? ReadOptions.Default;
            Diagnostics = diagnostics ?? new List<Diagnostic>();
        }

        /// <summary>The resource.</summary>
        public Resource Resource { get; }

        /// <summary>Its data as applications see it (decompressed when compressed).</summary>
        public ReadOnlyMemory<byte> Data { get; }

        /// <summary>Where problems go.</summary>
        public ICollection<Diagnostic> Diagnostics { get; }

        /// <summary>
        /// Another resource of the same fork, decompressed (a <c>TEXT</c> decoder looks for the <c>styl</c> of the same
        /// ID), or null.
        /// </summary>
        public ReadOnlyMemory<byte>? Find(FourCC type, short id) =>
            fork.Find(type, id) is { } other
                ? (ReadOnlyMemory<byte>?)ResourceDecompression.Default.GetData(other, fork, options, Diagnostics)
                : null;
    }
}
