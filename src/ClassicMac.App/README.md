# ClassicMac (desktop app)

A viewer and editor for classic Mac OS files on Windows, macOS and Linux, built with Avalonia.

```
dotnet run --project src/ClassicMac.App -- "some disk.img"
```

Open a file from the command line, the File menu, the Recent list or by dropping it on the window. Disk images,
archives and wrappers open as a tree, nested to any depth.

## What it does

- **Browse** volumes, folders, files and resources in one tree, with Finder kinds, type and creator, sizes and
  aliases resolved; type to jump to a name.
- **Preview** pictures, icons, cursors and patterns; sounds, with playback; text and styled text; bitmap and
  TrueType fonts; dialogs, alerts, menus, windows and controls as the Mac drew them; Finder windows; documents and
  help pages as HTML; code as listings; anything else in the hex view.
- **Export** files, resources and documents to modern formats (PNG or WebP, WAV, UTF-8, RTF, HTML, TrueType),
  with a manifest that `classicmac pack` can rebuild a fork from.
- **Edit** resources with undo: typed forms for common types, `TMPL` templates (a file's own win over ClassicMac's),
  the hex editor, image and sound import.
- **Save as** MacBinary III, BinHex 4.0, AppleSingle, an AppleDouble pair, a Basilisk II folder entry, an HFS volume
  image or the resource fork alone.
- **Volumes**: new files and folders, imports and deletes on HFS images; First Aid, Defragment and Resize from the
  Volume menu, with the volume's allocation map.

Saves are written aside, read back and verified before they replace anything; the first save over an original
keeps a `.orig` copy.

## Settings

View ▸ Theme, Text Encoding and Type/Creator Database, and the Export menu's image format and loadable fonts, last
between sessions in `settings.json` in the user's application-data folder (`ClassicMac/`).

## Icon

The icon's files and where each goes are in [design/icon](../../design/icon/README.md). The app carries
`Assets/icon/classicmac.ico` and the PNGs it draws itself: the title bar's pixel version by display scaling (16, 20,
24 or 32 px, never resampled) and the About box's 128 px.

## Development

- Views are Avalonia XAML; view models use CommunityToolkit.Mvvm. Sound plays through SoundFlow, HTML through
  NativeWebView.
- The design brief and tokens are in [design/](../../design/APP-DESIGN-BRIEF.md).
- `tests/ClassicMac.App.Tests` runs the app headless; on Windows its screenshots are compared with
  `tests/golden/app` (`CLASSICMAC_UPDATE_BASELINES=1` rewrites them after you have looked at the diffs).
