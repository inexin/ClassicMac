# ShrinkWrap 2.1, DiskDup+ and other raw images

ShrinkWrap 2.1 (1996) writes no format of its own [Verified: ShrinkWrap 2.1 in SheepShaver, floppy and 5 MB volume]:

| Option | Type/creator | Data fork | Resource fork |
| --- | --- | --- | --- |
| ShrinkWrap Image (floppy) | `dImg`/`Wrap` | Disk Copy 4.2, byte for byte; tag size 0 | none |
| DiskCopy Image (floppy) | `dImg`/`dCpy` | Disk Copy 4.2 (junk after the name, [diskcopy42.md §1](diskcopy42.md#1-layout)) | `dCpy` 0: the checksums as text |
| ShrinkWrap Image (volume) | `hdrv`/`Wrap` | the raw volume, no header | none |
| Drive Container | `hdrv`/`D:\>` | the raw volume, no header | none |
| Self-Mounting (floppy) | `APPL`/`sImg` | the raw volume | mounter code; `CKSM` 1 = data checksum (Disk Copy 4.2 sum) |
| Self-Mounting (volume) | `APPL`/`iImg` | the raw volume | mounter code; no checksum |

DiskDup+ 2.9.2 (creator `DDp+`) saves a "DiskDup+" image as type `DDim`: the raw disk, 512-byte sectors with no
header, tags or compression, byte-identical to the source volume [Verified: an 800K HFS floppy saved by DiskDup+ 2.9.2
in SheepShaver]. Its "Disk Copy" option writes Disk Copy 4.2.

The three volume forms have byte-identical data forks. A raw image is recognised by its file system (for HFS, `BD`
at offset 1024), so ClassicMac's volume readers open these directly [Verified].

Disk Copy 6.0 of 1994 (creator `dCpy`, a floppy duplicator unrelated to NDIF) writes its own RLE-compressed and
self-extracting `dImg` variants; they are not specified here [Code: its disassembly shows no NDIF code].
