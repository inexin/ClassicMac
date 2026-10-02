# Zip with Mac data

These are not Mac formats; Mac files travel in them with their resource fork and Finder info stored beside the data.
`ZipReader`, `TarArchiveReader` and `GzipReader` read them; the unwrapper tries them after LHA.

**Zip** (PKWARE's `APPNOTE.TXT` [Doc]). Detected by a local header (`PK\3\4`) or an empty archive's end record
(`PK\5\6`) at offset 0. The reader finds the end-of-central-directory record in the last 65,557 bytes, and the ZIP64
end record through its locator when a count, size or offset is all ones; the ZIP64 extra field (`0x0001`) supplies an
entry's 64-bit sizes and offset in the fixed order. Data before the archive (a self-extractor's code) shifts every
offset by the same amount, which is allowed for (the end record's position minus the central directory's recorded
offset and size). Multi-disk archives are refused. Each entry's data is found through its local header (name and extra
lengths from there, not the central copy). Methods 0 (stored) and 8 (deflate, `DeflateStream`) are read; other methods
are `archive.method-unsupported` and encrypted entries (flag bit 0) `archive.encrypted`, both skipped. The CRC-32 is
checked (`archive.fork-checksum`, data kept). Directories are entries ending in `/`, or with the Unix or DOS directory
attribute; a Unix symbolic link (mode `0xA000`) becomes a file whose `SymbolicLinkTarget` is its UTF-8 data.

Names are UTF-8 when general-purpose flag bit 11 is set [Doc]. Otherwise ASCII names are ASCII; a name from a
Macintosh host (version-made-by high byte 7) is Mac Roman; any other name that is valid UTF-8 is read as UTF-8, since
Mac OS X's Archive Utility writes UTF-8 without the flag **[Fitted]**; the rest are CP437, as APPNOTE says. Paths split
on `/` (and `\` from an MS-DOS host); empty and `.` components are dropped, and `..` components are dropped with
`archive.path-unsafe` (Warning). File names become Mac Roman, with `?` for characters it lacks; the Unicode names are
kept.

Dates, best first: the Mac extra fields' dates (Mac local time, used as is), the extended-timestamp field `0x5455`
(Unix UTC seconds, converted to `ContainerReadOptions.TimeZone`) [Doc: Info-ZIP `extrafld.txt`], then the DOS date and
time (local; an impossible date is no date). Unix permission bits are not used.

Mac extra fields, from Info-ZIP's `proginfo/extrafld.txt` [Doc]. The local header's copy is read first (it is the full
form) and the central copy fills gaps; a damaged field is `archive.extra-field-invalid` (Warning) and ignored.

| Tag | Writer | Layout | ClassicMac |
| --- | --- | --- | --- |
| `0x07c8` | Info-ZIP (old, J. Lee) | `"JLEE"`, FInfo (16), creation, modification (Mac dates), flags (bit 0: data fork), dirID, optional volume name; big-endian | Finder info and dates. The name has an extra `d` or `r`, removed when it matches the fork **[Fitted]**; the `r` entry becomes the file's resource fork |
| `0x334d` "M3" | Info-ZIP (new) | BSize (u32), flags (u16; bit 0 data fork, bit 2 attributes stored, bit 3 64-bit dates, bit 4 no GMT offsets), type, creator; the local copy adds a compression type (0 stored, 8 deflate) and CRC-32 unless bit 2, then the attributes: fdFlags, fdLocation, fdFldr, FXInfo (16), version, access, creation, modification, backup (Mac local), GMT offsets, charset, path, comment | Type, creator, Finder flags, location, folder, FXInfo and dates. The byte order is not stated; the numbers are read little-endian like zip's own fields **[Fitted]**. A CRC mismatch is `archive.extra-field-crc` (Warning; used anyway). A resource-fork entry is filed under `XtraStuf.mac/` + the file's path **[Fitted]**, which is stripped before pairing |
| `0x2605` | ZipIt | `"ZPIT"`, name length, Mac Roman name, type, creator, then optionally Finder flags and a reserved word **[Fitted]**; big-endian | The Mac name replaces the entry's last name component; type, creator, flags. Its entries hold MacBinary data, which the unwrapper opens in turn |
| `0x2705` | ZipIt 1.3.5+ | `"ZPIT"`, type, creator, optional Finder flags and reserved word | Type, creator, flags |
| `0x2805` | ZipIt (folders) | `"ZPIT"`, frFlags, view | Ignored (folders are not kept) |

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
