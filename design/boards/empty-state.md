# First launch / empty state

Item: S5.

- Window chrome as usual; menu bar as in [main-window.md](main-window.md) (File, Edit, View, Window, Help; Resource, Volume and Export appear once something is open). Toolbar: Open enabled, every other button disabled (40% opacity).
- Inspector area becomes one centred column 640 wide on CmPaneBackground:
  - Drop zone 300 high: 2 px dashed CmControlBorder, radius 12, CmSidebarBackground. Four 48 px pixel icons (hard disk, floppy, archive, application), heading "Drop a Mac file, disk image or archive here" (CmFontTitle), a line "HFS and HFS+ volume images, archives, MacBinary, BinHex, AppleSingle and AppleDouble files, or a file with its resource fork. ClassicMac shows everything inside, down to single resources." (CmTextMuted, max 460), primary **Open…** + "Ctrl+O" in mono muted.
  - While dragging over the window: the zone's border turns CmAccent and its fill CmRowHighlight.
  - **Recent** (caption) with a "Clear list" link, then a bordered list: kind icon, file name, folder path in CmTextMuted. Clicking opens it. Missing files show muted with "Not found" and are removed on click.
- Diagnostics collapsed with "Nothing opened yet"; status bar "Ready".
- Recent files persist between sessions (new setting; up to 10).
