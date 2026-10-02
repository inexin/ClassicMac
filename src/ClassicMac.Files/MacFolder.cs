using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// A folder of a volume as its catalog records it: name, place, Finder information (the window the Finder opens for
    /// it and its icon's place in its parent's window) and dates. Volume readers that keep folder records return these
    /// beside the files (<see cref="Hfs.HfsReader.ReadFolders"/>).
    /// </summary>
    public sealed record MacFolder
    {
        /// <summary>The folder's name; the volume's name for the root folder.</summary>
        public required MacString Name { get; init; }

        /// <summary>The folders from the volume's root down to the one holding this folder (the root not included).</summary>
        public IReadOnlyList<MacString> FolderPath { get; init; } = [];

        /// <summary>Whether this is the volume's root folder, whose window is the volume's.</summary>
        public bool IsRoot { get; init; }

        /// <summary>The <c>DInfo</c> and <c>DXInfo</c>.</summary>
        public FolderFinderInfo FinderInfo { get; init; } = FolderFinderInfo.Empty;

        /// <summary>
        /// On the root folder, the volume's free space as its header records it: free allocation blocks times the block
        /// size (HFS <c>drFreeBks</c> × <c>drAlBlkSiz</c>, HFS Plus <c>freeBlocks</c> × <c>blockSize</c>), the figure the
        /// Finder's window header shows as available; null on other folders.
        /// </summary>
        public long? FreeBytes { get; init; }

        /// <summary>When the folder was created, if recorded.</summary>
        public MacDate? Created { get; init; }

        /// <summary>When the folder was last modified, if recorded.</summary>
        public MacDate? Modified { get; init; }

        /// <summary>
        /// The folder path of the files inside this folder (<see cref="MacFile.FolderPath"/>): its parents and its name,
        /// or nothing for the root.
        /// </summary>
        public IReadOnlyList<MacString> Path => IsRoot ? [] : [.. FolderPath, Name];

        /// <summary>The Mac path of the folder inside the volume, joined with ':'; empty for the root.</summary>
        public string MacPath => string.Join(":", Path.Select(n => n.ToString()));
    }
}
