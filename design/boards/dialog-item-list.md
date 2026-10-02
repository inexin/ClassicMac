# Dialog item list ('DITL')

Item: E3. Uses the read-then-edit host ([read-then-edit.md](read-then-edit.md)).

## Layout

Header: name (`128 “Find”`), "Dialog item list in SimpleText"; facts Type `'DITL'`, ID, Items, **Used by** `'DLOG' 128` (mono). Values on the left, a 400 px preview on the right: the dialog drawn by `DialogView` at 1× on a checkerboard, with a note "Drawn from 'DLOG' 128 · 300 × 106".

## Read only

Columns: # (mono, muted) · Kind · Text or resource (`“Find”`, or "—" muted) · Top, left, bottom, right (mono) · Enabled (Yes / No, "No" muted). Rows 34 high.

## Editing

Each row: # · Kind select (Button, Check box, Radio button, Control, Static text, Edit text, Icon, Picture, User item, Help item — the existing `DialogItemRow.Kinds`) · Text input (or Resource ID for icon, picture, control) · four mono number inputs for the bounds · Enabled check box · remove. **Add item** under the list. Hint: "Item 1 is the default button. Esc cancels, Ctrl+Enter applies."

## Selection both ways

- Clicking a row selects the item in the preview: a 1 px dashed CmAccent outline 3 px outside its bounds.
- Clicking an item in the preview selects its row.
- Editing text or any bound redraws the preview on each keystroke; the item moves or resizes in place.
- Double-click a row in read only to start editing at that row.

The preview is Mac content: it keeps Mac OS 9 Platinum colours in dark mode, on the dark checkerboard.
