# MACE 3:1 and 6:1

MACE (Macintosh Audio Compression and Expansion) compresses 8-bit sound 3:1 or 6:1: each code of 2 or 3 bits picks a
delta from a table row chosen by an adaptive level. Apple documented the routines (`Comp3to1`, `Exp1to3`, `Comp6to1`,
`Exp1to6`) but not the algorithm; everything below is from the code [Doc]. Sampled sounds in `'snd '` resources name
it as their format (`'MAC3'`, `'MAC6'`). ClassicMac decodes both to 8-bit WAV samples, as the Sound Manager on a
PowerPC Mac gives them.

| | |
| --- | --- |
| Used by | [sound.md](../resources/sound.md) (formats `'MAC3'` and `'MAC6'`, compressionID 3 and 4) |
| ClassicMac | Reads; `ClassicMac.Resources.Decoders.Sound.Mace` |
| Verified against | Sound Manager 3.5.1 on Mac OS 9.0, its own decoding of MACE 3:1 and 6:1 samples it compressed: mono and stereo from 8-bit sources, mono from 16-bit sources |
| Sources | *Inside Macintosh: Sound*; Mac OS 9.0's SoundLib (`Exp1to3`, `Exp1to6`) and `sdec` components `'MAC3'` and `'MAC6'`, the 68k ROM and `gpch` 666, traced |

Contents

