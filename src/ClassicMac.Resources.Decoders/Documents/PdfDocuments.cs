using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Documents;

/// <summary>
/// PDF documents (docs/formats/documents/pdf.md): found by their header, and written out as they are, since PDF is
/// still read everywhere.
/// </summary>
public static class PdfDocuments
{
    /// <summary>The converter's name, recorded in the manifest.</summary>
    public const string ConverterName = "document.pdf";

    // Acrobat reads a file whose header lies anywhere in its first 1024 bytes (pdf.md §2).
    private const int HeaderWindow = 1024;

    private static ReadOnlySpan<byte> Marker => "%PDF-"u8;

    /// <summary>Whether files of <paramref name="type"/> are PDF documents ('PDF ', as Acrobat saves them).</summary>
    public static bool IsPdf(FourCC type) => type == FourCC.FromString("PDF ");

    /// <summary>
    /// The PDF version the header gives ("1.3"), or null when <paramref name="start"/> (a file's first bytes) has no
    /// <c>%PDF-</c> header within its first 1024 bytes.
    /// </summary>
    public static string? Version(ReadOnlySpan<byte> start)
    {
        var window = start[..Math.Min(start.Length, HeaderWindow)];
        for (var from = 0; from < window.Length;)
        {
            var at = window[from..].IndexOf(Marker);
            if (at < 0)
            {
                return null;
            }

            at += from;
            var end = at + Marker.Length;
            var digits = Digits(window, end);
            if (digits > 0 && end + digits < window.Length && window[end + digits] == '.')
            {
                var minor = Digits(window, end + digits + 1);
                if (minor > 0)
                {
                    return System.Text.Encoding.ASCII.GetString(window.Slice(end, digits + 1 + minor));
                }
            }

            from = at + 1;
        }

        return null;
    }

    private static int Digits(ReadOnlySpan<byte> bytes, int from)
    {
        var count = 0;
        while (from + count < bytes.Length && bytes[from + count] is >= (byte)'0' and <= (byte)'9')
        {
            count++;
        }

        return count;
    }
}

// PDF documents to document.pdf, their data fork as it is (pdf.md §5). Only files of type 'PDF ' are read.
internal sealed class PdfConverter : IDocumentConverter
{
    public string Name => PdfDocuments.ConverterName;

    public int Version => 1;

    public IReadOnlyList<DocumentFile> Convert(DocumentInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!PdfDocuments.IsPdf(input.Type))
        {
            return [];
        }

        ReadOnlyMemory<byte> data;
        try
        {
            data = input.ReadDataFork();
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pdf.unreadable", $"The PDF document (its data fork) cannot be read: {e.Message}"));
            return [];
        }

        if (PdfDocuments.Version(data.Span) is null)
        {
            input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "pdf.no-header",
                $"{input.Title}: of type 'PDF ' but with no %PDF- header in its first 1024 bytes; not written as a PDF."));
            return [];
        }

        return [new DocumentFile("document.pdf", data)];
    }
}
