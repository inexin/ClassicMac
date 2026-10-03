# Apple Help pages

Mac OS 8.5 to 9.2 show their help in Help Viewer: help books are folders of HTML pages (HTML 3.2 and 4 as authoring
tools of the time wrote them: layout tables, `font` tags, frames) with GIF and JPEG pictures, kept in the System
Folder's `Help` folder and in applications' folders. A page is a `TEXT` file of creator `'hbwr'` (Help Viewer); other
`.htm` and `.html` files are the same kind of document. ClassicMac shows a page in the viewer through the platform's
own web engine, after making it self-contained: the files it refers to are read from the disk and put into the page,
and its links point at files of the disk, so a click selects that file instead of leaving the disk image.

| | |
| --- | --- |
| Identified by | Type `'TEXT'`, creator `'hbwr'`; or type `'TEXT'` and a name ending `.htm` or `.html` |
| ClassicMac | Reads and shows (Rendered and Source); `ClassicMac.Resources.Decoders.Documents.HelpPages` (decoding, URL resolution, the page made ready), the app's `HelpPagePreview` and a NativeWebView |
| Verified against | Mac OS 9.0's `System Folder:Help` (AppleScript Help, Mac Help, QuickTime Help, Apple Help Viewer): 398 pages |
| Sources | HTML 4.01 (W3C); the WHATWG Encoding standard (ISO 8859-1 read as Windows-1252); Apple Help Reference (Apple Help's meta tags and `help:` commands); the pages themselves |

Contents

1. [Layout](#1-layout)
2. [Reading](#2-reading)
3. [Writing](#3-writing)
4. [Variants](#4-variants)
5. [ClassicMac](#5-classicmac)
6. [Diagnostics](#6-diagnostics)
7. [Verification](#7-verification)
8. [Not covered](#8-not-covered)
9. [References](#9-references)

## 1. Layout

### 1.1 The book

A help book is a folder tree on the disk. Pages link to each other and refer to their pictures by relative URLs
('/'-separated names, `..` for the folder above), never by Mac paths [Fitted: Mac OS 9.0's help books]. Mac OS 9's
books keep their pages in sub-folders (`at:pgs:`), their pictures in a shared `gfx` folder, and open on a frameset page
(`atFmSet.htm`) whose frames hold a table of contents and a page [Fitted].

| File | Type / creator | Notes |
| --- | --- | --- |
| A page | `'TEXT'` / `'hbwr'` | HTML; Mac OS 9's pages are named `.htm` [Fitted] |
| A picture | `'GIFf'` or `'JPEG'` | Any creator (`'8BIM'`, `'ogle'` in Mac OS 9) [Fitted] |
| The book's index | `'STOT'` / `'hbwr'` | Help Viewer's search index; not HTML |
| A script | `'osas'` | Run by `help:runscript` links |

### 1.2 Meta tags

In the page's `head`, `meta` tags with a `name` and `content` [Doc: Apple Help Reference] [Fitted]:

| Name | Notes |
| --- | --- |
| `AppleTitle` | The book's title, on its title page |
| `AppleIcon` | The book's icon (a picture of the book) |
| `AppleTarget` | The book's name for `help:` commands that open it |
| `AppleOrder` | Where the book sorts in Help Viewer's list |
| `keywords`, `description` | Words and a summary for Help Viewer's search |
| `ROBOTS` | `NOINDEX`: left out of the search index (frameset pages) |

The pages also carry `http-equiv="content-type"` with a charset; Mac OS 9's say `iso-8859-1` though they are
Mac OS Roman, and hold only ASCII, writing other characters as character references (`&#149;` for a bullet, as
Windows-1252) [Fitted].

### 1.3 Links

| Link | What Help Viewer does |
| --- | --- |
| A relative URL | Opens that page or file of the book [Doc] |
| `#name` | Moves to the anchor in the page [Doc] |
| `help:openbook='Book'`, `help:search=…`, `help:anchor=…` | Help Viewer commands: open a book, search, look an anchor up [Doc: Apple Help Reference] |
| `help:runscript="Book:script" string="…"` | Runs an AppleScript of the book, passing it the string [Doc: Apple Help Reference] |
| `http:`, `mailto:` and others | Hands the URL to the system's helper (a browser, a mail program) [Doc] |

## 2. Reading

1. A file is a page when §1's identification holds [ClassicMac].
2. The page's text: its charset is the one a `meta` tag of its first 1,024 bytes declares (`charset=` in a
   `content-type` or a `<meta charset>`). UTF-8 is read as UTF-8; ISO 8859-1, Windows-1252 and US-ASCII as Windows-1252
   (a byte $80–$9F is Windows-1252's character, as browsers read them) [Reference: WHATWG Encoding]; anything else,
   and no charset, as Mac OS Roman [ClassicMac].
3. The title: the `AppleTitle` meta tag, else the `title` element (character references decoded, white space
   collapsed), else the file's name [ClassicMac].
4. A relative URL resolves against the page's folder, as a path from the volume's root [ClassicMac]: the query and
   fragment are dropped; the path splits at '/'; empty names and "." are skipped; ".." goes up (above the root, the URL
   is not a file of the disk); a leading '/' starts at the root. Each name's `%XX` escapes are bytes, read as UTF-8 when
   they are valid UTF-8, else as Mac OS Roman, so a name may hold a '/' as `%2F`. Names compare ignoring case, as HFS
   compares them.

## 3. Writing

None. ClassicMac does not write help pages; it makes a page ready for its viewer (§5.1).

## 4. Variants

Mac OS X's Help Viewer (Apple Help 1.2 and later) reads the same pages, with more meta tags (`AppleFont`, …) and
`help:` commands; they are not covered. Mac OS 8.5–9's Help Viewer renders with Apple's HTML Rendering Library; the
platform engines ClassicMac uses do not render exactly as it did.

## 5. ClassicMac

### 5.1 The page made ready

`HelpPages.Render` turns the page's text into one self-contained document for the web view [ClassicMac]:

1. A `Content-Security-Policy` meta tag goes right after `<head>` (at the start, without a `head`): only `data:`
   content, inline styles, no scripts.
2. `base` tags are dropped (every URL is made absolute or inline instead).
3. A stylesheet (`link rel=stylesheet`) becomes a `style` element with the file's text.
4. A picture (`img src`, `input src`, the `background` of `body`, `table`, `tr`, `td`, `th`) becomes a `data:` URI:
   GIF, JPEG and PNG files by their signature, as they are; a `'PICT'` file drawn to PNG
   ([pict.md](../graphics/pict.md)). A file that is missing, or of another kind, gives an empty URL.
5. A frame (`frame src`, `iframe src`) becomes a `data:` URI of its page, made ready the same way against its own
   folder (two levels of frames at most).
6. A link (`a href`, `area href`) to a file of the disk becomes `https://help.classicmac.invalid/` and the file's
   path from the volume's root (each name escaped), keeping its fragment; other links keep their URL; `#name` links stay
   as they are. Every link but those gets `target="_top"` (any other target removed), so a click in a frame, or one
   asking for a new window, is the page's own navigation, which the viewer sees.
7. The result is loaded as `data:text/html;charset=utf-8;base64,…`.

A page, and each file it reads, is at most 16 MB.

### 5.2 The viewer

- The page shows in a NativeWebView (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux) with JavaScript off,
  private mode, no developer tools and no status bar; Rendered | Source switches to the decoded text.
- Every navigation the web view starts is asked about: the page's own `data:` URI loads; a link to a file of the disk
  is not followed but selects that file in the tree (opening the folders above it), which previews it; a missing file,
  a `help:` command, a script and an outside address are not followed, the status line saying why. Hovering a link
  shows where it goes.
- Without a web engine (or in a window with no native handle), the page shows as its source, with why.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `help.missing-file` | Warning | A picture, stylesheet or frame the page refers to is not on the disk | Leaves it out (an empty URL) | Shows a broken picture or an empty frame |
| `help.undrawable-picture` | Warning | A `'PICT'` file the page shows cannot be drawn | Leaves it out | Not traced |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/HelpPageTests.cs`: identification, the charset rules, meta tags and the
  title, URL resolution (escapes, Mac OS Roman bytes, `%2F`, `..` above the root), link classification, the page made
  ready (pictures, a PICT file, stylesheets, `base`, links and targets, frames, the policy), the `data:` URI.
- `tests/ClassicMac.App.Tests/HelpPageViewTests.cs`: an HFS fixture with a page, a picture and a linked page; the
  preview, a link selecting its page, the status line, Rendered | Source, the fallback without a web view.
- `tests/ClassicMac.App.Tests/HelpCorpusTests.cs`, run when `CLASSICMAC_HELP_CORPUS` names a Mac OS 9.0 startup disk
  image: its 398 pages (386 of Help Viewer) are made ready; 513 pictures go inline and 27 referred files are missing
  (frames Help Viewer makes itself, such as `toc.htm`).
- Checked by eye in the app on Windows (WebView2): AppleScript Help's pages render with their pictures, and a link to
  another page selects it in the tree. The rendered page cannot be compared headless (no web engine in the test host).

## 8. Not covered

- `url(…)` references inside stylesheets and `style` attributes, `embed` (QuickTime movies), `object` and `applet`:
  not resolved.
- `help:` commands are not carried out (no books are opened, searched or run), and scripts never run.
- Help Viewer's own pages (the `toc.htm` a book's frameset names, built from its index) and its search index
  (`'STOT'`).
- Rendering is the platform engine's, not Help Viewer's.
- The CLI's `convert` and `extract` leave help pages as the files they are; making them self-contained there is left
  for later.

## 9. References

1. HTML 4.01 Specification, W3C, 1999.
2. Encoding Standard, WHATWG: the labels and the Windows-1252 index.
3. Apple Help Reference and *Providing User Assistance With Apple Help*, Apple Computer, 1999–2002.
4. NativeWebView, Wiesław Šoltés, MIT: the Avalonia control hosting the platform web engines (behaviour only).
