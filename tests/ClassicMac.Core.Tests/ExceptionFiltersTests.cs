using System.IO;
using ClassicMac.Core;

namespace ClassicMac.Core.Tests;

public class ExceptionFiltersTests
{
    public static TheoryData<Exception, bool, bool, bool> Cases => new()
    {
        // exception, IsFileAccess, IsMalformed, IsMalformedOrOutOfRange
        { new IOException(), true, false, false },
        { new FileNotFoundException(), true, false, false },
        { new UnauthorizedAccessException(), true, false, false },
        { new EndOfStreamException(), true, true, true },            // an IOException too: truncated input
        { new InvalidDataException(), false, true, true },
        { new ArgumentException(), false, false, true },
        { new ArgumentOutOfRangeException(), false, false, true },
        { new InvalidOperationException(), false, false, false },
        { new NullReferenceException(), false, false, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_filter_takes_exactly_its_exceptions(Exception e, bool fileAccess, bool malformed, bool malformedOrOutOfRange)
    {
        Assert.Equal(fileAccess, ExceptionFilters.IsFileAccess(e));
        Assert.Equal(malformed, ExceptionFilters.IsMalformed(e));
        Assert.Equal(malformedOrOutOfRange, ExceptionFilters.IsMalformedOrOutOfRange(e));
    }
}
