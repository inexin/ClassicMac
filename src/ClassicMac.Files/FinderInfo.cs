using System;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// Finder flags (<c>fdFlags</c>), from <i>Inside Macintosh: Macintosh Toolbox Essentials</i>, the Finder Interface
    /// chapter.
    /// </summary>
    [Flags]
    public enum FinderFlags : ushort
    {
        /// <summary>No flags.</summary>
        None = 0,
        /// <summary>The file is on the desktop (System 6 and earlier).</summary>
        IsOnDesk = 0x0001,
        /// <summary>The three-bit colour label.</summary>
        ColorMask = 0x000E,
        /// <summary>An application that can run multiple times from a shared volume.</summary>
        IsShared = 0x0040,
        /// <summary>The file contains no <c>INIT</c> resources.</summary>
        HasNoInits = 0x0080,
        /// <summary>The Finder has recorded the file's bundle information.</summary>
        HasBeenInited = 0x0100,
        /// <summary>The file has a custom icon (resource ID -16455).</summary>
        HasCustomIcon = 0x0400,
        /// <summary>The file is stationery.</summary>
        IsStationery = 0x0800,
        /// <summary>The file's name cannot be changed.</summary>
        NameLocked = 0x1000,
        /// <summary>The file has a <c>BNDL</c> resource.</summary>
        HasBundle = 0x2000,
        /// <summary>The file is invisible.</summary>
        IsInvisible = 0x4000,
        /// <summary>The file is an alias.</summary>
        IsAlias = 0x8000,
    }

    /// <summary>
    /// A file's Finder information: the <c>FInfo</c> record (type, creator, flags, location, folder) and the 16 bytes
    /// of <c>FXInfo</c>, kept raw so containers round-trip them.
    /// </summary>
    public sealed record FinderInfo
    {
        /// <summary>No type, creator or flags.</summary>
        public static FinderInfo Empty { get; } = new();

        /// <summary>The file type (<c>fdType</c>).</summary>
        public FourCC Type { get; init; }

        /// <summary>The creator (<c>fdCreator</c>).</summary>
        public FourCC Creator { get; init; }

        /// <summary>The Finder flags (<c>fdFlags</c>).</summary>
        public FinderFlags Flags { get; init; }

        /// <summary>The icon's position in its window (<c>fdLocation</c>).</summary>
        public MacPoint Location { get; init; }

        /// <summary>The window the icon is in (<c>fdFldr</c>).</summary>
        public short Folder { get; init; }

        /// <summary>The extended Finder information (<c>FXInfo</c>), 16 bytes, uninterpreted.</summary>
        public ReadOnlyMemory<byte> Extended { get; init; } = new byte[16];
    }
}
