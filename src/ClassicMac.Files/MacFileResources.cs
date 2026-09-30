using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources;

namespace ClassicMac.Files
{
    /// <summary>Where a file's resources were found.</summary>
    public enum ResourceForkSource
    {
        /// <summary>The file has no resources.</summary>
        None,

        /// <summary>Its resource fork.</summary>
        ResourceFork,

        /// <summary>
        /// Its data fork, which holds a resource fork: some applications keep resources in data files (Realmz's
        /// <c>.rsf</c>), and a fork copied out on its own (<c>.rsrc</c>) arrives as a plain file.
        /// </summary>
        DataFork,
    }

    /// <summary>A file's resources and where they came from.</summary>
    /// <param name="Fork">The resource fork, or null when there is none (or it cannot be read).</param>
    /// <param name="Source">Where it was found.</param>
    public sealed record FileResources(ResourceFork? Fork, ResourceForkSource Source);

    /// <summary>Finds the resources of a Mac file, as the CLI and the app show them.</summary>
    public static class MacFileResources
    {
        /// <summary>
        /// The resources of <paramref name="file"/>: its resource fork, or — when it has none — its data fork if that is
        /// a clean resource fork. A resource fork that cannot be read is reported and gives no resources. The fork's own
        /// diagnostics go to <paramref name="diagnostics"/>.
        /// </summary>
        public static FileResources Read(MacFile file, ReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(file);
            options ??= ReadOptions.Default;
            if (file.ResourceFork.Length == 0)
            {
                return TryDataForkAsFork(file.DataFork, options) is { } inData
                    ? Found(inData, ResourceForkSource.DataFork, diagnostics)
                    : new FileResources(null, ResourceForkSource.None);
            }
            try
            {
                return Found(ReadFork(file.ResourceFork, options), ResourceForkSource.ResourceFork, diagnostics);
            }
            catch (InvalidDataException e)
            {
                diagnostics?.Add(new Diagnostic(DiagnosticSeverity.Error, "fork.unreadable", e.Message));
                return new FileResources(null, ResourceForkSource.None);
            }
        }

        /// <summary>
        /// A plain file read as a resource fork on its own (a <c>.rsrc</c> file). Throws <see cref="InvalidDataException"/>
        /// when its header does not describe a fork — an application's own data file, which the Resource Manager would
        /// refuse too — or the fork cannot be read.
        /// </summary>
        public static FileResources ReadRaw(ForkData data, ReadOptions? options = null, ICollection<Diagnostic>? diagnostics = null)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (!LooksLikeFork(data))
                throw new InvalidDataException("its first 16 bytes do not describe a resource fork's data and map.");
            return Found(ReadFork(data, options ?? ReadOptions.Default), ResourceForkSource.DataFork, diagnostics);
        }

        /// <summary>
        /// A cheap look at a fork's header, so data files are not all read in full: the data and map areas lie inside
        /// the fork, after the 16-byte header, and the map is at least its fixed part long.
        /// </summary>
        public static bool LooksLikeFork(ForkData data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length is < 256 or > ResourceFork.MaxForkLength) return false;
            var header = data.ReadPrefix(16);
            var headerReader = new ClassicMac.Core.BigEndianReader(header);
            long dataOffset = headerReader.ReadUInt32At(0);
            long mapOffset = headerReader.ReadUInt32At(4);
            long dataLength = headerReader.ReadUInt32At(8);
            long mapLength = headerReader.ReadUInt32At(12);
            return dataOffset >= 16 && mapOffset >= 16 && mapLength >= 30 && dataOffset + dataLength <= data.Length
                && mapOffset + mapLength <= data.Length;
        }

        private static FileResources Found(ResourceFork fork, ResourceForkSource source, ICollection<Diagnostic>? diagnostics)
        {
            if (diagnostics is not null)
            {
                foreach (var d in fork.Diagnostics) diagnostics.Add(d);
            }
            return new FileResources(fork, source);
        }

        private static ResourceFork ReadFork(ForkData fork, ReadOptions options)
        {
            using var stream = fork.Open();
            return ResourceFork.Read(stream, options);
        }

        // A data fork that reads as a resource fork with at least one resource and no errors, or null.
        private static ResourceFork? TryDataForkAsFork(ForkData data, ReadOptions options)
        {
            if (!LooksLikeFork(data)) return null;
            try
            {
                var fork = ReadFork(data, options);
                return fork.Resources.Count > 0 && !fork.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? fork : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
    }
}
