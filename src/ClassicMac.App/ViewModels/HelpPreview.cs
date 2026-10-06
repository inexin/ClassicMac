using System;
using System.Collections.Generic;
using System.Linq;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders;
using ClassicMac.Resources.Decoders.Documents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassicMac.App.ViewModels;

/// <summary>
/// An Apple Help page in the preview (docs/formats/resources/help-pages.md): its source, and the page made ready for
/// the web view (<see cref="HelpPages.Render"/>) from the files of its disk.
/// </summary>
public sealed class HelpPagePreview : IWebPreview
{
    /// <summary>The largest file a page reads (itself, or a picture, stylesheet or frame it shows).</summary>
    private const int MaxFile = 16 * 1024 * 1024;

    private HelpPagePreview(FileNode node, string title, IReadOnlyList<string> folder, string source, string html)
    {
        Node = node;
        Title = title;
        Folder = folder;
        Source = source;
        Html = html;
        DataUri = HelpPages.DataUri(html);
    }

    /// <summary>The page's file.</summary>
    public FileNode Node { get; }

    /// <summary>Its AppleTitle, else its title, else the file's name.</summary>
    public string Title { get; }

    /// <summary>Its folder's path from the volume's root.</summary>
    public IReadOnlyList<string> Folder { get; }

    /// <summary>The page's text as written (decoded).</summary>
    public string Source { get; }

    /// <summary>The page made ready for the web view.</summary>
    public string Html { get; }

    /// <summary>The address the web view loads: the ready page as a <c>data:</c> URI.</summary>
    public string DataUri { get; }

    /// <summary>Whether a file is a help page (a <c>TEXT</c> file of Help Viewer, or an <c>.htm</c>/<c>.html</c> file).</summary>
    internal static bool Applies(FileNode file) =>
        HelpPages.IsHelpPage(file.File.FinderInfo.Type, file.File.FinderInfo.Creator, file.Title) && file.File.DataFork.Length <= MaxFile;

    /// <summary>Reads the page and the files it shows; problems with them go to <paramref name="diagnostics"/>.</summary>
    internal static HelpPagePreview Create(FileNode file, DecodeOptions options, ICollection<Diagnostic> diagnostics)
    {
        var source = HelpPages.Decode(file.File.DataFork.ToArray(MaxFile));
        var folder = PathOf(TreeLayout.FolderOf(file));
        var root = RootOf(file);
        var html = HelpPages.Render(source, folder, path => Find(root, path) is FileNode found && found.File.DataFork.Length <= MaxFile
            ? new HelpFile(found.File.DataFork.ToArray(MaxFile), found.File.FinderInfo.Type) : null, options, diagnostics);
        return new HelpPagePreview(file, HelpPages.Title(source) ?? file.Title, folder, source.Replace("\r\n", "\n").Replace('\r', '\n'), html);
    }

    // The volume's root above a node: the input, or the container file (a disk image inside the disk) it is in.
    internal static NodeViewModel RootOf(NodeViewModel node)
    {
        var at = node;
        while (at.Parent is { } parent && at is not InputNode and not ContainerFileNode)
        {
            at = parent;
        }

        return at;
    }

    // A folder's path from its volume's root: the names of the folders down to it.
    private static List<string> PathOf(NodeViewModel? folder)
    {
        var names = new List<string>();
        for (var at = folder; at is FolderNode; at = TreeLayout.FolderOf(at))
        {
            names.Insert(0, at.BaseTitle);
        }

        return names;
    }

    /// <summary>The file or folder at a path from a volume's root (names compared as HFS does, ignoring case); null when there is none.</summary>
    internal static NodeViewModel? Find(NodeViewModel root, IReadOnlyList<string> path)
    {
        var at = root;
        foreach (var name in path)
        {
            var next = TreeLayout.Contents(at).SelectMany(n => n is NoNameGroupNode group ? TreeLayout.Contents(group) : [n])
                .FirstOrDefault(n => string.Equals(NameOf(n), name, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return null;
            }

            at = next;
        }

        return at;
    }

    private static string NameOf(NodeViewModel node) => node switch
    {
        FileNode file => file.File.Name.ToMacRoman(),
        ContainerFileNode container => container.File.Name.ToMacRoman(),
        _ => node.BaseTitle,
    };
}

// The help page's Rendered | Source switch and its links (boards: the document preview's switch, as Properties | JSON).
public sealed partial class HelpPreview(IAppSelection appSelection, IAppView appView) : ObservableObject
{
    /// <summary>The switch's segments, in <see cref="HelpModeIndex"/> order: Rendered, then Source (a help page's HTML) or Text (a document's).</summary>
    public IReadOnlyList<string> WebModes => ["Rendered", appView.Preview.IsDocument ? "Text" : appView.Preview.IsPdf ? "Info" : "Source"];

