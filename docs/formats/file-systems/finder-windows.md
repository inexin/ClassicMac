# Finder windows

What a volume records about how the Finder shows a folder: each folder's window (its rectangle and scroll position),
each item's icon position, the flags that hide an item or give it a custom icon, and where the Finder finds the icon
it draws. Mac users arranged icons with care, and some made "folder art": empty files with custom icons, set side by
side to make a picture. ClassicMac reads these fields from HFS and HFS Plus catalogs and draws a folder as the Finder's
icon view of its window, for the viewer's folder previews.

| | |
| --- | --- |
| Identified by | The Finder fields of catalog folder and file records ([hfs.md §1.9](hfs.md#19-catalog-records), [hfs-plus.md](hfs-plus.md)) |
| ClassicMac | Reads; `ClassicMac.Files` (`MacFolder`, `FolderFinderInfo`, `HfsReader.ReadFolders`), `ClassicMac.Resources.Decoders.Finder` (`FinderIconResolver`, `FinderWindowRenderer`); the viewer's folder preview |
| Verified against | Nothing yet; the layout was compared with a Mac OS 9.0 volume's folders (§7) |
| Sources | *Inside Macintosh: Macintosh Toolbox Essentials* (Finder Interface), *Inside Macintosh: More Macintosh Toolbox* (Icon Utilities), Universal Interfaces `Finder.h` and `Icons.h` |

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
| +$00 | 8 | `frRect` | `Rect`: the folder's window, in global coordinates |
| +$08 | 2 | `frFlags` | Finder flags, as `fdFlags` (§1.3) |
| +$0A | 4 | `frLocation` | `Point`: the folder's icon in the window of the folder holding it |
| +$0E | 2 | `frView` | How the window shows its contents (by icon, by name, …) |

`DXInfo`:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$10 | 4 | `frScroll` | `Point`: the window's scroll position |
| +$14 | 4 | `frOpenChain` | The chain of open folders |
| +$18 | 1 | `frScript` | The name's script, when its high bit is set |
| +$19 | 1 | `frXFlags` | Extended flags |
| +$1A | 2 | `frComment` | The comment's ID in the desktop database |
| +$1C | 4 | `frPutAway` | The folder an item on the desktop came from |

The root folder's record (CNID 2) describes the volume's window.

### 1.2 File Finder information (FInfo)

A file's `FInfo` ([unwrapping.md](../containers/unwrapping.md)) has the matching fields: `fdFlags` (§1.3),
`fdLocation`, the icon's place in its folder's window, and `fdFldr`, the window it is in (System 6 and earlier; a
volume with folders uses the catalog's parent instead). [Doc: Macintosh Toolbox Essentials]

### 1.3 Flags

The `fdFlags`/`frFlags` bits this document uses [Doc: Macintosh Toolbox Essentials; `Finder.h`]:

| Bit | Mask | Name | Meaning here |
| --- | --- | --- | --- |
| 15 | $8000 | `kIsAlias` | An alias: its name is drawn in italics, and its type may name its original's kind (§2.3) |
| 14 | $4000 | `kIsInvisible` | Not shown |
| 13 | $2000 | `kHasBundle` | The file has a `'BNDL'`: its bundle names icons for its signature's files |
| 10 | $0400 | `kHasCustomIcon` | The item has a custom icon (§2.3) |
| 3–1 | $000E | Colour | The label (not drawn, §8) |

### 1.4 Where the icons are

| What | Where |
| --- | --- |
| A file's custom icon | Its own resource fork: the icon family of ID −16455 (`kCustomIconResource`): `ICN#`, `icl4`, `icl8`, `ics#`, `ics4`, `ics8`, and on Mac OS 8.5 and later `'icns'` −16455 [Doc: Macintosh Toolbox Essentials; `Icons.h`] |
| A folder's custom icon | The resource fork of an invisible file named `Icon` followed by a return (`Icon\r`, $49 $63 $6F $6E $0D) inside the folder, ID −16455 [Fitted: every custom-icon folder on the volume of §7 has one] |
| An application's icons for its files | Its bundle: `'BNDL'`, `'FREF'` and the icon families they name ([finder.md §2.1](../resources/finder.md#21-a-file-types-icon)) |
| The generic icons | Icon families of the System file: −4000 document, −3999 folder, −3996 application, and others (`kGenericDocumentIconResource` …) [Doc: `Icons.h`]; Mac OS 9.0's System file has their `ICN#`, `icl8` and `ics#`, and its System Resources file `'icns'` of the same IDs [Fitted: the Mac OS 9.0 System Folder] |

## 2. Reading

### 2.1 A folder's window

1. Find the folder's record; for a volume's window, the root folder's.
2. `frRect`'s size is the window's size. ClassicMac takes it as the content area (§8).
3. A point in the window's local coordinates is drawn at that point less `frScroll`: `frScroll` is the local point at
   the content's top-left corner [Doc: Macintosh Toolbox Essentials: the scroll position; the subtraction is
   Fitted: folder art at local v 895 with `frScroll` v 883 shows at the window's top, as it was made to].

### 2.2 The items

1. The items are the files and folders whose catalog parent is the folder.
2. Items with `kIsInvisible` are not shown [Doc]. Among them are the `Icon\r` file (§1.4), the desktop database files
   and the system's own folders (`Desktop Folder`, `Trash`, `TheVolumeSettingsFolder`, …).
3. An item's position is its `fdLocation` (a folder's: `frLocation`), in the window's local coordinates [Doc]; it is
   the top-left corner of the item's 32 × 32 icon [Fitted: folder art of 32 × 32 tiles lies at 32-pixel steps, so the
   tiles meet].
4. A location of (−1, −1) means the Finder has not placed the item [Fitted: 688 items on the volume of §7 have it,
   most installed by Apple's installers into folders whose windows were never opened]; ClassicMac treats (0, 0) the
   same (§5.2).

### 2.3 Which icon

In order:

1. With `kHasCustomIcon`, the custom icon (§1.4): the ID −16455 icon family, when it has an `ICN#`, else its
   `'icns'` −16455 [Doc: Macintosh Toolbox Essentials, for the flag and the ID].
2. An alias without a custom icon has its original's type and creator, except an alias to an application (type
   `'adrp'`, shown with the application's icon) or to a folder (`'fdrp'`, a folder) [Doc: `Finder.h`,
   `kApplicationAliasType`, `kContainerFolderAliasType`].
3. Otherwise a file's icon is the one the bundle of the application whose signature is the file's creator maps to the
   file's type ([finder.md §2.1](../resources/finder.md#21-a-file-types-icon)); an application's own icon is its
   bundle's `'APPL'` entry. Only files with `kHasBundle` have bundles the Finder reads [Doc].
4. Otherwise the generic icon for its kind (§1.4).

The Finder keeps what it learns from bundles in the desktop database (`Desktop DB`, `Desktop DF`); ClassicMac reads
the bundles themselves (§8).

### 2.4 Drawing an item

1. The icon is plotted in its 32 × 32 rectangle as `PlotIconSuite` draws it
   ([icon-families.md](../resources/icon-families.md)), unselected and with no label colour.
2. The name is drawn in black under the icon, centred on it, an alias's in italics [Doc: Macintosh Toolbox
   Essentials, for the italics]. Its font, size and place are §5.3's.

