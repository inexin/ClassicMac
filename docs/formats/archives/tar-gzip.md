# tar and gzip with Mac data

tar and gzip are not Mac formats, but Mac files travel in them: Mac OS X's tar writes an AppleDouble `._name` entry
beside each file with its resource fork and Finder information, and MacGzip compresses a MacBinary file. ClassicMac
reads tar archives (POSIX ustar and pax, GNU long names, V7) and gzip files, and joins each file with its Mac data.

| | |
| --- | --- |
| Identified by | tar: the first header block's checksum, with `ustar` at +$101 or a V7 type flag. gzip: `$1F $8B` at +$00, method 8, no reserved flag bits. Extensions `.tar`, `.gz`, `.tgz` |
| ClassicMac | Reads; `ClassicMac.Files.Archives.TarArchiveReader`, `GzipReader` (the pairing in `UnixArchive`) |
| Verified against | Nothing yet (hand-built archives only) |
| Sources | POSIX (ustar and pax); RFC 1952 (gzip) |

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

### 1.1 tar header block

The fields ClassicMac uses to recognise a tar archive; the rest of the format is POSIX's [Doc: POSIX]:

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$000 | 100 | Name | A V7 archive needs a non-empty name |
| +$094 | 8 | Checksum | Octal: the byte sum of the 512-byte block with this field taken as spaces |
| +$09C | 1 | Type flag | V7: `0`, `1`, `2`, `5` or NUL |
| +$101 | 5 | Magic | `ustar` |

### 1.2 gzip header

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| +$00 | 2 | Magic | `$1F $8B` |
| +$02 | 1 | Method | 8 (deflate) |
| +$03 | 1 | Flags | Bit 2: an extra field follows. Bit 3: a name follows. Bits 5–7 reserved, zero |
| +$04 | 4 | MTIME | Unix seconds, little-endian; 0 for none |
| +$08 | 2 | Extra flags, OS | Not read |
| +$0A | | Extra field, name | When flagged: a `u16` length and the extra field; the name (FNAME), ISO 8859-1, NUL-terminated |

[Doc: RFC 1952]

## 2. Reading

### 2.1 tar

1. Read the entries. Pax `path` records and GNU `L` long names give the full path; names are UTF-8.
2. Regular and contiguous files become files; directories become folder paths; symbolic links become files with a
   link target; a hard link copies an earlier entry's data.
3. The modification time (Unix UTC) is the file's modification date; there is no creation date.
4. Pair `._` entries with their files ([zip.md §2.4](zip.md#24-appledouble-pairing)).

[Doc: POSIX]

### 2.2 gzip

1. Expand the deflate stream; concatenated members are read one after another.
2. The name is the header's FNAME (its last path component) or else the input's name without `.gz`, `-gz`, `_gz` or
   `.z`, with `.tgz` becoming `.tar`.
3. MTIME, if not 0, is the modification date.
4. A tar inside (`.tgz`, `.tar.gz`) or a MacBinary file inside (MacGzip) is opened in turn.

[Doc: RFC 1952]

## 3. Writing

None.

## 4. Variants

- Mac OS X's tar: an AppleDouble `._name` entry beside each file.
- macOS tar's extended attributes in pax records (`SCHILY.xattr.com.apple.*`, `LIBARCHIVE.xattr.*`): not read (§8).
- MacGzip: a gzip file holding a MacBinary file.

## 5. ClassicMac

- tar is read with .NET's `System.Formats.Tar`; gzip with `GZipStream`. [ClassicMac]
- Other entry types (devices, FIFOs) are reported and skipped; pax global headers are skipped. A hard link to no
  earlier entry is reported and skipped. A truncated archive keeps what was read, reported. A damaged header is an
  error. [ClassicMac]
- `..` path components are dropped and reported; names become Mac OS Roman with `?` for characters it lacks, the
  Unicode names kept; Unix times are converted to `ContainerReadOptions.TimeZone`. [ClassicMac]
- The gzip name rule of §2.2 step 2 follows gzip's own; without a host name the file is "gzip data". [ClassicMac]
- The unwrapper tries gzip and tar after zip. `ContainerReadOptions.MaxVolumeEntries` limits tar's entries;
  `MaxExpandedBytesPerInput` the expanded data. [ClassicMac]

## 6. Diagnostics

| Code | Severity | When | ClassicMac does | The Mac does |
| --- | --- | --- | --- | --- |
| `archive.appledouble-invalid` | Warning | A `._` entry is not an AppleDouble header | Keeps it as an ordinary file | Not traced |
| `archive.duplicate-entry` | Warning | Two entries have the same path | Keeps the last | Not traced |
| `archive.entry-skipped` | Info | A tar entry is neither a file, a folder nor a link | Skips it | Not traced |
| `archive.link-target-missing` | Warning | A tar hard link names no earlier entry | Skips it | Not traced |
| `archive.path-unsafe` | Warning | A path has a `..` component | Drops the component | Not traced |
| `archive.truncated` | Error | A tar entry or the archive ends early | Keeps what was read | Not traced |

## 7. Verification

Hand-built archives only (`ZipTarFeatureTests`): tar with AppleDouble pairing, long names and links; a `.tgz` through
gzip and tar; a gzip name from its header.

## 8. Not covered

- Empty folders and folders' Finder information ([zip.md §8](zip.md#8-not-covered)).
- macOS tar's extended attributes in pax records; only `._` entries are read.
- gzip methods other than deflate.

## 9. References

1. The Open Group, *The Single UNIX Specification* (POSIX.1-2017), `pax`: the ustar interchange format and pax
   extended headers.
2. RFC 1952, *GZIP file format specification version 4.3* (1996).
