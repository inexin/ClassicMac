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

        /// <summary>Type, creator, flags and icon position.</summary>
        public FinderInfo FinderInfo { get; init; } = FinderInfo.Empty;

        /// <summary>When the file was created, if the container records it.</summary>
        public MacDate? Created { get; init; }

        /// <summary>When the file was last modified, if the container records it.</summary>
        public MacDate? Modified { get; init; }

        /// <summary>The data fork.</summary>
        public ForkData DataFork { get; init; } = ForkData.Empty;

        /// <summary>The resource fork.</summary>
        public ForkData ResourceFork { get; init; } = ForkData.Empty;
    }
}
