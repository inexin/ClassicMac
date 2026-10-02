# tar and gzip with Mac data

These are not Mac formats; Mac files travel in them with their resource fork and Finder info stored beside the data.
`ZipReader`, `TarArchiveReader` and `GzipReader` read them; the unwrapper tries them after LHA.

**tar** (POSIX ustar and pax, GNU long names, V7), read with `System.Formats.Tar`. Detected by the first header
block's checksum (the byte sum with the checksum field taken as spaces, octal at +148 [Doc: POSIX]) together with the
`ustar` magic at +257 or, for V7, a name and a type flag of `0`, `1`, `2`, `5` or NUL. Pax `path` records and GNU `L`
long names give the full path; names are UTF-8. Regular and contiguous files become files; directories become folder
paths; symbolic links become files with `SymbolicLinkTarget`; a hard link copies an earlier entry's data
(`archive.link-target-missing`, Warning, when there is none); other types (devices, FIFOs) are `archive.entry-skipped`
(Info). The modification time (Unix UTC) is converted to the reading zone; there is no creation date. A truncated
archive keeps what was read (`archive.truncated`).

**gzip** (RFC 1952 [Doc]). Detected by `1F 8B`, method 8 and no reserved flag bits. It expands to one file through
`GZipStream`, which also reads concatenated members, under `MaxExpandedBytesPerInput`. The name is the header's FNAME
(ISO 8859-1, last path component) or else the input's name without `.gz`, `-gz`, `_gz` or `.z`, with `.tgz` becoming
`.tar`. MTIME (if nonzero) is the modification date. A tar inside (`.tgz`, `.tar.gz`) or a MacBinary file inside
(MacGzip) is opened by the unwrapper in turn.

**AppleDouble pairing** (zip and tar alike). An entry named `._name`, in the file's own folder or in the same folder
under a top-level `__MACOSX/`, is an AppleDouble header file ([applesingle-appledouble.md](../containers/applesingle-appledouble.md)) for `name`: its
resource fork, Finder info (unless all zero) and dates go to the file of that path. Without such a file it becomes a
file of its own with an empty data fork (a Mac application's empty data fork is often left out); when the path is a
folder it is the folder's Finder info and is dropped. A `._` entry that is not an AppleDouble header stays an ordinary
file (`archive.appledouble-invalid`, Warning). Two entries with the same path keep the last (`archive.duplicate-entry`,
Warning). The `__MACOSX` folder itself is not part of the result.

Not covered: empty folders (a `MacFile` has no folder record, so folders exist only in their files' paths) and folder
Finder info; zip methods other than stored and deflate (deflate64, bzip2, LZMA, …) and any encryption; the Info-ZIP
Unicode path field `0x7075`; macOS tar's extended attributes in pax records (`SCHILY.xattr.com.apple.*`,
`LIBARCHIVE.xattr.*`), since only `._` entries are read. The Mac extra-field rules are tested on
hand-built archives only; no zip made by Info-ZIP's Mac port or ZipIt has been checked yet.
