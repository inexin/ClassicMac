# Details tab

Item: P4. Same data as today's `DetailsViewModel` rows (labels: Name, Kind, Type / creator, Mac path, Location, Created, Modified, Data fork, Resource fork, Resources, Compressed, Holds, Finder flags, Read as, With…), regrouped.

## Layout

Two-column grid of cards (CmCardBorder, radius 8, caption header on CmSidebarBackground), 14 gap:

- **File:** Name · Kind · Type / creator (mono, quoted codes) · Mac path (mono 12, wraps) · In (link to the parent node + path; not "Location", which stays the Finder icon position).
- **Forks:** Data fork and Resource fork each with its size in mono and a 6 px bar (CmSegmentTrack, fill CmAccent, length relative to the larger fork); then Resources "614 in 58 types", Compressed "21 resources ('dcmp' 2)", Holds: the first few types in mono + "+54 more" link.
- **Dates:** Created, Modified in mono `yyyy-MM-dd HH:mm:ss`, then a note that depends on the source: HFS and MFS "Mac local time, as stored. No time zone."; HFS Plus, zip and tar "Stored in UTC, shown in your time zone."
- **Finder flags:** chips — set flags filled (CmRowHighlight, CmText), unset flags dashed and muted (Has bundle, Inited, Shared, Invisible, Locked, Custom icon, Stationery, Alias); then Raw `0x2100`, Label, Location `(14, 220)` (the icon position, as today).
- **How it was read** (full width): the container chain as chips with arrows — "Mac OS 9.hfv → Apple partition map → HFS Plus volume “Mac OS 9” → Finder · both forks" (last chip highlighted); right side "No problems found in this file" or the count with a link to diagnostics.

Header action **Copy all** puts the rows on the clipboard as plain "Label: value" lines. Every value stays selectable.

For inputs and resources the same cards apply with their own rows (input: Path, Read as, With; resource: Type, ID, Name, Attributes, Size, Compression).
