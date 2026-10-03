# Third-party notices

ClassicMac is MIT-licensed. Code or data taken from other projects is listed here with its licence.

## Mac OS Roman mapping (Apple, distributed by Unicode)

<https://www.unicode.org/Public/MAPPINGS/VENDORS/APPLE/ROMAN.TXT> (version c02, 2005)

- `src/ClassicMac.Core/MacRoman.cs` holds the byte-to-Unicode values of this mapping table. The file is © Apple
  Computer, Inc., published by the Unicode Consortium with Apple's other vendor mappings; ClassicMac contains the
  values, not the file.

## Unicode 3.2 data

`src/ClassicMac.Files/Hfs/HfsPlusUnicodeComparison.cs` and `HfsPlusUnicodeNormalization.Data.cs` contain simple
lowercase mappings, default-ignorable ranges, canonical decomposition mappings and combining classes derived from
`UnicodeData-3.2.0.txt` and `DerivedCoreProperties-3.2.0.txt`, used to implement TN1150's `FastUnicodeCompare` and
name-normalization rules. The source data is available from the
[Unicode 3.2 archive](https://www.unicode.org/Public/3.2-Update/).

```
UNICODE LICENSE V3

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2026 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
```

## resource_dasm

<https://github.com/fuzziqersoftware/resource_dasm>

- `src/ClassicMac.Resources/Compression/Dcmp3.cs` was ported from its `System3.cc`, then checked against the
  disassembly of the Mac OS 9.0 System's `'dcmp'` 3.
- The constant tables in `Dcmp01.cs` and `Dcmp2.cs` were cross-checked against its `System01.cc` and `System2.cc`.
  The tables themselves come from the Mac OS 9.0 System's decompressors.
- `src/ClassicMac.Code/Disassembly/PpcDisassembler.cs` was ported from the disassembler half (the `dasm_*`
  functions) of its `src/Emulators/PPC32Emulator.cc` (commit 5cbf27e): the opcode tables, field layouts and
  reserved-bit checks. The output is standard assembler syntax with structured operands; AltiVec and most extended
  mnemonics were added from the PowerPC and AltiVec programming environments manuals.
- `src/ClassicMac.Code/Disassembly/M68kDisassembler.cs` was ported from the disassembly half of its
  `src/Emulators/M68KEmulator.cc` and `.hh` (`decode_instruction`, the `DisassemblyState` formatters and the
  effective-address decoding), then checked against Motorola's M68000 Family Programmer's Reference Manual, which
  corrected several fields. The emulator half was not ported.

```
The MIT License (MIT)

Copyright (c) 2023 Martin Michelsen

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## LZHUF (Haruyasu Yoshizaki, Haruhiko Okumura, 1988)

- `src/ClassicMac.Files/Compression/DartLzh.cs` implements the LZHUF algorithm (adaptive Huffman coding with LZSS,
  including its position-code tables) as DART and Disk Copy 6.3.3 use it; it was written from the disassembly of Disk
  Copy's codec, which follows the authors' freely distributed `lzhuf.c`. The authors placed the program in the public
  domain; they are credited here for the algorithm and tables.

## StuffIt method-13 and method-15 interoperability tables

The method-13 interoperability table values in `src/ClassicMac.Files/Archives/StuffItMethod13Tables.cs` and the
method-15 randomization table in `src/ClassicMac.Files/Archives/StuffItMethod15Decoder.cs` are transcribed from
[KarpelesLab/compcol](https://github.com/KarpelesLab/compcol), `src/sit13/tables.rs` and `src/arsenic/tables.rs`
respectively. Both decoder implementations are independent. The table data is covered by the upstream MIT license:

```
The MIT License (MIT)

Copyright (c) 2026 Karpeles Lab Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## StuffIt method-13 and method-15 test fixtures

`tests/ClassicMac.Files.Tests/TestData/StuffItMethod13/` contains small fork streams and expected output extracted from
the StuffIt Deluxe 4.5 sample archive. `tests/ClassicMac.Files.Tests/TestData/StuffItMethod15/` contains a StuffIt
Deluxe 6.5.1 archive and expected method-15 data- and resource-fork output. Both are from
[ssokolow/stuffit-test-files](https://github.com/ssokolow/stuffit-test-files), whose author released the archives and
contents under CC0; see each fixture README and the upstream license for provenance.

## munbox Compact Pro test sample

`tests/ClassicMac.Files.Tests/TestData/CompactProMunbox/` holds a Compact Pro archive and its MD5 list from
[munbox](https://github.com/dafo123/munbox) (`test/testfiles/compact_pro_152.cpt/`), used under the MIT License. No
munbox code is used.

```
MIT License

Copyright (c) dafo123

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## lhasa MacLHA test archives

`tests/ClassicMac.Files.Tests/TestData/MacLha224/` holds seven MacLHA 2.24 archives from
[lhasa](https://github.com/fragglet/lhasa)'s test suite (`test/archives/maclha_224/`), used under the ISC License.
The `-lh1-` adaptive tree in `src/ClassicMac.Files/Archives/LhaOldDecoder.cs` follows lhasa's `lib/lh1_decoder.c`
(groups and leaders, reconstruction), used under the same licence.

```
Copyright (c) 2011-2025, Simon Howard

Permission to use, copy, modify, and/or distribute this software
for any purpose with or without fee is hereby granted, provided
that the above copyright notice and this permission notice appear
in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL
WARRANTIES WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE
AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR
CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT,
NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN
CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Executor (QuickDraw.Pict)

QuickDraw.Pict follows Apple's *Inside Macintosh: Imaging With QuickDraw* (Appendix A, Picture Opcodes) and Apple
Technical Notes as the specification; code ported from Executor keeps a reference to its source in comments.

<https://github.com/autc04/executor> — used for picture playback, pixel-data layouts, regions, drawing and
transfer modes.

```
Copyright 1986-2004 Abacus Research & Development, Inc.
Copyright 2018-2019 Wolfgang Thaller

Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation
files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
OTHER DEALINGS IN THE SOFTWARE.
```

## Multiversal Interfaces

<https://github.com/autc04/multiversal> — the classic Mac OS API definitions (YAML) from Executor 2000.

`tools/TrapTables` reads its `defs/*.yaml` (trap numbers, dispatchers and their selectors, low-memory globals) and
generates `src/ClassicMac.Code/Disassembly/TrapNames.g.cs`, `SelectorNames.g.cs` and `LowMemoryGlobals.g.cs`, with
Apple's trap-macro names supplemented from Inside Macintosh.

```
Copyright 2019 Wolfgang Thaller

Permission is hereby granted, free of charge, to any person
obtaining a copy of this software and associated documentation
files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
OTHER DEALINGS IN THE SOFTWARE.
```

## Bundled with the viewer app

The app (not the libraries) references these NuGet packages, which ship their own licence files:

- [SoundFlow](https://github.com/LSXPrime/SoundFlow) 1.4.1, MIT, for sound playback. It bundles native
  [miniaudio](https://miniaud.io) builds (MIT or public domain, at your choice).
- Avalonia and CommunityToolkit.Mvvm, MIT.
- [NativeWebView](https://github.com/wieslawsoltes/NativeWebView) 12.0.4.9 (`NativeWebView` and its Windows, macOS and
  Linux platform packages), MIT, which hosts the platform's web engine for Apple Help pages. On Windows it brings
  Microsoft's WebView2 SDK (`Microsoft.Web.WebView2`, BSD-3-Clause, © Microsoft Corporation), which uses the WebView2
  runtime installed with Windows; on macOS it uses WKWebView, and on Linux WebKitGTK, from the system.

The app also bundles fonts as assets:

- [IBM Plex](https://github.com/IBM/plex) Sans 1.1.0 (Regular, Italic, SemiBold, SemiBold Italic) and Plex Mono 2.5.0
  (Regular, SemiBold), TrueType files from IBM's releases, in `src/ClassicMac.App/Assets/Fonts/`. Copyright © 2017
  IBM Corp. with Reserved Font Name "Plex", under the SIL Open Font License 1.1, whose text is beside them in
  `OFL.txt`. The fonts are unmodified.