    /// <summary>The address the web view loads: the help page, or the document's page shown.</summary>
    public string? WebUri => appView.Preview.Web?.DataUri;

    /// <summary>Whether a help page shows its source rather than the rendered page (kept for the session).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HelpModeIndex), nameof(ShowsHelpRendered), nameof(ShowsHelpSource))]
    private bool showHelpSource;

    /// <summary>
    /// Why the window cannot render pages (no web engine, or a window without a native web view); null when it can.
    /// The view sets it the first time it needs the engine.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHelpRendered), nameof(ShowsHelpSource), nameof(HasWebEngineMessage))]
    private string? webEngineMessage;

    /// <summary>The help page's status line: where a hovered link goes, or why a clicked one was not followed.</summary>
    [ObservableProperty]
    private string? helpStatus;

    /// <summary>The switch: 0 Rendered, 1 Source.</summary>
    public int HelpModeIndex
    {
        get => ShowHelpSource ? 1 : 0;
        set => ShowHelpSource = value == 1;
    }

    public bool HasWebEngineMessage => WebEngineMessage is not null;

    /// <summary>Whether the help page shows in the web view.</summary>
    public bool ShowsHelpRendered => appView.Preview.IsWebPage && !ShowHelpSource && WebEngineMessage is null;

    /// <summary>Whether the help page shows as its source: on request, or when there is no web view.</summary>
    public bool ShowsHelpSource => appView.Preview.IsWebPage && (ShowHelpSource || WebEngineMessage is not null);

    /// <summary>
    /// The web view asks to go to <paramref name="url"/>: true to let it (the page itself); otherwise a page or file
    /// of the disk is selected in the tree, and anything else is not followed, the status line saying why.
    /// </summary>
    public bool FollowHelpLink(string url)
    {
        var link = HelpPages.Classify(url);
        if (link.Kind == HelpLinkKind.Page)
        {
            HelpStatus = null;
            return true;
        }

        if (appView.Preview.Document is { } document)
        {
            return FollowDocumentLink(document, url, link);
        }

        if (link.Kind != HelpLinkKind.File || appView.Preview.Help is not { } page)
        {
            HelpStatus = link.Description;
            return false;
        }

        if (HelpPagePreview.Find(HelpPagePreview.RootOf(page.Node), link.Path!) is not { } target)
        {
            HelpStatus = "Not on the disk: " + string.Join(':', link.Path!);
            return false;
        }

        for (var at = target.Parent; at is not null; at = at.Parent)
        {
            at.IsExpanded = true;
        }

        HelpStatus = null;
        appSelection.Selected = target;
        return false;
    }

    // A document's links turn its pages; its Back pictures (the HTML's history.back()) go back; nothing else is followed.
    private bool FollowDocumentLink(DocumentWebPreview document, string url, HelpLink link)
    {
        if (url.StartsWith("javascript:history.back", StringComparison.OrdinalIgnoreCase))
        {
            HelpStatus = null;
            document.BackCommand.Execute(null);
            return false;
        }

        if (link.Kind == HelpLinkKind.File && link.Path is [var page] && document.Open(page, link.Fragment))
        {
            HelpStatus = null;
            return false;
        }

        HelpStatus = link.Kind == HelpLinkKind.File ? "Not in this document: " + string.Join('/', link.Path!) : link.Description;
        return false;
    }

    private DocumentWebPreview? watchedDocument;

    private void OnDocumentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentWebPreview.DataUri))
        {
            OnPropertyChanged(nameof(WebUri));
        }
    }

    /// <summary>The web view's hovered link (null when none): the status line says where it goes.</summary>
    public void HoverHelpLink(string? url) => HelpStatus = string.IsNullOrEmpty(url) ? null : HelpPages.Classify(url).Description;

    internal void OnHelpPreviewChanged()
    {
        if (watchedDocument is not null)
        {
            watchedDocument.PropertyChanged -= OnDocumentChanged;
        }

        watchedDocument = appView.Preview.Document;
        if (watchedDocument is not null)
        {
            watchedDocument.PropertyChanged += OnDocumentChanged;
        }

        OnPropertyChanged(nameof(WebModes));
        OnPropertyChanged(nameof(WebUri));
        HelpStatus = null;
        OnPropertyChanged(nameof(ShowsHelpRendered));
        OnPropertyChanged(nameof(ShowsHelpSource));
    }
}
