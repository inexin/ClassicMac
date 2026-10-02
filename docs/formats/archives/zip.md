# Zip with Mac data

Zip is not a Mac format, but Mac files travel in it with their resource fork and Finder information stored beside
the data: as AppleDouble `._` entries (Mac OS X's Archive Utility, in a `__MACOSX` folder or beside the file), or in
the Mac extra fields of Info-ZIP's Mac port and ZipIt. ClassicMac reads zip archives, stored and deflated, and joins
each file with its Mac data.

| | |
| --- | --- |
| Identified by | A local header `PK\3\4` at +$00, or an empty archive's end record `PK\5\6` at +$00; extension `.zip` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.ZipReader` (the pairing in `UnixArchive`) |
| Verified against | Nothing yet (hand-built archives only) |
| Sources | PKWARE's `APPNOTE.TXT`; Info-ZIP's `proginfo/extrafld.txt`. Other readers (behaviour only): Info-ZIP's Zip and UnZip |

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

### 1.1 Zip structures

The archive's own structures (local headers, the central directory, the end-of-central-directory record, ZIP64) are
APPNOTE's, little-endian [Doc: APPNOTE]. ClassicMac uses the end record's entry count, central directory size and
offset; each central entry's version made by (host in the high byte: 0 MS-DOS, 3 Unix, 7 Macintosh), flags, method,
DOS time and date, CRC-32, sizes, name, extra fields, external attributes (Unix mode in the high 16 bits, the DOS
directory attribute `$10`) and local header offset; each local header's name and extra lengths and extra fields.

### 1.2 Mac extra fields

From Info-ZIP's `proginfo/extrafld.txt` [Doc: extrafld.txt]:

| Tag | Writer | Layout |
| --- | --- | --- |
| `$07C8` | Info-ZIP (old, J. Lee) | `JLEE`, `FInfo` (16), creation and modification dates (Mac, local), flags (bit 0: this entry is the data fork), directory ID, an optional volume name; big-endian. The entry's name carries an extra `d` or `r` |
| `$334D` "M3" | Info-ZIP (new) | BSize (`u32`, the attributes' expanded size), flags (`u16`: bit 0 data fork, bit 2 attributes stored, bit 3 64-bit dates, bit 4 no GMT offsets), type, creator. The local copy adds, unless bit 2, a compression type (0 stored, 8 deflate) and the CRC-32 of the expanded attributes, then the attributes: `fdFlags`, `fdLocation`, `fdFldr`, `FXInfo` (16), version, access, creation, modification and backup dates (Mac local; 32-bit unless bit 3), GMT offsets, charset, path, comment |
| `$2605` | ZipIt | `ZPIT`, the name (a length byte and Mac OS Roman), type, creator, then Finder flags and a reserved word; big-endian. Its entries hold MacBinary data |
| `$2705` | ZipIt 1.3.5 and later | `ZPIT`, type, creator, Finder flags and a reserved word; files without MacBinary data |
| `$2805` | ZipIt (folders) | `ZPIT`, `frFlags`, view |
| `$5455` | Extended timestamp | A flags byte, then the modification, access and creation times the flags name (the central copy has at most the modification time); Unix seconds UTC, little-endian |

The document does not give the M3 field's byte order; Info-ZIP's readers take BSize, the flags, the compression type,
the CRC and the attributes' numbers little-endian, like zip's own fields [Reference: Info-ZIP]. Info-ZIP's Mac port
stores a resource fork as an entry of its own under `XtraStuf.mac/` followed by the file's path
[Reference: Info-ZIP Mac port, `ResourceMark`].

## 2. Reading

### 2.1 The archive

1. Find the end-of-central-directory record in the last 65,557 bytes (22 bytes plus the longest comment), searching
   back for its signature with a comment length that fits.
2. If a count, size or offset is all ones and a ZIP64 locator precedes the record, read the ZIP64 end record it points
   to; the ZIP64 extra field (`$0001`) gives an entry's 64-bit sizes and offset, in the fixed order.
3. Data before the archive (a self-extractor's code) shifts every offset by the same amount: the end record's position
   less the central directory's recorded offset and size.
4. Read the central entries; find each entry's data through its local header, taking the name and extra lengths from
   there, not from the central copy.
5. Expand methods 0 (stored) and 8 (deflate); check the CRC-32.

[Doc: APPNOTE]

### 2.2 Names and paths

1. Names are UTF-8 when general-purpose flag bit 11 is set [Doc: APPNOTE].
2. Otherwise an ASCII name is ASCII; a name from a Macintosh host (version made by 7) is Mac OS Roman; any other name
   that is valid UTF-8 is UTF-8, since Mac OS X's Archive Utility writes UTF-8 without the flag [Fitted]; the rest are
   CP437, as APPNOTE says.
3. Paths split on `/`, and also on `\` from an MS-DOS host. Empty and `.` components are dropped.
4. A directory is an entry whose name ends in `/`, or has the Unix or DOS directory attribute.
5. A Unix symbolic link (mode `$A000`) is a file whose link target is its data, as UTF-8.

### 2.3 Mac data from extra fields

1. Read the local header's extra fields first (they are the full forms); the central copies fill what they lack.
2. `$07C8`: Finder information and dates; remove the name's extra `d` or `r` when it matches the fork; an `r` entry is
   the file's resource fork.
3. `$334D`: type, creator, Finder flags, location, folder, `FXInfo` and dates; expand the attributes if compressed and
   check their CRC. A resource-fork entry's `XtraStuf.mac/` prefix is removed before pairing.
4. `$2605`: the Mac name replaces the entry's last path component; type, creator and flags. `$2705`: type, creator,
   flags. `$2805`: ignored.
5. Dates, best first: the Mac extra fields' dates (Mac local time, used as they are), the extended timestamp `$5455`
   (Unix UTC), then the DOS date and time (local; an impossible date is no date).

[Doc: extrafld.txt]

### 2.4 AppleDouble pairing

The same for zip and tar ([tar-gzip.md](tar-gzip.md)):

1. An entry named `._name`, in the file's own folder or in the same folder under a top-level `__MACOSX/`, is an
   AppleDouble header file ([applesingle-appledouble.md](../containers/applesingle-appledouble.md)) for `name`.
2. Its resource fork, Finder information (unless all zero) and dates go to the file of that path.
3. Without such a file it becomes a file of its own with an empty data fork (a Mac application's empty data fork is
   often left out); when the path is a folder, it is the folder's Finder information and is dropped.
4. A `._` entry that is not an AppleDouble header stays an ordinary file.
5. The `__MACOSX` folder itself is not part of the result.

## 3. Writing

None.

## 4. Variants

- Mac OS X's Archive Utility: AppleDouble entries under `__MACOSX/`, UTF-8 names without flag bit 11.
- Info-ZIP's Mac port: the old `$07C8` or the new `$334D` field, the resource fork as a separate entry.
- ZipIt: `$2605` with MacBinary data in the entry, or `$2705` (1.3.5 and later) without.

## 5. ClassicMac

- Multi-disk archives are refused. Methods other than 0 and 8 and encrypted entries (flag bit 0) are reported and
  skipped. A CRC-32 mismatch is reported and the data kept. A stored entry whose sizes differ, or a deflated entry
  that expands to fewer bytes than its size, is reported and the bytes there kept. [ClassicMac]
- `..` components are dropped and reported. [ClassicMac]
- File names become Mac OS Roman, with `?` for characters it lacks; the Unicode names are kept. [ClassicMac]
- The ZipIt fields' Finder flags and reserved word are read when present: the document lists them only for `$2705`.
  [ClassicMac]
- A damaged Mac extra field is reported and ignored; an M3 attributes CRC mismatch is reported and the attributes
  used anyway. [ClassicMac]
- Unix times are converted to `ContainerReadOptions.TimeZone`. Unix permission bits are not used. [ClassicMac]
- Two entries with the same path keep the last, reported. A central directory that lists more entries than it holds
  is reported and the entries read kept. [ClassicMac]
- ZipIt entries hold MacBinary data, which the unwrapper opens in turn. [ClassicMac]
- The unwrapper tries zip after LHA. `ContainerReadOptions.MaxVolumeEntries` limits the entries;
  `MaxExpandedBytesPerInput` the central directory and the total expanded size. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.appledouble-invalid` | Warning | A `._` entry is not an AppleDouble header | Keeps it as an ordinary file | Not traced |
| `archive.count-mismatch` | Error | The central directory ends before its listed entry count | Keeps the entries read | Not traced |
| `archive.duplicate-entry` | Warning | Two entries have the same path | Keeps the last | Not traced |
| `archive.encrypted` | Warning | An entry is encrypted | Skips it | Not traced |
| `archive.extra-field-crc` | Warning | An M3 field's attributes CRC does not match | Uses the attributes | Not traced |
| `archive.extra-field-invalid` | Warning | A Mac extra field is damaged | Ignores the field | Not traced |
| `archive.fork-checksum` | Error | An entry's CRC-32 does not match | Keeps the data | Not traced |
| `archive.method-unsupported` | Warning | A method other than stored or deflate | Skips the entry | Not traced |
| `archive.path-unsafe` | Warning | A path has a `..` component | Drops the component | Not traced |
| `archive.truncated` | Error | A stored entry's sizes differ, or a deflated entry expands short | Keeps the bytes there | Not traced |

## 7. Verification

Hand-built archives only (`ZipTarFeatureTests`): `__MACOSX` and side-by-side AppleDouble pairing, a false `._` entry,
the M3 field with compressed attributes, the old `$07C8` field, ZipIt fields with Mac OS Roman names, CP437 and UTF-8
names, encrypted entries and bad CRCs, ZIP64 sizes, the unwrapper. No zip made by Info-ZIP's Mac port or ZipIt has
been checked.

## 8. Not covered

- Empty folders (a `MacFile` has no folder record, so folders exist only in their files' paths) and folders' Finder
  information.
- Methods other than stored and deflate (deflate64, bzip2, LZMA …) and any encryption.
- Multi-disk archives.
- The Info-ZIP Unicode path field `$7075`.

## 9. References

1. PKWARE, *APPNOTE.TXT — .ZIP File Format Specification*.
2. Info-ZIP, `proginfo/extrafld.txt`, and the Zip and UnZip sources (`macos/source`). The Info-ZIP licence
   (BSD-style); reference only.
