using System;
using System.IO;

namespace ClassicMac.Core;

/// <summary>
/// The exception sets that recur in <c>catch … when</c> filters, so each is named once. Each takes exactly its
/// exceptions and their subclasses; a rarer set stays written out where it is used.
/// </summary>
public static class ExceptionFilters
{
    /// <summary>
    /// The host's file system failed or refused: <see cref="IOException"/> (with its subclasses, such as a missing
    /// file or a truncated stream) or <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static bool IsFileAccess(Exception e) => e is IOException or UnauthorizedAccessException;

    /// <summary>The input's bytes do not parse: <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/> (truncated).</summary>
    public static bool IsMalformed(Exception e) => e is InvalidDataException or EndOfStreamException;

    /// <summary>
    /// <see cref="IsMalformed"/>, or an <see cref="ArgumentException"/>: a value read from the input out of the range
    /// an API takes (an offset past a buffer, a count too large).
    /// </summary>
    public static bool IsMalformedOrOutOfRange(Exception e) => IsMalformed(e) || e is ArgumentException;
}
