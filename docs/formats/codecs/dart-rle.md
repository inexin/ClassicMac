# DART RLE

DART's "fast" compression, and NDIF chunk type `$81` [Code: Disk Copy 6.3.3], [Verified: DART 1.5.3]. The input is
big-endian 16-bit words:

- Read a signed count *n*. If *n* ≥ 0, copy the next *n* words. If *n* < 0, repeat the next word −*n* times.
- Repeat until the output is full. A DART block is always exactly 10,480 words (20,960 bytes).
- Disk Copy refuses |*n*| ≥ 10,481 (−50) [Code: 6.3.3]. ClassicMac reports any run passing the end of the output,
  or input running out, as a damaged block.

A DART header counts an RLE block's length in words.
