using System.Text;
using ClassicMac.Core;
using ClassicMac.Resources.Decoders.Documents;

namespace ClassicMac.Resources.Decoders.Tests;

// Apple Help pages (docs/formats/resources/help-pages.md): the page's bytes decoded, its links and pictures resolved
// against its help book's folder, and the page made ready for a web view (pictures and stylesheets inline, links absolute).
public class HelpPageTests
{
    private static readonly string[] Folder = ["System Folder", "Help", "AppleScript Help", "at", "pgs"];

    [Theory]
    [InlineData("TEXT", "hbwr", "a", true)]
    [InlineData("TEXT", "ttxt", "Read Me.htm", true)]
    [InlineData("TEXT", "ttxt", "INDEX.HTML", true)]
    [InlineData("TEXT", "ttxt", "Read Me", false)]
    [InlineData("STOT", "hbwr", "a", false)]
    [InlineData("GIFf", "8BIM", "a.gif", false)]
    public void Help_pages_are_hbwr_text_or_html_files(string type, string creator, string name, bool page) =>
        Assert.Equal(page, HelpPages.IsHelpPage(FourCC.FromString(type), FourCC.FromString(creator), name));

    [Fact]
    public void Pages_are_Mac_OS_Roman_unless_they_declare_a_charset()
    {
        Assert.Equal("é•", HelpPages.Decode([0x8E, 0xA5]));
        Assert.Null(HelpPages.Charset([0x8E]));
        byte[] latin = [.. "<META HTTP-EQUIV=\"content-type\" CONTENT=\"text/html;charset=iso-8859-1\">"u8, 0xE9, 0x95];
        Assert.Equal("iso-8859-1", HelpPages.Charset(latin));
        Assert.EndsWith("é•", HelpPages.Decode(latin));                                // ISO 8859-1 read as Windows-1252, as browsers do
        byte[] utf8 = [.. "<meta charset='UTF-8'>"u8, .. Encoding.UTF8.GetBytes("é")];
        Assert.Equal("utf-8", HelpPages.Charset(utf8));
        Assert.EndsWith("é", HelpPages.Decode(utf8));
        byte[] mac = [.. "<meta http-equiv=content-type content=\"text/html; charset=x-mac-roman\">"u8, 0x8E];
        Assert.EndsWith("é", HelpPages.Decode(mac));
        byte[] unknown = [.. "<meta charset=\"klingon\">"u8, 0x8E];
        Assert.EndsWith("é", HelpPages.Decode(unknown));                              // an unknown charset: Mac OS Roman
    }

    [Fact]
    public void The_title_and_meta_tags_come_from_the_head()
    {
        const string html = """
            <HTML><HEAD><TITLE> Using
             Record </TITLE><META NAME="AppleTitle" CONTENT="Recording a script"><META NAME="keywords" CONTENT="record, script">
            <meta http-equiv="content-type" content="text/html">
            </HEAD><BODY>Hi</BODY></HTML>
            """;
        Assert.Equal([new("AppleTitle", "Recording a script"), new("keywords", "record, script"), new KeyValuePair<string, string>("content-type", "text/html")],
            HelpPages.Meta(html));
        Assert.Equal("Recording a script", HelpPages.Title(html));
        Assert.Equal("Using Record", HelpPages.Title("<title> Using\r\n Record </title>"));
        Assert.Null(HelpPages.Title("<p>no title"));
    }

    [Theory]
    [InlineData("at10.htm", "System Folder/Help/AppleScript Help/at/pgs/at10.htm")]
    [InlineData("../../gfx/asik.gif", "System Folder/Help/AppleScript Help/gfx/asik.gif")]
    [InlineData("./a%20b.htm#step", "System Folder/Help/AppleScript Help/at/pgs/a b.htm")]
    [InlineData("x.htm?q=1", "System Folder/Help/AppleScript Help/at/pgs/x.htm")]
    [InlineData("/Applications/Read Me", "Applications/Read Me")]
    [InlineData("caf%8E.htm", "System Folder/Help/AppleScript Help/at/pgs/café.htm")]   // not UTF-8: Mac OS Roman bytes
    [InlineData("caf%C3%A9.htm", "System Folder/Help/AppleScript Help/at/pgs/café.htm")]
    [InlineData("a%2Fb.htm", "System Folder/Help/AppleScript Help/at/pgs/a/b.htm")]    // '/' in a name
    public void Relative_URLs_resolve_against_the_page_s_folder(string url, string path)
    {
        var resolved = HelpPages.Resolve(Folder, url)!;
        Assert.Equal(path, string.Join("/", resolved.Take(resolved.Count - 1)) + "/" + resolved[^1]);
    }

