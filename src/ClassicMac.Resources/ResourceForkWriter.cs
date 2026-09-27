using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static ClassicMac.Resources.ResourceForkFormat;

namespace ClassicMac.Resources
{
    // Writes a fork in one canonical, compact layout. The layout is fitted, not taken from Apple code: header, reserved
    // areas, data (types in order, resources in order), then the map (header copy, type list at 28, reference lists in
    // type order, names in resource order). To be checked against Rez output and the Resource Manager's compaction.
    internal static class ResourceForkWriter
    {
        public static byte[] Write(ResourceFork fork)
        {
            var groups = fork.Types.Select(type => (Type: type, Resources: fork.OfType(type).ToList())).ToList();
            if (groups.Count > 0x10000)
                throw new InvalidOperationException($"A resource fork holds at most 65536 types; this has {groups.Count}.");
            foreach (var (type, resources) in groups)
            {
                if (resources.Count > 0x10000)
                    throw new InvalidOperationException($"A type holds at most 65536 resources; '{type}' has {resources.Count}.");
            }

            // Data area: each resource's length and bytes, remembering where each starts.
            var dataOffsets = new Dictionary<Resource, int>();
            long dataLength = 0;
            foreach (var resource in groups.SelectMany(g => g.Resources))
            {
                if (dataLength > MaxDataOffset)
                    throw new InvalidOperationException("The resource data exceeds the 16 MiB a fork can address.");
                dataOffsets[resource] = (int)dataLength;
                dataLength += 4 + resource.Length;
            }

            // Map: header, type list, reference lists, names.
            var typeListLength = TypeCountLength + groups.Count * TypeEntryLength;
            var referencesLength = fork.Resources.Count * ReferenceEntryLength;
            var nameListOffset = MapHeaderLength + typeListLength + referencesLength;
            if (nameListOffset > ushort.MaxValue)
                throw new InvalidOperationException("The type and reference lists exceed the 64 KiB a map can address.");
            var namesLength = fork.Resources.Sum(r => r.Name is { } name ? 1 + name.Length : 0);
            var mapLength = nameListOffset + namesLength;

            var dataOffset = ReservedEnd;
            var mapOffset = dataOffset + dataLength;
            var total = mapOffset + mapLength;
            if (total > int.MaxValue) throw new InvalidOperationException("The fork is too large to write.");

            var output = new byte[total];
            var span = output.AsSpan();
            WriteHeader(span, dataOffset, mapOffset, dataLength, mapLength);
            fork.SystemData.Span.CopyTo(span[HeaderLength..]);
            fork.ApplicationData.Span.CopyTo(span[(HeaderLength + ResourceFork.SystemDataLength)..]);

            foreach (var (resource, offset) in dataOffsets)
            {
                var at = dataOffset + offset;
                BinaryPrimitives.WriteUInt32BigEndian(span[at..], (uint)resource.Length);
                resource.GetData().Span.CopyTo(span[(at + 4)..]);
            }

            var map = span[(int)mapOffset..];
            WriteHeader(map, dataOffset, mapOffset, dataLength, mapLength);
            var attributes = (ushort)(fork.Attributes & ~ResourceForkAttributes.Changed);
            BinaryPrimitives.WriteUInt16BigEndian(map[MapAttributesOffset..], attributes);
            BinaryPrimitives.WriteUInt16BigEndian(map[MapTypeListOffsetOffset..], MapHeaderLength);
            BinaryPrimitives.WriteUInt16BigEndian(map[MapNameListOffsetOffset..], (ushort)nameListOffset);

            var typeList = map[MapHeaderLength..];
            BinaryPrimitives.WriteUInt16BigEndian(typeList, (ushort)(groups.Count - 1));
            var referenceOffset = typeListLength; // from the start of the type list
            var nameOffset = 0;
            for (var i = 0; i < groups.Count; i++)
            {
                var (type, resources) = groups[i];
                var entry = typeList[(TypeCountLength + i * TypeEntryLength)..];
                type.CopyTo(entry);
                BinaryPrimitives.WriteUInt16BigEndian(entry[4..], (ushort)(resources.Count - 1));
                BinaryPrimitives.WriteUInt16BigEndian(entry[6..], (ushort)referenceOffset);
                if (referenceOffset > ushort.MaxValue)
                    throw new InvalidOperationException("The reference lists exceed the 64 KiB a type list can address.");

                foreach (var resource in resources)
                {
                    var reference = typeList[referenceOffset..];
                    BinaryPrimitives.WriteInt16BigEndian(reference, resource.Id);
                    if (resource.Name is { } name)
                    {
                        if (nameOffset >= NoName)
                            throw new InvalidOperationException("The names exceed the 64 KiB a name list can address.");
                        BinaryPrimitives.WriteUInt16BigEndian(reference[2..], (ushort)nameOffset);
                        var at = nameListOffset + nameOffset;
                        map[at] = (byte)name.Length;
                        name.Bytes.CopyTo(map[(at + 1)..]);
                        nameOffset += 1 + name.Length;
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(reference[2..], NoName);
                    }
                    var packed = (uint)dataOffsets[resource] | (uint)(resource.Attributes & ~ResourceAttributes.Changed) << 24;
                    BinaryPrimitives.WriteUInt32BigEndian(reference[4..], packed);
                    referenceOffset += ReferenceEntryLength;
                }
            }
            return output;
        }

        public static void Write(ResourceFork fork, Stream output) => output.Write(Write(fork));

        private static void WriteHeader(Span<byte> at, long dataOffset, long mapOffset, long dataLength, long mapLength)
        {
            BinaryPrimitives.WriteUInt32BigEndian(at, (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(at[4..], (uint)mapOffset);
            BinaryPrimitives.WriteUInt32BigEndian(at[8..], (uint)dataLength);
            BinaryPrimitives.WriteUInt32BigEndian(at[12..], (uint)mapLength);
        }
    }
}
