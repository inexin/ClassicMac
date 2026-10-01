using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;
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
            // Mac OS 9 and the 68k ROM refuse to open a fork whose data or map ends past $FFFFFE (mapReadErr), and the
            // Resource Manager refuses to grow one that far (eofErr): so no fork ends past it (disassembly of CheckMap
            // and CheckGrow).
            if (total > MaxForkEnd)
                throw new InvalidOperationException($"The fork would be {total} bytes; the Resource Manager opens none that end past $FFFFFE.");

            // Written in file order; every offset above is already known.
            var writer = new BigEndianWriter((int)total);
            WriteHeader(writer, dataOffset, mapOffset, dataLength, mapLength);
            writer.WriteBytes(fork.SystemData.Span);
            writer.WriteBytes(fork.ApplicationData.Span);

            foreach (var (resource, _) in items)
            {
                writer.WriteUInt32(resource.Length);
                writer.WriteBytes(resource.GetData().Span);
            }

            WriteHeader(writer, dataOffset, mapOffset, dataLength, mapLength);
            writer.WriteBytes(fork.MapReservedData.Span);
            writer.WriteByte((byte)(fork.Attributes & ~NotOnDisk));
            writer.WriteByte((byte)fork.MapFlags);
            writer.WriteUInt16(MapHeaderLength);
            writer.WriteUInt16(nameListOffset);

            writer.WriteUInt16((ushort)(groups.Count - 1));
            var referenceOffset = typeListLength; // from the start of the type list
            foreach (var (type, resources) in groups)
            {
                if (referenceOffset > ushort.MaxValue)
                    throw new InvalidOperationException("The reference lists exceed the 64 KiB a type list can address.");
                writer.WriteFourCC(type);
                writer.WriteUInt16(resources.Count - 1);
                writer.WriteUInt16(referenceOffset);
                referenceOffset += resources.Count * ReferenceEntryLength;
            }
            foreach (var (_, resources) in groups)
                foreach (var resource in resources)
                {
                    writer.WriteInt16(resource.Id);
                    writer.WriteUInt16(nameOffsets.TryGetValue(resource, out var nameAt) ? nameAt : NoName);
                    writer.WriteUInt32((uint)dataOffsets[resource] | (uint)(resource.Attributes & ~ResourceAttributes.Changed) << 24);
                    writer.WriteUInt32(resource.StoredHandle);
                }

            foreach (var resource in named)
            {
                var name = resource.Name!.Value;
                writer.WriteByte(name.Length);
                writer.WriteBytes(name.Bytes);
            }
            return writer.ToArray();
        }

        public static void Write(ResourceFork fork, Stream output) => output.Write(Write(fork));

        private static void WriteHeader(BigEndianWriter writer, long dataOffset, long mapOffset, long dataLength, long mapLength)
        {
            writer.WriteUInt32(dataOffset);
            writer.WriteUInt32(mapOffset);
            writer.WriteUInt32(dataLength);
            writer.WriteUInt32(mapLength);
        }
    }
}
