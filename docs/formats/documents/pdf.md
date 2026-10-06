# PDF documents

Adobe's Portable Document Format, a page description language and file format. On the Mac, Acrobat and Acrobat Reader
made and read PDFs, and Mac OS X made them its native format; classic Mac OS volumes, archives and CDs carry them as
manuals and read-me files. ClassicMac does not read PDF itself: it recognises a PDF by its header, writes it out as it
is (PDF is read everywhere today), and the app shows it in the platform's web view.

| | |
| --- | --- |
| Identified by | Type `'PDF '` (creator `'CARO'` for Acrobat); the header `%PDF-` and a version within the first 1024 bytes of the data fork; extension `.pdf` |
| ClassicMac | Recognises and writes out; `ClassicMac.Resources.Decoders.Documents` (`PdfDocuments`, the `document.pdf` document converter); the app's preview (`PdfPreview`) |
| Verified against | Nothing yet |
| Sources | Adobe, *PDF Reference*, sixth edition (version 1.7), §3.4.1 and Appendix H; ISO 32000-1:2008 §7.5.2 |

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

The data fork holds the whole document; there is no resource fork content. Only the header is read here.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 5 | Marker | `%PDF-` [Author] |
| +$05 | varies | Version | Decimal digits, `.`, decimal digits (`1.3`, `1.7`, `2.0`), ending the first line [Author] |

The rest (objects, cross-reference table, trailer, `%%EOF`) is the format's own and is not read.

## 2. Reading

1. Read the first 1024 bytes of the data fork.
2. Find `%PDF-` followed by a version (digits, `.`, digits) lying wholly within them. The header normally starts the
   file [Author], but Acrobat accepts it anywhere in the first 1024 bytes, after other bytes such as a MacBinary-era
   prefix [Author: PDF Reference Appendix H].
3. If there is none, the file is not a PDF, whatever its type.

## 3. Writing

None. A PDF is written out as its data fork, unchanged.

## 4. Variants

PDF versions 1.0 (Acrobat 1, 1993) to 1.7 and 2.0 differ in their content, not in the header [Author]. Mac OS 9 and the
68k ROM have no PDF code; nothing differs between them.

## 5. ClassicMac

- **Conversion** (`convert`, the app's Convert Documents): the `document.pdf` converter, version 1, writes a file of
  type `'PDF '` whose header §2 finds as `document.pdf`, its data fork byte for byte. Files of other types are not
  read by it. [ClassicMac]
- **Preview** (the app): a file whose data fork has the header, whatever its type (files from zip archives, ISO
  9660 discs and FAT disks often have none), goes to the web view as a `data:application/pdf` URI, where the engine's
  own viewer shows it: WebView2 on Windows, WKWebView on macOS. WebKitGTK on Linux has no PDF viewer, so there a PDF
  has no preview and stays in Hex. Files larger than 64 MiB stay in Hex. The Info view gives the version and size.
  [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `pdf.no-header` | Warning | a file of type `'PDF '` has no header in its first 1024 bytes | writes no `document.pdf` | no Mac counterpart |
| `pdf.unreadable` | Warning | the data fork cannot be read | writes no `document.pdf` | no Mac counterpart |

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/PdfDocumentTests.cs`: the header and its 1024-byte window, the
  conversion, a typed file without the header, other types left alone.
- `tests/ClassicMac.App.Tests/PdfPreviewTests.cs`: the preview by type and by header, and a file that is not one.

## 8. Not covered

- Reading a PDF's content: pages, text, metadata, encryption.
- A PDF preview on Linux (WebKitGTK has no viewer; pdf.js could be bundled).

## 9. References

- Adobe Systems, *PDF Reference*, sixth edition, version 1.7 (2006): §3.4.1 File Header; Appendix H, implementation
  notes.
- ISO 32000-1:2008, *Document management — Portable document format — Part 1: PDF 1.7*, §7.5.2 File Header.
