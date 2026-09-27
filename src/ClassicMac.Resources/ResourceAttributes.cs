using System;

namespace ClassicMac.Resources
{
    /// <summary>
    /// A resource's attribute byte, from <i>Inside Macintosh: More Macintosh Toolbox</i>, the Resource Manager chapter.
    /// </summary>
    [Flags]
    public enum ResourceAttributes : byte
    {
        /// <summary>No attributes.</summary>
        None = 0,
        /// <summary>
        /// The resource is compressed (System 7 onward). Known only from behaviour (other tools, real files); to be
        /// confirmed against the Resource Manager's disassembly.
        /// </summary>
        Compressed = 0x01,
        /// <summary>The resource has been changed and must be written (meaningful in memory only).</summary>
        Changed = 0x02,
        /// <summary>The resource is loaded when the file is opened.</summary>
        Preload = 0x04,
        /// <summary>The resource cannot be changed or removed.</summary>
        Protected = 0x08,
        /// <summary>The resource's handle is locked.</summary>
        Locked = 0x10,
        /// <summary>The resource's handle is purgeable.</summary>
        Purgeable = 0x20,
        /// <summary>The resource is loaded into the system heap.</summary>
        SystemHeap = 0x40,
        /// <summary>Reserved (<c>resSysRef</c> in Apple's headers).</summary>
        SystemReference = 0x80,
    }

    /// <summary>A resource map's attributes, from the same chapter.</summary>
    [Flags]
    public enum ResourceForkAttributes : ushort
    {
        /// <summary>No attributes.</summary>
        None = 0,
        /// <summary>The map has changed and must be written.</summary>
        Changed = 0x0020,
        /// <summary>The file must be compacted when it is written.</summary>
        Compact = 0x0040,
        /// <summary>The file is read-only.</summary>
        ReadOnly = 0x0080,
    }
}
