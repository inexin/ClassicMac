# Browse tree

Items: T4 icons, T5 states, S6 filter and type-ahead.

## Rows

- Height CmRowTree 22, indent CmTreeIndent 16 per level, radius CmRadiusSmall.
- Order inside a row: 12 px disclosure chevron (hidden but space kept on leaves) · 16 px icon · title (CmFontBody; resource types like `'ICN#' (12)` in mono 12) · right-aligned meta in mono 11 at 80% (type · creator for files, size for resources, format and size for the input).

## Icons per node kind (T4)

16 × 16 pixel icons, drawn 1:1 (see pixel rules in [../TOKENS.md](../TOKENS.md)). Finder-like, each with a black outline:

| NodeKind | Icon |
| --- | --- |
| Input (disk image) | hard disk: grey body, green light |
| Container, disk image | floppy: dark body, white label |
| Container, archive | parcel: tan box with a string |
| Folder | folder with tab, lavender fill |
| File, application | diamond, purple fill |
| File, other | page with folded corner and text lines |
| ResourceType | two stacked cards with blue lines |
| Resource | card with a 2 × 3 grid of black "bytes" and a blue edge |
| Loading | none; a spinner takes its place |

A resource of an icon type (`ICN#`, `icl8`, `ics#`…, `cicn`, `CURS`) shows its own 16 px icon instead (ics8/ics4/ics# or the 32 px icon scaled by nearest neighbour to 16 only when no small one exists). Files show their own Finder icon now, through `FinderIconResolver` (custom icon, then the application's bundle icon, then the generic icon), the same resolution the folder preview uses. Open question: should folders also show their custom icons?

Replaces the 14 px vector glyphs in `Views/NodeIcons.cs`.

## States (T5)

| State | Look |
| --- | --- |
| Selected, tree focused | CmSelection background, CmSelectionText; the right-aligned meta also in CmSelectionText (muted text fails contrast on the dark selection) |
| Selected, focus elsewhere | CmSelectionInactive background, CmText |
| Unsaved edits | " •" after the title in CmAccent SemiBold (CmSelectionText when selected), on the file node as today and also on each edited resource (T6). |
| Loading (placeholder child) | 16 px spinner (CmBorder ring, CmAccent arc) + italic "Loading…" in CmTextMuted. No counts: a resource fork is read in one call and there is no progress reporting. |
| Unread container | chevron closed + a small chip "not read" (11, CmTextMuted, dashed 1 px border, radius 4) |
| Drag source | 1 px dashed CmAccent outline; status bar says "Writing MacBinary…" while the temp file is written |

## Filter and type-ahead (S6)

- Filter field at the top of the tree pane: 28 high, search icon, placeholder "Filter tree", hint "Ctrl+F" in mono 11 on the right.
- Type-ahead: typing while the tree has focus opens a pill at the bottom of the tree (CmPaneBackground, 1 px CmAccent border, CmShadowPopover): search icon, the typed text in mono with a caret, "1 of 2 loaded matches", key hints F3 and Esc.
- Matching rows highlight the matched letters (tint #FFE58A on the current match, lighter on others); non-matching siblings dim to CmTextMuted. Only loaded nodes are searched; the Ctrl+F filter also reads unread containers.
- Hidden and grouped files ([tree-no-name.md](tree-no-name.md)) are skipped.
