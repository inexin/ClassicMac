using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// A file as the Mac saw it: name, Finder information, dates and both forks. Every container yields these.
    /// </summary>
    public sealed record MacFile
    {
        /// <summary>The file name, in the encoding of the volume or container it came from.</summary>
        public required MacString Name { get; init; }

        /// <summary>The original Unicode name when the container stores Unicode rather than Mac-encoded bytes.</summary>
        public string? UnicodeName { get; init; }

        /// <summary>
        /// The folders from the container's root down to the file (a volume's root folder is not included); empty for
        /// single-file containers.
        /// </summary>
        public IReadOnlyList<MacString> FolderPath { get; init; } = [];

        /// <summary>The original Unicode folder names when the container stores Unicode paths.</summary>
        public IReadOnlyList<string>? UnicodeFolderPath { get; init; }

        /// <summary>Type, creator, flags and icon position.</summary>
        public FinderInfo FinderInfo { get; init; } = FinderInfo.Empty;

        /// <summary>
        /// Whether the file is locked (HFS <c>filFlags</c> bit 0, HFS Plus <c>kHFSFileLockedMask</c>): the Finder's Get Info
        /// "Locked" box, shown as a lock badge. False where the container records no such flag.
        /// </summary>
        public bool IsLocked { get; init; }

        /// <summary>When the file was created, if the container records it.</summary>
        public MacDate? Created { get; init; }

        /// <summary>When the file was last modified, if the container records it.</summary>
        public MacDate? Modified { get; init; }

        /// <summary>The data fork.</summary>
        public ForkData DataFork { get; init; } = ForkData.Empty;

        /// <summary>The resource fork.</summary>
        public ForkData ResourceFork { get; init; } = ForkData.Empty;

        /// <summary>The UTF-8 target path when this file is an HFS Plus symbolic link.</summary>
        public string? SymbolicLinkTarget { get; init; }

        /// <summary>The HFS Plus indirect-node reference when this file is a hard link.</summary>
        public uint? HardLinkReference { get; init; }

        /// <summary>The Mac path inside the container, folders and name joined with ':' as the Mac wrote paths.</summary>
        public string MacPath => UnicodeName is { } name
            ? string.Join(":", (UnicodeFolderPath ?? FolderPath.Select(n => n.ToString()).ToArray()).Append(name))
            : string.Join(":", FolderPath.Append(Name).Select(n => n.ToString()));
    }
}
