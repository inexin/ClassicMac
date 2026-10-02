# Finder windows

What a volume records about how the Finder shows a folder: each folder's window (its rectangle, scroll position and
view), each item's icon position, the flags that hide an item or give it a custom icon, badge or colour label, and
where the Finder finds the icon it draws. Mac users arranged icons with care, and some made "folder art": empty files
with custom icons, set side by side to make a picture. ClassicMac reads these fields from HFS and HFS Plus catalogs and
draws a folder as the Finder's icon or button view of its window, for the viewer's folder previews.

| | |
| --- | --- |
| Identified by | The Finder fields of catalog folder and file records ([hfs.md §1.9](hfs.md#19-catalog-records), [hfs-plus.md](hfs-plus.md)) |
| ClassicMac | Reads; `ClassicMac.Files` (`MacFolder`, `FolderFinderInfo`, `HfsReader.ReadFolders`, `MacFile.IsLocked`), `ClassicMac.Resources.Decoders.Finder` (`FinderIconResolver`, `FinderWindowRenderer`, `FinderView`, `FinderPreferences`); the viewer's folder preview |
| Verified against | The Mac OS 9.0 Finder: synthetic volumes' windows in large icon, small icon, large and small button views, scrolled and default, pixel for pixel in the icon area (§7) |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials* (Finder Interface), *Inside Macintosh: More Macintosh Toolbox* (Icon Utilities), Universal Interfaces `Finder.h` and `Icons.h`; the Mac OS 9.2.2 Finder and Icon Services as traced, checked on Mac OS 9.0 |

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

### 1.1 Folder Finder information (DInfo, DXInfo)

A folder's catalog record holds `DInfo` then, elsewhere in the record, `DXInfo` (HFS: `dirUsrInfo` at +$16 and
`dirFndrInfo` at +$26; HFS Plus: `userInfo` at +$30 and `finderInfo` at +$40). ClassicMac keeps them together as 32
bytes. [Doc: Macintosh Toolbox Essentials]

`DInfo`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 8 | `frRect` | `Rect`: the window's content rectangle in global coordinates, the header pane and the scroll bars included (§2.1) [Verified: Mac OS 9.0 Finder] |
| +$08 | 2 | `frFlags` | Finder flags, as `fdFlags` (§1.3) |
| +$0A | 4 | `frLocation` | `Point`: the folder's icon in the window of the folder holding it |
| +$0E | 2 | `frView` | Bits 8–11 the view family, bits 0–2 the arrangement, bit 6 small icons (§2.6) |

`DXInfo`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$10 | 4 | `frScroll` | `Point`: the local point at the icon area's top-left corner |
| +$14 | 4 | `frOpenChain` | The chain of open folders; bits 18–21 a list view's style (§2.6) |
| +$18 | 1 | `frScript` | With +$19, the extended Finder flags word (§1.3); bits $20, $40, $08 also choose the view (§2.6) |
| +$19 | 1 | `frXFlags` | Extended flags |
| +$1A | 2 | `frComment` | The comment's ID in the desktop database |
| +$1C | 4 | `frPutAway` | The folder an item on the desktop came from |

The root folder's record (CNID 2) describes the volume's window, by the same rules as any folder [Code: Finder 9.2.2].
`frFlags` bits 5–7, $0010, $0200 and $0800, `frOpenChain` and `frComment` are the Finder's own on 9.x and carry nothing
a reader needs [Code: Finder 9.2.2].

### 1.2 File Finder information (FInfo, FXInfo)

A file's `FInfo` ([unwrapping.md](../containers/unwrapping.md)) has the matching fields: `fdFlags` (§1.3),
`fdLocation`, the icon's place in its folder's window, and `fdFldr`, the window it is in (System 6 and earlier; a
volume with folders uses the catalog's parent instead). [Doc: Macintosh Toolbox Essentials] Its `FXInfo` holds, at +8,
the extended Finder flags word over `fdScript` and `fdXFlags` (§1.3) [Doc: `Finder.h`, `ExtendedFileInfo`].

The file's locked attribute is the catalog's (HFS `filFlags` bit 0, HFS Plus flags bit 0), not a Finder flag.

### 1.3 Flags

The `fdFlags`/`frFlags` bits this document uses [Doc: Macintosh Toolbox Essentials; `Finder.h`]:

| Bit | Mask | Name | Meaning here |
| --- | --- | --- | --- |
| 15 | $8000 | `kIsAlias` | An alias: its name is in italics, its icon has the alias badge, its type may name its original's kind (§2.3) |
| 14 | $4000 | `kIsInvisible` | Not shown |
| 13 | $2000 | `kHasBundle` | A file has a `'BNDL'`; a folder is a package (§8) |
| 11 | $0800 | `kIsStationery` | A stationery pad: its type is looked up with an `s` first (§2.3) |
| 10 | $0400 | `kHasCustomIcon` | The item has a custom icon (§2.3) |
| 8 | $0100 | `kHasBeenInited` | The Finder has recorded the item's position, and a folder's window (§2.1, §2.2) |
| 3–1 | $000E | Colour | The label, 0–7 (§2.7) |

The extended Finder flags word (`FXInfo` +8, `DXInfo` +8) [Doc: `Finder.h`]:

| Mask | Name | Meaning here |
| --- | --- | --- |
| $8000 | `kExtendedFlagsAreInvalid` | The other bits mean nothing |
| $0100 | `kExtendedFlagHasCustomBadge` | The item has a `'badg'` (§1.6) |

### 1.4 Where the icons are

| What | Where |
| --- | --- |
| A file's custom icon | Its own resource fork: the icon family of ID −16455 (`kCustomIconResource`): `ICN#`, `icl4`, `icl8`, `ics#`, `ics4`, `ics8`, and on Mac OS 8.5 and later `'icns'` −16455 [Doc: Macintosh Toolbox Essentials; `Icons.h`] |
| A folder's custom icon and badge | The resource fork of an invisible file named `Icon` followed by a return (`Icon\r`, $49 $63 $6F $6E $0D) inside the folder, ID −16455 [Code: Finder 9.2.2] |
| An application's icons for its files | Its bundle: `'BNDL'`, `'FREF'` and the icon families they name ([finder.md §2.1](../resources/finder.md#21-a-file-types-icon)); the Finder copies them into the desktop database and reads them from there |
| The system's icons | By type through the icon mapping table (§1.7), each ID an `'icns'` (in System Resources) or separate icon members (in the System file) [Code: Icon Services 9.2.2] |

### 1.5 Finder Preferences

The `Finder Preferences` file (type `'pref'`, creator `'MACS'`, in the System Folder's `Preferences`) holds the global
view settings in `'fvl8'` 128 [Code: Finder 9.2.2]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$18 | 4 | Views font | The font family; Geneva (3) by default |
| +$1C | 4 | Views size | Geneva 10 by default |
| +$24 | 8 | Grid spacing | Four 16-bit percentages; 200, 120, 200, 120 by default |

Other fields, the standard views' sizes among them, were not traced.

### 1.6 Custom badges (`'badg'`)

`'badg'` −16455 in the item's own fork (a folder's `Icon\r`), at least $1C bytes (`Icons.h`, `CustomBadgeResource`)
[Code: Finder 9.2.2]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | `version` | 0 or less, else the badge is not used |
| +$02 | 2 | `customBadgeResourceID` | The badge's icon family in the same fork; 0 for none |
| +$04 | 4 | `customBadgeType` | With the creator, a registered icon for the badge |
| +$08 | 4 | `customBadgeCreator` | |
| +$0C | 4 | `windowBadgeType` | The window's badge (not drawn, §8) |
| +$10 | 4 | `windowBadgeCreator` | |
| +$14 | 4 | `overrideType` | Replaces the item's type and creator for its icon, when it has no custom icon |
| +$18 | 4 | `overrideCreator` | |

### 1.7 The icon mapping table (`'isrv'`)

The System file's `'isrv'` 128, "Icon Mapping Table": 6-byte entries of a type (4) and a resource ID (2), no header;
168 of them on Mac OS 9.0 [Code: Icon Services 9.2.2]. Without it, Icon Services uses its own list:

| Kind | Type | ID |
| --- | --- | --- |
| Document, stationery | `docu`, `sdoc` | −4000, −3985 |
| Application, control panel, desk accessory | `APPL`, `APPC`, `APPD` | −3996, −3824, −3991 |
| Folder, open, shared, drop box, mounted, owned, private | `fldr`, `ofld`, `shfl`, `dbox`, `mntd`, `ownd`, `prvf` | −3999, −3997, −3978, −3979, −3977, −3980, −3994 |
| Hard disk, floppy, CD, server | `hdsk`, `flpy`, `cddr`, `srvr` | −3995, −3998, −3987, −3972 |
| Desktop, trash, full trash | `desk`, `trsh`, `ftrh` | −3992, −3993, −3984 |
| System Folder, preferences | `macs`, `pref` | −3983, −3971 |
| Badges: alias, locked, mounted, shared | `abdg`, `lbdg`, `mbdg`, `sbdg` | −20789, −20786, −20787, −20788 |

For an ID, an `'icns'` of that ID comes first; else the separate members (`ICN#`, `icl4`, `icl8`, `ics#`, `ics4`,
`ics8`, `icm#`, `icm4`, `icm8`), each only at its exact size. The badges and `APPC` exist only as `'icns'`.
[Code: Icon Services 9.2.2]

### 1.8 Label colours

Label n (1–7) has its colour in the System's `'rgb '` −16392 + n (an `RGBColor`, 6 bytes) and its name in `'lstr'` of
the same ID [Code: Finder 9.2.2]. Mac OS 9.0's are those of [icon-families.md](../resources/icon-families.md).

## 2. Reading

### 2.1 A folder's window

1. Find the folder's record; for a volume's window, the root folder's.
2. `frRect` is used only when `frFlags` has `kHasBeenInited` and `frRect` is not (0, 0, 0, 0) [Code: Finder 9.2.2].
   Then it is the content rectangle and `frScroll` the scroll position. Otherwise the window has the Finder's default
   content size, 404 wide and 218 high (the window template (62, 14, 280, 418)), and the scroll position (v −8, h −16)
   [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder]. A window the Finder opened this way and left keeps `frRect` 0.
3. The content rectangle holds, from the top, a header pane 21 pixels high ("n items, x MB available" and a 1-pixel
   line), the icon area, and the scroll bars, 15 pixels at the right and at the bottom
   [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder].
4. An item's 32 × 32 icon has its top-left corner on the screen at (`frRect.left` + h − `frScroll.h`,
   `frRect.top` + 21 + v − `frScroll.v`), (v, h) its position (§2.2) [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder:
   an item at (100, 60) with `frScroll` (100, 60) is at the icon area's corner].
5. The icon area's background is white; Finder 9 has no per-folder background [Code: Finder 9.2.2; Verified: Mac OS 9.0
   Finder, View Options].

### 2.2 The items

1. The items are the files and folders whose catalog parent is the folder.
2. Items with `kIsInvisible` are not shown [Doc]. Among them are the `Icon\r` file (§1.4) and the desktop database
   files. In a volume's root window the Finder also leaves out, by name, the files `AppleShare PDS`, `Desktop`,
   `Desktop DB`, `Desktop DF`, `DesktopPrinters DB`, `Finder`, `OpenFolderListDF`, `Shutdown Check` and `VM Storage`,
   and the folders `Temporary Items`, `Trash`, `Desktop Folder`, `Move&Rename` and `TheVolumeSettingsFolder`
   [Code: Finder 9.2.2].
3. An item's position is its `fdLocation` (a folder's: `frLocation`), the top-left corner of its 32 × 32 icon in the
   window's local coordinates [Verified: Mac OS 9.0 Finder]:
   - with `kHasBeenInited`, the location as stored;
   - without it, the location plus 20000 in each coordinate, wrapping at 16 bits, kept when −4000 < h < 4000 and
     v > −4000 (v has no upper limit); otherwise there is no position. [Code: Finder 9.2.2] An ordinary location, such
     as a folder's whose own `kHasBeenInited` is clear, falls outside, so the item is arranged [Verified: Mac OS 9.0
     Finder].
   - (0, 0) and (−1, −1) are no position, even with `kHasBeenInited` [Code: Finder 9.2.2].
