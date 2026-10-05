# ClassicMac.Core

The dependency-free types every ClassicMac package shares.

- **Identifiers and text:** `FourCC` (types, creators, signatures), `MacString` (a name's bytes, Pascal strings),
  `MacRoman` and `MacEncodings` (the Mac's text encodings, Japanese, Chinese, Korean, Arabic, Hebrew, Cyrillic and
  the rest, from Unicode's Apple mappings), `MacScripts`.
- **Values:** `MacDate` (seconds since 1904), `MacPoint`/`MacRect`, `Fixed`.
- **Binary data:** `BigEndianReader` (sequential and absolute reads over memory or a stream, bounded sub-readers,
  `Try…` variants) and `BigEndianWriter` (a growing buffer with placeholders patched later), `PackBits`,
  `CompoundFile` (OLE structured storage, for Word documents).
- **Host names:** `HostNames` turns Mac names into names a host file system accepts and back.
- **Diagnostics:** `Diagnostic` (code, severity, location, message): what readers report instead of throwing for
  damage they can work around.

```csharp
var reader = new BigEndianReader(bytes);
FourCC type = reader.ReadFourCC();
ushort count = reader.ReadUInt16();
MacRect bounds = reader.ReadMacRect();

string name = MacEncodings.Decode(nameBytes, MacTextEncoding.Japanese);
```

Part of [ClassicMac](https://github.com/inexin/ClassicMac). MIT.
