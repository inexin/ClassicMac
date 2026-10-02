# Hex view

Items: E7 (in-place byte editing), E8 (byte meanings per type). Follows the read-then-edit host ([read-then-edit.md](read-then-edit.md)).

## When it shows

As the app does today: the Hex tab exists only for a resource with no preview (an unknown type), and while bytes are being edited. Files, folders and containers have no hex. There is no source picker; the bytes are the selected resource's.

**Edit Hex** (Ctrl+H, toolbar and Resource menu) opens editing for any resource: it shows the Hex tab, already in editing. Today Ctrl+H opens the hex dialog; E7 replaces that dialog with this in-place view and keeps the shortcut.

## Grid

- Mono 13, line height 24, virtualised as today.
- Columns: offset `0x000000` (CmTextMuted) · 16 byte cells 24 wide with an extra 8 gap after the 8th · Mac OS Roman text column (one 9 px cell per byte; non-printables as "·" in muted).
- Column header row in 11 muted: "Offset", `00 … 0F`, "Mac OS Roman".
- Zero bytes in a lighter colour. Clicking a byte (or its character) selects it; the pair highlights in both columns with CmSelectionInactive.
- Right of the tab strip: **Go to** (mono input, placeholder `0x0000`). Find is a follow-up, not in the first release.

## Byte inspector (right panel, 260, CmSidebarBackground)

- **E7:** "At 0x0003" followed by plain interpretations of the bytes at the cursor: UInt8, Int8, UInt16 BE, Int16 BE, UInt32 BE, OSType (`'Unti'`), Binary.
- **E8, sized separately:** "In this resource" says what the byte is, e.g. "Character 1 of string 1, “Untitled”" or "Length byte of string 2". This needs a field map per resource type (from the typed decoders or a `TMPL`), so it is its own item and may start with `STR#`, `STR ` and template-backed types only.

## Editing

- Entered with Edit Hex or the **Edit Bytes** button in the read-only footer; badge "Editing bytes".
- Cursor byte: CmHexCursor fill with CmOnAccent text. Changed bytes: CmWarningTint background, SemiBold, in both columns.
- Footer: segmented **Overwrite | Insert** (the Insert key toggles), status in mono "0x000A = 32 · 1 byte changed · hex digits type, Insert toggles, Delete removes", **Discard**, **Apply**. Key handling, Apply and Discard as built today (`HexEditor`). When editing ends on a resource that has a preview, the Hex tab hides again.
