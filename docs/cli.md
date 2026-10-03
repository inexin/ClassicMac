# The command line

`classicmac` reads, lists, converts and extracts classic Mac OS files (`classicmac --help` lists the commands). This
document describes the file commands that work on *Mac paths* (planned in [PLAN.md](PLAN.md), "File commands, shell
and MCP"); the library's `MacPathTree` (`ClassicMac.Files`) implements the paths.

## 1. Mac paths

A Mac path names something inside a host file: the host file's own path, then Mac names, each a folder, a file, or a
container entered as a folder.

```
Mac OS 9.hfv:System Folder:Finder
disk.img:Archive.sit:Folder:File
disk.img/Applications/SimpleText
Mac OS 9.hfv:System Folder:Finder:#rsrc:'vers':1
```

### 1.1 The host file

The host file is the shortest part of the path, ending just before a `:`, `/` or `\`, that is an existing file; the
rest is the Mac part. A path that is only a host file names the host file itself. So `C:\Disks\disk.img:System Folder`
and `/home/me/disk.img/System Folder` both start with their disk image; a folder on the host is never a host file.

### 1.2 Names

- In the Mac part, `:` and `/` both separate names; empty names (`::`, a leading or trailing separator) are skipped.
- A backslash before `:`, `/` or `\` makes that character part of the name: a file named `A/B` is `A\/B`. A backslash
  before any other character is itself.
- Names compare as an HFS catalog compares them: ignoring case, not diacritics (`finder` is `Finder`, `e` is not
  `é`), with an exact match preferred. Names that are not Mac OS Roman (from zip and HFS Plus) compare ignoring case.
- The path ClassicMac prints for an entry joins the escaped names with `:` after the host file's path.

### 1.3 Containers

A file whose data fork holds files (a disk image, a partition map, an archive, a MacBinary, BinHex or AppleSingle
file…) is a container, and its contents are its children, as the app's tree shows them: its files, grouped into folders
by their folder paths. A container is read only when a path goes into it, one level at a time. A container that holds
only one container (a MacBinary file of a disk image, a disk image's disk) shows that container's contents in its
place; the name of the container it holds may be given or left out (`disk.bin:File` and `disk.bin:Disk Image:File`).

### 1.4 Resources

After a file, `#rsrc` is its resource fork (from the resource fork, an AppleDouble or Basilisk II companion, or a plain
host file that is a resource fork). Below it, a type is `'TYPE'` in single quotes (inside them `:` and `/` are part of
the type), or bare as its four characters (`TEXT`); below the type, a resource is its ID
(`-16455`). A real file named `#rsrc` wins over the fork.

| Path ends with | Names |
| --- | --- |
| `…:Finder` | the file |
| `…:Finder:#rsrc` | its resource fork |
| `…:Finder:#rsrc:'snd '` | its `'snd '` resources |
| `…:Finder:#rsrc:'snd ':128` | `'snd '` 128 |

## 3. Write commands

Each write command changes one thing on a Mac path, through the library's `InputEditSession`
(`ClassicMac.Files.Editing`), and never changes its input unless asked.

### 3.1 What can be written

- **A plain HFS volume image** (the host file is the volume, with no partition map or disk image wrapper): files and
  folders added, deleted (a folder only with `--recursive` when it holds anything), renamed in their folder, a file's
  type, creator and Finder flags and a folder's Finder flags set, and a file's resources added, replaced and deleted.
  The rules are [hfs.md §3](formats/file-systems/hfs.md#3-writing).
- **One Mac file** (a resource fork file, MacBinary, BinHex, AppleSingle, AppleDouble or Basilisk II file): its
  resources, and its type, creator and flags (a resource fork file has none). It is written back in its own form.
- Nothing else: a path that goes into an archive, a disk image of another kind, or a container inside the volume is
  refused.

### 3.2 Commands

| Command | Arguments and options | Does |
| --- | --- | --- |
| `put` | `<host file> <Mac path>` `[--name N] [--type T] [--creator C]` | Adds a host file: into the folder the path names (keeping its name), or as the file the path names. The host file is read with its AppleDouble or Basilisk II companions, MacBinary and AppleSingle unwrapped, else its bytes are the data fork |
| `mkdir` | `<Mac path>` | Makes an empty folder |
| `rm` | `<Mac path> [--recursive/-r]` | Deletes a file, or a folder (with everything in it only with `-r`) |
| `rename` | `<Mac path> <new name>` | Renames a file or folder in its folder (names are at most 31 bytes, unique as HFS compares them) |
| `set` | `<Mac path> [--type T] [--creator C] [--flags F]` | Sets a file's type and creator; `--flags` replaces the Finder flags: a number (`0x4000`, `$4000`, `16384`) or flag names joined with commas (`Invisible,HasBundle`; `IsInvisible` too) |
| `res-add` | `<file>:#rsrc:<type>:<ID> <data file> [--name N] [--replace]` | Adds a resource with the data file's bytes; one that exists is replaced only with `--replace` |
| `res-rm` | `<file>:#rsrc:<type>:<ID>` | Deletes a resource |

Every command takes:

| Option | Meaning |
| --- | --- |
| `-o`, `--output <file>` | Write the input with the change to this new file (verified by reading it back). It cannot be the input |
| `--in-place` | Write over the input itself (verified first), keeping the original as `<input>.orig` the first time |
| `--dry-run` | Print the change and write nothing (no `-o` needed) |
| `--json` | Print the result as JSON (§3.3) |

One of `-o` and `--in-place` is needed unless `--dry-run` is given.

### 3.3 Output

The text output is one line per change, `<action> <path> (<detail>)`, then `Wrote <file>` for each file written, or
`Dry run: nothing written.`. Paths are inside the volume, without its name (`Docs:Letter`), and empty for a single-file
input. With `--json`:

```json
{
  "input": "/disks/disk.img",
  "dryRun": false,
  "written": [ "/disks/out.img" ],
  "changes": [ { "action": "mkdir", "path": "Docs:Old", "detail": "a new folder" } ]
}
```

`action` is `add`, `mkdir`, `delete`, `rename`, `set`, `res-set` or `res-delete`. An AppleDouble pair or a Basilisk II
entry writes more than one file.

### 3.4 Exit codes

0 success; 2 a usage error or a refused change (a path that names nothing, a name in use, a folder that is not empty,
an input ClassicMac does not write), with the reason on standard error; 4 the file could not be written.
