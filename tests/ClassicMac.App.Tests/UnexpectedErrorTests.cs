using ClassicMac.App.ViewModels;
using ClassicMac.Core;

namespace ClassicMac.App.Tests;

// The last resort: an exception nothing else handles (a reader's bug on a damaged file, a failure in an async event
// handler) is shown as an error diagnostic and in the status line instead of ending the app.
public class UnexpectedErrorTests
{
    [Fact]
    public void An_unexpected_exception_is_reported_as_an_error()
    {
        var model = new MainViewModel();

        model.ReportUnexpected(new InvalidOperationException("boom"), "Opening Disk.img");

        var entry = Assert.Single(model.Diagnostics);
        Assert.Equal((DiagnosticSeverity.Error, "app.unexpected-error"), (entry.Diagnostic.Severity, entry.Code));
        Assert.Contains("InvalidOperationException", entry.Message);
        Assert.Contains("boom", entry.Message);
        Assert.Equal("Opening Disk.img", entry.Source);
        Assert.Contains("Opening Disk.img", model.Status);
    }
}
