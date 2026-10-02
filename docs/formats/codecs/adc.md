# ADC

Apple Data Compression, NDIF chunk type `$83` and UDIF run type `$80000004` [Code: Disk Copy 6.3.3 `hdi2` codec],
[Verified: 6.1.2, 6.3.3 and 6.5b13 images, NDIF and UDIF]. A byte-oriented LZ77 code; each token starts with an
opcode byte:

| Opcode bits | Token | Length | Distance |
| --- | --- | --- | --- |
| `1LLLLLLL` | Literal run: *length* bytes follow and are copied | `L` + 1 (1–128) | — |
| `00LLLLDD DDDDDDDD` | Short match | `L` + 3 (3–18) | `D` (10 bits) + 1 (1–1024) |
| `01LLLLLL DDDDDDDD DDDDDDDD` | Long match | `L` + 4 (4–67) | `D` (16 bits) + 1 (1–65,536) |

- A match copies *length* bytes from *distance* bytes back in the output, **one byte at a time**, so a match may
  overlap what it writes (distance 1 repeats the last byte).
- Decoding stops when the output (the chunk's decoded size) is full.
- **A token that would pass the end of the output is an error**, checked before any of it is written; Disk Copy
  reports −8819 (damaged) at read time.
- Disk Copy checks nothing else: a match reaching before the output's start reads the memory before its buffer, and
  input is read past the stored length. ClassicMac reports both instead (`ndif.bad-chunk`).
- No state carries from one chunk to the next.
