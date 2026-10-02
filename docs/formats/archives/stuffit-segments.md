# StuffIt split files (SegmentIt)

Each segment begins with a 100-byte `$B0 56 00` header. Byte 3 is its one-based volume number; byte 4 and the following
bytes hold the shared MacRoman filename. Bytes 68–93 contain Finder type/creator/flags, creation and modification
dates, and the resource- and data-fork lengths. Segments in a set repeat the filename and metadata; their payloads
concatenate in volume order after each 100-byte header is removed. The reader discovers sibling segments by matching
the filename and shared metadata, opens a set from any segment, restores both forks and Finder metadata, and reports a
missing volume rather than returning truncated forks. When the embedded filename ends in `.sit`, the default unwrapper
continues into the reconstructed StuffIt archive. This layout and sibling matching are **[Fitted]** against
[XADMaster's split-file parser](https://sources.debian.org/src/unar/1.10.8%2Bds1-9/XADStuffItSplitParser.m/); feature
tests use hand-built volumes, and an archive made with the original SegmentIt application remains to be verified.

StuffIt 1.5.1's own Other > Segment… command writes the same layout with the magic `$41A7` in place of `$B056`: a
100-byte header (bytes 2–3 the segment number, then the Pascal filename), then up to the segment size minus 100 bytes
of the file, in segment order. The segment files are type `SegM`, creator `SIT!`. Bytes 68–93 hold the source file's
type/creator/flags, dates and fork lengths as above. The bytes after the filename's length and 94–99 are uninitialised
(they differ between segments of one set) and are never read; the Pascal filename may be shorter than the file's name
(the sample set records `non` for `fx151_non.sit`), and the reassembled file takes it as is. Segments of one set must
share the magic, filename and metadata. The reader reassembles a set from any segment and reports a missing segment
as `archive.missing-volume`. This layout is **[Fitted]** to StuffIt 1.5.1's segments of a 41 974-byte archive
(`TestData/StuffIt151/fx151_non.seg1`–`seg5`), not read from its code.
