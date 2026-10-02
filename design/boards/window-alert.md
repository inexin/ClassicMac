# Window ('WIND') and alert ('ALRT') forms

Item: E4 (control 'CNTL' follows the window pattern). Uses the read-then-edit host ([read-then-edit.md](read-then-edit.md)).

## Window

Read only: property cards (CmCardBorder, radius 8, rows 34 with CmDivider; label column 150 in CmTextMuted):

- **Bounds:** Top, left `40, 6` · Bottom, right `322, 506` · Size `500 × 282 pixels`.
- **Window:** Title “Untitled” · Definition "Document window" + `documentProc · 0` in mono muted · Visible (empty dot + No) · Close box (filled CmAccent dot + Yes) · Reference constant `0` · Auto-position "Stagger on parent window’s screen" + `0x780A`.
- A segmented **Properties | JSON** toggle above the cards (see [property-view.md](property-view.md)).

Right panel (320, CmSidebarBackground): "Bounds on a 512 × 342 screen" — a half-scale screen with the menu bar and the window's content rect and title bar drawn in CmAccent / CmSelectionInactive; a Screen select (512 × 342 Classic, 640 × 480, 832 × 624, 1024 × 768). It follows the draft while editing.

Editing: Bounds as four number inputs · Title text · **Definition as a named select** (Document window · 0, Dialog box · 1, Plain box · 2, Alt dialog box · 3, No grow document · 4, Movable modal · 5, Zoom document · 8, Zoom no grow · 12, Rounded · 16, Other WDEF…) instead of "WDEF × 16 + variation" · Visible and Close box check boxes · Reference value · Positioning word check box + named select.

## Alert

Read only:
- Card: Bounds `40, 40, 160, 380` + "340 × 120" · Item list as a link `'DITL' 129 “Save Changes”` + "3 items" (selects that resource) · Auto-position "Alert position on main screen" + `0x300A`.
- **Stages** table, "What happens the 1st to 4th time the alert is shown in a row": Stage · Default button ("Item 1 “Save”") · Drawn (Yes/No) · Sound (Silent, 1 beep, 2 beeps, 3 beeps). Each stage is 4 bits: one bit for the bold (default) item, which can only be item 1 or item 2, one bit for drawn, two bits for the beep count.
- Clicking a stage selects it and the preview (460 panel, right) redraws that stage: which button has the default ring, and a note "Stage 3: default button is Item 2 “Cancel”, plays 2 beeps."

Editing: bounds inputs, Item list ID input, positioning check box + named select; each stage row gets a Default button select with exactly two choices, "Item 1" and "Item 2" (labelled with their text from the DITL), a Drawn check box and a Sound select.

The alert preview uses the existing dialog renderer with the chosen stage's default item.
