using ClassicMac.Core;

namespace ClassicMac.Resources.Decoders;

/// <summary>The files whose data fork is itself a document a converter reads, with or without a resource fork.</summary>
public static class DataForkDocuments
{
    /// <summary>Whether files of <paramref name="type"/> are converted from their data fork: Word documents, AIFF and AIFF-C sounds.</summary>
    public static bool Applies(FourCC type) => Documents.StyledDocuments.IsWord(type) || Sound.AiffConverter.IsSound(type);
}