4. Items with no position are arranged (§2.5); the Finder then writes their new positions and sets `kHasBeenInited`
   [Verified: Mac OS 9.0 Finder].

### 2.3 Which icon

For a file, in order [Code: Finder 9.2.2, Icon Services 9.2.2]:

1. With `kHasCustomIcon`, the custom icon (§1.4): the ID −16455 icon family when it has an `ICN#`, else its
   `'icns'` −16455.
2. Otherwise, with a custom badge (§1.6) whose override type is set, that type and creator replace the file's.
3. A stationery pad's type has its first character replaced by `s` (`TEXT` → `sEXT`).
4. An alias's type `fdrp`, `fadr`, `famn`, `fash` or `drop` (aliases to folders, disks and drop boxes) becomes `fldr`,
   and `fasy` (to the System Folder) `macs`; both then take the system's mapping (5a). Other aliases keep their type
   and creator, their original's.
5. `GetIconRef(creator, type)`. A creator or type of 0 or `????` is (`macs`, `docu`).
   1. The creators `movr`, `drag`, `MACS`, `macs`, `DMOV` and `chrp`, the type `pref`, and aliases to containers use
      the system's mapping: the type, with `adrp` as `APPL`, `acdp`, `cdev` and `cpnl` as `APPC`, and `addp`, `dfil`
      and `deka` as `APPD`, looked up in the icon mapping table (§1.7), else the generic icon (5d).
   2. Otherwise the desktop database's icon for the creator and type, which needs a mask member (`ICN#`, else `ics#`,
      else `ich#`);
   3. else the icon mapping table by type, except for the types `dict`, `dspl`, `mbug`, `ppdf`, `prof`, `sdev`,
      `thme`, `uams` and `utbl`;
   4. else the generic icon: `APPL`, `APPC` or `APPD` for those types (after the mapping in 5a), else `docu`. A
      stationery pad with no icon of its own shows `docu` [Verified: Mac OS 9.0 Finder].

