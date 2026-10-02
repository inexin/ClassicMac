# Control templates (CNTL)

| Offset | Size | Type | Meaning |
| --- | --- | --- | --- |
| +$00 | 8 | `Rect` | The control's rectangle, in the window's local coordinates |
| +$08 | 2 | `i16` | Initial value |
| +$0A | 1 | `u8` | Visible (non-zero) |
| +$0B | 1 | | Filler |
| +$0C | 2 | `i16` | Maximum |
| +$0E | 2 | `i16` | Minimum |
| +$10 | 2 | `i16` | Control definition ID: the `'CDEF'` resource × 16 + a 4-bit variation (`'CDEF'` 0 when missing) |
| +$12 | 4 | `i32` | Reference value |
| +$16 | 1 + *n* | Pascal string | Title |

A `'cctb'` of the same ID gives the control's colours [Code].

Classic definition IDs [Doc]: 0 `pushButProc`, 1 `checkBoxProc`, 2 `radioButProc` (+8 `useWFont`: the window's
font), 16 `scrollBarProc`, 1008 `popupMenuProc` (+1 `popupFixedWidth`, +4 `popupUseAddResMenu`, +8 `popupUseWFont`).
Mac OS 9 draws the first three with Appearance's buttons (368–370) [Code].

A **pop-up menu** reads the fields otherwise [Code]:

| Field | Meaning |
| --- | --- |
| Minimum | The `'MENU'` ID |
| Maximum | The title's width in pixels |
| Value | Low byte: the title's justification (0 left, 1 centre, −1 right); bits 8–14: its style (face bits × 256); bit 15: no style |
| Reference value | With `popupUseAddResMenu`, the resource type whose names are added to the menu |

Once made, the control sets its minimum to 1, its maximum to the number of items and its value to 1 [Code].
