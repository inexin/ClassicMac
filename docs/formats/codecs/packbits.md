# PackBits

A packed scan line is:

- a **byte count** (a u8, or a **u16 when rowBytes > 250**);
- then that many bytes of runs, each starting with a flag byte `n` (i8):
  - `n ≥ 0`: copy the next `n + 1` units;
  - `n < 0` and `n ≠ −128`: repeat the next unit `1 − n` times;
  - `n = −128`: no-op.

A unit is a byte, except for **word packing** (packType 3), where it is two bytes. Stop at the row length; ignore
extra bytes inside the counted block.