## 3. Writing

None.

## 4. Variants

- **System 6 and earlier**: `fdFldr` names the window an item is in, and `kIsOnDesk` puts it on the desktop. From
  System 7, desktop items are in the invisible `Desktop Folder` of each volume. [Doc: Macintosh Toolbox Essentials]
- **Mac OS 8 and later** let the user choose the views font and the grid spacing; where they keep them was not
  traced. The fields of §1 are unchanged.
- **Mac OS 8.5 and later** take an `'icns'` −16455 as a custom icon (§2.3).

## 5. ClassicMac

### 5.1 Reading the fields

- `HfsReader.ReadFolders(input, context)` reads an HFS or HFS Plus volume, plain or wrapped, as `Read` does (same
  checks, same diagnostics) and returns every folder with its `FolderFinderInfo` and dates, the root first marked
  `IsRoot` ([hfs.md §5.2](hfs.md#52-what-comes-out)). `MacFolder.Path` is the folder path the files inside it have.
  `Read`'s result is unchanged. [ClassicMac]
- Other containers (archives, FAT, ISO 9660) record no folder windows: their folders preview with every item arranged
  (§5.2) in a window of the default size. Folders that only files' paths name are items too. [ClassicMac]

### 5.2 Layout

- The bitmap is the window's content area, `frRect`'s size, on white; no frame, title bar, header or scroll bars.
  A folder with no `frRect` gets 480 × 300, taller when its arranged items need it. [ClassicMac]
- Items with no location (§2.2, and (0, 0)) are arranged in the free cells of a grid 80 pixels wide and 64 tall, from
  8 pixels down, across the window, in order; a cell whose icon would overlap a placed icon is skipped. [ClassicMac]
- Items are drawn subfolders first, by name, then files in the order the volume lists them; a later item covers an
  earlier one. [ClassicMac]
- An item no source gives an icon gets an outline drawn in code: a page with a turned corner, a folder or a diamond
  for an application. [ClassicMac]

### 5.3 Labels

- Geneva 9 (family 3), plain or italic, black in `srcOr`, centred on the icon, its top 2 pixels below the icon; no
  white box behind it and no truncation. [ClassicMac: the System 7 Finder's default; Mac OS 8 and later let the user
  choose the views font]
- The font comes from the volume's System file and the font suitcases in the `Fonts` folder beside it, else from the
  fonts in the files open in the viewer, else from `ITextFallback` (an installed font), whose labels only approximate
  the Mac's. [ClassicMac]

### 5.4 Icon sources

- Applications are indexed by creator on first use, once per volume; a file's bundle counts only when its signature
  is the file's creator. Unreadable forks and damaged bundles are passed over. [ClassicMac]
- Generic icons come from the System files on the volume (type `'zsys'`, creator `'MACS'`) and from the open files'
  forks that hold `ICN#` −4000. None are in ClassicMac. [ClassicMac]

## 6. Diagnostics

None of its own. `ReadFolders` reports what `Read` reports ([hfs.md §6](hfs.md#6-diagnostics),
[hfs-plus.md §6](hfs-plus.md#6-diagnostics)), and `hfs.orphan` for a folder whose parent is missing.

## 7. Verification

- `tests/ClassicMac.Files.Tests/FileModelTests.cs` (`MacFolderTests`, `FolderFinderInfoTests`): the 32-byte layout of
  §1.1 read and written, short input, paths. `HfsTests.cs` and `HfsPlusFeatureTests.cs`: `ReadFolders` on HFS, HFS
  Plus, a wrapper, with an orphan, and without the private folder.
- `tests/ClassicMac.Resources.Decoders.Tests/FinderWindowTests.cs`: the window size, the scroll subtraction,
  invisible items, (0, 0) and (−1, −1), the grid, labels through a bitmap font and through the fallback, aliases in
  italics, placeholders, drawing order. `FinderIconResolverTests.cs`: each step of §2.3 and §5.4.
- `tests/ClassicMac.App.Tests/FolderPreviewTests.cs`: folders, a volume's root, a disk inside an archive and an
  archive's folders in the viewer, with icons from custom icons, a folder's `Icon\r`, a bundle and a System file;
  `A_real_volume_s_folder_renders` draws a folder of the volume `CLASSICMAC_FOLDER_VOLUME` names.
- A Mac OS 9.0 volume (SheepShaver, not in the repository) was drawn this way: its folder art (100 tiles of 32 × 32 at
  32-pixel steps) forms its picture, which is what §2.1 and §2.2's [Fitted] rules rest on. Nothing was compared with
  the Finder's own screen.

## 8. Not covered

- Views other than by icon (`frView`), small icons, the label colours, selection, and Mac OS 8's window header and
  grid preferences; whether the Finder's local origin is below the header is open.
- The desktop database (`Desktop DB`, `Desktop DF`): its icons, comments and application list.
- Resolving aliases to their originals' icons; the window's frame and background (Mac OS 8's folder backgrounds).
- Folder records of MFS, FAT and ISO 9660.

## 9. References

1. Apple, *Inside Macintosh: Macintosh Toolbox Essentials* (1992), chapter 7 "Finder Interface": `FInfo`, `FXInfo`,
   `DInfo`, `DXInfo`, the Finder flags, custom icons, bundles, aliases.
2. Apple, *Inside Macintosh: More Macintosh Toolbox* (1993), chapter 5 "Icon Utilities".
3. Apple, Universal Interfaces 3.4, `Finder.h` (flags, `kCustomIconResource`, alias types) and `Icons.h` (generic
   icon resource IDs).
4. Apple, Technical Note TN1150, *HFS Plus Volume Format*: `HFSPlusCatalogFolder`.
