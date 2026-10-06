# Changelog

What each release brings, in brief. The detail of every change is in the git history.

## Unreleased

First release of ClassicMac: libraries, a command line and a desktop app for classic Mac OS files.

**Reaching the files**
- Resource forks read and written as the Resource Manager does, with the System's compressed resources (`dcmp` 0–3).
- Wrappers and host folders: AppleSingle, AppleDouble, MacBinary I–III, BinHex, uuencode, PC Exchange, Basilisk II;
  read and written.
- Archives: StuffIt 1–5 and segmented archives, Compact Pro, DiskDoubler, PackIt, LHA, zip, tar and gzip with Mac
  data, self-extracting archives.
- Disk images: Disk Copy 4.2, NDIF (also written, segmented on request), DART, UDIF, partition maps, raw CDs and cue
  sheets, DiskDup+, ROM images.
- File systems: HFS, HFS Plus and HFSX (plain, wrapped, journaled), MFS, ISO 9660 and High Sierra, FAT.
- Any nesting unwrapped to one tree, with limits that guard against hostile input; names in every Mac text encoding.

**Writing and repairing**
- HFS volumes edited in place or into a new image: files and folders added, deleted, renamed, moved, locked; Finder
  info; blessing; new volumes, resizing and defragmenting; every save verified before it is kept.
- First Aid for HFS, HFS Plus and HFSX: checks by stage as Disk First Aid does, and repairs, checked live against
  Disk First Aid 8.5.5.

**Decoding**
- QuickDraw, drawing exactly what a Mac draws (Mac OS 9 or the 68k ROM, 1–32 bit screens): PICT read and written,
  QuickTime images and their codecs, MacPaint; ImageSharp and SkiaSharp adapters.
- Icons, cursors, patterns and colour tables; sounds (MACE, IMA4, µ-law, AIFF) to WAV; text and styled text to UTF-8
  and RTF; bitmap and TrueType fonts (made loadable on request); dialogs, menus, windows and Finder windows drawn as
  the Mac drew them; DOCMaker, SimpleText and Word documents and help pages to HTML; PNG or lossless WebP images.
- Code: PEF and `cfrg`, 68k applications and code resources, 68k and PowerPC disassembly to annotated listings.

**Command line** (`classicmac`)
- `info`, `list`, `unpack`, `extract`, `convert`, `disasm`, `pack`; Mac paths through any container (`ls`, `stat`,
  `cat`, `find`, `get`); `derez` and `rez`; the write commands, `check` and `repair`, `format`, `resize`, `defrag`,
  `ndif`; an MCP server and a DOS-like shell; `help` with examples for every command.

**Desktop app** (Windows, macOS, Linux)
- Browse disks, archives and files in one tree; previews of pictures, sounds, text, fonts, dialogs, menus, Finder
  windows, documents, code and hex; export; resource editing with undo, typed forms and templates; image and sound
  import; Save As in every wrapper; the Volume menu with First Aid, Defragment and Resize.

**Releases**
- Self-contained downloads for Windows, macOS and Linux, and NuGet packages, built from a version tag: `ClassicMac.Formats`
  (resource forks, containers, file systems, decoders, code), `ClassicMac.Core`, `ClassicMac.Graphics` and its
  ImageSharp and SkiaSharp adapters, and the `ClassicMac.Cli` tool.

## QuickDraw.Pict (before it moved here)

ClassicMac.Graphics was QuickDraw.Pict until 2026-09-29; its history came along with it.

### 0.1.0 — 2026-09-27

First release of QuickDraw.Pict and QuickDraw.Pict.ImageSharp: PICT pictures (version 1, 2 and extended 2) decoded by
a software QuickDraw (Mac OS 9 or the 68k ROM, screen depths), `PictWriter`, QuickTime images, QTIF and MacPaint
files, icons, cursors and patterns, and the ImageSharp format plugin.
