# Writing containers

Reading each file with the matching reader gives back its name, Finder info, dates (MacBinary, AppleSingle) and forks.

---

## 1. Saving edited resources back

Edited resources (`ClassicMac.Files.Editing.ForkSaver`, the app's Save) go back into the container they were read from.
Only the resource fork changes; the name, Finder info, dates and data fork are written back as read [ClassicMac].

| The file opened | What Save writes |
| --- | --- |
| A resource fork on its own (a `.rsrc` file, or a data file holding resources) | the fork, in place |
| A data file with an AppleDouble header (`._name`, or `name.rsrc` holding AppleDouble) | the header file ([applesingle-appledouble.md §7](applesingle-appledouble.md#7-writing-appledouble)); the data file is untouched |
| A Basilisk II / SheepShaver folder entry | `.rsrc/name` (made when there was none); the data file and `.finf/name` are untouched |
| A MacBinary I, II or III file | the whole file, as MacBinary III ([macbinary.md §5](macbinary.md#5-writing-macbinary-iii)) |
| A BinHex 4.0 file | the whole file ([binhex.md §8](binhex.md#8-writing-binhex-40)) |
| An AppleSingle file | the whole file ([applesingle-appledouble.md §8](applesingle-appledouble.md#8-writing-applesingle)) |

- A file inside a disk image or archive, a PC Exchange folder or a macOS named fork is saved only with Save As, to any
  of: MacBinary III, BinHex 4.0, AppleSingle, an AppleDouble pair, a Basilisk II folder entry, or the resource fork alone.
- A file in a plain HFS image can also be saved as a copy of the image (`ForkSaver.SaveHfsImageAs`): the image, or the
  image with files and folders already created or deleted by `HfsWriter` ([hfs.md](../file-systems/hfs.md)), with each edited
  fork replaced; the copy is read back with the HFS reader and every replaced fork compared before it is placed. The
  source image is never written.
- **Verified:** the new file is written beside the original under a temporary name and read back with the reader for
  its container; its resources (type, ID, name, attributes but the in-memory changed bit, data), the fork's attributes,
  and the name, Finder info and other fork where the container has them must equal what was meant. Only then does it
  replace the original; otherwise the original is left as it was and the save reports what differed.
- **Backup:** the first save keeps the original as `<file>.orig` (beside the file written, so `._name.orig` for an
  AppleDouble header) unless one exists; later saves keep that first original.
- **Changed on disk:** a file whose size or modification time differs from when it was read is not overwritten
  without asking.
- The fork itself is written in the canonical layout of [resource-fork.md](../resources/resource-fork.md) (the writer's compact
  order), so an unchanged resource keeps its content but not necessarily its offset.

**Editing rules** (`ClassicMac.Resources.Editing`) [ClassicMac, as ResEdit enforces them; the Resource Manager's
AddResource checks less]: a type and ID already in the file is refused; IDs below 128 are allowed after a warning
(reserved for the system); the compressed attribute cannot be set by hand, and new data is stored uncompressed;
Duplicate gives the next free ID from 128 up, with the same name and attributes. Every edit can be undone, also after a
save.
