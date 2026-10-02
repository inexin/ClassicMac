# Read, then edit (and the menu form)

Items: E1 the shared host, E2 the menu form, E5 strings, string lists, text and version.

The Edit tab goes away. Every editable resource opens read only in **Preview**, showing its values next to its preview; editing happens in the same place. The Hex tab already works this way (Edit Bytes → Apply / Discard); this makes every form match.

## The host (E1)

| | Read only | Editing |
| --- | --- | --- |
| Header right | primary **Edit** button (pencil icon) | pill "Editing" (CmRowHighlight, pencil icon, SemiBold 12) |
| Values | plain text, labels in CmTextMuted | inputs |
| Values column top | none | 3 px CmAccent inset line, so the mode is visible |
| Footer (CmSidebarBackground, 1 px CmDivider above) | lock icon + "Read only. Press Edit or double-click a row to change it." + after an apply, "Applied · Undo Edit 'MENU' 129 (Ctrl+Z)" in CmAccent | hint line (CmTextMuted) + error line when invalid + **Cancel** + primary **Apply** |
| Preview title | "Preview" | "Live preview · unapplied changes" |

- Enter editing: the Edit button, the header Edit button, or double-click a row (editing starts with that row selected).
- Esc = Cancel (drop the draft), Ctrl+Enter = Apply. Apply makes one undoable edit, as `ApplyFormCommand` does today, and returns to read only.
- Open question: when the selection changes with unapplied edits, ask or apply? Hex applies today. Decide once and use the same rule for every form.
- Apply is disabled (grey fill, CmTextMuted text) while there is an error. Error line: error icon + message in CmError, 12 Medium.
- The preview redraws from the draft on every change.
- "Edit with template" (today a check box) stays, next to the hint in the footer, where a template applies.

## Menu form (E2)

Layout: values on the left, the preview panel on the right at one third of the width, at most 420 px (CmSidebarBackground header "Preview" + "2× · Mac OS 8/9"; checkerboard behind the rendered menu from `MenuView`).

Read only:
- Summary line: Menu title **File**, Enabled **Yes**, Menu ID **129**, MDEF **0**.
- Table header (CmFontCaption): #, Text, ⌘ key, Mark, Icon, Style, Enabled. Rows 32 high. Dividers show as a short rule + "divider". Missing values as "—". Disabled items in CmTextMuted.

Editing:
- Title (text), Enabled (check box), ID and MDEF (mono inputs), **Add item**, **Add divider**.
- Rows: move up / move down buttons with chevron icons, not text glyphs (also Alt+Up / Alt+Down), Text input, ⌘ key (1 char, mono, centred), Mark select in words with the Mac character code (None, Check mark `$12`, Diamond `$13`, Bullet `$A5`, Other…), so the list needs no symbol glyphs, Icon number, Style as toggle buttons for all seven face bits: B, I, U, Outline, Shadow, Condense, Extend (the first three as letters, the rest in a "More" popover). Editing must keep every bit it does not change, Enabled check box, remove (×).
- Duplicate ⌘ keys: both key fields get a CmError border and CmErrorTint ring; error line "Item 6 (“Save As…”) uses ⌘S, already used by item 5 (“Save”)."
- Hint: "Text “-” makes a divider. Esc cancels, Ctrl+Enter applies."

An edited resource gets its own " •" in the tree (as well as its file), cleared by undo back to the saved state or by Save.

Selection is shared: clicking a row highlights the item in the preview (Mac highlight) and clicking a preview item selects its row. Selected row: CmRowHighlight + 3 px CmAccent left bar.

## Strings, string lists, text, version (E5)

Same host. Read only shows the values as text (strings numbered, string lists as a numbered list, text with its styles applied, version as labelled rows). Editing shows today's inputs inside the host.
