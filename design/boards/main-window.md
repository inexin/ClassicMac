# Main window

Items: S1 title bar, S2 toolbar, S3 inspector header and tabs, S4 status bar, S7 menus and About, P1 image grid. Default 1200 × 780, minimum 700 × 450.

## Layout, top to bottom

| Part | Height | Background | Notes |
| --- | --- | --- | --- |
| Title bar | CmBarTitle 34 | CmTitleBarBackground | App icon (16 px pixel), "Mac OS 9.hfv • — ClassicMac" in CmFontMeta, CmTextMuted; the " •" in CmAccent when the file has unsaved edits. Caption buttons 46 × 34 on Windows. Linux: system title bar instead. |
| Menu bar | CmBarMenu 26 | CmChromeBackground | File, Edit, View, Resource, Volume, Export, Window, Help. See Menus below. |
| Toolbar | CmBarTool 48 | CmChromeBackground, CmBorder below | Buttons 34 high: 18 px stroke icon + label (CmFontMeta), transparent until hover. Groups split by 1 × 24 CmBorder lines. |
| Body | rest | | Tree 340 wide (CmSidebarBackground) · 1 px CmBorder splitter · inspector (CmPaneBackground). |
| Diagnostics | 196, 34 collapsed | | See [diagnostics.md](diagnostics.md). |
| Status bar | CmBarStatus 26 | CmChromeBackground, CmBorder above | See below. |

## Toolbar (S2)

Left to right: **Open** · **Save** | **Get Info** · **Edit Hex** | **Export…** · **Extract All** | **Play** — spacer — "Zoom" label + segmented 1× 2× 4× 8× · "Depth" + select (1-bit, 2-bit, 4-bit, 8-bit (256), 16-bit, 32-bit).

- Each button binds to the existing command and follows its `CanExecute` (disabled = 40% opacity). Play is disabled unless the preview is a sound. Edit Hex (Ctrl+H) is enabled only when a resource is selected; it opens the Hex tab in editing ([hex.md](hex.md)).
- Zoom and Depth bind to the existing `Zoom` / `ScreenDepth` and are disabled when `Preview.IsZoomable` is false. Segmented control: CmSegmentTrack track, chosen segment CmSegmentOn with a small shadow, 24 high.
- Icons: open folder, download tray (Save), circled i, `</>` (Hex), upload tray (Export), stacked box with down arrow (Extract All), triangle (Play). 1.5 stroke, `currentColor`.

## Inspector header (S3)

- 48 × 48 tile (CmCardBorder, checkerboard) holding the selection's own icon at 1× when it has one, else its kind icon.
- Name in CmFontHeading (`128 “Trash”`), then the kind and owner in CmFontMeta CmTextMuted ("Icon family in Finder").
- Facts row, CmFontMeta: label in CmTextMuted, value in CmText — Type (mono), ID, Members, Size, Attributes. For files: Type / creator, Total size, Resources.
- Right side: secondary **Export…**, primary **Edit** (only when the selection has a form; starts editing in place, see [read-then-edit.md](read-then-edit.md)).
- Tabs below, 36 high: **Details | Preview**, plus **Hex** only when the app shows it today (a resource with no preview, or while bytes are being edited). Files, folders and containers never have Hex. Active tab = CmText SemiBold with a 2 px CmAccent underline; others CmTextMuted. The old Edit tab is removed.

## Image preview (P1)

- Sub-bar: "6 members · 2× · nearest neighbour · 8-bit screen" in CmTextMuted; right side check boxes "Show masks", "Finder states".
- Cards wrap (WrapPanel), 12 gap, bottom-aligned: checkerboard area with 12 padding and the image at integer zoom, then a caption strip (CmCardBorder top): resource type in mono SemiBold (`'icl8'`), then "32×32 · 8-bit" in CmTextMuted 11.
- "Finder states" heading (12 SemiBold, muted) and a strip on checkerboard: Normal, Selected, Disabled, Offline, Open, then the seven labels (Essential, Hot, In Progress, Cool, Personal, Project 1, Project 2). Shown at 2× when zoom ≥ 2, else 1×. Captions on a small CmPaneBackground plate so they read on the checkerboard. These images already come from `FinderIcons`.
- Must stay smooth with hundreds of items (virtualise if needed).

## Status bar (S4)

Left: "Mac OS 9.hfv · HFS+ · 4,982 files · 2 errors · 3 warnings" with the counts in CmError / CmWarning. Right, while work runs: "Reading Realmz 6.1.sit… 1,240 of 3,906" and a 120 × 6 progress bar (CmSegmentTrack, fill CmAccent). Otherwise the existing `Status` text.

## Menus (S7)

Existing menus keep their commands. New content:

| Menu | Items |
| --- | --- |
| View | Zoom In (Ctrl+=), Zoom Out (Ctrl+-), Actual Size (Ctrl+0) · Screen Depth ▸ 1–32-bit · Show Diagnostics (Ctrl+Shift+D) · Group Files with No Name, Hide Invisible Files (check items, same as the tree's display options) · Theme ▸ System, Light, Dark |
| Window | Minimize, Zoom · one item per open input (switches the tree to it) |
| Help | ClassicMac Help (opens the README on GitHub) · Report a Problem… · About ClassicMac |

On macOS, About goes in the application menu. The About box: app icon, name, version, one line on what it does, licence and third-party notices (THIRD-PARTY-NOTICES.md), a link to the repository; one **OK** button. Dialog frame as in [dialogs.md](dialogs.md).

## Dark mode

Same layout, dark token values. Mac content (icons, dialogs, menus) keeps its own colours; only the checkerboard and frame change.
