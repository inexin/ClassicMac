# Saving edited resources back into containers

ClassicMac's editor saves an edited resource fork back into the container the file was read from (Save), or into a
new one (Save As). Only the resource fork changes: the name, Finder information, dates and data fork are written back
as read. This file says which container each opened file is saved to and how; the containers' own layouts are in
their files ([macbinary.md](macbinary.md), [binhex.md](binhex.md),
[applesingle-appledouble.md](applesingle-appledouble.md), [host-folders.md](host-folders.md)), and the resource fork's
in [resource-fork.md](../resources/resource-fork.md).

| | |
| --- | --- |
| Identified by | How the file was opened: its host layout ([host-folders.md §2.1](host-folders.md#21-choosing-the-layout)) and the container that held it ([unwrapping.md](unwrapping.md)) |
| ClassicMac | Writes; `ClassicMac.Files.Editing` (`ForkSaver`), `ClassicMac.Resources.Editing` (`ResourceEditRules`, `EditSession`) |
| Verified against | Nothing yet |
| Sources | ClassicMac's design; the editing rules after ResEdit |

Contents

1. [Layout](#1-layout)
2. [Reading](#2-reading)
3. [Writing](#3-writing)
4. [Variants](#4-variants)
5. [ClassicMac](#5-classicmac)
6. [Diagnostics](#6-diagnostics)
7. [Verification](#7-verification)
8. [Not covered](#8-not-covered)
9. [References](#9-references)

## 1. Layout

None.

## 2. Reading

None.

## 3. Writing

### 3.1 Save

Save writes the edited fork back into the container it came from [ClassicMac]:

| The file opened | What Save writes |
| --- | --- |
| A resource fork on its own (a `.rsrc` file, or a data file holding resources) | the fork, in place |
| A data file with an AppleDouble header (`._name`, or `name.rsrc` holding AppleDouble) | the header file ([applesingle-appledouble.md §3.1](applesingle-appledouble.md#31-appledouble-header-file)); the data file is untouched |
| A Basilisk II / SheepShaver folder entry | `.rsrc/name` (made when there was none); the data file and `.finf/name` are untouched |
| A MacBinary I, II or III file | the whole file, as MacBinary III ([macbinary.md §3](macbinary.md#3-writing)) |
| A BinHex 4.0 file | the whole file ([binhex.md §3](binhex.md#3-writing)) |
| An AppleSingle file | the whole file ([applesingle-appledouble.md §3.2](applesingle-appledouble.md#32-applesingle-file)) |

A MacBinary, BinHex or AppleSingle file is saved back only when it is a plain host file holding exactly one file that
is not itself a container. Anything else (a file inside a disk image or archive, a PC Exchange folder, a macOS named
fork) is saved only with Save As.

### 3.2 Save As

Save As writes the file, with its edited fork, in any of these forms [ClassicMac]: MacBinary III, BinHex 4.0,
AppleSingle, an AppleDouble pair ([host-folders.md §3.5](host-folders.md#35-writing-a-mac-file)), a Basilisk II folder
entry (the same), or the resource fork alone.

A file in a plain HFS image can also be saved as a copy of the image: the image, or the image with files and folders
already created or deleted by the HFS writer ([hfs.md](../file-systems/hfs.md)), with each edited fork replaced. The
source image is never written. [ClassicMac]

### 3.3 The fork

The fork is written in the canonical layout of [resource-fork.md](../resources/resource-fork.md) (the writer's compact
order), so an unchanged resource keeps its content but not necessarily its offset. [ClassicMac]

## 4. Variants

None.

## 5. ClassicMac

**Saving** (`ForkSaver`) [ClassicMac]:

1. **Changed on disk**: a file whose size or modification time differs from when it was read is not overwritten
   without asking (`FileChangedException` unless the caller passes `overwriteChanged`).
2. **Write**: the new file is written beside the original under a temporary name.
3. **Verify**: it is read back with the reader for its container. Its resources (type, ID, name, attributes but the
   in-memory changed bit, data), the fork's attributes, and the name, Finder information and other fork where the
   container has them must equal what was meant. Otherwise the original is left as it was and the save reports what
   differed (`SaveVerificationException`).
4. **Backup**: the first save keeps the original as `<file>.orig` (beside the file written, so `._name.orig` for an
   AppleDouble header) unless one exists; later saves keep that first original.
5. **Replace**: only then does the new file replace the original.

- Save As (`ForkSaver.SaveAs`) overwrites existing files and verifies the result the same way; for an AppleDouble
  pair or a Basilisk II entry it writes with the host writer and compares everything but the name.
- Saving an HFS image copy (`ForkSaver.SaveHfsImageAs`): the copy is read back with the HFS reader and every replaced
  fork compared before it is placed, under a temporary name first; the destination cannot be the source image.
- An AppleDouble header is written with the machine's time zone.

**Editing rules** (`ResourceEditRules`), as ResEdit enforces them; the Resource Manager's AddResource checks less
[ClassicMac]:

- A type and ID already in the file is refused (`edit.duplicate-id`).
- IDs below 128 are allowed after a warning: they are reserved for the system (`edit.reserved-id`).
- The compressed attribute cannot be set by hand (`edit.compressed`); new data is stored uncompressed.
- Duplicate gives the next free ID from 128 up (from 127 down when every ID above is taken), with the same name,
  attributes and data.
- Every edit can be undone (`EditSession`), also after a save.

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `edit.compressed` | Error | an edit sets the compressed attribute by hand | refuses the edit | ResEdit sets it only by compressing |
| `edit.duplicate-id` | Error | an edit gives a resource a type and ID already in the file | refuses the edit | ResEdit refuses it; AddResource does not check |
| `edit.reserved-id` | Warning | an edit gives a resource an ID below 128 | allows it | IDs below 128 are reserved for the system |
| `save.failed` | Error | App only: a save or Save As failed (changed on disk, verification, I/O); the message says why | leaves the original as it was | — |

## 7. Verification

- `tests/ClassicMac.Files.Tests/ForkSaverTests.cs`: `Edits_save_back_into_each_container` (every Save target of §3.1,
  the `.orig` kept once, the rest written back as read), `A_MacBinary_II_file_is_saved_as_MacBinary_III`,
  `A_raw_fork_file_saves_as_a_fork`, `A_file_changed_on_disk_is_not_overwritten_without_asking`.
- `tests/ClassicMac.Files.Tests/HfsSaveAsFeatureTests.cs`: the HFS image copy (§3.2):
  `SaveAsWritesReopenableVolumeAndLeavesSourceUntouched`, `RejectedSaveLeavesSourceAndExistingDestinationUntouched`,
  `SaveAsRefusesToUseTheSourceAsItsDestination`, `ExternalClassicHfsImageCanBeEditedAndReopened`.
- `tests/ClassicMac.Resources.Tests/EditSessionTests.cs`: `Edits_apply_undo_and_redo`,
  `Saving_marks_the_session_clean_and_undo_past_it_dirty`, `The_rules_refuse_clashes_and_hand_set_compression`,
  `Forks_are_compared_by_content`.
- `tests/ClassicMac.App.Tests/EditTests.cs`: `Edits_undo_redo_and_save_back_into_the_file`,
  `Clashing_ids_are_refused_and_new_resources_added`, `Save_as_writes_a_new_container_and_revert_rereads`,
  `Save_as_hfs_image_preserves_the_volume_and_other_forks`.

## 8. Not covered

- Saving back into a file inside a disk image or archive, a PC Exchange folder or a macOS named fork (Save As only).

## 9. References

1. The containers' formats: [macbinary.md](macbinary.md), [binhex.md](binhex.md),
   [applesingle-appledouble.md](applesingle-appledouble.md), [host-folders.md](host-folders.md).
2. The resource fork: [resource-fork.md](../resources/resource-fork.md).
