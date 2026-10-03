# Changelog

## Unreleased

- File systems: HFS volumes Mac OS wrote can be edited: their catalog index keys, stored at the maximum length, were
  refused as invalid; the writer now accepts them and writes rebuilt index keys the same way (hfs.md §1.8).
- Documents: Word 6 documents read: checked against 10 that Word 6.0 for the Macintosh wrote, their FIB identifier
  ($A5DC) is now accepted; line breaks, small caps, colour and tables (as HTML tables) as for Word 4 and 5.
- Documents: Word 4 and 5 checked against 21 documents Word 4.0 and 5.1a wrote (now test fixtures): styles are found by
  their number, not their place (Normal paragraphs no longer took Heading 1's spacing and font); line breaks stay in
  their paragraph; small caps and colour show; tables are HTML tables with their cell widths; fast-saved documents
  read through their piece table (a game's spell list now opens, 9 tables).
- Documents: Word 5 documents saved normally are read: the header flag $04 is set in every Word 5.1a document, so it no
  longer marks a fast save; only a piece table (zone 18) does. Checked against real Word 5.1a and 4.0 documents.
- Viewer: byte meanings for `'CODE'` resources in the hex inspector: `'CODE'` 0's A5 sizes and jump-table entries
  (near and far forms, the far marker), a segment's near or far header field by field, its code and far relocation
  lists.
- Viewer: help pages' CSS pictures (`url(…)` in stylesheets, `style` elements and `style` attributes) are put into
  the page, resolved from the stylesheet's own folder.
- Viewer: styled text (SimpleText files, TEXT + styl) is drawn on white paper in the dark theme too, so its black runs
  stay readable (Mac content keeps its own colours).
- Viewer, CLI and MCP: an alias whose original is not found says why: missing from its disk, on a disk that is not open
  (with the kind of disk, e.g. an 800K floppy disk), or on a network volume (with the AppleShare server, zone, volume and
  user from its mount information). The tree dims such an alias and marks its badge (a cross, a disk, a globe); the
  header, the preview's card and Details say it; `stat` and its JSON give `state` and `explanation`.
- CLI: `classicmac shell <input>`, a DOS-like shell on one input (docs/cli.md §5): `cd` into folders, disk images,
  archives, forks and types (`..`, `:`, paths from the input's name), `dir`, `type`, `info`, `res`, `find`, `copy` out
  to the host and in from it, `del`, `md`, `ren`, `set`, `save` (in place, keeping `.orig`) and `save as`; changes are
  seen at once and written only when saved; leaving with unsaved changes asks Save, Save As or Discard. At a terminal:
  line editing, history and tab completion of names (quoted when they hold spaces); with `--script` or piped input it
  runs without questions, stops at the first failure, and with `--json` prints one JSON object per command.
- Viewer: DOCMaker, SimpleText and Word documents preview as the HTML Convert Documents writes, in the web view the help
  pages use (stylesheet and pictures put into the page): a DOCMaker document's contents page and chapters in the page
  list, links and Back pictures turning its pages, Back; Rendered | Text, the text also when there is no web view. The
  app's own document layout is removed.
- CLI: `classicmac mcp`, an MCP server over standard input and output (docs/cli.md §4, on the official C# SDK's
  `ModelContextProtocol.Core`, Apache-2.0): `open` an input as a session, then `list`, `stat`, `read`, `search` and
  `extract`, and `put`, `mkdir`, `rm`, `rename`, `set`, `res_add`, `res_rm` (each with `dry_run`) changing the session
  only, on working copies that later reads see; `save_as` writes a new file, or over the input only with `in_place`.
  Results are docs/cli.md's JSON; lists and reads come in pages with a `more` cursor; errors carry a code. The write
  commands' changes and the path commands' kinds, decoding and JSON are shared with the CLI.
- Aliases: `'alis'` records are read (`AliasRecord`: the version 2 record and its tagged data, verified on Mac OS 9's
  aliases) and decoded as `finder.alias` JSON; alias files resolve on the open volumes as the Alias Manager's fast
  search does (`AliasResolver`: volume by name and date, then the file or folder number, the parent and name, the full
  path; aliases of aliases), with the path shown as Get Info shows it. HFS and HFS Plus files and folders keep their
  catalog IDs (`MacFile.CatalogId`, `ParentId`, `MacFolder.CatalogId`). docs/formats/resources/aliases.md.
- Viewer: alias files are italic in the tree with an arrow badge; the header says "Alias to Note · kind" with the
  original's path and Show Original (File menu, Ctrl+R); the Preview shows the original under an "Alias of …" strip, or
  an "Original not found" card with the recorded volume and path; Details has an Alias card (original, volume, found and
  how, IDs, dates).
- CLI: `stat` shows an alias's original and whether it resolves (`alias` in `--json`); `ls`, `cat` and `get` take
  `--follow`.

- CLI: file commands on Mac paths (docs/cli.md §2), through disk images and archives: `ls` (files and folders,
  empty ones too, with kind, type/creator, sizes, dates and flags; a fork's types, a type's resources), `stat` (all of
  it with the Finder kind and its source and how the entry was read), `cat` (text as UTF-8 from Mac OS Roman, `--hex`,
  `--raw` bytes, `--fork rsrc`, a resource decoded as JSON or text, `--max-bytes`), `find` (name pattern, type,
  creator, kind, resource type, `--contains` text or hex, `--max-depth`, `--limit`) and `get` (a file with both forks
  as AppleDouble, Basilisk II, MacBinary or raw forks, a folder, a container's contents with `--enter`, or a
  resource). Each takes `--json` with a documented schema; exit code 5 when a path names nothing. The operations are
  the library's `MacCommands` (`ClassicMac.Files.Commands`), for the MCP server and the shell to come.
- Tests: the app tests that watch PropertyChanged while a preview or header icon finishes on the thread pool collect
  into a concurrent queue and await those tasks first (a flake in the window-title test).
- CLI: write commands on Mac paths (docs/cli.md §3): `put` (a host file into a volume), `mkdir`, `rm` (`-r` for a
  folder with contents), `rename`, `set` (type, creator, Finder flags), `res-add` and `res-rm`, each with `--dry-run`,
  `--json`, and `-o <new file>` or `--in-place` (keeping `.orig`); plain HFS images and single Mac files are written,
  nothing inside other containers; exit code 5 when a path names nothing, as for the read commands.
- Decoders: ClassicMac ships TCDB 2003.10 (Type/Creator Database by Ilan Szekely, credited) as its own gzipped TSV
  (`TypeCreatorDatabase.Shipped`, read on first use; `tools/TcdbData` writes it from TCDB's export, less filext.com's
  records): kinds the volume and ClassicMac's table do not know come from it, shown as "TCDB". A user's TCDB
  spreadsheet (the app's setting, the CLI's `--type-creator-db`) replaces it, as "TCDB (your copy)".

- Library: Mac paths (`MacPathTree`, `MacPaths` in `ClassicMac.Files`; docs/cli.md §1): a host file, then Mac names
  joined by ':' or '/' (a backslash escapes them in a name), going into disk images, archives and other containers as
  folders (read one level at a time when entered; a wrapper of one disk passes on to its contents), and on to a file's
  resource fork (`#rsrc`), a type (`'TYPE'`) and a resource ID; names compare as HFS compares them. Resolve, children
  and parent.
- Files: an edit session on an opened input, for the app and the coming CLI write commands (`InputEditSession`,
  `PlannedChange`, `HostImport`): in a plain HFS image add files and folders, delete (recursively when asked), rename,
  set type, creator and Finder flags, and set or delete resources; in a single Mac file its resources, Finder info and
  name; saved as a new file (never over the input) or in place keeping `.orig`. `HfsWriter` gains `Rename`,
  `SetFinderInfo`, `SetFolderFlags` and a recursive `Delete` (hfs.md §3, §5.5). The app's Import File and Delete use
  the moved code.
- Viewer: a file in a "No name" group is named in the inspector header with its row's chips (`sp`, `tab`, `sp×3` …,
  the bytes on hover), in their plain colours; other files keep their name as text.
- Decoders: `'kind'` resources (the kind strings an application gives the Finder for its documents) are read
  (`FinderResources.ReadKind`) and decoded as `finder.kind` JSON; `FinderKindResolver` names a document's kind the
  Finder's way from a volume's applications (their `'kind'` for the type, else "<application> document") and the
  System's kinds of standard types (`'istd'`), reading an application's resource fork only when one of its documents is
  asked about.
- Documents: Word 6 and 95 documents (Word 6 for the Macintosh, `'W6BN'`) read by the Word 97 reader's Word 6 mode
  (docs/formats/documents/word-binary.md §4.1): one WordDocument stream, 8-bit text in Mac OS Roman or Windows-1252 by
  the FIB's character set, one-byte sprms, the older font table, bin tables and style sheet. No public specification
  exists; the rules follow LibreOffice's, Apache POI's and wv's behaviour, two are assumed from Word 97's.
- Documents: Word 97 binary documents (Word 98 for the Macintosh, `'W8BN'`) read by Microsoft's [MS-DOC]
  (`WordBinaryDocuments`, docs/formats/documents/word-binary.md): the main text through the piece table (compressed
  and UTF-16 pieces), character formatting through styles and their base styles, paragraph alignment, indents and
  spacing, fields' results, tables as tab-separated lines; encrypted and obfuscated documents and Word 6/95 are reported
  (`word.encrypted`, `word.unsupported-version`), a fast save's piece properties reported and not applied.
- Core: compound files (OLE2 structured storage) read by Microsoft's [MS-CFB] (`CompoundFile`,
  docs/formats/containers/compound-file.md): versions 3 and 4, the FAT through the header's and DIFAT sectors' lists,
  the mini stream, the directory tree; damaged chains and links reported and cut (`cfb.*`).
- Documents: Microsoft Word 4 and 5 for the Macintosh documents (`'WDBN'`) read into the styled-document model
  (`MacWordDocuments`, docs/formats/documents/word-mac.md): the text, its character formatting (bold, italic,
  underline, outline, shadow, font by name, size; hidden text left out, all caps upper-cased) and its paragraphs'
  alignment, indents and spacing (`DocumentChapter.Paragraphs`, `ParagraphFormat`, `Justification.Full`); tables read
  as tab-separated lines. The viewer previews them and `convert` writes them as HTML, also with no resource fork. The
  layout is fitted to a real Word 5 document (no specification was published); fast-saved documents and Word 1 and 3
  are reported (`word.fast-saved`, `word.unsupported-version`), not read.
- Viewer: Apple Help pages (Help Viewer's `TEXT`/`hbwr` files of Mac OS 8.5–9, and other `.htm`/`.html` files) show
  rendered by the platform's web engine (NativeWebView: WebView2, WKWebView, WebKitGTK), with Rendered | Source. The
  page is decoded (Mac OS Roman unless it declares a charset) and made self-contained: its pictures (GIF, JPEG, PNG,
  a PICT file drawn to PNG), stylesheets and frames are read from the disk and put into it, JavaScript is off, and a
  link to a page or file of the disk selects it in the tree; `help:`, AppleScript and outside links are not followed,
  the status line saying why. Without a web engine the page shows as its source
  (docs/formats/resources/help-pages.md).
- Viewer: the browse tree is one virtualized list of its visible rows (VisibleRows: the roots and, under each open
  row, its children the filter keeps), each a fixed 22 px high and indented by its depth, instead of a TreeView with
  a panel per open row: scrolling past an open folder no longer makes the tree jump or the scroll bar's thumb change
  size (the extent is the row count times the row height). Opening or closing a folder of thousands of files is one
  change of the list. The tree keeps its keys (Right opens or goes to the first child, Left goes to the parent or
  closes, + − and * open, close and open all below; Up, Down, Home, End, Page Up and Down), a press on the expander
  opens or closes without selecting (Alt: every row below), a double click opens or closes.
  (`FinderResources.ReadKind`) and decoded as `finder.kind` JSON. `FinderKindResolver` names kinds as Mac OS 9's Finder
  does from a volume's files: the Finder's own kinds of applications, system files, clippings and the like (from its
  `'STR '` 6902, `'fmap'` 5111 and `'STR#'` 5100), and a document's kind in `GetDocumentKindString`'s order (the `'kind'`
  for its creator and type, of the system's region; the creator's `'apnm'` and " document"; the application's file name
  and " document"; the System's `'istd'` kind; "document"), reading a file's resource fork only when needed.
  `KnownKinds` adds ClassicMac's own table of about 140 type and creator pairs, 40 types and 80 applications, and the
  English copies of the Finder's strings, as the fallback, and names resource types.
- Viewer: files show their kinds as the Finder names them: the inspector's kind line ("SimpleText text document in
  Docs"), the Details tab's Kind with where it came from ("from SimpleText’s 'kind' 128", "built-in"), and a tooltip on
  the tree's type · creator.
- CLI: `info` prints each file's kind and its source.
- CLI: `info --type-creator-db <xlsx>` names kinds from your copy of TCDB too, as the app does; a missing or
  unreadable file is a usage error.
- Viewer: View ▸ Type/Creator Database… uses your own copy of TCDB's spreadsheet (xlsx, read by
  `TypeCreatorDatabase` with no new package) for kinds the volume and ClassicMac's table do not know, shown as "TCDB
  (your copy)"; its path is kept in `settings.json`, and View ▸ Forget Type/Creator Database drops it.
- Editor: editing a menu gives the table the room: the live preview narrows to 166 while editing (272 read only; it
  scrolls at 2× with its scroll bars shown, its title wrapping), so the Text column is at least 160 wide in a
  1200-pixel window.
- Editor: the menu form's rows move with chevron icon buttons and with Alt+Up / Alt+Down on the selected row; the Mark
  select reads in words with each mark's Mac character code in the list (None, Check mark `$12`, Diamond `$13`,
  Bullet `$A5`, Other…, which shows a field for any other code such as `$2A`), and the table's Mark column names the
  mark ("Check mark", "* $2A") instead of drawing a symbol.
- Viewer: a font family ('FOND', boards/font-family.md, P6) previews as its sample and tables, with Properties | JSON:
  size chips from the association table (and TrueType), a style select (styles without a strike marked "(QuickDraw)",
  synthesised from the plain strike as the Font Manager does), an editable sample line drawn by QuickDraw from the
  strike in the family's file at the zoom, black on white ("From 'NFNT' 393 · 12 pt plain · 1-bit", a link to it);
  the association matrix (sizes down, styles across, links that select the resource, missing ones muted with a warning,
  depth badges); the metrics in ems and pixels at the chosen size with the flags as chips; the style extra widths,
  width tables and style-mapping names; the kerning pairs of the chosen style, strongest first, 8 then "Show all", a
  pair highlighted in the sample. The header shows the family's facts and "Aa" from its 24 pt (or largest) strike.
- Viewer: the files in a "No name" group show their names as ASCII token chips instead of symbol glyphs the UI font may
  lack (boards/tree-no-name.md): `sp`, `nbsp`, `tab`, `cr`, `lf`, other control characters as `^A`/`^?`, a run of
  one character as `sp×3`; mono 10 on CmSegmentTrack, on the new CmTokenOnSelection in the selected row; hovering
  shows the bytes in hex ("20 20 CA"); an empty name reads "(empty)" in italics.
- Editor: editing a dialog item list gives the table the room: the live preview narrows to 216 (it scrolls), the
  bounds inputs are compact, and the Text column is at least 160 wide in a 1200-pixel window. Dialog, alert, item
  list and menu previews keep their scroll bars in view, so a dialog wider than its pane scrolls at 1:1; the dialog
  beside a form's cards keeps a 16 margin from the window's edge.
- Files: volumes report their own dates: `HfsReader.ReadVolumeInfo` and `MfsReader.ReadVolumeInfo` (`IVolumeReader`)
  give a `VolumeInfo` with the creation, modification and backup dates of an HFS volume's MDB, an HFS Plus volume's
  header (plain or wrapped; creation in local time, the others in UTC) or an MFS volume's (no modification date);
  the unwrapper keeps them on the node holding the volume (`ContainerNode.Volume`).
- Viewer: Details shows a Volume card for a disk image and for a disk image found on a volume: format, created,
  modified, backed up, with how the volume keeps time; dates stored in UTC (HFS Plus, zip, tar) are shown in this
  computer's time zone, and a file's time note follows its volume's real format (HFS Plus read as "HFS volume" said
  local time). The hex inspector's readings and meaning stay clear of its scroll bar and fit "4,284,572,259", and a
  byte of template bit fields says which bits are on and which off.
