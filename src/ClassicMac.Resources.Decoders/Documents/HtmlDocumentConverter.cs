using System;
using System.Collections.Generic;
using System.IO;
using ClassicMac.Core;
using ClassicMac.Resources.Export;

namespace ClassicMac.Resources.Decoders.Documents
{
    // DOCMaker, SimpleText and Word documents to an HTML folder: StyledDocuments reads, HtmlDocuments writes. The data
    // fork is read only for a TEXT or ttro file (a SimpleText document's text) and a Word document (the document).
    internal sealed class HtmlDocumentConverter(DecodeOptions options) : IDocumentConverter
    {
        private static readonly FourCC Text = FourCC.FromString("TEXT"), Ttro = FourCC.FromString("ttro");

        public string Name => HtmlDocuments.ConverterName;

        public int Version => 1;

        public IReadOnlyList<DocumentFile> Convert(DocumentInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            var text = ReadOnlyMemory<byte>.Empty;
            if (input.Type == Text || input.Type == Ttro || StyledDocuments.IsWord(input.Type))
            {
                try
                {
                    text = input.ReadDataFork();
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                    input.Diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "document.unreadable-text",
                        $"The document's text (its data fork) cannot be read: {e.Message}"));
                    return [];
                }
            }
            var document = StyledDocuments.Read(text, input.Fork, input.Type, input.Title, options, input.ReadOptions, input.Diagnostics);
            return document is null ? [] : HtmlDocuments.Write(document, options, input.Diagnostics);
        }
    }
}