    [Theory]
    [InlineData("../../../../../../x.htm")]                                              // above the volume
    [InlineData("help:openbook='Mac Help'")]
    [InlineData("http://www.apple.com/")]
    [InlineData("#top")]
    [InlineData("")]
    public void Other_URLs_are_not_files_of_the_volume(string url) => Assert.Null(HelpPages.Resolve(Folder, url));

    [Theory]
    [InlineData("https://help.classicmac.invalid/System%20Folder/Help/a%2Fb.htm#x", HelpLinkKind.File, "System Folder|Help|a/b.htm", "x")]
    [InlineData("help:runscript=\"AppleScript%20Help:shrd:openSampleScript\" string=\"Help:x\"", HelpLinkKind.Script, null, null)]
    [InlineData("help:openbook='Mac Help'", HelpLinkKind.Help, null, null)]
    [InlineData("http://www.apple.com/", HelpLinkKind.External, null, null)]
    [InlineData("mailto:a@b.c", HelpLinkKind.External, null, null)]
    [InlineData("javascript:alert(1)", HelpLinkKind.Script, null, null)]
    [InlineData("data:text/html;base64,AA==", HelpLinkKind.Page, null, null)]
    [InlineData("about:blank", HelpLinkKind.Page, null, null)]
    public void Links_are_classified(string url, HelpLinkKind kind, string? path, string? fragment)
    {
        var link = HelpPages.Classify(url);
        Assert.Equal((kind, path, fragment), (link.Kind, link.Path is { } p ? string.Join("|", p) : null, link.Fragment));
        Assert.False(string.IsNullOrEmpty(link.Description));
    }

    [Fact]
    public void Link_descriptions_say_why_a_link_is_not_followed()
    {
        Assert.Equal("Runs an AppleScript in Help Viewer (not run here)", HelpPages.Classify("help:runscript=\"x\"").Description);
        Assert.Equal("A Help Viewer command (not followed here): help:openbook='Mac Help'", HelpPages.Classify("help:openbook='Mac Help'").Description);
        Assert.Equal("An address outside the disk (not followed): http://www.apple.com/", HelpPages.Classify("http://www.apple.com/").Description);
        Assert.Equal("Opens System Folder:Help:a.htm", HelpPages.Classify("https://help.classicmac.invalid/System%20Folder/Help/a.htm").Description);
    }

