# Diagnostics panel

Items: D1 severity and columns, D2 grouping, D3 collapse and badges.

## Header bar (34, CmChromeBackground)

Left to right: chevron button "Diagnostics" (SemiBold; toggles the panel, `aria-expanded`) · three count badges · spacer · filter field "Filter messages" (200 wide) · segmented filter **All | Warnings and errors | Errors** (the existing `DiagnosticFilter`) · toggle button "By file".

Badges: pill, 12 SemiBold, icon + count. Error = CmError on CmErrorTint, warning = CmWarning on CmWarningTint, info = CmInfo on CmInfoTint. Expanded they read "2 errors", "3 warnings", "27 info"; collapsed just the numbers.

## Columns (D1)

Header row 26, CmFontCaption: **Severity** (sortable, chevron) 110 · **Source** 260 · **Code** 210 · **Message** rest · 90 for a "Show item" link on the selected row.

- Severity cell: 14 px icon + word (Error / Warning / Info) in the severity colour, SemiBold. Error = filled circle with white ×; warning = filled triangle with white !; info = outlined circle with i.
- Source and Code in mono 12; Code in CmTextMuted. Codes are the library's dotted lowercase codes (`archive.fork-crc`, `sound.unknown-format`).
- Rows 24–28 high, CmDivider between. Selected row: CmRowHighlight with a 3 px CmAccent bar on the left. Where a selected row uses CmSelection instead, muted text in it (Code) switches to CmSelectionText. Clicking a row selects its node in the tree (as today).

## Grouping (D2)

- Rows group under the file they come from (the node path, e.g. "Mac OS 9.hfv › System Folder › Finder"). Group header 26–28 high, CmSidebarBackground: chevron, the file's 16 px kind icon, the path in SemiBold, its counts in CmTextMuted ("1 error · 2 info").
- Child rows indent 32. Groups that hold only info start collapsed ("Info-only groups start collapsed" hint on the right).
- "By file" off = one flat list, as today.

## Collapse (D3)

- Ctrl+Shift+D or the header chevron collapses the panel to its 34 px header. The splitter remembers the expanded height.
- Collapsed bar: chevron (rotated), "Diagnostics", error and warning badges with numbers only, then "Latest: archive.fork-crc in Realmz 6.1.sit › Data/Scenario 3" (code in CmError), ellipsised.
- Drag handle: a 6 px strip with a 36 × 2 grip above the header.
