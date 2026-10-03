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
