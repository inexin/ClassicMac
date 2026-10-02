# ClassicMac: app design brief

## What the app is

ClassicMac is a desktop app (Windows first; macOS and Linux possible) for opening, inspecting, converting and
editing files from the classic Macintosh (System 1 to Mac OS 9). Users drop in an old Mac file, a disk image or an
archive. The app shows what is inside, down to individual resources, and draws them exactly as a Macintosh would:
icons, pictures, dialogs, menus, sounds, fonts, styled text and code. They can then export, convert or edit them and
save the result in a Mac-compatible format.

**Users**
- Retro-computing enthusiasts and game preservationists (e.g. Realmz scenario authors).
- Emulator users (SheepShaver, Basilisk II) moving files in and out of disk images.
- Archivists and developers extracting assets or code.

**Tone:** a precise tool for experts that loves the classic Mac. The content is often pixel art and must be shown
pixel-exact. The chrome can borrow from Mac OS 8/9 "Platinum", or stay modern and neutral to frame the content; that
is the design question.

## Core workflow

1. **Open:** File ▸ Open, drag a file onto the window, or pass it on the command line. Several inputs can be open at
   once.
2. **Browse:** a tree, from the input down to resources. Example: `Mac OS 9.hfv` › `System Folder` › `Finder` ›
   `'ICN#' (12)` › `128 "Trash"`.
3. **Inspect** the selection in the right-hand area: details, preview, hex, or an edit form.
4. **Act:**
   - Export (save one resource, export all, extract everything, convert documents, unpack to AppleDouble or Basilisk
     II folders).
   - Drag files out.
   - Edit resources, with undo.
   - Create or delete files and folders in HFS volumes.
   - Save in a Mac container format.
5. **Diagnostics:** every problem found while reading appears in a list. Clicking one jumps to the item it is about.

Big inputs are common: a 500 MB disk with about 5,000 files, and archives inside it holding thousands more. Nested
containers (archives and disk images on a disk) open when their node is expanded.

## Information architecture and components

### Window

A single main window. From top to bottom:
- the menu bar;
- the tree on the left and the inspector on the right, with resizable splitters between them;
- the diagnostics panel;
- the status bar.

A window is about 1200×780 by default and at least 700×450.

### Menus and commands

| Menu | Commands |
| --- | --- |
| File | Open (Ctrl+O), Close (Ctrl+W), Save (Ctrl+S), Save As ▸ (MacBinary III, BinHex 4.0, AppleSingle, AppleDouble pair, Basilisk II entry, HFS volume image, resource fork only), Revert, Quit |
| Edit | Undo and Redo, with titles such as "Undo Edit 'STR ' 128" |
| Resource | New Resource (Ctrl+K), Duplicate (Ctrl+D), Delete, Get Info (Ctrl+I), Edit Hex (Ctrl+H), Replace Data from File, Import Image or Sound |
| Volume | New File, Import File, New Folder, Delete File or Folder (plain HFS images only) |
| Export | Save Resource As (Ctrl+E), Export Resources, Extract All Resources, Convert Documents, Unpack (AppleDouble), Unpack (Basilisk II), and a "Drag Files Out as MacBinary" check box |

The tree's context menu repeats the Resource, Volume and Export commands.

Missing so far:
- a toolbar;
- View, Window and Help menus, and an About box;
- recent files and preferences;
- search or filter in the tree;
- Cut, Copy and Paste.

### Browse tree

**Node kinds**, each with its own icon:
- **Input:** the opened file.
- **Container:** a disk image or archive inside the input, titled "name (format)".
- **Folder**
- **File:** a Mac file, with its type and creator.
- **Resource type:** e.g. `'PICT' (24)`.
- **Resource:** e.g. `128 "Title"`.
- **Loading…:** a placeholder while children are read.

