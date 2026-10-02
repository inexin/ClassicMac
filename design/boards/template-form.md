# Template form ('TMPL')

Item: E6. Uses the read-then-edit host ([read-then-edit.md](read-then-edit.md)). Example on the canvas: `'BNDL' 128` shown through `'TMPL' “BNDL”` in ResEdit.

## Layout

- Header facts include "Shown through `'TMPL' “BNDL”` in ResEdit" (`TemplateForm.Source`).
- Tab strip right side: segmented **Template | JSON**.
- Note banner at the top when `TemplateForm.Note` is set: CmWarningTint background, CmWarning triangle, text in a darker warning colour, e.g. "2 bytes after the last field are not in the template. They are kept as they are when you apply."
- Right panel (300, CmSidebarBackground): "Template" — where it was found, from `TemplateForm.Source` ("Found in ResEdit (open file)"). If several open files hold a template for the type, say which one is used, using the rule the code actually applies (check `TemplateForms.cs` before writing any wording), then the template's field list in mono with each type code (TNAM, DWRD, OCNT, LSTC, LSTE), indented by nesting. Below: the existing "Edit with template" check box and "Off: the resource opens in Hex only."

## Fields

- Scalar fields in a card: label (CmTextMuted, 180) · value · the field's type code on the right (mono 11, muted). Read only shows values (four-char codes quoted, mono); editing shows inputs.
- Count fields (OCNT and similar, `IsReadOnly` today): always read only, with "kept in step with the list".

## Lists

- A list = heading (13 SemiBold) + "2 items" + in editing **Add type** (labelled from the list's own items).
- Each list item = a card: header strip (CmSidebarBackground) with "1)", its key field inline, a summary ("3 IDs"), and in editing **Insert before** and **Remove** (Remove in CmError text).
- Nested lists inside an item render as a compact table (here Local ID · Resource ID) with "+ Add ID" in editing.
- Adding or removing updates the count fields and the header's Size at once.
