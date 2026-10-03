using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>A page the preview shows in the web view: the address it loads, and the text shown instead of it.</summary>
    public interface IWebPreview
    {
        /// <summary>The page as a <c>data:</c> URI.</summary>
        string DataUri { get; }

        /// <summary>What the Source (or Text) view shows, and what shows when there is no web view.</summary>
        string Source { get; }
    }

    /// <summary>
    /// A DOCMaker, SimpleText or Word document in the preview: the HTML that Convert Documents writes
    /// (<see cref="HtmlDocuments"/>), a page at a time, made ready for the web view the way a help page is
    /// (<see cref="HelpPages.Render"/>: the stylesheet and pictures put into the page), so the preview and the export are
    /// one layout (docs/PLAN.md "Documents previewed through their HTML export"). A DOCMaker document has its contents
    /// page first, then a page per chapter; links between them, and its Back pictures, turn the page here.
    /// </summary>
    public sealed partial class DocumentWebPreview : ObservableObject, IWebPreview
    {
        private readonly Dictionary<string, ReadOnlyMemory<byte>> files;
        private readonly IReadOnlyList<string> pages;
        private readonly DecodeOptions options;
        private readonly Stack<int> history = new();
        private string? fragment;
        private bool navigating;

        private DocumentWebPreview(StyledDocument document, IReadOnlyList<DocumentFile> written, DecodeOptions options)
        {
            Document = document;
            this.options = options;
            files = written.ToDictionary(f => f.Path, f => f.Content, StringComparer.OrdinalIgnoreCase);
            pages = written.Select(f => f.Path).Where(p => p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)).ToList();
            var contents = document.Kind == DocumentKind.DocMaker;
            ChapterTitles = contents ? ["Contents", .. document.Chapters.Select(c => c.Title)] : [document.Chapters.FirstOrDefault()?.Title ?? ""];
            Source = Text(document);
            chapterIndex = contents && pages.Count > 1 ? 1 : 0;
            dataUri = Render();
        }

        public StyledDocument Document { get; }

        /// <summary>The pages' titles, in <see cref="ChapterIndex"/> order: "Contents" and the chapters for a DOCMaker document.</summary>
        public IReadOnlyList<string> ChapterTitles { get; }

        public bool HasChapters => pages.Count > 1;

        /// <summary>The page shown (its file name in the HTML export).</summary>
        public string Page => pages[Math.Clamp(ChapterIndex, 0, pages.Count - 1)];

        /// <summary>The page shown, from 0; choosing another turns to it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Page))]
        private int chapterIndex;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        private bool canGoBack;

        /// <summary>The page shown as a <c>data:</c> URI, with the anchor a link went to.</summary>
        [ObservableProperty]
        private string dataUri;

        /// <summary>The document's text, chapter by chapter: the Text view, and what shows without a web view.</summary>
        public string Source { get; }

        /// <summary>Converts the document (off the UI thread); problems drawing its pictures go to <paramref name="diagnostics"/>.</summary>
        internal static DocumentWebPreview Create(StyledDocument document, DecodeOptions options, ICollection<Diagnostic> diagnostics) =>
            new(document, HtmlDocuments.Write(document, options, diagnostics), options);

        /// <summary>Turns to a page of the document by its file name, at an anchor; false when the document has no such page.</summary>
        public bool Open(string page, string? anchor)
        {
            var index = pages.ToList().FindIndex(p => string.Equals(p, page, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            fragment = anchor;
            if (index == ChapterIndex)
            {
                DataUri = Render();
            }
            else
            {
                ChapterIndex = index;
            }

            return true;
        }

        partial void OnChapterIndexChanged(int oldValue, int newValue)
        {
            if (!navigating)
            {
                history.Push(oldValue);
                CanGoBack = true;
            }

            DataUri = Render();
            fragment = null;
        }

        [RelayCommand(CanExecute = nameof(CanGoBack))]
        private void Back()
        {
            if (history.Count == 0)
            {
                return;
            }

            navigating = true;
            fragment = null;
            ChapterIndex = history.Pop();
            navigating = false;
            CanGoBack = history.Count > 0;
        }

        // The page with its stylesheet and pictures put in, as the web view loads it.
        private string Render()
        {
            var html = Encoding.UTF8.GetString(files[Page].Span);
            var ready = HelpPages.Render(html, [], path => files.TryGetValue(string.Join('/', path), out var data)
                ? new HelpFile(data, FourCC.FromString(path[^1].EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "PNGf" : "TEXT"))
                : null, options);
            return HelpPages.DataUri(ready) + (fragment is null ? "" : "#" + fragment);
        }

        // Each chapter's title (for a document of several) and its text, lines as the Mac breaks them.
        private static string Text(StyledDocument document)
        {
            var text = new StringBuilder();
            foreach (var chapter in document.Chapters)
            {
                if (document.Chapters.Count > 1)
                {
                    text.Append(chapter.Title).Append("\n\n");
                }

                text.Append(chapter.Text.Text.Replace('\r', '\n')).Append("\n\n");
            }

            return text.ToString().TrimEnd('\n');
        }
    }
}
