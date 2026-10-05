# ClassicMac.Files

Classic Mac OS files as the Mac saw them (name, Finder info, dates, data and resource forks) and every container
they travel in, read through any nesting: a MacBinary file in a StuffIt archive on an HFS CD unwraps to one tree.

| Area | Read | Written |
| --- | --- | --- |
| Single-file wrappers | AppleSingle, AppleDouble, MacBinary I–III, BinHex 4.0, uuencode, PC Exchange, Basilisk II folders | MacBinary III, AppleSingle, AppleDouble, BinHex, Basilisk II |
| Archives | StuffIt 1–5 and segments, Compact Pro, DiskDoubler, PackIt, LHA, zip, tar and gzip with Mac data, `.sea` | |
| Disk images | Disk Copy 4.2, NDIF, DART, UDIF, partition maps, raw CDs and cue sheets, DiskDup+, ROM images | Disk Copy 4.2, NDIF (segmented too) |
| File systems | HFS, HFS Plus and HFSX (plain, wrapped, journaled), MFS, ISO 9660 and High Sierra, FAT | HFS: files and folders added, deleted, renamed, moved, locked; format, resize, defragment |
| First Aid | HFS, HFS Plus and HFSX checked by stage, Disk First Aid's way, and repaired | |

- **`MacFile`** and **`ForkData`**: a file's name (in its volume's encoding), Finder info, dates and lazily read forks.
- **`ContainerUnwrapper`**: tries each `IContainerReader` on a data fork and unwraps what it finds, to a
  `ContainerNode` tree; extra readers plug in.
- **`MacFileResources`**: a file's resources, from its resource fork or a data fork holding one.
- **`InputEditSession`**: edits a file in any container (resources, Finder info, files on a volume) and saves it
  back in place or aside, verified.
- **`HfsFirstAid`**: `Verify` reports problems and a verdict; `Repair` fixes them and verifies again.
- **`MacCommands`**: the Mac-path commands (`ls`, `stat`, `cat`, `find`, `get` and the writes) behind the CLI, MCP
  and the shell.

```csharp
var root = ContainerUnwrapper.Default.Unwrap("Game.sit");
foreach (var node in root.Leaves())
{
    var file = node.File;
    var resources = MacFileResources.Read(file).Fork;
    Console.WriteLine($"{file.Name} {file.FinderInfo.Type}/{file.FinderInfo.Creator} {resources?.Resources.Count ?? 0} resources");
}

var report = HfsFirstAid.Verify(ForkData.FromFile("disk.img"));
Console.WriteLine(report.Summary);
```

Input is treated as hostile: sizes and offsets are checked, nesting, expansion and decompressed sizes are limited
(`ContainerReadOptions`), and only `InvalidDataException` and `EndOfStreamException` escape.

The formats: [containers](https://github.com/inexin/ClassicMac/tree/main/docs/formats/containers),
[archives](https://github.com/inexin/ClassicMac/tree/main/docs/formats/archives),
[disk images](https://github.com/inexin/ClassicMac/tree/main/docs/formats/disk-images),
[file systems](https://github.com/inexin/ClassicMac/tree/main/docs/formats/file-systems).

Part of [ClassicMac](https://github.com/inexin/ClassicMac). MIT.
