using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Files.Containers;

namespace ClassicMac.Files
{
    /// <summary>One file in an unwrapped input: the container format it was read with and what it held.</summary>
    /// <param name="Format">The format the file was found in (the reader's name, or the host layout at the root).</param>
    /// <param name="File">The Mac file.</param>
    /// <param name="Children">The files its data fork contained, if the data fork was itself a container.</param>
    public sealed record ContainerNode(string Format, MacFile File, IReadOnlyList<ContainerNode> Children)
    {
        /// <summary>The files at the bottom of the tree: those whose data fork is not a container.</summary>
        public IEnumerable<ContainerNode> Leaves() => Children.Count == 0 ? [this] : Children.SelectMany(c => c.Leaves());
    }

    /// <summary>
    /// Unwraps nested containers: tries each reader on a file's data fork and, when one matches, reads the files it
    /// holds and unwraps those in turn, up to <see cref="ContainerReadOptions.MaxNestingDepth"/>. A MacBinary file inside
    /// a BinHex file inside an AppleSingle file unwraps to one tree.
    /// </summary>
    public sealed class ContainerUnwrapper
    {
        private readonly IReadOnlyList<IContainerReader> readers;

        /// <summary>
        /// The built-in readers: AppleSingle, AppleDouble, MacBinary III, II and I, BinHex 4.0.
        /// </summary>
        public static ContainerUnwrapper Default { get; } = new([]);

        /// <summary>The built-in readers, with <paramref name="extra"/> tried first.</summary>
        public ContainerUnwrapper(IEnumerable<IContainerReader> extra)
        {
            ArgumentNullException.ThrowIfNull(extra);
            readers =
            [
                .. extra,
                AppleSingleReader.AppleSingle,
                AppleSingleReader.AppleDouble,
                MacBinaryReader.III,
                MacBinaryReader.II,
                MacBinaryReader.I,
                BinHexReader.Instance,
            ];
        }

        /// <summary>The readers, in the order they are tried.</summary>
        public IReadOnlyList<IContainerReader> Readers => readers;

        /// <summary>Unwraps <paramref name="file"/>, which was found as <paramref name="format"/>.</summary>
        public ContainerNode Unwrap(MacFile file, string format, ContainerContext context)
        {
            ArgumentNullException.ThrowIfNull(file);
            ArgumentNullException.ThrowIfNull(context);
            long expanded = 0;
            return Unwrap(file, format, context, 0, ref expanded);
        }

        /// <summary>Reads a host file with its companions and unwraps it.</summary>
        public ContainerNode Unwrap(string path, ContainerReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
        {
            var context = new ContainerContext(options, diagnostics);
            var host = HostFiles.Read(path, context.Options, context.Diagnostics);
            return Unwrap(host.File, HostFiles.FormatName(host.Layout), context);
        }

        private ContainerNode Unwrap(MacFile file, string format, ContainerContext context, int depth, ref long expanded)
        {
            if (file.DataFork.Length == 0) return new ContainerNode(format, file, []);
            var reader = readers.FirstOrDefault(r => r.CanRead(file.DataFork));
            if (reader is null) return new ContainerNode(format, file, []);
            if (depth >= context.Options.MaxNestingDepth)
            {
                context.Report(DiagnosticSeverity.Warning, "container.too-deep",
                    $"\"{file.Name}\" is a {reader.FormatName} file nested {depth + 1} deep, past the " +
                    $"{context.Options.MaxNestingDepth}-level limit; not unwrapped.");
                return new ContainerNode(format, file, []);
            }

            IReadOnlyList<MacFile> contents;
            try
            {
                contents = reader.Read(file.DataFork, context.WithHostName(file.Name));
            }
            catch (InvalidDataException e)
            {
                context.Report(DiagnosticSeverity.Error, "container.unreadable",
                    $"\"{file.Name}\" looks like {reader.FormatName} but cannot be read: {e.Message}");
                return new ContainerNode(format, file, []);
            }

            var children = new List<ContainerNode>(contents.Count);
            foreach (var inner in contents)
            {
                expanded += inner.DataFork.Length + inner.ResourceFork.Length;
                if (expanded > context.Options.MaxExpandedBytesPerInput)
                {
                    context.Report(DiagnosticSeverity.Error, "container.too-large",
                        $"Unwrapping produced more than {context.Options.MaxExpandedBytesPerInput} bytes; stopped.");
                    break;
                }
                children.Add(Unwrap(inner, reader.FormatName, context, depth + 1, ref expanded));
            }
            return new ContainerNode(format, file, children);
        }
    }
}
