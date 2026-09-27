# Third-party notices

ClassicMac is MIT-licensed. Code or data taken from other projects is listed here with its licence.

## Mac OS Roman mapping (Apple, distributed by Unicode)

<https://www.unicode.org/Public/MAPPINGS/VENDORS/APPLE/ROMAN.TXT> (version c02, 2005)

- `src/ClassicMac.Core/MacRoman.cs` holds the byte-to-Unicode values of this mapping table. The file is © Apple
  Computer, Inc., published by the Unicode Consortium with Apple's other vendor mappings; ClassicMac contains the
  values, not the file.

## resource_dasm

<https://github.com/fuzziqersoftware/resource_dasm>

- `src/ClassicMac.Resources/Compression/Dcmp3.cs` was ported from its `System3.cc`, then checked against the
  disassembly of the Mac OS 9.0 System's `'dcmp'` 3.
- The constant tables in `Dcmp01.cs` and `Dcmp2.cs` were cross-checked against its `System01.cc` and `System2.cc`.
  The tables themselves come from the Mac OS 9.0 System's decompressors.

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
