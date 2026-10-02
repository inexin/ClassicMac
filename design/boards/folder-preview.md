# Folder preview

Already built (`FolderPreview.cs`, `FinderWindowRenderer`, docs/formats/file-systems/finder-windows.md). This spec only covers the frame around it; the preview itself is Mac content and is not restyled.

## What the app already draws

The whole Mac OS 9 Finder window, pixel-exact against Finder screenshots: frame, title with the folder's icon, the "n items, x MB available" header, scroll bars and grow box. Labels are Geneva 10 from the volume's System file. Items are where the Finder put them; items with no stored place are arranged on the Finder's own grid, exactly as Mac OS 9 does. Invisible items are left out. Selecting an unread archive or disk image reads it and shows its window.

## Frame

- Header: folder icon, name, "Folder on Mac OS 9.hfv"; facts Items, Window `420 × 230 at (60, 80)`, View "as Icons", Modified. Action **Extract All…**. Tabs: Details | Preview (no Hex for folders).
- Sub-bar (CmTextMuted): "The folder’s window as the Finder left it". Optional check box **Mark unplaced items** (off by default): outlines the items the Finder would have placed itself, in CmAccent dashed, without moving them. No separate box for them.
- The window on the checkerboard at the chosen integer zoom.
- Clicking an icon selects that item, in the tree too, and shows a side card: name, Type · creator, icon source (custom icon, bundle, generic), position. Double-click opens it in the tree. Folders are read only here; files are added or removed from the Volume menu.
- Selecting the tree's "No name" group shows this preview of its parent folder ([tree-no-name.md](tree-no-name.md)).
