using System;
using System.Collections.Generic;
using System.Linq;

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

        private readonly List<Resource> resources = [];
        private readonly Dictionary<(FourCC Type, short Id), Resource> index = [];
        private ReadOnlyMemory<byte> systemData = new byte[SystemDataLength];
        private ReadOnlyMemory<byte> applicationData = new byte[ApplicationDataLength];

        /// <summary>The resources, in the order they were read or added.</summary>
        public IReadOnlyList<Resource> Resources => resources;

        /// <summary>The distinct types, in the order they first appear.</summary>
        public IReadOnlyList<FourCC> Types => resources.Select(r => r.Type).Distinct().ToList();

        /// <summary>The resource map's attributes.</summary>
        public ResourceForkAttributes Attributes { get; set; }

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

        /// <summary>Problems found while reading this fork.</summary>
        public List<Diagnostic> Diagnostics { get; } = [];

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