For a folder: with `kHasCustomIcon`, the custom icon from its `Icon\r`; else, with `kHasBundle`, its package icon
(§8); else `hdsk` for a volume's root and `fldr` for the others. [Code: Finder 9.2.2]

### 2.4 Badges

Composited on the icon, in its rectangle [Code: Finder 9.2.2]: an alias's `abdg`, a locked file's `lbdg`, and a custom
badge: when the extended flags have `kExtendedFlagHasCustomBadge` and not `kExtendedFlagsAreInvalid`, the item's
`'badg'` (§1.6), whose icon is the family of its ID in the same fork, else the icon registered for its type and
creator.

### 2.5 Arranging items without a position

Large icons [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder, both arranged items where §7's fixture predicts]:

1. The grid's cell is 128 wide and 64 high; its origin is (v 0, h 1).
2. Every placed item occupies its icon rectangle and its label rectangle (§2.7), each widened left and right by the
   views font size (10), and the union of the two.
3. The scan starts at the smallest grid point at or past (visible top + 4, visible left + 16): (v 64, h 129) with no
   scroll. When nothing is placed, it starts at (0, 1).
4. Along the row, h steps by 128. The first cell whose icon and label rectangles meet nothing occupied, and whose cell
   (h + 128) still fits in the visible width (the content width less the scroll bar), takes the item; past the width,
   v steps by 64 and h returns to the start.
5. An arranged item occupies its rectangles too, for the next. Snapping to the grid (Clean Up) rounds to the nearest
   point, halves away from zero.

Buttons do the same with cells 128 × 86 (large) and 128 × 62 (small), an item occupying its button and its name
(§2.8) [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder, an arranged item in each].

Small icons use cells 192 wide and 24 high and scan down columns: v steps by 24 while the cell fits the visible height,
then h steps by 192 and v returns to the start [Code: Finder 9.2.2]. The grid's origin is (v 0, h 2), and the scan
starts at the first grid point at or past (visible top, visible left + 16), keeping clear of the union of all the
placed items, not only of each [Fitted: Mac OS 9.0 Finder, two arranged items placed at (v 0, h 386) and (v 24, h 386)
beside a column of placed names, where each item's rectangles alone leave room in the column at h 194].

### 2.6 The view

From `frView`, `frScript` and `frOpenChain` [Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder]:

1. v = (`frView` >> 8) & 15. 3–8 is a list view of style v; above 8, of style 2; 2, of the style in `frOpenChain` bits
   18–21. 0 and 1 are the icon family.
2. In the icon family, `frScript` $20 with its bit 7 clear selects buttons; otherwise icons.
3. The size: when `frScript` has $40 (the folder has its own view options), icons are small when `frView`'s low byte has
   $40, buttons when `frScript` has $08. Without $40, the size is the Finder Preferences' standard view for icons or
   buttons, large by default. The Finder writes $50 for small icons, $68 for small buttons, $20 for large buttons.
4. `frView` bits 0–2 are the arrangement: 0 name, 4 kind, 5 label, 6 position, 7 none.

### 2.7 Drawing an item: large icons

[Code: Finder 9.2.2; Verified: Mac OS 9.0 Finder, 18 labels of 18]

1. The icon is plotted with `PlotIconRef` in its 32 × 32 rectangle, alignment none, transform (dimmed ? 1 : 0) |
   (selected ? $4000 : 0) | (label << 8), label = (`fdFlags` >> 1) & 7. The label tints it as
   [icon-families.md](../resources/icon-families.md) describes, with §1.8's colours. Badges follow (§2.4).
2. The name is in the views font (§1.5; Geneva 10 by default: ascent 10, descent 2, leading 1). With L the icon's
   top-left and tw its `StringWidth`, the label rectangle is top L.v + 32, bottom L.v + 45, left
   L.h + 16 + ((−tw) >> 1) − 2 (an arithmetic shift), right left + tw + 4, and the pen starts at (left + 2, L.v + 42).
3. The name is drawn `srcOr` in black, with no box behind it, never truncated or wrapped. A dimmed item's in
   `grayishTextOr`; a selected item's label rectangle is painted black and the name drawn `srcBic`, white on black.
4. An alias's name is in italics [Doc: Macintosh Toolbox Essentials].

### 2.8 Drawing an item: small icons and buttons

[Verified: Mac OS 9.0 Finder, unless tagged]

| View | Button | Icon | Pen (h, baseline) |
| --- | --- | --- | --- |
| Small icon | None | 16 × 16 at (v, h) | (h + 18, v + 11), flush left |
| Large button | 48 × 48 at (v, h − 8) | 32 × 32 at (v + 8, h) | (h + 16 + ((−tw) >> 1), v + 60), centred |
| Small button | 28 × 28 at (v, h + 2) | 16 × 16 at (v + 6, h + 8) | (h + 16 + ((−tw) >> 1), v + 40), centred |

(v, h) is the item's position. A small icon's name rectangle starts at h + 17 and v + 1.

1. A small icon's name has a pane 167 wide [Fitted: from h + 17 to h + 184]. When the name and 2 pixels are wider, it
   is drawn condensed; when that is still too wide, `TruncString(165, smTruncMiddle)` shortens it [Code: Finder 9.2.2].
   "WWWWWWWWWWWWWWWW" (160 pixels) stays plain; "ThirtyOneCharactersWithoutSpace" (168) is condensed to 137.
2. Buttons' names are never truncated.
3. A button is the Appearance Manager's bevel (`ApplyThemeBackground` 3 in the Finder): a $CCCC face in three rings,
   each lighter at the top and left and darker at the bottom and right, its top-right and bottom-left corners between:
   $6666, $3333 and $5555 outside, then $CCCC, $7777, $AAAA, then $FFFF, $9999, $CCCC.
4. A button's icon is centred in its rectangle by its mask (`kAlignAbsoluteCenter`): a shaped icon sits where its
   mask's bounds centre [Fitted: Mac OS 9.0 Finder, an arrow icon a pixel higher than plotted unaligned].

## 3. Writing

None.

## 4. Variants

- **System 6 and earlier**: `fdFldr` names the window an item is in, and `kIsOnDesk` puts it on the desktop. From
  System 7, desktop items are in the invisible `Desktop Folder` of each volume. [Doc: Macintosh Toolbox Essentials]
- **Mac OS 8 and later** let the user choose the views font and the grid spacing (§1.5) and add the header pane.
- **Mac OS 8.5 and later** take an `'icns'` −16455 as a custom icon (§2.3).

## 5. ClassicMac

### 5.1 Reading the fields

- `HfsReader.ReadFolders(input, context)` reads an HFS or HFS Plus volume, plain or wrapped, as `Read` does (same
  checks, same diagnostics) and returns every folder with its `FolderFinderInfo` and dates, the root first marked
  `IsRoot` ([hfs.md §5.2](hfs.md#52-what-comes-out)). `MacFolder.Path` is the folder path the files inside it have.
  `MacFile.IsLocked` is the catalog's locked flag. [ClassicMac]
- Other containers (archives, FAT, ISO 9660) record no folder windows: their folders preview as a default window with
  every item arranged (§2.5). Folders that only files' paths name are items too. [ClassicMac]
- `FinderView.Read` decodes §2.6; Finder Preferences' standard views are not read, so a folder without its own options
  is large icons or large buttons. [ClassicMac]

### 5.2 Layout

- The bitmap is the window's content rectangle (§2.1). The header pane is drawn in the Platinum appearance's colours
  (a white top and left edge, $DDDD grey, a $AAAA bottom and right edge, a black line) with "n items" centred on a
  baseline 14 down, in the views font; the free space is not shown. The scroll bars are their 15-pixel place: a black
  edge and an empty $EEEE trough, no arrows, thumb or grow box. [ClassicMac]
- Large and small icons and buttons are drawn as §2.7 and §2.8 say; a list view is drawn as large icons, and the
  preview's caption says so ("list view, shown as icons"). [ClassicMac]
- A button's and a small icon's name rectangle, for arranging, runs from the ascent above the baseline to 3 below it,
  2 pixels either side of the text. Badges are plotted unaligned in a button's icon rectangle. [ClassicMac]
- Truncation keeps the most characters about an ellipsis, the first half's extra one first; it was not compared with
  the Script Manager's `TruncText`. [ClassicMac]
- A window with no recorded rectangle grows taller to show all its arranged items. A window narrower than one cell takes
  one item a row. [ClassicMac]
- Items are drawn subfolders first, by name, then files in the order the volume lists them; a later item covers an
  earlier one. [ClassicMac]
- An item no source gives an icon gets an outline drawn in code: a page with a turned corner, a folder or a diamond
  for an application. [ClassicMac]
- Items are drawn unselected and not dimmed. [ClassicMac]

### 5.3 Labels

- The views font and size come from a `Finder Preferences` file on the volume (by name, type and creator, anywhere on
  it), else Geneva 10. [ClassicMac]
- The fonts come from the volume's System file and the font suitcases in the `Fonts` folder beside it (the suitcases'
  families replacing the System's), else from the fonts in the files open in the viewer, else from `ITextFallback`
  (an installed font), whose labels only approximate the Mac's. [ClassicMac]

### 5.4 Icon sources

- The desktop database is not read: applications' bundles stand in for it. Applications are indexed by creator on
  first use, once per volume; a file's bundle counts only when its signature is the file's creator. Unreadable forks
  and damaged bundles are passed over. [ClassicMac]
- The system's icons, badges, mapping table and label colours come from the System and System Resources files on the
  volume (type `'zsys'` or `'zsyr'`, creator `'MACS'`) and from the open files' forks that hold `ICN#` or `'icns'`
  −4000 or `'isrv'` 128. None are in ClassicMac; without them, badges are left out and §1.7's list maps types.
  [ClassicMac]
- A custom badge named only by type and creator takes the system's icon for its type. [ClassicMac]

## 6. Diagnostics

None of its own. `ReadFolders` reports what `Read` reports ([hfs.md §6](hfs.md#6-diagnostics),
[hfs-plus.md §6](hfs-plus.md#6-diagnostics)), and `hfs.orphan` for a folder whose parent is missing.

## 7. Verification

- `tests/ClassicMac.Files.Tests/FileModelTests.cs` (`MacFolderTests`, `FolderFinderInfoTests`): the 32-byte layout of
  §1.1 read and written, short input, paths. `HfsTests.cs` and `HfsPlusFeatureTests.cs`: `ReadFolders` on HFS, HFS
  Plus, a wrapper, with an orphan, and without the private folder; the locked flag.
- `tests/ClassicMac.Resources.Decoders.Tests/FinderWindowTests.cs`: the content rectangle, header and scroll bars, the
  default window, positions (§2.2's every branch), the root's own items, arranging (§2.5, with an equivalent of the
  fixture below: the two items land at (v 320, h 129) and (v 320, h 257)), label geometry, label tints, badges, the
  view decode; small icons (geometry, condensing, truncation, columns, the union) and buttons (bevel, icon centring,
  names, rows). `FinderIconResolverTests.cs`: each step of §2.3, the mapping table and its fallback, exact member
  sizes, badges, `'badg'`, label colours. `FinderPreferencesTests.cs`: §1.5.
- `tests/ClassicMac.App.Tests/FolderPreviewTests.cs`: folders, a volume's root, a disk inside an archive and an
  archive's folders in the viewer, with icons, badges, label colours, the views font and the caption;
  `A_real_volume_s_folder_renders` draws a folder of the volume `CLASSICMAC_FOLDER_VOLUME` names;
  `A_folder_matches_the_Finder_s_screenshot` compares a folder with a screenshot in the folder
  `CLASSICMAC_FINDER_GOLDEN` names (neither committed).
- Two synthetic HFS volumes, after Mac OS 9.0 Finder sessions, drawn with Mac OS 9.0's System, System Resources and
  Geneva suitcase and compared with the Finder's screenshots in every pixel of the icon area, all matching:
  folder art in large icons (custom icons, long names, label 2 and label 6 items, a locked file, an alias, stationery,
  generic documents and applications, a custom-icon folder, two items arranged; 347,936 pixels); small icons (16 items,
  names to 31 characters, two arranged; 413,253); large buttons and small buttons (5 items, one arranged; 72,800 each);
  a window scrolled to (60, 100) (72,800); a folder without `kHasBeenInited` in the default window (70,798).

## 8. Not covered

- List views (drawn as large icons, §5.2); the Finder Preferences' standard views.
- Why small icons keep clear of all the placed items together, and the arranging rules of the other views beyond the
  cases §7 checks.
- The desktop database (`Desktop DB`, `Desktop DF`): its icons, comments and application list; bundles stand in.
- Package folders' icons (`kHasBundle` on a folder); a volume's own icon (`hdsk`) is not drawn, as no window shows it.
- Resolving aliases to their originals; the mounted and shared badges; a custom badge's window badge.
- Selection and dimming (§2.7's rules are not drawn); the header's free space.
- The invisible bit of `ioFlAttrib` (bit 6), which the Finder also tests, is not read.
- Folder records of MFS, FAT and ISO 9660.

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), chapter 7 "Finder Interface": `FInfo`, `FXInfo`,
   `DInfo`, `DXInfo`, the Finder flags, custom icons, bundles, aliases.
2. Apple, *Inside Macintosh: More Macintosh Toolbox* (1993), chapter 5 "Icon Utilities".
3. Apple, Universal Interfaces 3.4, `Finder.h` (flags, extended flags, `kCustomIconResource`, alias types) and
   `Icons.h` (generic icon resource IDs, badge types, `CustomBadgeResource`).
4. Apple, Technical Note TN1150, *HFS Plus Volume Format*: `HFSPlusCatalogFolder`, `HFSPlusCatalogFile`.