- Viewer: the inspector header's tile shows a 32-pixel image: a picture's thumbnail (fitted by nearest neighbour), an
  icon's own image, a file's Finder icon, else the kind's own 32 × 32 art (disk, floppy, parcel, folder, application,
  document, "No name" group, resource type, resource), drawn for the size and shown 1:1, never the 16-pixel icon.
- Viewer: the sound's time ruler steps by 1, 2 or 5 × 10^n seconds (at least 80 px apart), labelled with only the
  decimals the step needs ("0.002 s", "0.5 s", "50 s").
- Viewer: a selection made away from the tree (type-ahead, the Details tab's "In", Show item, a diagnostic's row, a
  form's link) scrolls the tree to the selected row, however deep: each level's row is made, and the scroll repeats
  until the row sits in view.
- Editor: the hex byte inspector fits under the Find bar at 1200 × 780 with diagnostics open (rows without spacing,
  8 px padding); "In this resource" scrolls inside the panel when it does not fit.
- Viewer: a sound's header has Save as WAV… (the decoded sound, as `extract` writes it) and Replace from WAV… (the
  sound's data from a WAV file, one undoable edit) in place of Export….
- Viewer: resources previewed as JSON with no form (style runs, font families, code fragments and others) read as
  labelled values in cards (P2, `PropertyView`): plain labels, numbers in mono, a name beside its number with the
  number raw ("Times `20`"), yes/no dots, rectangles as coordinates with their size, a card per nested object and per
  item of a list (100 at most); Properties | JSON switches to the JSON for the session; right-click a row to copy it as
  decimal, hex or JSON.
- Editor: Find in the Hex tab: bytes as hex ("4E 75", "0x4E75") or text in Mac OS Roman (case-sensitive), Next and
  Previous wrapping round (Enter/Shift+Enter in the box, F3/Shift+F3, Ctrl+F in the Hex tab focuses it); the match is
  selected (or put under the cursor while editing, searching the edited bytes), highlighted and scrolled to, with "2 of 5",
  "Not found" or why the pattern is bad; the hex grid now takes the keyboard focus when a byte is clicked.
- Viewer: fixes from a look at Mac OS 9's System and Finder: a sound shorter than the lanes is drawn as a line, not
  dots, and the time ruler shows enough decimals to tell its ticks apart ("0.005 s", not "0.00 s" twice); text
  previews keep clear of the scroll bar; a disk image's Details no longer shows a Dates card of dashes.
- Editor: fixes from the same look: item kinds ("Radio button", "Static text") fit their select in the item list
  form; the cards' bounds, IDs and values are mono fields without spinner buttons, so "-32768" fits and an alert's
  four bounds stay in the card; template labels keep a gap before their values, and long values (a cursor's bits) wrap
  before the type code instead of running under it; Get Info's and Import's ID fields fit "-32511".
- Viewer: the image preview (P1) shows cards: each image on a checkerboard with its type and size under it
  ("'icl8'", "32×32 · 8-bit"); an icon shows every member of its family, its masks with "Show masks", and the Finder
  states strip (five states, seven labels, captioned; 2× from zoom 2) with "Finder states"; a summary line ("3 members ·
  4× · nearest neighbour · 32-bit screen"). The cards are cut into rows for the pane's width in a virtualising list, so
  a 500-icon 'SICN' scrolls smoothly; a new preview starts at the top.
- Viewer: the tree makes rows only for what is on screen, at every level, so a folder of 5,000 files opens in a
  fraction of a second instead of about 20–30 s; going to an item (type-ahead, Show item) scrolls its row into view
  through the levels, and an applied edit keeps the tree's selection.
- Viewer: the sound preview (P3) has a round Play/Stop button (Space too), the playhead's time over the length, detail
  chips (rate, channels, sample, length, loop, base note, format), one 170 px lane per channel with the loop as a
  band, a 2 px playhead and a time ruler; a click on the waveform plays from there, "Repeat the loop" loops the sound's
  loop. A sound that cannot be decoded shows why, with Show in Hex and Save raw data….
- Viewer: the audio player (A1) reports its position, seeks, and repeats a loop (`IAudioPlayer.Position`, `Seek`,
  `SetLoop`; SoundFlow's time, seek and loop points).
- Editor: Get Info shows the resource's icon on a checkerboard tile with its name and "Icon family in Finder · 2,240
  bytes"; Import shows the source's size and depth ("32 × 32 · 24-bit", or a sound's rate and length), highlights the
  chosen "Make" row and draws what it makes at 2× on a checkerboard with a note on how colours are mapped; the
  unsaved-changes alert names the image and counts the edited resources ("3 resources in Finder were edited.").
- Viewer: the Details tab in cards (File, Forks, Dates, Finder flags, How it was read): fork sizes with bars relative
  to the larger fork, the resource types a fork holds, compressed resources with their 'dcmp's, the dates with a note on
  their time zone (Mac local time for HFS and MFS, UTC for HFS Plus, zip and tar), the Finder flags as chips with the
  raw word, label and icon position, the container chain the file was read through with the file's problem count
  (a link to the diagnostics), "In" as a link to the folder holding the file; inputs, resources, types and folders
  get cards of their own; Copy all puts the details on the clipboard as plain "Label: value" lines.
- Viewer: the dialogs share one frame (drawn by the app: rounded, shadowed, a 42-pixel header with the title and ×, a
  footer with Cancel then the primary action on the right and a destructive choice on the left): Get Info and New
  Resource (Type and ID in mono, the attributes in two columns with Compressed shown but not settable), Import Image or
  Sound (what to make as a list: picture, colour icon, icon family, one icon kind, cursors, sound, with why a choice
  is not offered), New File, New Folder; the unsaved-changes, unapplied-changes and yes/no questions are alerts with an
  icon, the question and its detail (P5).
- Resources: built-in templates for MBAR, BNDL, FREF, SIZE, TMPL, CURS, PAT , PAT#, clut, wctb, actb, dctb, cctb and
  mctb (`BuiltInTemplates`, `ResourceTemplate.FromFields`), from Inside Macintosh and the ResEdit Reference; the
  template form and the hex view's byte meanings use them when no open file has a `TMPL` for the type, which still
  wins (templates.md §5).
- Editor: resources shown through a template (`'TMPL'`) open read only on the form host: a note banner for what the
  template does not cover, the fields in a card with their values (four-character codes quoted, flags as Yes/No) and
  type codes, counts always read only and "kept in step with the list", each list as a heading with its item count and
  one card per item (its number, key field and size), a list inside an item as a compact table, and a template panel
  with where the template was found, which one wins when several open files have one, and its fields. Edit shows
  inputs and Add/Insert before/Remove; counts and the header's Size follow at once. The header names the template
  ("Shown through 'TMPL' 128 “Rsrc” in Forms.rsrc").
- Editor: window, dialog, alert and control forms open read only as property cards (bounds; title, definition,
  visibility, close box, reference constant, auto-position) and switch to inputs with Edit. Window and control
  definitions and positioning words are named ("Document window", `documentProc · 0`; "Stagger on parent window’s
  screen", `0x780A`) with named selects while editing; a window's bounds are drawn on a half-scale screen of a chosen
  size. An alert lists its stages (default button, drawn, sound); a stage's default button is item 1 or 2 only, named
  by its text from the item list, and the selected stage is drawn in the preview. The alert's item list links to its
  `'DITL'`. The Hex tab's editing footer and accent line match the other forms.
- Editor: the dialog item list ('DITL') form on the read-then-edit host (E3): a read-only table (#, kind, text or
  resource, bounds, enabled), inputs while editing (bounds as mono text boxes), and a preview panel drawing the list in
  the 'DLOG' or 'ALRT' that uses it ("Drawn from 'DLOG' 128 · 300 × 106"; the header names it as Used by), redrawn on
  each keystroke; a click on a row outlines its item in the preview (dashed CmAccent), a click on an item in the preview
  selects its row. A click on any form's row now selects it.
- Viewer: the app icon (the diagonal-stripes monitor): the Windows executable's and the window's icon, the title bar's
  hand-drawn pixel version for the display scaling (16, 20, 24 or 32 px, drawn 1:1, never scaled) and the About box's.
- Editor: the hex inspector says what the byte at the cursor is in its resource ("Character 1 of string 1,
  “Untitled”", with its value) for `STR `, `STR#` and types with a `TMPL` in an open file, read from the bytes as
  edited, and the field's bytes are highlighted in the grid. Without editing, a click selects a byte (highlighted in
  both columns) and the inspector reads it. The grid and a 200-pixel inspector fit side by side at the default window
  size, the Mac OS Roman column in view.
- Editor: strings, string lists, text and version open read only on the form host: a string as its text, a string
  list as a numbered list (a double-click on a string edits with it selected), text with its styles applied, a
  version as labelled rows ("1.2b3", stage, region, short and long version); editing shows the inputs as before (E5).
- Editor: the menu form fits a 1200-pixel window while editing: table inputs 28 high in their columns, the fields on
  one line, the preview's title wrapping above "2× · Mac OS 8/9".
- Viewer: with nothing open, the status bar says "Ready" and the diagnostics panel is collapsed to its header, which
  says "Nothing opened yet"; it opens again when a file opens or something is reported, unless you opened or closed it
  yourself (S5).
- Viewer: byte meanings for the hex inspector (E8, model only): `MainViewModel.MeaningAt(node, offset)` says what a
  byte of a resource is ("Character 1 of string 1, “Untitled”", "Length of string 2", "ID of item 3") with the field's
  range and value, for `'STR '`, `'STR#'` and any type a `TMPL` in an open file describes (`ByteMeanings`,
  `ResourceTemplate.Map`); other types and compressed resources have none.
- Editor: Edit Hex (Ctrl+H) edits a resource's bytes in place in the Hex tab, already in editing, instead of in a
  dialog (retired): a grid of 24-pixel byte cells with a column header, zero bytes lighter, changed bytes tinted and in
  SemiBold in both columns, the cursor highlighted, a click on a byte moving the cursor; an Overwrite | Insert switch, a
  status line ("0x000A = 32 · 1 byte changed"), Discard and Apply in the footer; a byte inspector reading the bytes at the
  cursor as UInt8, Int8, UInt16/Int16/UInt32 BE, OSType and binary; Go to a hex offset. When editing ends on a resource
  with a preview, the preview shows again. The inspector shows a byte's meaning when a meaning provider is set (for E8).
- Editor: every resource form opens read only in the Preview tab and is edited in place: Edit (or a double-click on
  a row, which edits with that row selected) shows the inputs under a CmAccent line and the footer's hint, error line,
  Cancel and Apply; Esc cancels, Ctrl+Enter applies; Apply makes one undoable edit, is disabled while the values have
  an error, and leaves "Applied · Undo … (Ctrl+Z)" in the read-only footer. Leaving with unapplied values still asks
  (E1).
- Editor: the menu form reads as a summary and a table (#, text, ⌘ key, mark, icon, style, enabled; dividers as a
  rule, disabled items muted) beside the live preview; editing has Add item, Add divider, a mark select (none, ✓, •,
  ◆), toggles for all seven style bits (B, I, U, and Outline, Shadow, Condense, Extend under More; other bits kept),
  and marks a Command key used twice on both key fields with the error "Item 4 (“Save As…”) uses ⌘S, already used by
  item 3 (“Save”)."; selecting a row highlights its item in the preview and a click in the preview selects the row
  (E2).
- Viewer: the inspector header's tile shows the selection's large icon drawn 1:1 at any display scaling: an icon
  resource's family at 32 × 32 (16 when it has only small members), a cicn fitted into 32, a cursor, or a file's
  Finder icon at 32; the kind icon otherwise. Icon families list their members ("ICN# · icl8 · ics#", or an icns's
  member types). The toolbar's Export… now does what the header's does for the selection.
- Viewer: tree rows fit the pane: no sideways scrolling; a long name trims with an ellipsis while the "not read" chip
  and the right-hand details stay whole; the vertical scroll bar no longer covers the details (it is laid out beside
  the rows, not over them).
- Viewer: Tree display ▸ Show details column (also in View, off by default, remembered): the rows' right-hand details
  (type · creator, resource sizes, the input's format and size, the "No name" count) show only when it is on.
- Viewer: the inspector has a header above its tabs: the selection's icon on a checkerboard tile, its name, its kind
  and owner ("String list in Prefs") and its facts (type, ID, size and attributes for a resource; type / creator, total
  size and resources for a file), with Export… and, for resources with a form, Edit. The Edit tab is gone: Edit opens
  the form in the Preview tab with Cancel and Apply, and the header shows "Editing" until either.
- Viewer: the status bar shows the selected file's name, format and file count with its errors and warnings in their
  colours; while a file is read or resources are extracted, exported, unpacked or converted, it shows what runs, how
  far ("1,240 of 3,906") and a progress bar. `ResourceExporter.Export` and `DocumentConverter.Convert` take an
  `IProgress<int>`.
- Viewer: a custom title bar on Windows and macOS (the window extends into its decorations; the caption buttons stay,
  Avalonia's own drawn title is hidden so the title shows once, the bar drags the window and a double-click maximizes
  it), with the selected file's name, a " •" while it has unsaved edits, and the app's name; Linux keeps the system
  title bar (S1).
- Viewer: a toolbar with Open, Save, Get Info, Edit Hex, Export…, Extract All and Play, each following its command's
  enabled state, and the preview's Zoom (1× 2× 4× 8×) and screen Depth, moved there from the Preview tab (S2).
- Viewer: View menu (Zoom In, Zoom Out, Actual Size, Screen Depth, Show Diagnostics, the tree's display options, Theme:
  System, Light or Dark, remembered), Window menu (Minimize, Zoom, one item per open file) and Help menu (ClassicMac
  Help, Report a Problem…, About ClassicMac); the About box shows the version, what the app does, the repository, the
  MIT licence and the third-party notices (S7).
- Viewer: with nothing open, the inspector shows the empty state: a drop zone (marked while files are dragged over
  the window) with Open…, and the Recent list (up to 10 files, kept in settings.json), which opens a file on click,
  shows a missing one as "Not found" and drops it on click, and can be cleared (S5).
- Viewer: a "Filter tree" field (Ctrl+F, Esc clears) above the tree shows only the rows that match, with what holds
  them (opened) and what they hold, reading archives and disk images not yet read; typing in the tree opens a
  type-ahead pill ("1 of 2 loaded matches") that selects the matches among the loaded rows, F3 and Shift+F3 step,
  Backspace and Esc; matched letters are highlighted (CmMatch, CmMatchSoft) and other rows dimmed; files in a
  "No name" group are skipped, the group row matches "no name" (S6).
- Viewer: each resource that differs from the file as saved (new, renamed, other attributes or data) carries the
  unsaved " •" in the tree, not only its file; it clears on Save, or when undo brings the resource back (T6).
- Viewer: a file never given a type or creator (zeros) shows no type · creator in the tree, and a dash for the one
  that is zero, not "\x00\x00\x00\x00".
- Editor: Save and the unapplied-edits question follow every edit: typing in a `STR#` item, adding or removing one, a
  template field inside a list item, adding list items, and bytes typed in the hex view.
- Viewer: the tree hides files with the invisible flag (`Icon
`, the desktop database; folders such as `Desktop
  Folder` and `Trash` still show), with a footer "N invisible items hidden · Show", and folds a folder's files with no
  name (empty or only whitespace) into one collapsed "No name" node when there are two or more, their names shown with
  the whitespace visible (␣ ⍽ ↵); a single one is titled "(no name)". A "Tree display" popover switches both; the
  choice is kept in `%AppData%/ClassicMac/settings.json`. Exports, previews and the Volume commands still see every file.
- Files: `HfsWriter` finds files whose names contain control characters (a folder's `Icon
`, a name that is a tab):
  replacing a fork or deleting one no longer says the file was not found.
- Editor: "Edit with template" and Save also ask about unapplied edits first: the check box stays until the question is
  answered; Save applies the draft (or saves without it, or does nothing on Cancel), and is available for a draft alone.
- Core: `PackBits` (`Unpack` with the unit size and the `$80` flag's meaning, reporting why it stopped; `Pack`) is the
  one PackBits codec; pictures, MacPaint, QuickTime's planar codec and StuffIt's method 6 share it instead of their own
  copies (codecs/packbits.md §5).
- Viewer: the selected diagnostic has a "Show item" link (when it belongs to a tree node) that selects its node,
  asking about unapplied edits first, opens its ancestors and scrolls the tree to it; the diagnostics panel's splitter
  is a 6-pixel drag handle with a grip, hidden while the panel is collapsed.
- Viewer: the diagnostics panel has a header bar with error, warning and info count badges, a "Filter messages"
  field (message, code or source), the severity filter as a segmented control and a "By file" toggle; column headers,
  with Severity sortable (errors first, info first, arrival); each severity drawn with its own icon and colour; source
  and code in mono; the selected row highlighted with an accent bar (D1).
- Viewer: diagnostics group by file under headers with the file's icon and counts; groups holding only info start
  collapsed, and a click on a header opens or closes it; "By file" off gives the flat list (D2).
- Viewer: Ctrl+Shift+D or the header's chevron collapses the diagnostics panel to its header, which then shows the
  error and warning numbers and the latest problem; the splitter's height comes back when it opens (D3).
- Viewer: the tree's rows are 22 high with 16-pixel pixel icons drawn 1:1 at any display scaling (hard disk, floppy,
  parcel, folder, application, document, resource type, resource); icon resources show their own small icon and files
  their Finder icon (custom icon, the application's bundle, the generic icon), resolved in the background only for rows
  that come on screen and cached per volume; folders keep the folder icon. Each row shows its meta on the right in mono:
  type · creator for files, the size for resources, format and size for the opened file.
- Viewer: tree row states: the unsaved " •" in the accent colour, the loading placeholder as a spinner and "Loading…",
  a "not read" chip on containers not read yet, a dashed outline on the row being dragged out while the status bar says
  what is written; in the selected row of the focused tree the meta and the mark read in the selection's text colour.
- Editor: unapplied edits (a form whose values differ from the resource's, or bytes changed in the hex view) are no
  longer applied or lost silently: selecting another node (in the tree, from a diagnostic, by opening a file), undo,
  redo, the Resource commands, closing a file and quitting first ask "Apply your changes to 'STR#' 128?" with Apply
  (one undoable edit), Discard and Cancel (stay, nothing changes); a draft with an error offers no Apply and names it.
  Applied but unsaved edits still go to the Save prompt only.
- Viewer tests: the window tests compare their frames with screenshot baselines in light, dark and 150% display
  scaling (`tests/golden/app`, Windows only); `CLASSICMAC_UPDATE_BASELINES=1` rewrites them, and a mismatch writes the
  actual frame and a diff image (tests/golden/README.md).
- Viewer: image previews, dialogs, menus and document pictures stay sharp at any display scaling: each Mac pixel is a
  whole number of device pixels (max(1, floor(zoom × scaling)): one at 125% and 150%, three at 2× zoom and 150%),
  drawn from a whole device pixel without smoothing, so 1-pixel detail no longer blurs or doubles.
- Viewer: the app bundles IBM Plex Sans (the default font) and IBM Plex Mono (hex, plain text, the hex dialog; the
  `CmFontSans` and `CmFontMono` resources), so Windows, macOS and Linux show the same type; Inter is no longer used.
- Viewer: the design tokens (design/TOKENS.md) are the app's colours, light and dark (`Styles/Tokens.axaml`, merged in
  `App.axaml`): the window, menu, status bar, tree and inspector take their surfaces from them and flip with the
  theme variant; Fluent's accent and its shades come from CmAccent (F1).
- Viewer: shared styles in `Styles/ClassicMac.axaml`: `.chrome`, `.pane`, `.sidebar`, `.badge.error` / `.warning` /
  `.info`, `.segmented`, `.readonly-bar`, `.editing-badge`, `.caption`, `.muted`. Tree, list and tab selection use the
  CmSelection tokens (inactive while focus is elsewhere), and muted text is a colour (CmTextMuted), not an opacity,
  reading in CmSelectionText inside a focused selected row (F3).
- Viewer: the hex cursor, form errors, the template note, the checkerboard behind images and the sound waveform take
  their colours from the tokens and redraw when the theme changes (F4).
- Viewer: the first selection after opening a disk no longer freezes the app (20 s on a 500 MB disk): the hex list's
  source never goes null, which made Avalonia walk every line of the previous fork. Only resources with no preview
  (unknown types) have a hex view, which opens for them; the Hex tab is hidden otherwise (Edit Hex… still edits any
  resource).
- Viewer: a folder's preview is the whole Finder window: the Platinum frame with the close, zoom and collapse boxes,
  the title with the folder's or the disk's icon, "n items, x MB available" in the header, scroll bars with
  proportional thumbs where the items reach beyond the window, and the grow box; checked against Mac OS 9.0
  screenshots, every pixel but the anti-aliased title matching (file-systems/finder-windows.md §2.9). Selecting an
  archive or disk image not yet read reads it and shows its contents; a wrapper (MacBinary, a disk image) shows the
  window of what it holds.
- Files: `MacFolder.FreeBytes` gives a volume's free space on its root folder.
- Viewer: folders in small icon, large button and small button views preview as the Finder draws them: 16 × 16 icons
  with names flush left (condensed, then truncated in the middle, past a 167-pixel pane) arranged down 192 × 24
  columns; Platinum bevel buttons (48 or 28 pixels) with the icon centred by its mask and the name centred below,
  arranged in 128 × 86 or 128 × 62 rows. Each matches a Mac OS 9.0 Finder screenshot in every icon-area pixel
  (file-systems/finder-windows.md §2.5, §2.8).
- Viewer: folder previews follow the Mac OS 9 Finder's own rules (traced in Finder 9.2.2, checked on Mac OS 9.0, where
  a folder-art window now matches the Finder's screenshot pixel for pixel): the window is `frRect`'s content with the
  21-pixel header pane and the scroll bars' place, used only when the folder has been inited (else the Finder's
  404 × 218 default window and scroll); positions follow `kHasBeenInited` and the +20000 rule, and items without one
  are arranged in the Finder's 128 × 64 grid around the placed icons and names; names are in the views font (Geneva 10,
  or the volume's Finder Preferences) at the Finder's label geometry; icons carry their colour label and the alias,
  lock and custom badges; icons come from `GetIconRef`'s order (stationery, alias types, the system's creators, the
  System's `'isrv'` icon mapping table and the `'icns'` in System Resources); the volume's own files are left out of its
  root window; views other than large icons are named in the caption (file-systems/finder-windows.md).
- Files: HFS and HFS Plus files say whether they are locked (`MacFile.IsLocked`: `filFlags` bit 0, the catalog's
  file-locked flag) (file-systems/hfs.md §5.2, hfs-plus.md §5.1).
- Files: a disk image or volume whose first file is a `.hqx` or `.uu` file within 64 KB of its start is read as the
  disk, not as that file: BinHex and uuencode are recognised only when text alone precedes their marker or `begin`
  line (containers/binhex.md §5, uuencode.md §5).
- Disassembly: more invalid forms are data, as Motorola's and IBM's manuals draw them: 68k `mul.l`/`div.l` with bits
  9–3 of the extension word set, `fmove FPn,<ea>` with a k-factor to a non-packed format, `move.b` from an address
  register and `fmovem.x` with a dynamic list outside 0rrr0000 are `dc.w`; PowerPC `lmw`/`lswi` with rA among the
  registers loaded, `lswx` with rD = rA or rB, and conditional branches with a BO z bit set are `.long`. A 68k full extension word with only a base register is written
  `(0,a0)`, `(0,pc)` or `($0000,zpc)` instead of `(a0)`, `(pc)` or `(zpc)` (output/disassembly.md §1.1, §2.6, §2.7).
- Viewer: a folder, a volume's root or a container read open previews as the Finder's icon view of its window: the
  window's size and scroll from the folder record, each icon where the Finder put it (so folder art shows as it was
  arranged), invisible items left out; icons are the item's custom icon (a folder's in its `Icon
` file), its
  application's bundle icon or the System's generic icon, found on the same volume or in the open files; names in
  Geneva 9 from the volume's System file. Items with no place, and folders in archives, are arranged in a grid
  (file-systems/finder-windows.md).
- Files: `HfsReader.ReadFolders` returns an HFS or HFS Plus volume's folders (`MacFolder`) with their `DInfo` and
  `DXInfo` (`FolderFinderInfo`: window rectangle, flags, icon location, view, scroll position, …) and dates; `Read` is
  unchanged (file-systems/hfs.md §5.2).
- Decoders: `FinderIconResolver` picks the icon the Finder shows for an item (custom icon, bundle, generic) and
  `FinderWindowRenderer` draws a folder's window in icon view through the QuickDraw renderer.
- Files: a container's files are probed for formats in parallel, all probes of a file sharing one read of its head and
  tail: opening a 500 MB disk of 5,170 files takes 0.4 s, and unwrapping everything on it (`list`) 2.4 s instead of 11 s.
- StuffIt: archives decode about ten times faster: forks decode in parallel, method 15 (Arsenic) writes into arrays
  and checks its CRC-32 from a table, and the fork CRC-16 is table-driven (a 38 MB archive of 1,826 files: 9.3 s to
  0.8 s).
- Viewer: opening a disk reads its files, not what is inside them: archives and disk images on it are read when their
  node is first expanded (a 500 MB Mac OS 9 disk opens in under a second instead of 22 s); exports still read
  everything. Problems found inside a nested file name it (`disk.hfv › Disks:Tools.img`).
- CLI: a diagnostic from inside a nested file names it after the input (`disk.hfv > Disks:Tools.img: …`).
- Files: `ForkData.ReadAt` reads without opening a stream or copying, and a host file stays open between reads, so
  probing a disk's files for containers is about six times faster. `ContainerUnwrapper.Unwrap(…, levels)` and
  `Expand` read containers a level at a time; `Diagnostic.Location` names the nested file a problem is in.
- Host folders: a file's own Basilisk II (`.rsrc`/`.finf`) or AppleDouble companions win over a `FINDER.DAT` or
  `RESOURCE.FRK` in its folder, so an unpacked Mac folder that holds a `FINDER.DAT` reads back as written
  (containers/host-folders.md §2.1).
- Code: a new package, `ClassicMac.Code`, reads classic Mac code: PEF containers (sections, pattern-initialized data,
  the loader's imports, relocations and export hash, transition vectors, traceback tables) and `'cfrg'`
  (`.Ppc`); 68k applications (`'CODE'` 0 and the jump table, near and far segments, the entry point and the bootstrap
  shape, MPW `%A5Init`, CodeWarrior `'DATA'` 0, Retro68 `'RELA'`) and code resources (the standard header, `DRVR`, the
  `$A9FF` package form, components, routine descriptors) (`.M68k`); and disassembles them (`.Disassembly`): the 68k and
  PowerPC disassemblers ported from resource_dasm, trap, selector and low-memory names from Multiversal Interfaces,
  MacsBug names, and annotated listings with a function and reference model (code/*.md, output/disassembly.md).
- Extract: `CODE`, `cfrg` and code resources (native `ncod`, `nlib`, `ndrv`, …; 68k `CDEF`, `WDEF`, `DRVR`, `PACK`,
  `INIT`, `XCMD`, …) are decoded: the data stays the main file (`.bin`), with the listing (`.s`) and the model
  (`.json`) beside it; `CODE` 0 and `cfrg` give JSON (output/disassembly.md, output/export-manifest.md §3.8).
- Pack: a decoded resource whose main file is `.bin` packs back from that file, as a raw resource does, so decoded
  code round-trips without `--keep-raw` (output/export-manifest.md §2.2).
- CLI: `disasm <input>` writes a listing per 68k segment, code resource and fragment (the data-fork fragments the
  `'cfrg'` resources name included) and `code.json`, for every Mac file inside the input; `--cpu 68k|ppc|both`
  (output/disassembly.md §3.2).
- Viewer: a code resource's preview is its listing.
- Docs: the format documents are in category folders under `docs/formats/` (`containers/`, `archives/`,
  `disk-images/`, `file-systems/`, `resources/`, `graphics/`, `codecs/`, `output/`), one file per format.
- Interface previews: dialogs and alerts are drawn as Mac OS 9 with Appearance (Platinum) draws them, frames and
  controls pixel for pixel; text through the fonts in open files (resources/windows-dialogs.md §2.4).
- Icons: Mac OS 9's 8-bit icon masks (`l8mk`, `s8mk`, `h8mk`) in icon suites at 8 bits and more (resources/icon-families.md).
- Inputs: Mac ROM images (the ROM's resource table, and the New World `Mac OS ROM` file; disk-images/rom.md); `.sea`
  self-extracting archives through their data fork; StuffIt 1.5.1 segment sets.
- Pixel patterns: a colour table's size is signed, so ResEdit's `ppat`s with an empty table load and draw.
- Inputs: uuencode (`.uu`, `begin-base64`); zip (stored, DEFLATE, ZIP64) and tar/gzip with Mac data: AppleDouble `._`
  and `__MACOSX/` pairing, Info-ZIP and ZipIt Mac extra fields (archives/zip.md, archives/tar-gzip.md, containers/uuencode.md).
- CD images: multisession discs are read by their last session's descriptors, as the Mac reads them, from cue sheets
  and whole-disc raw images (disk-images/cd-images.md §2.2 and §5.3).
- HFS: catalog keys whose length leaves out the pad byte, as Mac OS writes them, are no longer rejected; most files on
  Mac-written volumes were missing from listings (file-systems/hfs.md §1.9).
- Editor III: Volume ▸ New File, Import File, New Folder and Delete on plain HFS images, saved with Save As.
- Viewer: files and resources drag out to the desktop (files as data fork plus AppleDouble, or MacBinary).
- Core: `BigEndianReader` is a class over `ReadOnlyMemory<byte>`, with a constructor that reads a stream from its
  current position to its end; readers are passed without `ref` and can be fields. Every big-endian read in the
  libraries goes through it, and the methods that read with it take `ReadOnlyMemory<byte>` instead of
  `ReadOnlySpan<byte>`.
- Core: `BigEndianWriter` is a class that grows as it is written, like a `StringBuilder` for bytes, with `Write…At`
  to patch a length or offset written earlier, `ToArray` and `WriteTo(Stream)`. `BigEndianStreamWriter` is gone;
  `PictWriter` builds its output with `BigEndianWriter`.
- Graphics: PICT pictures, QuickTime image files and MacPaint documents decode from streams, seekable or not, from the
  current position to the end, leaving the stream open. `QuickTimeImageFile.Read(Stream)` returns a
  `QuickTimeImageResult` with the description and ICC profile; `PictSkia.DecodeAny(Stream)`. The ImageSharp decoders
  read their streams directly.
- Edit tab: "Edit with template" shows a resource that has a typed form through its `TMPL` instead, when one is at hand.
- Hex editing in the hex view: Edit Bytes types hex digits over or into a resource's bytes, with Delete, Backspace and
  cursor keys, applied as one undoable edit.
- Resource forks: a map offset past the end of the fork is recovered from the end of the data area, as ResEdit does
  (`fork.map-recovered`; resources/resource-fork.md §5.3). A damaged fork opened this way saves as a clean one.
- Templates: resources without a form of their own are edited through a `TMPL` from their file or any other open
  file, as ResEdit 2.1.3 reads and writes them; `ResourceTemplate` in the decoders library (resources/templates.md).
- Editor II, import: Resource ▸ Import Image or Sound makes a `PICT`, `cicn`, icon, icon family or cursor from an image
  (PNG, JPEG, …) and a `snd ` from a WAV file, as an undoable edit; `ImageImport` and `SoundImport` in the decoders
  library (resources/icons.md, graphics/pict.md §3, resources/sound.md §3).
- Editor II, UI templates: forms for `DLOG`, `ALRT`, `WIND`, `DITL`, `MENU` and `CNTL` in the Edit tab, the dialog or
  menu preview beside them redrawn as they change; `InterfaceWriter` writes the templates (the Writing sections of resources/windows-dialogs.md, menus.md and dialog-items.md).
- Editor II, first part: an Edit tab with forms for `STR `, `STR#`, `TEXT` (its `styl` runs kept in step) and `vers`,
  applied as undoable edits; the library writes them (`TextResources`, `VersionResource`; the Writing sections of resources/strings.md, styled-text.md and version.md).
- The viewer shows an icon's suite as the Finder draws it: each size, plain, selected, disabled, offline and open, and
  the label colours, at the chosen screen depth.
- Editing (Editor I): the app adds, duplicates, deletes and renumbers resources, changes their names and attributes,
  edits their bytes as hex or replaces them from a file, with undo and redo, and saves back into the file (raw fork,
  AppleDouble, Basilisk II, MacBinary as III, BinHex, AppleSingle), verified, keeping the original as `.orig`; Save As
  writes any of those forms. Ctrl+S is Save; Save Resource As moved to Ctrl+E. Library: `ClassicMac.Resources.Editing`
  (`EditSession`) and `ClassicMac.Files.Editing` (`ForkSaver`).
- `IconSuite.Plot` draws icon suites into a `QuickDrawPort` as PlotIconSuite/PlotIconID do: member choice by rect
  and depth, alignment, the selected/disabled/offline/open transforms and labels, for Mac OS 9 or the ROM.
  `QuickDrawPort.CopyMask` and `Region.FromBitMap` (BitMapToRegion) are new.
- Icon families: `IconFamily` reads `icns` resources as Mac OS 9's Icon Services does (all 20 members: 48 × 48,
  32-bit with its RLE, 8-bit masks) and builds families from classic icon resources; the `image.icon-family` decoder
  exports each member through the mask Icon Services picks for its size.
- `QuickDrawPort`: `SetOrigin`, `HidePen`/`ShowPen`, `CharExtra`, `TextWidth`/`StringWidth`/`CharWidth`, `GetFontInfo`,
  and `DrawPicture` onto a port (saving and restoring its state, clipped to nothing until the picture's ClipRgn, as
  the Mac does). Pictures now start with the pen ScalePt((1, 1)) to the destination, as DrawPicture does.
- The PICT specification is split into `docs/formats/graphics/pict.md` (the format and playback), `quickdraw.md` (the
  drawing rules), `quicktime.md`, `macpaint.md` and the icon documents in `docs/formats/resources/`.
- `QuickDrawPort`: the renderer as a public colour QuickDraw port (shapes, lines, regions, patterns, CopyBits, text)
  with QuickDraw's names, drawing exactly what the picture player draws, which now runs on it. Public `RgbColor`,
  `TransferMode`, `QuickDrawStyle`, `QuickDrawPattern`, `QuickDrawOptions`, `Region` and `PixMap`
  (`docs/QUICKDRAW-API.md`, since removed).
- Renamed for the public drawing API (`docs/QUICKDRAW-API.md`, since removed): `PictBitmap` → `RgbaBitmap`,
  `PictColor` → `RgbaColor`, `PictQuickDraw` → `QuickDrawVersion`, `PictFontLibrary` → `FontLibrary`,
  `IPictTextFallback` → `ITextFallback` (with `TextFallbackMask`, `TextFallbackStyle`); the last four are in
  `ClassicMac.Graphics.QuickDraw`.
- The QuickDraw renderer reads fonts with `ClassicMac.Graphics.Fonts` instead of its own parser. In Mac OS 9 mode a
  strike is now read by Mac OS 9's rules (depth in fontType bits 2–3, rowWords' top bit ignored, the location table
  just before the offset/width table); only damaged or unusual fonts draw differently. A strike whose characters do not
  run within 0–255 is treated as missing.
- The graphics package: QuickDraw.Pict becomes `ClassicMac.Graphics`, one package in layers with a namespace each
  (`ClassicMac.Graphics`, `.Fonts`, `.QuickTime`, `.QuickDraw`, `.Pict`), plus `ClassicMac.Graphics.ImageSharp` and
  `ClassicMac.Graphics.SkiaSharp`. `PictInfo`'s frame and bounds are Core's `MacRect`. `PictBitmap.Info` is gone: `PictReader.Read` returns the bitmap with its `PictInfo`. `QuickDrawResources` is part of
  `ClassicMac.Resources.Decoders`. Usage in `docs/GRAPHICS.md`.
- QuickDraw.Pict moved into this repository with its history: the picture renderer and PICT reader/writer
  (`src/QuickDraw.Pict`), its ImageSharp and SkiaSharp packages, tests and golden tools. The decoders use it directly
  instead of the NuGet package, so the Origin fix (below) reaches them; its spec is `docs/formats/PICT-FORMAT.md`.
- Fonts (`ClassicMac.Graphics.Fonts`): bitmap strikes (`NFNT`, `FONT`), families (`FOND`) with their width,
  kerning and style-mapping tables, font colour tables and TrueType `sfnt` data. Font decoders: strikes to a glyph
  sheet PNG, BDF and metrics JSON; families and font colours to JSON; `sfnt` to `.ttf`. Specified in
  `docs/formats/resources/bitmap-fonts.md`, `font-families.md` and `outline-fonts.md`.
- `pack`: an export folder back into a resource fork, raw or in an AppleDouble, AppleSingle, MacBinary III or
  BinHex 4.0 file (new writers for the last three); unchanged resources come back byte for byte from `raw/` or a
  base fork. The corpus test now packs every export back.
- Finder resources (`BNDL`, `FREF`, `SIZE`) to JSON, a bundle with each file type's icon, specified in
  `docs/formats/resources/finder.md`.
- Colour tables (`clut`) and palettes (`pltt`) to JSON and Adobe `.act`, specified in `docs/formats/resources/palettes.md`;
  the viewer shows them as swatches.
- Interface resources to JSON: menus and menu bars, window, dialog and alert templates, dialog item lists and
  control templates, and their colour tables and Appearance extensions (`ui.*` decoders), specified in
  `docs/formats/resources/` (`menus.md`, `windows-dialogs.md`, `dialog-items.md`, `controls.md`). The viewer draws
  dialogs, alerts, item lists and menus in the System 7 style.
- DOCMaker stand-alone documents and SimpleText documents with pictures: read into a styled-document model
  (`StyledDocuments`) and converted to an HTML folder (`HtmlDocuments`: a page per chapter, a contents page, the
  pictures as PNG reflowed into the text, picture actions as links). Specified in
  `docs/formats/resources/documents.md` and `docs/formats/output/html.md`.
  `extract` adds a document's HTML folder as `document/` (manifest format 1.2: a `document` field; `--no-documents`
  leaves it out); the new `convert` command writes only the documents. Document converters plug in through
  `IDocumentConverter` and `ExportOptions.Documents`. The viewer previews documents a chapter at a time with the same
  reflow (picture links followed), has Export ▸ Convert Documents, and includes `document/` in its resource exports.
- UDIF disk images (`.dmg`): Disk Copy 6.4/6.5's (block tables in an embedded resource fork) and Mac OS X's (XML
  property list), with zeros, raw, ADC, zlib and bzip2 runs (a bzip2 decoder in `ClassicMac.Files.Compression`) and
  CRC-32/MD5 checksums checked with `--verify`; encrypted and segmented images refused. NDIF and UDIF share a
  chunked-disk reader.
- Phase 3 exit check: golden outputs for every decoder (fixtures made in code; text outputs as files, images and
  sounds as hashes; the export manifest pinned) and a corpus export test with a committed baseline of counts and
  output hashes; `CLASSICMAC_CORPUS` takes several folders. A resource fork whose map gives over 1,000 errors (another
  format read as a fork) is refused instead of read slowly.
- Viewer app (`src/ClassicMac.App`, Avalonia): open files, disk images and resource forks, browse them down to
  individual resources, see details and diagnostics; preview images, styled text, strings and version resources,
  sounds (waveform and playback through SoundFlow), and any fork or resource in hex; export a resource, a file's resources, everything under a node, or unpack it
  (AppleDouble, Basilisk II) into new folders. `MacFileResources` and `ClassicMac.Files.Export` (`Unpacker`,
  `OutputLayout`, `ExportFolders`) in Files, shared with the CLI; `StyledText` in Decoders.

- Repository layout, build settings and CI.
- Packages: `ClassicMac.Core` (`FourCC`, `MacString`, `MacDate`, `MacPoint`, `MacRect`, `Fixed`, `UnsignedFixed`,
  diagnostics), `ClassicMac.Files` (`MacFile`, `FinderInfo`, `ForkData`, `IContainerReader`, `ContainerReadOptions`)
  and `ClassicMac.Resources` (`Resource`, `ResourceFork`, `ReadOptions`), each with its own test project.
- Resource fork reader (damage reported as diagnostics; whether Mac OS 9 or the ROM would open the fork) and a
  writer that lays forks out as the Resource Manager's compaction does; map attributes (`mAttr`) and map
  flags (`mInMemoryAttr`) kept as separate bytes.
- Compressed resources: `ResourceDecompression` with System `dcmp` 0–3 (from disassembly), including dcmp 3's
  overshoot into the memory after the block; Mac OS 9 or 68k ROM Resource Manager behaviour via
  `ReadOptions.ResourceManager`.
- Containers in `ClassicMac.Files`: AppleSingle/AppleDouble (v1, v2), MacBinary I/II/III, BinHex 4.0; host files
  with PC Exchange `RESOURCE.FRK`/`FINDER.DAT`, Basilisk II `.rsrc`/`.finf`, AppleDouble `._` files and macOS named
  forks; `ContainerUnwrapper` for nesting;
  lazy `ForkData` slices. `MacRoman` in Core.
- Disk images in `ClassicMac.Files.Hfs`: HFS and MFS volumes (files with folder paths, forks read in place through
  their extents), Disk Copy 4.2 and Apple partition maps; `MacFile.FolderPath` and `MacPath`. The file layer is one
  package: single-file readers moved to `ClassicMac.Files.Containers`.
- FAT volumes in `ClassicMac.Files.Fat`: FAT12/16/32 with long names, and the PC Exchange / File Exchange data
  on them (Mac names, Finder info, dates, resource forks), matching what OS 9 lists; DOS partition tables.
  `DosTime` and `PcExchange.Apply` shared with host folders. Long names converted as File Exchange converts them;
  an opt-in `ExtensionMap` for placeholder types.
- DART images ("fast" RLE, "best" LZH, stored; checksums checked), verified on DART 1.5.3's own files; LZH also
  decodes NDIF chunk type $82, and KenCode ($80, Disk Copy's "Smaller (KC)") is decoded too.
- Image decoders (`ClassicMac.Resources.Decoders.Images`, through QuickDraw.Pict): `PICT`, icons (`ICON`, `ICN#`
  and the colour icon families masked by their lists, `cicn`, `SICN`), cursors (PNG + JSON), patterns; written as
  PNG by a built-in encoder behind `IImageEncoder`; `extract --screen-depth`.
- Sound decoder (`ClassicMac.Resources.Decoders.Sound`): `snd ` formats 1 and 2 with standard, extended and
  compressed headers, uncompressed PCM to WAV (loops and base note in `smpl`) plus a JSON of the exact rate, header and
  commands; `SoundResource` model. MACE 3:1/6:1, IMA4 and µ-law decoded as the Mac OS 9 Sound Manager decodes them
  (disassembly; byte-identical to its output); format 2 headers found as SndPlay finds them.
- NDIF disk images (Disk Copy 6: read-only, ADC-compressed, DART RLE chunks, map versions 10–12 (Disk Copy 6.1–6.5), `.smi`, segmented
  parts found by their `bcm#` ID, CRC-32 verified on request) with ADC in
  `ClassicMac.Files.Compression`; container readers can see the whole file (resource fork) and sibling files;
  `ClassicMac.Files` references `ClassicMac.Resources`.
  Version 2 maps (Disk Image Mounter, Disk Copy 6.0.1) read as Disk Copy 6.1.2's driver reads them, with a request
  for real samples. CLI `--verify` checks disk image checksums.
- Raw CD images (`.bin`, 2352/2336-byte sectors) and cue sheets, read as the 2048-byte blocks a drive hands the Mac.
- CD volumes in `ClassicMac.Files.Iso`: ISO 9660 and High Sierra as Mac OS 9 reads them (Apple `AA`/`BA` Finder
  info, associated files as resource forks, the Mac's name, date and listing rules), matching OS 9 on a test disc.
- Writing Mac files to the host: `HostFiles.Write` (AppleDouble or Basilisk II layout, `HostWriteOptions`),
  `AppleDoubleWriter`, `HostNames`, `FinderInfo.Write`; `classicmac unpack` writes every file inside an input
  (through containers and disk images) to a folder. Basilisk II folder names follow SheepShaver (Windows-1252
  bytes, its escape set), checked in the emulator.
- `classicmac` CLI: `info` and `list` read through containers and disk images, showing Mac paths (text or JSON);
  `extract` decodes text resources by default (`STR `/`TEXT` → UTF-8, `STR#`/`styl`/`vers` → JSON, `TEXT` + `styl`
  → RTF; `ClassicMac.Resources.Decoders`, `--raw` for the data itself) and writes every resource into type folders
  with a `manifest.json` (format 1.1, with a JSON
  Schema), a folder per file for disk images; `--keep-raw` keeps the stored bytes; `HostNames` moved to Core;
  limit options, exit codes.

## QuickDraw.Pict (before it moved here)

### Unreleased (before the move)

**QuickDraw.Pict**
- Fixed: the Origin opcode ($000C) reads dh before dv, as Mac OS 9.0's DrawPicture does; pictures that move their
  origin (DOCMaker's, for one) drew mostly outside their frame.

**QuickDraw.Pict.SkiaSharp** (new)
- Decode pictures, QTIF files and MacPaint documents to `SKBitmap`/`SKImage` (`PictSkia.Decode`, `DecodeImage`,
  `DecodeAny`), with the same options as the ImageSharp decoder.
- Encode `SKBitmap`/`SKPixmap` as PICT (`PictSkia.Encode`, `SaveAsPict`).
- JPEG, PNG, GIF, WebP and BMP QuickTime images through Skia's codecs; outline-font text fallback through Skia.

### 0.1.0 — 2026-09-27

First release.

**QuickDraw.Pict**
- Reads PICT version 1, version 2 and extended version 2 pictures, bare or with a 512-byte file header. It draws them
  with a software QuickDraw engine to an RGBA `PictBitmap`. The engine covers:
  - shapes, regions, patterns, pen modes and transfer modes;
  - CopyBits scaling and masks;
  - bitmap-font and color-bitmap-font text through a Font Manager model;
  - QuickTime images: raw, Animation, Road Pizza, Graphics, Cinepak, 8BPS, YUV2, YVU9, Targa and MacPaint.
- Two QuickDraw models: Mac OS 9 (default) and the 68k ROM.
- `ScreenDepth` draws a picture as it looks on a 1, 2, 4, 8 or 16-bit screen.
- `PictWriter` writes 1/2/4/8-bit indexed, 16-bit and 32-bit pictures with resolution and an ICC profile.
- `PictInfo` gives the header, resolution, comments and ICC profile.
- `QuickTimeImageFile` (QTIF) and `MacPaintFile` (PNTG).
- `QuickDrawResources` decodes icons, cursors and patterns from resource data.

**QuickDraw.Pict.ImageSharp**
- ImageSharp format plugin: PICT detection, decoding and encoding (`SaveAsPict`).
- It also loads QTIF and MacPaint files.
- JPEG, PNG, GIF, TIFF, WebP and BMP images inside QuickTime pictures are decoded through ImageSharp.
- Outline-font fallback for text without bitmap fonts.
