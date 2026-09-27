using System;
using System.Threading;
using ClassicMac.Core;

namespace ClassicMac.Resources
{
    /// <summary>
    /// One resource: type, ID, optional name, attributes and data. The data is as stored in the fork (still compressed
    /// when <see cref="ResourceAttributes.Compressed"/> is set); a read fork's resources share its buffer.
    /// </summary>
    public sealed class Resource
    {
        // Placement keys for data and names added or moved after reading: beyond any real offset, in the order they
        // happen, as the Resource Manager appends new data and names at the end.
        private static long nextPlacement = 1L << 40;

        private ReadOnlyMemory<byte> data;
        private MacString? name;

        /// <summary>Creates a resource holding <paramref name="data"/>.</summary>
        public Resource(FourCC type, short id, ReadOnlyMemory<byte> data)
        {
            Type = type;
            Id = id;
            this.data = data;
            DataPlacement = NextPlacement();
        }

        /// <summary>The resource type.</summary>
        public FourCC Type { get; }

        /// <summary>The resource ID; change it with <see cref="ResourceFork.Renumber"/>.</summary>
        public short Id { get; internal set; }

        /// <summary>
        /// The name, or <see langword="null"/> when the resource has none (an empty name is distinct). Giving a new name
        /// moves it to the end of the name list, as SetResInfo does.
        /// </summary>
        public MacString? Name
        {
            get => name;
            set
            {
                if (value == name) return;
                name = value;
                NamePlacement = NextPlacement();
            }
        }

        /// <summary>The attribute byte.</summary>
        public ResourceAttributes Attributes { get; set; }

        /// <summary>The length of the stored data in bytes.</summary>
        public int Length => data.Length;

        // The fork this resource belongs to, so it cannot be added to two.
        internal ResourceFork? Owner { get; set; }

        // Where the data sits in the data area (as read), or a key after all real offsets for data added or grown.
        internal long DataPlacement { get; set; }

        // Where the name sits in the name list (as read), or a key after all real offsets for names set later.
        internal long NamePlacement { get; set; }

        // False while the data is exactly as read, so resources that shared data on disk can share it again.
        internal bool DataModified { get; set; } = true;

        // The reference entry's handle field as read: memory the Resource Manager wrote out with the map. Kept so such
        // forks round-trip; zero for new resources.
        internal uint StoredHandle { get; set; }

        /// <summary>The data as stored in the fork.</summary>
        public ReadOnlyMemory<byte> GetData() => data;

        /// <summary>
        /// Replaces the data. Like the Resource Manager, data that grows moves to the end of the fork; data that does
        /// not grow keeps its place.
        /// </summary>
        public void SetData(ReadOnlyMemory<byte> data)
        {
            if (data.Length > this.data.Length) DataPlacement = NextPlacement();
            this.data = data;
            DataModified = true;
        }

        /// <inheritdoc/>
        public override string ToString() => Name is { } n ? $"'{Type}' {Id} \"{n}\"" : $"'{Type}' {Id}";

        private static long NextPlacement() => Interlocked.Increment(ref nextPlacement);
    }
}
