namespace ClassicMac.Files;

/// <summary>
/// How a volume's files and free space lie (docs/formats/file-systems/hfs.md §5.7): whether a defragmentation is worth
/// it, or why a shrink finds no room.
/// </summary>
/// <param name="Files">The files on the volume.</param>
/// <param name="FragmentedFiles">The files with a fork in more than one extent.</param>
/// <param name="FragmentedForks">The forks in more than one extent.</param>
/// <param name="MostExtents">The most extents one fork has (0 when no fork has blocks).</param>
/// <param name="FreeRuns">The runs of free allocation blocks.</param>
/// <param name="LargestFreeRun">The longest run of free allocation blocks.</param>
public sealed record VolumeFragmentation(int Files, int FragmentedFiles, int FragmentedForks, int MostExtents, int FreeRuns, long LargestFreeRun);
