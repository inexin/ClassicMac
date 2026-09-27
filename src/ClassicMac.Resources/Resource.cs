using System;

namespace ClassicMac.Resources
{
    /// <summary>
    /// One resource: type, ID, optional name, attributes and data. The data is as stored in the fork (still compressed
    /// when <see cref="ResourceAttributes.Compressed"/> is set) and may be read lazily from the source.
    /// </summary>
    public sealed class Resource
    {
        private Func<ReadOnlyMemory<byte>> load;

        /// <summary>Creates a resource holding <paramref name="data"/>.</summary>
        public Resource(FourCC type, short id, ReadOnlyMemory<byte> data)
        {
            Type = type;
            Id = id;
            Length = data.Length;
            load = () => data;
        }

        // For readers: the data is loaded from the source each time it is asked for, so opening a fork stays cheap.
        internal Resource(FourCC type, short id, int length, Func<ReadOnlyMemory<byte>> load)
        {
            Type = type;
            Id = id;
            Length = length;
            this.load = load;
        }

        /// <summary>The resource type.</summary>
        public FourCC Type { get; }

        /// <summary>The resource ID; change it with <see cref="ResourceFork.Renumber"/>.</summary>
        public short Id { get; internal set; }

        /// <summary>The name, or <see langword="null"/> when the resource has none (an empty name is distinct).</summary>
        public MacString? Name { get; set; }

        /// <summary>The attribute byte.</summary>
        public ResourceAttributes Attributes { get; set; }

        /// <summary>The length of the stored data in bytes.</summary>
        public int Length { get; private set; }

        // The fork this resource belongs to, so it cannot be added to two.
        internal ResourceFork? Owner { get; set; }

        /// <summary>The data as stored in the fork.</summary>
        public ReadOnlyMemory<byte> GetData() => load();

        /// <summary>Replaces the data.</summary>
        public void SetData(ReadOnlyMemory<byte> data)
        {
            Length = data.Length;
            load = () => data;
        }

        /// <inheritdoc/>
        public override string ToString() => Name is { } name ? $"'{Type}' {Id} \"{name}\"" : $"'{Type}' {Id}";
    }
}
