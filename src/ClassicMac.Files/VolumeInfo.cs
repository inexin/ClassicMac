using ClassicMac.Core;

namespace ClassicMac.Files
{
    /// <summary>
    /// A volume's own dates, from its master directory block or volume header: HFS's <c>drCrDate</c>, <c>drLsMod</c>
    /// and <c>drVolBkUp</c>; HFS Plus's <c>createDate</c>, <c>modifyDate</c> and <c>backupDate</c>; MFS's
    /// <c>drCrDate</c> and <c>drLsBkUp</c> (MFS keeps no modification date). A zero date is null.
    /// </summary>
    /// <param name="Format">"HFS", "HFS Plus" or "MFS".</param>
    /// <param name="Created">When the volume was created (initialised).</param>
    /// <param name="Modified">When it was last modified; MFS has none.</param>
    /// <param name="BackedUp">When it was last backed up.</param>
    public sealed record VolumeInfo(string Format, MacDate? Created, MacDate? Modified, MacDate? BackedUp)
    {
        /// <summary>
        /// Whether the dates after the creation date are in UTC: HFS Plus keeps <c>createDate</c> in local time and the
        /// others in UTC (TN1150); HFS and MFS keep all of them in local time.
        /// </summary>
        public bool UtcAfterCreation => Format == "HFS Plus";
    }

    /// <summary>A container reader for a volume format, which can also give the volume's own dates.</summary>
    public interface IVolumeReader
    {
        /// <summary>The volume's dates, or null when <paramref name="input"/> is not a volume this reader can read.</summary>
        VolumeInfo? ReadVolumeInfo(ForkData input);
    }
}
