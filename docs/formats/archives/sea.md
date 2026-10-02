# Self-extracting archives (`.sea`)

A self-extracting archive is an application (type `APPL`) whose resource fork holds the extractor and whose data fork
is the archive, from offset 0 to the end of the fork:

- StuffIt's classic extractor (creator `aust`): the data fork is a `SIT!` archive whose archive length equals the
  fork's length. The extractor opens its own data fork by name and reads from offset 0 without checking the
  signature [Code] [Verified on StuffIt SEA 3.5 samples].
- StuffIt 5 extractors: the data fork is a complete StuffIt 5 archive whose length field equals the fork's length
  [Verified].
- Compact Pro extractors (creator `EXTR`): the data fork is the archive from offset 0 to the end of the fork, the
  same bytes as the archive saved without Self-Extracting except its id (header bytes 2–3); the extractor is a
  13057-byte resource fork [Verified against Compact Pro 1.52].

ClassicMac needs no special rule: the unwrapper tries every reader on a file's data fork whatever its type, so a
`.sea` lists as the application with the archive's files inside it. The extractor's own resources are often
compressed with Aladdin's own `dcmp` (128) and are listed, not decompressed.

Not covered: InstallerMaker installers (creator `STi0`, data fork signature `ST65`) and other installer formats.