    // A tiny GIF and its data URI.
    private static readonly byte[] Gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0];

    [Fact]
    public void Rendering_inlines_pictures_and_makes_links_absolute()
    {
        var asked = new List<string>();
        HelpFile? Resolve(IReadOnlyList<string> path)
        {
            asked.Add(string.Join("/", path));
            return path[^1] switch
            {
                "asik.gif" => new HelpFile(Gif, FourCC.FromString("GIFf")),
                "photo" => new HelpFile(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, FourCC.FromString("JPEG")),
                "style.css" => new HelpFile("body { color: red }"u8.ToArray(), FourCC.FromString("TEXT")),
                _ => null,
            };
        }

        var diagnostics = new List<Diagnostic>();
        var html = HelpPages.Render("""
            <HTML><HEAD><BASE HREF="file:///elsewhere/"><LINK REL="stylesheet" HREF="style.css"><TITLE>T</TITLE></HEAD>
            <BODY BACKGROUND="../../gfx/asik.gif"><IMG SRC="../../gfx/asik.gif" WIDTH=16><img src='photo'><img src="gone.gif">
            <A HREF="at20.htm#s">next</A> <a href="#top">top</a> <A HREF='help:runscript="x"' TARGET="_blank">run</A> <a href="http://apple.com/">web</a>
            <script>alert(1)</script></BODY></HTML>
            """, Folder, Resolve, diagnostics: diagnostics);

        const string gifUri = "data:image/gif;base64,R0lGODlhAQABAAAAAA==";
        Assert.Contains($"""<IMG SRC="{gifUri}" WIDTH=16>""", html);
        Assert.Contains($"""<BODY BACKGROUND="{gifUri}">""", html);
        Assert.Contains("""<img src='data:image/jpeg;base64,/9j/4A=='>""", html);
        Assert.Contains("""<img src="">""", html);                                       // missing: nothing fetched
        Assert.Contains("<style>body { color: red }</style>", html);
        Assert.DoesNotContain("<LINK", html);
        Assert.DoesNotContain("BASE HREF", html);
        Assert.Contains("""<A HREF="https://help.classicmac.invalid/System%20Folder/Help/AppleScript%20Help/at/pgs/at20.htm#s" target="_top">next</A>""", html);
        Assert.Contains("""<a href="#top">top</a>""", html);                             // in the page: kept
        Assert.Contains("""<A HREF='help:runscript="x"' target="_top">run</A>""", html);  // a new window becomes the page's own
        Assert.Contains("""<a href="http://apple.com/" target="_top">web</a>""", html);
        Assert.Contains("""<meta http-equiv="Content-Security-Policy" content="default-src data: 'unsafe-inline'; script-src 'none'">""", html);
        Assert.Equal(["System Folder/Help/AppleScript Help/at/pgs/style.css", "System Folder/Help/AppleScript Help/gfx/asik.gif",
            "System Folder/Help/AppleScript Help/gfx/asik.gif", "System Folder/Help/AppleScript Help/at/pgs/photo", "System Folder/Help/AppleScript Help/at/pgs/gone.gif"],
            asked);
        Assert.Equal(["help.missing-file"], diagnostics.Select(d => d.Code));
        Assert.Contains("gone.gif", diagnostics[0].Message);
    }

    // CSS pictures (url(...)) are put in as data: URIs too: in a linked stylesheet (relative to the stylesheet's own
    // folder), in a <style> element and in a style attribute; addresses outside the disk and data: URIs are kept.
    [Fact]
    public void Pictures_in_CSS_are_inlined()
    {
        var asked = new List<string>();
        HelpFile? Resolve(IReadOnlyList<string> path)
        {
            asked.Add(string.Join("/", path));
            return path[^1] switch
            {
                "dot.gif" => new HelpFile(Gif, FourCC.FromString("GIFf")),
                "sheet.css" => new HelpFile("body { background: url(img/dot.gif) } h1 { background: url('http://x/y.gif') }"u8.ToArray(), FourCC.FromString("TEXT")),
                _ => null,
            };
        }

        var html = HelpPages.Render("""
            <HTML><HEAD><LINK REL="stylesheet" HREF="../css/sheet.css">
            <STYLE>p { background-image: URL( "dot.gif" ) } q { background: url(data:image/png;base64,AA==) }</STYLE></HEAD>
            <BODY><DIV STYLE="background: url('dot.gif')">x</DIV></BODY></HTML>
            """, Folder, Resolve);

        const string gifUri = "data:image/gif;base64,R0lGODlhAQABAAAAAA==";
        Assert.Contains($"<style>body {{ background: url(\"{gifUri}\") }} h1 {{ background: url('http://x/y.gif') }}</style>", html);
        Assert.Contains($"p {{ background-image: url(\"{gifUri}\") }}", html);
        Assert.Contains("q { background: url(data:image/png;base64,AA==) }", html);
        Assert.Contains($"""<DIV STYLE="background: url(&quot;{gifUri}&quot;)">""", html);
        Assert.Contains("System Folder/Help/AppleScript Help/at/css/img/dot.gif", asked);       // the stylesheet's folder
        Assert.Contains("System Folder/Help/AppleScript Help/at/pgs/dot.gif", asked);           // the page's folder
    }

    [Fact]
    public void A_frameset_s_frames_are_rendered_inline()
    {
        HelpFile? Resolve(IReadOnlyList<string> path) => path[^1] switch
        {
            "toc.htm" => new HelpFile(MacRoman.Encode("<a href=\"at10.htm\">Contents</a>"), FourCC.FromString("TEXT")),
            _ => null,
        };

        var html = HelpPages.Render("""<FRAMESET COLS="190,50%"><FRAME SRC="../toc.htm" NAME="_left"><FRAME SRC="gone.htm"></FRAMESET>""",
            Folder, Resolve);
        var start = html.IndexOf("SRC=\"data:text/html;charset=utf-8;base64,", StringComparison.Ordinal) + 41;
        var frame = Encoding.UTF8.GetString(Convert.FromBase64String(html[start..html.IndexOf('"', start)]));
        // The frame's own links resolve against its own folder.
        Assert.Contains("""<a href="https://help.classicmac.invalid/System%20Folder/Help/AppleScript%20Help/at/at10.htm" target="_top">Contents</a>""", frame);
        Assert.Contains("""<FRAME SRC="">""", html);
    }

    [Fact]
    public void The_page_is_a_data_URI()
    {
        var uri = HelpPages.DataUri("<p>é</p>");
        Assert.Equal("data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>é</p>")), uri);
    }

    [Fact]
    public void PICT_files_are_drawn_to_PNG()
    {
        // A PICT file: a 512-byte header, then a version 1 picture of a 1 × 1 frame that only ends.
        byte[] pict = [.. new byte[512], 0, 10, 0, 0, 0, 0, 0, 1, 0, 1, 0x11, 0x01, 0xFF];
        var html = HelpPages.Render("<img src=\"p\">", Folder, _ => new HelpFile(pict, FourCC.FromString("PICT")));
        Assert.Contains("<img src=\"data:image/png;base64,iVBORw0KGgo", html);
    }
}
