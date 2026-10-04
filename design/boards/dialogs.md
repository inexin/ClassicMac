# Dialogs

Item: P5. Applies to Get Info / New Resource, Import Image or Sound, New File / Import File / New Folder, Unsaved changes and Yes/No confirmations (`Dialogs/EditDialogs.cs`). System pickers stay native.

## Common frame

- CmPaneBackground, radius CmRadiusDialog 10, CmShadowDialog.
- Header 42: title 14 SemiBold, close button (×) 30 × 30 on the right, CmDivider below. Alerts (unsaved changes, confirmations) have no header: icon left, question in 14 SemiBold, detail in CmTextMuted.
- Body padding 16, 14 gap. Labels 12 CmTextMuted in a 70–90 px column; inputs 30 high, radius 6, CmControlBorder; codes and IDs in mono.
- Footer 12 × 16 padding on CmSidebarBackground with CmDivider above. Buttons right-aligned: **Cancel** then the primary action (CmAccent, OnAccent text, SemiBold). A destructive third choice ("Don’t Save") sits on the left.
- Enter = primary, Esc = Cancel. Real `<label for>` association for every field.

## Get Info

Icon tile (48, checkerboard) + name + "Icon family in Finder · 2,240 bytes". Fields Type (mono, 90), ID (mono, 90), Name (full width). Attributes fieldset in two columns: System heap, Purgeable, Locked, Protected, Preload, Compressed (disabled: it can't be set by hand). Buttons Cancel, **OK**.

## Import Image or Sound

Source row: file icon, file name, "32 × 32 · 24-bit". "Make" radio list: Picture `PICT`, Color icon `cicn`, Icon family (`ICN# icl4 icl8 ics# ics4 ics8`, chosen row on CmRowHighlight), One icon kind + select, Cursor `CURS`, Sound `snd ` (disabled for an image: "needs an audio file"). First ID (mono), Name. A 2× preview strip on checkerboard of what will be made, with a note on colour mapping. Buttons Cancel, **Import**.

## Unsaved changes

Warning icon, "Save changes to “Mac OS 9.hfv” before closing?", detail "3 resources in Finder were edited. If you don’t save, the changes are lost." Buttons: **Don’t Save** (left), Cancel, **Save**.
