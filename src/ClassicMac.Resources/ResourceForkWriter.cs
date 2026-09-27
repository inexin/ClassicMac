using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static ClassicMac.Resources.ResourceForkFormat;

namespace ClassicMac.Resources
{
    // Writes a fork the way the Resource Manager leaves one after UpdateResFile compacts it (Mac OS 9 native and ROM
    // $077D disassembly; the model matches 32 of 33 forks written in SheepShaver byte for byte):
    // - header; the 240 reserved bytes (kept from the model; zero for a new fork);
    // - data items (length + bytes) packed with no padding, in ascending order of where they were — data added or grown
    //   goes after the old data, in the order it happened; data two references shared stays shared;
    // - the map: header copy, the kept next-map handle and file reference, attributes without compact and changed
    //   ($60), the kept in-memory flags, type list at 28, types in the order first added, each type's references in the
    //   order added, then the names in the order they were set (a renamed resource's name moves to the end).
    // Real files carry memory in the map's handle fields; they are kept from the model (zero for new data).
    internal static class ResourceForkWriter
    {
        private const ResourceForkAttributes NotOnDisk = ResourceForkAttributes.Compact | ResourceForkAttributes.Changed;

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

            // Data area: packed in placement order; unmodified data that shared a place still shares one item.
            var dataOffsets = new Dictionary<Resource, int>();
            var shared = new Dictionary<long, int>();
            var items = new List<(Resource Resource, int Offset)>();
            long dataLength = 0;
            foreach (var resource in fork.Resources.OrderBy(r => r.DataPlacement))
            {
                if (!resource.DataModified && shared.TryGetValue(resource.DataPlacement, out var existing))
                {
                    dataOffsets[resource] = existing;
                    continue;
                }
                if (dataLength > MaxDataOffset)
                    throw new InvalidOperationException("The resource data exceeds the 16 MiB a fork can address.");
                dataOffsets[resource] = (int)dataLength;
                if (!resource.DataModified) shared[resource.DataPlacement] = (int)dataLength;
                items.Add((resource, (int)dataLength));
                dataLength += 4 + resource.Length;
            }

            // Name list: in the order the names were placed.
            var nameOffsets = new Dictionary<Resource, int>();
            var named = fork.Resources.Where(r => r.Name is not null).OrderBy(r => r.NamePlacement).ToList();
            var namesLength = 0;
            foreach (var resource in named)
            {
                if (namesLength >= NoName)
                    throw new InvalidOperationException("The names exceed the 64 KiB a name list can address.");
                nameOffsets[resource] = namesLength;
                namesLength += 1 + resource.Name!.Value.Length;
            }

            // Map: header, type list, reference lists, names.
            var typeListLength = TypeCountLength + groups.Count * TypeEntryLength;
            var referencesLength = fork.Resources.Count * ReferenceEntryLength;
            var nameListOffset = MapHeaderLength + typeListLength + referencesLength;
            if (nameListOffset > ushort.MaxValue)
                throw new InvalidOperationException("The type and reference lists exceed the 64 KiB a map can address.");
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

            foreach (var (resource, offset) in items)
            {
                var at = dataOffset + offset;
                BinaryPrimitives.WriteUInt32BigEndian(span[at..], (uint)resource.Length);
                resource.GetData().Span.CopyTo(span[(at + 4)..]);
            }

            var map = span[(int)mapOffset..];
            WriteHeader(map, dataOffset, mapOffset, dataLength, mapLength);
            fork.MapReservedData.Span.CopyTo(map[HeaderLength..]);
            map[MapAttributesOffset] = (byte)(fork.Attributes & ~NotOnDisk);
            map[MapFlagsOffset] = (byte)fork.MapFlags;
            BinaryPrimitives.WriteUInt16BigEndian(map[MapTypeListOffsetOffset..], MapHeaderLength);
            BinaryPrimitives.WriteUInt16BigEndian(map[MapNameListOffsetOffset..], (ushort)nameListOffset);

            foreach (var resource in named)
            {
                var at = nameListOffset + nameOffsets[resource];
                var name = resource.Name!.Value;
                map[at] = (byte)name.Length;
                name.Bytes.CopyTo(map[(at + 1)..]);
            }

            var typeList = map[MapHeaderLength..];
            BinaryPrimitives.WriteUInt16BigEndian(typeList, (ushort)(groups.Count - 1));
            var referenceOffset = typeListLength; // from the start of the type list
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
                    BinaryPrimitives.WriteUInt16BigEndian(reference[2..],
                        nameOffsets.TryGetValue(resource, out var nameAt) ? (ushort)nameAt : NoName);
                    var packed = (uint)dataOffsets[resource] | (uint)(resource.Attributes & ~ResourceAttributes.Changed) << 24;
                    BinaryPrimitives.WriteUInt32BigEndian(reference[4..], packed);
                    BinaryPrimitives.WriteUInt32BigEndian(reference[8..], resource.StoredHandle);
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
