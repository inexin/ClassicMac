using System;
using System.Globalization;
using ClassicMac.Files;
using ClassicMac.Resources.Decoders.Documents;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// A PDF document (docs/formats/documents/pdf.md) in the web view, whose engine shows PDFs itself on Windows (WebView2)
/// and macOS (WKWebView). WebKitGTK has none, so on Linux a PDF stays in Hex.
/// </summary>
public sealed class PdfPreview : IWebPreview
{
    // Larger documents stay in Hex: the whole file goes to the web view as a data: URI.
    internal const long MaxLength = 64L * 1024 * 1024;

    private PdfPreview(string version, byte[] data)
    {
        DataUri = "data:application/pdf;base64," + Convert.ToBase64String(data);
        Source = string.Create(CultureInfo.InvariantCulture, $"PDF {version} document, {data.Length} bytes.");
    }

    public string DataUri { get; }

    /// <summary>What the Info view (or a window without a web view) shows: the version and size.</summary>
    public string Source { get; }

    /// <summary>Whether this system's web view shows PDFs.</summary>
    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The preview of a file whose data fork is a PDF (by its header, whatever its type), or null.</summary>
    public static PdfPreview? Create(MacFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!Supported || file.DataFork.Length is 0 or > MaxLength || PdfDocuments.Version(file.DataFork.ReadPrefix(1024)) is not { } version)
        {
            return null;
        }

        return new PdfPreview(version, file.DataFork.ToArray());
    }
}