**States**
- Expanded or collapsed.
- Loading; children are read off the UI thread.
- Unsaved edits, marked by " •" after the title.
- Selected.
- Drag source: files, containers and resources drag out to the desktop.
- An unread container, whose contents load when it is expanded.

Today the icons are 14 px monochrome vector glyphs. Better ones are welcome, for example Finder-like icons per kind.
The tree could also show a file's own Mac icon, since the app can render it.

### Inspector

Tabs: **Details | Preview | Hex | Edit**. The Edit tab appears only for editable resources. The inspector switches to
Preview automatically when there is one.

- **Details:** a heading, then label/value rows (selectable values).
  - Input: path, how it was read, companion files, contents.
  - File: Mac path, type and creator, Finder flags, dates, fork sizes, resource count.
  - Resource: type, ID, name, attributes, size, compression.
- **Preview**, one view per kind:
  - **Image:** pictures, icons (all sizes and depths), cursors with their hotspots, patterns, palettes as swatch grids,
    and bitmap fonts as glyph sheets.
    - Several images wrap in a grid on a checkerboard, each with a caption.
    - Integer zoom (1×, 2×, 4×, 8×) with nearest-neighbour scaling, and a screen-depth picker (1–32 bit).
    - Icon families also get Finder-state strips (selected, disabled, offline, open, the 7 label colours).
  - **Text:** styled Mac text with fonts and colours; plain monospace for string lists and code listings.
  - **Structured data:** pretty JSON for version, window, control and menu-bar resources and others; a nicer property
    view would be welcome.
  - **Sound:** Play and Stop, a details line (rate, channels, bits, length, loop), and a waveform per channel.
  - **Document:** DOCMaker and SimpleText documents. One chapter at a time, with Back and a chapter picker; text and
    pictures at 72 dpi; clickable links.
  - **Dialog and alert:** drawn exactly as Mac OS 9 Platinum draws them (a rendered bitmap), zoomable.
  - **Menu:** a pulled-down classic menu, with marks, ⌘ keys, submenus, dividers and disabled items.
  - Empty, loading and "no preview" states.
- **Hex:**
  - A source picker: data fork, resource fork or the resource.
  - A virtualised view of millions of lines: offset, hex, and Mac Roman text.
  - Edit mode: a cursor byte, insert or overwrite, keyboard navigation, Apply and Discard.
- **Edit:** a form per resource kind, with Apply (an undoable edit), an error line and a hint.
  - **Simple forms:**
    - Strings and string lists (reorder, add, remove).
    - Text.
    - Version.
    - Window, alert and control.
  - **Editors with a live preview beside the form**, redrawn on every change:
    - dialog item lists, with a per-item kind, rectangle and text;
    - menus, with per-item text, key, mark, icon and style.
  - **Template form:** a generic ResEdit-style form driven by a `TMPL` resource. It has scalar rows and repeating
    lists (add, insert, remove).

### Diagnostics panel

- A filter: All / Warnings and errors / Errors.
- Columns: severity, source path, code, message.
- Clicking a row selects the node it is about.
- Real inputs can produce dozens of entries, including many informational ones. The list needs clear severity icons
  and colours, column headers, grouping by file, and a way to collapse the panel. Most of these are missing today.

### Status bar

One line: what was opened (file and diagnostic counts), progress ("Extracting 120 of 4,000 files…"), and errors.

### Dialogs (modal, small)

