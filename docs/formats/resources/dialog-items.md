# Dialog item lists (DITL)

An `i16` count **less one**, then the items [Doc]:

| Size | Type | Meaning |
| --- | --- | --- |
| 4 | | Placeholder: should be 0 (Mac OS 9 keeps a user item's non-zero value and calls it as a drawing procedure) [Code] |
| 8 | `Rect` | The item's rectangle, in the dialog's local coordinates |
| 1 | `u8` | Type; bit 7 ($80, `itemDisable`) disables it, the only flag |
| 1 | `u8` | Data length |
| *n* | | Data, then a pad byte when *n* is odd, for every type [Code] |

- **Count.** A signed number: −1 ($FFFF) or any negative count is an empty list [Code]. It is never checked against
  the resource's size: a count past the data makes the Dialog Manager read past the end [Code]. ClassicMac stops at the
  end of the data ([windows-dialogs.md §10](windows-dialogs.md#10-diagnostics)) [ClassicMac].
- **Types.** Mac OS 9 accepts exactly these values of the type less bit 7; any other item draws nothing but still
  takes space and clicks [Code]. The 68k Dialog Manager tested bits in the order control, editable text, static text,
  icon, picture, so combined values took the first that matched [Code: 68k].

| Type | Name | Data |
| --- | --- | --- |
| 0 | user item | none; the application draws it |
| 1 | help item | `i16` kind, `i16` resource ID (kind 1: an `'hdlg'`; 2: an `'hrct'`; 8: an `'hdlg'` appended, with an `i16` offset that the Dialog Manager overwrites) [Code] |
| 4 | button | its title |
| 5 | check box | its title |
| 6 | radio button | its title |
| 7 | control | a `'CNTL'` ID |
| 8 | static text | the text |
| 16 | editable text | the initial text |
| 32 | icon | an icon ID: `'cicn'` first in a colour dialog, else `'ICON'`; Mac OS 9 draws IDs 0, 1 and 2 as the system's stop, note and caution icons [Code] |
| 64 | picture | a `'PICT'` ID |

- **IDs.** A control's, icon's or picture's ID is the `i16` at the start of the data, whatever the length says [Code];
  ClassicMac reads it only when the length is at least 2 [ClassicMac].
- **Text.** The data is the text itself, with no length byte of its own. In static text, `^0`–`^3` are replaced by
  `ParamText`'s strings when drawn, on a copy (a missing string deletes the citation) [Code].

---

## 1. Writing templates

ClassicMac's editor writes `MENU`, `WIND`, `DLOG`, `ALRT`, `DITL` and `CNTL` back from their fields
(`InterfaceWriter`), in the layouts above [ClassicMac]:

- **`DITL`:** an item's data is made from its kind: the text of buttons, check boxes, radio buttons, static and
  editable text; the resource ID of controls, icons and pictures; help and user items keep their data as stored.
