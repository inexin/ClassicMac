using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;
using static ClassicMac.Resources.ResourceForkFormat;

namespace ClassicMac.Resources;

// Parses a resource fork held in memory. Damage goes to the fork's diagnostics; only a fork whose header or map
// cannot be located at all throws.
internal static class ResourceForkReader
{
    public static ResourceFork Read(ReadOnlyMemory<byte> input, ReadOptions options)
    {
        var fork = new ResourceFork();
        if (input.IsEmpty)
        {
            return fork; // a file without a resource fork
        }

        var bytes = input;
        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException($"A resource fork needs a {HeaderLength}-byte header; this is {bytes.Length}.");
        }

        // Whether the Mac would open the fork; said in the diagnostics, and in the exception when we cannot read it.
        var verdict = "";
        if (ResourceForkChecks.Rejects(bytes, options.ResourceManager) is var (error, reason))
        {
            var who = options.ResourceManager == ResourceManagerModel.Rom68k ? "The 68k ROM" : "Mac OS 9";
            var name = error switch { -39 => "eofErr", -40 => "posErr", -50 => "paramErr", _ => "mapReadErr" };
            verdict = error == 0
                ? $"{who} would open this fork but read memory past its map: {reason}."
                : $"{who} would not open this fork ({name} {error}): {reason}.";
            fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning,
                error == 0 ? "fork.mac-misreads" : "fork.mac-rejects", $"{verdict} Read anyway."));
        }

        var reader = new BigEndianReader(bytes);
        long dataOffset = reader.ReadUInt32();
        long mapOffset = reader.ReadUInt32();
        long dataLength = reader.ReadUInt32();
        long mapLength = reader.ReadUInt32();

        // ResEdit's recovery [ClassicMac, from ResEdit's behaviour]: a map offset past the end is replaced by the end of the data area
        // when a map with a sane type-list offset sits there.
        var guess = dataOffset + dataLength;
        if (mapOffset + MapHeaderLength > bytes.Length && guess + MapHeaderLength <= bytes.Length
            && reader.ReadUInt16At((int)guess + MapTypeListOffsetOffset) is var guessTypes
            && guessTypes >= MapHeaderLength && guess + guessTypes + TypeCountLength <= bytes.Length)
        {
            fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "fork.map-recovered",
                $"The map offset {mapOffset} lies outside the {bytes.Length}-byte fork; using the map found right after the data area, at {guess}.", 4));
            mapOffset = guess;
            mapLength = bytes.Length - guess;
        }

        if (mapOffset + MapHeaderLength > bytes.Length)
        {
            throw new InvalidDataException($"The resource map at {mapOffset} lies outside the {bytes.Length}-byte fork. {verdict}".TrimEnd());
        }

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
        if (!headerCopy.Span.SequenceEqual(bytes.Span[..HeaderLength]) && headerCopy.Span.IndexOfAnyExcept((byte)0) >= 0)
        {
            fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "fork.header-mismatch",
                "The map's copy of the fork header differs from the header.", mapOffset));
        }

        fork.MapReservedData = input.Slice(map + HeaderLength, ResourceFork.MapReservedDataLength).ToArray();
        fork.Attributes = (ResourceForkAttributes)bytes.Span[map + MapAttributesOffset];
        fork.MapFlags = (ResourceMapFlags)bytes.Span[map + MapFlagsOffset];
        var typeList = mapOffset + reader.ReadUInt16At(map + MapTypeListOffsetOffset);
        var nameList = mapOffset + reader.ReadUInt16At(map + MapNameListOffsetOffset);
        if (typeList + TypeCountLength > mapEnd)
        {
            throw new InvalidDataException($"The type list at {typeList} lies outside the resource map. {verdict}".TrimEnd());
        }

        var context = new Context(input, fork, options, dataOffset, dataEnd, mapEnd, nameList);
        var typeCount = (ushort)(reader.ReadUInt16At((int)typeList) + 1);
        var seenTypes = new HashSet<FourCC>();
        // The Resource Manager assumes the reference lists follow the type list contiguously, in type order.
        long expectedReferences = TypeCountLength + (long)typeCount * TypeEntryLength;
        var outOfOrderReported = false;
        for (var i = 0; i < typeCount; i++)
        {
            var entry = typeList + TypeCountLength + (long)i * TypeEntryLength;
            if (entry + TypeEntryLength > mapEnd)
            {
                fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, "fork.type-list-truncated",
                    $"The type list promises {typeCount} types but the map ends after {i}.", entry));
                break;
            }
            reader.Position = (int)entry;
            var type = reader.ReadFourCC();
            var count = reader.ReadUInt16() + 1;
            var references = typeList + reader.ReadUInt16();
            if (!seenTypes.Add(type))
            {
                fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "fork.duplicate-type",
                    $"Type '{type}' appears more than once in the type list; its lists are merged. On the Mac, " +
                    "counting and indexing see only the first list; Mac OS 9's GetResource searches all, the ROM's " +
                    "only the first.", entry));
            }
            if (count - 1 >= 0x7FFF && options.ResourceManager == ResourceManagerModel.MacOS9)
            {
                fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "fork.mac-hangs",
                    $"Type '{type}' claims {count} resources; Mac OS 9 opens such a fork and then loops forever " +
                    "preloading it.", entry));
            }
            if (references - typeList != expectedReferences && !outOfOrderReported)
            {
                outOfOrderReported = true;
                fork.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "fork.ref-lists-out-of-order",
                    $"The reference list of '{type}' is not where the previous lists end; the Mac assumes they are " +
                    "contiguous in type order and would report wrong IDs and names for later types.", entry));
            }
            expectedReferences = references - typeList + (long)count * ReferenceEntryLength;
            ReadReferences(context, type, count, references);
        }

        ReportOverlaps(context);
        return fork;
    }

    private static void ReadReferences(Context context, FourCC type, int count, long references)
    {
        var reader = new BigEndianReader(context.Input);
        for (var i = 0; i < count; i++)
        {
            var entry = references + (long)i * ReferenceEntryLength;
            if (entry + ReferenceEntryLength > context.MapEnd)
            {
                context.Report(DiagnosticSeverity.Error, "fork.ref-list-out-of-range",
                    $"The reference list of '{type}' promises {count} resources but the map ends after {i}.", entry);
                return;
            }
            reader.Position = (int)entry;
            var id = reader.ReadInt16();
            var nameOffset = reader.ReadUInt16();
            var attributesAndData = reader.ReadUInt32();
            var attributes = (ResourceAttributes)(attributesAndData >> 24);
            var dataOffset = attributesAndData & MaxDataOffset;
            var storedHandle = reader.ReadUInt32();
            var label = $"'{type}' {id}";

            if (context.Fork.Find(type, id) is not null)
            {
                context.Report(DiagnosticSeverity.Warning, "resource.duplicate",
                    $"{label} appears again; the first is kept, as GetResource would return it.", entry);
                continue;
            }

            var data = ReadData(context, label, dataOffset, entry);
            if (data is null)
            {
                continue;
            }

            var resource = new Resource(type, id, data.Value) { Attributes = attributes };
            if (nameOffset != NoName)
            {
                resource.Name = ReadName(context, label, nameOffset, entry);
            }
            // Where the data and name sat, and the handle field, so the writer can lay the fork out as the
            // Resource Manager's compaction would and keep what real files carry.
            resource.DataPlacement = dataOffset;
            resource.NamePlacement = nameOffset;
            resource.DataModified = false;
            resource.StoredHandle = storedHandle;
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
        long length = context.Reader.ReadUInt32At((int)start);
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
                $"{label} claims {length} bytes but only {available} remain; the rest is missing (the Mac " +
                "cannot load it at all: eofErr).", start);
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
            {
                return new MacString(context.Input.Span.Slice((int)start + 1, length));
            }
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

    private const int MaxErrors = 1000;

    private sealed class Context(
        ReadOnlyMemory<byte> input, ResourceFork fork, ReadOptions options,
        long dataOffset, long dataEnd, long mapEnd, long nameList)
    {
        public ReadOnlyMemory<byte> Input { get; } = input;

        // The fork's one reader, for the data lengths read at each resource's offset.
        public BigEndianReader Reader { get; } = new(input);

        public ResourceFork Fork { get; } = fork;
        public ReadOptions Options { get; } = options;
        public long DataOffset { get; } = dataOffset;
        public long DataEnd { get; } = dataEnd;
        public long MapEnd { get; } = mapEnd;
        public long NameList { get; } = nameList;
        public List<(long Start, long End, string Label)> Blocks { get; } = [];

        private int errors;

        // A map that is not one (another format's bytes read as a fork) gives an error per entry, and can promise
        // 65536 types of thousands of entries each: past MaxErrors the fork is refused instead of read.
        public void Report(DiagnosticSeverity severity, string code, string message, long offset)
        {
            if (severity == DiagnosticSeverity.Error && ++errors > MaxErrors)
            {
                throw new InvalidDataException($"The fork is too damaged to read: over {MaxErrors} errors in its map.");
            }

            Fork.Diagnostics.Add(new Diagnostic(severity, code, message, offset));
        }
    }
}
