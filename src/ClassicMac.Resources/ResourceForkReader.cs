using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using static ClassicMac.Resources.ResourceForkFormat;

namespace ClassicMac.Resources
{
    // Parses a resource fork held in memory. Damage goes to the fork's diagnostics; only a fork whose header or map
    // cannot be located at all throws.
    internal static class ResourceForkReader
    {
        public static ResourceFork Read(ReadOnlyMemory<byte> input, ReadOptions options)
        {
            var fork = new ResourceFork();
            if (input.IsEmpty) return fork; // a file without a resource fork
            var bytes = input.Span;
            if (bytes.Length < HeaderLength)
                throw new InvalidDataException($"A resource fork needs a {HeaderLength}-byte header; this is {bytes.Length}.");

            long dataOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            long mapOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
            long dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
            long mapLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);

            if (mapOffset + MapHeaderLength > bytes.Length)
                throw new InvalidDataException($"The resource map at {mapOffset} lies outside the {bytes.Length}-byte fork.");

            var mapEnd = mapOffset + mapLength;
            if (mapLength < MapHeaderLength || mapEnd > bytes.Length)
            {
                fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "fork.map-length",
                    $"The map length {mapLength} does not fit the fork; using the rest of the fork.", 12));
                mapEnd = bytes.Length;
            }

            var dataEnd = dataOffset + dataLength;
            if (dataEnd > bytes.Length)
            {
                fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "fork.data-length",
                    $"The data area ({dataLength} bytes at {dataOffset}) runs past the end of the fork.", 8));
                dataEnd = bytes.Length;
            }

            if (bytes.Length >= ReservedEnd)
            {
                fork.SystemData = input[HeaderLength..(HeaderLength + ResourceFork.SystemDataLength)].ToArray();
                fork.ApplicationData = input[(HeaderLength + ResourceFork.SystemDataLength)..ReservedEnd].ToArray();
            }

            var map = (int)mapOffset;
            var headerCopy = bytes.Slice(map, HeaderLength);
            if (!headerCopy.SequenceEqual(bytes[..HeaderLength]) && headerCopy.IndexOfAnyExcept((byte)0) >= 0)
            {
                fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "fork.header-mismatch",
                    "The map's copy of the fork header differs from the header.", mapOffset));
            }

            fork.Attributes = (ResourceForkAttributes)BinaryPrimitives.ReadUInt16BigEndian(bytes[(map + MapAttributesOffset)..]);
            var typeList = mapOffset + BinaryPrimitives.ReadUInt16BigEndian(bytes[(map + MapTypeListOffsetOffset)..]);
            var nameList = mapOffset + BinaryPrimitives.ReadUInt16BigEndian(bytes[(map + MapNameListOffsetOffset)..]);
            if (typeList + TypeCountLength > mapEnd)
                throw new InvalidDataException($"The type list at {typeList} lies outside the resource map.");

            var context = new Context(input, fork, options, dataOffset, dataEnd, mapEnd, nameList);
            var typeCount = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(bytes[(int)typeList..]) + 1);
            var seenTypes = new HashSet<FourCC>();
            for (var i = 0; i < typeCount; i++)
            {
                var entry = typeList + TypeCountLength + (long)i * TypeEntryLength;
                if (entry + TypeEntryLength > mapEnd)
                {
                    fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "fork.type-list-truncated",
                        $"The type list promises {typeCount} types but the map ends after {i}.", entry));
                    break;
                }
                var e = bytes.Slice((int)entry, TypeEntryLength);
                var type = new FourCC(e[..4]);
                var count = BinaryPrimitives.ReadUInt16BigEndian(e[4..]) + 1;
                var references = typeList + BinaryPrimitives.ReadUInt16BigEndian(e[6..]);
                if (!seenTypes.Add(type))
                {
                    fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "fork.duplicate-type",
                        $"Type '{type}' appears more than once in the type list; its lists are merged.", entry));
                }
                ReadReferences(context, type, count, references);
            }

            ReportOverlaps(context);
            return fork;
        }

        private static void ReadReferences(Context context, FourCC type, int count, long references)
        {
            var bytes = context.Input.Span;
            for (var i = 0; i < count; i++)
            {
                var entry = references + (long)i * ReferenceEntryLength;
                if (entry + ReferenceEntryLength > context.MapEnd)
                {
                    context.Report(DiagnosticSeverity.Error, "fork.ref-list-out-of-range",
                        $"The reference list of '{type}' promises {count} resources but the map ends after {i}.", entry);
                    return;
                }
                var e = bytes.Slice((int)entry, ReferenceEntryLength);
                var id = BinaryPrimitives.ReadInt16BigEndian(e);
                var nameOffset = BinaryPrimitives.ReadUInt16BigEndian(e[2..]);
                var attributes = (ResourceAttributes)e[4];
                var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(e[4..]) & MaxDataOffset;
                var label = $"'{type}' {id}";

                if (context.Fork.Find(type, id) is not null)
                {
                    context.Report(DiagnosticSeverity.Warning, "resource.duplicate",
                        $"{label} appears again; the first is kept, as GetResource would return it.", entry);
                    continue;
                }

                var data = ReadData(context, label, dataOffset, entry);
                if (data is null) continue;

                var resource = new Resource(type, id, data.Value) { Attributes = attributes };
                if (nameOffset != NoName) resource.Name = ReadName(context, label, nameOffset, entry);
                context.Fork.Add(resource);
            }
        }

        private static ReadOnlyMemory<byte>? ReadData(Context context, string label, long offset, long entry)
        {
            var start = context.DataOffset + offset;
            if (start + 4 > context.DataEnd)
            {
                context.Report(DiagnosticSeverity.Error, "resource.data-out-of-range",
                    $"The data of {label} at {start} lies outside the data area; the resource is skipped.", entry);
                return null;
            }
            long length = BinaryPrimitives.ReadUInt32BigEndian(context.Input.Span[(int)start..]);
            if (length > context.Options.MaxResourceSize)
            {
                context.Report(DiagnosticSeverity.Error, "resource.too-large",
                    $"{label} claims {length} bytes, over the {context.Options.MaxResourceSize}-byte limit; skipped.", start);
                return null;
            }
            var available = context.DataEnd - (start + 4);
            if (length > available)
            {
                context.Report(DiagnosticSeverity.Error, "resource.data-truncated",
                    $"{label} claims {length} bytes but only {available} remain; the rest is missing.", start);
                length = available;
            }
            context.Blocks.Add((start, start + 4 + length, label));
            return context.Input.Slice((int)start + 4, (int)length);
        }

        private static MacString? ReadName(Context context, string label, ushort offset, long entry)
        {
            var start = context.NameList + offset;
            if (start < context.MapEnd)
            {
                var length = context.Input.Span[(int)start];
                if (start + 1 + length <= context.MapEnd)
                    return new MacString(context.Input.Span.Slice((int)start + 1, length));
            }
            context.Report(DiagnosticSeverity.Warning, "resource.name-out-of-range",
                $"The name of {label} at {start} lies outside the resource map; the resource is kept unnamed.", entry);
            return null;
        }

        // Overlapping or shared data blocks read fine but are unusual, and a writer will separate them.
        private static void ReportOverlaps(Context context)
        {
            context.Blocks.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (var i = 1; i < context.Blocks.Count; i++)
            {
                var previous = context.Blocks[i - 1];
                var current = context.Blocks[i];
                if (current.Start < previous.End)
                {
                    context.Report(DiagnosticSeverity.Info, "resource.data-overlap",
                        $"The data of {current.Label} overlaps that of {previous.Label}.", current.Start);
                }
            }
        }

        private sealed class Context(
            ReadOnlyMemory<byte> input, ResourceFork fork, ReadOptions options,
            long dataOffset, long dataEnd, long mapEnd, long nameList)
        {
            public ReadOnlyMemory<byte> Input { get; } = input;
            public ResourceFork Fork { get; } = fork;
            public ReadOptions Options { get; } = options;
            public long DataOffset { get; } = dataOffset;
            public long DataEnd { get; } = dataEnd;
            public long MapEnd { get; } = mapEnd;
            public long NameList { get; } = nameList;
            public List<(long Start, long End, string Label)> Blocks { get; } = [];

            public void Report(DiagnosticSeverity severity, string code, string message, long offset) =>
                Fork.Diagnostics.Add(new Diagnostic(severity, code, message, offset));
        }
    }
}
