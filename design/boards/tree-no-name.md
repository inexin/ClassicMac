# Tree: invisible files and files with no name

Items: T1, T2, T3. A tree-only fix: a shorter list that shows the files that matter by default. Nothing is removed from the volume, the folder preview or exports.

## Problem

A folder can hold many files whose names are empty or only whitespace (often folder art: icon-only files arranged into a picture, but not always). Each becomes a blank tree row; the Realmz 8.0.7 folder shows 15. The folder's custom-icon file `Icon\r` also shows, with a tall row because its name ends in a return.

## Rules

1. **Hidden (T1):** **files** with the Finder's invisible flag (`kIsInvisible`, $4000), such as `Icon\r` and the desktop database files. The Finder never showed them; the folder preview already leaves them out (`FolderPreview.cs`). Folders are never hidden by this rule: on Mac OS 9 `Desktop Folder` and `Trash` are invisible folders that hold everything on the desktop. Show them with their usual names and icons, as the Finder shows the desktop and the Trash, at the volume's top level.
2. **Grouped (T2):** files whose name is empty or only whitespace go under one collapsed "No name" node in their folder, when the folder has 2 or more. Whitespace = space, option-space ($CA), tab, return and other control characters. Nothing else about the file is checked. A single such file stays a normal row titled "(no name)" in italics.
3. Hidden files are removed before grouping is counted.

## Look

- Group row: a stacked pair of document icons, the title "No name" in italics, a count "15 files" right-aligned in CmTextMuted 11. Placed first among the folder's files.
- Expanded, each child shows its stored name with every whitespace character made visible as a small token, so nothing depends on symbol glyphs the UI font may lack:
  - Token look: mono 10 SemiBold, CmTextMuted on CmSegmentTrack, radius 3, 3 px side padding, 2 px apart; inside a selected row, CmSelectionText on a 20% white tint.
  - Labels, ASCII only: `sp` space ($20), `nbsp` option-space ($CA), `tab` ($09), `cr` return ($0D), `lf` line feed ($0A), any other control character in caret form (`^A` for $01, `^?` for $7F).
  - Runs of the same character collapse to one token with a count: `sp×3`.
  - Hovering the name shows the raw bytes in hex (`20 20 CA`).
  - An empty name shows "(empty)" in italics.
  - Plus size and kind on the right. Children open, preview, export and drag like any file.
- Selecting the group row shows the parent folder's preview (the folder art, if any, is already covered there).
- Footer under the tree when anything is hidden: eye-slash icon, "1 invisible item hidden", link "Show" (turns the option off).

## Options (T3)

A display-options button (three lines icon) next to the tree filter opens a popover "Tree display" with two check boxes, both on by default and persisted between sessions:

- Group files with no name
- Hide invisible files

## Search

Search and type-ahead skip hidden and grouped files. The group row itself matches "no name".
