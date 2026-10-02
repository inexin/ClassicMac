using System;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Resources.Editing
{
    /// <summary>A change to a resource fork that can be applied and undone.</summary>
    public interface IResourceEdit
    {
        /// <summary>What the edit does, for an Undo menu item ("Delete 'STR ' 128").</summary>
        string Description { get; }

        /// <summary>Makes the change.</summary>
        void Apply(ResourceFork fork);

        /// <summary>Reverses <see cref="Apply"/>.</summary>
        void Undo(ResourceFork fork);
    }

    /// <summary>Adds a new resource.</summary>
    public sealed class AddResource(FourCC type, short id, MacString? name, ReadOnlyMemory<byte> data, ResourceAttributes attributes = ResourceAttributes.None)
        : IResourceEdit
    {
        private Resource? added;

        /// <summary>The resource added (after <see cref="Apply"/>).</summary>
        public Resource? Added => added;

        /// <inheritdoc/>
        public string Description => $"New '{type}' {id}";

        /// <inheritdoc/>
        public void Apply(ResourceFork fork)
        {
            added ??= new Resource(type, id, data) { Name = name, Attributes = attributes & ~ResourceAttributes.Compressed };
            fork.Add(added);
        }

        /// <inheritdoc/>
        public void Undo(ResourceFork fork) => fork.Remove(added!);
    }

    /// <summary>Deletes a resource; undo puts it back where it was.</summary>
    public sealed class DeleteResource(Resource resource) : IResourceEdit
    {
        private int position;

        /// <inheritdoc/>
        public string Description => $"Delete '{resource.Type}' {resource.Id}";

        /// <inheritdoc/>
        public void Apply(ResourceFork fork)
        {
            position = fork.Resources.ToList().IndexOf(resource);
            fork.Remove(resource);
        }

        /// <inheritdoc/>
        public void Undo(ResourceFork fork) => fork.Insert(position, resource);
    }

    /// <summary>Duplicates a resource under the next free ID from 128 up, with its name, attributes and data.</summary>
    public sealed class DuplicateResource(Resource resource) : IResourceEdit
    {
        private Resource? copy;

        /// <summary>The copy (after <see cref="Apply"/>).</summary>
        public Resource? Copy => copy;

        /// <inheritdoc/>
        public string Description => $"Duplicate '{resource.Type}' {resource.Id}";

        /// <inheritdoc/>
        public void Apply(ResourceFork fork)
        {
            copy ??= new Resource(resource.Type, ResourceEditRules.NextFreeId(fork, resource.Type), resource.GetData().ToArray())
            {
                Name = resource.Name,
                Attributes = resource.Attributes,
            };
            fork.Add(copy);
        }

        /// <inheritdoc/>
        public void Undo(ResourceFork fork) => fork.Remove(copy!);
    }

    /// <summary>Changes a resource's ID, name and attributes (Get Info).</summary>
    public sealed class SetResourceInfo(Resource resource, short id, MacString? name, ResourceAttributes attributes) : IResourceEdit
    {
        private (short Id, MacString? Name, ResourceAttributes Attributes) before;

        /// <inheritdoc/>
        public string Description => $"Change '{resource.Type}' {resource.Id}";

        /// <inheritdoc/>
        public void Apply(ResourceFork fork)
        {
            before = (resource.Id, resource.Name, resource.Attributes);
            Set(fork, id, name, attributes);
        }

        /// <inheritdoc/>
        public void Undo(ResourceFork fork) => Set(fork, before.Id, before.Name, before.Attributes);

        private void Set(ResourceFork fork, short newId, MacString? newName, ResourceAttributes newAttributes)
        {
            fork.Renumber(resource, newId);
            resource.Name = newName;
            resource.Attributes = newAttributes;
        }
    }

    /// <summary>Replaces a resource's data (from a file or the hex editor). New data is stored uncompressed.</summary>
    public sealed class SetResourceData(Resource resource, ReadOnlyMemory<byte> data, string? description = null) : IResourceEdit
    {
        private ReadOnlyMemory<byte> oldData;
        private ResourceAttributes oldAttributes;

        /// <inheritdoc/>
        public string Description => description ?? $"Change data of '{resource.Type}' {resource.Id}";

        /// <inheritdoc/>
        public void Apply(ResourceFork fork)
        {
            (oldData, oldAttributes) = (resource.GetData(), resource.Attributes);
            resource.SetData(data);
            resource.Attributes &= ~ResourceAttributes.Compressed;
        }

        /// <inheritdoc/>
        public void Undo(ResourceFork fork)
        {
            resource.SetData(oldData);
            resource.Attributes = oldAttributes;
        }
    }

    /// <summary>Several edits made and undone as one (a <c>'TEXT'</c> with its <c>'styl'</c>).</summary>
    public sealed class CompoundEdit(string description, params IResourceEdit[] edits) : IResourceEdit
    {
        /// <inheritdoc/>
        public string Description => description;

        /// <inheritdoc/>
        public void Apply(ResourceFork fork)
        {
            foreach (var edit in edits)
            {
                edit.Apply(fork);
            }
        }

        /// <inheritdoc/>
        public void Undo(ResourceFork fork)
        {
            for (var i = edits.Length - 1; i >= 0; i--)
            {
                edits[i].Undo(fork);
            }
        }
    }

    /// <summary>Changes the resource map's attributes.</summary>
    public sealed class SetForkAttributes(ResourceForkAttributes attributes) : IResourceEdit
    {
        private ResourceForkAttributes before;

        /// <inheritdoc/>
        public string Description => "Change file attributes";

        /// <inheritdoc/>
        public void Apply(ResourceFork fork)
        {
            before = fork.Attributes;
            fork.Attributes = attributes;
        }

        /// <inheritdoc/>
        public void Undo(ResourceFork fork) => fork.Attributes = before;
    }
}
