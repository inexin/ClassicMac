using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources
{
    /// <summary>
    /// A resource fork in memory: its resources in the order they were read or added, the map attributes, and the
    /// reserved header areas, kept so a read → write round trip can be exact. Not thread-safe.
    /// </summary>
    public sealed class ResourceFork
    {
        /// <summary>Size of the area after the fork header reserved for the system.</summary>
        public const int SystemDataLength = 112;

        /// <summary>Size of the area after the system area reserved for the application.</summary>
        public const int ApplicationDataLength = 128;

        /// <summary>Size of the map's next-map handle and file reference number.</summary>
        public const int MapReservedDataLength = 6;

        private readonly List<Resource> resources = [];
        private readonly Dictionary<(FourCC Type, short Id), Resource> index = [];
        private ReadOnlyMemory<byte> systemData = new byte[SystemDataLength];
        private ReadOnlyMemory<byte> applicationData = new byte[ApplicationDataLength];
        private ReadOnlyMemory<byte> mapReservedData = new byte[MapReservedDataLength];

        /// <summary>The resources, in the order they were read or added.</summary>
        public IReadOnlyList<Resource> Resources => resources;

        /// <summary>The distinct types, in the order they first appear.</summary>
        public IReadOnlyList<FourCC> Types => resources.Select(r => r.Type).Distinct().ToList();

        /// <summary>The resource map's attributes.</summary>
        public ResourceForkAttributes Attributes { get; set; }

        /// <summary>The map's in-memory flags byte (<c>mInMemoryAttr</c>), kept as read.</summary>
        public ResourceMapFlags MapFlags { get; set; }

        /// <summary>The 112 bytes after the fork header reserved for the system, kept as read.</summary>
        public ReadOnlyMemory<byte> SystemData
        {
            get => systemData;
            set => systemData = value.Length == SystemDataLength
                ? value
                : throw new ArgumentException($"The system area is {SystemDataLength} bytes.", nameof(value));
        }

        /// <summary>The 128 bytes after the system area reserved for the application, kept as read.</summary>
        public ReadOnlyMemory<byte> ApplicationData
        {
            get => applicationData;
            set => applicationData = value.Length == ApplicationDataLength
                ? value
                : throw new ArgumentException($"The application area is {ApplicationDataLength} bytes.", nameof(value));
        }

        /// <summary>
        /// The map's next-map handle (4 bytes) and file reference number (2), kept as read. They are runtime values the
        /// Resource Manager leaves in the file; keeping them lets such forks round-trip byte for byte.
        /// </summary>
        public ReadOnlyMemory<byte> MapReservedData
        {
            get => mapReservedData;
            set => mapReservedData = value.Length == MapReservedDataLength
                ? value
                : throw new ArgumentException($"The map's reserved fields are {MapReservedDataLength} bytes.", nameof(value));
        }

        /// <summary>Problems found while reading this fork.</summary>
        public List<Diagnostic> Diagnostics { get; } = [];

        /// <summary>
        /// The most bytes <see cref="Read(Stream, ReadOptions?)"/> accepts: 16 MiB of data (24-bit offsets) plus a map,
        /// whose lists are addressed by 16-bit offsets.
        /// </summary>
        public const int MaxForkLength = 32 * 1024 * 1024;

        /// <summary>
        /// Reads a fork from the stream's current position to its end. An empty stream gives an empty fork. Damage goes
        /// to <see cref="Diagnostics"/>; throws <see cref="InvalidDataException"/> only when the header or map cannot
        /// be found.
        /// </summary>
        public static ResourceFork Read(Stream input, ReadOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (input.CanSeek && input.Length - input.Position > MaxForkLength)
                throw new InvalidDataException($"A resource fork is at most {MaxForkLength} bytes.");
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = input.Read(chunk)) > 0)
            {
                if (buffer.Length + read > MaxForkLength)
                    throw new InvalidDataException($"A resource fork is at most {MaxForkLength} bytes.");
                buffer.Write(chunk, 0, read);
            }
            return Read(buffer.ToArray(), options);
        }

        /// <summary>
        /// Reads a fork held in memory; the resources' data are slices of <paramref name="input"/>, which must not
        /// change afterwards.
        /// </summary>
        public static ResourceFork Read(ReadOnlyMemory<byte> input, ReadOptions? options = null) =>
            ResourceForkReader.Read(input, options ?? ReadOptions.Default);

        /// <summary>
        /// Writes the fork in a compact canonical layout. The in-memory <see cref="ResourceAttributes.Changed"/> bits are
        /// cleared; throws <see cref="InvalidOperationException"/> when the fork exceeds the format's limits.
        /// </summary>
        public void Write(Stream output)
        {
            ArgumentNullException.ThrowIfNull(output);
            ResourceForkWriter.Write(this, output);
        }

        /// <summary>The fork written as by <see cref="Write"/>.</summary>
        public byte[] ToArray() => ResourceForkWriter.Write(this);

        /// <summary>The resource with this type and ID, or <see langword="null"/>.</summary>
        public Resource? Find(FourCC type, short id) => index.GetValueOrDefault((type, id));

        /// <summary>The resources of one type, in order.</summary>
        public IEnumerable<Resource> OfType(FourCC type) => resources.Where(r => r.Type == type);

        /// <summary>Adds a resource; its type and ID must be new to this fork and it must not belong to another.</summary>
        public void Add(Resource resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (resource.Owner is not null)
                throw new InvalidOperationException($"{resource} already belongs to a resource fork.");
            if (!index.TryAdd((resource.Type, resource.Id), resource))
                throw new InvalidOperationException($"The fork already has a resource '{resource.Type}' {resource.Id}.");
            resources.Add(resource);
            resource.Owner = this;
        }

        /// <summary>Adds a resource at a position in <see cref="Resources"/> (as <see cref="Add"/> otherwise), to put back one removed.</summary>
        public void Insert(int position, Resource resource)
        {
            Add(resource);
            resources.RemoveAt(resources.Count - 1);
            resources.Insert(Math.Clamp(position, 0, resources.Count), resource);
        }

        /// <summary>Removes a resource; returns whether it was in this fork.</summary>
        public bool Remove(Resource resource)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (resource.Owner != this) return false;
            index.Remove((resource.Type, resource.Id));
            resources.Remove(resource);
            resource.Owner = null;
            return true;
        }

        /// <summary>Gives a resource of this fork a new ID, which must be free for its type.</summary>
        public void Renumber(Resource resource, short newId)
        {
            ArgumentNullException.ThrowIfNull(resource);
            if (resource.Owner != this) throw new InvalidOperationException($"{resource} is not in this fork.");
            if (newId == resource.Id) return;
            if (!index.TryAdd((resource.Type, newId), resource))
                throw new InvalidOperationException($"The fork already has a resource '{resource.Type}' {newId}.");
            index.Remove((resource.Type, resource.Id));
            resource.Id = newId;
        }
    }
}
