# µ-law

`'ulaw'` is G.711 µ-law [Author], one byte per sample, decoded by the `sdec` component −16593 to 16-bit [Code]:

```
u = (NOT code) & $FF
t = (((u & $0F) << 3) + $84) << ((u >> 4) & 7)
sample = (u & $80) ? ($84 - t) : (t - $84)
```

[Author][Code][Verified]. The component's table is the standard G.711 one, all 256 entries [Code]. The samples range
over ±32 124; $FF and $7F give 0.

- `numFrames` counts samples [Code][Verified].
- Channels interleave **by byte**: L, R, L, R … (68k `sift` and PowerPC `nift` alike) [Code]. Only mono was verified.
- Asked for 8-bit output, the component gives the sample's high byte XOR $80 [Code]. ClassicMac writes 16-bit.
