using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Documents;
using ClassicMac.Resources.Decoders.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>
    /// A stretch of a chapter's text between picture rows, with its alignment: the chapter's, or for a Word document the
    /// paragraphs' own, with their side indents and spacing as the margin (a point a pixel).
    /// </summary>
    public sealed record DocumentTextItem(StyledText Text, Justification Justification, Thickness Margin = default)
    {
        public TextAlignment Alignment => Justification switch
        {
            Justification.Center => TextAlignment.Center,
            Justification.Right => TextAlignment.Right,
            Justification.Full => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
    }

    /// <summary>A row of pictures, each in its place: left, centre or right.</summary>
    public sealed record DocumentRowItem(IReadOnlyList<DocumentPictureItem> Left, IReadOnlyList<DocumentPictureItem> Center,
        IReadOnlyList<DocumentPictureItem> Right);

    /// <summary>
    /// A picture: its PNG (null when it cannot be drawn), its size on screen, a tooltip, and what clicking it does (null
    /// for nothing).
    /// </summary>
    public sealed record DocumentPictureItem(byte[]? Png, int Width, int Height, string ToolTip, IRelayCommand? Open)
    {
        public bool IsLink => Open is not null;
    }

    /// <summary>
    /// A DOCMaker or SimpleText document in the preview: a chapter at a time, laid out as the HTML output lays it out
    /// (<see cref="DocumentFlow"/>: pictures reflowed into the text), at the Mac's 72 dpi (a point is a pixel). Clicking a
    /// picture that goes to a chapter, the next or previous one, or back, does so.
    /// </summary>
    public sealed partial class DocumentPreview : ObservableObject
    {
        private readonly Dictionary<short, byte[]?> pictures;
        private readonly Stack<int> history = new();

        private DocumentPreview(StyledDocument document, Dictionary<short, byte[]?> pictures)
        {
            Document = document;
            this.pictures = pictures;
            ChapterTitles = document.Chapters.Select(c => c.Title).ToList();
            Items = Layout(0);
        }

        public StyledDocument Document { get; }

        public IReadOnlyList<string> ChapterTitles { get; }

        public bool HasChapters => Document.Chapters.Count > 1;

        /// <summary>The chapter shown, from 0.</summary>
        [ObservableProperty]
        private int chapterIndex;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Background), nameof(ColumnWidth))]
        private IReadOnlyList<object> items = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        private bool canGoBack;

        private DocumentChapter Chapter => Document.Chapters[Math.Clamp(ChapterIndex, 0, Document.Chapters.Count - 1)];

        /// <summary>The chapter's background, white when it has none.</summary>
        public IBrush Background => new SolidColorBrush(Chapter.Background is { } b ? Color.FromRgb(b.Red, b.Green, b.Blue) : Colors.White);

        /// <summary>Where the chapter is scrolled to; a new chapter starts at the top.</summary>
        [ObservableProperty]
        private Vector offset;

        /// <summary>The text column's width in pixels, or NaN (no fixed width).</summary>
        public double ColumnWidth => Chapter.ColumnWidth > 0 ? Chapter.ColumnWidth : double.NaN;

        /// <summary>Reads the document and draws its pictures (off the UI thread); drawing problems go to <paramref name="diagnostics"/>.</summary>
        internal static DocumentPreview Create(StyledDocument document, DecodeOptions options, ICollection<Diagnostic> diagnostics)
        {
            var drawn = new Dictionary<short, byte[]?>();
            foreach (var chapter in document.Chapters)
            {
                foreach (var picture in chapter.Pictures)
                {
                    if (!drawn.ContainsKey(picture.PictureId))
                    {
                        drawn[picture.PictureId] = DocumentPictures.Draw(chapter, picture, options, diagnostics);
                    }
                }
            }
            return new DocumentPreview(document, drawn);
        }

        partial void OnChapterIndexChanged(int oldValue, int newValue)
        {
            if (!navigating)
            {
                history.Push(oldValue);
                CanGoBack = true;
            }
            Items = Layout(newValue);
            Offset = default;
        }

        private bool navigating;

        [RelayCommand(CanExecute = nameof(CanGoBack))]
        private void Back()
        {
            if (history.Count == 0)
            {
                return;
            }

            navigating = true;
            ChapterIndex = history.Pop();
            navigating = false;
            CanGoBack = history.Count > 0;
        }

        // The chapter's blocks, consecutive lines joined into one text.
        private List<object> Layout(int index)
        {
            var chapter = Document.Chapters[Math.Clamp(index, 0, Document.Chapters.Count - 1)];
            var items = new List<object>();
            var lines = new List<DocumentLine>();
            ParagraphFormat? format = null;
            void Flush()
            {
                if (lines.Count == 0)
                {
                    return;
                }

                var first = chapter.ParagraphAt(lines[0].Start);
                var last = chapter.ParagraphAt(lines[^1].Start);
                items.Add(first is null
                    ? new DocumentTextItem(Join(chapter.Text, lines), chapter.Justification)
                    : new DocumentTextItem(Join(chapter.Text, lines), first.Justification,
                        new Thickness(first.LeftIndent, first.SpaceBefore, first.RightIndent, last?.SpaceAfter ?? 0)));
                lines.Clear();
            }
            foreach (var block in DocumentFlow.Blocks(chapter))
            {
                if (block is DocumentLine line)
                {
                    // A Word paragraph whose alignment or indents differ from the lines before starts a new stretch, as
                    // does one with space around it (its own margins).
                    var next = chapter.ParagraphAt(line.Start);
                    if (next is not null && format is not null && next.Start == line.Start
                        && (next with { Start = 0, FirstLineIndent = 0 } != format with { Start = 0, FirstLineIndent = 0 }
                            || next.SpaceBefore != 0 || format.SpaceAfter != 0))
                    {
                        Flush();
                    }

                    format = next;
                    lines.Add(line);
                    continue;
                }
                Flush();
                var row = ((PictureRow)block).Pictures.Select(p => (p.Alignment, Item: Picture(chapter, index, p))).ToList();
                items.Add(new DocumentRowItem(
                    row.Where(p => p.Alignment == PictureAlignment.Left).Select(p => p.Item).ToList(),
                    row.Where(p => p.Alignment == PictureAlignment.Center).Select(p => p.Item).ToList(),
                    row.Where(p => p.Alignment == PictureAlignment.Right).Select(p => p.Item).ToList()));
            }
            Flush();
            return items;
        }

        // Lines as one styled text: each line's characters, CR between them, and the runs cut to them.
        private static StyledText Join(StyledText text, List<DocumentLine> lines)
        {
            var joined = new System.Text.StringBuilder();
            var runs = new List<TextRun>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (i > 0)
                {
                    // The line break belongs to the run before it (runs cover every character), or after empty lines
                    // at the start, to one in the style of the previous line's first character.
                    if (runs.Count > 0)
                    {
                        runs[^1] = runs[^1] with { Length = joined.Length + 1 - runs[^1].Start };
                    }
                    else if (text.Runs.FirstOrDefault(r => r.Start <= lines[i - 1].Start && lines[i - 1].Start < r.Start + r.Length) is { } style)
                    {
                        runs.Add(style with { Start = joined.Length, Length = 1 });
                    }

                    joined.Append('\r');
                }
                var offset = joined.Length - line.Start;
                joined.Append(text.Text, line.Start, line.End - line.Start);
                foreach (var run in text.Runs)
                {
                    var from = Math.Max(line.Start, run.Start);
                    var to = Math.Min(line.End, run.Start + run.Length);
                    if (from < to)
                    {
                        runs.Add(run with { Start = from + offset, Length = to - from });
                    }
                }
            }
            return new StyledText(joined.ToString(), runs, text.Complete);
        }

        private DocumentPictureItem Picture(DocumentChapter chapter, int index, DocumentPicture picture)
        {
            var png = pictures.GetValueOrDefault(picture.PictureId);
            int width = picture.Width, height = picture.Height;
            if (!picture.NoScale && chapter.ColumnWidth > 0 && width > chapter.ColumnWidth)
            {
                height = (int)Math.Round((double)height * chapter.ColumnWidth / width);
                width = chapter.ColumnWidth;
            }
            var (target, what) = picture.Action.Code switch
            {
                1 => (Document.Chapters.ToList().FindIndex(c => c.Number == picture.Action.Chapter) is var i and >= 0 ? i : (int?)null, "Go to a chapter"),
                14 => (index + 1 < Document.Chapters.Count ? index + 1 : null, "Next chapter"),
                15 => (index > 0 ? index - 1 : null, "Previous chapter"),
                10 => (null, "Back"),
                8 => (null, picture.Action.Text ?? "A note"),
                5 => (null, $"Opens {picture.Action.Text}"),
                7 => (null, $"Plays the movie {picture.Action.Text}"),
                13 or 16 => (null, "Runs a script"),
                _ => ((int?)null, ""),
            };
            IRelayCommand? open = target is { } t ? new RelayCommand(() => ChapterIndex = t)
                : picture.Action.Code == 10 ? BackCommand : null;
            var tip = target is { } to ? $"{what}: {Document.Chapters[to].Title}" : what;
            return new DocumentPictureItem(png, width, height, png is null ? $"PICT {picture.PictureId} (not drawn)" : tip, open);
        }
    }
}