1. [Layout](#1-layout)
2. [Reading](#2-reading)
3. [Writing](#3-writing)
4. [Variants](#4-variants)
5. [ClassicMac](#5-classicmac)
6. [Diagnostics](#6-diagnostics)
7. [Verification](#7-verification)
8. [Not covered](#8-not-covered)
9. [References](#9-references)

## 1. Layout

### 1.1 Packets and channels

| Format | Packet per channel | Samples per packet | Code order in each byte |
| --- | --- | --- | --- |
| MACE 3:1 | 2 bytes | 6 | Lowest bits first: bits 0–2, 3–4, 5–7 |
| MACE 6:1 | 1 byte | 6 (two per code) | Highest bits first: bits 5–7, 3–4, 0–2 |

[Doc] [Code]

- Channels alternate by packet: MACE 3 stores L L R R L L R R …, MACE 6 stores L R L R … [Code] [Verified].
- Each channel has its own state; all of it starts at zero [Code].
- The header's `numFrames` counts packets; the output is 6 × `numFrames` frames, channels interleaved by frame.
- A trailing partial packet (or partial set of packets across the channels) is dropped: the component decodes
  ⌊samples/6⌋ packets [Code].

### 1.2 Adjustment tables

Signed 16-bit, indexed by the code [Code]:

```
T3 = [ -13,   8,  76, 222, 222,  76,   8, -13 ]      3-bit codes
T2 = [ -18, 140, 140, -18 ]                          2-bit codes
```

### 1.3 Delta tables

Signed 16-bit; the row is `(old level >> 4) & $7F`, the column is the code [Code]. Apple's numbers, as they stand in
SoundLib, `gpch` 666 and the ROM.

`T3D` (3-bit codes):

```
row         c0      c1      c2      c3      c4      c5      c6      c7
  0         37     116     206     330    -331    -207    -117     -38
  1         39     121     216     346    -347    -217    -122     -40
  2         41     127     225     361    -362    -226    -128     -42
  3         42     132     235     377    -378    -236    -133     -43
  4         44     137     245     392    -393    -246    -138     -45
  5         46     144     256     410    -411    -257    -145     -47
  6         48     150     267     428    -429    -268    -151     -49
  7         51     157     280     449    -450    -281    -158     -52
  8         53     165     293     470    -471    -294    -166     -54
  9         55     172     306     490    -491    -307    -173     -56
 10         58     179     319     511    -512    -320    -180     -59
 11         60     187     333     534    -535    -334    -188     -61
 12         63     195     348     557    -558    -349    -196     -64
 13         66     205     364     583    -584    -365    -206     -67
 14         69     214     380     609    -610    -381    -215     -70
 15         72     223     396     635    -636    -397    -224     -73
 16         75     233     414     663    -664    -415    -234     -76
 17         79     244     433     694    -695    -434    -245     -80
 18         82     254     453     725    -726    -454    -255     -83
 19         86     265     472     756    -757    -473    -266     -87
 20         90     278     495     792    -793    -496    -279     -91
 21         94     290     516     826    -827    -517    -291     -95
 22         98     303     538     862    -863    -539    -304     -99
 23        102     316     562     901    -902    -563    -317    -103
 24        107     331     588     942    -943    -589    -332    -108
 25        112     345     614     983    -984    -615    -346    -113
 26        117     361     641    1027   -1028    -642    -362    -118
 27        122     377     670    1074   -1075    -671    -378    -123
 28        127     394     701    1123   -1124    -702    -395    -128
 29        133     411     732    1172   -1173    -733    -412    -134
 30        139     430     764    1224   -1225    -765    -431    -140
 31        145     449     799    1280   -1281    -800    -450    -146
 32        152     469     835    1337   -1338    -836    -470    -153
 33        159     490     872    1397   -1398    -873    -491    -160
 34        166     512     911    1459   -1460    -912    -513    -167
 35        173     535     951    1523   -1524    -952    -536    -174
 36        181     558     993    1590   -1591    -994    -559    -182
 37        189     584    1038    1663   -1664   -1039    -585    -190
 38        197     610    1085    1738   -1739   -1086    -611    -198
 39        206     637    1133    1815   -1816   -1134    -638    -207
 40        215     665    1183    1895   -1896   -1184    -666    -216
 41        225     695    1237    1980   -1981   -1238    -696    -226
 42        235     726    1291    2068   -2069   -1292    -727    -236
 43        246     759    1349    2161   -2162   -1350    -760    -247
 44        257     792    1409    2257   -2258   -1410    -793    -258
 45        268     828    1472    2357   -2358   -1473    -829    -269
 46        280     865    1538    2463   -2464   -1539    -866    -281
 47        293     903    1606    2572   -2573   -1607    -904    -294
 48        306     944    1678    2688   -2689   -1679    -945    -307
 49        319     986    1753    2807   -2808   -1754    -987    -320
 50        334    1030    1832    2933   -2934   -1833   -1031    -335
 51        349    1076    1914    3065   -3066   -1915   -1077    -350
 52        364    1124    1999    3202   -3203   -2000   -1125    -365
 53        380    1174    2088    3344   -3345   -2089   -1175    -381
 54        398    1227    2182    3494   -3495   -2183   -1228    -399
 55        415    1281    2278    3649   -3650   -2279   -1282    -416
 56        434    1339    2380    3811   -3812   -2381   -1340    -435
 57        453    1398    2486    3982   -3983   -2487   -1399    -454
 58        473    1461    2598    4160   -4161   -2599   -1462    -474
 59        495    1526    2714    4346   -4347   -2715   -1527    -496
 60        517    1594    2835    4540   -4541   -2836   -1595    -518
 61        540    1665    2961    4741   -4742   -2962   -1666    -541
 62        564    1740    3093    4953   -4954   -3094   -1741    -565
 63        589    1818    3232    5175   -5176   -3233   -1819    -590
 64        615    1898    3375    5405   -5406   -3376   -1899    -616
 65        643    1984    3527    5647   -5648   -3528   -1985    -644
 66        671    2072    3683    5898   -5899   -3684   -2073    -672
 67        701    2164    3848    6161   -6162   -3849   -2165    -702
 68        733    2261    4020    6438   -6439   -4021   -2262    -734
 69        766    2362    4199    6724   -6725   -4200   -2363    -767
 70        800    2467    4386    7024   -7025   -4387   -2468    -801
 71        836    2578    4583    7339   -7340   -4584   -2579    -837
 72        873    2692    4786    7664   -7665   -4787   -2693    -874
 73        912    2813    5001    8008   -8009   -5002   -2814    -913
 74        952    2938    5223    8364   -8365   -5224   -2939    -953
 75        995    3070    5457    8739   -8740   -5458   -3071    -996
 76       1039    3207    5701    9129   -9130   -5702   -3208   -1040
 77       1086    3350    5956    9537   -9538   -5957   -3351   -1087
 78       1134    3499    6220    9960   -9961   -6221   -3500   -1135
 79       1185    3655    6497   10404  -10405   -6498   -3656   -1186
 80       1238    3818    6788   10869  -10870   -6789   -3819   -1239
 81       1293    3989    7091   11355  -11356   -7092   -3990   -1294
 82       1351    4166    7407   11861  -11862   -7408   -4167   -1352
 83       1411    4352    7738   12390  -12391   -7739   -4353   -1412
 84       1474    4547    8084   12946  -12947   -8085   -4548   -1475
 85       1540    4750    8444   13522  -13523   -8445   -4751   -1541
 86       1609    4962    8821   14126  -14127   -8822   -4963   -1610
 87       1680    5183    9215   14756  -14757   -9216   -5184   -1681
 88       1756    5415    9626   15415  -15416   -9627   -5416   -1757
 89       1834    5657   10057   16104  -16105  -10058   -5658   -1835
 90       1916    5909   10505   16822  -16823  -10506   -5910   -1917
 91       2001    6173   10975   17574  -17575  -10976   -6174   -2002
 92       2091    6448   11463   18356  -18357  -11464   -6449   -2092
 93       2184    6736   11974   19175  -19176  -11975   -6737   -2185
 94       2282    7037   12510   20032  -20033  -12511   -7038   -2283
 95       2383    7351   13068   20926  -20927  -13069   -7352   -2384
 96       2490    7679   13652   21861  -21862  -13653   -7680   -2491
 97       2601    8021   14260   22834  -22835  -14261   -8022   -2602
 98       2717    8380   14897   23854  -23855  -14898   -8381   -2718
 99       2838    8753   15561   24918  -24919  -15562   -8754   -2839
100       2965    9144   16256   26031  -26032  -16257   -9145   -2966
101       3097    9553   16982   27193  -27194  -16983   -9554   -3098
102       3236    9979   17740   28407  -28408  -17741   -9980   -3237
103       3380   10424   18532   29675  -29676  -18533  -10425   -3381
104       3531   10890   19359   31000  -31001  -19360  -10891   -3532
105       3688   11375   20222   32382  -32383  -20223  -11376   -3689
106       3853   11883   21125   32767  -32768  -21126  -11884   -3854
107       4025   12414   22069   32767  -32768  -22070  -12415   -4026
108       4205   12967   23053   32767  -32768  -23054  -12968   -4206
109       4392   13546   24082   32767  -32768  -24083  -13547   -4393
110       4589   14151   25157   32767  -32768  -25158  -14152   -4590
111       4793   14783   26280   32767  -32768  -26281  -14784   -4794
112       5007   15442   27452   32767  -32768  -27453  -15443   -5008
113       5231   16132   28678   32767  -32768  -28679  -16133   -5232
114       5464   16851   29957   32767  -32768  -29958  -16852   -5465
115       5708   17603   31294   32767  -32768  -31295  -17604   -5709
116       5963   18389   32691   32767  -32768  -32692  -18390   -5964
117       6229   19210   32767   32767  -32768  -32768  -19211   -6230
118       6507   20067   32767   32767  -32768  -32768  -20068   -6508
119       6797   20963   32767   32767  -32768  -32768  -20964   -6798
120       7101   21899   32767   32767  -32768  -32768  -21900   -7102
121       7418   22876   32767   32767  -32768  -32768  -22877   -7419
122       7749   23897   32767   32767  -32768  -32768  -23898   -7750
123       8095   24964   32767   32767  -32768  -32768  -24965   -8096
124       8456   26078   32767   32767  -32768  -32768  -26079   -8457
125       8833   27242   32767   32767  -32768  -32768  -27243   -8834
126       9228   28457   32767   32767  -32768  -32768  -28458   -9229
127       9639   29727   32767   32767  -32768  -32768  -29728   -9640
```

`T2D` (2-bit codes):

```
row         c0      c1      c2      c3
  0         64     216    -217     -65
  1         67     226    -227     -68
  2         70     236    -237     -71
  3         74     246    -247     -75
  4         77     257    -258     -78
  5         80     268    -269     -81
  6         84     280    -281     -85
  7         88     294    -295     -89
  8         92     307    -308     -93
  9         96     321    -322     -97
 10        100     334    -335    -101
 11        104     350    -351    -105
 12        109     365    -366    -110
 13        114     382    -383    -115
 14        119     399    -400    -120
 15        124     416    -417    -125
 16        130     434    -435    -131
 17        136     454    -455    -137
 18        142     475    -476    -143
 19        148     495    -496    -149
 20        155     519    -520    -156
 21        162     541    -542    -163
 22        169     564    -565    -170
 23        176     590    -591    -177
 24        185     617    -618    -186
 25        193     644    -645    -194
 26        201     673    -674    -202
 27        210     703    -704    -211
 28        220     735    -736    -221
 29        230     767    -768    -231
 30        240     801    -802    -241
 31        251     838    -839    -252
 32        262     876    -877    -263
 33        274     914    -915    -275
 34        286     955    -956    -287
 35        299     997    -998    -300
 36        312    1041   -1042    -313
 37        326    1089   -1090    -327
 38        341    1138   -1139    -342
 39        356    1188   -1189    -357
 40        372    1241   -1242    -373
 41        388    1297   -1298    -389
 42        406    1354   -1355    -407
 43        424    1415   -1416    -425
 44        443    1478   -1479    -444
 45        462    1544   -1545    -463
 46        483    1613   -1614    -484
 47        505    1684   -1685    -506
 48        527    1760   -1761    -528
 49        551    1838   -1839    -552
 50        576    1921   -1922    -577
 51        601    2007   -2008    -602
 52        628    2097   -2098    -629
 53        656    2190   -2191    -657
 54        686    2288   -2289    -687
 55        716    2389   -2390    -717
 56        748    2496   -2497    -749
 57        781    2607   -2608    -782
 58        816    2724   -2725    -817
 59        853    2846   -2847    -854
 60        891    2973   -2974    -892
 61        930    3104   -3105    -931
 62        972    3243   -3244    -973
 63       1016    3389   -3390   -1017
 64       1061    3539   -3540   -1062
 65       1108    3698   -3699   -1109
 66       1158    3862   -3863   -1159
 67       1209    4035   -4036   -1210
 68       1264    4216   -4217   -1265
 69       1320    4403   -4404   -1321
 70       1379    4599   -4600   -1380
 71       1441    4806   -4807   -1442
 72       1505    5019   -5020   -1506
 73       1572    5244   -5245   -1573
 74       1642    5477   -5478   -1643
 75       1715    5722   -5723   -1716
 76       1792    5978   -5979   -1793
 77       1872    6245   -6246   -1873
 78       1955    6522   -6523   -1956
 79       2043    6813   -6814   -2044
 80       2134    7118   -7119   -2135
 81       2229    7436   -7437   -2230
 82       2329    7767   -7768   -2330
 83       2432    8114   -8115   -2433
 84       2541    8477   -8478   -2542
 85       2655    8854   -8855   -2656
 86       2773    9250   -9251   -2774
 87       2897    9663   -9664   -2898
 88       3026   10094  -10095   -3027
 89       3162   10546  -10547   -3163
 90       3303   11016  -11017   -3304
 91       3450   11508  -11509   -3451
 92       3604   12020  -12021   -3605
 93       3765   12556  -12557   -3766
 94       3933   13118  -13119   -3934
 95       4108   13703  -13704   -4109
 96       4292   14315  -14316   -4293
 97       4483   14953  -14954   -4484
 98       4683   15621  -15622   -4684
 99       4892   16318  -16319   -4893
100       5111   17046  -17047   -5112
101       5339   17807  -17808   -5340
102       5577   18602  -18603   -5578
103       5826   19433  -19434   -5827
104       6086   20300  -20301   -6087
105       6358   21205  -21206   -6359
106       6642   22152  -22153   -6643
107       6938   23141  -23142   -6939
108       7248   24173  -24174   -7249
109       7571   25252  -25253   -7572
110       7909   26380  -26381   -7910
111       8262   27557  -27558   -8263
112       8631   28786  -28787   -8632
113       9016   30072  -30073   -9017
114       9419   31413  -31414   -9420
115       9839   32767  -32768   -9840
116      10278   32767  -32768  -10279
117      10737   32767  -32768  -10738
118      11216   32767  -32768  -11217
119      11717   32767  -32768  -11718
120      12240   32767  -32768  -12241
121      12786   32767  -32768  -12787
122      13356   32767  -32768  -13357
123      13953   32767  -32768  -13954
124      14576   32767  -32768  -14577
125      15226   32767  -32768  -15227
126      15906   32767  -32768  -15907
127      16615   32767  -32768  -16616
```

## 2. Reading

The `sdec` components `'MAC3'` and `'MAC6'` are wrappers: they call the Sound Manager's `Exp1to3` and `Exp1to6`
through `_SoundDispatch` [Code]. On a PowerPC Mac those are SoundLib's native routines, which this section describes;
the 68k versions are §4.1.

Arithmetic in the pseudocode is on 32-bit signed integers. `>>` is an arithmetic shift right, rounding towards minus
infinity; `clamp(v, lo, hi)` limits `v` to `lo … hi`; `wrap16(v)` keeps the low 16 bits of `v` as a signed value.

### 2.1 Levels and table rows

Each code, 3 or 2 bits, updates the channel's `level` and picks a delta from a table row chosen by the level
**before** the update [Code]:

```
row(adjust, code):
    old   = level
    level = old + adjust[code] - (old >> 5)
    if level < 0: level = 0
    return (old >> 4) & $7F
```

- 3-bit codes use the adjustment table `T3` and the delta table `T3D` (128 rows × 8 columns); 2-bit codes use `T2`
  and `T2D` (128 rows × 4 columns) (§1.3) [Code].
- `level` is a 32-bit value on PowerPC and is never limited from above; the row wraps through the `& $7F` mask
  [Code].

### 2.2 MACE 3:1

State per channel: `level`, `prev`. Within each byte the fields are taken **lowest bits first** [Code]:

```
for each packet (2 bytes) of the channel:
    for each byte b of the packet:
        code3(b & 7,        T3, T3D)
        code3((b >> 3) & 3, T2, T2D)
        code3(b >> 5,       T3, T3D)

code3(code, adjust, deltas):
    d    = deltas[row(adjust, code)][code]
    v    = clamp(d + prev, -32767, 32767)
    prev = v - (v >> 3)                        # the value decays by 1/8
    emit byte(v)

byte(v) = ((v >> 8) & $FF) XOR $80             # 8-bit offset binary
```

[Code][Verified].

### 2.3 MACE 6:1

State per channel: `level`, `pred`, `fac`, `last`, `A` (the older value), `B` (the newer). Within each byte the
fields are taken **highest bits first**, and each code gives two samples [Code]:

```
for each byte b of the channel:
    code6(b >> 5,       T3, T3D)
    code6((b >> 3) & 3, T2, T2D)
    code6(b & 7,        T3, T3D)

code6(code, adjust, deltas):
    d = deltas[row(adjust, code)][code]
    v = clamp(d + pred, -32767, 32767)
    if (d & $8000) == (last & $8000):  fac = min(fac + 506, 32767)
    else:                              fac = max(fac - 314, -32767)
    last = v
    pred = (v * fac) >> 15
    emit byte(clamp(((3 * A) >> 3) + (B >> 1) + (v >> 3), -32767, 32767))
    emit byte(clamp((B >> 1) + (A >> 3) + ((3 * v) >> 3), -32767, 32767))
    A = B
    B = v
```

[Code][Verified]. `d` and `last` both lie within 16 bits, so `& $8000` compares their signs. The two samples
interpolate between the two previous values and the new one, so the output lags the input by about 4 samples [Code].

### 2.4 Output

- Both expanders give 8-bit offset binary samples [Doc] [Code] [Verified].
- Asked for 16-bit output, the Sound Manager replicates the byte: `s16 = ((b XOR $80) << 8) | (b XOR $80)`
  [Verified]. No extra precision exists.

## 3. Writing

None.

## 4. Variants

### 4.1 The 68k ROM's expanders

The 68k ROM (and the copy in `gpch` 666) holds 68k versions of `Exp1to3` and `Exp1to6`: identical for 3:1 but for one
saturation corner, slightly different in 6:1 rounding [Code]. The tables are byte-identical in SoundLib, `gpch` 666
and the ROM [Code].

The 68k versions work in 16-bit words [Code]:

- **3:1**: the same as §2.2, except that the sum `d + prev` is replaced by ±32767 only when it overflows 16 bits: a
  sum of exactly −32768 is kept, where PowerPC gives −32767. The byte is the same; the next `prev` differs by 1.
- **6:1**: different rounding and no output clamp. The state keeps halves, `p` = older >> 1 and `q` = newer >> 1:

```
code6_68k(code, adjust, deltas):
    old   = level
    level = wrap16(old + adjust[code] - (old >> 5));  if level < 0: level = 0
    d = deltas[(old >> 4) & $7F][code]
    s = d + pred;  v = (s > 32767) ? 32767 : (s < -32768) ? -32767 : s
    if ((d XOR last) & $8000) == 0:  fac = min(fac + 506, 32767)
    else:  f = fac - 314;  fac = (f < -32768) ? -32767 : f
    pred = wrap16(((v * fac * 2) & $FFFFFFFF) >> 16)
    last = v >> 1
    h = v >> 1
    e = wrap16(p - h) >> 2
    emit byte(wrap16(p + q - e))
    emit byte(wrap16(e + h + q))
    p = q
    q = h
```

The two 6:1 expanders differ on about 0.25 % of output bytes. The Sound Manager on a PowerPC Mac gives the PowerPC
result (§7) [Verified].

## 5. ClassicMac

- ClassicMac follows the PowerPC expanders (§2), as the Sound Manager on a PowerPC Mac does. [ClassicMac]
- MACE is written as 8-bit samples ([sound.md](../resources/sound.md)). [ClassicMac]
- More than two channels are decoded alike, each channel by packet in turn. [ClassicMac]

## 6. Diagnostics

None. Sample counts beyond the resource are reported by the sound reader (`sound.short`,
[sound.md](../resources/sound.md)).

## 7. Verification

- `tests/ClassicMac.Resources.Decoders.Tests/SoundDecoderTests.cs`:
  - `Compression_IDs_are_read_as_the_Sound_Manager_reads_them`: compressionID 0 is PCM whatever the format says.
  - `Sound_Manager_samples_decode_as_the_Sound_Manager_does`: with `CLASSICMAC_CORPUS` set, MACE 3:1 and 6:1 mono and
    stereo from 8-bit sources (`mac3m8`, `mac3s8`, `mac6m8`, `mac6s8`) and mono from 16-bit sources (`mac3m16`,
    `mac6m16`) decode byte for byte to Sound Manager 3.5.1's own 8-bit output on Mac OS 9.0 in SheepShaver. Not
    committed.
- The 68k model of §4.1 differs from that output on 119 to 267 bytes of each MACE 6 sample, the PowerPC model on none
  [Verified].

## 8. Not covered

- Compressing MACE.
- The 68k expanders (§4.1) are described but not implemented.

## 9. References

1. Apple, *Inside Macintosh: Sound*, "Sound Manager", the MACE routines.
2. Mac OS 9.0 System file: SoundLib (`nlib` 666), the `sdec` components `'MAC3'` and `'MAC6'`, `gpch` 666; the 68k
   ROM `$077D`. Traced in disassembly.