- Get Info / New Resource: type, ID, name, and attribute check boxes.
- Import Image or Sound: what to make (PICT, cicn, icon kinds, cursor, icon family, sound), ID, name.
- New File / Import File (name, type, creator) and New Folder.
- Unsaved changes (Save / Don't Save / Cancel) and Yes/No confirmations.
- System pickers for Open, Save and folder.

## Constraints from the content

- **Pixel-exact:** Mac content must never be smoothed. Integer zoom only, nearest-neighbour, no fractional scaling of
  previews.
- **Many pixel images at once:** icon families, cursor sets and font sheets. Grids must handle hundreds of small items.
- **Dark mode:** the app follows the OS theme. Mac content keeps its own colours (white dialogs, black text), so it
  needs a neutral frame in dark mode. Some colours are hard-coded today (checkerboard, waveform, form notes, hex
  cursor).
- **Fonts:** Apple fonts (Chicago, Charcoal, Geneva, Monaco) may not be bundled. The app uses them only when the user
  has them installed or when they are inside the files opened. A Platinum-style chrome needs open-licensed
  substitutes; the rendered Mac content does not.
- **Big trees:** thousands of nodes, loaded lazily, with keyboard navigation and possibly type-ahead search.

## UI framework: Avalonia 12 (with the Fluent theme)

**What it gives us**
- Cross-platform (Windows, macOS, Linux) from one XAML/C# codebase. Everything is drawn with Skia, not native
  controls, so the look is fully customisable.
- Full control templates and styles (CSS-like selectors). A complete custom look, such as a faithful Platinum skin,
  is possible.
- Light and dark theme variants, theme resources, animations and transitions.
- Virtualised lists and trees, a split view, tabs, menus, context menus and flyouts.
- Custom-drawn controls: the app already draws waveforms, dialogs, menus and documents itself.

**Limitations**
- **Not native look:** controls look the same on every OS unless restyled. There is no automatic Windows 11 or macOS
  look beyond the Fluent theme.
- **Window chrome:** the title bar can be replaced (an extended client area with custom caption buttons) on Windows
  and macOS. Linux support for this is limited, so plan for a fallback to the system title bar.
- **Menu bar:** in-window on Windows and Linux. On macOS it can be the global menu bar, but then it is not styleable.
- **Text rendering:** text is anti-aliased vector text. Crisp bitmap-font text (a Chicago-style UI font) needs a
  pixel font drawn at its exact size with aliased rendering, and it blurs at fractional display scaling.
- **Fractional DPI** (125% or 150% on Windows): 1-px Platinum bevels and pixel art can blur or turn uneven unless
  designed for 1×/2× and snapped to the device grid. A pixel-faithful classic chrome is fragile at 125%/150%.
- **Built-in controls:** no ribbon, docking panels or MDI (those need third-party libraries). DataGrid and TreeDataGrid
  are separate packages; a tree with columns, like the Finder's list view, needs TreeDataGrid.
- **Icons:** no icon set is built in. Use vector paths or bitmaps; the app can also render real Mac icons from the
  files.
- **Drag and drop:** dragging out works by writing temporary files first. There are no lazy "file promises" (macOS) or
  virtual files (Windows), so a very large drag is written before it starts.
- **Effects:** blur, acrylic and mica backgrounds are available but uneven across platforms. There are no custom pixel
  shaders beyond Skia drawing.
- **Accessibility:** good on Windows (UI Automation) and macOS. A custom-drawn control (previews, hex view) needs its
  own automation peers to be accessible.

**Testable:** a headless test harness renders real frames of the window, so a new design can be checked with
screenshots in automated tests.

## Design asks

1. **Chrome direction:** a modern neutral frame or a Platinum homage. Show a light and a dark variant of the main
   window, with a disk image open, an icon family previewed and diagnostics visible.
2. **Toolbar and command surfaces** for the common actions (open, export, extract, get info, play, zoom, depth).
3. **Tree design:** icons per node kind (or the file's real Mac icon), the unsaved and loading states, an unread
   container, and type-ahead search.
4. **Inspector:** a unified header for the selection (name, kind, size, type and creator) above the tabs; a property
   view to replace raw JSON; a better image grid with a zoom control.
5. **Diagnostics:** severity icons and colours, headers, grouping by file, a collapsible panel, and a count badge.
6. **Empty state:** the first launch ("Drop a Mac file, disk image or archive here").
7. **Dialogs and forms:** a consistent look for the small modals and the edit forms, including the live-preview
   layout.
