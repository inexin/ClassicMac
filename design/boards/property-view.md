# Property view (instead of JSON)

Item: P2. Today `vers`, window, control and menu-bar resources (and others shown as `PreviewKind.Json`) preview as pretty JSON. Show them as labelled values instead, with JSON one click away.

- Segmented **Properties | JSON** above the values (Properties default; the choice persists per session).
- Values grouped in cards: a caption header (CmFontCaption on CmSidebarBackground), then rows: label column ~150 in CmTextMuted, value in CmText.
- Plain words first, raw values beside them in mono CmTextMuted: "Document window `documentProc · 0`", "Stagger on parent window’s screen `0x780A`".
- Booleans as a small dot + Yes / No (filled CmAccent for yes, outlined for no).
- Numbers that are coordinates in mono; derived values (Size `500 × 282 pixels`) after the raw ones.
- Every value selectable; right-click offers copy as decimal, hex or JSON.
- When the resource has a form, the header's **Edit** turns these same cards into inputs ([read-then-edit.md](read-then-edit.md)); the property view is the read-only state.
- Example: [window-alert.md](window-alert.md).
